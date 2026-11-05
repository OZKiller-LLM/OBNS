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
				return 1;
			}
			finally
			{
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
		}

		private static int Run()
		{
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
			Menu.Init(host, fileSystem, "en-US", "1.14.1.2-nx");
			Stage("file system and translations");

			string routeFile = null, trainFolder = null;
			string choice = Root + "/UserData/nx-load.txt";
			if (File.Exists(choice))
			{
				string[] lines = File.ReadAllLines(choice);
				routeFile = lines.Length > 0 && lines[0].Trim().Length != 0 ? lines[0].Trim() : null;
				trainFolder = lines.Length > 1 && lines[1].Trim().Length != 0 ? lines[1].Trim() : null;
			}

			// As GameView: the renderer (its GL calls go nowhere until the deko3d renderer), sounds, trains, plugins.
			DisplayDevice.Default = new DisplayDevice(1280, 720, 1.0f, 1.0f);
			AndroidRenderer renderer = new AndroidRenderer(host, options, fileSystem);
			host.Renderer = renderer;
			OpenBve.Program.CurrentHost = host;
			OpenBve.Program.Renderer = renderer;
			host.Options = options;
			renderer.GameWindow = new GameWindow { Width = 1280, Height = 720 };
			renderer.Screen.Width = 1280;
			renderer.Screen.Height = 720;
			renderer.Screen.AspectRatio = 1280.0 / 720.0;

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
			InitializeWithoutDrawing(renderer, host);
			OpenBve.Program.FileSystem = fileSystem;
			Stage("plugins: " + plugins.Replace("\n", "; "));

			host.FileSystem = fileSystem;
			host.CurrentTrainFolder = trainFolder ?? AndroidTrainSession.FindTrain(fileSystem.InitialTrainFolder, host) ?? string.Empty;
			OpenBveApi.Path.CacheListings = true;
			// As GameView: where the route's time goes, by category (parsing, objects, textures, sounds, file access).
			LoadProfile.Begin("route parser");
			string route = AndroidRouteLoader.LoadRoute(host, fileSystem, renderer, routeFile, null, out RouteManager2.CurrentRoute currentRoute);
			Log("load profile, route: " + LoadProfile.End());
			string gaps = LoadProfile.GapReport();
			if (gaps.Length != 0)
			{
				Log("load profile, " + gaps);
			}
			Stage("route: " + route);
			GC.Collect();
			Stage("route, after a collection");

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
				Stage("scene prepared: " + MeshSharing.Describe());
				LoadProfile.Begin("train and placement");
				try
				{
					string train = AndroidTrainSession.Load(host, trainManager, renderer, currentRoute, fileSystem, host.CurrentTrainFolder, null, out AndroidTrainSession session);
					Log("load profile, train: " + LoadProfile.End());
					Stage("train: " + train.Replace("\n", "; "));
				}
				catch (Exception e)
				{
					Log("load profile, train: " + LoadProfile.End());
					Log("train failed: " + e);
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
			return 0;
		}
	}
}
