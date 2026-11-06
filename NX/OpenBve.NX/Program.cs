//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2026, The OpenBVE Android port contributors
//
//Redistribution and use in source and binary forms, with or without
//modification, are permitted provided that the following conditions are met:
//
//1. Redistributions of source code must retain the above copyright notice, this
//   list of conditions and the following disclaimer.
//2. Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
//THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
//ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
//WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
//DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
//ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
//(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
//LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
//ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
//(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
//SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OpenBve.Android;
using OpenTK;
using SoundManager;

namespace OpenBve.NX
{
	/// <summary>
	/// OBNS 1.31N, step 1: the game started on the Switch as Android's GameView starts it, without drawing yet (deko3d is
	/// step 3), to load a route and its train and log the time and memory each part takes - whether TML fits in title
	/// takeover's 3.1 GB. The layout on the SD card (OBNS/SWITCH.md):
	///
	///   /switch/OBNS/Data            OpenBVE's data
	///   /switch/OBNS/UserData        settings and logs (obns.log)
	///   /switch/OBNS/LegacyContent   Railway and Train
	///
	/// The runner (NX/runner) changes directory to sdmc:/switch/OBNS before Mono starts, so these paths, without a
	/// device, are on the SD card: .NET on Unix doesn't take "sdmc:/" as an absolute path.
	///
	/// UserData/nx-load.txt may name the route (first line) and train folder (second line); otherwise the first route
	/// under LegacyContent/Railway/Route and the first train are loaded, as on Android.
	/// </summary>
	internal static class Program
	{
		private const string Root = "/switch/OBNS";
		private const string Tag = "OBNS";

		/// <summary>The native heap in use (malloc's, mallinfo): libnx reserves title takeover's memory up front, so the
		/// process's used memory says only 3,185 MiB whatever is loaded.</summary>
		[DllImport("__Internal", EntryPoint = "obns_native_heap_used")]
		private static extern long NativeHeapUsed();

		[DllImport("__Internal", EntryPoint = "obns_set_cpu_boost")]
		private static extern int SetCpuBoost(int mode);

		private static readonly Stopwatch Clock = Stopwatch.StartNew();
		private static StreamWriter logFile;

		private static void Log(string line)
		{
			global::Android.Util.Log.Info(Tag, line);
		}

		private static string Memory()
		{
			string heap;
			try
			{
				GCMemoryInfo info = GC.GetGCMemoryInfo();
				heap = ", GC heap " + (info.HeapSizeBytes >> 20) + " MiB";
			}
			catch (Exception)
			{
				heap = string.Empty;
			}

			return "native " + (NativeHeapUsed() >> 20) + " MiB, managed " + (GC.GetTotalMemory(false) >> 20) + " MiB" + heap;
		}

		private static void Stage(string what)
		{
			Log($"{Clock.Elapsed.TotalSeconds,7:F1} s  {what}  [{Memory()}]");
		}

		/// <summary>Exceptions thrown, caught or not, by type and the method that threw: on Mono ahead of time each costs
		/// a stack walk, so a parser that throws on ordinary content is slow on the Switch where a JIT barely notices.</summary>
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Thrown = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

