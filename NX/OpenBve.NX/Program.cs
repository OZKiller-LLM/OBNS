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
	/// OBNS 1.32, step 1: the game started on the Switch as Android's GameView starts it, without drawing yet (deko3d is
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

		/// <summary>The drive benchmark, when UserData/nx-drive-benchmark.txt asks for it; otherwise null.</summary>
		private static DriveBenchmark driveBenchmark;

		/// <summary>The clocks at the last 5 s window's end, for the drive benchmark.</summary>
		private static int benchmarkCpu, benchmarkGpu;
		private const string Tag = "OBNS";

		/// <summary>The native heap in use (malloc's, mallinfo): libnx reserves title takeover's memory up front, so the
		/// process's used memory says only 3,185 MiB whatever is loaded.</summary>
		[DllImport("__Internal", EntryPoint = "obns_native_heap_used")]
		private static extern long NativeHeapUsed();

		/// <summary>A fault on purpose (the watchdog, a game stuck for good): Atmosphère's crash report then has every thread's stack.</summary>
		/// <summary>The sound output's counts (obns_audio.c): buffers played, waits that timed out, restarts.</summary>
		[DllImport("__Internal", EntryPoint = "obns_audio_stats")]
		private static extern void AudioStats(out long played, out int timedOut, out int restarted);

		[DllImport("__Internal", EntryPoint = "obns_watchdog_crash")]
		private static extern void WatchdogCrash();

		[DllImport("__Internal", EntryPoint = "obns_set_cpu_boost")]
		private static extern int SetCpuBoost(int mode);

		[DllImport("__Internal", EntryPoint = "obns_hold_clocks")]
		private static extern int HoldClocks();

		[DllImport("__Internal", EntryPoint = "obns_clock_rates")]
		private static extern void ClockRates(out int cpu, out int gpu, out int memory);

		/// <summary>The clocks as they are now, read back from the system: what another program set shows here too.</summary>
		[DllImport("__Internal", EntryPoint = "obns_clock_now")]
		private static extern void ClockNow(out int cpu, out int gpu, out int memory);

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
			// The run before's log kept beside this one's (2026-11-24: HOME and sleep tested in two runs, the first's log gone).
			try
			{
				if (File.Exists(Root + "/UserData/obns.log"))
				{
					File.Copy(Root + "/UserData/obns.log", Root + "/UserData/obns-previous.log", true);
				}
			}
			catch (Exception)
			{
				// Not kept: this run's log is what matters.
			}

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
				// The log's last lines written (they go through a writer thread) before its file is closed.
				global::Android.Util.Log.Flush(3000);
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
		private static Deko.CompressedTextures compressedTextures;

		// The frame loop's step, and frames finished: what the watchdog reads.
		private static volatile int loopStep;
		private static long framesFinished;
		private static readonly string[] LoopSteps = { "input", "drive benchmark", "controls", "simulation", "RenderScene", "texture unload", "overlay", "sound: update", "end of frame", "sound: the output watch", "frame profile" };

		/*
		 * The watchdog (2026-11-07: twice the game stood still at Tuen Mun a few seconds into the drive benchmark, no crash,
		 * nothing in the log). No frame finished for 5 s: where the loop is - its step, the renderer's last phase, the
		 * benchmark's step, and whether an emulated plugin is mid-call (its instruction count and address, which move if it
		 * loops) - logged then, and every 10 s after while it stays stuck.
		 */
		private static System.Threading.Thread StartWatchdog(AndroidRenderer renderer, Func<bool> done)
		{
			System.Threading.Thread thread = new System.Threading.Thread(() =>
			{
				long seen = -1;
				uint away = Applet.AwayCount;
				Stopwatch since = Stopwatch.StartNew();
				double reported = 0;
				while (!done())
				{
					System.Threading.Thread.Sleep(1000);
					long now = System.Threading.Interlocked.Read(ref framesFinished);
					// The time the game was away (the HOME menu, sleep: it waits out of focus) is no frame stuck.
					uint awayNow = Applet.AwayCount;
					if (now != seen || awayNow != away || !Applet.InFocus)
					{
						seen = now;
						away = awayNow;
						since.Restart();
						reported = 0;
						continue;
					}

					double stuck = since.Elapsed.TotalSeconds;
					if (stuck < 5.0 || stuck - reported < 10.0 && reported > 0)
					{
						continue;
					}

					reported = stuck;
					OpenBve.X86.Cpu cpu = OpenBve.X86.Cpu.Running;
					int step = loopStep;
					Log("watchdog: no frame for " + stuck.ToString("0") + " s - the loop in " + (step >= 0 && step < LoopSteps.Length ? LoopSteps[step] : step.ToString()) +
					    ", the renderer's last phase \"" + renderer.Profile.LastPhase + "\"" +
					    (driveBenchmark != null ? ", the benchmark at " + driveBenchmark.StepName : string.Empty) +
					    (cpu != null ? ", an emulated plugin mid-call: " + cpu.InstructionCount + " instructions, at 0x" + cpu.Eip.ToString("X8") : ", no emulated plugin running"));
					if (stuck >= 40.0 && driveBenchmark != null)
					{
						// Stuck for good in a test drive: stopped with a fault on purpose, so Atmosphère's crash report has every
						// thread's stack - where the game stands, natively, which the log cannot say.
						Log("watchdog: stuck - stopping the game for a crash report (atmosphere/crash_reports: every thread's stack)");
						global::Android.Util.Log.Flush(2000);
						WatchdogCrash();
					}
				}
			})
			{
				IsBackground = true,
				Name = "watchdog"
			};
			thread.Start();
			return thread;
		}

		// Full collections at rest (GcAtRest).
		private static long heapAtLastFull = -1;
		private static int lastFullCount = -1, restCollections;
		private static double stoppedSince = -1, heapCheckedAt = double.NegativeInfinity;

		/// <summary>
		/// A full collection when the game is at rest - the train stopped for 2 s (a station, a signal), or a menu open - once the
		/// managed heap has grown by 192 MB since the last: the collector's own full collections otherwise come mid-run, and
		/// with a heap of 800 MB and more on the Switch's cores one can hold a frame for a second or more (the user,
		/// 2026-11-25: "garbage-collection stalls").
		/// </summary>
		private static void GcAtRest(AndroidTrainSession session, bool menuOpen, double now)
		{
			if (session?.Train == null)
			{
				return;
			}

			bool stopped = Math.Abs(session.Train.CurrentSpeed) < 0.01;
			stoppedSince = !stopped ? -1 : stoppedSince < 0 ? now : stoppedSince;
			bool atRest = menuOpen || stoppedSince >= 0 && now - stoppedSince > 2.0;
			/*
			 * The heap's size only at rest, and at most every 2 s: Mono's count of it walks every block of the heap under the
			 * collector's lock, and asked every frame it took 55 ms a frame of 800 MB (2026-11-25: 12 fps in the cab, input
			 * 54-58 ms a frame).
			 */
			if (!atRest || now - heapCheckedAt < 2.0)
			{
				return;
			}

			heapCheckedAt = now;
			int full = GC.CollectionCount(2);
			long heap = GC.GetTotalMemory(false);
			if (heapAtLastFull < 0 || full != lastFullCount)
			{
				// After any full collection (the first look at rest since), the heap then is where growth counts from.
				heapAtLastFull = heap;
				lastFullCount = full;
				return;
			}

			if (heap - heapAtLastFull < (192L << 20))
			{
				return;
			}

			long start = Stopwatch.GetTimestamp();
			GC.Collect(2, GCCollectionMode.Forced, true, true);
			long after = GC.GetTotalMemory(false);
			restCollections++;
			Log($"frames: a full collection at rest ({(menuOpen ? "a menu open" : "stopped")}): {heap >> 20} MB to {after >> 20} MB in {(Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency} ms");
			heapAtLastFull = after;
			lastFullCount = GC.CollectionCount(2);
		}

		private static unsafe void Frames(AndroidRenderer renderer, AndroidHost host, AndroidSounds sounds, AndroidTrainSession session, Deko.DekoGL deko, double seconds)
		{
			Stage("frames: drawing " + (double.IsPositiveInfinity(seconds) ? "until the game is left" : "for " + seconds.ToString("0") + " s") + (session == null ? " (no train: the camera travels the route)" : string.Empty));
			bool framesOver = false;
			StartWatchdog(renderer, () => framesOver);
			try
			{
				FramesLoop(renderer, host, sounds, session, deko, seconds);
			}
			finally
			{
				framesOver = true;
			}
		}

		private static unsafe void FramesLoop(AndroidRenderer renderer, AndroidHost host, AndroidSounds sounds, AndroidTrainSession session, Deko.DekoGL deko, double seconds)
		{
			Stopwatch clock = Stopwatch.StartNew();
			// The first frame's first phase would otherwise be charged the whole load.
			renderer.Profile.Restart();
			DekoCanvas overlay = new DekoCanvas();
			// The frame's phases (input, simulation, RenderScene, overlay, sound, end of frame), summed over the 5 s window.
			double[] phase = new double[6];
			// The phases at the frame's start (a long frame's own time by phase, for the log), and the collector's counts.
			double[] phaseBefore = new double[6];
			string[] phaseNames = { "input", "simulation", "RenderScene", "overlay", "sound", "end of frame" };
			int fullBefore = GC.CollectionCount(2), gen0Before = GC.CollectionCount(0);
			int windowGen0 = GC.CollectionCount(0), windowGen1 = GC.CollectionCount(1), windowGen2 = GC.CollectionCount(2);
			double gpuWait = 0, frameStart = 0, clocksHeld = 0;
			int lastGpuMhz = 0;
			// The controllers drive the train (Android's controller layout, SwitchPad); + pauses.
			AndroidControls controls = new AndroidControls();
			OpenBve.Switch.SwitchPad pad = new OpenBve.Switch.SwitchPad();
			// A USB keyboard (upstream's key bindings) and mouse (the cab's controls, the camera), docked or handheld.
			OpenBve.Switch.KeyBindings.SettingsFolder = Root + "/UserData";
			OpenBve.Switch.SwitchKeyboard keyboard = new OpenBve.Switch.SwitchKeyboard();
			OpenBve.Switch.SwitchMouse mouse = new OpenBve.Switch.SwitchMouse();
			Log("frames: keyboard: " + keyboard.Description);
			// A Densha de GO! controller on USB (HORI's, ZUIKI's) drives the handles, its pad left out of the one above.
			OpenBve.Switch.MasconPad mascon = new OpenBve.Switch.MasconPad();
			if (session != null && SwitchSettings.GetTrainController())
			{
				mascon.Attach(Log);
			}

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
			// The switch map (upstream's switch menu, Android's SwitchMapPanel): from the pause menu, or SWITCH_MENU.
			OpenBve.Switch.SwitchMapCard switchMap = new OpenBve.Switch.SwitchMapCard(() => session?.GetSwitchMap(OpenBve.Switch.SwitchMapCard.Radius));
			switchMap.ChangeRequested += id => controls.Post(s => s.ToggleSwitch(id));
			pause.HasSwitches = () => session?.HasSwitches == true;
			pause.SwitchesRequested += () => switchMap.Show();
			controls.SwitchMenuRequested += () =>
			{
				if (switchMap.Shown)
				{
					switchMap.Hide();
				}
				else
				{
					switchMap.Show();
				}
			};
			// The timetable card (Android's route card, its Timetable tab): − shows and hides it; refreshed once a second.
			OpenBve.Switch.TimetableCard timetable = new OpenBve.Switch.TimetableCard
			{
				// Its Map and Gradient tabs (Android's route card): the line worked out once, the trains read twice a second.
				Geometry = () => session?.GetRouteGeometry(),
				Live = () => session?.GetRouteLive()
			};
			TimetableSnapshot snapshot = null;
			double snapshotAge = 1.0;
			pause.QuitRequested += () => leave = true;
			// Android's touch controls (handheld, when Options turn them on): the pause button opens the pause menu as + does.
			OpenBve.Switch.TouchControls touch = new OpenBve.Switch.TouchControls(controls);
			touch.SetSingleHandle(session?.Train.Handles.HandleType == global::TrainManager.Handles.HandleType.SingleHandle);
			bool touchPause = false;
			touch.PauseRequested += () => touchPause = true;
			touch.ViewRequested += () => controls.Post(s => s.Camera.CycleView());
			touch.TimetableRequested += () =>
			{
				timetable.Toggle();
				snapshotAge = 1.0;
			};
			double last = 0, windowStart = 0, worst = 0, trackPosition = 0, unloadAge = 0;
			int windows = 0;
			int frames = 0, total = 0;
			while (clock.Elapsed.TotalSeconds < seconds)
			{
				Array.Copy(phase, phaseBefore, phase.Length);
				loopStep = 0;
				int touching, docked;
				float touchX, touchY;
				// The console's applet messages (Applet): the HOME menu, sleep, docking.
				AppletEvents applet = Applet.Poll();
				string consoleMessages = Applet.TakeMessages();
				if (consoleMessages != null)
				{
					Log("frames: " + consoleMessages);
					global::Android.Util.Log.Flush(500);
				}

				if (!Applet.InFocus)
				{
					applet |= AppletEvents.FocusLost;
				}

				if (applet != AppletEvents.None)
				{
					Log("frames: " + Applet.Describe(applet));
					// Written out at once: on 2026-11-16 a HOME press and closing the game from the HOME menu ended it before
					// the log's thread wrote the line, so the test showed nothing.
					global::Android.Util.Log.Flush(500);
					if ((applet & (AppletEvents.FocusLost | AppletEvents.Resumed)) != 0 && session != null && !pause.Open)
					{
						// Away (or back from sleep): the game waits in its pause menu, as upstream's does on losing the window.
						paused = true;
						sounds.SetPaused(true);
						touch.ReleaseAll();
						pause.Show();
					}

					if (!Applet.InFocus)
					{
						// Not suspended by the system (runner/source/obns_applet.c): the game waits, paused, until it is back.
						applet |= Applet.WaitForFocus(Log, "frames: ");
					}

					if ((applet & (AppletEvents.FocusBack | AppletEvents.Resumed)) != 0)
					{
						// The time away left out: the game waited out of focus, and the next frame would have run it all at
						// once. The frame statistics' window starts again too.
						double back = clock.Elapsed.TotalSeconds;
						last = back;
						frameStart = back;
						windowStart = back;
						frames = 0;
						worst = 0;
						for (int i = 0; i < phase.Length; i++)
						{
							phase[i] = 0;
						}

						renderer.Profile.Restart();
						// The time away is no time the textures went unused: none unloaded for it.
						renderer.TextureManager.NoteAllUsed();
					}

					if ((applet & AppletEvents.Exit) != 0)
					{
						Log("frames: closed by the system");
						break;
					}
				}

				touch.Update(pause.Open || session == null);
				if (session != null)
				{
					touch.SetAiActive(session.Train.AI != null);
				}

				if (touchPause && !pause.Open)
				{
					paused = true;
					sounds.SetPaused(true);
					touch.ReleaseAll();
					pause.Show();
				}

				touchPause = false;
				if (switchMap.Shown && !pause.Open)
				{
					// The switch map takes the buttons while it is open; the game goes on (upstream's switch menu does).
					uint buttons = MenuLoop.ReadInput(&touching, &touchX, &touchY, &docked);
					mascon.Update(controls, commands: false);
					keyboard.Update(controls, commands: false);
					OpenBve.Switch.SwitchMouse.Read(clock.Elapsed.TotalSeconds, out _);
					buttons |= (uint)mascon.MenuHeld();
					menuInput.Update((OpenBve.Switch.MenuButtons)buttons, touching != 0, touchX, touchY, clock.Elapsed.TotalSeconds - last);
					switchMap.Update(menuInput, clock.Elapsed.TotalSeconds - last);
				}
				else if (pause.Open)
				{
					uint buttons = MenuLoop.ReadInput(&touching, &touchX, &touchY, &docked);
					mascon.Update(controls, commands: false);
					keyboard.Update(controls, commands: false);
					OpenBve.Switch.SwitchMouse.Read(clock.Elapsed.TotalSeconds, out _);
					buttons |= (uint)mascon.MenuHeld();
					menuInput.Update((OpenBve.Switch.MenuButtons)buttons, touching != 0, touchX, touchY, clock.Elapsed.TotalSeconds - last);
					pause.Update(menuInput);
				}
				else
				{
					pad.Update(controls);
					mascon.Update(controls);
					OpenBve.Switch.SwitchKeyboard.Special key = keyboard.Update(controls);
					mouse.Update(session, controls, clock.Elapsed.TotalSeconds, OpenBve.NX.DekoCanvas.Width, timetable);
					if ((pad.Held & (OpenBve.Switch.SwitchPad.Plus | OpenBve.Switch.SwitchPad.Minus)) == (OpenBve.Switch.SwitchPad.Plus | OpenBve.Switch.SwitchPad.Minus))
					{
						Log("frames: left with + and −");
						break;
					}

					if (pad.Is(OpenBve.Switch.SwitchPad.Minus) || mascon.Is(OpenBve.Switch.SwitchPad.Minus) || key == OpenBve.Switch.SwitchKeyboard.Special.Timetable)
					{
						// − goes through the route card's tabs (Timetable, Map, Gradient), then hides it.
						timetable.Cycle();
						snapshotAge = 1.0;
					}

					timetable.Update(pad.RightX, pad.RightY, clock.Elapsed.TotalSeconds - last);
					// The sticks move and turn the camera, as Android's controller (the right one scrolls the timetable when shown).
					pad.Camera(controls, !timetable.Shown);
					if (pad.Is(OpenBve.Switch.SwitchPad.Plus) || mascon.Is(OpenBve.Switch.SwitchPad.Plus) || key == OpenBve.Switch.SwitchKeyboard.Special.Pause)
					{
						paused = true;
						sounds.SetPaused(true);
						// Keys and the mouse let go of what they hold: the menu has them now.
						keyboard.ReleaseAll(controls);
						mouse.ReleaseAll(session, controls);
						pause.Show();
						// The menu's input in step with the buttons held now.
						uint buttons = MenuLoop.ReadInput(&touching, &touchX, &touchY, &docked) | (uint)mascon.MenuHeld();
						menuInput.Update((OpenBve.Switch.MenuButtons)buttons, touching != 0, touchX, touchY, 0);
					}
				}

				if (leave)
				{
					Log("frames: left from the pause menu");
					break;
				}

				double now = clock.Elapsed.TotalSeconds, elapsed = now - last;
				if (elapsed > 2.0 && total > 10)
				{
					// A long gap with no frame (2026-11-16: 5.6 s with the CPU back at the system's clock, and no applet
					// event): said in the log. Over 10 s, mid-game, only a suspension makes one - the HOME menu or sleep seen
					// by neither the system's messages nor the focus state - so it is taken as one: the game waits in its pause
					// menu, and the time away is left out.
					Log($"frames: {elapsed:F1} s since the last frame, with no event from the console" + (elapsed > 10.0 && session != null && driveBenchmark == null ? "; taken as time away: paused" : string.Empty));
					// Seconds with no frame are no seconds of textures unused (TextureManager.NoteAllUsed).
					renderer.TextureManager.NoteAllUsed();
					if (elapsed > 10.0 && session != null && driveBenchmark == null)
					{
						if (!pause.Open)
						{
							paused = true;
							sounds.SetPaused(true);
							touch.ReleaseAll();
							pause.Show();
						}

						last = now - 1.0 / 60.0;
						elapsed = now - last;
						frameStart = last;
						windowStart = now;
						frames = 0;
						worst = 0;
						for (int i = 0; i < phase.Length; i++)
						{
							phase[i] = 0;
						}

						renderer.Profile.Restart();
					}

					global::Android.Util.Log.Flush(500);
				}

				// Turbo's and Overclock's clocks and the full fan, set again each second: docking and undocking set the system's
				// clocks (and Turbo's GPU clock follows), its thermal control its own fan level. Nothing in Normal.
				if (now - clocksHeld >= 1.0)
				{
					clocksHeld = now;
					int gpuMhz = HoldClocks();
					if (gpuMhz != 0 && gpuMhz != lastGpuMhz)
					{
						ClockRates(out int cpuMhz, out _, out int memoryMhz);
						Log($"frames: clocks CPU {cpuMhz} MHz, GPU {gpuMhz} MHz, memory {memoryMhz} MHz");
						lastGpuMhz = gpuMhz;
					}
				}

				phase[0] += now - frameStart;
				last = now;
				if (session != null)
				{
					if (driveBenchmark != null && !paused)
					{
						loopStep = 1;
						driveBenchmark.Update(session, controls, now, elapsed, benchmarkCpu, benchmarkGpu);
						if (driveBenchmark.Finished)
						{
							Log("frames: the drive benchmark is over");
							break;
						}
					}

					loopStep = 2;
					controls.Apply(session);
					loopStep = 3;
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

				double mark = clock.Elapsed.TotalSeconds;
				phase[1] += mark - now;
				loopStep = 4;
				host.BeginFrame();
				renderer.RenderScene(elapsed);
				// The cab's touch areas where they are drawn now, for the mouse (Android's GameView does it for touches).
				if (session != null && OpenBve.Switch.SwitchMouse.Connected)
				{
					controls.Cab.Update(session, renderer, OpenBve.NX.DekoCanvas.Width, OpenBve.NX.DekoCanvas.Height);
				}
				/*
				 * Textures not drawn for 20 s unloaded, as upstream's GameWindow and Android's GameView do after drawing - here
				 * once a second (the Tuen Ma Line registers some 31,000, too many to look through every frame). Missing until
				 * 2026-11-07: every texture ever shown stayed, and 941 MB of them ran the native heap out after 19 minutes.
				 */
				loopStep = 5;
				unloadAge += elapsed;
				if (unloadAge >= 1.0 && !paused)
				{
					renderer.TextureManager.UnloadUnusedTextures(unloadAge);
					unloadAge = 0;
				}

				phase[2] += clock.Elapsed.TotalSeconds - mark;
				mark = clock.Elapsed.TotalSeconds;
				// The scene's draws all recorded first: the interface goes on top.
				loopStep = 6;
				deko.FlushDraws();
				if (session != null && overlay.BeginOverlay())
				{
					// The in-game interface over the scene (the driving information, as Android's).
					OpenBve.Switch.DriverCard.Draw(overlay, session.Status(session.Camera.ModeName, paused));
					touch.Draw(overlay);
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

					switchMap.Draw(overlay);
					pause.Draw(overlay, elapsed);
					OpenBve.Switch.SwitchMouse.DrawPointer(overlay, clock.Elapsed.TotalSeconds);
					overlay.EndOverlay();
				}

				phase[3] += clock.Elapsed.TotalSeconds - mark;
				mark = clock.Elapsed.TotalSeconds;
				loopStep = 7;
				sounds.Update(elapsed);
				loopStep = 9;
				string audio = sounds.Watch(elapsed, paused);
				if (audio != null)
				{
					Log(audio);
				}

				loopStep = 10;
				renderer.Profile.EndFrame();
				phase[4] += clock.Elapsed.TotalSeconds - mark;
				mark = clock.Elapsed.TotalSeconds;
				loopStep = 8;
				deko.EndFrame();
				phase[5] += clock.Elapsed.TotalSeconds - mark;
				gpuWait += deko.LastEndMilliseconds;
				frameStart = clock.Elapsed.TotalSeconds;
				frames++;
				total++;
				System.Threading.Interlocked.Increment(ref framesFinished);
				double took = clock.Elapsed.TotalSeconds - now;
				if (total > 3)
				{
					worst = Math.Max(worst, took);
				}

				// A long frame said, with whether a full collection ran in it (the 2026-11-24 drive had frames of 1.4 to 4.3 s,
				// with the managed heap at about 840 MB: whether those were the collector is what this tells).
				int fullNow = GC.CollectionCount(2), gen0Now = GC.CollectionCount(0);
				if (took > 0.25 && total > 3)
				{
					int longest = 0;
					for (int i = 1; i < phase.Length; i++)
					{
						if (phase[i] - phaseBefore[i] > phase[longest] - phaseBefore[longest])
						{
							longest = i;
						}
					}

					Log($"frames: a frame of {took * 1000:F0} ms, {(phase[longest] - phaseBefore[longest]) * 1000:F0} ms of it in {phaseNames[longest]}" +
					    (fullNow != fullBefore ? $", with {fullNow - fullBefore} full collection(s) in it" : gen0Now != gen0Before ? ", with a nursery collection in it" : ", no collection in it"));
				}

				fullBefore = fullNow;
				gen0Before = gen0Now;
				// (Not in the drive benchmark: its frames are the measure.)
				GcAtRest(driveBenchmark == null ? session : null, pause.Open, clock.Elapsed.TotalSeconds);

				if (total == 1)
				{
					Stage($"frames: the first in {took * 1000:F0} ms; {deko.Describe()}");
				}

				if (now - windowStart >= 5.0)
				{
					// The clocks as they are at the window's end (the user's, 2026-11-07: whether the overclock holds).
					ClockNow(out int cpuNow, out int gpuNow, out int memoryNow);
					benchmarkCpu = cpuNow;
					benchmarkGpu = gpuNow;
					Stage($"frames: {frames / (now - windowStart):F1} fps, the slowest {worst * 1000:F1} ms, clocks CPU {cpuNow} GPU {gpuNow} memory {memoryNow} MHz, {renderer.VisibleFaceCount} visible faces; {deko.Describe()}");
					// The collector in the window: collections by generation, and the full ones made at rest (GcAtRest).
					int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
					Log($"frames: collections in the window: {g0 - windowGen0} nursery, {g1 - windowGen1} gen 1, {g2 - windowGen2} full ({restCollections} of them at rest so far)");
					windowGen0 = g0;
					windowGen1 = g1;
					windowGen2 = g2;
					// Where a frame's time goes, on average: the loop's phases, then the renderer's own (as Android's GameView logs it).
					string[] names = { "input", "simulation", "RenderScene", "overlay", "sound", "end of frame" };
					System.Text.StringBuilder split = new System.Text.StringBuilder("frames: per frame ");
					for (int i = 0; i < phase.Length; i++)
					{
						split.Append(i == 0 ? string.Empty : ", ").Append(names[i]).Append(' ').Append((phase[i] * 1000 / Math.Max(1, frames)).ToString("0.0")).Append(" ms");
						phase[i] = 0;
					}

					split.Append(" (of it submitting and waiting for the GPU ").Append((gpuWait / Math.Max(1, frames)).ToString("0.0")).Append(" ms)");
					gpuWait = 0;
					Log(split.ToString());
					Log("frames: renderer " + renderer.Profile.Report());
					if (++windows % 6 == 1)
					{
						Log("frames: static batches " + renderer.BatchSummary);
						Log("frames: passes " + renderer.PassSummary + "; lists kept in " + renderer.ReusedLists + " frames so far; faces through the cores " + renderer.QueuedFacesDrawn + ", left out " + renderer.DroppedFaces + "; emulated plugins " + OpenBve.NativePlugins.EmulatedWin32Plugin.TakeElapse() + "; scripted trains far along the line, not placed: " + OpenBve.Android.AndroidTrainManager.FarTrainsSkipped);
						AudioStats(out long played, out int timedOut, out int restarted);
						Log("frames: audio output: " + played + " buffers played, " + timedOut + " waits timed out, " + restarted + " restarts");
						if (compressedTextures != null)
						{
							Log("frames: " + compressedTextures.Describe());
							Log("frames: texture transparency in the background: " + OpenBve.Android.TextureClassifier.BackgroundSummary + "; decoded on the render thread: " +
							    LibRender2.Textures.TextureManager.TypeDecodes + " (" + LibRender2.Textures.TextureManager.TypeDecodeTicks * 1000 / System.Diagnostics.Stopwatch.Frequency + " ms)");
						}
					}

					frames = 0;
					worst = 0;
					windowStart = now;
				}
			}

			mascon.Detach();
			Stage($"frames: {total} drawn");
		}

		private static int Run()
		{
			AppDomain.CurrentDomain.FirstChanceException += OnThrown;
			// Loading at the CPU's 1,785 MHz with the GPU held low: apm's FastLoad, the console's own loading clock.
			int mhz = SetCpuBoost(1);
			Stage($"OBNS for the Switch (1.32): {RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} cores, CPU {mhz} MHz");

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
			// Horizon runs a lower-priority thread only when nothing higher on its core can: the background texture pass at
			// Lowest froze the game at Tuen Mun's start (2026-11-07), so here it runs at Normal, its own Sleep(1)s giving way.
			OpenBve.Android.TextureClassifier.BackgroundPriority = System.Threading.ThreadPriority.Normal;

			string routeFile = null, trainFolder = null, imageFile = null;
			string choice = Root + "/UserData/nx-load.txt";
			// The drive benchmark (DriveBenchmark): its route and train, without the menu.
			driveBenchmark = DriveBenchmark.FromFile(Root, Log);
			if (driveBenchmark != null)
			{
				routeFile = driveBenchmark.RouteFile;
				trainFolder = driveBenchmark.TrainFolder;
				Stage(driveBenchmark.Describe());
			}
			else if (File.Exists(choice))
			{
				string[] lines = File.ReadAllLines(choice);
				routeFile = lines.Length > 0 && lines[0].Trim().Length != 0 ? lines[0].Trim() : null;
				trainFolder = lines.Length > 1 && lines[1].Trim().Length != 0 ? lines[1].Trim() : null;
			}

			if (driveBenchmark == null && !File.Exists(choice))
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
				// #11 stage 1 (OBNS/SWITCH.md): meshes straight into deko3d's memory (NX/LibRender2), no GL names and no
				// vertex copies kept - so nothing for VertexCopyReleaser's 128 states a frame to look through.
				// UserData/nx-gl-meshes.txt keeps the meshes on DekoGL's GL side, as before, for comparisons.
				if (!File.Exists(Root + "/UserData/nx-gl-meshes.txt"))
				{
					LibRender2.NativeMeshes.Device = deko;
					VertexCopyReleaser.Enabled = false;
					Log("deko3d: meshes in deko3d's memory directly (#11 stage 1)");
				}
				// Streamed textures kept block-compressed on the SD card (item E); UserData/nx-no-texture-cache.txt keeps it off.
				if (!File.Exists(Root + "/UserData/nx-no-texture-cache.txt"))
				{
					try
					{
						compressedTextures = new Deko.CompressedTextures(deko, Root + "/UserData/TextureCache");
						AndroidHost.CompressedTextures = compressedTextures;
					}
					catch (Exception e)
					{
						Log("compressed textures: off (" + e.Message + ")");
					}
				}
			}
			else
			{
				Log("deko3d did not open (" + opened + "): nothing is drawn");
			}

			// As GameView: the renderer, sounds, trains, plugins.
			DisplayDevice.Default = new DisplayDevice(width, height, 1.0f, 1.0f);
			AndroidRenderer renderer = new AndroidRenderer(host, options, fileSystem);
			// Colour-keyed faces in one cut-out pass (the user's choice, 2026-11-07: harder edges for a good part of the frame).
			AndroidRenderer.CutoutKeyedFaces = true;
			// Cars behind the camera or far away updated less often (the user's choice, 2026-11-07).
			AndroidTrainManager.CarUpdateLod = true;
			// Content textures unused for 20 s unloaded (upstream's unloading never did: nothing is marked unloadable) - the Options'
			// "Unload unused textures", on by default.
			LibRender2.Textures.TextureManager.UnloadUnpinned = SwitchSettings.GetUnloadTextures();
			// Last frame's filtered face lists kept while nothing they depend on changed (#2, 2026-11-07).
			// Each of the 2026-11-07 renderer changes can be turned off for a comparison by a file in UserData.
			AndroidRenderer.ReuseFaceLists = !File.Exists(Root + "/UserData/nx-no-list-reuse.txt");
			StaticBatcher.BatchWorldObjects = !File.Exists(Root + "/UserData/nx-no-world-batches.txt");
			// Faces completed on all three cores (#1, 2026-11-07); UserData/nx-serial-faces.txt keeps them on one.
			AndroidRenderer.ParallelFaces = !File.Exists(Root + "/UserData/nx-serial-faces.txt");
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

			AndroidSounds sounds = new AndroidSounds(host, renderer, options)
			{
				// Closing the device joins SDL's audio thread, which can be waiting in audout: a hang for good (2026-11-07).
				ReopenOutput = false
			};
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
			// Parsed objects kept on the SD card for the next load of the route (ObjectDiskCache); the file keeps it off.
			ObjectDiskCache.Folder = File.Exists(Root + "/UserData/nx-no-object-cache.txt") ? null : Root + "/UserData/ObjectCache";
			/*
			 * The textures' transparency, as Android keeps it in its cache folder: the next load of the same content decodes
			 * none of them for it. Unset here until 2026-11-07, which cost the Tuen Ma Line 93 s of decoding on every load.
			 */
			try
			{
				Directory.CreateDirectory(Root + "/UserData/Cache");
				TextureClassifier.CacheFolder = Root + "/UserData/Cache";
			}
			catch (Exception e)
			{
				Log("texture transparency: no cache folder (" + e.Message + ")");
			}
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
					// As GameView: the trains' vertex copies dropped once their buffers are made, and the trains handed to the
					// static batcher, which merges each car's still parts into a few draws (without it every face of the
					// Tuen Ma Line's cars was a draw of its own - most of the 10,900 a frame, 2026-11-06).
					VertexCopyReleaser.ReleaseAll(renderer);
					renderer.TrainSource = () => System.Linq.Enumerable.Concat(host.TrainList, System.Linq.Enumerable.OfType<global::TrainManager.Trains.TrainBase>(trainManager.TFOs));
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

			// The objects parsed this load, written down for the next (ObjectDiskCache).
			if (ObjectDiskCache.Active)
			{
				ObjectDiskCache.Close();
				Log(ObjectDiskCache.Describe());
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
				// Play's clocks: FastLoad, the loading clock, holds the GPU at 76 MHz - on the console the first frames waited
				// 120 ms a frame for it (2026-11-06) - so it ends here, and the performance mode in Options sets the clocks and
				// the fan (the user, 2026-11-06), held there by the loop and put back when OBNS ends.
				OpenBve.Android.PerformanceMode performance = SwitchSettings.GetPerformanceMode();
				SetCpuBoost((int)performance);
				ClockRates(out int cpuMhz, out int gpuMhz, out int memoryMhz);
				Stage($"frames: performance mode {performance} - CPU {cpuMhz} MHz, GPU {(gpuMhz > 0 ? gpuMhz + " MHz" : "the system's")}, memory {(memoryMhz > 0 ? memoryMhz + " MHz" : "the system's")}" +
				      (performance == OpenBve.Android.PerformanceMode.Normal ? ", the system's fan control" : ", the fan at full"));
				// The CPU records a frame while the GPU draws the one before (DekoGL); the file turns it off, for comparisons.
				OpenBve.NX.Deko.DekoGL.SetFramesInFlight(!File.Exists(Root + "/UserData/nx-serial-frames.txt"));
				Stage("frames: " + (OpenBve.NX.Deko.DekoGL.FramesInFlight ? "two in flight" : "one at a time (nx-serial-frames.txt)"));
				deko.Drawing = true;
				// As GameView: textures first needed while driving are decoded in the background, not in the frame.
				host.StreamTextures = true;
				// No time limit in play (the user, 2026-11-08: a 10-minute one was left from the first tests); the drive benchmark,
				// which runs unattended, stops after 2 hours whatever happens.
				Frames(renderer, host, sounds, session, deko, driveBenchmark != null ? 7200.0 : double.PositiveInfinity);
				deko.Dispose();
				OpenTK.Graphics.OpenGL.GL.Device = null;
				LibRender2.NativeMeshes.Device = null;
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
