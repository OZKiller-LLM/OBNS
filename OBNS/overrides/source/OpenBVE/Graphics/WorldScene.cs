// OBNS: not an upstream file. The world layer's frame (OBNS/VULKAN.md, OBNS/DIRECT3D.md), whichever API draws it:
// upstream's per-face drawing (LibRender2's BaseRenderer.RenderFace) as records the shaders read by the draw's index,
// made on every core, and the draws in order; how the records reach the GPU and are drawn is the API's.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibRender2;
using LibRender2.Objects;
using LibRender2.ShadowMapping;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Textures;
using Blend = OpenBve.Graphics.WorldBlend;

namespace OpenBve.Graphics
{
	internal abstract unsafe class WorldScene : IWorldScene, IDisposable
	{
		// --- the shaders' records (std430; see Vulkan/Shaders/world.vert, and Direct3D11/Shaders/world.hlsl) ---

		[StructLayout(LayoutKind.Sequential)]
		protected struct FrameData
		{
			public fixed float Projection[16];
			public fixed float Light[24]; // position, ambient, diffuse, specular, model, fog colour (vec4 each)
			public float FogStart, FogEnd, FogDensity;
			public int FogIsLinear;
			public int IsLight, ShadowEnabled, ShadowCascades, ShadowSmooth;
			public fixed float InverseView[16];
			public fixed float LightSpace[64];
			public fixed float ShadowSplit[4];
			public fixed float ShadowBias[4];
			public fixed float ShadowNormalBias[4];
			public fixed float ShadowTexelSize[4];
			public float ShadowStrength, ShadowFilterRadius, Pad0, Pad1;
		}

		[StructLayout(LayoutKind.Sequential)]
		protected struct ObjectData
		{
			public fixed float ModelView[16];
			public fixed float Normal[12];
			public fixed float TextureMatrix[16];
			public int MatrixBase, Pad0, MatrixCount, Pad2;
		}

		[StructLayout(LayoutKind.Sequential)]
		protected struct DrawData
		{
			public float ColorR, ColorG, ColorB, ColorA;
			public float SpecularR, SpecularG, SpecularB, SpecularA;
			public float EmissionR, EmissionG, EmissionB, Shininess;
			public int Flags;
			public float Brightness;
			public float Opacity;
			public int Texture;
			public int Sampler;
			public int AlphaFunction;
			public float AlphaReference;
			public int Object;
			public int Fog;
			public int CullFront;
			public int Pad0, Pad1;
		}

		/// <summary>A draw to record: its pipeline and buffer block (the order key), its indices, and its record.</summary>
		protected struct DrawCommand
		{
			public long Key;
			public uint FirstIndex, IndexCount;
			public int VertexBase, Draw, Pass;
			/// <summary>Whether the face casts a shadow: 0 no, 1 culled as upstream's shadow pass culls it, 2 both sides.</summary>
			public int Shadow;
		}

		/// <summary>OpenGL's alpha functions as upstream's shader numbers them; Never means none.</summary>
		internal const int AlphaNever = WorldLayer.AlphaNever, AlphaLess = WorldLayer.AlphaLess, AlphaEqual = WorldLayer.AlphaEqual, AlphaGreater = WorldLayer.AlphaGreater;

		// The frame's records, merged from the chunks they were made in.
		protected ObjectData[] objects = new ObjectData[1024];
		protected int objectCount;
		protected DrawData[] draws = new DrawData[4096];
		protected int drawCount;
		protected float[] matrices = new float[16 * 256];
		protected int matrixCount;
		protected DrawCommand[] commands = new DrawCommand[4096];
		protected int commandCount;
		/// <summary>Where each pass's commands start, and whether they must be drawn in order.</summary>
		private readonly List<int> segmentStarts = new List<int>();
		private readonly List<bool> segmentOrdered = new List<bool>();
		/// <summary>The layers' frame records: the world's (0) and the cab's (1), chosen by the push constant.</summary>
		protected readonly FrameData[] frameData = new FrameData[2];
		protected BaseRenderer renderer;

		/// <summary>A face to draw, as upstream's passes give it, with the alpha test in force for it.</summary>
		private struct Item
		{
			public ObjectState State;
			public MeshFace Face;
			public int Pass, AlphaFunction;
			public float AlphaReference;
			// Resolved by Prepare (main thread): the mesh, the face's indices, its textures' places (-1: none).
			public WorldMesh Mesh;
			public uint FirstIndex, IndexCount;
			public int DayTexture, NightTexture;
		}