		private static void OnThrown(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
		{
			System.Reflection.MethodBase site = e.Exception.TargetSite;
			string key = e.Exception.GetType().Name + " in " + (site == null ? "?" : site.DeclaringType?.Name + "." + site.Name);
			Thrown.AddOrUpdate(key, 1, (_, n) => n + 1);
		}

		/// <summary>The exceptions since the last call, most thrown first, then forgotten.</summary>
		private static string Exceptions()
		{
			System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>> list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(Thrown);
			Thrown.Clear();
			list.Sort((a, b) => b.Value.CompareTo(a.Value));
			int total = 0;
			foreach (System.Collections.Generic.KeyValuePair<string, int> pair in list) total += pair.Value;
			StringBuilder text = new StringBuilder("exceptions: " + total);
			for (int i = 0; i < list.Count && i < 6; i++)
			{
				text.Append(i == 0 ? ": " : ", ").Append(list[i].Value).Append(' ').Append(list[i].Key);
			}

			return text.ToString();
		}

		private static int Main(string[] args)
		{
			Directory.CreateDirectory(Root + "/UserData");
			logFile = new StreamWriter(Root + "/UserData/obns.log", false, new UTF8Encoding(false)) { AutoFlush = true };
			global::Android.Util.Log.Sink = line => logFile.WriteLine(line);
			try
			{
				return Run();
			}
			catch (Exception e)
			{
				Log("failed: " + e);
				LoadingScreen.ShowError(e.Message);
				// The error on the screen for a while, as Android's loading screen leaves it until back is pressed.
				System.Threading.Thread.Sleep(LoadingScreen.Opened ? 10000 : 0);
				return 1;
			}
			finally
			{
				LoadingScreen.Stop();
				SetCpuBoost(0);
				logFile.Dispose();
			}
		}

		/// <summary>
		/// The code pages content is written in (TML's Big5, BVE's Shift-JIS, GBK, Korean), each decoded once here on the
		/// main thread before any loading thread starts: their tables load once, and a failure is logged with its stack.
		/// The first TML load (2026-11-05) failed in the route parser with "NotSupported_NoCodepageData, 950", though the
		/// hello decoded Big5 on the same console.
		/// </summary>
		private static void WarmCodePages()
		{
			byte[] sample = { 0x41, 0x42, 0x43 };
			foreach (int codePage in new[] { 950, 932, 936, 949, 1252 })
			{
				try
				{
					Encoding encoding = Encoding.GetEncoding(codePage);
					string text = encoding.GetString(codePage == 950 ? new byte[] { 0xAA, 0xF7 } : codePage == 932 ? new byte[] { 0x93, 0xFA } : sample);
					Log($"code page {codePage}: {encoding.WebName}, a sample decodes to U+{(int)text[0]:X4}");
				}
				catch (Exception e)
				{
					Log($"code page {codePage} failed: {e}");
				}
			}
		}

		/// <summary>
		/// What BaseRenderer.Initialize sets up that loading uses, without its shaders, text and GL state, which need a
		/// device (step 3, deko3d). Without the texture manager every texture registration threw, the route's and the
		/// objects' alike; with Initialize itself, OpenGlString failed on the shader the GL stubs couldn't make.
		/// </summary>
		private static void InitializeWithoutDrawing(AndroidRenderer renderer, AndroidHost host)
		{
			renderer.Background = new LibRender2.Backgrounds.Background(renderer);
			renderer.Fog = new LibRender2.Fogs.Fog(renderer);
			renderer.TextureManager = new LibRender2.Textures.TextureManager(host, renderer);
			renderer.Loading = new LibRender2.Loadings.Loading(renderer);
			renderer.StaticObjectStates = new System.Collections.Generic.List<OpenBveApi.Objects.ObjectState>();
			renderer.DynamicObjectStates = new System.Collections.Generic.List<OpenBveApi.Objects.ObjectState>();
			renderer.VisibleObjects = new LibRender2.Objects.VisibleObjectLibrary(renderer);
			renderer.Lighting.Initialize();

			// The default shader, never compiled: what the vertex arrays need of it is its vertex layout, whose handles
			// all start at -1 (no attribute), so the objects' buffers are made, to the GL stubs, as GameView's scene
			// preparation makes them - and the visibility thread gets its sorted lists. deko3d's shaders replace it.
			LibRender2.Shaders.Shader shader = (LibRender2.Shaders.Shader)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibRender2.Shaders.Shader));
			typeof(LibRender2.Shaders.Shader).GetField(nameof(LibRender2.Shaders.Shader.VertexLayout)).SetValue(shader, new LibRender2.VertexLayout());
			typeof(LibRender2.Shaders.Shader).GetField(nameof(LibRender2.Shaders.Shader.UniformLayout)).SetValue(shader, new LibRender2.UniformLayout());
			renderer.DefaultShader = shader;
		}

