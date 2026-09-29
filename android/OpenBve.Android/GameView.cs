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
using System.Threading;
using Android.Content;
using Android.Opengl;
using Android.Runtime;
using Android.Util;
using Android.Views;
using LibRender2;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenTK;
using OpenTK.Graphics;
using SoundManager;
using OpenTK.Graphics.OpenGL;
using MatrixMode = OpenTK.Graphics.OpenGL.MatrixMode;
using Vector2 = OpenBveApi.Math.Vector2;

namespace OpenBve.Android
{
	/// <summary>
	/// The drawing surface and render loop.
	/// </summary>
	/// <remarks>
	/// Upstream drives rendering from OpenTK's GameWindow, which does not exist on Android. Here
	/// the activity owns a SurfaceView, and a dedicated render thread holds the EGL context for
	/// its lifetime, because LibRender2 assumes GL calls happen on one consistent thread, and tells
	/// itself which one through AndroidGraphicsContext.
	/// </remarks>
	public class GameView : SurfaceView, ISurfaceHolderCallback
	{
		private const string Tag = "OpenBVE";

		private Thread renderThread;
		private volatile bool running;
		private volatile int surfaceWidth;
		private volatile int surfaceHeight;

		/// <summary>Raised once the renderer has started, with a line describing what happened.</summary>
		public event Action<string> Started;

		/// <summary>Raised if the renderer could not start.</summary>
		public event Action<Exception> Failed;

		/// <summary>Raised a few times a second, on the render thread, with the driving information line.</summary>
		public event Action<DriverStatus> InfoChanged;

		/// <summary>The driver's commands, fed by the on-screen controls.</summary>
		public AndroidControls Controls { get; } = new AndroidControls();

		/// <summary>
		/// Whether the demo driver takes the controls instead of the player, for unattended tests
		/// (the <c>autodrive</c> intent extra).
		/// </summary>
		public bool AutoDrive { get; set; }

		/// <summary>Whether the desktop interface is in use (upstream's HUD drawn), rather than the touch one.</summary>
		public bool DesktopInterface { get; set; }

		/// <summary>The display's density (1 at 160 dpi), kept up to date by the activity as the window moves between displays.</summary>
		public float DisplayDensity { get; set; }

		/// <summary>
		/// Switches between the touch and desktop interfaces while running: upstream's HUD is
		/// loaded or cleared on the render thread, and the touch interface's own displays take
		/// over from upstream's timetable (or give way to it).
		/// </summary>
		public void SetInterface(bool desktop)
		{
			DesktopInterface = desktop;
			Controls.Post(session =>
			{
				if (desktop)
				{
					HUD.LoadHUD();
				}
				else
				{
					HUD.CurrentHudElements = new HUD.Element[0];
					Program.Renderer.CurrentTimetable = LibRender2.Overlays.DisplayedTimetable.None;
				}

				AndroidTrainSession.TouchInterface = !desktop;
			});
		}

		/// <summary>
		/// Whether the simulation is stopped for the pause menu. The view is still drawn, and the
		/// camera can still be moved, as upstream's menu leaves the scene behind it.
		/// </summary>
		public bool Paused
		{
			get => paused;
			set => paused = value;
		}

		private volatile bool paused;

		/// <summary>The running session, once the train has loaded; null before, or without a train.</summary>
		public AndroidTrainSession Session => session;

		private volatile AndroidTrainSession session;

		/// <summary>For measurements: skip scripted trains (the <c>skip_tfo</c> intent extra).</summary>
		public bool SkipTrackFollowingObjects { get; set; }

		/// <summary>What to load, as chosen in the menu. Empty fields fall back to the first route and train found.</summary>
		public LaunchParameters Launch { get; set; } = new LaunchParameters();

		public GameView(Context context) : base(context)
		{
			Holder.AddCallback(this);
		}

