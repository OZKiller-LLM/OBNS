// OBNS: a copy of upstream's source/LibRender2/Shadows/Shadows.cs (OpenBVE 1.14.1.2), laid over the desktop build's mirror
// (OBNS/build-desktop.ps1). What differs: the OpenGL renderer's shadow pass draws the static batches rather than the faces they
// cover, draws an object only into the cascades its bounds reach (upstream's distance check reads ObjectState.WorldPosition,
// which only some objects set), and has no fragment shader for what has nothing see-through. The other renderers have their own.
// On an upstream update: copy the file again and reapply RenderPass and what follows it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LibRender2.Objects;
using LibRender2.Shaders;
using OpenBveApi.Interface;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;

namespace LibRender2.ShadowMapping
{
	/// <summary>
	/// Manages the Cascaded Shadow Mapping (CSM) system, including depth passes and shader binding.
	/// </summary>
	public class Shadows
	{
		private readonly BaseRenderer renderer;

		/// <summary>The shadow map textures and framebuffers.</summary>
		internal CascadedShadowMap Map;
		/// <summary>The math engine for computing cascade frustums and matrices.</summary>
		internal CascadedShadowCaster Caster;
		/// <summary>The shader used for rendering the shadow depth pass.</summary>
		internal ShadowDepthShader DepthShader;
		/// <summary>OBNS: the same with no fragment shader.</summary>
		internal ShadowDepthShader DepthOnlyShader;

		/// <summary>Whether shadows are currently active.</summary>
		public bool Enabled;
		/// <summary>The current darkness of the shadows (0.0 to 1.0).</summary>
		public float Strength;

		// Reference tuning for ComputeEffectiveFilterRadius: Medium preset (300m / 3 cascades) with 2048px maps.
		private const double ReferenceDistPerCascade = 100.0;
		private const double ReferenceResolution = 2048.0;
		private const double MinDistFactor = 0.7;
		private const double MaxDistFactor = 1.8;
		private const double MinResFactor = 0.7;
		private const double MaxResFactor = 1.4;
		private const double MinFilterRadius = 0.5;
		private const double MaxFilterRadius = 3.0;
		private const float MinNormalBiasTexels = 0.0f;
		private const float MaxNormalBiasTexels = 4.0f;

		public Shadows(BaseRenderer renderer)
		{
			this.renderer = renderer;
		}

		/// <summary>
		/// Initializes (or reinitializes) shadow mapping from current options.
		/// </summary>
		public void Initialize()
		{
			var opts = renderer.currentOptions;

			if (opts.ShadowResolution == ShadowMapResolution.Off)
			{
				Dispose();
				Enabled = false;
				renderer.fileSystem.AppendToLogFile("[CSM] Shadows disabled by user setting.");
				return;
			}

			int resolution = Math.Max(1, (int)opts.ShadowResolution);
			int cascadeCount = (int)opts.ShadowCascades;
			double shadowDistance = opts.ShadowDrawDistance == ShadowDistance.ViewingDistance ? opts.ViewingDistance : (double)(int)opts.ShadowDrawDistance;
			shadowDistance = Math.Max(1.0, shadowDistance);
			Strength = (float)opts.ShadowStrength;

			try
			{
				if (Map == null)
				{
					Map = new CascadedShadowMap(cascadeCount, resolution);
				}
				else
				{
					Map.Resize(cascadeCount, resolution);
				}

				if (Caster == null || cascadeCount != Caster.CascadeCount)
				{
					Caster = new CascadedShadowCaster(cascadeCount);
				}

				Caster.ShadowDistance = shadowDistance;
				Caster.Resolution = resolution;
				Caster.SplitLambda = 0.75;
				Caster.DepthMargin = 150.0;

				if (DepthShader == null)
				{
					DepthShader = new ShadowDepthShader(renderer, "shadow_depth", "shadow_depth", true);
				}

				// OBNS: the same vertex shader and no fragment shader (no such resource), for what has nothing see-through.
				if (DepthOnlyShader == null)
				{
					DepthOnlyShader = new ShadowDepthShader(renderer, "shadow_depth", "obns_no_fragment_shader", true);
				}

				Enabled = true;
				renderer.fileSystem.AppendToLogFile($"[CSM] Initialized: {cascadeCount} cascades, {resolution}×{resolution}, distance={shadowDistance}m, strength={Strength:P0}");
			}
			catch (Exception ex)
			{
				renderer.fileSystem.AppendToLogFile($"[CSM] Init failed: {ex.Message}");
				Enabled = false;
				GL.GetError();
			}
		}