		/// <summary>
		/// Where an object's load time goes on the Switch (TML's objects took 580 of the route's 669 s, and the
		/// collector wasn't it): about 300 of the route's object files, spread over its folders, each timed through
		/// reading its bytes, finding its encoding and the plugin's parse - the plugin called directly, past the
		/// host's cache, single-threaded.
		/// </summary>
		private static void ObjectBenchmark(AndroidHost host, string objectFolder)
		{
			Stopwatch watch = Stopwatch.StartNew();
			System.Collections.Generic.List<string> all = new System.Collections.Generic.List<string>();
			foreach (string file in Directory.EnumerateFiles(objectFolder, "*", SearchOption.AllDirectories))
			{
				string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
				if (extension == ".csv" || extension == ".b3d" || extension == ".x" || extension == ".animated")
				{
					all.Add(file);
				}
			}

			double listing = watch.Elapsed.TotalSeconds;
			all.Sort(StringComparer.Ordinal);
			int step = Math.Max(1, all.Count / 300);
			double read = 0, detect = 0, parse = 0;
			long bytes = 0;
			int parsed = 0, failed = 0;
			double worst = 0;
			string worstFile = string.Empty;
			for (int i = 0; i < all.Count; i += step)
			{
				string file = all[i];
				watch.Restart();
				bytes += File.ReadAllBytes(file).Length;
				read += watch.Elapsed.TotalSeconds;
				watch.Restart();
				Encoding encoding = OpenBveApi.TextEncoding.GetSystemEncodingFromFile(file, Encoding.UTF8);
				detect += watch.Elapsed.TotalSeconds;
				watch.Restart();
				bool ok = false;
				foreach (OpenBveApi.ContentLoadingPlugin plugin in host.Plugins)
				{
					if (plugin.Object != null && plugin.Object.CanLoadObject(file))
					{
						try
						{
							ok = plugin.Object.LoadObject(file, encoding, out OpenBveApi.Objects.UnifiedObject loaded) && loaded != null;
						}
						catch (Exception)
						{
							ok = false;
						}

						break;
					}
				}

				double seconds = watch.Elapsed.TotalSeconds;
				parse += seconds;
				if (seconds > worst)
				{
					worst = seconds;
					worstFile = file.Substring(objectFolder.Length);
				}

				if (ok) parsed++; else failed++;
			}

			int count = parsed + failed;
			Log($"object benchmark: {all.Count} object files listed in {listing:F1} s; {count} timed ({bytes >> 10} KiB): " +
			    $"read {read:F2} s ({read * 1000 / count:F1} ms each), encoding {detect:F2} s, parse {parse:F2} s ({parse * 1000 / count:F1} ms each), " +
			    $"{parsed} parsed, {failed} failed; slowest {worst * 1000:F0} ms ({worstFile})");
		}

