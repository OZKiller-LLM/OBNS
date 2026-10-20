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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LibRender2.Overlays;
using LibRender2.Screens;
using OpenBveApi;
using OpenBveApi.Hosts;
using OpenBveApi.Interface;
using OpenBveApi.Motor;
using OpenBveApi.Runtime;
using RouteManager2.Stations;
using TrainManager.Car;
using TrainManager.Car.Systems;
using TrainManager.Handles;
using TrainManager.Motor;
using TrainManager.SafetySystems;
using TrainManager.Trains;

namespace OpenBve.Android
{
	/// <summary>
	/// Carries driver commands from the UI thread, where touches arrive, to the render thread,
	/// where the simulation runs, and applies them there as upstream's input loop does.
	/// </summary>
	/// <remarks>
	/// A port of the train-control parts of upstream's MainLoop.ProcessDigitalControl: each press
	/// goes to the driver car's safety systems and traction components, then to
	/// <see cref="CabHandles.ControlDown"/>, which holds all of upstream's handle logic (single
	/// handle, air brake, hold brake), then to the safety plugin as a key if it is a security
	/// command; horns and doors are handled here as upstream handles them in the main loop.
	/// </remarks>
	public class AndroidControls
	{
		private readonly ConcurrentQueue<(Translations.Command Command, bool Pressed, double Strength, bool Exact)> pending =
			new ConcurrentQueue<(Translations.Command, bool, double, bool)>();

		private readonly ConcurrentQueue<Action<AndroidTrainSession>> actions = new ConcurrentQueue<Action<AndroidTrainSession>>();

		/// <summary>The cab's touch areas, shared by the touch overlay and the desktop view.</summary>
		public CabTouch Cab { get; } = new CabTouch();

		/// <summary>Raised on the render thread for SWITCH_MENU: the interface opens or closes its switch map (SwitchMapPanel).</summary>
		public event Action SwitchMenuRequested;

		/// <summary>Runs an action against the session on the render thread, before the next frame. Safe to call from any thread.</summary>
		public void Post(Action<AndroidTrainSession> action)
		{
			actions.Enqueue(action);
		}

		/// <summary>
		/// Holds an analog command (camera movement from a drag) at a strength from 0 to 1; zero
		/// releases it. Safe to call from any thread.
		/// </summary>
		public void Analog(Translations.Command command, double strength)
		{
			pending.Enqueue((command, strength > 0.0, strength, false));
		}

		private readonly ConcurrentQueue<(Translations.Command Command, double State)> axes = new ConcurrentQueue<(Translations.Command, double)>();

		/// <summary>
		/// Sets one of upstream's axis commands (POWER_HALFAXIS and the rest) to an axis's position:
		/// 0 to 1 for a half axis, -1 to 1 for a full one. Safe to call from any thread.
		/// </summary>
		public void Axis(Translations.Command command, double state)
		{
			axes.Enqueue((command, state));
		}

		/// <summary>Queues a press. Safe to call from any thread.</summary>
		/// <param name="exact">
		/// Whether the command is applied as given. Otherwise the on-screen levers' power and brake
		/// commands become the Single* ones on a single-handle train; the keyboard, whose bindings
		/// carry both sets as upstream's do, sends them exactly.
		/// </param>
		public void Press(Translations.Command command, bool exact = false)
		{
			pending.Enqueue((command, true, 1.0, exact));
		}

		/// <summary>Queues a release. Safe to call from any thread.</summary>
		public void Release(Translations.Command command, bool exact = false)
		{
			pending.Enqueue((command, false, 0.0, exact));
		}

		private readonly HashSet<Translations.Command> pressedThisFrame = new HashSet<Translations.Command>();
		private readonly List<(Translations.Command, bool, double, bool)> deferredReleases = new List<(Translations.Command, bool, double, bool)>();