		/// <summary>
		/// Performs the shadow depth pass for all cascades.
		/// </summary>
		public void RenderPass()
		{
			List<FaceState> opaque, alpha;
			lock (renderer.VisibleObjects.LockObject)
			{
				opaque = renderer.VisibleObjects.OpaqueFaces.ToList();
				alpha = renderer.VisibleObjects.AlphaFaces.ToList();
			}

			RenderPass(opaque, alpha, null);
		}

		/// <summary>
		/// OBNS: performs the shadow depth pass from the faces the scene draws, and what the static batches draw instead of the
		/// faces they cover (each face given with its index count).
		/// </summary>
		public void RenderPass(List<FaceState> opaque, List<FaceState> alpha, Action<Action<ObjectState, MeshFace, int>> batches)
		{
			if (!Enabled || Map == null || Caster == null || DepthShader == null)
			{
				return;
			}

			// 1. Get light direction pointing FROM the sun TOWARD the scene
			// Sun position is in OpenBVE coordinates (X: right, Y: up, Z: backward)
			// Light direction needs to be negated for some components to match the shadow math.
			Vector3 lightDir = new Vector3(
				-renderer.Lighting.OptionLightPosition.X,
				-renderer.Lighting.OptionLightPosition.Y,
				renderer.Lighting.OptionLightPosition.Z
			);

			if (lightDir.IsNullVector())
			{
				return;
			}

			// 2. Update cascade matrices
			// NOTE: We pass renderer.CurrentViewMatrix here which reflects the camera's rotation.
			// The Caster will use this to align the shadow frustums with the view direction.
			Caster.Resolution = Map.Resolution;
			if (renderer.currentOptions.ShadowDrawDistance == ShadowDistance.ViewingDistance)
			{
				Caster.ShadowDistance = renderer.currentOptions.ViewingDistance;
			}
			Caster.Update(lightDir, renderer.CurrentViewMatrix, renderer.CurrentProjectionMatrix, 0.1, renderer.Camera.VerticalViewingAngle, renderer.Screen.AspectRatio);

			// 3. Setup state for depth pass
			renderer.CurrentShader?.Deactivate();
			GL.Enable(EnableCap.DepthTest);
			GL.DepthFunc(DepthFunction.Less);
			if (renderer.OptionBackFaceCulling)
			{
				GL.Enable(EnableCap.CullFace);
				GL.CullFace(CullFaceMode.Front); // OpenBVE culls Front faces by default
			}
			else
			{
				GL.Disable(EnableCap.CullFace);
			}
			GL.DepthMask(true);
			casterVisit = casterVisit ?? DepthOnlyCaster;
			animatedThisFrame.Clear();
			cullEnabled = renderer.OptionBackFaceCulling;

			for (int i = 0; i < Caster.CascadeCount; i++)
			{
				Map.BindCascadeForWriting(i);
				GL.Clear(ClearBufferMask.DepthBufferBit);
				cascadeLight = Caster.LightSpaceMatrices[i];
				radiusX = Math.Sqrt(cascadeLight.Row0.X * cascadeLight.Row0.X + cascadeLight.Row1.X * cascadeLight.Row1.X + cascadeLight.Row2.X * cascadeLight.Row2.X);
				radiusY = Math.Sqrt(cascadeLight.Row0.Y * cascadeLight.Row0.Y + cascadeLight.Row1.Y * cascadeLight.Row1.Y + cascadeLight.Row2.Y * cascadeLight.Row2.Y);
				lastVAO = -1;

				// What has nothing see-through first, with no fragment shader; what has, after, as upstream's shader has it.
				seeThrough.Clear();
				activeShader = DepthOnlyShader;
				activeShader.Activate();
				activeShader.SetLightSpaceMatrix(cascadeLight);
				lastState = null;
				foreach (FaceState face in opaque)
				{
					DepthOnlyCaster(face.Object, face.Face, face.Face.Vertices.Length);
				}

				foreach (FaceState face in alpha)
				{
					DepthOnlyCaster(face.Object, face.Face, face.Face.Vertices.Length);
				}

				batches?.Invoke(casterVisit);
				if (seeThrough.Count > 0)
				{
					activeShader = DepthShader;
					activeShader.Activate();
					activeShader.SetLightSpaceMatrix(cascadeLight);
					activeShader.SetTexture(0);
					activeShader.SetAlphaCutoff(0.5f);
					activeShader.SetHasTexture(true);
					GL.ActiveTexture(TextureUnit.Texture0);
					lastState = null;
					foreach ((ObjectState state, MeshFace face, int count) in seeThrough)
					{
						SeeThroughCaster(state, face, count);
					}
				}

				Map.Unbind();
			}

			// 4. Restore state
			GL.DepthFunc(DepthFunction.Lequal);
			GL.Enable(EnableCap.CullFace);
			GL.CullFace(CullFaceMode.Front);
			GL.Viewport(0, 0, renderer.Screen.Width, renderer.Screen.Height);
			// Shadow pass corrupts the GL texture state, so ensure we null it out
			// so the next render pass re-binds what it needs.
			renderer.LastBoundTexture = null;
		}

