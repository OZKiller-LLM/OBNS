// OBNS: not an upstream file. The Direct3D 9.0c world layer's frame (OBNS/DIRECT3D.md): the scene's records (WorldScene)
// as float4 texels of one float texture the shaders read by the draw's index (set per draw, as shader model 3 has no
// instance stream to give it), the frame's record in constant registers; each draw a DrawIndexedPrimitive, the render
// states set where the pipeline key changes. The shadow maps are the quarters of one R32F atlas (Shaders/world9.hlsl).

using System;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2;
using OpenBveApi.Graphics;
using Blend = OpenBve.Graphics.WorldBlend;

namespace OpenBve.Graphics.Direct3D9
{
	internal sealed unsafe class D3D9Scene : WorldScene
	{
		// The constant registers (world9.hlsl).
		private const uint FrameRegisters = 37, RecordsSizeRegister = 37, RecordBasesRegister = 38, PixelOffsetRegister = 39, DrawRegister = 40, ShadowAtlasRegister = 41;

		// The samplers: the vertex shaders' records; the pixel shaders' face texture, shadow atlas, records.
		private const uint FaceSampler = 0, ShadowSampler = 1, PixelRecordsSampler = 2;

		/// <summary>The records' texture's width in texels (records are found by index: x the remainder, y the rows).</summary>
		private const int RecordsWidth = 4096;

		// Texels a record takes (world9.hlsl's DrawTexel, ObjectTexel, transformVector).
		private const int DrawTexels = 6, ObjectTexels = 12, MatrixTexels = 4;

		private readonly D3D9Device device;
		private readonly D3D9Meshes meshes;
		private readonly D3D9Textures textures;

		protected override WorldTextures SceneTextures => textures;

		private IntPtr vertexShader, blendedShader, opaqueShader, shadowVertexShader, shadowPixelShader, declaration;

		/// <summary>The records' texture (dynamic: written whole each frame), its rows, and the texels written into it.</summary>
		private IntPtr records;
		private int recordsHeight;
		private float[] texels = new float[4 * 65536];
		private readonly float[] recordBases = new float[4];

		/// <summary>The layers' frame records as constant registers (their integers as floats).</summary>
		private readonly float[][] frameConstants = { new float[FrameRegisters * 4], new float[FrameRegisters * 4] };

		// The shadow pass: the atlas (a cascade a quarter; R32F, the depth as a colour) and its depth buffer.
		private IntPtr shadowAtlas, shadowSurface, shadowDepth;
		private int shadowResolution;

		// Upstream's texture filtering (the options), as Direct3D 9's sampler states.
		private uint minFilter, magFilter, mipFilter, maxAnisotropy;

		/// <summary>Pipeline and texture changes in the last frame, for the log.</summary>
		internal int LastStateChanges { get; private set; }

		internal D3D9Scene(D3D9Device device, D3D9Meshes meshes, D3D9Textures textures)
		{
			this.device = device;
			this.meshes = meshes;
			this.textures = textures;
			// Pages of one texture: the draws are grouped by their texture (it is bound per draw).
			TexturePage = 1;
			CreateShaders();
			ChooseFiltering(Interface.CurrentOptions.Interpolation, Interface.CurrentOptions.AnisotropicFilteringLevel);
			EnsureShadowMaps(1, 1);
		}