		protected GameView(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
		{
		}

		/*
		 * In the desktop interface there is no overlay over the view: the mouse (and touches, on
		 * a DeX touch screen) reach the scene here - cab touch areas, looking around, the wheel.
		 */
		private SceneInput scene;

		public override bool OnTouchEvent(MotionEvent e)
		{
			if (!DesktopInterface)
			{
				return base.OnTouchEvent(e);
			}

			scene ??= new SceneInput(this, Controls);
			return scene.OnTouch(e);
		}

		public override bool OnGenericMotionEvent(MotionEvent e)
		{
			scene ??= new SceneInput(this, Controls);
			return scene.OnGenericMotion(e) || base.OnGenericMotionEvent(e);
		}

		/// <inheritdoc />
		public void SurfaceCreated(ISurfaceHolder holder)
		{
			// A new window for a game already running (another display, back from the background): carry on in it.
			if (renderThread != null && renderThread.IsAlive)
			{
				newWindow = holder.Surface;
				windowReady.Set();
				return;
			}

			running = true;
			renderThread = new Thread(() => RenderLoop(holder)) { Name = "OpenBVE render", IsBackground = true };
			renderThread.Start();
		}

		/*
		 * The window can go and come back without the game ending: moving between the phone's
		 * screen and a desktop display (DeX), or the app going to the background. The render
		 * thread then lets go of the window, keeping its GL context - and with it the loaded
		 * route, train and sounds - waits for the new one, and carries on. Rebuilding the game
		 * each time cost a full reload and put the train back at its first station.
		 */
		private readonly ManualResetEventSlim windowGone = new ManualResetEventSlim(false);
		private readonly ManualResetEventSlim windowReady = new ManualResetEventSlim(false);
		private volatile bool detachRequested;

		/// <summary>Raised on the render thread when the game carries on in a new window (possibly on another display).</summary>
		public event Action WindowBack;
		private volatile Surface newWindow;

		/// <inheritdoc />
		public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height)
		{
			surfaceWidth = width;
			surfaceHeight = height;
		}

		/// <inheritdoc />
		public void SurfaceDestroyed(ISurfaceHolder holder)
		{
			if (renderThread == null || !renderThread.IsAlive)
			{
				return;
			}

			// The window must be let go of before this returns; the thread then waits for the next one.
			windowReady.Reset();
			windowGone.Reset();
			detachRequested = true;
			if (!windowGone.Wait(3000))
			{
				Log.Warn(Tag, "the render thread did not let go of the window in time");
			}
		}

		/// <summary>
		/// On the render thread: if the window is going, lets go of it and waits for the next one.
		/// Returns false if the game ended meanwhile.
		/// </summary>
		private bool FollowWindow(IRenderSurface egl, AndroidSounds sounds, ref DateTime last)
		{
			if (!detachRequested)
			{
				return true;
			}

			detachRequested = false;
			egl.DetachWindow();
			windowGone.Set();
			// Silent while there is nothing to see (the background, or between displays).
			sounds?.SetPaused(true);
			Log.Info(Tag, "window gone: waiting for the next one, with the game kept");
			while (running && !windowReady.Wait(500))
			{
			}

			if (!running)
			{
				return false;
			}

			egl.AttachWindow(newWindow);
			GL.ResetStateCache();
			// No simulated time passes while there is no window.
			last = DateTime.UtcNow;
			WindowBack?.Invoke();
			sounds?.SetPaused(paused);
			Log.Info(Tag, "window back: carrying on");
			return true;
		}

