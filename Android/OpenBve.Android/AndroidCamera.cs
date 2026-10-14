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
using System.Collections.Generic;
using LibRender2.Camera;
using LibRender2.Cameras;
using LibRender2.Trains;
using LibRender2.Viewports;
using OpenBveApi.Graphics;
using OpenBveApi.Interface;
using OpenBveApi.Math;
using OpenBveApi.Routes;
using OpenBveApi.Runtime;
using OpenBveApi.Trains;
using RouteManager2;
using TrainManager;
using TrainManager.Car;
using TrainManager.Trains;

namespace OpenBve.Android
{
	/// <summary>
	/// The camera: the view modes (cab, cab with look-ahead, exterior, track, fly-by), moving,
	/// turning and zooming it, and placing it each frame.
	/// </summary>
	/// <remarks>
	/// A port of upstream's World.UpdateAbsoluteCamera and InitializeCameraRestriction, the camera
	/// cases of MainLoop.ProcessDigitalControl and ProcessAnalogControl, MainLoop's
	/// Save/RestoreCameraSettings, and the per-frame camera part of GameWindow.OnRenderFrame.
	/// The only omission is the animated interior/exterior mode transition (the moving between
	/// cars in exterior view is kept): it is off in upstream's options for many users, and it
	/// replaces the camera's own motion for its duration, which on a touch screen reads as lag.
	/// </remarks>
	public class AndroidCamera
	{
		/// <summary>Seconds taken to glide between cars in exterior view, upstream's default.</summary>
		private const double CarTransitionSeconds = 0.4;

		private readonly AndroidRenderer renderer;
		private readonly CurrentRoute route;
		private readonly AndroidTrainManager trainManager;

		/*
		 * Held camera commands and how hard each is held (1 for a button, a fraction for a drag).
		 * Upstream's analog controls set the alignment direction every frame while held and the
		 * main loop clears it after each frame; these are re-applied the same way.
		 */
		private readonly Dictionary<Translations.Command, double> held = new Dictionary<Translations.Command, double>();

		public AndroidCamera(AndroidRenderer renderer, CurrentRoute route, AndroidTrainManager trainManager)
		{
			this.renderer = renderer;
			this.route = route;
			this.trainManager = trainManager;
		}

		private CameraProperties Camera => renderer.Camera;

		private static TrainBase Player => TrainManagerBase.PlayerTrain;

		private CameraRestriction Restriction => Player.Cars[Player.DriverCar].CameraRestriction;

		private double BackgroundDistance => route.CurrentBackground.BackgroundImageDistance;

		/// <summary>A short name for the current view, for the overlay.</summary>
		public string ModeName
		{
			get
			{
				switch (Camera.CurrentMode)
				{
					case CameraViewMode.Interior:
						return Menu.T("notification", "interior", "Interior view");
					case CameraViewMode.InteriorLookAhead:
						return Menu.T("notification", "interior_lookahead", "Interior view (look-ahead)");
					case CameraViewMode.Exterior:
						return Menu.T("notification", "exterior", "Exterior view") + " " + (Player.CurrentDirection == TrackDirection.Reverse ? Player.Cars.Length - Player.CameraCar : Player.CameraCar + 1);
					case CameraViewMode.Track:
						return Menu.T("notification", "track", "Track view");
					case CameraViewMode.FlyBy:
						return Menu.T("notification", "flybynormal", "Fly-by view (normal)");
					default:
						return Menu.T("notification", "flybyzooming", "Fly-by view (zooming)");
				}
			}
		}

		/// <summary>
		/// The initial view and saved views, as upstream's GameWindow sets them once the train is
		/// placed. The view mode itself has already been chosen, since the cars pick their visible
		/// sections from it as they initialise.
		/// </summary>
		public void Initialize(double firstStationPosition)
		{
			if (Camera.CurrentMode == CameraViewMode.Interior &&
			    (Camera.CurrentRestriction == CameraRestrictionMode.NotAvailable || Camera.CurrentRestriction == CameraRestrictionMode.Restricted3D))
			{
				Camera.CurrentMode = CameraViewMode.InteriorLookAhead;
			}

			if (Player.CurrentDirection == TrackDirection.Reverse)
			{
				double reverse = 180 / 57.2957795130824;
				Camera.SavedExterior = new CameraAlignment(new Vector3(2.5, 1.5, 15), 0.3 + reverse, -0.2, 0.0, firstStationPosition, 1.0);
				Camera.SavedTrack = new CameraAlignment(new Vector3(3.0, 2.5, 0.0), 0.3 + reverse, 0.0, 0.0, Player.Cars[Player.Cars.Length - 1].TrackPosition, 1.0);
			}
			else
			{
				Camera.SavedExterior = new CameraAlignment(new Vector3(-2.5, 1.5, -15.0), 0.3, -0.2, 0.0, firstStationPosition, 1.0);
				Camera.SavedTrack = new CameraAlignment(new Vector3(-3.0, 2.5, 0.0), 0.3, 0.0, 0.0, Player.Cars[0].TrackPosition - 10.0, 1.0);
			}

			Camera.SavedExterior.CameraCar = Player.DriverCar;
			Camera.ModeTransitionTimer = 1.0;
			PlaceInCar(0.0);
			InitializeRestriction();
			if (Player.CurrentDirection == TrackDirection.Reverse)
			{
				Camera.Alignment.Yaw = 180 / 57.2957795130824;
			}

			UpdateAbsolute();
		}

