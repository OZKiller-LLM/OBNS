// OBNS: a copy of upstream's source/OpenBVE/System/GameWindow.cs (OpenBVE 1.14.1.2), laid over the desktop build's
// mirror (OBNS/build-desktop.ps1): frame time statistics in the log with OBNS_FRAMESTATS=1 (FrameStats).
// On an upstream update: copy the file again and reapply the parts marked OBNS (FrameStats calls).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LibRender2;
using LibRender2.Cameras;
using LibRender2.Menu;
using LibRender2.Overlays;
using LibRender2.Screens;
using LibRender2.Trains;
using LibRender2.Viewports;
using OpenBve.Graphics;
using OpenBve.Input;
using OpenBveApi.Colors;
using OpenBveApi.Runtime;
using OpenBveApi.Interface;
using OpenBveApi.Trains;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Input;
using OpenBveApi;
using OpenBveApi.Graphics;
using OpenBveApi.Hosts;
using OpenBveApi.Math;
using OpenBveApi.Routes;
using OpenTK.Graphics.OpenGL;
using RouteManager2.MessageManager;
using SoundManager;
using TrainManager.Car;
using TrainManager.Trains;
using Path = System.IO.Path;
using Vector2 = OpenTK.Vector2;
using Control = OpenBveApi.Interface.Control;
using MouseCursor = LibRender2.MouseCursor;

namespace OpenBve
{
	internal class OpenBVEGame: GameWindow
	{
		/// <summary>The current time acceleration factor</summary>
		private int TimeFactor = 1;
		private double TotalTimeElapsedForInfo;
		private double TotalTimeElapsedForSectionUpdate;
		private bool loadComplete;
		private bool firstFrame;
		/// <summary>OBNS: whether OBNS_START_VIEW has been seen to (once, after loading).</summary>
		private bool startViewChosen;
		private double RenderTimeElapsed;
		private double RenderRealTimeElapsed;
		//We need to explicitly specify the default constructor
		public OpenBVEGame(int width, int height, GraphicsMode currentGraphicsMode, GameWindowFlags @default): base(width, height, currentGraphicsMode, Translations.GetInterfaceString(HostApplication.OpenBve, new[] {"program","title"}), @default)
		{
			Program.FileSystem.AppendToLogFile("Creating game window with standard context.");
			if (Program.CurrentHost.Platform == HostPlatform.AppleOSX && IntPtr.Size != 4)
			{
				return;
			}
			try
			{
				string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
				Icon ico = new Icon(OpenBveApi.Path.CombineFile(OpenBveApi.Path.CombineDirectory(assemblyFolder, "Data"), "icon.ico"));
				Icon = ico;
			}
			catch
			{
				//it's only an icon
			}
		}

		public OpenBVEGame(int width, int height, GraphicsMode currentGraphicsMode, GameWindowFlags @default, GraphicsContextFlags flags): base(width, height, currentGraphicsMode, Translations.GetInterfaceString(HostApplication.OpenBve, new[] {"program","title"}), @default, DisplayDevice.Default, 3,3, flags)
		{
			Program.FileSystem.AppendToLogFile("Creating game window with forwards-compatible context.");
			if (Program.CurrentHost.Platform == HostPlatform.AppleOSX && IntPtr.Size != 4)
			{
				Interface.CurrentOptions.ForceForwardsCompatibleContext = true;
				return;
			}
			try
			{
				var assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
				Icon ico = new Icon(OpenBveApi.Path.CombineFile(OpenBveApi.Path.CombineDirectory(assemblyFolder, "Data"), "icon.ico"));
				Icon = ico;
			}
			catch
			{
				//it's only an icon
			}
		}


		//This renders the frame
		protected override void OnRenderFrame(FrameEventArgs e)
		{
			
			if (!firstFrame)
			{
				//If the load is not complete, then we shouldn't be running the mainloop
				return;
			}

			if (Program.Renderer.RenderThreadJobWaiting)
			{
				while (!Program.Renderer.RenderThreadJobs.IsEmpty)
				{
					Program.Renderer.RenderThreadJobs.TryDequeue(out ThreadStart currentJob);
					currentJob();
					lock (currentJob)
					{
						Monitor.Pulse(currentJob);
					}
				}
			}
			
			Program.Renderer.RenderThreadJobWaiting = false;
			double TimeElapsed = RenderTimeElapsed;
			double RealTimeElapsed = RenderRealTimeElapsed;
			
			//Next, check if we're in paused/ in a menu
			if(Program.Renderer.CurrentInterface != InterfaceType.Normal)
			{
				MainLoop.UpdateControlRepeats(0.0);
				MainLoop.ProcessKeyboard();
				MainLoop.ProcessControls(0.0);
				if (Program.Renderer.CurrentInterface == InterfaceType.Pause)
				{
					Thread.Sleep(10);
				}
				Program.Renderer.RenderScene(TimeElapsed, RealTimeElapsed);
				SwapBuffers();
				if (MainLoop.Quit != QuitMode.ContinueGame)
				{
					Close();
					if (Program.CurrentHost.MonoRuntime && MainLoop.Quit == QuitMode.QuitProgram)
					{
						Environment.Exit(0);
					}
				}
				//If the menu state has not changed, don't update the rendered simulation
				return;
			}
			
			//Use the OpenTK framerate as this is much more accurate
			//Also avoids running a calculation
			if (TotalTimeElapsedForInfo >= 0.2)
			{
				Program.Renderer.FrameRate = RenderFrequency;
				TotalTimeElapsedForInfo = 0.0;
			}
			
			
			FrameStats.BeginFrame();
			if (Program.Renderer.PreviousInterface != InterfaceType.Normal)
			{
				// Update animated objects with zero elapsed time (NOT time elapsed in menu)
				// and set again to avoid glitching
				ObjectManager.UpdateAnimatedWorldObjects(0.0, false);
				Program.Renderer.CurrentInterface = InterfaceType.Normal;
			}
			else
			{
				ObjectManager.UpdateAnimatedWorldObjects(TimeElapsed, false);
			}
			FrameStats.Mark(FrameStats.Part.WorldObjects);

			//We need to update the camera position in the render sequence
			//Not doing this means that the camera doesn't move
			// update in one piece
			CameraViewMode cameraMode = Program.Renderer.Camera.CurrentMode;
			if (cameraMode == CameraViewMode.Interior || cameraMode == CameraViewMode.InteriorLookAhead || cameraMode == CameraViewMode.Exterior)
			{
				CameraProperties cameraProperties = Program.Renderer.Camera;
				TrackFollower trackFollower = Program.Renderer.CameraTrackFollower;

				if (cameraProperties.IsTransitioning) cameraProperties.CameraCarTransitionTimer += RealTimeElapsed;
				if (cameraProperties.ModeTransitionTimer < 1.0) cameraProperties.ModeTransitionTimer += RealTimeElapsed / Interface.CurrentOptions.CameraTransitionSpeed;
				double carTransitionProgress = Math.Min(1.0, cameraProperties.CameraCarTransitionTimer / Interface.CurrentOptions.CameraTransitionSpeed);
				if ((cameraMode == CameraViewMode.Exterior && Interface.CurrentOptions.CameraExteriorTransition == false) || (cameraMode != CameraViewMode.Exterior && Interface.CurrentOptions.CameraInteriorTransition == false) || cameraMode == CameraViewMode.Interior)
				{
					// disabled transition
					carTransitionProgress = 1.0;
					cameraProperties.ModeTransitionTimer = 1.0;
				}
				if (carTransitionProgress >= 1.0)
				{
					cameraProperties.IsTransitioning = false;
					if (cameraProperties.TargetCameraCar != -1)
					{
						cameraProperties.PreviousCameraCar = TrainManager.PlayerTrain.CameraCar;
						TrainManager.PlayerTrain.CameraCar = cameraProperties.TargetCameraCar;
						cameraProperties.TargetCameraCar = -1;
						cameraProperties.IsTransitioning = true;
						cameraProperties.CameraCarTransitionTimer = 0.0;
						carTransitionProgress = 0.0;
					}
				}

				CarBase previousCar = (cameraProperties.IsTransitioning && cameraProperties.PreviousCameraCar != -1) ? TrainManager.PlayerTrain.Cars[cameraProperties.PreviousCameraCar] : null;
				//Update the in-car camera based upon the current driver car (Cabview or passenger view)
				TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.CameraCar].UpdateCamera(previousCar, carTransitionProgress);

				if (cameraProperties.ModeTransitionTimer < 1.0)
				{
					double modeTransitionLinear = Math.Min(1.0, cameraProperties.ModeTransitionTimer);
					double modeTransitionCosine = (1.0 - Math.Cos(modeTransitionLinear * Math.PI)) / 2.0;
					var startAnchor = cameraProperties.ModeTransitionStart;

					trackFollower.WorldPosition = OpenBveApi.Math.Vector3.CosineInterpolate(startAnchor.Position, trackFollower.WorldPosition, modeTransitionLinear);
					trackFollower.WorldDirection = OpenBveApi.Math.Vector3.CosineInterpolate(startAnchor.Direction, trackFollower.WorldDirection, modeTransitionLinear);
					trackFollower.WorldDirection.Normalize();
					trackFollower.WorldUp = OpenBveApi.Math.Vector3.CosineInterpolate(startAnchor.Up, trackFollower.WorldUp, modeTransitionLinear);
					trackFollower.WorldUp.Normalize();
					trackFollower.WorldSide = OpenBveApi.Math.Vector3.CosineInterpolate(startAnchor.Side, trackFollower.WorldSide, modeTransitionLinear);
					trackFollower.WorldSide.Normalize();
					trackFollower.UpdateAbsolute(
						startAnchor.TrackPosition + (trackFollower.TrackPosition - startAnchor.TrackPosition) * modeTransitionCosine,
						false,
						false);

					// Smoothly decay alignment back to zero
					cameraProperties.AlignmentDirection.Yaw = startAnchor.Alignment.Yaw * (1.0 - modeTransitionCosine);
					cameraProperties.AlignmentDirection.Pitch = startAnchor.Alignment.Pitch * (1.0 - modeTransitionCosine);
					cameraProperties.AlignmentDirection.Roll = startAnchor.Alignment.Roll * (1.0 - modeTransitionCosine);
					cameraProperties.AlignmentDirection.Position = startAnchor.Alignment.Position * (1.0 - modeTransitionCosine);
					cameraProperties.AlignmentDirection.Zoom = startAnchor.Alignment.Zoom + (0.0 - startAnchor.Alignment.Zoom) * modeTransitionCosine;
				}
			}
			