		private void RenderLoop(ISurfaceHolder holder)
		{
			IRenderSurface egl = null;
			AndroidRenderer renderer = null;
			AndroidSounds sounds = null;

			try
			{
				// Fixed for the life of the process: the GL bindings attach to a library on first use.
				GraphicsBackend backend = GraphicsLibraries.Select(AndroidSettings.GetBackend(Context));
				egl = backend == GraphicsBackend.Vulkan
					? AngleSurface.Create(holder.Surface)
					: EglSurface.Create(holder.Surface);
				Log.Info(Tag, "graphics backend: " + backend + " (" + GraphicsLibraries.Detail + ")");

				// LibRender2 checks whether it is on the render thread before touching GL objects.
				AndroidGraphicsContext.RenderThreadId = Environment.CurrentManagedThreadId;

				int width = surfaceWidth > 0 ? surfaceWidth : Width;
				int height = surfaceHeight > 0 ? surfaceHeight : Height;
				DisplayDevice.Default = new DisplayDevice(width, height, 1.0f, 1.0f);
				/*
				 * Upstream's overlays are sized for a monitor at 96 dpi; Android's density is on
				 * the same footing (1.0 at 160 dpi, viewed nearer). A DeX monitor comes out at
				 * about 1 to 1.5, a phone at about 2.6 - capped so the virtual screen keeps at least
				 * 600 pixels of height, which upstream's HUD layouts need.
				 */
				DisplayDensity = Resources?.DisplayMetrics?.Density ?? 1.0f;
				AndroidRenderer.OverlayScale = Math.Max(1.0, Math.Min(DisplayDensity, height / 600.0));

				AndroidHost host = new AndroidHost();
				AndroidOptions options = new AndroidOptions();
				AndroidSettings.ApplyTo(Context, options);
				OpenBveApi.FileSystem.FileSystem fileSystem = AndroidFileSystem.Create(Context, host);

				// Load translations before anything can report an error, or the messages come out empty.
				Menu.Init(Context);
				Menu.SetLanguage(options.LanguageCode);

				renderer = new AndroidRenderer(host, options, fileSystem);
				profile = renderer.Profile;
				// The host loads textures through the renderer's texture manager.
				host.Renderer = renderer;
				// For the upstream files compiled as they are (UpstreamFacade.cs).
				Program.CurrentHost = host;
				Program.Renderer = renderer;
				host.Options = options;
				renderer.GameWindow = new GameWindow { Width = width, Height = height };
				renderer.Screen.Width = width;
				renderer.Screen.Height = height;
				renderer.Screen.AspectRatio = (double)width / height;

				// Before any content loads: routes and trains register their sounds as they are parsed.
				sounds = new AndroidSounds(host, renderer, options);
				sounds.Initialize(SoundRange.Low);
				host.Sounds = sounds;
				Program.Sounds = sounds;

				// Route plugins place scripted trains through the train manager during a full load.
				AndroidTrainManager trainManager = new AndroidTrainManager(host, renderer, options, fileSystem);
				Program.TrainManager = trainManager;
				Interface.CurrentOptions = options;

				// Plugins before Initialize: it loads the menu textures, which go through the texture plugins.
				string plugins = AndroidPlugins.Register(host, fileSystem, options, trainManager, renderer);
				renderer.Initialize();
				Program.FileSystem = fileSystem;
				// The in-game HUD layout (Data/In-game/<UserInterfaceFolder>/interface.cfg), as upstream's GameWindow.
				HUD.LoadHUD();
				if (!DesktopInterface)
				{
					/*
					 * Touch mode: the overlay's information line under the top row of buttons shows
					 * speed, limits, handles, the next stop and the current messages, so the desktop
					 * HUD's elements are not drawn. The timetable and route map still are, on request.
					 */
					HUD.CurrentHudElements = new HUD.Element[0];
					AndroidTrainSession.TouchInterface = true;
				}

				// Track following objects, loaded with the route, look for their trains relative to these.
				host.FileSystem = fileSystem;
				host.SkipTrackFollowingObjects = SkipTrackFollowingObjects;
				host.CurrentTrainFolder = !string.IsNullOrEmpty(Launch.TrainFolder) ? Launch.TrainFolder : AndroidTrainSession.FindTrain(fileSystem.InitialTrainFolder, host) ?? string.Empty;

				string version = GL.GetString(StringName.Version) + " on " + GL.GetString(StringName.Renderer) +
				                 (backend == GraphicsBackend.Vulkan ? " (Vulkan via ANGLE)" : string.Empty);
				LoadProfile.Begin("route parser");
				string route = AndroidRouteLoader.LoadRoute(host, fileSystem, renderer, Launch.RouteFile, Launch.RouteEncoding, out RouteManager2.CurrentRoute currentRoute);
				Log.Info(Tag, "load profile, route: " + LoadProfile.End());
				Log.Info(Tag, "memory after route: " + (GC.GetTotalMemory(true) >> 20) + " MB live, " + ResidentMegabytes() + " MB resident");

				bool haveScene = currentRoute != null;
				AndroidTrainSession loadedSession = null;
				string train = string.Empty;
				if (haveScene)
				{
					renderer.Route = currentRoute;
					Program.CurrentRoute = currentRoute;
					sounds.Route = currentRoute;
					LoadProfile.Begin("scene preparation");
					renderer.PrepareScene(host, options.ViewingDistance);
					Log.Info(Tag, "load profile, scene: " + LoadProfile.End());
					LoadProfile.Begin("train and placement");
					train = "\n" + AndroidTrainSession.Load(host, trainManager, renderer, currentRoute, fileSystem, host.CurrentTrainFolder, Launch.TrainEncoding, out loadedSession);
					Log.Info(Tag, "load profile, train: " + LoadProfile.End());
					// The render loop and the activity's menus reach the session through this field.
					session = loadedSession;
					loadedSession?.PrepareOverlays();
				// From here on, textures first needed while driving are decoded in the background.
				host.StreamTextures = true;
				}

				/*
				 * Loading leaves a great deal of garbage behind - decoded texture probes, parser
				 * buffers, intermediate meshes - and on a phone that heap competes with everything
				 * else for RAM. Collect it once now, before driving starts, rather than letting it
				 * sit until the collector next runs (or the low-memory killer does).
				 */
				long before = GC.GetTotalMemory(false) >> 20;
				GC.Collect();
				GC.WaitForPendingFinalizers();
				GC.Collect();
				Log.Info(Tag, "memory after load: " + before + " MB managed before collection, " + (GC.GetTotalMemory(true) >> 20) +
				              " MB live, " + ResidentMegabytes() + " MB resident");

				string summary = version + "\n" + plugins + "\n" + route + train +
				                 (host.ErrorCount == 0 ? string.Empty : "\n" + host.ErrorCount + " error(s) - see logcat");
				Log.Info(Tag, "startup: " + summary.Replace("\n", " | "));
				if (BetaLog.Enabled)
				{
					BetaLog.Event(null, "Game loaded - details below", true);
					System.Collections.Generic.List<(string, string)> fields = new System.Collections.Generic.List<(string, string)>
					{
						("Route", Launch.RouteFile + " (text encoding " + (Launch.RouteEncoding?.WebName ?? "auto-detected") + ")"),
						("Train", host.CurrentTrainFolder + " (text encoding " + (Launch.TrainEncoding?.WebName ?? "auto-detected") + ")"),
						("Safety plugin", session?.Train.Plugin == null ? "none" : session.Train.Plugin.PluginTitle),
						("Graphics", (AndroidSettings.GetBackend(Context) == GraphicsBackend.Vulkan ? "Vulkan (via ANGLE)" : "OpenGL ES") + " chosen, " +
						             GraphicsLibraries.Active + " in use"),
						("Graphics detail", GraphicsLibraries.Detail),
						("Game mode", DesktopInterface ? "Desktop (Android desktop mode: HUD, keyboard and controller, as on a PC)" : "Phone (touch interface)"),
						("Load time", TimeSpan.FromSeconds(BetaLog.SecondsSinceStart).ToString(@"m\:ss") + " (from opening the game)"),
						("Content errors", host.ErrorCount == 0 ? "none" : host.ErrorCount + " (listed in the log above)")
					};
					foreach (string part in summary.Split('\n'))
					{
						fields.Add(("Loaded", part.Replace("see logcat", "listed in the log")));
					}

					BetaLog.Block("Game", fields.ToArray());
					BetaLog.Block("Log (continued)");
				}

				Started?.Invoke(summary);

				DemoDriver driver = AutoDrive ? new DemoDriver() : null;
				bool soundsPaused = false;
				double infoTimer = 0.0;

				// Without a train, the camera simply travels the route at a steady 15 m/s.
				const double cameraSpeed = 15.0;
				double trackPosition = 0.0;
				DateTime last = DateTime.UtcNow;
				DateTime windowStart = last;
				int frames = 0;

				while (running)
				{
					if (!FollowWindow(egl, sounds, ref last))
					{
						break;
					}

					int newWidth = surfaceWidth > 0 ? surfaceWidth : width;
					int newHeight = surfaceHeight > 0 ? surfaceHeight : height;
					if (newWidth != width || newHeight != height)
					{
						// A resized window or another display: the picture's proportions follow.
						width = newWidth;
						height = newHeight;
						renderer.Screen.AspectRatio = (double)width / height;
						renderer.GameWindow.Width = width;
						renderer.GameWindow.Height = height;
						renderer.Camera.HorizontalViewingAngle = 2.0 * Math.Atan(Math.Tan(0.5 * renderer.Camera.VerticalViewingAngle) * renderer.Screen.AspectRatio);
						Log.Info(Tag, "surface now " + width + " x " + height);
					}

					renderer.Screen.Width = width;
					renderer.Screen.Height = height;
					// The window can be resized, or move to a display of another density (DeX).
					AndroidRenderer.OverlayScale = Math.Max(1.0, Math.Min(DisplayDensity, height / 600.0));

					if (haveScene)
					{
						DateTime now = DateTime.UtcNow;
						double elapsed = (now - last).TotalSeconds;
						last = now;
						renderer.Profile.Mark("swap and wait");

						if (session != null)
						{
							Controls.Apply(session);
							if (paused != soundsPaused)
							{
								soundsPaused = paused;
								sounds.SetPaused(paused);
							}

							if (paused)
							{
								session.UpdateView(elapsed, 0.0);
								// The scene still draws, so the camera can be moved, but there is no
								// need to redraw a standing train as fast as the screen allows.
								Thread.Sleep(10);
							}
							else
							{
								driver?.Update(session.Train, elapsed);
								session.Update(elapsed);
								Controls.UpdateTimetableScroll(elapsed);
								BetaLog.Frame(elapsed, () => session.Train.FrontCarTrackPosition.ToString("0") + " m, " +
								                             (session.Train.CurrentSpeed * 3.6).ToString("0") + " km/h, view " + renderer.Camera.CurrentMode +
								                             ", " + width + "x" + height + ", " + GraphicsLibraries.Active + ", " + (DesktopInterface ? "desktop" : "touch"));
								Game.CurrentScore.Update(elapsed);
							}

							// Messages time out in real time, as upstream (but not while paused).
							MessageManager.UpdateMessages(paused ? 0.0 : elapsed);
							Game.UpdateScoreMessages(paused ? 0.0 : elapsed);

							infoTimer += elapsed;
							if (infoTimer >= 0.2)
							{
								infoTimer = 0.0;
								InfoChanged?.Invoke(session.Status(session.Camera.ModeName, paused));
							}
							trackPosition = session.Train.Cars[0].TrackPosition;
							renderer.Profile.Mark("simulation");
						}
						else
						{
							trackPosition += cameraSpeed * elapsed;
							renderer.PlaceCamera(trackPosition);
							renderer.Profile.Mark("place camera");
						}

						host.BeginFrame();
						renderer.RenderScene(elapsed);
						if (session != null)
						{
							Controls.Cab.Update(session, renderer, width, height);
						}

						// As upstream's GameWindow, after drawing.
						if (options.UnloadUnusedTextures)
						{
							renderer.TextureManager.UnloadUnusedTextures(elapsed);
						}
						// After the scene, as upstream does: the listener follows the camera just placed.
						sounds.Update(elapsed, SoundModels.Inverse);
						renderer.Profile.Mark("sound");
						renderer.Profile.EndFrame();

						// Report the measured frame rate every five seconds.
						frames++;
						double window = (now - windowStart).TotalSeconds;
						if (window >= 5.0)
						{
							string speed = session != null ? ", " + session.SpeedKmh.ToString("0") + " km/h, section " + session.Train.CurrentSectionIndex : string.Empty;
							Log.Info(Tag, "scene: " + (frames / window).ToString("0.0") + " fps at track position " +
							              trackPosition.ToString("0") + " m" + speed + ", " + renderer.VisibleFaceCount + " visible faces");
							Log.Info(Tag, "frame: " + renderer.Profile.Report());
							if (renderer.LastFaceDescription.Length > 0)
							{
								Log.Info(Tag, "faces: " + renderer.LastFaceDescription + "; drawn in " + renderer.OpaqueDrawCalls + " calls");
							}

							renderer.DescribeFacesNextFrame = true;
							if (AndroidRenderer.CheckMerging)
							{
								if (renderer.LastMergeCheck.Length > 0)
								{
									Log.Info(Tag, "merge check: " + renderer.LastMergeCheck);
								}

								renderer.CheckMergeNextFrame = true;
							}
							if (session != null)
							{
								Log.Info(Tag, "train: " + session.Describe() + ", " + renderer.VisibleCabFaceCount + " cab faces");
								Log.Info(Tag, "memory: " + ResidentMegabytes() + " MB resident, " + (GC.GetTotalMemory(false) >> 20) + " MB managed, " +
								              renderer.TextureManager.GetNumberOfLoadedTextures() + " textures loaded, " + host.TexturesDecoding + " decoding");
								Log.Info(Tag, "sound: " + sounds.GetNumberOfPlayingSources() + " playing of " + sounds.GetNumberOfRegisteredSources() +
								              " sources, " + sounds.GetNumberOfLoadedBuffers() + " of " + sounds.GetNumberOfRegisteredBuffers() + " buffers loaded");
							}
							frames = 0;
							windowStart = now;
						}
					}
					else
					{
						DrawFrame(renderer, width, height);
					}

					egl.SwapBuffers();
				}
			}
			catch (Exception ex)
			{
				Log.Error(Tag, "render thread failed: " + ex);
				/*
				 * A failure part-way through a run is the hard one to report: the phone is not
				 * usually plugged into a computer when it happens, so logcat has gone by the time
				 * anyone looks. Keep a copy where the player can find and send it.
				 */
				WriteCrashLog(ex);
				Failed?.Invoke(ex);
			}
			finally
			{
				AndroidGraphicsContext.RenderThreadId = -1;
				sounds?.DeInitialize();
				egl?.Dispose();
			}
		}