		/// <summary>Applies everything queued since the last frame. Render thread only.</summary>
		public void Apply(AndroidTrainSession session)
		{
			TrainBase train = session.Train;
			while (actions.TryDequeue(out Action<AndroidTrainSession> action))
			{
				action(session);
			}

			/*
			 * A quick tap delivers its press and release within milliseconds, so both usually
			 * arrive in the same frame. Plugins that look at whether a key is held during Elapse
			 * (the KCR TBL plugin's start button, for one) would then never see it down, as a
			 * real keypress always spans a few frames. A release that arrives in the same frame
			 * as its own press is therefore held over to the next frame.
			 */
			List<(Translations.Command, bool, double, bool)> heldOver = null;
			pressedThisFrame.Clear();
			foreach ((Translations.Command, bool, double, bool) deferred in deferredReleases)
			{
				pending.Enqueue(deferred);
			}

			deferredReleases.Clear();
			int count = pending.Count;
			for (int n = 0; n < count && pending.TryDequeue(out (Translations.Command Command, bool Pressed, double Strength, bool Exact) item); n++)
			{
				if (item.Pressed)
				{
					pressedThisFrame.Add(item.Command);
				}
				else if (pressedThisFrame.Contains(item.Command) && !AndroidCamera.Handles(item.Command))
				{
					(heldOver ??= new List<(Translations.Command, bool, double, bool)>()).Add(item);
					continue;
				}

				// The in-game displays (timetable, route map, HUD items): acted on when pressed.
				if (IsDisplayCommand(item.Command))
				{
					if (item.Pressed)
					{
						DisplayCommand(item.Command);
					}

					continue;
				}

				// Timetable scrolling, held like an analog control (see UpdateTimetableScroll).
				if (item.Command == Translations.Command.TimetableUp || item.Command == Translations.Command.TimetableDown)
				{
					timetableScroll = item.Pressed ? (item.Command == Translations.Command.TimetableUp ? 1 : -1) : 0;
					continue;
				}

				if (IsGameCommand(item.Command))
				{
					if (item.Pressed)
					{
						GameCommand(session, item.Command);
					}

					continue;
				}

				if (item.Command == Translations.Command.SwitchMenu)
				{
					if (item.Pressed)
					{
						SwitchMenuRequested?.Invoke();
					}

					continue;
				}

				if (item.Command == Translations.Command.MiscAI)
				{
					if (item.Pressed)
					{
						ToggleAI(train);
					}

					continue;
				}

				// The camera is the viewer's, not the train's: never blocked by a plugin.
				if (AndroidCamera.Handles(item.Command))
				{
					session.Camera.Control(item.Command, item.Pressed, item.Strength);
					continue;
				}

				// Trains with the plugin's input lock engaged take no driver input, as upstream.
				if (train.Plugin != null && train.Plugin.BlockingInput)
				{
					continue;
				}

				Control control = new Control { Command = item.Exact ? item.Command : ForHandleType(train, item.Command) };
				if (item.Pressed)
				{
					ControlDown(train, control);
				}
				else
				{
					ControlUp(train, control);
				}
			}

			if (heldOver != null)
			{
				deferredReleases.AddRange(heldOver);
			}

			// Controller axes on the handles: the latest position of each, as it moved.
			if (!axes.IsEmpty)
			{
				Dictionary<Translations.Command, double> latest = new Dictionary<Translations.Command, double>();
				while (axes.TryDequeue(out (Translations.Command Command, double State) axis))
				{
					latest[axis.Command] = axis.State;
				}

				if (train.Plugin == null || !train.Plugin.BlockingInput)
				{
					foreach (KeyValuePair<Translations.Command, double> axis in latest)
					{
						HandleAxis(train, axis.Key, axis.Value);
					}
				}
			}
		}