		// --- per frame ---

		/// <summary>Places the camera for this frame. Call after the trains have moved.</summary>
		public void Update(double timeElapsed)
		{
			foreach (KeyValuePair<Translations.Command, double> control in held)
			{
				ApplyAnalog(control.Key, control.Value);
			}

			PlaceInCar(timeElapsed);

			if (Camera.CurrentRestriction == CameraRestrictionMode.NotAvailable || Camera.CurrentRestriction == CameraRestrictionMode.Restricted3D)
			{
				Player.DriverBody.Update(timeElapsed);
			}

			UpdateAbsolute(timeElapsed);

			CameraViewMode mode = Camera.CurrentMode;
			Camera.CurrentSpeed = mode == CameraViewMode.Interior | mode == CameraViewMode.InteriorLookAhead | mode == CameraViewMode.Exterior
				? Player.Cars[Player.DriverCar].CurrentSpeed
				: 0.0;
		}

		/// <summary>Called after the train objects have been placed: upstream clears the direction once a frame.</summary>
		public void EndFrame()
		{
			Camera.AlignmentDirection = new CameraAlignment();
		}

		/// <summary>In the cab and exterior views the camera rides a car; this puts it there, gliding between cars as upstream does.</summary>
		private void PlaceInCar(double timeElapsed)
		{
			CameraViewMode mode = Camera.CurrentMode;
			if (mode != CameraViewMode.Interior && mode != CameraViewMode.InteriorLookAhead && mode != CameraViewMode.Exterior)
			{
				return;
			}

			if (Camera.IsTransitioning)
			{
				Camera.CameraCarTransitionTimer += timeElapsed;
			}

			double progress = mode == CameraViewMode.Exterior ? Math.Min(1.0, Camera.CameraCarTransitionTimer / CarTransitionSeconds) : 1.0;
			if (progress >= 1.0)
			{
				Camera.IsTransitioning = false;
				if (Camera.TargetCameraCar != -1)
				{
					Camera.PreviousCameraCar = Player.CameraCar;
					Player.CameraCar = Camera.TargetCameraCar;
					Camera.TargetCameraCar = -1;
					Camera.IsTransitioning = true;
					Camera.CameraCarTransitionTimer = 0.0;
					progress = 0.0;
				}
			}

			CarBase previousCar = Camera.IsTransitioning && Camera.PreviousCameraCar != -1 && Camera.PreviousCameraCar < Player.Cars.Length
				? Player.Cars[Camera.PreviousCameraCar]
				: null;
			Player.Cars[Player.CameraCar].UpdateCamera(previousCar, progress);
		}

		// --- controls ---

		/// <summary>Whether a command is one of the camera's.</summary>
		public static bool Handles(Translations.Command command)
		{
			switch (command)
			{
				case Translations.Command.CameraInterior:
				case Translations.Command.CameraExterior:
				case Translations.Command.CameraTrack:
				case Translations.Command.CameraFlyBy:
				case Translations.Command.CameraPOIPrevious:
				case Translations.Command.CameraPOINext:
				case Translations.Command.CameraReset:
				case Translations.Command.CameraRestriction:
					return true;
				default:
					return IsAnalog(command);
			}
		}

		private static bool IsAnalog(Translations.Command command)
		{
			switch (command)
			{
				case Translations.Command.CameraMoveForward:
				case Translations.Command.CameraMoveBackward:
				case Translations.Command.CameraMoveLeft:
				case Translations.Command.CameraMoveRight:
				case Translations.Command.CameraMoveUp:
				case Translations.Command.CameraMoveDown:
				case Translations.Command.CameraRotateLeft:
				case Translations.Command.CameraRotateRight:
				case Translations.Command.CameraRotateUp:
				case Translations.Command.CameraRotateDown:
				case Translations.Command.CameraRotateCCW:
				case Translations.Command.CameraRotateCW:
				case Translations.Command.CameraZoomIn:
				case Translations.Command.CameraZoomOut:
					return true;
				default:
					return false;
			}
		}

		/// <summary>A camera command pressed, held (analog, with its strength) or released. Render thread only.</summary>
		public void Control(Translations.Command command, bool pressed, double strength = 1.0)
		{
			if (IsAnalog(command))
			{
				if (pressed && strength > 0.0)
				{
					held[command] = Math.Min(1.0, strength);
				}
				else
				{
					held.Remove(command);
				}

				return;
			}

			if (pressed)
			{
				Digital(command);
			}
		}