		private volatile FrameProfile profile;

		/// <summary>
		/// How long the render thread has been inside one step, or zero if it is keeping up. A
		/// frame that never returns leaves the picture and the simulation frozen while the buttons,
		/// which live on the UI thread, still answer - so the watchdog names the step it stuck in.
		/// </summary>
		public TimeSpan Stalled
		{
			get
			{
				FrameProfile current = profile;
				long ticks = current == null ? 0L : System.Threading.Interlocked.Read(ref current.LastMarkTicks);
				if (ticks == 0L || !running)
				{
					return TimeSpan.Zero;
				}

				TimeSpan since = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
				return since > TimeSpan.FromSeconds(5.0) ? since : TimeSpan.Zero;
			}
		}

		/// <summary>The step the render thread last finished, for the stall report.</summary>
		public string LastPhase => profile?.LastPhase ?? "starting";

		/// <summary>Where a crash log was last written, for the message shown to the player.</summary>
		public static string CrashLogHint { get; private set; } = string.Empty;

		/// <summary>Writes the failure, with the state around it, to the app's own files folder.</summary>
		private void WriteCrashLog(Exception ex)
		{
			try
			{
				string folder = Context?.GetExternalFilesDir(null)?.AbsolutePath;
				if (folder == null)
				{
					return;
				}

				string path = System.IO.Path.Combine(folder, "crash.log");
				AndroidTrainSession current = session;
				string state = current == null
					? "no train"
					: "track position " + current.Train.Cars[0].TrackPosition.ToString("0.0") + " m, " +
					  current.SpeedKmh.ToString("0.0") + " km/h, " + current.Describe();
				System.IO.File.WriteAllText(path,
					DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
					"OpenBVE " + Menu.Version + ", route " + (Launch?.RouteFile ?? "?") + ", train " + (Launch?.TrainFolder ?? "?") + Environment.NewLine +
					state + Environment.NewLine + Environment.NewLine + ex);
				CrashLogHint = "Details written to " + path;
				BetaLog.Event("CRASH", "the game stopped: " + state + "\n" + ex, true);
				Log.Info(Tag, "crash log written to " + path);
			}
			catch (Exception write)
			{
				Log.Warn(Tag, "could not write the crash log: " + write.Message);
			}
		}