		// Upstream's renderer state for the frame, taken by Begin: the frame's records are made from these (on other
		// threads, while the main thread goes on) rather than from the renderer.
		private Matrix4D cameraTranslation, viewMatrix, cabViewMatrix;
		private bool backFaceCulling, fogEnabled;
		private float lightingAmount;

		/// <summary>The layers: upstream's world layer, and its overlay (cab) layer, drawn into an image of its own.</summary>
		internal const int WorldLayerIndex = 0, CabLayer = 1;

		/// <summary>A pass's state: its layer, blending, depth test and writes, and whether its faces are drawn in the order given.</summary>
		private struct PassInfo
		{
			public int Layer;
			public Blend Blend;
			public bool DepthTest, DepthWrite, Ordered;
		}

		private int currentLayer;

		private Item[] items = new Item[8192];
		private int itemCount;
		private readonly List<PassInfo> passes = new List<PassInfo>();
		private int currentAlphaFunction;
		private float currentAlphaReference;

		/// <summary>The records one chunk of the faces makes, on whichever thread builds it.</summary>
		private sealed class Chunk
		{
			public ObjectData[] Objects = new ObjectData[256];
			public int ObjectCount;
			public DrawData[] Draws = new DrawData[512];
			public int DrawCount;
			public DrawCommand[] Commands = new DrawCommand[512];
			public int CommandCount;
			public float[] Matrices = new float[16 * 16];
			public int MatrixCount;
			public int Skipped;
			public long ObjectTicks;
		}

		private readonly List<Chunk> chunks = new List<Chunk>();
		private int chunkCount;
		private readonly Action<int, int> buildChunks;

		/// <summary>Draw records and indirect draws in the last frame, for the log.</summary>
		internal int LastDraws { get; private set; }
		internal int LastCabDraws { get; private set; }
		private int skippedFaces;

		/// <summary>The textures the faces' places are looked up in.</summary>
		protected abstract WorldTextures SceneTextures { get; }

		/// <summary>The size of the textures' pages (0: one array, no pages): draws are grouped by page.</summary>
		protected int TexturePage;

		protected WorldScene()
		{
			buildChunks = BuildChunks;
		}

		/// <summary>Makes the shadow maps at a size and count (upstream's options), if they are not so already.</summary>
		public abstract void EnsureShadowMaps(int resolution, int cascades);

		public abstract void Dispose();

		// --- gathering the frame: upstream's passes give their faces (main thread), then Build makes the records ---

		/// <summary>Starts a frame's world layer with upstream's renderer state: camera, lighting and fog.</summary>
		public void Begin(BaseRenderer baseRenderer)
		{
			renderer = baseRenderer;
			itemCount = 0;
			passes.Clear();
			currentLayer = WorldLayerIndex;
			cabItemStart = -1;
			cameraTranslation = renderer.Camera.TranslationMatrix;
			viewMatrix = renderer.CurrentViewMatrix;
			backFaceCulling = renderer.OptionBackFaceCulling;
			fogEnabled = renderer.Fog.Enabled;
			lightingAmount = renderer.Lighting.OptionLightingResultingAmount;

			FrameData data = new FrameData();
			Store(renderer.CurrentProjectionMatrix, data.Projection);
			Vector3 light = renderer.TransformedLightPosition;
			light.Normalize();
			data.Light[0] = (float)light.X;
			data.Light[1] = (float)light.Y;
			data.Light[2] = (float)light.Z;
			StoreColor(renderer.Lighting.OptionAmbientColor, data.Light, 4);
			StoreColor(renderer.Lighting.OptionDiffuseColor, data.Light, 8);
			StoreColor(renderer.Lighting.OptionSpecularColor, data.Light, 12);
			Vector4 model = renderer.Lighting.LightModel;
			data.Light[16] = (float)model.X;
			data.Light[17] = (float)model.Y;
			data.Light[18] = (float)model.Z;
			data.Light[19] = (float)model.W;
			StoreColor(renderer.Fog.Color, data.Light, 20);
			data.FogStart = renderer.Fog.Start;
			data.FogEnd = renderer.Fog.End;
			data.FogDensity = renderer.Fog.Density;
			data.FogIsLinear = renderer.Fog.IsLinear ? 1 : 0;
			data.IsLight = renderer.OptionLighting ? 1 : 0;
			frameData[WorldLayerIndex] = data;
		}

