// OBNS: a copy of upstream's source/OpenBVE/Graphics/NewRenderer.cs (OpenBVE 1.14.1.2), laid over the desktop build's
// mirror (OBNS/build-desktop.ps1): static scenery and trains' still parts drawn in batches (OBNS/shared/StaticBatcher.cs).
// On an upstream update: copy the file again and reapply the parts marked OBNS.
using LibRender2;
using LibRender2.MotionBlurs;
using LibRender2.Objects;
using LibRender2.Overlays;
using LibRender2.Screens;
using LibRender2.Shaders;
using LibRender2.Viewports;
using OpenBve.Graphics.Renderers;
using OpenBveApi;
using OpenBveApi.Colors;
using OpenBveApi.FileSystem;
using OpenBveApi.Graphics;
using OpenBveApi.Hosts;
using OpenBveApi.Interface;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Routes;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using TrainManager.Trains;
using Vector3 = OpenBveApi.Math.Vector3;

namespace OpenBve.Graphics
{
	internal class NewRenderer : BaseRenderer, IStaticBatchHost
	{
		internal bool OptionClock = false;
		internal GradientDisplayMode OptionGradient = GradientDisplayMode.None;
		internal SpeedDisplayMode OptionSpeed = SpeedDisplayMode.None;
		internal DistanceToNextStationDisplayMode OptionDistanceToNextStation = DistanceToNextStationDisplayMode.None;
		internal bool OptionFrameRates = false;
		internal bool OptionBrakeSystems = false;
		internal bool DebugTouchMode = false;

		internal Shader pickingShader;
		private Events events;
		private Overlays overlays;
		internal Touch Touch;

		public override void Initialize()
		{
			base.Initialize();
			if (pickingShader == null)
			{
				pickingShader = new Shader(this, "default", "picking", true);
			}

			pickingShader.Activate();
			pickingShader.Deactivate();

            events = new Events(this);
			overlays = new Overlays(this);
			Touch = new Touch(this);
			ObjectsSortedByStart = new int[] { };
			ObjectsSortedByEnd = new int[] { };
			
			
			Program.FileSystem.AppendToLogFile("Renderer initialised successfully.");
		}
		
		protected override void UpdateViewport(int width, int height)
		{
			_programLogo = null;
			Screen.Width = width;
			Screen.Height = height;
			GL.Viewport(0, 0, Screen.Width, Screen.Height);

			Screen.AspectRatio = Screen.Width / (double)Screen.Height;
			Camera.HorizontalViewingAngle = 2.0 * Math.Atan(Math.Tan(0.5 * Camera.VerticalViewingAngle) * Screen.AspectRatio);

			switch (CurrentViewportMode)
			{
				case ViewportMode.Scenery:
					double cd = Program.CurrentRoute.CurrentBackground is BackgroundObject b ? Math.Max(Program.CurrentRoute.CurrentBackground.BackgroundImageDistance, b.ClipDistance) : Program.CurrentRoute.CurrentBackground.BackgroundImageDistance;
					double nearClipScenery = Math.Max(0.01, Interface.CurrentOptions.NearClipScenery);
					CurrentProjectionMatrix = Matrix4D.CreatePerspectiveFieldOfView(Camera.VerticalViewingAngle, Screen.AspectRatio, nearClipScenery, cd);
					break;
				case ViewportMode.Cab:
					double nearClipCab = Math.Max(0.01, Interface.CurrentOptions.NearClipCab);
					CurrentProjectionMatrix = Matrix4D.CreatePerspectiveFieldOfView(Camera.VerticalViewingAngle, Screen.AspectRatio, nearClipCab, 50.0);
					break;
			}

			Touch.UpdateViewport();
		}
		