		/// <summary>The process's resident memory in MB, from /proc (textures on a phone GPU count here).</summary>
		private static long ResidentMegabytes()
		{
			try
			{
				// statm: size resident shared ... in pages.
				string[] fields = System.IO.File.ReadAllText("/proc/self/statm").Split(' ');
				return long.Parse(fields[1]) * Environment.SystemPageSize >> 20;
			}
			catch (Exception)
			{
				return -1;
			}
		}

		/// <summary>
		/// Draws a frame using LibRender2's own 2D path, in the same order the desktop overlay
		/// renderer does: an orthographic projection, then rectangles through the rectangle shader.
		/// </summary>
		private static void DrawFrame(BaseRenderer renderer, int width, int height)
		{
			GL.Viewport(0, 0, width, height);
			GL.ClearColor(0.05f, 0.09f, 0.16f, 1.0f);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			renderer.SetBlendFunc();
			renderer.PushMatrix(MatrixMode.Projection);
			Matrix4D.CreateOrthographicOffCenter(0.0f, width, height, 0.0f, -1.0f, 1.0f, out renderer.CurrentProjectionMatrix);
			renderer.PushMatrix(MatrixMode.Modelview);
			renderer.CurrentViewMatrix = Matrix4D.Identity;

			// A row of bars, so that both geometry and per-draw colour are visibly working.
			double margin = width * 0.08;
			double barWidth = (width - margin * 2.0) / 7.0;
			for (int i = 0; i < 6; i++)
			{
				double barHeight = height * (0.12 + 0.06 * i);
				renderer.Rectangle.Draw(null,
					new Vector2(margin + barWidth * i * 1.16, height * 0.75 - barHeight),
					new Vector2(barWidth, barHeight),
					new Color128(0.2f + 0.13f * i, 0.75f - 0.08f * i, 0.95f, 1.0f));
			}

			renderer.PopMatrix(MatrixMode.Modelview);
			renderer.PopMatrix(MatrixMode.Projection);
			renderer.UnsetBlendFunc();
		}
	}