		/// <summary>Where the cab layer's faces start, or -1 if it has none this frame.</summary>
		private int cabItemStart;

		/// <summary>Whether the frame has a cab layer to draw (and lay over).</summary>
		internal bool HasCab => cabItemStart >= 0 && cabItemStart < itemCount;

		/// <summary>
		/// Starts the frame's overlay (cab) layer, as upstream's renderer draws it after the world: its own projection
		/// (the cab's near plane, 50 m far), the view, no fog, and for a 3D cab the light-grey light upstream sets for it.
		/// The passes given from now are the cab's.
		/// </summary>
		public void BeginCab(Matrix4D projection, Matrix4D view, bool lit, Vector3 lightPosition)
		{
			currentLayer = CabLayer;
			cabItemStart = itemCount;
			cabViewMatrix = view;
			FrameData data = frameData[WorldLayerIndex];
			Store(projection, data.Projection);
			if (lit)
			{
				Vector3 light = lightPosition;
				light.Normalize();
				data.Light[0] = (float)light.X;
				data.Light[1] = (float)light.Y;
				data.Light[2] = (float)light.Z;
				StoreColor(Color24.LightGrey, data.Light, 4);
				StoreColor(Color24.LightGrey, data.Light, 8);
			}

			data.IsLight = lit ? 1 : 0;
			// Upstream draws the cab without shadows.
			data.ShadowEnabled = 0;
			frameData[CabLayer] = data;
		}

		/// <summary>
		/// The world layer's shadows this frame, as upstream binds them to its shader (LibRender2's Shadows.Bind): the
		/// cascades' light-space matrices and splits, the biases, strength and filter; null for none.
		/// </summary>
		public void SetShadows(CascadedShadowCaster caster, float strength, bool smooth, float filterRadius, float userBias, float normalBiasTexels)
		{
			FrameData data = frameData[WorldLayerIndex];
			shadowCascades = 0;
			if (caster == null)
			{
				data.ShadowEnabled = 0;
				frameData[WorldLayerIndex] = data;
				return;
			}

			shadowCascades = caster.CascadeCount;
			data.ShadowEnabled = 1;
			data.ShadowCascades = caster.CascadeCount;
			data.ShadowSmooth = smooth ? 1 : 0;
			data.ShadowStrength = strength;
			data.ShadowFilterRadius = filterRadius;
			Store(Matrix4D.Invert(viewMatrix), data.InverseView);
			for (int i = 0; i < 4; i++)
			{
				if (i < caster.CascadeCount)
				{
					Store(caster.LightSpaceMatrices[i], data.LightSpace + 16 * i);
					data.ShadowSplit[i] = caster.SplitDistances[i];
					data.ShadowBias[i] = caster.CascadeBiases[i] + userBias;
					data.ShadowNormalBias[i] = normalBiasTexels;
					data.ShadowTexelSize[i] = caster.TexelWorldSizes[i];
				}
				else
				{
					data.ShadowSplit[i] = 0.0f;
					data.ShadowTexelSize[i] = caster.CascadeCount > 0 ? caster.TexelWorldSizes[caster.CascadeCount - 1] : 0.05f;
				}
			}

			frameData[WorldLayerIndex] = data;
		}

		/// <summary>How many cascades the shadow pass draws this frame (0: none).</summary>
		protected int shadowCascades;

		private static void StoreColor(Color24 color, float* to, int at)
		{
			to[at] = color.R / 255.0f;
			to[at + 1] = color.G / 255.0f;
			to[at + 2] = color.B / 255.0f;
			to[at + 3] = 1.0f;
		}