		/// <summary>
		/// The first frames on deko3d (OBNS/SWITCH.md, step 3): the scene from where the train was placed, drawn as
		/// GameView's loop draws it, the simulation running (the train stands at its first station), for some seconds or
		/// until − is pressed; the frame rate, the draws and the memory logged every 5 s. Without a train the camera travels
		/// the route at 15 m/s, as on Android.
		/// </summary>
		private static unsafe void Frames(AndroidRenderer renderer, AndroidHost host, AndroidSounds sounds, AndroidTrainSession session, Deko.DekoGL deko, double seconds)
		{
			Stage("frames: drawing for " + seconds.ToString("0") + " s" + (session == null ? " (no train: the camera travels the route)" : string.Empty));
			Stopwatch clock = Stopwatch.StartNew();
			DekoCanvas overlay = new DekoCanvas();
			// The controllers drive the train (Android's controller layout, SwitchPad); + pauses.
			AndroidControls controls = new AndroidControls();
			OpenBve.Switch.SwitchPad pad = new OpenBve.Switch.SwitchPad();
			bool paused = false, leave = false;
			// The pause menu (Android's): + opens it, the simulation stands still while it's open.
			OpenBve.Switch.MenuInput menuInput = new OpenBve.Switch.MenuInput();
			OpenBve.Switch.PauseMenu pause = new OpenBve.Switch.PauseMenu
			{
				Stations = () => session?.JumpStations ?? (System.Collections.Generic.IReadOnlyList<(int, string)>)Array.Empty<(int, string)>(),
				LastStation = () => session?.Train.LastStation ?? -1
			};
			pause.Resumed += () =>
			{
				paused = false;
				sounds.SetPaused(false);
			};
			pause.JumpRequested += station => controls.Post(s => s.JumpToStation(station));
			pause.ExitRequested += () => leave = true;
			// The timetable card (Android's route card, its Timetable tab): − shows and hides it; refreshed once a second.
			OpenBve.Switch.TimetableCard timetable = new OpenBve.Switch.TimetableCard();
			TimetableSnapshot snapshot = null;
			double snapshotAge = 1.0;
			pause.QuitRequested += () => leave = true;
			double last = 0, windowStart = 0, worst = 0, trackPosition = 0;
			int frames = 0, total = 0;
			while (clock.Elapsed.TotalSeconds < seconds)
			{
				int touching, docked;
				float touchX, touchY;
				if (pause.Open)
				{
					uint buttons = MenuLoop.ReadInput(&touching, &touchX, &touchY, &docked);
					menuInput.Update((OpenBve.Switch.MenuButtons)buttons, touching != 0, touchX, touchY, clock.Elapsed.TotalSeconds - last);
					pause.Update(menuInput);
				}
				else
				{
					pad.Update(controls);
					if ((pad.Held & (OpenBve.Switch.SwitchPad.Plus | OpenBve.Switch.SwitchPad.Minus)) == (OpenBve.Switch.SwitchPad.Plus | OpenBve.Switch.SwitchPad.Minus))
					{
						Log("frames: left with + and −");
						break;
					}

					if (pad.Is(OpenBve.Switch.SwitchPad.Minus))
					{
						timetable.Toggle();
						snapshotAge = 1.0;
					}

					timetable.Update(pad.RightY, clock.Elapsed.TotalSeconds - last);
					if (pad.Is(OpenBve.Switch.SwitchPad.Plus))
					{
						paused = true;
						sounds.SetPaused(true);
						pause.Show();
						// The menu's input in step with the buttons held now.
						uint buttons = MenuLoop.ReadInput(&touching, &touchX, &touchY, &docked);
						menuInput.Update((OpenBve.Switch.MenuButtons)buttons, touching != 0, touchX, touchY, 0);
					}
				}

				if (leave)
				{
					Log("frames: left from the pause menu");
					break;
				}

				double now = clock.Elapsed.TotalSeconds, elapsed = now - last;
				last = now;
				if (session != null)
				{
					controls.Apply(session);
					if (paused)
					{
						session.UpdateView(elapsed, 0.0);
					}
					else
					{
						session.Update(elapsed);
					}
				}
				else
				{
					trackPosition += 15.0 * elapsed;
					renderer.PlaceCamera(trackPosition);
				}

				host.BeginFrame();
				renderer.RenderScene(elapsed);
				if (session != null && overlay.BeginOverlay())
				{
					// The in-game interface over the scene (the driving information, as Android's).
					OpenBve.Switch.DriverCard.Draw(overlay, session.Status(session.Camera.ModeName, paused));
					if (timetable.Shown)
					{
						snapshotAge += elapsed;
						if (snapshotAge >= 1.0 || snapshot == null)
						{
							snapshot = session.GetTimetable();
							snapshotAge = 0;
						}

						timetable.Draw(overlay, snapshot, elapsed);
					}

					pause.Draw(overlay, elapsed);
					overlay.EndOverlay();
				}

				sounds.Update(elapsed);
				deko.EndFrame();
				frames++;
				total++;
				double took = clock.Elapsed.TotalSeconds - now;
				if (total > 3)
				{
					worst = Math.Max(worst, took);
				}

				if (total == 1)
				{
					Stage($"frames: the first in {took * 1000:F0} ms; {deko.Describe()}");
				}

				if (now - windowStart >= 5.0)
				{
					Stage($"frames: {frames / (now - windowStart):F1} fps, the slowest {worst * 1000:F1} ms, {renderer.VisibleFaceCount} visible faces; {deko.Describe()}");
					frames = 0;
					worst = 0;
					windowStart = now;
				}
			}

			Stage($"frames: {total} drawn");
		}