	/// <summary>A GL context bound to an Android surface, held for the life of the render thread.</summary>
	public interface IRenderSurface : IDisposable
	{
		/// <summary>Presents the back buffer.</summary>
		void SwapBuffers();

		/// <summary>
		/// Lets go of the window, keeping the context and everything in it (textures, buffers,
		/// shaders): the window is going away, as when the app moves to another display.
		/// </summary>
		void DetachWindow();

		/// <summary>Makes the kept context current again on a new window.</summary>
		void AttachWindow(Surface window);
	}

	/// <summary>An EGL context from the system OpenGL ES driver.</summary>
	public sealed class EglSurface : IRenderSurface
	{
		private readonly EGLDisplay display;
		private readonly EGLConfig config;
		private readonly EGLContext context;
		private EGLSurface surface;

		private EglSurface(EGLDisplay display, EGLConfig config, EGLSurface surface, EGLContext context)
		{
			this.display = display;
			this.config = config;
			this.surface = surface;
			this.context = context;
		}

		/// <inheritdoc />
		public void DetachWindow()
		{
			EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
			EGL14.EglDestroySurface(display, surface);
			surface = EGL14.EglNoSurface;
		}

		/// <inheritdoc />
		public void AttachWindow(Surface window)
		{
			surface = EGL14.EglCreateWindowSurface(display, config, window, new[] { EGL14.EglNone }, 0);
			if (surface == EGL14.EglNoSurface || !EGL14.EglMakeCurrent(display, surface, surface, context))
			{
				throw new Exception("could not attach the context to the new window (0x" + EGL14.EglGetError().ToString("x") + ")");
			}
		}