		/// <summary>Upstream's Matrix4D as its shader has it: the rows, in order (OpenGL reads them as columns).</summary>
		protected static void Store(Matrix4D m, float* to)
		{
			to[0] = (float)m.Row0.X; to[1] = (float)m.Row0.Y; to[2] = (float)m.Row0.Z; to[3] = (float)m.Row0.W;
			to[4] = (float)m.Row1.X; to[5] = (float)m.Row1.Y; to[6] = (float)m.Row1.Z; to[7] = (float)m.Row1.W;
			to[8] = (float)m.Row2.X; to[9] = (float)m.Row2.Y; to[10] = (float)m.Row2.Z; to[11] = (float)m.Row2.W;
			to[12] = (float)m.Row3.X; to[13] = (float)m.Row3.Y; to[14] = (float)m.Row3.Z; to[15] = (float)m.Row3.W;
		}

		/// <summary>
		/// Starts a pass with upstream's state for it: blending, the alpha function, depth writes, and whether its faces
		/// must be drawn in the order given (blended faces), or may be grouped (the rest).
		/// </summary>
		public void Pass(Blend blend, int alphaFunction, float alphaReference, bool depthWrite, bool ordered, bool depthTest = true)
		{
			passes.Add(new PassInfo { Layer = currentLayer, Blend = blend, DepthTest = depthTest, DepthWrite = depthWrite, Ordered = ordered });
			currentAlphaFunction = alphaFunction;
			currentAlphaReference = alphaReference;
		}

		/// <summary>Changes the pass's alpha function (upstream's additive faces have none) without starting a new pass.</summary>
		public void SetAlphaFunction(int alphaFunction, float alphaReference)
		{
			currentAlphaFunction = alphaFunction;
			currentAlphaReference = alphaReference;
		}

		/// <summary>A face for the pass, drawn as upstream's RenderFace would draw it.</summary>
		public void Face(ObjectState state, MeshFace face)
		{
			if (itemCount == items.Length)
			{
				Array.Resize(ref items, items.Length * 2);
			}

			items[itemCount++] = new Item { State = state, Face = face, Pass = passes.Count - 1, AlphaFunction = currentAlphaFunction, AlphaReference = currentAlphaReference };
		}

		private const float Inv255 = 1.0f / 255.0f;


		/// <summary>
		/// The faces given, resolved on the main thread: what needs OpenGL or changes shared state (missing VAOs, textures
		/// not loaded yet or animated, objects' lazily made model matrices), and each face's mesh, indices and textures'
		/// places, so that making the records (BuildRecords, on other threads) reads nothing the main thread changes.
		/// </summary>
		internal void Prepare()
		{
			SceneTextures.StartBudget();
			ObjectState last = null;
			for (int i = 0; i < itemCount; i++)
			{
				ref Item item = ref items[i];
				item.Mesh = null;
				ObjectState state = item.State;
				Mesh mesh = state.Prototype.Mesh;
				if (mesh.Vertices.Length < 1)
				{
					continue;
				}

				if (mesh.VAO == null)
				{
					VAOExtensions.CreateOrUpdateVAO(mesh, state.Prototype.Dynamic, renderer.DefaultShader.VertexLayout, renderer);
				}

				if (state != last)
				{
					// ObjectState works its model matrix out when first asked, so it is asked here, not on the workers.
					Matrix4D model = state.ModelMatrix;
					last = state;
				}

				if (!((mesh.VAO as VertexArrayObject)?.ObnsWorldMesh is WorldMesh worldMesh) || !worldMesh.Find(item.Face.IboStartIndex, out int slot, out item.FirstIndex, out item.IndexCount))
				{
					continue;
				}

				ref MeshMaterial material = ref mesh.Materials[item.Face.Material];
				OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;
				item.DayTexture = TextureIndex(material.DaytimeTexture, wrap, worldMesh.DaySlots, slot);
				item.NightTexture = -1;
				if (material.NighttimeTexture != null && material.NighttimeTexture != material.DaytimeTexture && BlendFactor(state) != 0)
				{
					item.NightTexture = TextureIndex(material.NighttimeTexture, wrap, worldMesh.NightSlots, slot);
				}

				item.Mesh = worldMesh;
			}
		}

		/// <summary>
		/// Makes the frame's records from the prepared faces, in chunks on every core, then merges the chunks in order
		/// (blended faces keep upstream's order). Reads only the faces and the frame's state taken by Begin.
		/// </summary>
		internal void BuildRecords()
		{
			chunkCount = itemCount == 0 ? 0 : Math.Min(Math.Max(1, itemCount / 128), FrameWorkers.Shared.Threads * 4);
			while (chunks.Count < chunkCount)
			{
				chunks.Add(new Chunk());
			}

			FrameWorkers.Shared.Run(chunkCount, buildChunks);
			Merge();
		}