		/// <summary>
		/// Steps through the views: cab, outside the train, track side, fly-by, zooming fly-by, and
		/// back to the cab. The cab view's own button (upstream F1) toggles look-ahead.
		/// </summary>
		public void CycleView()
		{
			switch (Camera.CurrentMode)
			{
				case CameraViewMode.Interior:
				case CameraViewMode.InteriorLookAhead:
					Digital(Translations.Command.CameraExterior);
					break;
				case CameraViewMode.Exterior:
					Digital(Translations.Command.CameraTrack);
					break;
				case CameraViewMode.Track:
					Digital(Translations.Command.CameraFlyBy);
					break;
				case CameraViewMode.FlyBy:
					// Pressing fly-by again gives the zooming variant, as upstream.
					Digital(Translations.Command.CameraFlyBy);
					break;
				default:
					Digital(Translations.Command.CameraInterior);
					break;
			}
		}

		private void ApplyAnalog(Translations.Command command, double state)
		{
			switch (command)
			{
				case Translations.Command.CameraZoomIn:
					Camera.AlignmentDirection.Zoom = -CameraProperties.ZoomTopSpeed * state;
					break;
				case Translations.Command.CameraZoomOut:
					Camera.AlignmentDirection.Zoom = CameraProperties.ZoomTopSpeed * state;
					break;
				case Translations.Command.CameraRotateLeft:
				case Translations.Command.CameraRotateRight:
				case Translations.Command.CameraRotateUp:
				case Translations.Command.CameraRotateDown:
				case Translations.Command.CameraRotateCCW:
				case Translations.Command.CameraRotateCW:
					Camera.Rotate(command, state);
					break;
				default:
					Camera.Move(command, state);
					break;
			}
		}