		private static int Run()
		{
			AppDomain.CurrentDomain.FirstChanceException += OnThrown;
			// Loading at the CPU's 1,785 MHz with the GPU held low: apm's FastLoad, the console's own loading clock.
			int mhz = SetCpuBoost(1);
			Stage($"OBNS for the Switch (1.31N): {RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} cores, CPU {mhz} MHz");

			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			WarmCodePages();
			AndroidHost host = new AndroidHost();
			AndroidOptions options = new AndroidOptions();
			// The Switch's measures (OBNS/SWITCH.md, step 2), as Android's on a phone of 4.5 GB or less.
			TextureSizeLimit.MaxSize = 1024;

			OpenBveApi.FileSystem.FileSystem fileSystem = OpenBveApi.FileSystem.FileSystem.FromCommandLineArgs(new string[0], host);
			string content = Root + "/LegacyContent";
			fileSystem.DataFolder = Root + "/Data";
			fileSystem.SettingsFolder = Root + "/UserData";
			fileSystem.InitialRouteFolder = content + "/Railway/Route";
			fileSystem.RouteInstallationDirectory = content + "/Railway";
			fileSystem.InitialTrainFolder = content + "/Train";
			fileSystem.TrainInstallationDirectory = content + "/Train";
			fileSystem.OtherInstallationDirectory = content + "/Other";
			fileSystem.LoksimPackageInstallationDirectory = content + "/Loksim3D";
			fileSystem.LoksimDataDirectory = fileSystem.LoksimPackageInstallationDirectory;
			fileSystem.RestartProcess = string.Empty;
			fileSystem.RestartArguments = string.Empty;
			Menu.Init(host, fileSystem, SwitchSettings.GetLanguage(), "1.14.1.2-nx");
			Stage("file system and translations");

			string routeFile = null, trainFolder = null, imageFile = null;
			string choice = Root + "/UserData/nx-load.txt";
			if (File.Exists(choice))
			{
				string[] lines = File.ReadAllLines(choice);
				routeFile = lines.Length > 0 && lines[0].Trim().Length != 0 ? lines[0].Trim() : null;
				trainFolder = lines.Length > 1 && lines[1].Trim().Length != 0 ? lines[1].Trim() : null;
			}

			if (!File.Exists(choice))
			{
				// The menu (OBNS/SWITCH.md, step 6) chooses the route and the train; nx-load.txt skips it for measurements.
				// Before the game's renderer and plugins: the menu's previews set up their own (RouteInfo), as Android's
				// menu does in its own process.
				OpenBve.Switch.SwitchLaunch launch = MenuLoop.Run(Log);
				if (launch == null)
				{
					Stage("menu: left without starting");
					return 0;
				}

				routeFile = launch.RouteFile;
				trainFolder = launch.TrainFolder;
				imageFile = launch.ImageFile;
			}

			// The options the menu keeps (SwitchSettings), onto the game's.
			SwitchSettings.ApplyTo(options);
			Stage($"options: textures up to {TextureSizeLimit.MaxSize} px, {(AndroidHost.Textures16Bit ? 16 : 32)}-bit, viewing distance {options.ViewingDistance} m");

			// deko3d (OBNS/SWITCH.md, step 3): the renderer's GL calls go to DekoGL, which draws them with deko3d, as
			// Android's native Vulkan renderer (VulkanGL) does with Vulkan. Without it they go to the stubs, as before.
			Deko.DekoGL deko = null;
			int opened = DekoCanvas.Open();
			int width = DekoCanvas.Width, height = DekoCanvas.Height;
			if (opened == 0)
			{
				Deko.DekoGL.Log = line => Log("deko3d: " + line);
				deko = new Deko.DekoGL(width, height);
				Log($"deko3d: drawing at {width}x{height}");
				OpenTK.Graphics.OpenGL.GL.Device = deko;
			}
			else
			{
				Log("deko3d did not open (" + opened + "): nothing is drawn");
			}

			// As GameView: the renderer, sounds, trains, plugins.
			DisplayDevice.Default = new DisplayDevice(width, height, 1.0f, 1.0f);
			AndroidRenderer renderer = new AndroidRenderer(host, options, fileSystem);
			host.Renderer = renderer;
			OpenBve.Program.CurrentHost = host;
			OpenBve.Program.Renderer = renderer;
			host.Options = options;
			renderer.GameWindow = new GameWindow { Width = width, Height = height };
			renderer.Screen.Width = width;
			renderer.Screen.Height = height;
			renderer.Screen.AspectRatio = (double)width / height;
			// Upstream's overlays are sized for 720 lines or so; 1080p docked scales them up, as Android's density does.
			AndroidRenderer.OverlayScale = height / 720.0;

			AndroidSounds sounds = new AndroidSounds(host, renderer, options);
			sounds.Initialize(SoundRange.Low);
			host.Sounds = sounds;
			OpenBve.Program.Sounds = sounds;

			AndroidTrainManager trainManager = new AndroidTrainManager(host, renderer, options, fileSystem);
			OpenBve.Program.TrainManager = trainManager;
			OpenBve.Interface.CurrentOptions = options;
			string plugins = AndroidPlugins.Register(host, fileSystem, options, trainManager, renderer);
			// As GameView: this thread is the render thread (textures load in place rather than waiting on a queue no
			// frame services), and the renderer is set up after the plugins.
			OpenTK.AndroidGraphicsContext.RenderThreadId = Environment.CurrentManagedThreadId;
			bool initialized = false;
			if (deko != null)
			{
				try
				{
					renderer.Initialize();
					initialized = true;
				}
				catch (Exception e)
				{
					Log("renderer.Initialize failed on deko3d: " + e);
				}
			}

			if (!initialized)
			{
				InitializeWithoutDrawing(renderer, host);
			}
			OpenBve.Program.FileSystem = fileSystem;
			Stage("plugins: " + plugins.Replace("\n", "; "));
			LoadingScreen.Start(Log, routeFile, imageFile);

			host.FileSystem = fileSystem;
			host.CurrentTrainFolder = trainFolder ?? AndroidTrainSession.FindTrain(fileSystem.InitialTrainFolder, host) ?? string.Empty;
			OpenBveApi.Path.CacheListings = true;
			// As GameView: where the route's time goes, by category (parsing, objects, textures, sounds, file access).
			LoadProfile.Begin("route parser");
			string route = AndroidRouteLoader.LoadRoute(host, fileSystem, renderer, routeFile, null, out RouteManager2.CurrentRoute currentRoute);
			Log("load profile, route: " + LoadProfile.End());
			Log(Exceptions());
			string gaps = LoadProfile.GapReport();
			if (gaps.Length != 0)
			{
				Log("load profile, " + gaps);
			}
			Stage("route: " + route);
			GC.Collect();
			Stage("route, after a collection");

			AndroidTrainSession session = null;
			if (currentRoute != null)
			{
				renderer.Route = currentRoute;
				OpenBve.Program.CurrentRoute = currentRoute;
				sounds.Route = currentRoute;
				// As GameView's scene preparation: meshes shared, the camera and its track follower (the train's
				// placement needs it), the objects' buffers made (to the GL stubs until deko3d) and their vertex copies
				// dropped before the train loads on top.
				LoadProfile.Begin("scene preparation");
				MeshSharing.Reset();
				MeshSharing.Share(trainManager.TFOs);
				MeshSharing.Share(host.AnimatedWorldObjects, host.AnimatedWorldObjectsUsed);
				renderer.PrepareScene(host, options.ViewingDistance);
				VertexCopyReleaser.ReleaseAll(renderer);
				GC.Collect();
				Log("load profile, scene: " + LoadProfile.End());
				Log(Exceptions());
				Stage("scene prepared: " + MeshSharing.Describe());
				LoadProfile.Begin("train and placement");
				try
				{
					string train = AndroidTrainSession.Load(host, trainManager, renderer, currentRoute, fileSystem, host.CurrentTrainFolder, null, out session);
					Log("load profile, train: " + LoadProfile.End());
					Log(Exceptions());
					Stage("train: " + train.Replace("\n", "; "));
				}
				catch (Exception e)
				{
					Log("load profile, train: " + LoadProfile.End());
					Log(Exceptions());
					Log("train failed: " + e);
					LoadingScreen.ShowError(e.Message);
					Stage("train failed: " + e.GetType().Name);
				}
			}

			OpenBveApi.Path.CacheListings = false;
			long before = GC.GetTotalMemory(false) >> 20;
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Stage($"loaded; managed {before} MiB before the final collection; {host.ErrorCount} error(s)");
			Stage(TextureCensus.DescribeMeshes(renderer));
			if (File.Exists(Root + "/UserData/nx-benchmark.txt"))
			{
				try
				{
					ObjectBenchmark(host, content + "/Railway/Object");
				}
				catch (Exception e)
				{
					Log("object benchmark failed: " + e);
				}

				Log(Exceptions());
				Stage("benchmark done");
			}

			if (deko != null && initialized && currentRoute != null)
			{
				// The loading screen gives way to the game's frames on the same device.
				LoadingScreen.Stop(false);
				deko.Drawing = true;
				Frames(renderer, host, sounds, session, deko, 600.0);
				deko.Dispose();
				OpenTK.Graphics.OpenGL.GL.Device = null;
				DekoCanvas.Close();
				return 0;
			}

			LoadingScreen.Finish();
			// The full bar for a few seconds, to be seen on the console.
			System.Threading.Thread.Sleep(5000);
			return 0;
		}
	}
}