		private void CreateShaders()
		{
			string source;
			using (Stream stream = typeof(D3D9Scene).Assembly.GetManifestResourceStream("OBNS.Direct3D9.world9.hlsl"))
			{
				if (stream == null)
				{
					throw new InvalidOperationException("the Direct3D 9 shaders are missing from the program");
				}

				using (StreamReader reader = new StreamReader(stream))
				{
					source = reader.ReadToEnd();
				}
			}

			IntPtr d = device.Device;
			vertexShader = D9.CreateVertexShader(d, Direct3D11.Dx.Compile(source, "world9.hlsl", "VS", "vs_3_0", "OPAQUE_COVERAGE", "0"));
			blendedShader = D9.CreatePixelShader(d, Direct3D11.Dx.Compile(source, "world9.hlsl", "PS", "ps_3_0", "OPAQUE_COVERAGE", "0"));
			opaqueShader = D9.CreatePixelShader(d, Direct3D11.Dx.Compile(source, "world9.hlsl", "PS", "ps_3_0", "OPAQUE_COVERAGE", "1"));
			shadowVertexShader = D9.CreateVertexShader(d, Direct3D11.Dx.Compile(source, "world9.hlsl", "ShadowVS", "vs_3_0", "OPAQUE_COVERAGE", "0"));
			shadowPixelShader = D9.CreatePixelShader(d, Direct3D11.Dx.Compile(source, "world9.hlsl", "ShadowPS", "ps_3_0", "OPAQUE_COVERAGE", "0"));

			// LibRender2's vertex, as upstream's VAOs have it; the matrix chain's three integers read as four bytes each.
			D9.VertexElement* elements = stackalloc D9.VertexElement[8];
			elements[0] = Element(nameof(LibRenderVertex.Position), D9.DeclFloat3, D9.UsagePosition, 0, 0);
			elements[1] = Element(nameof(LibRenderVertex.Normal), D9.DeclFloat3, D9.UsageNormal, 0, 0);
			elements[2] = Element(nameof(LibRenderVertex.UV), D9.DeclFloat2, D9.UsageTexCoord, 0, 0);
			elements[3] = Element(nameof(LibRenderVertex.Color), D9.DeclFloat4, D9.UsageColor, 0, 0);
			for (int i = 0; i < 3; i++)
			{
				elements[4 + i] = Element(nameof(LibRenderVertex.MatrixChain), D9.DeclUByte4, D9.UsageBlendIndices, (byte)i, 4 * i);
			}

			elements[7] = new D9.VertexElement { Stream = 0xFF, Type = D9.DeclUnused };
			declaration = D9.CreateVertexDeclaration(d, elements);
		}

		private static D9.VertexElement Element(string field, byte type, byte usage, byte usageIndex, int extraOffset)
		{
			return new D9.VertexElement { Offset = (ushort)((int)Marshal.OffsetOf(typeof(LibRenderVertex), field) + extraOffset), Type = type, Usage = usage, UsageIndex = usageIndex };
		}

		/// <summary>The face textures' filtering, as upstream's options say (as the other layers').</summary>
		private void ChooseFiltering(InterpolationMode interpolation, int anisotropy)
		{
			minFilter = magFilter = mipFilter = D9.FilterLinear;
			maxAnisotropy = 1;
			switch (interpolation)
			{
				case InterpolationMode.NearestNeighbor:
					minFilter = magFilter = D9.FilterPoint;
					mipFilter = D9.FilterNone;
					break;
				case InterpolationMode.Bilinear:
					mipFilter = D9.FilterNone;
					break;
				case InterpolationMode.NearestNeighborMipmapped:
					minFilter = magFilter = mipFilter = D9.FilterPoint;
					break;
				case InterpolationMode.BilinearMipmapped:
					// As upstream: GL_NEAREST_MIPMAP_LINEAR.
					minFilter = D9.FilterPoint;
					break;
				case InterpolationMode.AnisotropicFiltering:
					if (anisotropy > 0)
					{
						minFilter = D9.FilterAnisotropic;
						maxAnisotropy = (uint)Math.Min(16, anisotropy);
					}

					break;
			}
		}

		private void PointSampler(uint sampler)
		{
			IntPtr d = device.Device;
			D9.SetSamplerState(d, sampler, D9.SamplerMinFilter, D9.FilterPoint);
			D9.SetSamplerState(d, sampler, D9.SamplerMagFilter, D9.FilterPoint);
			D9.SetSamplerState(d, sampler, D9.SamplerMipFilter, D9.FilterNone);
			D9.SetSamplerState(d, sampler, D9.SamplerAddressU, D9.AddressClamp);
			D9.SetSamplerState(d, sampler, D9.SamplerAddressV, D9.AddressClamp);
		}

		// --- shadow maps ---