		private void Digital(Translations.Command command)
		{
			TrainBase train = Player;
			switch (command)
			{
				case Translations.Command.CameraInterior:
				{
					SaveSettings();
					bool lookahead = Camera.CurrentMode != CameraViewMode.InteriorLookAhead && Camera.CurrentRestriction == CameraRestrictionMode.NotAvailable;
					bool hasCab = train.Cars[0].InteriorCamera != null || train.Cars[0].HasInteriorView;
					Camera.CurrentMode = CameraViewMode.Interior;
					RestoreSettings();
					bool returnToCab = false;
					for (int j = 0; j < train.Cars.Length; j++)
					{
						if (j == train.CameraCar && train.Cars[j].HasInteriorView)
						{
							train.Cars[j].ChangeCarSection(CarSectionType.Interior);
						}
						else
						{
							train.Cars[j].ChangeCarSection(CarSectionType.NotVisible, true);
							returnToCab |= j == train.CameraCar;
						}
					}

					if (returnToCab)
					{
						// The chosen car has no cab, so back to the driver's.
						train.CameraCar = train.DriverCar;
						train.Cars[train.DriverCar].ChangeCarSection(CarSectionType.Interior);
					}

					if (!hasCab)
					{
						Camera.AlignmentDirection = new CameraAlignment();
					}

					Camera.AlignmentSpeed = new CameraAlignment();
					renderer.UpdateViewport(ViewportChangeMode.NoChange);
					PlaceInCar(0.0);
					UpdateAbsolute();
					renderer.UpdateViewingDistances(BackgroundDistance);
					if (Camera.CurrentRestriction != CameraRestrictionMode.NotAvailable && !Camera.PerformRestrictionTest(Restriction))
					{
						InitializeRestriction();
					}

					if (lookahead)
					{
						Camera.CurrentMode = CameraViewMode.InteriorLookAhead;
					}

					break;
				}
				case Translations.Command.CameraExterior:
					SaveSettings();
					Camera.CurrentMode = CameraViewMode.Exterior;
					RestoreSettings();
					foreach (CarBase car in train.Cars)
					{
						car.ChangeCarSection(CarSectionType.Exterior);
					}

					Camera.AlignmentDirection = new CameraAlignment();
					Camera.AlignmentSpeed = new CameraAlignment();
					renderer.UpdateViewport(ViewportChangeMode.NoChange);
					PlaceInCar(0.0);
					UpdateAbsolute();
					renderer.UpdateViewingDistances(BackgroundDistance);
					break;
				case Translations.Command.CameraTrack:
				case Translations.Command.CameraFlyBy:
					SaveSettings();
					if (command == Translations.Command.CameraTrack)
					{
						Camera.CurrentMode = CameraViewMode.Track;
					}
					else
					{
						Camera.CurrentMode = Camera.CurrentMode == CameraViewMode.FlyBy ? CameraViewMode.FlyByZooming : CameraViewMode.FlyBy;
					}

					RestoreSettings();
					foreach (CarBase car in train.Cars)
					{
						car.ChangeCarSection(CarSectionType.Exterior);
					}

					Camera.AlignmentDirection = new CameraAlignment();
					Camera.AlignmentSpeed = new CameraAlignment();
					renderer.UpdateViewport(ViewportChangeMode.NoChange);
					UpdateAbsolute();
					renderer.UpdateViewingDistances(BackgroundDistance);
					break;
				case Translations.Command.CameraPOIPrevious:
				case Translations.Command.CameraPOINext:
				{
					bool next = command == Translations.Command.CameraPOINext;
					if (Camera.CurrentMode == CameraViewMode.Exterior)
					{
						// In the exterior view, these step along the train's cars instead.
						train.ChangeCameraCar(next);
						return;
					}

					if (!route.ApplyPointOfInterest(next ? TrackDirection.Forwards : TrackDirection.Reverse))
					{
						return;
					}

					UpdateAbsolute();
					if (Camera.CurrentMode < CameraViewMode.Exterior)
					{
						SaveSettings();
						Camera.CurrentMode = CameraViewMode.Track;
					}

					double z = Camera.Alignment.Position.Z;
					Camera.Alignment.Position = new Vector3(Camera.Alignment.Position.X, Camera.Alignment.Position.Y, 0.0);
					Camera.Alignment.Zoom = 0.0;
					Camera.AlignmentDirection = new CameraAlignment();
					Camera.AlignmentSpeed = new CameraAlignment();
					foreach (CarBase car in train.Cars)
					{
						car.ChangeCarSection(CarSectionType.Exterior);
					}

					renderer.CameraTrackFollower.UpdateRelative(z, true, false);
					Camera.Alignment.TrackPosition = renderer.CameraTrackFollower.TrackPosition;
					Camera.VerticalViewingAngle = Camera.OriginalVerticalViewingAngle;
					renderer.UpdateViewport(ViewportChangeMode.NoChange);
					UpdateAbsolute();
					renderer.UpdateViewingDistances(BackgroundDistance);
					break;
				}
				case Translations.Command.CameraReset:
					if (Camera.CurrentMode == CameraViewMode.Interior | Camera.CurrentMode == CameraViewMode.InteriorLookAhead)
					{
						Camera.Alignment.Position = new Vector3(0.0, 0.0, 0.0);
					}

					Camera.Alignment.Yaw = train.CurrentDirection == TrackDirection.Reverse ? 180 / 57.2957795130824 : 0;
					Camera.Alignment.Pitch = 0.0;
					Camera.Alignment.Roll = 0.0;
					if (Camera.CurrentMode == CameraViewMode.Track)
					{
						renderer.CameraTrackFollower.UpdateAbsolute(train.Cars[0].TrackPosition, true, false);
					}
					else if (Camera.CurrentMode == CameraViewMode.FlyBy | Camera.CurrentMode == CameraViewMode.FlyByZooming)
					{
						if (train.CurrentSpeed >= 0.0)
						{
							double d = 30.0 + 4.0 * train.CurrentSpeed;
							renderer.CameraTrackFollower.UpdateAbsolute(train.Cars[0].FrontAxle.Follower.TrackPosition + d, true, false);
						}
						else
						{
							double d = 30.0 - 4.0 * train.CurrentSpeed;
							renderer.CameraTrackFollower.UpdateAbsolute(train.Cars[train.Cars.Length - 1].RearAxle.Follower.TrackPosition - d, true, false);
						}
					}

					Camera.Alignment.TrackPosition = renderer.CameraTrackFollower.TrackPosition;
					Camera.Alignment.Zoom = 0.0;
					Camera.VerticalViewingAngle = Camera.OriginalVerticalViewingAngle;
					Camera.AlignmentDirection = new CameraAlignment();
					Camera.AlignmentSpeed = new CameraAlignment();
					renderer.UpdateViewport(ViewportChangeMode.NoChange);
					UpdateAbsolute();
					renderer.UpdateViewingDistances(BackgroundDistance);
					if ((Camera.CurrentMode == CameraViewMode.Interior | Camera.CurrentMode == CameraViewMode.InteriorLookAhead) &
					    (Camera.CurrentRestriction == CameraRestrictionMode.On || Camera.CurrentRestriction == CameraRestrictionMode.Restricted3D) &&
					    !Camera.PerformRestrictionTest(Restriction))
					{
						InitializeRestriction();
					}

					break;
				case Translations.Command.CameraRestriction:
					switch (Camera.CurrentRestriction)
					{
						case CameraRestrictionMode.Restricted3D:
							Camera.CurrentRestriction = CameraRestrictionMode.NotAvailable;
							break;
						case CameraRestrictionMode.NotAvailable:
							Camera.CurrentRestriction = train.Cars[train.DriverCar].CameraRestrictionMode;
							break;
						default:
							Camera.CurrentRestriction = Camera.CurrentRestriction == CameraRestrictionMode.Off
								? train.Cars[train.DriverCar].CameraRestrictionMode
								: CameraRestrictionMode.Off;
							InitializeRestriction();
							break;
					}

					break;
			}
		}