		private float BlendFactor(ObjectState state)
		{
			float blendFactor = Inv255 * state.DaytimeNighttimeBlend + 1.0f - lightingAmount;
			return blendFactor > 1.0f ? 1.0f : blendFactor;
		}

		private void BuildChunks(int first, int last)
		{
			for (int c = first; c < last; c++)
			{
				BuildChunk(chunks[c], (int)((long)itemCount * c / chunkCount), (int)((long)itemCount * (c + 1) / chunkCount));
			}
		}

		/// <summary>A chunk's records (on a worker): read-only use of upstream's state, writes only to the chunk.</summary>
		private void BuildChunk(Chunk chunk, int start, int end)
		{
			chunk.ObjectCount = chunk.DrawCount = chunk.CommandCount = chunk.MatrixCount = chunk.Skipped = 0;
			chunk.ObjectTicks = 0;
			ObjectState lastState = null;
			Matrix4D lastModelMatrix = Matrix4D.Identity;
			int lastObject = 0, lastLayer = -1;
			for (int i = start; i < end; i++)
			{
				int layer = passes[items[i].Pass].Layer;
				if (layer != lastLayer)
				{
					// An object's record holds its model-view, which is the layer's.
					lastState = null;
					lastLayer = layer;
				}

				BuildFace(chunk, ref items[i], ref lastState, ref lastModelMatrix, ref lastObject);
			}
		}

		/// <summary>A face's records, as upstream's RenderFace would draw it in its pass.</summary>
		private void BuildFace(Chunk chunk, ref Item item, ref ObjectState lastState, ref Matrix4D lastModelMatrix, ref int lastObject)
		{
			WorldMesh worldMesh = item.Mesh;
			if (worldMesh == null)
			{
				if (item.State.Prototype.Mesh.Vertices.Length > 0)
				{
					chunk.Skipped++;
				}

				return;
			}

			ObjectState state = item.State;
			Mesh mesh = state.Prototype.Mesh;
			uint firstIndex = item.FirstIndex, indexCount = item.IndexCount;
			PassInfo pass = passes[item.Pass];

			// The object: its matrices, once per object as upstream sends them.
			if (state != lastState || state.Prototype.Dynamic)
			{
				long objectStart = System.Diagnostics.Stopwatch.GetTimestamp();
				lastModelMatrix = state.ModelMatrix * cameraTranslation;
				lastObject = AddObject(chunk, state, lastModelMatrix * (pass.Layer == CabLayer ? cabViewMatrix : viewMatrix));
				lastState = state;
				chunk.ObjectTicks += System.Diagnostics.Stopwatch.GetTimestamp() - objectStart;
			}

			ref MeshMaterial material = ref mesh.Materials[item.Face.Material];
			bool cull = backFaceCulling && (item.Face.Flags & FaceFlags.Face2Mask) == 0;
			OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;

			float distanceFactor = material.GlowAttenuationData != 0
				? (float)Glow.GetDistanceFactor(lastModelMatrix, mesh.Vertices, ref item.Face, material.GlowAttenuationData)
				: 1.0f;
			float blendFactor = BlendFactor(state);

			DrawData draw = new DrawData
			{
				ColorR = material.Color.R * Inv255, ColorG = material.Color.G * Inv255, ColorB = material.Color.B * Inv255, ColorA = material.Color.A * Inv255,
				Flags = (int)material.Flags,
				Object = lastObject,
				Sampler = (int)wrap & 3,
				Fog = fogEnabled && pass.Layer == WorldLayerIndex ? 1 : 0,
				AlphaFunction = item.AlphaFunction,
				AlphaReference = item.AlphaReference,
				Shininess = 1.0f
			};
			Color24 specular = (material.Flags & MaterialFlags.Specular) != 0 ? material.SpecularColor : new Color24(material.Color.R, material.Color.G, material.Color.B);
			draw.SpecularR = specular.R * Inv255;
			draw.SpecularG = specular.G * Inv255;
			draw.SpecularB = specular.B * Inv255;
			draw.SpecularA = (material.Flags & MaterialFlags.Specular) != 0 ? 1.0f : draw.ColorA;
			draw.EmissionR = material.EmissiveColor.R * Inv255;
			draw.EmissionG = material.EmissiveColor.G * Inv255;
			draw.EmissionB = material.EmissiveColor.B * Inv255;

			// Daytime texture, else white.
			draw.Texture = item.DayTexture >= 0 ? item.DayTexture : WorldTextures.White;

			Blend blend = pass.Blend;
			bool additive = material.BlendMode == MeshMaterialBlendMode.Additive;
			float factor;
			if (additive)
			{
				factor = 1.0f;
				blend = Blend.Additive;
				draw.Fog = 0;
			}
			else if (material.NighttimeTexture == null || material.NighttimeTexture == material.DaytimeTexture)
			{
				factor = 1.0f - 0.7f * blendFactor;
			}
			else
			{
				factor = 1.0f;
			}

			draw.Brightness = factor;
			float alphaFactor = distanceFactor;
			if (material.NighttimeTexture != null && (material.Flags & MaterialFlags.CrossFadeTexture) != 0)
			{
				alphaFactor *= 1.0f - blendFactor;
			}

			draw.Opacity = Inv255 * material.Color.A * alphaFactor;
			// A shadow caster as upstream's shadow pass has it: a world face, not additive, its material and object casting.
			int shadow = 0;
			if (pass.Layer == WorldLayerIndex && !additive && (material.Flags & MaterialFlags.NoShadow) == 0 && !state.DisableShadowCasting)
			{
				shadow = backFaceCulling && (item.Face.Flags & FaceFlags.Face2Mask) == 0 ? 1 : 2;
			}

			AddDraw(chunk, draw, pass, item.Pass, blend, cull, firstIndex, indexCount, worldMesh, false, shadow);

			// Nighttime texture, blended over the daytime one.
			if (blendFactor != 0 && item.NightTexture >= 0)
			{
				draw.Texture = item.NightTexture;
				draw.AlphaFunction = AlphaGreater;
				draw.AlphaReference = 0.0f;
				draw.Opacity = Inv255 * material.Color.A * distanceFactor * blendFactor;
				AddDraw(chunk, draw, pass, item.Pass, additive ? Blend.Additive : Blend.Normal, cull, firstIndex, indexCount, worldMesh, true, 0);
			}
		}