		/// <summary>The atlas at a resolution (a quarter a cascade; 1x1 quarters when shadows are off), made again when it changes.</summary>
		public override void EnsureShadowMaps(int resolution, int cascades)
		{
			resolution = Math.Max(1, Math.Min(4096, resolution));
			if (shadowAtlas != IntPtr.Zero && resolution == shadowResolution)
			{
				return;
			}

			ReleaseShadowMaps();
			uint size = (uint)resolution * 2;
			shadowAtlas = D9.CreateTexture(device.Device, size, size, 1, D9.UsageRenderTarget, D9.FormatR32F, D9.PoolDefault);
			shadowSurface = D9.SurfaceLevel(shadowAtlas, 0);
			shadowDepth = D9.CreateDepthStencilSurface(device.Device, size, size, device.DepthFormat);
			shadowResolution = resolution;
		}

		private void ReleaseShadowMaps()
		{
			D9.Release(shadowSurface);
			D9.Release(shadowAtlas);
			D9.Release(shadowDepth);
			shadowSurface = shadowAtlas = shadowDepth = IntPtr.Zero;
		}

		// --- the frame ---

		/// <summary>Writes the frame's records into the records' texture and the layers' constants.</summary>
		internal void Upload()
		{
			Order();
			int total = drawCount * DrawTexels + objectCount * ObjectTexels + matrixCount * MatrixTexels;
			if (texels.Length < total * 4)
			{
				texels = new float[Math.Max(total * 4 + total * 2, texels.Length * 2)];
			}

			int at = 0;
			recordBases[0] = 0;
			fixed (DrawData* draw = draws)
			{
				for (int i = 0; i < drawCount; i++, at += DrawTexels * 4)
				{
					DrawData* d = draw + i;
					float* from = (float*)d;
					// Colour, specular, emission and shininess as they are; then the integers as floats.
					for (int k = 0; k < 12; k++)
					{
						texels[at + k] = from[k];
					}

					texels[at + 12] = d->Flags;
					texels[at + 13] = d->Brightness;
					texels[at + 14] = d->Opacity;
					texels[at + 15] = d->Texture;
					texels[at + 16] = d->Sampler;
					texels[at + 17] = d->AlphaFunction;
					texels[at + 18] = d->AlphaReference;
					texels[at + 19] = d->Object;
					texels[at + 20] = d->Fog;
					texels[at + 21] = d->CullFront;
					texels[at + 22] = texels[at + 23] = 0;
				}
			}

			recordBases[1] = at / 4;
			fixed (ObjectData* objectData = objects)
			{
				for (int i = 0; i < objectCount; i++, at += ObjectTexels * 4)
				{
					ObjectData* o = objectData + i;
					float* from = (float*)o;
					for (int k = 0; k < 44; k++)
					{
						texels[at + k] = from[k];
					}

					texels[at + 44] = o->MatrixBase;
					texels[at + 45] = o->Pad0;
					texels[at + 46] = o->MatrixCount;
					texels[at + 47] = 0;
				}
			}

			recordBases[2] = at / 4;
			Array.Copy(matrices, 0, texels, at, matrixCount * 16);
			at += matrixCount * 16;
			WriteRecords(at / 4);

			for (int layer = 0; layer < 2; layer++)
			{
				fixed (FrameData* frame = &frameData[layer])
				fixed (float* to = frameConstants[layer])
				{
					float* from = (float*)frame;
					for (int k = 0; k < FrameRegisters * 4; k++)
					{
						to[k] = from[k];
					}

					// The frame's integers (fog is linear; lighting, shadows, cascades, smooth shadows) as floats.
					to[43] = frame->FogIsLinear;
					to[44] = frame->IsLight;
					to[45] = frame->ShadowEnabled;
					to[46] = frame->ShadowCascades;
					to[47] = frame->ShadowSmooth;
				}
			}
		}

		/// <summary>The texels into the records' texture (made taller when the frame needs more rows).</summary>
		private void WriteRecords(int count)
		{
			int rows = Math.Max(1, (count + RecordsWidth - 1) / RecordsWidth);
			if (records == IntPtr.Zero || rows > recordsHeight)
			{
				D9.Release(records);
				recordsHeight = Math.Max(64, rows + rows / 2);
				records = D9.CreateTexture(device.Device, RecordsWidth, (uint)recordsHeight, 1, D9.UsageDynamic, D9.FormatA32B32G32R32F, D9.PoolDefault);
			}

			D9.LockedRect locked = D9.LockRect(records, 0, D9.LockDiscard);
			fixed (float* from = texels)
			{
				for (int row = 0; row < rows; row++)
				{
					int first = row * RecordsWidth, length = Math.Min(RecordsWidth, count - first);
					if (length > 0)
					{
						Buffer.MemoryCopy(from + first * 4, locked.Bits + (long)row * locked.Pitch, locked.Pitch, length * 16L);
					}
				}
			}

			D9.UnlockRect(records, 0);
		}