		/// <summary>Whether the camera is currently confined to the cab (for the overlay's restriction button).</summary>
		public bool Restricted => Camera.CurrentRestriction == CameraRestrictionMode.On || Camera.CurrentRestriction == CameraRestrictionMode.Restricted3D;

		// --- saved views (upstream MainLoop.SaveCameraSettings / RestoreCameraSettings) ---

		private void SaveSettings()
		{
			switch (Camera.CurrentMode)
			{
				case CameraViewMode.Interior:
				case CameraViewMode.InteriorLookAhead:
					Player.Cars[Player.CameraCar].InteriorCamera = Camera.Alignment;
					break;
				case CameraViewMode.Exterior:
					Camera.SavedExterior = Camera.Alignment;
					Camera.SavedExterior.CameraCar = Player.CameraCar;
					break;
				default:
					Camera.SavedTrack = Camera.Alignment;
					break;
			}
		}

		private void RestoreSettings()
		{
			switch (Camera.CurrentMode)
			{
				case CameraViewMode.Interior:
				case CameraViewMode.InteriorLookAhead:
					Camera.Alignment = Player.Cars[Player.CameraCar].InteriorCamera ?? Player.Cars[Player.DriverCar].InteriorCamera ?? new CameraAlignment();
					break;
				case CameraViewMode.Exterior:
					Camera.Alignment = Camera.SavedExterior;
					Player.CameraCar = Camera.SavedExterior.CameraCar;
					break;
				default:
					Camera.Alignment = Camera.SavedTrack;
					renderer.CameraTrackFollower.UpdateAbsolute(Camera.SavedTrack.TrackPosition, true, false);
					Camera.Alignment.TrackPosition = renderer.CameraTrackFollower.TrackPosition;
					break;
			}

			Camera.Alignment.Zoom = 0.0;
			Camera.VerticalViewingAngle = Camera.OriginalVerticalViewingAngle;
		}

		// --- upstream World ---

		/// <summary>Brings the camera back inside the cab's restriction box, as upstream's World.InitializeCameraRestriction.</summary>
		public void InitializeRestriction()
		{
			if (!((Camera.CurrentMode == CameraViewMode.Interior | Camera.CurrentMode == CameraViewMode.InteriorLookAhead) & Camera.CurrentRestriction == CameraRestrictionMode.On))
			{
				return;
			}

			Camera.AlignmentSpeed = new CameraAlignment();
			UpdateAbsolute();
			if (Camera.PerformRestrictionTest(Restriction))
			{
				return;
			}

			Camera.Alignment = new CameraAlignment();
			Camera.VerticalViewingAngle = Camera.OriginalVerticalViewingAngle;
			renderer.UpdateViewport(ViewportChangeMode.NoChange);
			UpdateAbsolute();
			renderer.UpdateViewingDistances(BackgroundDistance);
			if (!Camera.PerformRestrictionTest(Restriction))
			{
				Camera.Alignment.Position.Z = 0.8;
				UpdateAbsolute();
				Camera.PerformProgressiveAdjustmentForCameraRestriction(ref Camera.Alignment.Position.Z, 0.0, true, Restriction);
				if (!Camera.PerformRestrictionTest(Restriction))
				{
					Camera.Alignment.Position.X = 0.5 * (Restriction.BottomLeft.X + Restriction.TopRight.X);
					Camera.Alignment.Position.Y = 0.5 * (Restriction.BottomLeft.Y + Restriction.TopRight.Y);
					Camera.Alignment.Position.Z = 0.0;
					UpdateAbsolute();
					if (Camera.PerformRestrictionTest(Restriction))
					{
						Camera.PerformProgressiveAdjustmentForCameraRestriction(ref Camera.Alignment.Position.X, 0.0, true, Restriction);
						Camera.PerformProgressiveAdjustmentForCameraRestriction(ref Camera.Alignment.Position.Y, 0.0, true, Restriction);
					}
					else
					{
						Camera.Alignment.Position.Z = 0.8;
						UpdateAbsolute();
						Camera.PerformProgressiveAdjustmentForCameraRestriction(ref Camera.Alignment.Position.Z, 0.0, true, Restriction);
						if (!Camera.PerformRestrictionTest(Restriction))
						{
							Camera.Alignment = new CameraAlignment();
						}
					}
				}
			}

			UpdateAbsolute();
		}