			if (Program.Renderer.Camera.CurrentRestriction == CameraRestrictionMode.NotAvailable || Program.Renderer.Camera.CurrentRestriction == CameraRestrictionMode.Restricted3D)
			{
				TrainManager.PlayerTrain.DriverBody.Update(TimeElapsed);
			}
			//Check if we are running at an accelerated time factor-
			//Camera motion speed should be the same whatever the game speed is
			if (TimeFactor != 1)
			{
				World.UpdateAbsoluteCamera(TimeElapsed / TimeFactor);
			}
			else
			{
				World.UpdateAbsoluteCamera(TimeElapsed);
			}
			FrameStats.Mark(FrameStats.Part.Other);
			Program.TrainManager.UpdateTrainObjects(TimeElapsed, false);
			FrameStats.Mark(FrameStats.Part.TrainObjects);
			if (Program.Renderer.Camera.CurrentMode == CameraViewMode.Interior | Program.Renderer.Camera.CurrentMode == CameraViewMode.InteriorLookAhead | Program.Renderer.Camera.CurrentMode == CameraViewMode.Exterior)
			{
				int d = TrainManager.PlayerTrain.DriverCar;
				Program.Renderer.Camera.CurrentSpeed = TrainManager.PlayerTrain.Cars[d].CurrentSpeed;
			}
			else
			{
				Program.Renderer.Camera.CurrentSpeed = 0.0;
			}

			Program.Renderer.Camera.AlignmentDirection = new CameraAlignment();
			if (MainLoop.Quit != QuitMode.ContinueGame)
			{
				Exit();
				if (Program.CurrentHost.MonoRuntime && MainLoop.Quit == QuitMode.QuitProgram)
				{
					Environment.Exit(0);
				}				
			}

			if (Program.CurrentRoute.DynamicLighting)
			{
				Program.Renderer.Lighting.UpdateLighting(Program.CurrentRoute.SecondsSinceMidnight, Program.CurrentRoute.LightDefinitions);
			}
			FrameStats.Mark(FrameStats.Part.Other);
			Program.Renderer.RenderScene(TimeElapsed, RealTimeElapsed);
			FrameStats.Mark(FrameStats.Part.Render);
			Program.Sounds.Update(TimeElapsed);
			FrameStats.Mark(FrameStats.Part.Other);
			// OBNS: a picture of the frame now and then, for comparing renderers (OBNS_SNAPSHOT_EVERY).
			FrameSnapshots.Take(Width, Height);
			Program.Renderer.GameWindow.SwapBuffers();
			FrameStats.Mark(FrameStats.Part.Swap);
			Game.UpdateBlackBox();
			// pause/menu
			
			MainLoop.UpdateControlRepeats(RealTimeElapsed);
			MainLoop.ProcessKeyboard();
			MainLoop.UpdateMouse(RealTimeElapsed);
			// OBNS: OBNS_START_VIEW=exterior starts in the exterior view (F2), as a key would: for comparing renderers from a
			// camera that does not depend on timing, with no keys sent (they cannot be while the screen is locked).
			if (simulationSetup && !startViewChosen)
			{
				startViewChosen = true;
				if (Environment.GetEnvironmentVariable("OBNS_START_VIEW") == "exterior")
				{
					for (int i = 0; i < Interface.CurrentControls.Length; i++)
					{
						if (Interface.CurrentControls[i].Command == Translations.Command.CameraExterior)
						{
							Interface.CurrentControls[i].DigitalState = DigitalControlState.Pressed;
							break;
						}
					}
				}
			}

			MainLoop.ProcessControls(TimeElapsed);
			if (Program.Joysticks.AttachedJoysticks.TryGetTypedValue(AbstractRailDriver.Guid, out AbstractRailDriver railDriver))
			{
				if (Interface.CurrentOptions.RailDriverMPH)
				{
					railDriver.SetDisplay((int)(TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.DriverCar].Specs.PerceivedSpeed * 2.23694));
				}
				else
				{
					railDriver.SetDisplay((int)(TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.DriverCar].Specs.PerceivedSpeed * 3.6));
				}
			}
			
			RenderRealTimeElapsed = 0.0;
			RenderTimeElapsed = 0.0;
				
				

#if DEBUG
			MainLoop.CheckForOpenGlError("MainLoop");
			 