		/// <summary>Creates a GLES 3 context on the given surface and makes it current.</summary>
		public static EglSurface Create(Surface window)
		{
			EGLDisplay display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
			if (display == EGL14.EglNoDisplay)
			{
				throw new Exception("no EGL display");
			}

			int[] version = new int[2];
			if (!EGL14.EglInitialize(display, version, 0, version, 1))
			{
				throw new Exception("eglInitialize failed");
			}

			int[] configAttributes =
			{
				EGL14.EglRenderableType, EGLExt.EglOpenglEs3BitKhr,
				EGL14.EglSurfaceType, EGL14.EglWindowBit,
				EGL14.EglRedSize, 8,
				EGL14.EglGreenSize, 8,
				EGL14.EglBlueSize, 8,
				EGL14.EglAlphaSize, 8,
				EGL14.EglDepthSize, 24,
				EGL14.EglNone
			};

			EGLConfig[] configs = new EGLConfig[1];
			int[] configCount = new int[1];
			if (!EGL14.EglChooseConfig(display, configAttributes, 0, configs, 0, 1, configCount, 0) || configCount[0] < 1)
			{
				throw new Exception("no GLES 3 capable EGL config with a 24 bit depth buffer");
			}

			int[] contextAttributes = { EGL14.EglContextClientVersion, 3, EGL14.EglNone };
			EGLContext context = EGL14.EglCreateContext(display, configs[0], EGL14.EglNoContext, contextAttributes, 0);
			if (context == EGL14.EglNoContext)
			{
				throw new Exception("eglCreateContext failed (0x" + EGL14.EglGetError().ToString("x") + ")");
			}

			EGLSurface surface = EGL14.EglCreateWindowSurface(display, configs[0], window, new[] { EGL14.EglNone }, 0);
			if (surface == EGL14.EglNoSurface)
			{
				throw new Exception("eglCreateWindowSurface failed (0x" + EGL14.EglGetError().ToString("x") + ")");
			}

			if (!EGL14.EglMakeCurrent(display, surface, surface, context))
			{
				throw new Exception("eglMakeCurrent failed (0x" + EGL14.EglGetError().ToString("x") + ")");
			}

			// The GL shim caches driver state per context; this is a new one.
			GL.ResetStateCache();
			return new EglSurface(display, configs[0], surface, context);
		}

		/// <summary>Presents the back buffer.</summary>
		public void SwapBuffers()
		{
			EGL14.EglSwapBuffers(display, surface);
		}

		/// <inheritdoc />
		public void Dispose()
		{
			EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
			if (surface != EGL14.EglNoSurface)
			{
				EGL14.EglDestroySurface(display, surface);
			}

			EGL14.EglDestroyContext(display, context);
			EGL14.EglTerminate(display);
		}
	}
}