		/// <summary>Computes the camera's absolute position and orientation from its alignment, as upstream's World.UpdateAbsoluteCamera.</summary>
		public void UpdateAbsolute(double timeElapsed = 0.0)
		{
			TrainBase player = Player;
			CameraRestriction restriction = Restriction;

			// zoom
			double zm = Camera.Alignment.Zoom;
			Camera.AdjustAlignment(ref Camera.Alignment.Zoom, Camera.AlignmentDirection.Zoom, ref Camera.AlignmentSpeed.Zoom, timeElapsed, true, restriction);
			if (zm != Camera.Alignment.Zoom)
			{
				Camera.ApplyZoom();
			}

			if (Camera.CurrentMode == CameraViewMode.FlyBy | Camera.CurrentMode == CameraViewMode.FlyByZooming)
			{
				UpdateFlyBy(timeElapsed, restriction);
				return;
			}

			// current alignment
			Camera.AdjustAlignment(ref Camera.Alignment.Position, Camera.AlignmentDirection.Position, ref Camera.AlignmentSpeed.Position, timeElapsed, false, restriction);
			bool interior = Camera.CurrentMode == CameraViewMode.Interior | Camera.CurrentMode == CameraViewMode.InteriorLookAhead;
			if (interior & Camera.CurrentRestriction == CameraRestrictionMode.On && Camera.Alignment.Position.Z > 0.75)
			{
				Camera.Alignment.Position.Z = 0.75;
			}

			bool q = Camera.AlignmentSpeed.Yaw != 0.0 | Camera.AlignmentSpeed.Pitch != 0.0 | Camera.AlignmentSpeed.Roll != 0.0;
			Camera.AdjustAlignment(ref Camera.Alignment.Yaw, Camera.AlignmentDirection.Yaw, ref Camera.AlignmentSpeed.Yaw, timeElapsed, false, restriction);
			Camera.AdjustAlignment(ref Camera.Alignment.Pitch, Camera.AlignmentDirection.Pitch, ref Camera.AlignmentSpeed.Pitch, timeElapsed, false, restriction);
			Camera.AdjustAlignment(ref Camera.Alignment.Roll, Camera.AlignmentDirection.Roll, ref Camera.AlignmentSpeed.Roll, timeElapsed, false, restriction);
			double tr = Camera.Alignment.TrackPosition;
			Camera.AdjustAlignment(ref Camera.Alignment.TrackPosition, Camera.AlignmentDirection.TrackPosition, ref Camera.AlignmentSpeed.TrackPosition, timeElapsed, false, restriction);
			if (tr != Camera.Alignment.TrackPosition)
			{
				renderer.CameraTrackFollower.UpdateAbsolute(Camera.Alignment.TrackPosition, true, false);
				q = true;
			}

			if (q)
			{
				renderer.UpdateViewingDistances(BackgroundDistance);
			}

			// camera
			TrackFollower follower = renderer.CameraTrackFollower;
			Vector3 cF = new Vector3(follower.WorldPosition);
			Vector3 dF = new Vector3(follower.WorldDirection);
			Vector3 uF = new Vector3(follower.WorldUp);
			Vector3 sF = new Vector3(follower.WorldSide);
			CarBase driverCar = player.Cars[player.DriverCar];
			double lookaheadYaw = 0.0;
			double lookaheadPitch = 0.0;
			if (Camera.CurrentMode == CameraViewMode.InteriorLookAhead)
			{
				// look-ahead
				double d = 20.0;
				if (player.CurrentSpeed > 0.0)
				{
					d += 3.0 * (Math.Sqrt(player.CurrentSpeed * player.CurrentSpeed + 1.0) - 1.0);
				}

				d -= driverCar.FrontAxle.Position;
				TrackFollower f = driverCar.FrontAxle.Follower.Clone();
				f.TriggerType = EventTriggerType.None;
				f.UpdateRelative(d, true, false);
				Vector3 r = new Vector3(f.WorldPosition - cF + follower.WorldSide * driverCar.Driver.X + follower.WorldUp * driverCar.Driver.Y + follower.WorldDirection * driverCar.Driver.Z);
				r.Normalize();
				double t = dF.Z * (sF.Y * uF.X - sF.X * uF.Y) + dF.Y * (-sF.Z * uF.X + sF.X * uF.Z) + dF.X * (sF.Z * uF.Y - sF.Y * uF.Z);
				if (t != 0.0)
				{
					t = 1.0 / t;
					double tx = (r.Z * (-dF.Y * uF.X + dF.X * uF.Y) + r.Y * (dF.Z * uF.X - dF.X * uF.Z) + r.X * (-dF.Z * uF.Y + dF.Y * uF.Z)) * t;
					double ty = (r.Z * (dF.Y * sF.X - dF.X * sF.Y) + r.Y * (-dF.Z * sF.X + dF.X * sF.Z) + r.X * (dF.Z * sF.Y - dF.Y * sF.Z)) * t;
					double tz = (r.Z * (sF.Y * uF.X - sF.X * uF.Y) + r.Y * (-sF.Z * uF.X + sF.X * uF.Z) + r.X * (sF.Z * uF.Y - sF.Y * uF.Z)) * t;
					lookaheadYaw = tx * tz != 0.0 ? Math.Atan2(tx, tz) : 0.0;
					lookaheadPitch = ty < -1.0 ? -0.5 * Math.PI : ty > 1.0 ? 0.5 * Math.PI : Math.Asin(ty);
				}
			}

			{
				// cab pitch and yaw
				Vector3 d2 = new Vector3(dF);
				Vector3 u2 = new Vector3(uF);
				if (interior && driverCar.CarSections.ContainsKey(CarSectionType.Interior))
				{
					d2.Rotate(sF, -driverCar.DriverPitch);
					u2.Rotate(sF, -driverCar.DriverPitch);
				}

				cF += sF * Camera.Alignment.Position.X + u2 * Camera.Alignment.Position.Y + d2 * Camera.Alignment.Position.Z;
			}

			// yaw, pitch, roll
			double headYaw = Camera.Alignment.Yaw + lookaheadYaw;
			double headPitch = Camera.Alignment.Pitch + lookaheadPitch;
			if (interior)
			{
				headYaw += driverCar.DriverYaw;
				headPitch += driverCar.DriverPitch;
			}

			double headRoll = Camera.Alignment.Roll;
			if ((Camera.CurrentRestriction == CameraRestrictionMode.NotAvailable || Camera.CurrentRestriction == CameraRestrictionMode.Restricted3D) & interior)
			{
				// with body and head
				double bodyPitch = player.DriverBody.Pitch;
				headPitch -= 0.2 * player.DriverBody.Pitch;
				double bodyRoll = player.DriverBody.Roll;
				headRoll += 0.2 * player.DriverBody.Roll;
				double shoulder = player.DriverBody.ShoulderHeight;
				double head = player.DriverBody.HeadHeight;
				// body pitch
				cF += dF * (Math.Sin(-bodyPitch) * shoulder) + uF * ((Math.Cos(-bodyPitch) - 1.0) * shoulder);
				if (bodyPitch != 0.0)
				{
					dF.Rotate(sF, -bodyPitch);
					uF.Rotate(sF, -bodyPitch);
				}

				// body roll
				cF += sF * (Math.Sin(bodyRoll) * shoulder) + uF * ((Math.Cos(bodyRoll) - 1.0) * shoulder);
				if (bodyRoll != 0.0)
				{
					uF.Rotate(dF, -bodyRoll);
					sF.Rotate(dF, -bodyRoll);
				}

				// head yaw
				cF += sF * (Math.Sin(headYaw) * head) + dF * ((Math.Cos(headYaw) - 1.0) * head);
				if (headYaw != 0.0)
				{
					dF.Rotate(uF, headYaw);
					sF.Rotate(uF, headYaw);
				}

				// head pitch
				cF += dF * (Math.Sin(-headPitch) * head) + uF * ((Math.Cos(-headPitch) - 1.0) * head);
				if (headPitch != 0.0)
				{
					dF.Rotate(sF, -headPitch);
					uF.Rotate(sF, -headPitch);
				}

				// head roll
				cF += sF * (Math.Sin(headRoll) * head) + uF * ((Math.Cos(headRoll) - 1.0) * head);
				if (headRoll != 0.0)
				{
					uF.Rotate(dF, -headRoll);
					sF.Rotate(dF, -headRoll);
				}
			}
			else
			{
				// without body or head
				if (headYaw != 0.0)
				{
					dF.Rotate(uF, headYaw);
					sF.Rotate(uF, headYaw);
				}

				if (headPitch != 0.0)
				{
					dF.Rotate(sF, -headPitch);
					uF.Rotate(sF, -headPitch);
				}

				if (headRoll != 0.0)
				{
					uF.Rotate(dF, -headRoll);
					sF.Rotate(dF, -headRoll);
				}
			}

			if (Camera.CurrentMode < CameraViewMode.Exterior &&
			    driverCar.CarSections.TryGetValue(driverCar.CurrentCarSection, out CarSection interiorSection) && interiorSection.ViewDirection != null)
			{
				dF.Rotate(interiorSection.ViewDirection);
				uF.Rotate(interiorSection.ViewDirection);
				sF.Rotate(interiorSection.ViewDirection);
			}

			Camera.AbsolutePosition = cF;
			Camera.AbsoluteDirection = dF;
			Camera.AbsoluteUp = uF;
			Camera.AbsoluteSide = sF;
		}