		private void SetConstant(uint register, float x, float y, float z, float w)
		{
			float* value = stackalloc float[4];
			value[0] = x;
			value[1] = y;
			value[2] = z;
			value[3] = w;
			D9.SetVertexConstants(device.Device, register, value, 1);
			D9.SetPixelConstants(device.Device, register, value, 1);
		}

		/// <summary>What every draw of a pass shares: the shaders' inputs, the records, the layer's frame and the pixel size.</summary>
		private void Bind(int layer, float cascade, uint width, uint height)
		{
			IntPtr d = device.Device;
			D9.SetVertexDeclaration(d, declaration);
			D9.SetTexture(d, D9.VertexSampler0, records);
			D9.SetTexture(d, PixelRecordsSampler, records);
			PointSampler(D9.VertexSampler0);
			PointSampler(PixelRecordsSampler);
			fixed (float* frame = frameConstants[layer])
			{
				D9.SetVertexConstants(d, 0, frame, FrameRegisters);
				D9.SetPixelConstants(d, 0, frame, FrameRegisters);
			}

			SetConstant(RecordsSizeRegister, RecordsWidth, 1.0f / RecordsWidth, 1.0f / recordsHeight, 0);
			SetConstant(RecordBasesRegister, recordBases[0], recordBases[1], recordBases[2], cascade);
			SetConstant(PixelOffsetRegister, 1.0f / width, 1.0f / height, 0, 0);
			SetConstant(ShadowAtlasRegister, shadowResolution, 1.0f / (2 * shadowResolution), 0, 0);
			D9.SetSamplerState(d, FaceSampler, D9.SamplerMinFilter, minFilter);
			D9.SetSamplerState(d, FaceSampler, D9.SamplerMagFilter, magFilter);
			D9.SetSamplerState(d, FaceSampler, D9.SamplerMipFilter, mipFilter);
			D9.SetSamplerState(d, FaceSampler, D9.SamplerMaxAnisotropy, maxAnisotropy);
		}

		/// <summary>
		/// The shadow pass (after Upload, before the world layer): the atlas cleared, and each cascade's casters drawn into
		/// its quarter in the cascade's light space, as upstream's Shadows.RenderPass; with shadows off, only cleared.
		/// </summary>
		internal void DrawShadows()
		{
			IntPtr d = device.Device;
			// The atlas is read by the world's pixel shader: not while it is drawn into.
			D9.SetTexture(d, ShadowSampler, IntPtr.Zero);
			D9.SetRenderTarget(d, 0, shadowSurface);
			D9.SetRenderTarget(d, 1, IntPtr.Zero);
			D9.SetDepthStencilSurface(d, shadowDepth);
			D9.Clear(d, D9.ClearTarget | D9.ClearZBuffer, 0xFFFFFFFF, 1.0f);
			if (shadowCascades == 0 || shadowCount == 0)
			{
				return;
			}

			uint size = (uint)shadowResolution;
			D9.SetVertexShader(d, shadowVertexShader);
			D9.SetPixelShader(d, shadowPixelShader);
			D9.SetRenderState(d, D9.StateAlphaBlendEnable, 0);
			D9.SetRenderState(d, D9.StateZEnable, 1);
			D9.SetRenderState(d, D9.StateZWriteEnable, 1);
			D9.SetRenderState(d, D9.StateZFunc, D9.CompareLess);
			for (int cascade = 0; cascade < Math.Min(shadowCascades, 4); cascade++)
			{
				D9.SetViewport(d, (uint)(cascade & 1) * size, (uint)(cascade >> 1) * size, size, size);
				Bind(WorldLayerIndex, cascade, size, size);
				int boundCull = -1, boundTexture = -1, boundSampler = -1, boundVertexBlock = -1, boundIndexBlock = -1;
				for (int i = shadowFirst[cascade], end = shadowFirst[cascade] + shadowLength[cascade]; i < end; i++)
				{
					DrawCommand command = shadowCommands[i];
					int cull = (int)(command.Key >> 48) == 1 ? 1 : 0;
					if (cull != boundCull)
					{
						D9.SetRenderState(d, D9.StateCullMode, cull == 1 ? D9.CullClockwise : D9.CullNone);
						boundCull = cull;
					}

					BindFace(command, ref boundTexture, ref boundSampler, ref boundVertexBlock, ref boundIndexBlock);
					DrawCommandNow(command);
				}
			}
		}