		/*
		 * Upstream's ProcessAnalogControl for the handles (OpenBVE 1.14.1.2, MainLoop's
		 * ProcessControls.Analog.cs), where an axis sets a handle's position outright. Upstream
		 * applies an axis every frame and none at exactly 0; here it is applied when it moves,
		 * 0 included - so a lever or stick at rest leaves the touch levers and keys their say, and
		 * a trigger let go returns its handle to 0.
		 */
		private static void HandleAxis(TrainBase train, Translations.Command command, double state)
		{
			if (train.AI != null)
			{
				// As upstream: the AI driver would fight the axis.
				return;
			}

			ref CabHandles handles = ref train.Handles;
			bool allowAxisEB = Interface.CurrentOptions.AllowAxisEB;
			switch (command)
			{
				case Translations.Command.PowerHalfAxis:
				case Translations.Command.PowerFullAxis:
					if (handles.HandleType != HandleType.SingleHandle)
					{
						double a = command == Translations.Command.PowerFullAxis ? 0.5 * (state + 1.0) : state;
						handles.Power.ApplyState((int)Math.Round(a * handles.Power.MaximumNotch), false);
					}

					break;
				case Translations.Command.BrakeHalfAxis:
				case Translations.Command.BrakeFullAxis:
					if (handles.HandleType == HandleType.SingleHandle)
					{
						break;
					}

					double brake = command == Translations.Command.BrakeFullAxis ? 0.5 * (state + 1.0) : state;
					if (handles.Brake is AirBrakeHandle)
					{
						switch ((int)Math.Round(3.0 * brake))
						{
							case 0:
								handles.EmergencyBrake.Driver = false;
								handles.Brake.ApplyState(AirBrakeHandleState.Release);
								break;
							case 1:
								handles.EmergencyBrake.Driver = false;
								handles.Brake.ApplyState(AirBrakeHandleState.Lap);
								break;
							case 2:
								handles.EmergencyBrake.Driver = false;
								handles.Brake.ApplyState(AirBrakeHandleState.Service);
								break;
							case 3:
								if (allowAxisEB)
								{
									handles.EmergencyBrake.Apply();
								}

								break;
						}
					}
					else if (handles.HasHoldBrake)
					{
						int b = (int)Math.Round(brake * (handles.Brake.MaximumNotch + 2.0));
						bool hold = b == 1;
						if (b > 0)
						{
							b--;
						}

						if (b <= handles.Brake.MaximumNotch)
						{
							handles.EmergencyBrake.Release();
							handles.Brake.ApplyState(b, false);
						}
						else
						{
							handles.EmergencyBrake.Apply();
						}

						handles.HoldBrake.ApplyState(hold);
					}
					else
					{
						int b = (int)Math.Round(brake * (handles.Brake.MaximumNotch + 1.0));
						if (b <= handles.Brake.MaximumNotch)
						{
							handles.EmergencyBrake.Release();
							handles.Brake.ApplyState(b, false);
						}
						else if (allowAxisEB)
						{
							handles.EmergencyBrake.Apply();
						}
					}

					break;
				case Translations.Command.SingleFullAxis:
					if (handles.HandleType != HandleType.SingleHandle)
					{
						break;
					}

					int p = Math.Max(0, (int)Math.Round(state * handles.Power.MaximumNotch));
					int single = Math.Max(0, (int)Math.Round(-state * (handles.Brake.MaximumNotch + (handles.HasHoldBrake ? 2.0 : 1.0))));
					bool singleHold = handles.HasHoldBrake && single == 1;
					if (handles.HasHoldBrake && single > 0)
					{
						single--;
					}

					if (single <= handles.Brake.MaximumNotch)
					{
						handles.EmergencyBrake.Release();
						handles.Brake.ApplyState(single, false);
						handles.Power.ApplyState(p, false);
					}
					else if (allowAxisEB)
					{
						handles.EmergencyBrake.Apply();
					}

					if (handles.HasHoldBrake)
					{
						handles.HoldBrake.ApplyState(singleHold);
					}

					break;
				case Translations.Command.ReverserFullAxis:
					handles.Reverser.ApplyState((ReverserPosition)(int)Math.Round(state));
					break;
			}
		}

		private int timetableScroll;

		/// <summary>
		/// TIMETABLE_UP / TIMETABLE_DOWN while held, as upstream's ProcessControls.Analog: the
		/// timetable picture moves at 250 pixels a second, no further than its own height allows.
		/// Render thread, once a frame.
		/// </summary>
		public void UpdateTimetableScroll(double timeElapsed)
		{
			if (timetableScroll == 0 || timeElapsed <= 0.0)
			{
				return;
			}

			const double scrollSpeed = 250.0;
			double step = scrollSpeed * timeElapsed * timetableScroll;
			// The overlays are laid out on a virtual screen (AndroidRenderer.OverlayScale).
			double screenHeight = Program.Renderer.Screen.Height / AndroidRenderer.OverlayScale;
			switch (Program.Renderer.CurrentTimetable)
			{
				case DisplayedTimetable.Default:
				{
					double lowest = 0.0;
					if (Timetable.DefaultTimetableTexture != null)
					{
						Program.CurrentHost.LoadTexture(ref Timetable.DefaultTimetableTexture, OpenBveApi.Textures.OpenGlTextureWrapMode.ClampClamp);
						lowest = Math.Min(screenHeight - Timetable.DefaultTimetableTexture.Height, 0.0);
					}

					Timetable.DefaultTimetablePosition = Math.Max(lowest, Math.Min(0.0, Timetable.DefaultTimetablePosition + step));
					break;
				}
				case DisplayedTimetable.Custom:
				{
					OpenBveApi.Textures.Texture texture = Timetable.CurrentCustomTimetableDaytimeTexture ?? Timetable.CurrentCustomTimetableNighttimeTexture;
					double lowest = 0.0;
					if (texture != null)
					{
						Program.CurrentHost.LoadTexture(ref texture, OpenBveApi.Textures.OpenGlTextureWrapMode.ClampClamp);
						lowest = Math.Min(screenHeight - texture.Height, 0.0);
					}

					Timetable.CustomTimetablePosition = Math.Max(lowest, Math.Min(0.0, Timetable.CustomTimetablePosition + step));
					break;
				}
			}
		}