		/// <summary>
		/// A texture's place in the layer's textures (made the first time it is needed; main thread), or -1 if it cannot be
		/// made. The place is kept per face, for as long as it is the same texture and nothing has moved; an animated
		/// texture is asked for every frame (which advances it).
		/// </summary>
		private int TextureIndex(Texture texture, OpenGlTextureWrapMode wrap, TextureSlot[] slots, int slot)
		{
			if (texture == null)
			{
				return -1;
			}

			TextureSlot cached = slots[slot];
			if (cached != null && ReferenceEquals(cached.Texture, texture) && cached.Generation == SceneTextures.Generation)
			{
				return cached.Index;
			}

			int index = SceneTextures.Prepare(texture, wrap, out bool animated);
			if (index == WorldTextures.Later)
			{
				// Not made yet (the frame's budget is spent): white this frame, asked for again next frame.
				return -1;
			}

			slots[slot] = animated ? null : new TextureSlot(texture, index, SceneTextures.Generation);
			return index;
		}

		private int AddObject(Chunk chunk, ObjectState state, Matrix4D modelView)
		{
			if (chunk.ObjectCount == chunk.Objects.Length)
			{
				Array.Resize(ref chunk.Objects, chunk.Objects.Length * 2);
			}

			ObjectData data = new ObjectData();
			Store(modelView, data.ModelView);
			Store(state.TextureTranslation, data.TextureMatrix);

			// The normal matrix: transpose(inverse(mat3(modelView as the shader has it))), which is the inverse of
			// the upper 3x3 of upstream's (row-major) matrix; stored as the shader's columns.
			double a = modelView.Row0.X, b = modelView.Row0.Y, c = modelView.Row0.Z;
			double d = modelView.Row1.X, e = modelView.Row1.Y, f = modelView.Row1.Z;
			double g = modelView.Row2.X, h = modelView.Row2.Y, i = modelView.Row2.Z;
			double determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
			double s = Math.Abs(determinant) > 1e-30 ? 1.0 / determinant : 0.0;
			// inverse[row][column]
			double i00 = (e * i - f * h) * s, i01 = (c * h - b * i) * s, i02 = (b * f - c * e) * s;
			double i10 = (f * g - d * i) * s, i11 = (a * i - c * g) * s, i12 = (c * d - a * f) * s;
			double i20 = (d * h - e * g) * s, i21 = (b * g - a * h) * s, i22 = (a * e - b * d) * s;
			data.Normal[0] = (float)i00; data.Normal[1] = (float)i10; data.Normal[2] = (float)i20;
			data.Normal[4] = (float)i01; data.Normal[5] = (float)i11; data.Normal[6] = (float)i21;
			data.Normal[8] = (float)i02; data.Normal[9] = (float)i12; data.Normal[10] = (float)i22;

			Matrix4D[] animation = state.Matricies;
			if (animation != null && animation.Length > 0)
			{
				// Pad0 marks an object with animation matrices: their base is moved when the chunks are merged.
				data.MatrixBase = chunk.MatrixCount;
				data.Pad0 = 1;
				data.MatrixCount = animation.Length;
				int needed = (chunk.MatrixCount + animation.Length) * 16;
				if (needed > chunk.Matrices.Length)
				{
					Array.Resize(ref chunk.Matrices, Math.Max(needed, chunk.Matrices.Length * 2));
				}

				fixed (float* to = chunk.Matrices)
				{
					for (int m = 0; m < animation.Length; m++)
					{
						Store(animation[m], to + (chunk.MatrixCount + m) * 16);
					}
				}

				chunk.MatrixCount += animation.Length;
			}

			chunk.Objects[chunk.ObjectCount] = data;
			return chunk.ObjectCount++;
		}