		/// <summary>A draw's texture, sampler and buffers, where they change; true if the texture did.</summary>
		private bool BindFace(DrawCommand command, ref int boundTexture, ref int boundSampler, ref int boundVertexBlock, ref int boundIndexBlock)
		{
			IntPtr d = device.Device;
			int texture = draws[command.Draw].Texture, sampler = draws[command.Draw].Sampler & 3;
			int vertexBlock = (int)((command.Key >> 16) & 0xFFFF), indexBlock = (int)(command.Key & 0xFFFF);
			bool changed = false;
			if (texture != boundTexture)
			{
				D9.SetTexture(d, FaceSampler, textures.Texture(texture));
				boundTexture = texture;
				changed = true;
			}

			if (sampler != boundSampler)
			{
				// OpenGlTextureWrapMode: RepeatClamp (2) repeats S, ClampRepeat (1) repeats T.
				D9.SetSamplerState(d, FaceSampler, D9.SamplerAddressU, (sampler & 2) != 0 ? D9.AddressWrap : D9.AddressClamp);
				D9.SetSamplerState(d, FaceSampler, D9.SamplerAddressV, (sampler & 1) != 0 ? D9.AddressWrap : D9.AddressClamp);
				boundSampler = sampler;
			}

			if (vertexBlock != boundVertexBlock)
			{
				D9.SetStreamSource(d, 0, meshes.Vertices.Buffers[vertexBlock], (uint)WorldMeshes.VertexSize);
				boundVertexBlock = vertexBlock;
			}

			if (indexBlock != boundIndexBlock)
			{
				D9.SetIndices(d, meshes.Indices.Buffers[indexBlock]);
				boundIndexBlock = indexBlock;
			}

			return changed;
		}

		/// <summary>A draw: its record's index set for the vertex shader, its triangles drawn.</summary>
		private void DrawCommandNow(DrawCommand command)
		{
			IntPtr d = device.Device;
			float* index = stackalloc float[4];
			index[0] = command.Draw;
			index[1] = index[2] = index[3] = 0;
			D9.SetVertexConstants(d, DrawRegister, index, 1);
			// The vertices the indices may name: the block's from the draw's base vertex on.
			int vertexBlock = (int)((command.Key >> 16) & 0xFFFF);
			uint vertices = (uint)(meshes.Vertices.Sizes[vertexBlock] / (ulong)WorldMeshes.VertexSize) - (uint)command.VertexBase;
			D9.DrawIndexedTriangles(d, command.VertexBase, vertices, command.FirstIndex, command.IndexCount / 3);
		}

		/// <summary>
		/// Draws a layer's draws: into the colour target and depth buffer set, and for the world layer the depth as a colour
		/// (depthTarget), bound for the draws that write depth.
		/// </summary>
		internal void Draw(int layer, uint width, uint height, IntPtr depthTarget)
		{
			int start = layer == WorldLayerIndex ? 0 : cabCommandStart, end = layer == WorldLayerIndex ? cabCommandStart : commandCount;
			if (layer == WorldLayerIndex)
			{
				LastStateChanges = 0;
			}

			if (start >= end)
			{
				return;
			}

			IntPtr d = device.Device;
			D9.SetViewport(d, 0, 0, width, height);
			Bind(layer, 0, width, height);
			D9.SetTexture(d, ShadowSampler, shadowAtlas);
			PointSampler(ShadowSampler);
			D9.SetVertexShader(d, vertexShader);
			bool depthBound = false;
			int boundPipeline = -1, boundTexture = -1, boundSampler = -1, boundVertexBlock = -1, boundIndexBlock = -1, changes = 0;
			for (int i = start; i < end; i++)
			{
				DrawCommand command = commands[i];
				int pipelineKey = (int)((command.Key >> 48) & 0x3FFF);
				if (pipelineKey != boundPipeline)
				{
					SetPipeline(pipelineKey & 31);
					Blend blend = (Blend)(pipelineKey & 3);
					// The depth as a colour, written where depth is: bound for those draws (blended ones where R32F can be blended).
					bool bind = layer == WorldLayerIndex && (pipelineKey & 4) != 0 && (blend == Blend.None || device.FloatBlending);
					if (bind != depthBound)
					{
						D9.SetRenderTarget(d, 1, bind ? depthTarget : IntPtr.Zero);
						depthBound = bind;
					}

					boundPipeline = pipelineKey;
					changes++;
				}

				if (BindFace(command, ref boundTexture, ref boundSampler, ref boundVertexBlock, ref boundIndexBlock))
				{
					changes++;
				}

				DrawCommandNow(command);
			}

			if (depthBound)
			{
				D9.SetRenderTarget(d, 1, IntPtr.Zero);
			}

			LastStateChanges += changes;
		}