		// OBNS: the casters, each object placed once per cascade, and only into the cascades its bounds reach.
		private static readonly ConditionalWeakTable<Mesh, double[]> meshSpheres = new ConditionalWeakTable<Mesh, double[]>();
		private readonly List<(ObjectState, MeshFace, int)> seeThrough = new List<(ObjectState, MeshFace, int)>();
		private readonly HashSet<ObjectState> animatedThisFrame = new HashSet<ObjectState>();
		private Action<ObjectState, MeshFace, int> casterVisit;
		private ShadowDepthShader activeShader;
		private ObjectState lastState;
		private bool lastVisible, cullEnabled;
		private int lastVAO;
		private Matrix4D cascadeLight;
		private double radiusX, radiusY;

		private void DepthOnlyCaster(ObjectState state, MeshFace face, int count)
		{
			Mesh mesh = state.Prototype.Mesh;
			if (mesh.VAO == null || state.DisableShadowCasting || count <= 0)
			{
				return;
			}

			MeshMaterial material = mesh.Materials[face.Material];
			// What upstream's shader discards all of, or never draws.
			if ((material.Flags & MaterialFlags.NoShadow) != 0 || material.BlendMode == MeshMaterialBlendMode.Additive || material.Color.A <= 127)
			{
				return;
			}

			OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;
			// Only what is loaded: the scene draws these faces and asks for their textures, and a request here would compete with
			// its own for the few decodes (the first seconds' white faces lasted 25 s, not 6). Until then a face casts as solid.
			if (material.DaytimeTexture != null && material.DaytimeTexture.OpenGlTextures[(int)wrap].Valid && material.DaytimeTexture.Transparency != TextureTransparencyType.Opaque)
			{
				seeThrough.Add((state, face, count));
				return;
			}

			if (PlaceObject(state, mesh))
			{
				DrawCaster(mesh, face, count);
			}
		}

		private void SeeThroughCaster(ObjectState state, MeshFace face, int count)
		{
			Mesh mesh = state.Prototype.Mesh;
			if (!PlaceObject(state, mesh))
			{
				return;
			}

			MeshMaterial material = mesh.Materials[face.Material];
			OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;
			GL.BindTexture(TextureTarget.Texture2D, material.DaytimeTexture.OpenGlTextures[(int)wrap].Name);
			activeShader.SetMaterialAlpha(material.Color.A / 255.0f);
			activeShader.SetMaterialFlags(material.Flags);
			DrawCaster(mesh, face, count);
		}

		/// <summary>The object's matrices for the faces that follow; whether it reaches the cascade.</summary>
		private bool PlaceObject(ObjectState state, Mesh mesh)
		{
			if (state == lastState)
			{
				return lastVisible;
			}

			lastState = state;
			Matrix4D model = state.ModelMatrix * renderer.Camera.TranslationMatrix;
			bool animated = state.Matricies != null && state.Matricies.Length > 0;
			lastVisible = animated || state.Prototype.Dynamic || InCascade(mesh, model);
			if (!lastVisible)
			{
				return false;
			}

			activeShader.SetModelMatrix(model);
			activeShader.SetTextureMatrix(state.TextureTranslation);
			if (animated)
			{
				// The buffer once a frame, however many cascades it is drawn into.
				if (animatedThisFrame.Add(state))
				{
					activeShader.SetCurrentAnimationMatricies(state);
				}

				GL.BindBufferBase(BufferTarget.UniformBuffer, 0, state.MatrixBufferIndex);
			}

			return true;
		}