		private void AddDraw(Chunk chunk, DrawData draw, PassInfo pass, int passIndex, Blend blend, bool cull, uint firstIndex, uint indexCount, WorldMesh mesh, bool night, int shadow)
		{
			if (chunk.DrawCount == chunk.Draws.Length)
			{
				Array.Resize(ref chunk.Draws, chunk.Draws.Length * 2);
			}

			if (chunk.CommandCount == chunk.Commands.Length)
			{
				Array.Resize(ref chunk.Commands, chunk.Commands.Length * 2);
			}

			// Grouped passes cull in the pipeline; ordered ones draw both sides and discard OpenGL's front faces.
			bool pipelineCull = cull && !pass.Ordered;
			draw.CullFront = cull && pass.Ordered ? 1 : 0;
			int pipelineKey = (int)blend | (pass.DepthWrite ? 4 : 0) | (pipelineCull ? 8 : 0) | (pass.DepthTest ? 0 : 16);
			// Grouped by: nighttime draws after daytime ones (they blend over them), then pipeline, then (with pages) the
			// texture's page (Direct3D 11: pages of one, the texture), then vertex and index blocks.
			long page = TexturePage == 0 ? 0 : draw.Texture / TexturePage;
			long key = ((night ? 1L : 0L) << 62) | ((long)pipelineKey << 48) | ((page & 0xFFFF) << 32) | ((long)(mesh.VertexBlock & 0xFFFF) << 16) | (long)(mesh.IndexBlock & 0xFFFF);
			chunk.Draws[chunk.DrawCount] = draw;
			chunk.Commands[chunk.CommandCount++] = new DrawCommand { Key = key, FirstIndex = firstIndex, IndexCount = indexCount, VertexBase = mesh.VertexBase, Draw = chunk.DrawCount, Pass = passIndex, Shadow = shadow };
			chunk.DrawCount++;
		}