		/// <summary>The render states and pixel shader of a pipeline key: blending (2 bits), depth writes (4), culling (8), no depth test (16).</summary>
		private void SetPipeline(int key)
		{
			IntPtr d = device.Device;
			Blend blend = (Blend)(key & 3);
			bool depthWrite = (key & 4) != 0, cull = (key & 8) != 0, depthTest = (key & 16) == 0;
			D9.SetPixelShader(d, blend == Blend.None ? opaqueShader : blendedShader);
			// Colours blend as upstream's, alpha accumulating coverage for the composite.
			D9.SetRenderState(d, D9.StateAlphaBlendEnable, blend == Blend.None ? 0u : 1u);
			if (blend != Blend.None)
			{
				D9.SetRenderState(d, D9.StateSeparateAlphaBlendEnable, 1);
				D9.SetRenderState(d, D9.StateBlendOp, D9.BlendOpAdd);
				D9.SetRenderState(d, D9.StateBlendOpAlpha, D9.BlendOpAdd);
				D9.SetRenderState(d, D9.StateSrcBlend, D9.BlendSrcAlpha);
				D9.SetRenderState(d, D9.StateDestBlend, blend == Blend.Normal ? D9.BlendInvSrcAlpha : D9.BlendOne);
				D9.SetRenderState(d, D9.StateSrcBlendAlpha, blend == Blend.Normal ? D9.BlendOne : D9.BlendZero);
				D9.SetRenderState(d, D9.StateDestBlendAlpha, blend == Blend.Normal ? D9.BlendInvSrcAlpha : D9.BlendOne);
			}

			// Without a depth test, depth writes are off as well (Direct3D, like OpenGL, writes none then).
			D9.SetRenderState(d, D9.StateZEnable, depthTest ? 1u : 0u);
			D9.SetRenderState(d, D9.StateZWriteEnable, depthWrite && depthTest ? 1u : 0u);
			D9.SetRenderState(d, D9.StateZFunc, D9.CompareLessEqual);
			// Upstream culls OpenGL's front faces; drawn upside down, those are clockwise.
			D9.SetRenderState(d, D9.StateCullMode, cull ? D9.CullClockwise : D9.CullNone);
		}

		/// <summary>Shadow casters drawn in the last frame (per cascade), for the log.</summary>

		/// <summary>The device's states the layer never changes, set off (the fixed-function ones).</summary>
		internal void SetFixedStates()
		{
			IntPtr d = device.Device;
			D9.SetRenderState(d, D9.StateLighting, 0);
			D9.SetRenderState(d, D9.StateAlphaTestEnable, 0);
			D9.SetRenderState(d, D9.StateStencilEnable, 0);
			D9.SetRenderState(d, D9.StateScissorTestEnable, 0);
		}

		public override void Dispose()
		{
			ReleaseShadowMaps();
			foreach (IntPtr com in new[] { vertexShader, blendedShader, opaqueShader, shadowVertexShader, shadowPixelShader, declaration, records })
			{
				D9.Release(com);
			}

			vertexShader = blendedShader = opaqueShader = shadowVertexShader = shadowPixelShader = declaration = records = IntPtr.Zero;
		}
	}
}