		/// <summary>MISC_AI, as upstream: the simple human driver takes over the player's train, or hands it back.</summary>
		private static void ToggleAI(TrainBase train)
		{
			if (Interface.CurrentOptions.GameMode == GameMode.Expert)
			{
				MessageManager.AddMessage(
					Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "notification", "notavailableexpert" }),
					RouteManager2.MessageManager.MessageDependency.None, GameMode.Expert, OpenBveApi.Colors.MessageColor.White, 5, null);
				return;
			}

			if (train.AI == null)
			{
				train.AI = new Game.SimpleHumanDriverAI(train, double.PositiveInfinity);
				if (train.Plugin != null && train.Plugin.SupportsAI == AISupport.None)
				{
					MessageManager.AddMessage(
						Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "notification", "aiunable" }),
						RouteManager2.MessageManager.MessageDependency.None, GameMode.Expert, OpenBveApi.Colors.MessageColor.White, 10, null);
				}
			}
			else
			{
				train.AI = null;
			}
		}

		private static bool IsGameCommand(Translations.Command command)
		{
			switch (command)
			{
				case Translations.Command.MiscTimeFactor:
				case Translations.Command.MiscMute:
				case Translations.Command.MiscBackface:
				case Translations.Command.PlayMicSounds:
				case Translations.Command.MiscInterface:
				case Translations.Command.DebugATS:
				case Translations.Command.DebugBrakeSystems:
				case Translations.Command.ShowEvents:
				case Translations.Command.DebugWireframe:
				case Translations.Command.DebugNormals:
				case Translations.Command.DebugTouchMode:
				case Translations.Command.AccessibilityCurrentSpeed:
				case Translations.Command.AccessibilityNextSignal:
				case Translations.Command.AccessibilityNextStation:
				case Translations.Command.AccessibilityNextLimit:
					return true;
				default:
					return false;
			}
		}

		/// <summary>The game's own commands, as upstream's ProcessControls handles them.</summary>
		private static void GameCommand(AndroidTrainSession session, Translations.Command command)
		{
			TrainBase train = session.Train;
			switch (command)
			{
				case Translations.Command.MiscTimeFactor:
					if (train.Plugin != null && train.Plugin.DisableTimeAcceleration)
					{
						break;
					}

					if (Interface.CurrentOptions.GameMode == GameMode.Expert)
					{
						MessageManager.AddMessage(Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "notification", "notavailableexpert" }),
							RouteManager2.MessageManager.MessageDependency.None, GameMode.Expert, OpenBveApi.Colors.MessageColor.White, 5, null);
						break;
					}

					session.TimeFactor = session.TimeFactor == 1 ? Math.Max(1, Interface.CurrentOptions.TimeAccelerationFactor) : 1;
					MainLoop.TimeFactor = session.TimeFactor;
					MessageManager.AddMessage(session.TimeFactor.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x",
						RouteManager2.MessageManager.MessageDependency.None, GameMode.Expert, OpenBveApi.Colors.MessageColor.White, 5, null);
					break;
				case Translations.Command.MiscMute:
					Program.Sounds.GlobalMute = !Program.Sounds.GlobalMute;
					break;
				case Translations.Command.PlayMicSounds:
					Program.Sounds.IsPlayingMicSounds = !Program.Sounds.IsPlayingMicSounds;
					break;
				case Translations.Command.MiscInterface:
				{
					// The interface and the developer overlay, as upstream: default, debug, none
					OpenBve.Graphics.NewRenderer renderer = Program.Renderer;
					switch (renderer.CurrentOutputMode)
					{
						case OutputMode.Default:
							renderer.CurrentOutputMode = Interface.CurrentOptions.GameMode == GameMode.Expert ? OutputMode.None : OutputMode.Debug;
							break;
						case OutputMode.Debug:
							renderer.CurrentOutputMode = OutputMode.None;
							break;
						case OutputMode.DebugATS:
							renderer.CurrentOutputMode = renderer.PreviousOutputMode;
							break;
						default:
							renderer.CurrentOutputMode = OutputMode.Default;
							break;
					}

					renderer.PreviousOutputMode = renderer.CurrentOutputMode;
					break;
				}
				case Translations.Command.DebugATS:
				{
					OpenBve.Graphics.NewRenderer renderer = Program.Renderer;
					if (renderer.CurrentOutputMode == OutputMode.DebugATS)
					{
						renderer.CurrentOutputMode = renderer.PreviousOutputMode;
					}
					else
					{
						renderer.PreviousOutputMode = renderer.CurrentOutputMode;
						renderer.CurrentOutputMode = OutputMode.DebugATS;
					}

					break;
				}
				case Translations.Command.DebugBrakeSystems:
					if (Interface.CurrentOptions.GameMode == GameMode.Expert)
					{
						MessageManager.AddMessage(Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "notification", "notavailableexpert" }),
							RouteManager2.MessageManager.MessageDependency.None, GameMode.Expert, OpenBveApi.Colors.MessageColor.White, 5, null);
					}
					else
					{
						Program.Renderer.OptionBrakeSystems = !Program.Renderer.OptionBrakeSystems;
					}

					break;
				case Translations.Command.ShowEvents:
					Interface.CurrentOptions.ShowEvents = !Interface.CurrentOptions.ShowEvents;
					break;
				case Translations.Command.DebugWireframe:
					Program.Renderer.OptionWireFrame = !Program.Renderer.OptionWireFrame;
					break;
				case Translations.Command.DebugNormals:
					Program.Renderer.OptionNormals = !Program.Renderer.OptionNormals;
					break;
				case Translations.Command.DebugTouchMode:
					Program.Renderer.DebugTouchMode = !Program.Renderer.DebugTouchMode;
					break;
				case Translations.Command.AccessibilityCurrentSpeed:
				case Translations.Command.AccessibilityNextSignal:
				case Translations.Command.AccessibilityNextStation:
				case Translations.Command.AccessibilityNextLimit:
					Announce(train, command);
					break;
				case Translations.Command.MiscBackface:
					Program.Renderer.OptionBackFaceCulling = !Program.Renderer.OptionBackFaceCulling;
					MessageManager.AddMessage(Translations.GetInterfaceString(HostApplication.OpenBve, Program.Renderer.OptionBackFaceCulling
							? new[] { "notification", "backfaceculling_on" }
							: new[] { "notification", "backfaceculling_off" }),
						RouteManager2.MessageManager.MessageDependency.None, GameMode.Expert, OpenBveApi.Colors.MessageColor.White, 2, null);
					break;
			}
		}

		/// <summary>The accessibility read-outs, as upstream: shown, and spoken while a screen reader runs (Speech).</summary>
		private static void Announce(TrainBase train, Translations.Command command)
		{
			void Say(string text)
			{
				Program.CurrentHost.AddMessage(text, RouteManager2.MessageManager.MessageDependency.AccessibilityHelper, GameMode.Normal, OpenBveApi.Colors.MessageColor.White, 10.0, null);
			}

			switch (command)
			{
				case Translations.Command.AccessibilityCurrentSpeed:
					Say(Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "message", "train_currentspeed" }).Replace("[speed]", $"{train.CurrentSpeed * 3.6:0.0}") + "km/h");
					break;
				case Translations.Command.AccessibilityNextSignal:
				{
					RouteManager2.SignalManager.Section nextSection = global::TrainManager.TrainManagerBase.CurrentRoute.NextSection(train.FrontCarTrackPosition);
					if (nextSection != null)
					{
						double distance = nextSection.TrackPosition - train.FrontCarTrackPosition;
						// Upstream 1.14.1.2 replaces [aspect] in the "m" alone (as it did [name] for stations before 1.14.1.1): here, in the text.
						Say(Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "message", "route_nextsection_aspect" }).Replace("[distance]", $"{distance:0.0}m").Replace("[aspect]", nextSection.CurrentAspect.ToString()));
					}

					break;
				}
				case Translations.Command.AccessibilityNextStation:
				{
					RouteStation nextStation = global::TrainManager.TrainManagerBase.CurrentRoute.NextStation(train.FrontCarTrackPosition);
					if (nextStation != null)
					{
						double distance = nextStation.DefaultTrackPosition - train.FrontCarTrackPosition;
						Say(Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "message", "route_nextstation" }).Replace("[distance]", $"{distance:0.0}m").Replace("[name]", nextStation.Name));
						nextStation.AccessibilityAnnounced = true;
					}

					break;
				}
				case Translations.Command.AccessibilityNextLimit:
					Say(Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "message", "route_nextlimit" }));
					break;
			}
		}

		private static bool IsDisplayCommand(Translations.Command command)
		{
			switch (command)
			{
				case Translations.Command.TimetableToggle:
				case Translations.Command.RouteInformation:
				case Translations.Command.MiscClock:
				case Translations.Command.MiscSpeed:
				case Translations.Command.MiscGradient:
				case Translations.Command.MiscDistNextStation:
				case Translations.Command.MiscFps:
					return true;
				default:
					return false;
			}
		}

		/// <summary>The display commands, as upstream's ProcessControls handles their keys.</summary>
		private static void DisplayCommand(Translations.Command command)
		{
			OpenBve.Graphics.NewRenderer renderer = Program.Renderer;
			bool expert = Interface.CurrentOptions.GameMode == GameMode.Expert;
			switch (command)
			{
				case Translations.Command.TimetableToggle:
					if (Interface.CurrentOptions.TimeTableStyle == TimeTableMode.None)
					{
						break;
					}

					if (Interface.CurrentOptions.TimeTableStyle == TimeTableMode.AutoGenerated || !Timetable.CustomTimetableAvailable)
					{
						renderer.CurrentTimetable = renderer.CurrentTimetable == DisplayedTimetable.Default ? DisplayedTimetable.None : DisplayedTimetable.Default;
					}
					else if (Interface.CurrentOptions.TimeTableStyle == TimeTableMode.PreferCustom)
					{
						renderer.CurrentTimetable = renderer.CurrentTimetable != DisplayedTimetable.Custom ? DisplayedTimetable.Custom : DisplayedTimetable.None;
					}
					else
					{
						// Custom, then the auto-generated one, then none.
						renderer.CurrentTimetable = renderer.CurrentTimetable == DisplayedTimetable.Custom ? DisplayedTimetable.Default
							: renderer.CurrentTimetable == DisplayedTimetable.Default ? DisplayedTimetable.None : DisplayedTimetable.Custom;
					}

					break;
				case Translations.Command.RouteInformation when Program.CurrentRoute.Information.RouteMap == null || Program.CurrentRoute.Information.GradientProfile == null:
					// Still being drawn (or that failed): nothing to show yet.
					break;
				case Translations.Command.RouteInformation:
					Game.RouteInfoOverlay.ProcessCommand(Translations.Command.RouteInformation);
					break;
				case Translations.Command.MiscClock:
					renderer.OptionClock = !renderer.OptionClock;
					break;
				case Translations.Command.MiscFps:
					renderer.OptionFrameRates = !renderer.OptionFrameRates;
					break;
				case Translations.Command.MiscSpeed:
				case Translations.Command.MiscGradient:
				case Translations.Command.MiscDistNextStation:
					if (expert)
					{
						MessageManager.AddMessage(Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "notification", "notavailableexpert" }),
							RouteManager2.MessageManager.MessageDependency.None, GameMode.Expert, OpenBveApi.Colors.MessageColor.White, 5, null);
					}
					else if (command == Translations.Command.MiscSpeed)
					{
						renderer.OptionSpeed = (SpeedDisplayMode)(((int)renderer.OptionSpeed + 1) % 3);
					}
					else if (command == Translations.Command.MiscGradient)
					{
						renderer.OptionGradient = (GradientDisplayMode)(((int)renderer.OptionGradient + 1) % 4);
					}
					else
					{
						renderer.OptionDistanceToNextStation = (DistanceToNextStationDisplayMode)(((int)renderer.OptionDistanceToNextStation + 1) % 3);
					}

					break;
			}
		}

		/*
		 * The on-screen levers are always power and brake. A single-handle train has one lever for
		 * both, which upstream drives with the Single* commands: towards power (releasing any brake
		 * first) or towards brake (shutting off power first).
		 */
		private static Translations.Command ForHandleType(TrainBase train, Translations.Command command)
		{
			if (train.Handles.HandleType != HandleType.SingleHandle)
			{
				return command;
			}

			switch (command)
			{
				case Translations.Command.PowerIncrease:
				case Translations.Command.BrakeDecrease:
					return Translations.Command.SinglePower;
				case Translations.Command.PowerDecrease:
				case Translations.Command.BrakeIncrease:
					return Translations.Command.SingleBrake;
				default:
					return command;
			}
		}

		private static void ControlDown(TrainBase train, Control control)
		{
			CarBase driverCar = train.Cars[train.DriverCar];
			foreach (SafetySystem system in driverCar.SafetySystems.Keys.ToArray())
			{
				driverCar.SafetySystems[system].ControlDown(control.Command);
			}

			foreach (EngineComponent component in driverCar.TractionModel.Components.Keys.ToArray())
			{
				driverCar.TractionModel.Components[component].ControlDown(control.Command);
			}

			train.Handles.ControlDown(control);

			if (Translations.SecurityToVirtualKey(control.Command, out VirtualKeys key))
			{
				train.Plugin?.KeyDown(key);
			}

			switch (control.Command)
			{
				case Translations.Command.HornPrimary:
				case Translations.Command.HornSecondary:
				case Translations.Command.HornMusic:
				{
					int j = control.Command == Translations.Command.HornPrimary ? 0 : control.Command == Translations.Command.HornSecondary ? 1 : 2;
					if (driverCar.Horns.Length > j)
					{
						driverCar.Horns[j].Play();
						if (j == 1 && train.Specs.HornTriggersBell && driverCar.Horns.Length > 3)
						{
							// As upstream 1.14.1.1: MSTS lets the horn also trigger the bell (US trains)
							driverCar.Horns[3].Play();
						}

						train.Plugin?.HornBlow(j == 0 ? HornTypes.Primary : j == 1 ? HornTypes.Secondary : HornTypes.Music);
					}

					break;
				}
				case Translations.Command.DoorsLeft:
					ToggleDoors(train, 0);
					break;
				case Translations.Command.DoorsRight:
					ToggleDoors(train, 1);
					break;
				case Translations.Command.DeviceConstSpeed:
					if (train.Specs.HasConstSpeed)
					{
						train.Specs.CurrentConstSpeed = !train.Specs.CurrentConstSpeed;
					}

					break;
				case Translations.Command.RaisePantograph:
				case Translations.Command.LowerPantograph:
					foreach (CarBase car in train.Cars)
					{
						if (car.TractionModel.Components.TryGetTypedValue(EngineComponent.Pantograph, out Pantograph pantograph))
						{
							if (control.Command == Translations.Command.RaisePantograph)
							{
								pantograph.Raise();
							}
							else
							{
								pantograph.Lower();
							}
						}
					}

					break;
				case Translations.Command.GearUp:
				case Translations.Command.GearDown:
					foreach (CarBase car in train.Cars)
					{
						if (car.TractionModel.Components.TryGetTypedValue(EngineComponent.Gearbox, out Gearbox gearbox))
						{
							if (gearbox.OperationMode == GearboxOperation.Automatic)
							{
								break;
							}

							if (control.Command == Translations.Command.GearUp)
							{
								gearbox.GearUp();
							}
							else
							{
								gearbox.GearDown();
							}
						}
					}

					break;
				case Translations.Command.Headlights:
					train.SafetySystems.Headlights.ChangeState();
					break;
				case Translations.Command.MainBreaker:
					// Upstream 1.14.1.2 has nothing here either: the breaker is the plugin's or the train's.
					break;
				case Translations.Command.Sanders:
					SetSanders(train, true);
					break;
				case Translations.Command.UncoupleFront:
				case Translations.Command.UncoupleRear:
					Uncouple(train, control.Command == Translations.Command.UncoupleFront);
					break;
				case Translations.Command.AccessPermissiveSection:
					train.ContactSignaller();
					break;
			}
		}

		/// <summary>SANDERS pressed or released, as upstream: press-and-hold sanders run while held, the others toggle on each press and release.</summary>
		private static void SetSanders(TrainBase train, bool pressed)
		{
			foreach (CarBase car in train.Cars)
			{
				if (car.ReAdhesionDevice is Sanders sanders)
				{
					if (sanders.Type == SandersType.PressAndHold)
					{
						sanders.SetActive(pressed);
					}
					else
					{
						sanders.Toggle();
					}
				}
			}
		}

		/// <summary>UNCOUPLE_FRONT / UNCOUPLE_REAR, as upstream: the camera car's coupler, from the exterior view.</summary>
		private static void Uncouple(TrainBase train, bool front)
		{
			void Say(string key, string suffix = "")
			{
				MessageManager.AddMessage(Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "notification", key }) + suffix,
					RouteManager2.MessageManager.MessageDependency.None, GameMode.Expert, OpenBveApi.Colors.MessageColor.White, 5, null);
			}

			if (Program.Renderer.Camera.CurrentMode != OpenBveApi.Runtime.CameraViewMode.Exterior)
			{
				Say("switchexterior_uncouple");
				return;
			}

			int car = train.CameraCar;
			if (front ? car == 0 : car == train.Cars.Length - 1)
			{
				Say("unable_uncouple");
				return;
			}

			// The front coupler is the one on the rear of the car ahead.
			int coupler = front ? car - 1 : car;
			if (coupler >= train.Cars.Length || !train.Cars[coupler].Coupler.CanUncouple)
			{
				Say("fixed_uncouple");
				return;
			}

			Say(front ? "exterior_uncouplefront" : "exterior_uncouplerear", " " + (car + 1));
			train.Cars[car].Uncouple(front, !front);
		}

		private static void ControlUp(TrainBase train, Control control)
		{
			CarBase driverCar = train.Cars[train.DriverCar];
			foreach (SafetySystem system in driverCar.SafetySystems.Keys.ToArray())
			{
				driverCar.SafetySystems[system].ControlUp(control.Command);
			}

			foreach (EngineComponent component in driverCar.TractionModel.Components.Keys.ToArray())
			{
				driverCar.TractionModel.Components[component].ControlUp(control.Command);
			}

			train.Handles.ControlUp(control);

			if (Translations.SecurityToVirtualKey(control.Command, out VirtualKeys key))
			{
				train.Plugin?.KeyUp(key);
			}

			switch (control.Command)
			{
				case Translations.Command.HornPrimary:
				case Translations.Command.HornSecondary:
				case Translations.Command.HornMusic:
				{
					/*
					 * Every horn, as upstream: releasing is what resets Horn.LoopStarted, which Play
					 * sets. Without it a play-once horn sounds once and then never again, and a
					 * three-part horn never plays its end sound.
					 */
					int j = control.Command == Translations.Command.HornPrimary ? 0 : control.Command == Translations.Command.HornSecondary ? 1 : 2;
					if (driverCar.Horns.Length > j)
					{
						driverCar.Horns[j].Stop();
					}

					break;
				}
				case Translations.Command.DoorsLeft:
					driverCar.Doors[0].ButtonPressed = false;
					break;
				case Translations.Command.DoorsRight:
					driverCar.Doors[1].ButtonPressed = false;
					break;
				case Translations.Command.Sanders:
					SetSanders(train, false);
					break;
			}
		}

		/// <summary>Opens the doors on one side if they are shut, otherwise closes them.</summary>
		private static void ToggleDoors(TrainBase train, int side)
		{
			CarBase driverCar = train.Cars[train.DriverCar];
			if (driverCar.Doors[side].ButtonPressed)
			{
				return;
			}

			bool left = side == 0;
			if ((train.GetDoorsState(left, !left) & TrainDoorState.Opened) == 0)
			{
				if (train.Specs.DoorOpenMode != DoorMode.Automatic)
				{
					train.OpenDoors(left, !left);
				}
			}
			else if (train.Specs.DoorCloseMode != DoorMode.Automatic)
			{
				train.CloseDoors(left, !left);
			}

			driverCar.Doors[side].ButtonPressed = true;
		}
	}
}