		/// <summary>The chunks' records joined in order, their indices moved to the joined arrays, and the passes' ranges found.</summary>
		private void Merge()
		{
			objectCount = drawCount = matrixCount = commandCount = 0;
			skippedFaces = 0;
			for (int c = 0; c < chunkCount; c++)
			{
				Chunk chunk = chunks[c];
				int objectBase = objectCount, drawBase = drawCount, matrixBase = matrixCount;
				Grow(ref objects, objectCount + chunk.ObjectCount);
				Grow(ref draws, drawCount + chunk.DrawCount);
				Grow(ref commands, commandCount + chunk.CommandCount);
				Grow(ref matrices, (matrixCount + chunk.MatrixCount) * 16);
				for (int k = 0; k < chunk.ObjectCount; k++)
				{
					ObjectData data = chunk.Objects[k];
					if (data.Pad0 != 0)
					{
						data.MatrixBase += matrixBase;
					}

					objects[objectCount++] = data;
				}

				Array.Copy(chunk.Matrices, 0, matrices, matrixCount * 16, chunk.MatrixCount * 16);
				matrixCount += chunk.MatrixCount;
				for (int k = 0; k < chunk.DrawCount; k++)
				{
					DrawData draw = chunk.Draws[k];
					draw.Object += objectBase;
					draws[drawCount++] = draw;
				}

				for (int k = 0; k < chunk.CommandCount; k++)
				{
					DrawCommand command = chunk.Commands[k];
					command.Draw += drawBase;
					commands[commandCount++] = command;
				}

				skippedFaces += chunk.Skipped;
				ObjectTicks += chunk.ObjectTicks;
			}

			segmentStarts.Clear();
			segmentOrdered.Clear();
			cabCommandStart = commandCount;
			int lastPass = -1;
			for (int i = 0; i < commandCount; i++)
			{
				if (commands[i].Pass != lastPass)
				{
					lastPass = commands[i].Pass;
					segmentStarts.Add(i);
					segmentOrdered.Add(passes[lastPass].Ordered);
					if (passes[lastPass].Layer == CabLayer && cabCommandStart == commandCount)
					{
						cabCommandStart = i;
					}
				}
			}
		}

		/// <summary>Where the cab layer's commands start among the merged ones (commandCount if none).</summary>
		protected int cabCommandStart;

		protected static void Grow<T>(ref T[] array, int needed)
		{
			if (needed > array.Length)
			{
				Array.Resize(ref array, Math.Max(needed, array.Length * 2));
			}
		}

		/// <summary>Whether a layer has anything to draw.</summary>
		internal bool Empty(int layer) => layer == WorldLayerIndex ? cabCommandStart == 0 : cabCommandStart == commandCount;

		/// <summary>
		/// The frame's draws in their order: grouped passes sorted by key, ordered ones kept as they are; and the shadow
		/// casters' draws, grouped by culling and buffer blocks.
		/// </summary>
		protected void Order()
		{
			// Grouped passes sorted by key; ordered ones kept as they are.
			for (int s = 0; s < segmentStarts.Count; s++)
			{
				int start = segmentStarts[s], end = s + 1 < segmentStarts.Count ? segmentStarts[s + 1] : commandCount;
				if (!segmentOrdered[s] && end - start > 1)
				{
					Array.Sort(commands, start, end - start, KeyComparer.Instance);
				}
			}

			// The shadow casters' draws, grouped by culling and buffer blocks, after the layers' commands.
			shadowCount = 0;
			if (shadowCascades > 0)
			{
				Grow(ref shadowCommands, cabCommandStart);
				for (int i = 0; i < cabCommandStart; i++)
				{
					if (commands[i].Shadow != 0)
					{
						DrawCommand command = commands[i];
						command.Key = ((long)command.Shadow << 48) | (command.Key & 0xFFFFFFFFFFFFL);
						shadowCommands[shadowCount++] = command;
					}
				}

				Array.Sort(shadowCommands, 0, shadowCount, KeyComparer.Instance);
			}

			LastDraws = commandCount;
			LastCabDraws = commandCount - cabCommandStart;
		}

		protected DrawCommand[] shadowCommands = new DrawCommand[1024];
		protected int shadowCount;

		/// <summary>Shadow casters drawn in the last frame (per cascade), for the log.</summary>
		internal int LastShadowDraws => shadowCount;

		protected sealed class KeyComparer : IComparer<DrawCommand>
		{
			internal static readonly KeyComparer Instance = new KeyComparer();
			public int Compare(DrawCommand x, DrawCommand y) => x.Key.CompareTo(y.Key);
		}

		/// <summary>Time making object records (matrices, on all threads) since the statistics were last taken, for the log.</summary>
		internal long ObjectTicks;

		/// <summary>Object records in the last frame, for the log.</summary>
		internal int LastObjects => objectCount;

		/// <summary>Faces skipped this frame because their mesh had no Vulkan copy, for the log.</summary>
		internal int SkippedFaces => skippedFaces;
	}
}