		/// <summary>Whether a mesh's bounding sphere, placed by a model matrix, reaches the current cascade's box across the light.</summary>
		private bool InCascade(Mesh mesh, Matrix4D model)
		{
			double[] sphere = meshSpheres.GetValue(mesh, BoundingSphere);
			if (sphere[3] < 0.0)
			{
				return true;
			}

			// The shaders negate Z before these matrices, which take row vectors.
			double bx = sphere[0], by = sphere[1], bz = -sphere[2];
			double cx = bx * model.Row0.X + by * model.Row1.X + bz * model.Row2.X + model.Row3.X;
			double cy = bx * model.Row0.Y + by * model.Row1.Y + bz * model.Row2.Y + model.Row3.Y;
			double cz = bx * model.Row0.Z + by * model.Row1.Z + bz * model.Row2.Z + model.Row3.Z;
			double scale = Math.Sqrt(Math.Max(model.Row0.X * model.Row0.X + model.Row0.Y * model.Row0.Y + model.Row0.Z * model.Row0.Z,
				Math.Max(model.Row1.X * model.Row1.X + model.Row1.Y * model.Row1.Y + model.Row1.Z * model.Row1.Z, model.Row2.X * model.Row2.X + model.Row2.Y * model.Row2.Y + model.Row2.Z * model.Row2.Z)));
			double x = cx * cascadeLight.Row0.X + cy * cascadeLight.Row1.X + cz * cascadeLight.Row2.X + cascadeLight.Row3.X;
			double y = cx * cascadeLight.Row0.Y + cy * cascadeLight.Row1.Y + cz * cascadeLight.Row2.Y + cascadeLight.Row3.Y;
			double radius = sphere[3] * scale;
			return Math.Abs(x) <= 1.0 + radius * radiusX && Math.Abs(y) <= 1.0 + radius * radiusY;
		}

		private static double[] BoundingSphere(Mesh mesh)
		{
			VertexTemplate[] vertices = mesh.Vertices;
			if (vertices == null || vertices.Length == 0)
			{
				return new[] { 0.0, 0.0, 0.0, -1.0 };
			}

			double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
			foreach (VertexTemplate vertex in vertices)
			{
				Vector3 c = vertex.Coordinates;
				minX = Math.Min(minX, c.X); maxX = Math.Max(maxX, c.X);
				minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
				minZ = Math.Min(minZ, c.Z); maxZ = Math.Max(maxZ, c.Z);
			}

			double x = 0.5 * (minX + maxX), y = 0.5 * (minY + maxY), z = 0.5 * (minZ + maxZ), squared = 0.0;
			foreach (VertexTemplate vertex in vertices)
			{
				Vector3 c = vertex.Coordinates;
				squared = Math.Max(squared, (c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y) + (c.Z - z) * (c.Z - z));
			}

			return new[] { x, y, z, Math.Sqrt(squared) };
		}

		private void DrawCaster(Mesh mesh, MeshFace face, int count)
		{
			VertexArrayObject vao = (VertexArrayObject)mesh.VAO;
			if (vao.handle != lastVAO)
			{
				vao.Bind();
				lastVAO = vao.handle;
			}

			if (renderer.OptionBackFaceCulling)
			{
				// Double-sided faces (Face2) must not be culled to ensure they cast shadows from both sides
				bool cull = (face.Flags & FaceFlags.Face2Mask) == 0;
				if (cull != cullEnabled)
				{
					if (cull)
					{
						GL.Enable(EnableCap.CullFace);
					}
					else
					{
						GL.Disable(EnableCap.CullFace);
					}

					cullEnabled = cull;
				}
			}

			vao.Draw(renderer.GetPrimitiveType(face.Flags), face.IboStartIndex, count);
		}