#endif
			if (Interface.CurrentOptions.UnloadUnusedTextures)
			{
				Program.Renderer.TextureManager.UnloadUnusedTextures(TimeElapsed);
			}
			if (!textureStatsLogged && simulationSetup && Environment.TickCount - setupTickCount > 10000)
			{
				textureStatsLogged = true;
				Program.FileSystem.AppendToLogFile("Texture stats @10s: decodes " + Program.CurrentHost.TextureDecodeCalls + " calls / " + Program.CurrentHost.TextureDecodeMs + " ms | uploads " + LibRender2.Textures.TextureManager.UploadCount + " calls / " + LibRender2.Textures.TextureManager.UploadMs + " ms | GC gen0/1/2: " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2));
			}
			// finish
			try
			{
				Interface.SaveLogs();
			}
			catch
			{
				Interface.CurrentOptions.BlackBox = false;
			}
		}

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			TimeFactor = MainLoop.TimeFactor;
			// timer
			double RealTimeElapsed;
			double TimeElapsed;
			if (Program.CurrentRoute.SecondsSinceMidnight >= Game.StartupTime)
			{
				
				RealTimeElapsed = CPreciseTimer.GetElapsedTime();
				TimeElapsed = RealTimeElapsed * TimeFactor;
				if (loadComplete && !firstFrame)
				{
					/*
					 * Our current in-game time is equal to or greater than the startup time, but the first frame has not yet been processed
					 * Therefore, reset the timer to zero as time consuming texture loads may cause us to be late at the first station
					 *
					 * Also update the viewing distances in case of jumps etc.
					 */
					Program.Renderer.UpdateViewingDistances(Program.CurrentRoute.CurrentBackground.BackgroundImageDistance);
					// OBNS: OBNS_START_DELAY=<seconds> waits before the first frame, for testing on a machine loading leaves hot.
					if (int.TryParse(Environment.GetEnvironmentVariable("OBNS_START_DELAY"), out int startDelay) && startDelay > 0)
					{
						Program.FileSystem.AppendToLogFile("OBNS waiting " + startDelay + " s before the first frame (OBNS_START_DELAY)");
						System.Threading.Thread.Sleep(startDelay * 1000);
						CPreciseTimer.GetElapsedTime();
					}

					RealTimeElapsed = 0.0;
					TimeElapsed = 0.0;
					firstFrame = true;
				}
			}
			else
			{
				RealTimeElapsed = 0.0;
				TimeElapsed = Game.StartupTime - Program.CurrentRoute.SecondsSinceMidnight;
			}

			//We only want to update the simulation if we aren't in a menu
			if (Program.Renderer.CurrentInterface == InterfaceType.Normal)
			{
#if DEBUG
				//If we're in debug mode and a frame takes greater than a second to render, we can safely assume that VS has hit a breakpoint
				//Check this and the sim no longer barfs because the update time was too great
				if (RealTimeElapsed > 1)
				{
					RealTimeElapsed = 0.0;
					TimeElapsed = 0.0;
				}
#endif
				TotalTimeElapsedForInfo += RealTimeElapsed;
				TotalTimeElapsedForSectionUpdate += TimeElapsed;


				if (TotalTimeElapsedForSectionUpdate >= 1.0)
				{
					if (Program.CurrentRoute.Sections.Length != 0)
					{
						Program.CurrentRoute.UpdateAllSections();
					}
					TotalTimeElapsedForSectionUpdate = 0.0;
				}

				// events

				// update simulation in chunks
				{
					const double chunkTime = 1.0/2.0;
					if (TimeElapsed <= chunkTime)
					{
						Program.CurrentRoute.SecondsSinceMidnight += TimeElapsed;
						Program.TrainManager.UpdateTrains(TimeElapsed);
					}
					else
					{
						const int maxChunks = 2;
						int chunks = Math.Min((int) Math.Round(TimeElapsed/chunkTime), maxChunks);
						double time = TimeElapsed/chunks;
						for (int i = 0; i < chunks; i++)
						{
							Program.CurrentRoute.SecondsSinceMidnight += time;
							Program.TrainManager.UpdateTrains(time);
						}
					}
				}
				Game.CurrentScore.Update(TimeElapsed);
				MessageManager.UpdateMessages(Program.Renderer.CurrentInterface != InterfaceType.Menu ? RealTimeElapsed : 0); // n.b. don't update message timeouts when in menu
				Game.UpdateScoreMessages(Program.Renderer.CurrentInterface != InterfaceType.Menu ? RealTimeElapsed : 0);

				for (int i = 0; i < InputDevicePlugin.AvailablePluginInfos.Count; i++)
				{
					if (InputDevicePlugin.AvailablePluginInfos[i].Status == InputDevicePlugin.PluginInfo.PluginStatus.Enable)
					{
						InputDevicePlugin.AvailablePlugins[i].OnUpdateFrame();
					}
				}
			}
			RenderTimeElapsed += TimeElapsed;
			RenderRealTimeElapsed += RealTimeElapsed;
		}

		protected override void OnResize(EventArgs e)
		{
			if(Width == 0 && Height == 0)
			{
				/*
				 * HACK: Don't resize if minimized
				 * 
				 * Unsure if this is an openTK bug, Windows or what
				 * but setting our width / height to zero breaks 
				 * stuff.....
				 */
				Program.Renderer.Screen.Minimized = true;
				return;
			}
			Program.Renderer.Screen.Minimized = false;
			Screen.WindowResize(Width,Height);
			switch (Program.Renderer.CurrentInterface)
			{
				case InterfaceType.SwitchChangeMap:
					// call the show method again to trigger resize
					Game.SwitchChangeDialog.Show();
					break;
				case InterfaceType.Menu:
				case InterfaceType.GLMainMenu:
					Game.Menu.OnResize();
					break;
			}

			Program.Renderer.Rectangle.Update();
			Program.Renderer.OpenGlString.Update();
		}

		[DllImport("user32.dll")]
		private static extern int FindWindow(string className, string windowText);
		[DllImport("user32.dll")]
		public static extern int FindWindowEx(int parentHandle, int childAfter, string className, int windowTitle);

		[DllImport("user32.dll")]
		private static extern int ShowWindow(int hwnd, int command);
		[DllImport("user32.dll")]
		private static extern int GetDesktopWindow();
		protected override void OnLoad(EventArgs e)
		{
			// OBNS: with OBNS_GL_FILTER=1, sets of GL state that change nothing are skipped (Graphics/GLStateFilter.cs); the context is current here.
			GLStateFilter.Install();
			// OBNS: the world layer drawn by Vulkan where it can share images with OpenGL (OBNS/VULKAN.md).
			OpenBve.Graphics.WorldLayer.Initialize();
			// OBNS: OBNS_D3D11_TEST=1 checks the Direct3D 11 world layer's device and its images' path to OpenGL (OBNS/DIRECT3D.md).
			if (Environment.GetEnvironmentVariable("OBNS_D3D11_TEST") == "1")
			{
				OpenBve.Graphics.Direct3D11.D3D11Device device = OpenBve.Graphics.Direct3D11.D3D11Device.Create(out string reason);
				string test = device?.SelfTest();
				Program.FileSystem.AppendToLogFile(device == null ? "OBNS Direct3D 11: not usable: " + reason : "OBNS Direct3D 11: " + device.Description + "; images to OpenGL: " + (test ?? "right"));
				device?.Dispose();
			}
			// OBNS: OBNS_D3D12_TEST=1 checks the Direct3D 12 world layer's device and its images' path to OpenGL (OBNS/DIRECT3D.md).
			if (Environment.GetEnvironmentVariable("OBNS_D3D12_TEST") == "1")
			{
				OpenBve.Graphics.Direct3D12.D3D12Device device = OpenBve.Graphics.Direct3D12.D3D12Device.Create(out string reason);
				string test = device?.SelfTest();
				Program.FileSystem.AppendToLogFile(device == null ? "OBNS Direct3D 12: not usable: " + reason : "OBNS Direct3D 12: " + device.Description + "; images to OpenGL: " + (test ?? "right"));
				device?.Dispose();
			}
			// OBNS: OBNS_D3D9_TEST=1 checks the Direct3D 9 world layer's device and its images' path to OpenGL (OBNS/DIRECT3D.md).
			if (Environment.GetEnvironmentVariable("OBNS_D3D9_TEST") == "1")
			{
				OpenBve.Graphics.Direct3D9.D3D9Device device = OpenBve.Graphics.Direct3D9.D3D9Device.Create(out string reason);
				string test = device?.SelfTest();
				Program.FileSystem.AppendToLogFile(device == null ? "OBNS Direct3D 9: not usable: " + reason : "OBNS Direct3D 9: " + device.Description + "; images to OpenGL: " + (test ?? "right"));
				device?.Dispose();
			}
			// OBNS: OBNS_METAL_TEST=1 checks the Metal world layer's device and its images' path to OpenGL on macOS (OBNS/METAL.md).
			if (Environment.GetEnvironmentVariable("OBNS_METAL_TEST") == "1" && Program.CurrentHost.Platform == HostPlatform.AppleOSX)
			{
				OpenBve.Graphics.Metal.MetalDevice device = OpenBve.Graphics.Metal.MetalDevice.Create(out string reason);
				string test = device?.SelfTest();
				Program.FileSystem.AppendToLogFile(device == null ? "OBNS Metal: not usable: " + reason : "OBNS Metal: " + device.Description + "; images to OpenGL: " + (test ?? "right"));
				device?.Dispose();
			}
			if (Program.CurrentHost.Platform == HostPlatform.MicrosoftWindows && WindowState != WindowState.Fullscreen)
			{
				int w = DisplayDevice.Default.Width;
				int h = DisplayDevice.Default.Height;
				if (w == Interface.CurrentOptions.WindowWidth && h == Interface.CurrentOptions.WindowHeight)
				{
					// If we are not in full-screen, but height and width are equal to resolution, hide taskbar
					// e.g. Borderless windowed full-screen
					int hwnd = FindWindow("Shell_TrayWnd","");
					ShowWindow(hwnd,0);
					int hstart = FindWindowEx(GetDesktopWindow(), 0, "button", 0);
					ShowWindow(hstart,0);
					WindowBorder = WindowBorder.Hidden;
					Bounds = new Rectangle(0, 0, w, h);
				}
			}
			Program.FileSystem.AppendToLogFile("Game window initialised successfully.");
			//Initialise the loader thread queues
			Program.Renderer.Initialize();
			Program.Renderer.DetermineMaxAFLevel();
			Interface.CurrentOptions.Save(OpenBveApi.Path.CombineFile(Program.FileSystem.SettingsFolder, "1.5.0/options.cfg"));
			HUD.LoadHUD();
			Program.Renderer.Loading.InitLoading(Program.FileSystem.GetDataFolder("In-game"), typeof(NewRenderer).Assembly.GetName().Version.ToString());
			Program.Renderer.UpdateViewport(ViewportChangeMode.NoChange);
			Program.Renderer.MotionBlur.Initialize(Interface.CurrentOptions.MotionBlur);
			if (string.IsNullOrEmpty(MainLoop.currentResult.RouteFile))
			{
				GameMenu.LogoPictureBox.Texture = Program.Renderer.ProgramLogo;
				Game.Menu.PushMenu(MenuType.GameStart);
				Loading.Complete = true;
				Program.Renderer.CameraTrackFollower = new TrackFollower(Program.CurrentHost);
				loadComplete = true;
			}
			else
			{
				Loading.LoadAsynchronously(MainLoop.currentResult.RouteFile, MainLoop.currentResult.RouteEncoding, MainLoop.currentResult.TrainFolder, MainLoop.currentResult.TrainEncoding);
				LoadingScreenLoop();
			}
			//Add event handler hooks for keyboard and mouse buttons
			//Do this after the renderer has init and the loop has started to prevent timing issues
			KeyDown	+= MainLoop.KeyDownEvent;
			KeyUp	+= MainLoop.KeyUpEvent;
			MouseDown	+= MainLoop.mouseDownEvent;
			MouseUp += MainLoop.mouseUpEvent;
			MouseMove	+= MainLoop.mouseMoveEvent;
			MouseWheel  += MainLoop.mouseWheelEvent;
			FileDrop += GameMenu.Instance.DragFile;

			for (int i = 0; i < InputDevicePlugin.AvailablePluginInfos.Count; i++)
			{
				if (InputDevicePlugin.AvailablePluginInfos[i].Status == InputDevicePlugin.PluginInfo.PluginStatus.Enable)
				{
					int AddControlsLength = InputDevicePlugin.AvailablePlugins[i].Controls.Length;
					Control[] AddControls = new Control[AddControlsLength];
					for (int j = 0; j < AddControlsLength; j++)
					{
						AddControls[j].Command = InputDevicePlugin.AvailablePlugins[i].Controls[j].Command;
						AddControls[j].Method = ControlMethod.InputDevicePlugin;
						AddControls[j].Option = InputDevicePlugin.AvailablePlugins[i].Controls[j].Option;
					}
					Interface.CurrentControls = Interface.CurrentControls.Concat(AddControls).ToArray();
					foreach (var Train in Program.TrainManager.Trains)
					{
						if (Train.State != TrainState.Bogus)
						{
							if (Train.IsPlayerTrain)
							{
								InputDevicePlugin.AvailablePlugins[i].SetMaxNotch(Train.Handles.Power.MaximumDriverNotch, Train.Handles.Brake.MaximumDriverNotch);
							}
						}
					}
					InputDevicePlugin.AvailablePlugins[i].KeyDown += MainLoop.InputDevicePluginKeyDown;
					InputDevicePlugin.AvailablePlugins[i].KeyUp += MainLoop.InputDevicePluginKeyUp;
				}
			}
		}
		protected override void OnClosing(CancelEventArgs e)
		{
			// Minor hack:
			// If we are currently loading, catch the first close event, and terminate the loader threads
			// before actually closing the game-window.
			if (Loading.Cancel)
			{
				return;
			}
			if (!Loading.Complete)
			{
				e.Cancel = true;
				Loading.Cancel = true;
			}

			try
			{
				//Force-save the black-box log, as otherwise we may be missing upto 30s of data
				Interface.SaveLogs(true);
			}
			catch
			{
				//Saving black-box failed, not really important
			}
			for (int i = 0; i < Program.TrainManager.Trains.Count; i++)
			{
				if (Program.TrainManager.Trains[i].State != TrainState.Bogus)
				{
					Program.TrainManager.Trains[i].UnloadPlugin();
				}
			}
			Program.Renderer.TextureManager.UnloadAllTextures(false);
			Program.Renderer.VisibilityThreadShouldRun = false;
			for (int i = 0; i < InputDevicePlugin.AvailablePluginInfos.Count; i++)
			{
				InputDevicePlugin.CallPluginUnload(i);
			}
			if (MainLoop.Quit == QuitMode.ContinueGame && Program.CurrentHost.MonoRuntime)
			{
				//More forcefully close under Mono, stuff *still* hanging around....
				Environment.Exit(0);
			}

			if (Program.CurrentHost.Platform == HostPlatform.MicrosoftWindows)
			{
				//Restore taskbar visibility if hidden
				int hwnd = FindWindow("Shell_TrayWnd","");
				ShowWindow(hwnd,1);
				int hstart = FindWindowEx(GetDesktopWindow(), 0, "button", 0);
				ShowWindow(hstart,1);
			}
			base.OnClosing(e);
		}

		protected override void OnMouseMove(MouseMoveEventArgs e)
		{
			base.OnMouseMove(e);

			if (Program.Renderer.CurrentInterface == InterfaceType.Normal)
			{
				if (Program.Renderer.Touch.MoveCheck(new Vector2(e.X, e.Y), out MouseCursor.Status Status, out MouseCursor newCursor))
				{
					if (newCursor != null)
					{
						switch (Status)
						{
							case MouseCursor.Status.Default: 
								Program.Renderer.SetCursor(newCursor.MyCursor);
								break;
							case MouseCursor.Status.Plus:
								Program.Renderer.SetCursor(newCursor.MyCursorPlus);
								break;
							case MouseCursor.Status.Minus:
								Program.Renderer.SetCursor(newCursor.MyCursorMinus);
								break;
						}
					}
					else if (AvailableCursors.CurrentCursor != null)
					{
						switch (Status)
						{
							case MouseCursor.Status.Default:
								Program.Renderer.SetCursor(AvailableCursors.CurrentCursor);
								break;
							case MouseCursor.Status.Plus:
								Program.Renderer.SetCursor(AvailableCursors.CurrentCursorPlus);
								break;
							case MouseCursor.Status.Minus:
								Program.Renderer.SetCursor(AvailableCursors.CurrentCursorMinus);
								break;
						}
					}
				}
				else
				{
					Program.Renderer.SetCursor(OpenTK.MouseCursor.Default);
				}
			}
		}

		/// <summary>This method is called once the route and train data have been preprocessed, in order to physically setup the simulation</summary>
		private void SetupSimulation()
		{
			if (Loading.Cancel)
			{
				Close();
			}

			lock (BaseRenderer.GdiPlusLock)
			{
				Timetable.CreateTimetable();
			}
			//Check if any critical errors have occured during the route or train loading
			foreach (LogMessage logMessage in Interface.GetLogSnapshot())
			{
				if (logMessage.Type == MessageType.Critical)
				{
					string currentError = Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "errors", "critical_loading" });
					currentError = currentError.Replace("[error]", logMessage.Text);
					MessageBox.Show(currentError, Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "program", "title" }), MessageBoxButtons.OK, MessageBoxIcon.Hand);
					Close();
				}
			}
			Program.Renderer.Lighting.Initialize();
			Game.LogRouteName = Path.GetFileName(MainLoop.currentResult.RouteFile);
			Game.LogTrainName = Path.GetFileName(MainLoop.currentResult.TrainFolder);
			Game.LogDateTime = DateTime.Now;

			if (Interface.CurrentOptions.LoadInAdvance)
			{
				// OBNS: with the Vulkan world layer, the world's textures are loaded in advance by it, not by OpenGL: one
				// copy of each on the GPU (OBNS/VULKAN.md).
				if (OpenBve.Graphics.WorldLayer.Enabled)
				{
					OpenBve.Graphics.WorldLayer.Instance.LoadAllTextures();
				}
				else
				{
					Program.Renderer.TextureManager.LoadAllTextures();
				}
			}
			else
			{
				Program.Renderer.TextureManager.UnloadAllTextures(false);
			}
			// camera
			Program.Renderer.InitializeVisibility();
			Program.Renderer.CameraTrackFollower.UpdateAbsolute(0.0, true, false);
			Program.Renderer.CameraTrackFollower.UpdateAbsolute(-0.1, true, false);
			Program.Renderer.CameraTrackFollower.UpdateAbsolute(0.1, true, false);
			Program.Renderer.CameraTrackFollower.TriggerType = EventTriggerType.Camera;
			// starting time and track position
			Program.CurrentRoute.SecondsSinceMidnight = 0.0;
			Game.StartupTime = 0.0;
			int PlayerFirstStationIndex = Program.CurrentRoute.PlayerFirstStationIndex;
			double PlayerFirstStationPosition;
			
			{
				int s = Program.CurrentRoute.Stations[PlayerFirstStationIndex].GetStopIndex(TrainManager.PlayerTrain);
				if (s >= 0)
				{
					PlayerFirstStationPosition = Program.CurrentRoute.Stations[PlayerFirstStationIndex].Stops[s].TrackPosition;

					double TrainLength = 0.0;
					for (int c = 0; c < TrainManager.PlayerTrain.Cars.Length; c++)
					{
						TrainLength += TrainManager.PlayerTrain.Cars[c].Length;
					}

					for (int j = 0; j < Program.CurrentRoute.BufferTrackPositions.Count; j++)
					{
						if (Program.CurrentRoute.BufferTrackPositions[j].TrackIndex != 0)
						{
							// Player currently always starts on Rail0
							continue;
						}
						if (PlayerFirstStationPosition > Program.CurrentRoute.BufferTrackPositions[j].TrackPosition && PlayerFirstStationPosition - TrainLength < Program.CurrentRoute.BufferTrackPositions[j].TrackPosition)
						{
							/*
							 * HACK: The initial start position for the player train is stuck on a set of buffers
							 * This means we have to make some one the fly adjustments to the first station stop position
							 */

							//Set the start position to be the buffer position plus the train length plus 1m
							PlayerFirstStationPosition = Program.CurrentRoute.BufferTrackPositions[j].TrackPosition + TrainLength + 1;
							//Update the station stop location
							Program.CurrentRoute.Stations[PlayerFirstStationIndex].Stops[s].TrackPosition = PlayerFirstStationPosition;
							break;
						}
					}
				}
				else
				{
					PlayerFirstStationPosition = Program.CurrentRoute.Stations[PlayerFirstStationIndex].DefaultTrackPosition;
				}
				if (Program.CurrentRoute.InitialStationTime != -1)
				{
					Program.CurrentRoute.SecondsSinceMidnight = Program.CurrentRoute.InitialStationTime;
					Game.StartupTime = Program.CurrentRoute.InitialStationTime;
				}
				else
				{
					if (Program.CurrentRoute.Stations[PlayerFirstStationIndex].ArrivalTime < 0.0)
					{
						if (Program.CurrentRoute.Stations[PlayerFirstStationIndex].DepartureTime < 0.0)
						{
							Program.CurrentRoute.SecondsSinceMidnight = 0.0;
							Game.StartupTime = 0.0;
						}
						else
						{
							Program.CurrentRoute.SecondsSinceMidnight = Program.CurrentRoute.Stations[PlayerFirstStationIndex].DepartureTime -
							                            Program.CurrentRoute.Stations[PlayerFirstStationIndex].StopTime;
							Game.StartupTime = Program.CurrentRoute.Stations[PlayerFirstStationIndex].DepartureTime -
							                   Program.CurrentRoute.Stations[PlayerFirstStationIndex].StopTime;
						}
					}
					else
					{
						Program.CurrentRoute.SecondsSinceMidnight = Program.CurrentRoute.Stations[PlayerFirstStationIndex].ArrivalTime;
						Game.StartupTime = Program.CurrentRoute.Stations[PlayerFirstStationIndex].ArrivalTime;
					}
				}
			}
			int OtherFirstStationIndex = -1;
			double OtherFirstStationPosition = 0.0;
			double OtherFirstStationTime = 0.0;
			for (int i = 0; i < Program.CurrentRoute.Stations.Length; i++)
			{
				if (Program.CurrentRoute.Stations[i].StopMode == StationStopMode.AllStop | Program.CurrentRoute.Stations[i].StopMode == StationStopMode.PlayerPass & Program.CurrentRoute.Stations[i].Stops.Length != 0)
				{
					OtherFirstStationIndex = i;
					int s = Program.CurrentRoute.Stations[i].GetStopIndex(TrainManager.PlayerTrain);
					OtherFirstStationPosition = s >= 0 ? Program.CurrentRoute.Stations[i].Stops[s].TrackPosition : Program.CurrentRoute.Stations[i].DefaultTrackPosition;
					if (Program.CurrentRoute.Stations[i].ArrivalTime < 0.0)
					{
						if (Program.CurrentRoute.Stations[i].DepartureTime < 0.0)
						{
							OtherFirstStationTime = 0.0;
						}
						else
						{
							OtherFirstStationTime = Program.CurrentRoute.Stations[i].DepartureTime - Program.CurrentRoute.Stations[i].StopTime;
						}
					}
					else
					{
						OtherFirstStationTime = Program.CurrentRoute.Stations[i].ArrivalTime;
					}
					break;
				}
			}
			if (Program.CurrentRoute.PrecedingTrainTimeDeltas.Length != 0)
			{
				OtherFirstStationTime -= Program.CurrentRoute.PrecedingTrainTimeDeltas[Program.CurrentRoute.PrecedingTrainTimeDeltas.Length - 1];
				if (OtherFirstStationTime < Program.CurrentRoute.SecondsSinceMidnight)
				{
					Program.CurrentRoute.SecondsSinceMidnight = OtherFirstStationTime;
				}
			}
			// initialize trains
			for (int i = 0; i <  Program.TrainManager.Trains.Count; i++)
			{
				Program.TrainManager.Trains[i].Initialize();
				int s = Program.TrainManager.Trains[i].IsPlayerTrain ? PlayerFirstStationIndex : OtherFirstStationIndex;
				if (s >= 0)
				{
					if (Program.CurrentRoute.Stations[s].OpenLeftDoors)
					{
						for (int j = 0; j < Program.TrainManager.Trains[i].Cars.Length; j++)
						{
							Program.TrainManager.Trains[i].Cars[j].Doors[0].AnticipatedOpen = true;
						}
					}
					if (Program.CurrentRoute.Stations[s].OpenRightDoors)
					{
						for (int j = 0; j < Program.TrainManager.Trains[i].Cars.Length; j++)
						{
							Program.TrainManager.Trains[i].Cars[j].Doors[1].AnticipatedOpen = true;
						}
					}
				}
				if (Program.CurrentRoute.Sections.Length != 0)
				{
					Program.CurrentRoute.Sections[0].Enter(Program.TrainManager.Trains[i]);
				}
				for (int j = 0; j < Program.TrainManager.Trains[i].Cars.Length; j++)
				{
					double length = Program.TrainManager.Trains[i].Cars[0].Length;
					Program.TrainManager.Trains[i].Cars[j].Move(-length);
					Program.TrainManager.Trains[i].Cars[j].Move(length);
				}
			}

			// ReSharper disable once PossibleInvalidCastExceptionInForeachLoop
			foreach (ScriptedTrain Train in Program.TrainManager.TFOs)  //Must not use var, as otherwise the wrong inferred type
			{
				Train.Initialize();

				foreach (var Car in Train.Cars)
				{
					double length = Train.Cars[0].Length;
					Car.Move(-length);
					Car.Move(length);
				}
			}

			// score
			Game.CurrentScore.ArrivalStation = PlayerFirstStationIndex + 1;
			Game.CurrentScore.DepartureStation = PlayerFirstStationIndex;
			Game.CurrentScore.Maximum = 0;
			for (int i = 0; i < Program.CurrentRoute.Stations.Length; i++)
			{
				if (i != PlayerFirstStationIndex & Program.CurrentRoute.Stations[i].PlayerStops())
				{
					if (i == 0 || Program.CurrentRoute.Stations[i - 1].Type != StationType.ChangeEnds && Program.CurrentRoute.Stations[i - 1].Type != StationType.Jump)
					{
						Game.CurrentScore.Maximum += Game.ScoreValueStationArrival;
					}
				}
			}
			if (Game.CurrentScore.Maximum <= 0)
			{
				Game.CurrentScore.Maximum = Game.ScoreValueStationArrival;
			}
			// signals
			if (Program.CurrentRoute.Sections.Length > 0)
			{
				Program.CurrentRoute.UpdateAllSections();
			}
			// move train in position
			for (int i = 0; i < Program.TrainManager.Trains.Count; i++)
			{
				double p;
				if (Program.TrainManager.Trains[i].IsPlayerTrain)
				{
					p = PlayerFirstStationPosition;
				}
				else if (Program.TrainManager.Trains[i].State == TrainState.Bogus)
				{
					p = Program.CurrentRoute.BogusPreTrainInstructions[0].TrackPosition;
					Program.TrainManager.Trains[i].AI = new Game.BogusPretrainAI(Program.TrainManager.Trains[i]);
				}
				else
				{
					p = OtherFirstStationPosition;
				}
				if (TrainManager.PlayerTrain.CurrentDirection == TrackDirection.Reverse)
				{
					/*
					 * Flip the train if running in reverse direction
					 * We also need to add the length of the train so that the driver car is actually positioned on the platform
					 *
					 * Position on routes not specifically designed for reverse running may well be wrong, but that's life
					 *
					 * We should also suppress any sound events triggered by moving the train into the 'new' position
					 */
					SoundsBase.SuppressSoundEvents = true;
					Program.TrainManager.Trains[i].Reverse(true, true);
					p += Program.TrainManager.Trains[i].Length;
					if (Program.TrainManager.Trains[i].IsPlayerTrain)
					{
						// HACK!
						Program.TrainManager.Trains[i].Station = PlayerFirstStationIndex;
					}
				}

				
				for (int j = 0; j < Program.TrainManager.Trains[i].Cars.Length; j++)
				{
					Program.TrainManager.Trains[i].Cars[j].Move(p);
				}
				SoundsBase.SuppressSoundEvents = false;
			}
			// timetable
			if (Program.CurrentRoute.Information.DefaultTimetableDescription.Length == 0)
			{
				Program.CurrentRoute.Information.DefaultTimetableDescription = Game.LogTrainName;
			}

			// initialize camera
			if (Program.Renderer.Camera.CurrentRestriction == CameraRestrictionMode.NotAvailable || Program.Renderer.Camera.CurrentRestriction == CameraRestrictionMode.Restricted3D)
			{
				Program.Renderer.Camera.CurrentMode = CameraViewMode.InteriorLookAhead;
			}
			//Place the initial camera in the driver car
			TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.DriverCar].UpdateCamera();
			Program.Renderer.CameraTrackFollower.UpdateAbsolute(-1.0, true, false);
			Program.Renderer.UpdateVisibility(true);
			if (TrainManager.PlayerTrain.CurrentDirection == TrackDirection.Reverse)
			{
				double reverse = 180 / 57.2957795130824;
				Program.Renderer.Camera.SavedExterior = new CameraAlignment(new OpenBveApi.Math.Vector3(2.5, 1.5, 15), 0.3 + reverse, -0.2, 0.0, PlayerFirstStationPosition, 1.0);
				Program.Renderer.Camera.SavedTrack = new CameraAlignment(new OpenBveApi.Math.Vector3(3.0, 2.5, 0.0), 0.3 + reverse, 0.0, 0.0, TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.Cars.Length - 1].TrackPosition, 1.0);
			}
			else
			{
				Program.Renderer.Camera.SavedExterior = new CameraAlignment(new OpenBveApi.Math.Vector3(-2.5, 1.5, -15.0), 0.3, -0.2, 0.0, PlayerFirstStationPosition, 1.0);
				Program.Renderer.Camera.SavedTrack = new CameraAlignment(new OpenBveApi.Math.Vector3(-3.0, 2.5, 0.0), 0.3, 0.0, 0.0, TrainManager.PlayerTrain.Cars[0].TrackPosition - 10.0, 1.0);
			}
			Program.Renderer.Camera.SavedExterior.CameraCar = TrainManager.PlayerTrain.DriverCar; // otherwise non-default driver car jumps to Car0 when first switching to exterior

			// signalling sections
			for (int i = 0; i < Program.TrainManager.Trains.Count; i++)
			{
				int s = Program.TrainManager.Trains[i].CurrentSectionIndex;
				if (Program.CurrentRoute.Sections.Length > Program.TrainManager.Trains[i].CurrentSectionIndex)
				{
					Program.CurrentRoute.Sections[s].Enter(Program.TrainManager.Trains[i]);
				}
			}
			if (Program.CurrentRoute.Sections.Length > 0)
			{
				Program.CurrentRoute.UpdateAllSections();
			}
			// fast-forward until start time
			{
				Game.MinimalisticSimulation = true;
				const double w = 0.25;
				double u = Game.StartupTime - Program.CurrentRoute.SecondsSinceMidnight;
				if (u > 0)
				{
					while (true)
					{
						double v = u < w ? u : w; u -= v;
						Program.CurrentRoute.SecondsSinceMidnight += v;
						Program.TrainManager.UpdateTrains(v);
						if (u <= 0.0) break;
						TotalTimeElapsedForSectionUpdate += v;
						if (TotalTimeElapsedForSectionUpdate >= 1.0)
						{
							if (Program.CurrentRoute.Sections.Length > 0)
							{
								Program.CurrentRoute.UpdateAllSections();
							}
							TotalTimeElapsedForSectionUpdate = 0.0;
						}
					}
				}
				Game.MinimalisticSimulation = false;
			}
			// animated objects
			ObjectManager.UpdateAnimatedWorldObjects(0.0, true);
			Program.TrainManager.UpdateTrainObjects(0.0, true);
			//HACK: This function calls a single update on all objects attached to the player's train
			//      but ignores any specified damping so that all needles etc. are in the correct place
			//      for the first frame, rather than spinning wildly to get to the starting point.
			TrainManager.PlayerTrain.UpdateCabObjects();
			// timetable
			if (TrainManager.PlayerTrain.Station >= 0)
			{
				Timetable.UpdateCustomTimetable(Program.CurrentRoute.Stations[TrainManager.PlayerTrain.Station].TimetableDaytimeTexture, Program.CurrentRoute.Stations[TrainManager.PlayerTrain.Station].TimetableNighttimeTexture);
				if (Timetable.CustomObjectsUsed != 0 & Timetable.CustomTimetableAvailable && Interface.CurrentOptions.TimeTableStyle != TimeTableMode.AutoGenerated && Interface.CurrentOptions.TimeTableStyle != TimeTableMode.None)
				{
					Program.Renderer.CurrentTimetable = DisplayedTimetable.Custom;
				}
			}
			//Create AI driver for the player train if specified via the command line
			if (Game.InitialAIDriver)
			{
				TrainManager.PlayerTrain.AI = new Game.SimpleHumanDriverAI(TrainManager.PlayerTrain, double.PositiveInfinity);
				if (TrainManager.PlayerTrain.Plugin != null && TrainManager.PlayerTrain.Plugin.SupportsAI == AISupport.None)
				{
					MessageManager.AddMessage(Translations.GetInterfaceString(HostApplication.OpenBve, new[] {"notification","aiunable"}),MessageDependency.None, GameMode.Expert,
						MessageColor.White, 10, null);
				}
			}
			
			// warnings / errors
			List<LogMessage> logSnapshot = Interface.GetLogSnapshot();
			if (logSnapshot.Count != 0)
			{
				int filesNotFound = 0;
				int errors = 0;
				int warnings = 0;
				for (int i = 0; i < logSnapshot.Count; i++)
				{
					if (logSnapshot[i].FileNotFound)
					{
						filesNotFound++;
					}
					else if (logSnapshot[i].Type == MessageType.Error)
					{
						errors++;
					}
					else if (logSnapshot[i].Type == MessageType.Warning)
					{
						warnings++;
					}
				}
				string NotFound = null;
				string Messages;
				if (filesNotFound != 0)
				{
					NotFound = filesNotFound + " file(s) not found";
					MessageManager.AddMessage(NotFound, MessageDependency.None, GameMode.Expert, MessageColor.Magenta, 10, null);
				}
				if (errors != 0 & warnings != 0)
				{
					Messages = errors + " error(s), " + warnings + " warning(s)";
					
				}
				else if (errors != 0)
				{
					Messages = errors + " error(s)";
				}
				else
				{
					Messages = warnings + " warning(s)";
				}
				MessageManager.AddMessage(Messages, MessageDependency.None, GameMode.Expert, MessageColor.Magenta, 10, null);
				Program.CurrentRoute.Information.FilesNotFound = NotFound;
				Program.CurrentRoute.Information.ErrorsAndWarnings = Messages;
				//Print the plugin error encountered (If any) for 10s
				//This must be done after the simulation has init, as otherwise the timeout doesn't work
				if (TrainManager.PluginError != null)
				{
					MessageManager.AddMessage(TrainManager.PluginError, MessageDependency.None, GameMode.Expert, MessageColor.Red, 5, null);
					MessageManager.AddMessage(Translations.GetInterfaceString(HostApplication.OpenBve, new[] {"errors","plugin_failure2"}), MessageDependency.None, GameMode.Expert, MessageColor.Red, 5, null);
				}
			}
			loadComplete = true;
			RenderRealTimeElapsed = 0.0;
			RenderTimeElapsed = 0.0;
			World.InitializeCameraRestriction();
			Loading.SimulationSetup = true;
			Program.Renderer.CurrentInterface = InterfaceType.Normal;
			// OBNS: with the Vulkan world layer (which draws the cab too), the train's textures are preloaded by it, not
			// into OpenGL: one copy of each on the GPU (OBNS/VULKAN.md).
			if (OpenBve.Graphics.WorldLayer.Enabled)
			{
				OpenBve.Graphics.WorldLayer.Instance.PreloadTrain(TrainManager.PlayerTrain);
			}
			else
			{
				TrainManager.PlayerTrain.PreloadTextures();
			}
			if (TrainManager.PlayerTrain.CurrentDirection == TrackDirection.Reverse)
			{
				Program.Renderer.Camera.Alignment.Yaw = 180 / 57.2957795130824;
			}
			switch (Interface.CurrentOptions.InitialViewpoint)
			{
				case 0:
					if (Game.InitialReversedConsist)
					{
						/*
						 * HACK: The cab view has been created using the position of the initial driver car.
						 * Trigger a forced change to ensure that it is now correctly positioned in the consist
						 */
						TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.DriverCar].ChangeCarSection(CarSectionType.Interior);
					}
					break;
				case 1:
					//Switch camera to exterior
					MainLoop.SaveCameraSettings();
					Program.Renderer.Camera.CurrentMode = CameraViewMode.Exterior;
					MainLoop.RestoreCameraSettings();
					for (int j = 0; j < TrainManager.PlayerTrain.Cars.Length; j++)
					{
						TrainManager.PlayerTrain.Cars[j].ChangeCarSection(CarSectionType.Exterior);
					}
					Program.Renderer.Camera.AlignmentDirection = new CameraAlignment();
					Program.Renderer.Camera.AlignmentSpeed = new CameraAlignment();
					Program.Renderer.UpdateViewport(ViewportChangeMode.NoChange);
					World.UpdateAbsoluteCamera();
					Program.Renderer.UpdateViewingDistances(Program.CurrentRoute.CurrentBackground.BackgroundImageDistance);
					break;
				case 2:
					//Switch camera to track
					MainLoop.SaveCameraSettings();
					Program.Renderer.Camera.CurrentMode = CameraViewMode.Track;
					MainLoop.RestoreCameraSettings();
					for (int j = 0; j < TrainManager.PlayerTrain.Cars.Length; j++)
					{
						TrainManager.PlayerTrain.Cars[j].ChangeCarSection(CarSectionType.Exterior);
					}

					Program.Renderer.Camera.AlignmentDirection = new CameraAlignment();
					Program.Renderer.Camera.AlignmentSpeed = new CameraAlignment();
					Program.Renderer.UpdateViewport(ViewportChangeMode.NoChange);
					World.UpdateAbsoluteCamera();
					Program.Renderer.UpdateViewingDistances(Program.CurrentRoute.CurrentBackground.BackgroundImageDistance);
					break;
				case 3:
					//Switch camera to flyby
					MainLoop.SaveCameraSettings();
					Program.Renderer.Camera.CurrentMode = CameraViewMode.FlyBy;
					MainLoop.RestoreCameraSettings();
					for (int j = 0; j < TrainManager.PlayerTrain.Cars.Length; j++)
					{
						TrainManager.PlayerTrain.Cars[j].ChangeCarSection(CarSectionType.Exterior);
					}

					Program.Renderer.Camera.AlignmentDirection = new CameraAlignment();
					Program.Renderer.Camera.AlignmentSpeed = new CameraAlignment();
					Program.Renderer.UpdateViewport(ViewportChangeMode.NoChange);
					World.UpdateAbsoluteCamera();
					Program.Renderer.UpdateViewingDistances(Program.CurrentRoute.CurrentBackground.BackgroundImageDistance);
					break;
				case 4:
					//Switch camera to flyby
					MainLoop.SaveCameraSettings();
					Program.Renderer.Camera.CurrentMode = CameraViewMode.FlyByZooming;
					MainLoop.RestoreCameraSettings();
					for (int j = 0; j < TrainManager.PlayerTrain.Cars.Length; j++)
					{
						TrainManager.PlayerTrain.Cars[j].ChangeCarSection(CarSectionType.Exterior);
					}

					Program.Renderer.Camera.AlignmentDirection = new CameraAlignment();
					Program.Renderer.Camera.AlignmentSpeed = new CameraAlignment();
					Program.Renderer.UpdateViewport(ViewportChangeMode.NoChange);
					World.UpdateAbsoluteCamera();
					Program.Renderer.UpdateViewingDistances(Program.CurrentRoute.CurrentBackground.BackgroundImageDistance);
					break;
			}
			
			if (IntPtr.Size == 4)
			{
				using (Process proc = Process.GetCurrentProcess())
				{
					long memoryUsed = proc.PrivateMemorySize64;
					if ((memoryUsed > 900000000 && !Interface.CurrentOptions.LoadInAdvance) || memoryUsed > 1600000000)
					{
						// Either using ~900mb at the first station or 1.5gb + with all textures loaded is likely to cause critical OOM errors with the 32-bit process memory limit
						// Turn on UnloadUnusedTextures to try and mitigate
						Program.FileSystem.AppendToLogFile("Automatically enabling UnloadUnusedTextures due to memory pressure.");
						Interface.CurrentOptions.UnloadUnusedTextures = true;
					}
				}
			}

			simulationSetup = true;
			// OBNS: from now, textures OpenGL first needs are decoded on worker threads (TextureManager).
			LibRender2.Textures.TextureManager.ObnsAsyncDecoding = true;
			Program.FileSystem.AppendToLogFile(@"--------------------", false);
			Program.FileSystem.AppendToLogFile(@"Loading complete, starting simulation.");
			Program.FileSystem.AppendToLogFile("Texture stats @load: decodes " + Program.CurrentHost.TextureDecodeCalls + " calls / " + Program.CurrentHost.TextureDecodeMs + " ms | uploads " + LibRender2.Textures.TextureManager.UploadCount + " calls / " + LibRender2.Textures.TextureManager.UploadMs + " ms | GC gen0/1/2: " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2));
			setupTickCount = Environment.TickCount;
			Program.FileSystem.AppendToLogFile(@"--------------------", false);
			MemoryCensus.Log();
		}

		private bool simulationSetup = false;
		private int setupTickCount;
		private bool textureStatsLogged;

		/// <summary>OBNS: runs the OpenGL work the loader's threads are waiting on (BaseRenderer.RunInRenderThread).</summary>
		private static void ObnsRunRenderThreadJobs()
		{
			if (!Program.Renderer.RenderThreadJobWaiting)
			{
				return;
			}

			while (!Program.Renderer.RenderThreadJobs.IsEmpty)
			{
				Program.Renderer.RenderThreadJobs.TryDequeue(out ThreadStart currentJob);
				currentJob();
				lock (currentJob)
				{
					Monitor.Pulse(currentJob);
				}
			}

			Program.Renderer.RenderThreadJobWaiting = false;
		}

		public void LoadingScreenLoop()
		{
			Program.Renderer.PushMatrix(MatrixMode.Projection);
			Matrix4D.CreateOrthographicOffCenter(0.0f, Program.Renderer.Screen.Width, Program.Renderer.Screen.Height, 0.0f, -1.0f, 1.0f, out Program.Renderer.CurrentProjectionMatrix);
			Program.Renderer.PushMatrix(MatrixMode.Modelview);
			Program.Renderer.CurrentViewMatrix = Matrix4D.Identity;

			// OBNS: the loader's threads hand their OpenGL work (vertex arrays, preloaded textures) to this thread and wait
			// for it, so it is done on every pass, not once a frame after the buffer swap; and the swap does not wait for
			// vertical sync while loading. Where a swap stalls (Linux with the screen locked or the display off: about a
			// second each), the loading screen is drawn only every 2 s, or the whole load waited on it.
			LibRender2.Textures.TextureManager.ObnsAsyncDecoding = false;
			VSyncMode loadingVSync = VSync;
			VSync = VSyncMode.Off;
			Stopwatch sinceDrawn = Stopwatch.StartNew();
			bool drawSlowly = false;
			bool first = true;

			while (!Loading.Complete && !Loading.Cancel && !simulationSetup)
			{
				ObnsRunRenderThreadJobs();
				if (!first && sinceDrawn.ElapsedMilliseconds < (drawSlowly ? 2000 : 1000 / 60))
				{
					ProcessEvents();
					if (IsExiting)
					{
						Loading.Cancel = true;
					}

					Thread.Sleep(1);
					continue;
				}

				first = false;
				sinceDrawn.Restart();
				CPreciseTimer.GetElapsedTime();
				ProcessEvents();
				if (IsExiting)
				{
					Loading.Cancel = true;
				}
				double routeProgress = 1.0, trainProgress = 0.0;
				for (int i = 0; i < Program.CurrentHost.Plugins.Length; i++)
				{
					if (Program.CurrentHost.Plugins[i].Route != null && Program.CurrentHost.Plugins[i].Route.IsLoading)
					{
						routeProgress = Program.CurrentHost.Plugins[i].Route.CurrentProgress;
						trainProgress = 0.0;
						break;
					}

					if (Program.CurrentHost.Plugins[i].Train != null && Program.CurrentHost.Plugins[i].Train.IsLoading)
					{
						trainProgress = Program.CurrentHost.Plugins[i].Train.CurrentProgress;
					}
				}
				double trainProgressWeight = 1.0 / Program.TrainManager.Trains.Count;
				double finalTrainProgress;
				if (Program.TrainManager.Trains.Count != 0)
				{
					//Trains are not loaded until after the route
					finalTrainProgress = (Loading.LoadedTrain * trainProgressWeight) + trainProgressWeight * trainProgress;
				}
				else
				{
					finalTrainProgress = 0.0;
				}

				Program.Renderer.Loading.SetLoadingBkg(Program.CurrentRoute.Information.LoadingScreenBackground);
				Program.Renderer.Loading.DrawLoadingScreen(Program.Renderer.Fonts.SmallFont, routeProgress, finalTrainProgress);
				long swapStart = Stopwatch.GetTimestamp();
				SwapBuffers();
				double swapMilliseconds = (Stopwatch.GetTimestamp() - swapStart) * 1000.0 / Stopwatch.Frequency;
				if (swapMilliseconds > 250 != drawSlowly)
				{
					drawSlowly = swapMilliseconds > 250;
					Program.FileSystem.AppendToLogFile(drawSlowly
						? "OBNS: the loading screen's buffer swap took " + swapMilliseconds.ToString("0") + " ms (the window not shown?): drawn every 2 s while loading"
						: "OBNS: the loading screen's buffer swap is quick again: drawn every frame");
				}
			}

			VSync = loadingVSync;
			if(Loading.Cancel)
			{
				Exit();
				return;
			}
			Program.Renderer.PopMatrix(MatrixMode.Modelview);
			Program.Renderer.PopMatrix(MatrixMode.Projection);
			SetupSimulation();
		}
	}

	/// <summary>
	/// OBNS: where a frame's time goes, logged every 10 s when OBNS_FRAMESTATS=1: the animated world objects, the
	/// trains' objects, the scene's rendering, the buffer swap (the wait for the GPU and vertical sync) and the rest.
	/// For measuring the desktop backports (OBNS/README.md); off, it costs a flag test per call.
	/// </summary>
	internal static class FrameSnapshots
	{
		private static readonly double every = double.TryParse(Environment.GetEnvironmentVariable("OBNS_SNAPSHOT_EVERY"), System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? seconds : 0.0;
		private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
		private static double next = every;
		private static int count;

		/// <summary>
		/// OBNS: with OBNS_SNAPSHOT_EVERY=s, saves the frame about to be shown every s seconds, as snapshot-N.png beside the
		/// log: the game's own back buffer, read before the swap (a capture of the window misses frames, and other windows).
		/// </summary>
		internal static void Take(int width, int height)
		{
			if (every <= 0.0 || clock.Elapsed.TotalSeconds < next || width <= 0 || height <= 0)
			{
				return;
			}

			next = clock.Elapsed.TotalSeconds + every;
			count++;
			using (Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
			{
				System.Drawing.Imaging.BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
				OpenTK.Graphics.OpenGL.GL.ReadBuffer(OpenTK.Graphics.OpenGL.ReadBufferMode.Back);
				OpenTK.Graphics.OpenGL.GL.ReadPixels(0, 0, width, height, OpenTK.Graphics.OpenGL.PixelFormat.Bgra, OpenTK.Graphics.OpenGL.PixelType.UnsignedByte, data.Scan0);
				bitmap.UnlockBits(data);
				// OpenGL's rows run bottom to top; the frame is opaque.
				bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
				string file = OpenBveApi.Path.CombineFile(Program.FileSystem.SettingsFolder, "snapshot-" + count + ".png");
				using (Bitmap opaque = bitmap.Clone(new Rectangle(0, 0, width, height), System.Drawing.Imaging.PixelFormat.Format24bppRgb))
				{
					opaque.Save(file, System.Drawing.Imaging.ImageFormat.Png);
				}

				Program.FileSystem.AppendToLogFile("OBNS snapshot " + count + ": " + file);
			}
		}
	}

	internal static class FrameStats
	{
		internal enum Part { WorldObjects, TrainObjects, Render, Swap, Other }

		private static readonly bool enabled = Environment.GetEnvironmentVariable("OBNS_FRAMESTATS") == "1";
		private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
		private static readonly long[] totals = new long[5];
		private static long frameStart, lastMark, windowStart = -1, frames, worstFrame;
		// The worst frame's own parts, RenderScene laps and garbage collections, to tell what a single long frame was.
		private static readonly long[] current = new long[5], worstParts = new long[5], worstLaps = new long[7];
		private static int frameGen0, frameGen2, worstGen0, worstGen2;

		internal static void BeginFrame()
		{
			if (!enabled)
			{
				return;
			}

			long now = clock.ElapsedTicks;
			if (windowStart < 0)
			{
				windowStart = now;
			}
			else if (frameStart != 0)
			{
				if (now - frameStart > System.Diagnostics.Stopwatch.Frequency / 8)
				{
					// Each frame over 125 ms at once, to the millisecond, to line up with outside samples (e.g. nvidia-smi's).
					double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
					Program.FileSystem.AppendToLogFile(string.Format(System.Globalization.CultureInfo.InvariantCulture, "OBNS long frame at {0:HH:mm:ss.fff}: {1:0} ms (swap {2:0}, render {3:0} ms)",
						DateTime.Now, (now - frameStart) * ms, current[(int)Part.Swap] * ms, current[(int)Part.Render] * ms));
				}

				if (now - frameStart > worstFrame)
				{
					worstFrame = now - frameStart;
					Array.Copy(current, worstParts, current.Length);
					Array.Copy(NewRenderer.SceneLaps.Frame, worstLaps, worstLaps.Length);
					worstGen0 = GC.CollectionCount(0) - frameGen0;
					worstGen2 = GC.CollectionCount(2) - frameGen2;
				}

				frames++;
			}

			frameStart = now;
			lastMark = now;
			Array.Clear(current, 0, current.Length);
			frameGen0 = GC.CollectionCount(0);
			frameGen2 = GC.CollectionCount(2);
			if (now - windowStart >= 10 * System.Diagnostics.Stopwatch.Frequency && frames > 0)
			{
				double ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames;
				double span = (now - windowStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				Program.FileSystem.AppendToLogFile(string.Format(System.Globalization.CultureInfo.InvariantCulture,
					"OBNS frame stats: {0} frames, {1:0.00} ms a frame ({2:0.0} fps), worst {3:0.0} ms ({17}); world objects {4:0.00} ms, train objects {5:0.00} ms, render {6:0.00} ms ({12}), swap {7:0.00} ms, other {8:0.00} ms; parallel updates {9}; GL state filter {10}; Vulkan world layer {11}; {13}; {14}; managed heap {15:0} MB, process private {16:0} MB",
					frames, span / frames, frames * 1000.0 / span, worstFrame * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
					ms(totals[(int)Part.WorldObjects]), ms(totals[(int)Part.TrainObjects]), ms(totals[(int)Part.Render]), ms(totals[(int)Part.Swap]), ms(totals[(int)Part.Other]),
					ParallelUpdates.Enabled ? "on (" + FrameWorkers.Shared.Threads + " threads)" : "off", GLFilterSummary(), OpenBve.Graphics.WorldLayer.Summary, NewRenderer.SceneParts.Take(), GpuStats.Take(), NewRenderer.SceneLaps.Take(),
					GC.GetTotalMemory(false) / 1048576.0, ProcessPrivateMegabytes(), WorstFrame()));
				Array.Clear(totals, 0, totals.Length);
				frames = 0;
				worstFrame = 0;
				windowStart = now;
			}
		}

		/// <summary>The worst frame's parts in ms and the garbage collections during it, for the frame stats.</summary>
		private static string WorstFrame()
		{
			double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			return string.Format(System.Globalization.CultureInfo.InvariantCulture,
				"world objects {0:0.0}, train objects {1:0.0}, render {2:0.0} [{3}], swap {4:0.0}, other {5:0.0} ms; collections gen0 {6}, gen2 {7}",
				worstParts[(int)Part.WorldObjects] * ms, worstParts[(int)Part.TrainObjects] * ms, worstParts[(int)Part.Render] * ms, NewRenderer.SceneLaps.Describe(worstLaps),
				worstParts[(int)Part.Swap] * ms, worstParts[(int)Part.Other] * ms, worstGen0, worstGen2);
		}

		/// <summary>The process's private memory in MB, for the frame stats (0 where it cannot be read).</summary>
		private static double ProcessPrivateMegabytes()
		{
			try
			{
				using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
				{
					return process.PrivateMemorySize64 / 1048576.0;
				}
			}
			catch
			{
				return 0;
			}
		}

		/// <summary>The GL state filter's sets passed on and skipped a frame, since it was last asked.</summary>
		private static string GLFilterSummary()
		{
			if (!GLStateFilter.Installed)
			{
				return "off";
			}

			GLStateFilter.TakeCounts(out long passed, out long skipped);
			return string.Format(System.Globalization.CultureInfo.InvariantCulture, "on ({0:0} sets a frame passed on, {1:0} skipped)", (double)passed / frames, (double)skipped / frames);
		}

		internal static void Mark(Part part)
		{
			if (!enabled)
			{
				return;
			}

			long now = clock.ElapsedTicks;
			totals[(int)part] += now - lastMark;
			current[(int)part] += now - lastMark;
			lastMark = now;
		}
	}
}