		internal void RenderScene(double TimeElapsed, double RealTimeElapsed)
		{
			// OBNS: the frame's parts timed for the frame statistics (OBNS_FRAMESTATS).
			long sceneStart = System.Diagnostics.Stopwatch.GetTimestamp();
			ReleaseResources();
			// initialize
			ResetOpenGlState();

			if (OptionWireFrame)
			{
				if (Program.CurrentRoute.CurrentFog.Start < Program.CurrentRoute.CurrentFog.End)
				{
					const float fogDistance = 600.0f;
					float n = (fogDistance - Program.CurrentRoute.CurrentFog.Start) / (Program.CurrentRoute.CurrentFog.End - Program.CurrentRoute.CurrentFog.Start);
					float cr = n * inv255 * Program.CurrentRoute.CurrentFog.Color.R;
					float cg = n * inv255 * Program.CurrentRoute.CurrentFog.Color.G;
					float cb = n * inv255 * Program.CurrentRoute.CurrentFog.Color.B;
					GL.ClearColor(cr, cg, cb, 1.0f);
				}
				else
				{
					GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
				}
			}
			else
			{
				GL.ClearColor(Interface.CurrentOptions.ClearColor.R * inv255, Interface.CurrentOptions.ClearColor.G * inv255, Interface.CurrentOptions.ClearColor.B * inv255, 1.0f);
			}

			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
			
			// set up camera and lighting early for shadows
			CurrentViewMatrix = Matrix4D.LookAt(Vector3.Zero, new Vector3(Camera.AbsoluteDirection.X, Camera.AbsoluteDirection.Y, -Camera.AbsoluteDirection.Z), new Vector3(Camera.AbsoluteUp.X, Camera.AbsoluteUp.Y, -Camera.AbsoluteUp.Z));
			TransformedLightPosition = new Vector3(Lighting.OptionLightPosition.X, Lighting.OptionLightPosition.Y, -Lighting.OptionLightPosition.Z);
			TransformedLightPosition.Transform(CurrentViewMatrix);

			UpdateViewport(ViewportChangeMode.ChangeToScenery);

			PerformCSMShadowPass();

            if (Lighting.ShouldInitialize)
			{
				Lighting.Initialize();
				Lighting.ShouldInitialize = false;
			}
			
			Lighting.OptionLightingResultingAmount = (Lighting.OptionAmbientColor.R + Lighting.OptionAmbientColor.G + Lighting.OptionAmbientColor.B) / 480.0f;

			if (Lighting.OptionLightingResultingAmount > 1.0f)
			{
				Lighting.OptionLightingResultingAmount = 1.0f;
			}
			// fog
			double fd = Program.CurrentRoute.NextFog.TrackPosition - Program.CurrentRoute.PreviousFog.TrackPosition;

			if (fd != 0.0)
			{
				float fr = (float)((CameraTrackFollower.TrackPosition - Program.CurrentRoute.PreviousFog.TrackPosition) / fd);
				float frc = 1.0f - fr;
				Program.CurrentRoute.CurrentFog.Start = Program.CurrentRoute.PreviousFog.Start * frc + Program.CurrentRoute.NextFog.Start * fr;
				Program.CurrentRoute.CurrentFog.End = Program.CurrentRoute.PreviousFog.End * frc + Program.CurrentRoute.NextFog.End * fr;
				Program.CurrentRoute.CurrentFog.Color.R = (byte)(Program.CurrentRoute.PreviousFog.Color.R * frc + Program.CurrentRoute.NextFog.Color.R * fr);
				Program.CurrentRoute.CurrentFog.Color.G = (byte)(Program.CurrentRoute.PreviousFog.Color.G * frc + Program.CurrentRoute.NextFog.Color.G * fr);
				Program.CurrentRoute.CurrentFog.Color.B = (byte)(Program.CurrentRoute.PreviousFog.Color.B * frc + Program.CurrentRoute.NextFog.Color.B * fr);
				if (!Program.CurrentRoute.CurrentFog.IsLinear)
				{
					Program.CurrentRoute.CurrentFog.Density = Program.CurrentRoute.PreviousFog.Density * frc + Program.CurrentRoute.NextFog.Density * fr;
				}
			}
			else
			{
				Program.CurrentRoute.CurrentFog = Program.CurrentRoute.PreviousFog;
			}

			// OBNS: the world layer drawn by Vulkan (OBNS/VULKAN.md), unless shadows or the debug views are on (not drawn
			// by it yet): its faces are given to it now, and its frame made and submitted on another thread while OpenGL
			// draws the background below; it is laid over the background where upstream draws the world layer.
			bool vulkanWorld = Vulkan.VulkanWorld.Enabled && !ShadowsEnabled && !OptionWireFrame && !OptionNormals;
			List<FaceState> vulkanOverlayOpaque = null, vulkanOverlayAlpha = null;
			if (vulkanWorld)
			{
				StartVulkanWorld(out vulkanOverlayOpaque, out vulkanOverlayAlpha);
			}

			DefaultShader.Activate();
			BindCSMToDefaultShader();

            // render background
            // n.b. must disable shadows
            GL.Disable(EnableCap.DepthTest);
			DefaultShader.SetShadowEnabled(false);
			Program.CurrentRoute.UpdateBackground(TimeElapsed, Program.Renderer.CurrentInterface != InterfaceType.Normal);
			DefaultShader.SetShadowEnabled(ShadowsEnabled);


			// fog
			float aa = Program.CurrentRoute.CurrentFog.Start;
			float bb = Program.CurrentRoute.CurrentFog.End;

			if (aa < bb & aa < Program.CurrentRoute.CurrentBackground.BackgroundImageDistance)
			{
				Fog.Enabled = true;
				Fog.Start = aa;
				Fog.End = bb;
				Fog.Color = Program.CurrentRoute.CurrentFog.Color;
				Fog.Density = Program.CurrentRoute.CurrentFog.Density;
				Fog.IsLinear = Program.CurrentRoute.CurrentFog.IsLinear;
				Fog.Set();
			}
			else
			{
				Fog.Enabled = false;
			}

            // world layer
            // opaque face
            //Setup the shader for rendering the scene
            if (OptionLighting)
            {
	            DefaultShader.SetIsLight(true);
	            DefaultShader.SetLightPosition(TransformedLightPosition);
	            DefaultShader.SetLightAmbient(Lighting.OptionAmbientColor);
	            DefaultShader.SetLightDiffuse(Lighting.OptionDiffuseColor);
	            DefaultShader.SetLightSpecular(Lighting.OptionSpecularColor);
	            DefaultShader.SetLightModel(Lighting.LightModel);
            }
            Fog.Set();
            DefaultShader.SetTexture(0);
            DefaultShader.SetCurrentProjectionMatrix(CurrentProjectionMatrix);

            ResetOpenGlState();
			long worldStart = System.Diagnostics.Stopwatch.GetTimestamp();
			List<FaceState> overlayOpaqueFaces, overlayAlphaFaces;
			if (vulkanWorld)
			{
				// OBNS: the world layer Vulkan has drawn, laid over the background.
				overlayOpaqueFaces = vulkanOverlayOpaque;
				overlayAlphaFaces = vulkanOverlayAlpha;
				FinishVulkanWorld();
			}
			else
			{
			List<FaceState> opaqueFaces, alphaFaces, unsortedAlphaFaces;
			lock (VisibleObjects.LockObject)
			{
				opaqueFaces = VisibleObjects.OpaqueFaces.ToList();
				alphaFaces = VisibleObjects.GetSortedPolygons();
				overlayOpaqueFaces = VisibleObjects.OverlayOpaqueFaces.ToList();
				overlayAlphaFaces = VisibleObjects.GetSortedPolygons(true);
				// OBNS: the alpha faces in the order their objects were shown, for the static batcher.
				unsortedAlphaFaces = VisibleObjects.AlphaFaces.ToList();
			}

			// OBNS: faces the static batches draw come out of the lists (OBNS/shared/StaticBatcher.cs).
			batcher = batcher ?? new StaticBatcher(this);
			batcher.Filter(opaqueFaces, unsortedAlphaFaces, alphaFaces);

			foreach (FaceState face in opaqueFaces)
			{
				face.Draw();
			}

			// OBNS: the batches' opaque faces.
			batcher.DrawOpaque();

			// alpha face
			ResetOpenGlState();
			
			if (Interface.CurrentOptions.TransparencyMode == TransparencyMode.Performance)
			{
				SetBlendFunc();
				SetAlphaFunc(AlphaFunction.Greater, 0.0f);
				GL.DepthMask(false);

				foreach (FaceState face in alphaFaces)
				{
					face.Draw();
				}
			}
			else
			{
				UnsetBlendFunc();
				SetAlphaFunc(AlphaFunction.Equal, 1.0f);
				GL.DepthMask(true);

				foreach (FaceState face in alphaFaces)
				{
					if (face.Object.Prototype.Mesh.Materials[face.Face.Material].BlendMode == MeshMaterialBlendMode.Normal && face.Object.Prototype.Mesh.Materials[face.Face.Material].GlowAttenuationData == 0)
					{
						if (face.Object.Prototype.Mesh.Materials[face.Face.Material].Color.A == 255)
						{
							face.Draw();
						}
					}
				}

				// OBNS: the batches' colour-keyed faces, which are all of that kind (opaque texels, depth writes).
				batcher.DrawAlpha();

				SetBlendFunc();
				SetAlphaFunc(AlphaFunction.Less, 1.0f);
				GL.DepthMask(false);
				bool additive = false;
				// OBNS: ...and blended on top: their blended texels are edge fringes, so they go first, unsorted.
				batcher.DrawAlpha();

				foreach (FaceState face in alphaFaces)
				{
					if (face.Object.Prototype.Mesh.Materials[face.Face.Material].BlendMode == MeshMaterialBlendMode.Additive)
					{
						if (!additive)
						{
							UnsetAlphaFunc();
							additive = true;
						}
					}
					else
					{
						if (additive)
						{
							SetAlphaFunc();
							additive = false;
						}
					}
					face.Draw();
				}
			}

			}

			long worldEnd = System.Diagnostics.Stopwatch.GetTimestamp();

			// motion blur
			ResetOpenGlState();
			SetAlphaFunc(AlphaFunction.Greater, 0.0f);
			GL.Disable(EnableCap.DepthTest);
			GL.DepthMask(false);
			OptionLighting = false;

			if (Interface.CurrentOptions.MotionBlur != MotionBlurMode.None)
			{
				DefaultShader.Deactivate();
                MotionBlur.RenderFullscreen(Interface.CurrentOptions.MotionBlur, FrameRate, Math.Abs(Camera.CurrentSpeed));
			}

			// particle sources
			SetBlendFunc();
			SetAlphaFunc(AlphaFunction.Greater, 0.0f);
			if(CurrentInterface != InterfaceType.GLMainMenu)
			{
				foreach (TrainBase train in Program.TrainManager.Trains)
				{
					train.UpdateParticleSources(TimeElapsed);
				}

				// ReSharper disable once PossibleInvalidCastExceptionInForeachLoop
				foreach (ScriptedTrain scriptedTrain in Program.TrainManager.TFOs)
				{
					scriptedTrain.UpdateParticleSources(TimeElapsed);
				}
				
			}

			if (Interface.CurrentOptions.ShowEvents)
			{
				if (Math.Abs(CameraTrackFollower.TrackPosition - events.LastTrackPosition) > 50)
				{
					events.LastTrackPosition = CameraTrackFollower.TrackPosition;
					CubesToDraw.Clear();
					for (int i = 0; i < Program.CurrentRoute.Tracks.Count; i++)
					{
						int railIndex = Program.CurrentRoute.Tracks.ElementAt(i).Key;
						events.FindEvents(railIndex);
					}
				}

				for (int i = 0; i < CubesToDraw.Count; i++)
				{
					Texture T = CubesToDraw.ElementAt(i).Key;
					foreach (Vector3 v in CubesToDraw[T])
					{
						Cube.Draw(v, Camera.AbsoluteDirection, Camera.AbsoluteUp, Camera.AbsoluteSide, 0.2, Camera.AbsolutePosition, T);
					}
				}
			}

			// overlay (cab / interior) layer
			Fog.Enabled = false;
			UpdateViewport(ViewportChangeMode.ChangeToCab);

			/*
			 * We must reset the shader between overlay and world layers for correct lighting results.
			 * Additionally, the viewport change updates the projection matrix
			 */
			DefaultShader.Activate();
			ResetShader(DefaultShader);
			DefaultShader.SetCurrentProjectionMatrix(CurrentProjectionMatrix);

            CurrentViewMatrix = Matrix4D.LookAt(Vector3.Zero, new Vector3(Camera.AbsoluteDirection.X, Camera.AbsoluteDirection.Y, -Camera.AbsoluteDirection.Z), new Vector3(Camera.AbsoluteUp.X, Camera.AbsoluteUp.Y, -Camera.AbsoluteUp.Z));
			
			if (Camera.CurrentRestriction == CameraRestrictionMode.NotAvailable || Camera.CurrentRestriction == CameraRestrictionMode.Restricted3D)
			{
				ResetOpenGlState(); // TODO: inserted
				GL.Clear(ClearBufferMask.DepthBufferBit);
				OptionLighting = true;
				Color24 prevOptionAmbientColor = Lighting.OptionAmbientColor;
				Color24 prevOptionDiffuseColor = Lighting.OptionDiffuseColor;
				Lighting.OptionAmbientColor = Color24.LightGrey;
				Lighting.OptionDiffuseColor = Color24.LightGrey;

				DefaultShader.SetIsLight(true);
				TransformedLightPosition = new Vector3(Lighting.OptionLightPosition.X, Lighting.OptionLightPosition.Y, -Lighting.OptionLightPosition.Z);
				DefaultShader.SetLightPosition(TransformedLightPosition);
				DefaultShader.SetLightAmbient(Lighting.OptionAmbientColor);
				DefaultShader.SetLightDiffuse(Lighting.OptionDiffuseColor);
				DefaultShader.SetLightSpecular(Lighting.OptionSpecularColor);
				DefaultShader.SetLightModel(Lighting.LightModel);

                // overlay opaque face
                foreach (FaceState face in overlayOpaqueFaces)
				{
					face.Draw();
				}

				// overlay alpha face
				ResetOpenGlState();
				if (Interface.CurrentOptions.TransparencyMode == TransparencyMode.Performance)
				{
					SetBlendFunc();
					SetAlphaFunc(AlphaFunction.Greater, 0.0f);
					GL.DepthMask(false);

					foreach (FaceState face in overlayAlphaFaces)
					{
						face.Draw();
					}
				}
				else
				{
					UnsetBlendFunc();
					SetAlphaFunc(AlphaFunction.Equal, 1.0f);
					GL.DepthMask(true);

					foreach (FaceState face in overlayAlphaFaces)
					{
						if (face.Object.Prototype.Mesh.Materials[face.Face.Material].BlendMode == MeshMaterialBlendMode.Normal && face.Object.Prototype.Mesh.Materials[face.Face.Material].GlowAttenuationData == 0)
						{
							if (face.Object.Prototype.Mesh.Materials[face.Face.Material].Color.A == 255)
							{
								face.Draw();
							}
						}
					}

					SetBlendFunc();
					SetAlphaFunc(AlphaFunction.Less, 1.0f);
					GL.DepthMask(false);
					bool additive = false;

					foreach (FaceState face in overlayAlphaFaces)
					{
						if (face.Object.Prototype.Mesh.Materials[face.Face.Material].BlendMode == MeshMaterialBlendMode.Additive)
						{
							if (!additive)
							{
								UnsetAlphaFunc();
								additive = true;
							}
						}
						else
						{
							if (additive)
							{
								SetAlphaFunc();
								additive = false;
							}
						}
						face.Draw();
					}
				}

				Lighting.OptionAmbientColor = prevOptionAmbientColor;
				Lighting.OptionDiffuseColor = prevOptionDiffuseColor;
				Lighting.Initialize();
			}
			else
			{
				/*
                 * Render 2D Cab
                 * This is actually an animated object generated on the fly and held in memory
                 */
				ResetOpenGlState();
				OptionLighting = false;
				DefaultShader.SetIsLight(false);

                SetBlendFunc();
				UnsetAlphaFunc();
				GL.Disable(EnableCap.DepthTest);
				GL.DepthMask(false);
				foreach (FaceState face in overlayAlphaFaces)
				{
					face.Draw();
				}
			}
			// render touch
			OptionLighting = false;
			Touch.RenderScene();
			
			// render overlays
			ResetOpenGlState();
			UnsetAlphaFunc();
			SetBlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); //FIXME: Remove when text switches between two renderer types
			GL.Disable(EnableCap.DepthTest);
			overlays.Render(RealTimeElapsed);
			switch (CurrentInterface)
			{
				case InterfaceType.Menu:
				case InterfaceType.GLMainMenu:
					Game.Menu.Draw(TimeElapsed);
					break;
				case InterfaceType.SwitchChangeMap:
					Game.SwitchChangeDialog.Draw();
					break;
			}
			OptionLighting = true;
			SceneParts.Add(worldStart - sceneStart, worldEnd - worldStart, System.Diagnostics.Stopwatch.GetTimestamp() - worldEnd);
		}