		/// <summary>
		/// Binds shadow data to the main scene shader.
		/// </summary>
		public void Bind(Shader shader)
		{
			if (!Enabled || Map == null || Caster == null)
			{
				shader.SetShadowEnabled(false);
				// To satisfy strict OpenGL drivers, always bind something to the shadow map units
				// even if shadow is disabled, to avoid "sampler collision" errors.
				for (int i = 0; i < 4; i++)
				{
					GL.ActiveTexture(TextureUnit.Texture4 + i);
					GL.BindTexture(TextureTarget.Texture2D, renderer.nullDepthMap);
				}
				GL.ActiveTexture(TextureUnit.Texture0);
				return;
			}

			shader.Activate();
			shader.SetShadowEnabled(true);
			shader.SetShadowStrength((float)renderer.currentOptions.ShadowStrength);
			shader.SetShadowSmooth(renderer.currentOptions.ShadowSmooth);
			shader.SetShadowFilterRadius(ComputeEffectiveFilterRadius());
			shader.SetCurrentViewMatrix(renderer.CurrentViewMatrix);

			Map.BindAllCascadesForReading(TextureUnit.Texture4);

			int cascadeCount = Caster.CascadeCount;
			// Normal bias is in Unity-style texel units (typical 0.3-1.0). Clamp so a stale
			// config (old 2.0x multiplier default) can't detach shadows catastrophically.
			float normalBiasTexels = Math.Max(MinNormalBiasTexels, Math.Min(MaxNormalBiasTexels, (float)renderer.currentOptions.ShadowNormalBias));
			for (int i = 0; i < cascadeCount; i++)
			{
				shader.SetCascadeLightSpaceMatrix(i, Caster.LightSpaceMatrices[i]);
				shader.SetCascadeShadowMapUnit(i, 4 + i);
				// Split distance = the view-space Z where this cascade ends.
				shader.SetShadowSplitDistance(i, (float)Caster.SplitDistances[i]);
				shader.SetCascadeBias(i, Caster.CascadeBiases[i] + (float)renderer.currentOptions.ShadowBias);
				shader.SetNormalBias(i, normalBiasTexels);
				shader.SetTexelWorldSize(i, Caster.TexelWorldSizes[i]);
			}

			for (int i = cascadeCount; i < 4; i++)
			{
				shader.SetShadowSplitDistance(i, 0.0f);
				shader.SetTexelWorldSize(i, cascadeCount > 0 ? Caster.TexelWorldSizes[cascadeCount - 1] : 0.05f);
			}
			shader.SetShadowCascadeCount(cascadeCount);
		}

		/// <summary>
		/// Auto-scales the filter radius by cascade count, shadow distance and resolution
		/// so softness stays perceptually consistent: far distance / few cascades / high res → slightly larger texel radius.
		/// </summary>
		private float ComputeEffectiveFilterRadius()
		{
			double baseRadius = renderer.currentOptions.ShadowFilterRadius;

			double shadowDist = renderer.currentOptions.ShadowDrawDistance == ShadowDistance.ViewingDistance
				? renderer.currentOptions.ViewingDistance
				: (double)(int)renderer.currentOptions.ShadowDrawDistance;
			int optCascadeCount = Math.Max(1, (int)renderer.currentOptions.ShadowCascades);
			int res = Math.Max(512, (int)renderer.currentOptions.ShadowResolution);

			double actualDistPerCascade = shadowDist / optCascadeCount;
			double distFactor = Math.Sqrt(actualDistPerCascade / ReferenceDistPerCascade);
			distFactor = Math.Max(MinDistFactor, Math.Min(MaxDistFactor, distFactor));

			double resFactor = Math.Sqrt((double)res / ReferenceResolution);
			resFactor = Math.Max(MinResFactor, Math.Min(MaxResFactor, resFactor));

			double effectiveRadius = baseRadius * distFactor * resFactor;
			effectiveRadius = Math.Max(MinFilterRadius, Math.Min(MaxFilterRadius, effectiveRadius));
			return (float)effectiveRadius;
		}

		/// <summary>
		/// Disposes shadow resources.
		/// </summary>
		public void Dispose()
		{
			Map?.Dispose();
			Map = null;
			DepthShader?.Dispose();
			DepthShader = null;
			DepthOnlyShader?.Dispose();
			DepthOnlyShader = null;
			Caster = null;
			Enabled = false;
		}
	}
}