		/// <summary>The fly-by views: a fixed point by the track, turning to follow the nearest train.</summary>
		private void UpdateFlyBy(double timeElapsed, CameraRestriction restriction)
		{
			TrackFollower follower = renderer.CameraTrackFollower;
			Camera.AdjustAlignment(ref Camera.Alignment.Position.X, Camera.AlignmentDirection.Position.X, ref Camera.AlignmentSpeed.Position.X, timeElapsed, false, restriction);
			Camera.AdjustAlignment(ref Camera.Alignment.Position.Y, Camera.AlignmentDirection.Position.Y, ref Camera.AlignmentSpeed.Position.Y, timeElapsed, false, restriction);
			double tr = Camera.Alignment.TrackPosition;
			Camera.AdjustAlignment(ref Camera.Alignment.TrackPosition, Camera.AlignmentDirection.TrackPosition, ref Camera.AlignmentSpeed.TrackPosition, timeElapsed, false, restriction);
			if (tr != Camera.Alignment.TrackPosition)
			{
				follower.UpdateAbsolute(Camera.Alignment.TrackPosition, true, false);
				renderer.UpdateViewingDistances(BackgroundDistance);
			}

			// position to focus on
			Vector3 focusPosition = Vector3.Zero;
			double zoomMultiplier = 1.0;
			const double heightFactor = 0.75;
			TrainBase bestTrain = null;
			double bestDistanceSquared = double.MaxValue;
			TrainBase secondBestTrain = null;
			double secondBestDistanceSquared = double.MaxValue;
			foreach (TrainBase train in trainManager.Trains)
			{
				if (train.State != TrainState.Available)
				{
					continue;
				}

				Vector3 focusPos = 0.5 * (train.Cars[0].FrontAxle.Follower.WorldPosition + train.Cars[0].RearAxle.Follower.WorldPosition);
				focusPos.Y += heightFactor * train.Cars[0].Height;
				focusPos -= follower.WorldPosition;
				if (focusPos.NormSquared() < bestDistanceSquared)
				{
					secondBestTrain = bestTrain;
					secondBestDistanceSquared = bestDistanceSquared;
					bestTrain = train;
					bestDistanceSquared = focusPos.NormSquared();
				}
				else if (focusPos.NormSquared() < secondBestDistanceSquared)
				{
					secondBestTrain = train;
					secondBestDistanceSquared = focusPos.NormSquared();
				}
			}

			if (bestTrain != null)
			{
				const double maxDistance = 100.0;
				double bestDistance = Math.Sqrt(bestDistanceSquared);
				double secondBestDistance = Math.Sqrt(secondBestDistanceSquared);
				focusPosition = 0.5 * (bestTrain.Cars[0].FrontAxle.Follower.WorldPosition + bestTrain.Cars[0].RearAxle.Follower.WorldPosition);
				focusPosition.Y += heightFactor * bestTrain.Cars[0].Height;
				if (secondBestTrain != null && secondBestDistance - bestDistance <= maxDistance)
				{
					Vector3 secondBestTrainPos = 0.5 * (secondBestTrain.Cars[0].FrontAxle.Follower.WorldPosition + secondBestTrain.Cars[0].RearAxle.Follower.WorldPosition);
					secondBestTrainPos.Y += heightFactor * secondBestTrain.Cars[0].Height;
					double bt = Math.Max(0.0, 0.5 - (secondBestDistance - bestDistance) / (2.0 * maxDistance));
					bt = 2.0 * bt * bt; /* in order to change the shape of the interpolation curve */
					focusPosition = (1.0 - bt) * focusPosition + bt * secondBestTrainPos;
					zoomMultiplier = 1.0 - 2.0 * bt;
				}
			}

			// camera
			Camera.AbsoluteDirection = new Vector3(follower.WorldDirection);
			Camera.AbsolutePosition = follower.WorldPosition + follower.WorldSide * Camera.Alignment.Position.X + follower.WorldUp * Camera.Alignment.Position.Y + Camera.AbsoluteDirection * Camera.Alignment.Position.Z;
			Camera.AbsoluteDirection = focusPosition - Camera.AbsolutePosition;
			double t = Camera.AbsoluteDirection.Norm();
			Camera.AbsoluteDirection *= Camera.AbsoluteDirection.Magnitude();
			Camera.AbsoluteSide = new Vector3(Camera.AbsoluteDirection.Z, 0.0, -Camera.AbsoluteDirection.X);
			Camera.AbsoluteSide.Normalize();
			Camera.AbsoluteUp = Vector3.Cross(Camera.AbsoluteDirection, Camera.AbsoluteSide);
			renderer.UpdateViewingDistances(BackgroundDistance);
			if (Camera.CurrentMode == CameraViewMode.FlyByZooming)
			{
				const double fadeOutDistance = 600.0; /* the distance with the highest zoom factor is half the fade-out distance */
				const double maxZoomFactor = 7.0; /* the zoom factor at half the fade-out distance */
				const double factor = 256.0 / (fadeOutDistance * fadeOutDistance * fadeOutDistance * fadeOutDistance * fadeOutDistance * fadeOutDistance * fadeOutDistance * fadeOutDistance);
				double zoom = 1.0;
				if (t < fadeOutDistance)
				{
					double tdist4 = fadeOutDistance - t;
					tdist4 *= tdist4;
					tdist4 *= tdist4;
					double t4 = t * t;
					t4 *= t4;
					zoom = 1.0 + factor * zoomMultiplier * (maxZoomFactor - 1.0) * tdist4 * t4;
				}

				Camera.VerticalViewingAngle = Camera.OriginalVerticalViewingAngle / zoom;
				renderer.UpdateViewport(ViewportChangeMode.NoChange);
			}
		}
	}
}