		/// <summary>OBNS: RenderScene's time before the world layer (background, fog), in it, and after it (cab, overlays), for the log.</summary>
		internal static class SceneParts
		{
			private static long before, world, after, frames;

			internal static void Add(long beforeTicks, long worldTicks, long afterTicks)
			{
				before += beforeTicks;
				world += worldTicks;
				after += afterTicks;
				frames++;
			}

			internal static string Take()
			{
				double f = Math.Max(1, frames), ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				string text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "before {0:0.00}, world {1:0.00}, after {2:0.00} ms", before * ms / f, world * ms / f, after * ms / f);
				before = world = after = frames = 0;
				return text;
			}
		}

		public NewRenderer(HostInterface currentHost, BaseOptions CurrentOptions, FileSystem fileSystem) : base(currentHost, CurrentOptions, fileSystem)
		{
			// OBNS: OBNS_STATIC_BATCHES=0 and OBNS_DETACH_PARTS=0 turn off the static batches and the detached train parts.
			StaticBatcher.Enabled = Environment.GetEnvironmentVariable("OBNS_STATIC_BATCHES") != "0";
			StaticBatcher.DetachParts = Environment.GetEnvironmentVariable("OBNS_DETACH_PARTS") != "0";
		}

		/*
		 * OBNS: the route's static scenery, and each car's still parts, drawn in batches (OBNS/shared/StaticBatcher.cs,
		 * from the Android port): the objects of a stretch of track, or a car's parts that never move relative to it,
		 * merged per material into one mesh drawn with one call, instead of one call per object and material.
		 */
		private StaticBatcher batcher;

		/// <summary>OBNS: whether faces drawn now go to the Vulkan world layer (the static batcher's draws included).</summary>
		private bool vulkanGathering;

		/// <summary>OBNS: when the Vulkan world layer's frame was started, and how long that took, for the log.</summary>
		private long vulkanGatherTicks;

		/// <summary>
		/// OBNS: gives the Vulkan world layer the frame's faces, in the passes of upstream's world layer (below, in
		/// RenderScene), in the same order and with the same state, and starts its frame; returns the overlay layer's
		/// faces, taken with the world's. Called before the background is drawn, with the camera, lighting and fog set.
		/// </summary>
		private void StartVulkanWorld(out List<FaceState> overlayOpaqueFaces, out List<FaceState> overlayAlphaFaces)
		{
			long gatherStart = System.Diagnostics.Stopwatch.GetTimestamp();

			// The fog as upstream sets it after drawing the background (the same values; OpenGL's is set there).
			float fogStart = Program.CurrentRoute.CurrentFog.Start, fogEnd = Program.CurrentRoute.CurrentFog.End;
			if (fogStart < fogEnd & fogStart < Program.CurrentRoute.CurrentBackground.BackgroundImageDistance)
			{
				Fog.Enabled = true;
				Fog.Start = fogStart;
				Fog.End = fogEnd;
				Fog.Color = Program.CurrentRoute.CurrentFog.Color;
				Fog.Density = Program.CurrentRoute.CurrentFog.Density;
				Fog.IsLinear = Program.CurrentRoute.CurrentFog.IsLinear;
			}
			else
			{
				Fog.Enabled = false;
			}

			List<FaceState> opaqueFaces, alphaFaces, unsortedAlphaFaces;
			lock (VisibleObjects.LockObject)
			{
				opaqueFaces = VisibleObjects.OpaqueFaces.ToList();
				alphaFaces = VisibleObjects.GetSortedPolygons();
				overlayOpaqueFaces = VisibleObjects.OverlayOpaqueFaces.ToList();
				overlayAlphaFaces = VisibleObjects.GetSortedPolygons(true);
				unsortedAlphaFaces = VisibleObjects.AlphaFaces.ToList();
			}

			batcher = batcher ?? new StaticBatcher(this);
			batcher.Filter(opaqueFaces, unsortedAlphaFaces, alphaFaces);

			Vulkan.VulkanScene scene = Vulkan.VulkanWorld.Instance.Scene;
			scene.Begin(this);
			vulkanGathering = true;
			try
			{
				// Opaque: no blending, alpha above 0.9 (ResetOpenGlState), any order.
				scene.Pass(Vulkan.VulkanScene.Blend.None, Vulkan.VulkanScene.AlphaGreater, 0.9f, true, false);
				foreach (FaceState face in opaqueFaces)
				{
					scene.Face(face.Object, face.Face);
				}

				batcher.DrawOpaque();
				if (Interface.CurrentOptions.TransparencyMode == TransparencyMode.Performance)
				{
					scene.Pass(Vulkan.VulkanScene.Blend.Normal, Vulkan.VulkanScene.AlphaGreater, 0.0f, false, true);
					foreach (FaceState face in alphaFaces)
					{
						scene.Face(face.Object, face.Face);
					}
				}
				else
				{
					scene.Pass(Vulkan.VulkanScene.Blend.None, Vulkan.VulkanScene.AlphaEqual, 1.0f, true, false);
					foreach (FaceState face in alphaFaces)
					{
						MeshMaterial material = face.Object.Prototype.Mesh.Materials[face.Face.Material];
						if (material.BlendMode == MeshMaterialBlendMode.Normal && material.GlowAttenuationData == 0 && material.Color.A == 255)
						{
							scene.Face(face.Object, face.Face);
						}
					}

					batcher.DrawAlpha();
					scene.Pass(Vulkan.VulkanScene.Blend.Normal, Vulkan.VulkanScene.AlphaLess, 1.0f, false, true);
					batcher.DrawAlpha();
					bool additive = false;
					foreach (FaceState face in alphaFaces)
					{
						if (face.Object.Prototype.Mesh.Materials[face.Face.Material].BlendMode == MeshMaterialBlendMode.Additive)
						{
							if (!additive)
							{
								scene.SetAlphaFunction(Vulkan.VulkanScene.AlphaNever, 0.0f);
								additive = true;
							}
						}
						else if (additive)
						{
							scene.SetAlphaFunction(Vulkan.VulkanScene.AlphaLess, 1.0f);
							additive = false;
						}

						scene.Face(face.Object, face.Face);
					}
				}
			}
			finally
			{
				vulkanGathering = false;
			}

			int[] viewport = new int[4];
			GL.GetInteger(GetPName.Viewport, viewport);
			Vulkan.VulkanWorld.Instance.Start(viewport[0], viewport[1], viewport[2], viewport[3]);
			vulkanGatherTicks = System.Diagnostics.Stopwatch.GetTimestamp() - gatherStart;
		}

		/// <summary>OBNS: lays the Vulkan world layer's frame over the background, then puts back the GL state upstream's renderer expects.</summary>
		private void FinishVulkanWorld()
		{
			long finishStart = System.Diagnostics.Stopwatch.GetTimestamp();
			Vulkan.VulkanWorld.Instance.Finish();
			Vulkan.VulkanWorld.Instance.Timed(vulkanGatherTicks, System.Diagnostics.Stopwatch.GetTimestamp() - finishStart);

			// The composite used its own program, vertex array and textures.
			DefaultShader.Deactivate();
			DefaultShader.Activate();
			lastVAO = -1;
			LastBoundTexture = null;
			ResetOpenGlState();
		}

		/// <summary>OBNS: the static batcher's state, for the log.</summary>
		internal string BatchSummary => batcher?.Describe() ?? "none";

		LibRender2.BaseRenderer IStaticBatchHost.Renderer => this;

		HostInterface IStaticBatchHost.Host => Program.CurrentHost;

		bool IStaticBatchHost.QualityTransparency => Interface.CurrentOptions.TransparencyMode == TransparencyMode.Quality;

		IEnumerable<TrainBase> IStaticBatchHost.Trains()
		{
			if (Program.TrainManager?.Trains == null)
			{
				return null;
			}

			List<TrainBase> trains = new List<TrainBase>(Program.TrainManager.Trains);
			foreach (OpenBveApi.Trains.AbstractTrain train in Program.TrainManager.TFOs)
			{
				if (train is ScriptedTrain scripted)
				{
					trains.Add(scripted);
				}
			}

			return trains;
		}

		/// <summary>OBNS: vertex arrays of a length, for merged faces (RenderFace reads only their length); shared, not allocated per draw.</summary>
		private readonly Dictionary<int, MeshFaceVertex[]> placeholderVertices = new Dictionary<int, MeshFaceVertex[]>();

		MeshFaceVertex[] IStaticBatchHost.VerticesOfLength(int length)
		{
			if (!placeholderVertices.TryGetValue(length, out MeshFaceVertex[] vertices))
			{
				if (placeholderVertices.Count > 4096)
				{
					placeholderVertices.Clear();
				}

				vertices = new MeshFaceVertex[length];
				placeholderVertices[length] = vertices;
			}

			return vertices;
		}

		void IStaticBatchHost.DrawFace(Shader shader, ObjectState state, MeshFace face)
		{
			// OBNS: to the Vulkan world layer while it is gathering the frame.
			if (vulkanGathering)
			{
				Vulkan.VulkanWorld.Instance.Scene.Face(state, face);
			}
			else
			{
				RenderFace(shader, state, face);
			}
		}

		/*
		 * The texture's own type once it is uploaded; before that, its register-time decode's, which upstream's
		 * ObjectLibrary.ShowObject has classified as the object came into view. Otherwise not known yet.
		 */
		bool IStaticBatchHost.TryGetTransparencyType(Texture texture, out TextureTransparencyType type)
		{
			type = texture.Transparency;
			if (type != 0)
			{
				return true;
			}

			Texture decoded = texture.DecodedTexture;
			if (decoded != null)
			{
				type = decoded.GetTransparencyType();
				return true;
			}

			return false;
		}

		// The desktop keeps no separate CPU copy of uploaded vertices.
		void IStaticBatchHost.ReleaseVertexCopy(Mesh mesh)
		{
		}

		void IStaticBatchHost.Log(string message, bool warning)
		{
			Program.FileSystem.AppendToLogFile((warning ? "WARNING: " : string.Empty) + message);
		}
	}
}
