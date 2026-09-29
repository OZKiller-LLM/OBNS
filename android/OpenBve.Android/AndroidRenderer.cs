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

using System.Collections.Generic;
using System.Linq;
using LibRender2;
using LibRender2.Objects;
using LibRender2.Viewports;
using OpenBveApi;
using OpenBveApi.Hosts;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Routes;
using OpenBveApi.Graphics;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Android
{
	/// <summary>
	/// The concrete renderer for the Android front end.
	/// </summary>
	/// <remarks>
	/// <see cref="BaseRenderer"/> carries the rendering implementation. Upstream's desktop
	/// subclass (OpenBVE/Graphics/NewRenderer.cs) adds the per-frame scene pass, which lives in
	/// the Windows Forms project and leans on Program statics, so it is re-expressed here.
	///
	/// This is deliberately the world layer only, in the same order upstream draws it: camera
	/// and light transforms, the scenery viewport, the background, fog, opaque faces, then alpha
	/// faces. Not yet ported: shadow cascades, motion blur, particles, event markers, and the cab
	/// overlay layer.
	/// </remarks>
	public class AndroidRenderer : OpenBve.Graphics.NewRenderer
	{
		/// <summary>The driver's eye height above the rail, in metres.</summary>
		public const double EyeHeight = 2.5;

		/// <summary>Creates the renderer.</summary>
		public AndroidRenderer(HostInterface host, BaseOptions options, OpenBveApi.FileSystem.FileSystem fileSystem)
			: base(host, options, fileSystem)
		{
			this.options = options;
		}

		private readonly BaseOptions options;

		/// <summary>
		/// Prepares the camera and visibility structures once a route has been built, mirroring
		/// upstream's Program startup (field of view) and loading sequence (visibility).
		/// </summary>
		public void PrepareScene(HostInterface host, double viewingDistance)
		{
			Camera.VerticalViewingAngle = 45.0.ToRadians();
			Camera.HorizontalViewingAngle = 2.0 * System.Math.Atan(System.Math.Tan(0.5 * Camera.VerticalViewingAngle) * Screen.AspectRatio);
			Camera.OriginalVerticalViewingAngle = Camera.VerticalViewingAngle;
			Camera.ForwardViewingDistance = viewingDistance;
			Camera.BackwardViewingDistance = 0.0;

			CameraTrackFollower = new TrackFollower(host);
			PlaceCamera(0.0);

			// Builds the VAOs for every object the route created, then shows the ones in range.
			InitializeVisibility();
			UpdateVisibility(true);
		}

		/// <summary>
		/// Places the camera at the given track position, looking along the track from the
		/// driver's eye height, and asks the visibility thread to catch up.
		/// </summary>
		/// <remarks>
		/// Upstream derives the camera from the player train's driver car. With no train yet, a
		/// track follower stands in for it.
		/// </remarks>
		public void PlaceCamera(double trackPosition)
		{
			CameraTrackFollower.UpdateAbsolute(trackPosition, true, false);

			Vector3 up = CameraTrackFollower.WorldUp;
			Camera.AbsolutePosition = CameraTrackFollower.WorldPosition + up * EyeHeight;
			Camera.AbsoluteDirection = CameraTrackFollower.WorldDirection;
			Camera.AbsoluteUp = up;
			Camera.AbsoluteSide = CameraTrackFollower.WorldSide;

			UpdateVisibility(false);
		}

		private static readonly System.Collections.Generic.HashSet<string> reportedErrors = new System.Collections.Generic.HashSet<string>();

		/// <summary>
		/// Turns on the per-frame GL error checkpoints. Off by default: glGetError is a synchronous
		/// round trip, which is exactly the kind of call that stalls a mobile GPU pipeline.
		/// </summary>
		public static bool DiagnoseGlErrors;

		/// <summary>The projection, view and camera translation the cab was last drawn with.</summary>
		public Matrix4D CabProjection, CabView, CabCameraTranslation;

		/// <summary>Whether the cab matrices have been set.</summary>
		public bool CabMatricesValid;

		/// <summary>How much upstream's overlays (HUD, messages, timetable, route map) are enlarged; see RenderScene.</summary>
		public static double OverlayScale { get; set; } = 1.0;

		/// <summary>
		/// Diagnostic: reports a pending GL error, once per location. GL errors are sticky until
		/// read, so LibRender2's own debug check only says that *something* earlier failed;
		/// checkpoints narrow down what.
		/// </summary>
		private static void GlCheck(string where)
		{
			if (!DiagnoseGlErrors)
			{
				return;
			}

			ErrorCode error = GL.GetError();
			if (error != ErrorCode.NoError && reportedErrors.Add(where + error))
			{
				global::Android.Util.Log.Warn("OpenBVE", "GL error " + error + " at: " + where);
			}
		}

		/// <summary>The route being drawn. Supplies the background and fog.</summary>
		public RouteManager2.CurrentRoute Route { get; set; }

		/// <summary>
		/// Interpolates fog between the fog changes either side of the camera, exactly as
		/// upstream does, so fog fades in and out along the route instead of switching.
		/// </summary>
		private void UpdateFog()
		{
			double span = Route.NextFog.TrackPosition - Route.PreviousFog.TrackPosition;
			if (span != 0.0)
			{
				float t = (float)((CameraTrackFollower.TrackPosition - Route.PreviousFog.TrackPosition) / span);
				float u = 1.0f - t;
				Route.CurrentFog.Start = Route.PreviousFog.Start * u + Route.NextFog.Start * t;
				Route.CurrentFog.End = Route.PreviousFog.End * u + Route.NextFog.End * t;
				Route.CurrentFog.Color.R = (byte)(Route.PreviousFog.Color.R * u + Route.NextFog.Color.R * t);
				Route.CurrentFog.Color.G = (byte)(Route.PreviousFog.Color.G * u + Route.NextFog.Color.G * t);
				Route.CurrentFog.Color.B = (byte)(Route.PreviousFog.Color.B * u + Route.NextFog.Color.B * t);
				if (!Route.CurrentFog.IsLinear)
				{
					Route.CurrentFog.Density = Route.PreviousFog.Density * u + Route.NextFog.Density * t;
				}
			}
			else
			{
				Route.CurrentFog = Route.PreviousFog;
			}

			float start = Route.CurrentFog.Start;
			float end = Route.CurrentFog.End;
			if (start < end && start < Route.CurrentBackground.BackgroundImageDistance)
			{
				Fog.Enabled = true;
				Fog.Start = start;
				Fog.End = end;
				Fog.Color = Route.CurrentFog.Color;
				Fog.Density = Route.CurrentFog.Density;
				Fog.IsLinear = Route.CurrentFog.IsLinear;
			}
			else
			{
				Fog.Enabled = false;
			}
		}

		/// <summary>Draws the world layer from the current camera.</summary>
		/// <param name="timeElapsed">Seconds since the previous frame, for background animation.</param>
		public void RenderScene(double timeElapsed)
		{
			GlCheck("start of frame (left over from the previous frame)");
			ReleaseResources();
			GlCheck("ReleaseResources");
			ResetOpenGlState();
			GlCheck("ResetOpenGlState");

			// A daytime sky, until the route background is ported.
			GL.ClearColor(0.55f, 0.72f, 0.88f, 1.0f);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			/*
			 * Upstream's view space has Z negated relative to world space, so the camera
			 * direction and up vectors are flipped on Z to build the view matrix.
			 */
			CurrentViewMatrix = Matrix4D.LookAt(Vector3.Zero,
				new Vector3(Camera.AbsoluteDirection.X, Camera.AbsoluteDirection.Y, -Camera.AbsoluteDirection.Z),
				new Vector3(Camera.AbsoluteUp.X, Camera.AbsoluteUp.Y, -Camera.AbsoluteUp.Z));
			// For the cab's touch areas (CabTouch), projected as the cab is drawn.
			CabProjection = CurrentProjectionMatrix;
			CabView = CurrentViewMatrix;
			CabCameraTranslation = Camera.TranslationMatrix;
			CabMatricesValid = true;
			TransformedLightPosition = new Vector3(Lighting.OptionLightPosition.X, Lighting.OptionLightPosition.Y, -Lighting.OptionLightPosition.Z);
			TransformedLightPosition.Transform(CurrentViewMatrix);

			UpdateViewport(ViewportChangeMode.ChangeToScenery);
			GlCheck("UpdateViewport");

			if (Lighting.ShouldInitialize)
			{
				Lighting.Initialize();
				Lighting.ShouldInitialize = false;
			}

			DefaultShader.Activate();
			GlCheck("DefaultShader.Activate");
			DefaultShader.SetShadowEnabled(false);
			GlCheck("SetShadowEnabled");
			Fog.Enabled = false;

			Profile.Mark("setup");

			if (Route != null)
			{
				// The background is drawn first, without depth testing, behind everything else.
				GL.Disable(EnableCap.DepthTest);
				Route.UpdateBackground(timeElapsed, false);
				UpdateFog();
				DefaultShader.Activate();
			}

			Profile.Mark("background");

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

			List<FaceState> opaqueFaces;
			List<FaceState> alphaFaces;
			List<FaceState> overlayOpaqueFaces;
			List<FaceState> overlayAlphaFaces;
			lock (VisibleObjects.LockObject)
			{
				opaqueFaces = VisibleObjects.OpaqueFaces.ToList();
				alphaFaces = VisibleObjects.GetSortedPolygons();
				overlayOpaqueFaces = VisibleObjects.OverlayOpaqueFaces.ToList();
				overlayAlphaFaces = VisibleObjects.GetSortedPolygons(true);
			}

			Profile.Mark("face lists");

			if (DescribeFacesNextFrame)
			{
				DescribeFacesNextFrame = false;
				LastFaceDescription = DescribeFaces(opaqueFaces);
			}

			if (CheckMergeNextFrame && MergeOpaqueFaces)
			{
				CheckMergeNextFrame = false;
				LastMergeCheck = CompareMerging(opaqueFaces);
			}

			DrawOpaqueFaces(opaqueFaces);

			Profile.Mark("opaque faces");

			ResetOpenGlState();
			DrawAlphaFaces(alphaFaces);
			Profile.Mark("alpha faces");

			DrawingCab = true;
			try
			{
				RenderCab(overlayOpaqueFaces, overlayAlphaFaces);
			}
			finally
			{
				DrawingCab = false;
			}
			DefaultShader.Deactivate();
			Profile.Mark("cab");

			// Upstream's in-game overlays - HUD, messages, score, timetable, route information -
			// with the state upstream sets for them.
			ResetOpenGlState();
			UnsetAlphaFunc();
			SetBlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
			GL.Disable(EnableCap.DepthTest);
			overlays ??= new OpenBve.Graphics.Renderers.Overlays(this);
			/*
			 * Upstream's overlays are laid out in screen pixels for a monitor: HUD images at their
			 * own size, text in 96 dpi fonts. On a dense display they are drawn on a smaller
			 * virtual screen that the projection stretches over the real one, so the images and
			 * the text grow together and still line up (DPI-scaling the fonts alone, as upstream
			 * does on Windows, leaves the text spilling out of the HUD's boxes).
			 */
			int screenWidth = Screen.Width, screenHeight = Screen.Height;
			if (OverlayScale > 1.0)
			{
				Screen.Width = (int)(screenWidth / OverlayScale);
				Screen.Height = (int)(screenHeight / OverlayScale);
			}

			try
			{
				overlays.Render(timeElapsed);
			}
			finally
			{
				Screen.Width = screenWidth;
				Screen.Height = screenHeight;
			}
			Profile.Mark("overlays");
		}

		private OpenBve.Graphics.Renderers.Overlays overlays;

		/// <summary>
		/// Draws the overlay layer: the 3D cab, in its own viewport over the world. A port of the
		/// 3D branch of upstream's overlay pass; the 2D panel branch is not ported yet.
		/// </summary>
		private void RenderCab(List<FaceState> overlayOpaqueFaces, List<FaceState> overlayAlphaFaces)
		{
			Fog.Enabled = false;
			UpdateViewport(ViewportChangeMode.ChangeToCab);

			// The shader is reset between layers for correct lighting; the viewport change has
			// also replaced the projection matrix.
			DefaultShader.Activate();
			ResetShader(DefaultShader);
			DefaultShader.SetCurrentProjectionMatrix(CurrentProjectionMatrix);
			CurrentViewMatrix = Matrix4D.LookAt(Vector3.Zero,
				new Vector3(Camera.AbsoluteDirection.X, Camera.AbsoluteDirection.Y, -Camera.AbsoluteDirection.Z),
				new Vector3(Camera.AbsoluteUp.X, Camera.AbsoluteUp.Y, -Camera.AbsoluteUp.Z));

			if (Camera.CurrentRestriction != CameraRestrictionMode.NotAvailable && Camera.CurrentRestriction != CameraRestrictionMode.Restricted3D)
			{
				/*
				 * A 2D panel (panel.cfg / panel2.cfg): an animated object built on the fly and held
				 * in front of the camera. As upstream: unlit, blended, no depth test, in list order.
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

				GL.DepthMask(true);
				return;
			}

			// The cab is drawn over the world, so it starts with a clear depth buffer.
			ResetOpenGlState();
			GL.Clear(ClearBufferMask.DepthBufferBit);

			// Upstream lights the cab with neutral grey, independent of the scenery lighting.
			OptionLighting = true;
			OpenBveApi.Colors.Color24 previousAmbient = Lighting.OptionAmbientColor;
			OpenBveApi.Colors.Color24 previousDiffuse = Lighting.OptionDiffuseColor;
			Lighting.OptionAmbientColor = OpenBveApi.Colors.Color24.LightGrey;
			Lighting.OptionDiffuseColor = OpenBveApi.Colors.Color24.LightGrey;

			DefaultShader.SetIsLight(true);
			TransformedLightPosition = new Vector3(Lighting.OptionLightPosition.X, Lighting.OptionLightPosition.Y, -Lighting.OptionLightPosition.Z);
			DefaultShader.SetLightPosition(TransformedLightPosition);
			DefaultShader.SetLightAmbient(Lighting.OptionAmbientColor);
			DefaultShader.SetLightDiffuse(Lighting.OptionDiffuseColor);
			DefaultShader.SetLightSpecular(Lighting.OptionSpecularColor);
			DefaultShader.SetLightModel(Lighting.LightModel);

			foreach (FaceState face in overlayOpaqueFaces)
			{
				face.Draw();
			}

			ResetOpenGlState();
			DrawAlphaFaces(overlayAlphaFaces);
			Lighting.OptionAmbientColor = previousAmbient;
			Lighting.OptionDiffuseColor = previousDiffuse;
			Lighting.Initialize();
		}

		/*
		 * Transparent faces, in the options' transparency mode, as upstream's world and cab passes.
		 * In quality mode (the default here as on desktop) faces whose texture is only partly
		 * transparent - colour-keyed windows, seats, tree leaves - are in this list too, so their
		 * solid texels must be drawn first with depth writes on. The world pass once had only the
		 * performance branch, with depth writes off for everything: a tree sorted after a carriage's
		 * interior then drew over it, which from the exterior view put trees inside the train.
		 */
		private void DrawAlphaFaces(List<FaceState> faces)
		{
			if (options.TransparencyMode == TransparencyMode.Performance)
			{
				SetBlendFunc();
				SetAlphaFunc(AlphaFunction.Greater, 0.0f);
				GL.DepthMask(false);
				foreach (FaceState face in faces)
				{
					face.Draw();
				}
			}
			else
			{
				// Fully opaque texels of normal faces first, writing depth...
				UnsetBlendFunc();
				SetAlphaFunc(AlphaFunction.Equal, 1.0f);
				GL.DepthMask(true);
				foreach (FaceState face in faces)
				{
					MeshMaterial material = face.Object.Prototype.Mesh.Materials[face.Face.Material];
					if (material.BlendMode == MeshMaterialBlendMode.Normal && material.GlowAttenuationData == 0 && material.Color.A == 255)
					{
						face.Draw();
					}
				}

				// ...then everything partly transparent, blended on top without writing depth.
				SetBlendFunc();
				SetAlphaFunc(AlphaFunction.Less, 1.0f);
				GL.DepthMask(false);
				bool additive = false;
				foreach (FaceState face in faces)
				{
					if (face.Object.Prototype.Mesh.Materials[face.Face.Material].BlendMode == MeshMaterialBlendMode.Additive)
					{
						if (!additive)
						{
							UnsetAlphaFunc();
							additive = true;
						}
					}
					else if (additive)
					{
						SetAlphaFunc();
						additive = false;
					}

					face.Draw();
				}
			}

			GL.DepthMask(true);
		}

		/// <summary>Per-phase frame timings, for finding where frame time goes.</summary>
		public FrameProfile Profile { get; } = new FrameProfile();

		/// <summary>The number of cab (overlay) faces currently queued for drawing.</summary>
		/// <summary>
		/// True while the cab layer is drawn. The host loads cab textures immediately rather than
		/// streaming them: cab displays and gauges swap between many small images, and a streamed
		/// one would show blank for the frames it takes to decode.
		/// </summary>
		public bool DrawingCab { get; private set; }

		/// <summary>Whether runs of opaque faces are merged into one draw call (see <see cref="DrawOpaqueFaces"/>).</summary>
		public static bool MergeOpaqueFaces = true;

		/// <summary>Draw calls issued for opaque faces in the last frame, for the performance log.</summary>
		public int OpaqueDrawCalls { get; private set; }

		/// <summary>
		/// Draws the opaque faces, merging runs that can share one draw call.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Upstream issues one draw call, with its uniforms and state, per face. A dense stretch
		/// of the MTR route shows about 8,300 opaque faces from only 600 objects, and on a phone
		/// the per-call cost made that the largest part of the frame. But when an object's faces
		/// are shown, upstream inserts each one before the object's previous face, so an object's
		/// faces sit in the list in reverse order, and consecutive faces with the same material
		/// usually occupy adjacent ranges of the object's index buffer. About 70% of the faces
		/// continue such a run.
		/// </para>
		/// <para>
		/// A run - same object state, same material, same face flags, triangle lists, each face's
		/// indices ending where the previous face's begin - is drawn as one face covering the
		/// whole range, through upstream's own RenderFace, so the state it sets is exactly what it
		/// would have set for each face of the run. Two things differ from drawing the faces one
		/// by one. Within the run the triangles are rasterised in index order, which reverses
		/// their order; with the LEQUAL depth test that only matters where two faces of the same
		/// object and the same material overlap exactly, which the parsers do not produce
		/// deliberately. And glowing materials are left alone, because their brightness is
		/// computed from the face's own vertices.
		/// </para>
		/// </remarks>
		private void DrawOpaqueFaces(List<FaceState> faces)
		{
			int calls = 0;
			int i = 0;
			while (i < faces.Count)
			{
				FaceState first = faces[i];
				MeshFace face = first.Face;
				int start = face.IboStartIndex;
				int count = face.Vertices.Length;
				int next = i + 1;
				if (MergeOpaqueFaces && CanMerge(first))
				{
					while (next < faces.Count)
					{
						FaceState candidate = faces[next];
						if (candidate.Object != first.Object || candidate.Face.Material != face.Material || candidate.Face.Flags != face.Flags ||
						    candidate.Face.IboStartIndex + candidate.Face.Vertices.Length != start)
						{
							break;
						}

						start = candidate.Face.IboStartIndex;
						count += candidate.Face.Vertices.Length;
						next++;
					}
				}

				if (next == i + 1)
				{
					first.Draw();
				}
				else
				{
					MeshFace merged = face;
					merged.IboStartIndex = start;
					merged.Vertices = VerticesOfLength(count);
					RenderFace(CurrentShader as LibRender2.Shaders.Shader, first.Object, merged);
				}

				calls++;
				i = next;
			}

			OpaqueDrawCalls = calls;
		}

		/// <summary>Test switch: periodically compare merged and unmerged drawing (the <c>merge_check</c> intent extra).</summary>
		public static bool CheckMerging;

		/// <summary>Set to have the next frame run <see cref="CompareMerging"/>.</summary>
		public volatile bool CheckMergeNextFrame;

		/// <summary>The result of the last comparison, for the log.</summary>
		public string LastMergeCheck = string.Empty;

		/// <summary>
		/// Draws this frame's opaque faces twice into a cleared framebuffer - one draw call per face
		/// as upstream does, then merged - reads both back, and counts the pixels that differ.
		/// </summary>
		/// <remarks>
		/// A test of <see cref="DrawOpaqueFaces"/> on real content from an identical camera, which
		/// two separate runs cannot give. It stalls the GPU twice and leaves that frame's sky
		/// black, so it only runs when asked for.
		/// </remarks>
		private string CompareMerging(List<FaceState> faces)
		{
			int width = Screen.Width;
			int height = Screen.Height;
			byte[] unmerged = new byte[width * height * 4];
			byte[] merged = new byte[width * height * 4];

			GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
			MergeOpaqueFaces = false;
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
			DrawOpaqueFaces(faces);
			int unmergedCalls = OpaqueDrawCalls;
			GL.ReadPixelsRgba(0, 0, width, height, unmerged);

			MergeOpaqueFaces = true;
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
			DrawOpaqueFaces(faces);
			int mergedCalls = OpaqueDrawCalls;
			GL.ReadPixelsRgba(0, 0, width, height, merged);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			int different = 0;
			int significant = 0;
			int covered = 0;
			for (int i = 0; i < unmerged.Length; i += 4)
			{
				int delta = System.Math.Max(System.Math.Abs(unmerged[i] - merged[i]),
					System.Math.Max(System.Math.Abs(unmerged[i + 1] - merged[i + 1]), System.Math.Abs(unmerged[i + 2] - merged[i + 2])));
				if (delta != 0)
				{
					different++;
				}

				if (delta > 16)
				{
					significant++;
				}

				if (unmerged[i] != 0 || unmerged[i + 1] != 0 || unmerged[i + 2] != 0)
				{
					covered++;
				}
			}

			return different + " of " + covered + " drawn pixels differ (" + significant + " by more than 16/255), " +
			       unmergedCalls + " calls unmerged vs " + mergedCalls + " merged";
		}

		/// <summary>Whether a face may start or join a merged run.</summary>
		private static bool CanMerge(FaceState state)
		{
			return (state.Face.Flags & FaceFlags.FaceTypeMask) == FaceFlags.Triangles &&
			       state.Object.Prototype.Mesh.Materials[state.Face.Material].GlowAttenuationData == 0;
		}

		/*
		 * RenderFace takes the index count from the face's vertex array, and for non-glowing
		 * materials reads nothing else from it, so merged faces carry an array of the right
		 * length. They are shared by length rather than allocated per draw.
		 */
		private readonly Dictionary<int, MeshFaceVertex[]> placeholderVertices = new Dictionary<int, MeshFaceVertex[]>();

		private MeshFaceVertex[] VerticesOfLength(int length)
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

		/// <summary>Set to have the next frame describe its opaque faces into <see cref="LastFaceDescription"/>.</summary>
		public volatile bool DescribeFacesNextFrame;

		/// <summary>What the last described frame's opaque faces were made of, for the performance log.</summary>
		public string LastFaceDescription = string.Empty;

		/// <summary>
		/// Counts what a frame draws: faces, distinct objects, face types, and how many faces could
		/// share a draw call with the one before (same object, material and flags, triangles, and
		/// contiguous in the index buffer). Diagnostic only; costs one pass over the list.
		/// </summary>
		private static string DescribeFaces(List<FaceState> faces)
		{
			HashSet<ObjectState> objects = new HashSet<ObjectState>();
			HashSet<StaticObject> prototypes = new HashSet<StaticObject>();
			int triangles = 0, polygons = 0, quads = 0, strips = 0, mergeable = 0, mergeableReversed = 0, runs = 0, vertices = 0;
			FaceState previous = null;
			foreach (FaceState state in faces)
			{
				objects.Add(state.Object);
				prototypes.Add(state.Object.Prototype);
				vertices += state.Face.Vertices.Length;
				switch (state.Face.Flags & FaceFlags.FaceTypeMask)
				{
					case FaceFlags.Triangles:
						triangles++;
						break;
					case FaceFlags.Quads:
						quads++;
						break;
					case FaceFlags.TriangleStrip:
					case FaceFlags.QuadStrip:
						strips++;
						break;
					default:
						polygons++;
						break;
				}

				if (previous != null && previous.Object == state.Object && previous.Face.Material == state.Face.Material &&
				    previous.Face.Flags == state.Face.Flags)
				{
					if (previous.Face.IboStartIndex + previous.Face.Vertices.Length == state.Face.IboStartIndex)
					{
						mergeable++;
					}
					else if (state.Face.IboStartIndex + state.Face.Vertices.Length == previous.Face.IboStartIndex)
					{
						mergeableReversed++;
					}
				}

				if (previous == null || previous.Object != state.Object || previous.Face.Material != state.Face.Material)
				{
					runs++;
				}

				previous = state;
			}

			return faces.Count + " opaque faces (" + vertices + " indices) from " + objects.Count + " objects / " + prototypes.Count +
			       " prototypes; triangles " + triangles + ", polygons " + polygons + ", quads " + quads + ", strips " + strips +
			       "; " + mergeable + " contiguous with the previous face, " + mergeableReversed + " contiguous in reverse; " +
			       runs + " runs of one object and material";
		}

		public int VisibleCabFaceCount
		{
			get
			{
				lock (VisibleObjects.LockObject)
				{
					return VisibleObjects.OverlayOpaqueFaces.Count + VisibleObjects.OverlayAlphaFaces.Count;
				}
			}
		}

		/// <summary>The number of faces currently queued for drawing.</summary>
		public int VisibleFaceCount
		{
			get
			{
				lock (VisibleObjects.LockObject)
				{
					return VisibleObjects.OpaqueFaces.Count + VisibleObjects.AlphaFaces.Count;
				}
			}
		}
	}
}
