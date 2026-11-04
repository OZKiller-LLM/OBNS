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

// OBNS: not an upstream file. The 2D part of a frame and its particles drawn by Direct3D 12 (OBNS/RENDERERS.md), with Direct3D 11's
// shaders (Direct3D11/Shaders/frame2d.hlsl). The quads are in upload memory, a buffer per frame in flight.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2.Frame;
using Dx = OpenBve.Graphics.Direct3D11.Dx;

namespace OpenBve.Graphics.Direct3D12
{
	internal sealed unsafe class D3D12FrameDrawing : WholeFrameDrawing, IDisposable
	{
		private const uint RootTexture = 0, RootSampler = 1;

		private readonly D3D12Device device;
		private readonly D3D12Scene scene;
		private readonly D3D12Textures textures;
		private readonly IntPtr rootSignature;
		private readonly byte[] vertexCode, pixelCode;

		/// <summary>The pipelines by blending, for drawing with no depth; and the two for particles, tested against the world's depth.</summary>
		private readonly IntPtr[] pipelines = new IntPtr[4];
		private readonly IntPtr opaqueParticles, blendedParticles;

		// The quads of each frame in flight, and buffers replaced while it was being recorded (let go of when that frame is drawn).
		private readonly IntPtr[] buffers = new IntPtr[D3D12Scene.FramesInFlight];
		private readonly byte*[] data = new byte*[D3D12Scene.FramesInFlight];
		private readonly int[] capacities = new int[D3D12Scene.FramesInFlight];
		private readonly List<IntPtr>[] replaced = new List<IntPtr>[D3D12Scene.FramesInFlight];
		private int slot, used;

		protected override WorldTextures Textures => textures;

		internal D3D12FrameDrawing(D3D12Device device, D3D12Scene scene, D3D12Textures textures, IFrameDrawing openGl) : base(openGl)
		{
			this.device = device;
			this.scene = scene;
			this.textures = textures;
			for (int i = 0; i < replaced.Length; i++)
			{
				replaced[i] = new List<IntPtr>();
			}

			string source;
			using (Stream stream = typeof(D3D12FrameDrawing).Assembly.GetManifestResourceStream("OBNS.Direct3D11.frame2d.hlsl"))
			{
				if (stream == null)
				{
					throw new InvalidOperationException("the Direct3D 2D shaders are missing from the program");
				}

				using (StreamReader reader = new StreamReader(stream))
				{
					source = reader.ReadToEnd();
				}
			}

			vertexCode = Dx.Compile(source, "frame2d.hlsl", "VS", "vs_5_0");
			pixelCode = Dx.Compile(source, "frame2d.hlsl", "PS", "ps_5_0");

			// The quad's texture (t0) and sampler (s0), by tables.
			D12.DescriptorRange* ranges = stackalloc D12.DescriptorRange[2];
			ranges[0] = new D12.DescriptorRange { RangeType = D12.RangeShaderResources, NumDescriptors = 1, BaseShaderRegister = 0 };
			ranges[1] = new D12.DescriptorRange { RangeType = D12.RangeSamplers, NumDescriptors = 1, BaseShaderRegister = 0 };
			D12.RootParameter* parameters = stackalloc D12.RootParameter[2];
			parameters[RootTexture] = new D12.RootParameter { ParameterType = D12.RootTable, NumDescriptorRanges = 1, DescriptorRanges = ranges, ShaderVisibility = D12.VisiblePixel };
			parameters[RootSampler] = new D12.RootParameter { ParameterType = D12.RootTable, NumDescriptorRanges = 1, DescriptorRanges = ranges + 1, ShaderVisibility = D12.VisiblePixel };
			rootSignature = D12.CreateRootSignature(device.Device, D12.SerializeRootSignature(new D12.RootSignatureDesc { NumParameters = 2, Parameters = parameters, Flags = D12.RootAllowInputLayout }));

			pipelines[(int)Blend.None] = MakePipeline(Blend.None, false, false);
			pipelines[(int)Blend.Normal] = MakePipeline(Blend.Normal, false, false);
			pipelines[(int)Blend.GlyphMask] = MakePipeline(Blend.GlyphMask, false, false);
			pipelines[(int)Blend.GlyphAdd] = MakePipeline(Blend.GlyphAdd, false, false);
			opaqueParticles = MakePipeline(Blend.None, true, true);
			blendedParticles = MakePipeline(Blend.Normal, true, false);
		}

		private static void Factors(Blend blend, out uint source, out uint destination)
		{
			switch (blend)
			{
				case Blend.Normal:
					source = Dx.BlendSrcAlpha;
					destination = Dx.BlendInvSrcAlpha;
					break;
				case Blend.GlyphMask:
					source = Dx.BlendZero;
					destination = Dx.BlendInvSrcColor;
					break;
				case Blend.GlyphAdd:
					source = Dx.BlendSrcAlpha;
					destination = Dx.BlendOne;
					break;
				default:
					source = Dx.BlendOne;
					destination = Dx.BlendZero;
					break;
			}
		}

		private IntPtr MakePipeline(Blend blend, bool depthTest, bool depthWrite)
		{
			string[] names = { "POSITION", "TEXCOORD", "COLOR", "TEXCOORD", "DEPTHVALUE", "FOGVALUE" };
			uint[] indices = { 0, 0, 0, 1, 0, 0 };
			uint[] formats = { Dx.FormatR32G32B32A32Float, Dx.FormatR32G32B32A32Float, Dx.FormatR32G32B32A32Float, Dx.FormatR32G32Float, Dx.FormatR32Float, Dx.FormatR32G32B32A32Float };
			uint[] offsets = { 0, 16, 32, 48, 64, 68 };
			IntPtr[] nameTexts = new IntPtr[names.Length];
			try
			{
				Dx.InputElementDesc* elements = stackalloc Dx.InputElementDesc[names.Length];
				for (int i = 0; i < names.Length; i++)
				{
					nameTexts[i] = Marshal.StringToHGlobalAnsi(names[i]);
					elements[i] = new Dx.InputElementDesc { SemanticName = nameTexts[i], SemanticIndex = indices[i], Format = formats[i], AlignedByteOffset = offsets[i], InputSlotClass = Dx.InputPerInstance, InstanceDataStepRate = 1 };
				}

				Factors(blend, out uint source, out uint destination);
				D12.GraphicsPipelineStateDesc desc = new D12.GraphicsPipelineStateDesc
				{
					RootSignature = rootSignature,
					InputElements = elements,
					NumInputElements = (uint)names.Length,
					PrimitiveTopologyType = D12.TopologyTypeTriangle,
					SampleMask = 0xFFFFFFFF,
					SampleCount = 1,
					FillMode = Dx.FillSolid,
					CullMode = Dx.CullNone,
					DepthClipEnable = 1,
					DepthEnable = depthTest ? 1 : 0,
					DepthWriteMask = depthWrite ? 1u : 0u,
					DepthFunc = Dx.ComparisonLessEqual,
					StencilReadMask = 0xFF,
					StencilWriteMask = 0xFF,
					NumRenderTargets = 1,
					DSVFormat = depthTest ? Dx.FormatD32Float : 0,
					Target0 = new D12.RenderTargetBlendDesc
					{
						BlendEnable = blend == Blend.None ? 0 : 1,
						SrcBlend = source,
						DestBlend = destination,
						BlendOp = Dx.BlendOpAdd,
						// OpenGL's blend functions apply to alpha alike; Direct3D has no colour factors there, and the source's alpha is what OpenGL's come to.
						SrcBlendAlpha = source,
						DestBlendAlpha = destination == Dx.BlendInvSrcColor ? Dx.BlendInvSrcAlpha : destination,
						BlendOpAlpha = Dx.BlendOpAdd,
						LogicOp = D12.LogicNoop,
						RenderTargetWriteMask = 0xF
					}
				};
				desc.RTVFormats[0] = Dx.FormatR8G8B8A8Unorm;
				D12.StencilOpDesc keep = new D12.StencilOpDesc { StencilFailOp = Dx.StencilOpKeep, StencilDepthFailOp = Dx.StencilOpKeep, StencilPassOp = Dx.StencilOpKeep, StencilFunc = Dx.ComparisonAlways };
				desc.FrontFace = desc.BackFace = keep;
				fixed (byte* vs = vertexCode, ps = pixelCode)
				{
					desc.VS = new D12.ShaderBytecode { Code = vs, Length = (UIntPtr)vertexCode.Length };
					desc.PS = new D12.ShaderBytecode { Code = ps, Length = (UIntPtr)pixelCode.Length };
					return D12.CreateGraphicsPipelineState(device.Device, &desc);
				}
			}
			finally
			{
				foreach (IntPtr text in nameTexts)
				{
					Marshal.FreeHGlobal(text);
				}
			}
		}

		/// <summary>The frame in flight being recorded (the GPU has drawn the one that had its buffer last).</summary>
		internal void BeginFrame(int frameSlot)
		{
			slot = frameSlot;
			used = 0;
			foreach (IntPtr buffer in replaced[slot])
			{
				D12.Release(buffer);
			}

			replaced[slot].Clear();
		}

		/// <summary>Writes quads into the frame's upload memory, and sets them as the input.</summary>
		private void Write(IntPtr list, List<Quad> quadList)
		{
			if (used + quadList.Count > capacities[slot])
			{
				if (buffers[slot] != IntPtr.Zero)
				{
					// The frame's list may be drawing from it already.
					replaced[slot].Add(buffers[slot]);
				}

				int capacity = Math.Max((used + quadList.Count) * 2, 4096);
				D12.ResourceDesc desc = new D12.ResourceDesc { Dimension = D12.DimensionBuffer, Width = (ulong)capacity * (ulong)sizeof(QuadData), Height = 1, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, Layout = D12.LayoutRowMajor };
				buffers[slot] = D12.CreateCommittedResource(device.Device, D12.HeapUpload, D12.HeapFlagNone, desc, D12.StateGenericRead);
				data[slot] = D12.Map(buffers[slot]);
				capacities[slot] = capacity;
				used = 0;
			}

			QuadData* mapped = (QuadData*)data[slot] + used;
			for (int i = 0; i < quadList.Count; i++)
			{
				mapped[i] = quadList[i].Data;
			}

			D12.IASetVertexBuffer(list, 0, new D12.VertexBufferView { BufferLocation = D12.GpuAddress(buffers[slot]) + (ulong)used * (ulong)sizeof(QuadData), SizeInBytes = (uint)(quadList.Count * sizeof(QuadData)), StrideInBytes = (uint)sizeof(QuadData) });
			used += quadList.Count;
		}

		private void Bind(IntPtr list, uint width, uint height)
		{
			D12.SetDescriptorHeaps(list, textures.Heap, scene.SamplerHeap);
			D12.SetGraphicsRootSignature(list, rootSignature);
			D12.IASetPrimitiveTopology(list, Dx.TopologyTriangleList);
			D12.SetViewport(list, width, height);
		}

		/// <summary>The 2D quads (HUD, menus, panel) the last frame drew, for the frame statistics.</summary>
		internal int LastQuads { get; private set; }

		/// <summary>Draws the frame's quads into the target set (no depth), runs of the same texture and blending together.</summary>
		internal void Record(IntPtr list, uint width, uint height)
		{
			LastQuads = quads.Count;
			if (quads.Count == 0)
			{
				return;
			}

			Bind(list, width, height);
			Write(list, quads);
			Blend boundBlend = (Blend)(-1);
			int run = 0;
			while (run < quads.Count)
			{
				Quad first = quads[run];
				int next = run + 1;
				while (next < quads.Count && quads[next].Blend == first.Blend && quads[next].Data.Texture == first.Data.Texture && quads[next].Data.Sampler == first.Data.Sampler)
				{
					next++;
				}

				if (first.Blend != boundBlend)
				{
					D12.SetPipelineState(list, pipelines[(int)first.Blend]);
					boundBlend = first.Blend;
				}

				D12.SetGraphicsRootDescriptorTable(list, RootTexture, textures.View(first.Data.Texture));
				D12.SetGraphicsRootDescriptorTable(list, RootSampler, scene.SamplerView(first.Data.Sampler));
				D12.DrawInstanced(list, 6, (uint)(next - run), 0, (uint)run);
				run = next;
			}
		}

		/// <summary>Draws the frame's particles into the target and world depth set: opaque texels writing depth, then blended ones not.</summary>
		internal void RecordParticles(IntPtr list, uint width, uint height)
		{
			if (particles.Count == 0)
			{
				return;
			}

			Bind(list, width, height);
			Write(list, particles);
			for (int i = 0; i < particles.Count; i++)
			{
				D12.SetPipelineState(list, particles[i].Blend == Blend.None ? opaqueParticles : blendedParticles);
				D12.SetGraphicsRootDescriptorTable(list, RootTexture, textures.View(particles[i].Data.Texture));
				D12.SetGraphicsRootDescriptorTable(list, RootSampler, scene.SamplerView(particles[i].Data.Sampler));
				D12.DrawInstanced(list, 6, 1, 0, (uint)i);
			}
		}

		private readonly List<Quad> single = new List<Quad>();

		/// <summary>A whole image laid over the target set (no depth): with the given alpha and blending, and its rows running as asked.</summary>
		internal void DrawImage(IntPtr list, ulong view, uint width, uint height, float alpha, bool blended, bool upsideDown)
		{
			single.Clear();
			single.Add(new Quad
			{
				Data = new QuadData { X = -1.0f, Y = -1.0f, Width = 2.0f, Height = 2.0f, UWidth = 1.0f, V = upsideDown ? 1.0f : 0.0f, VHeight = upsideDown ? -1.0f : 1.0f, R = 1.0f, G = 1.0f, B = 1.0f, A = alpha }
			});
			Bind(list, width, height);
			Write(list, single);
			D12.SetPipelineState(list, pipelines[(int)(blended ? Blend.Normal : Blend.None)]);
			D12.SetGraphicsRootDescriptorTable(list, RootTexture, view);
			D12.SetGraphicsRootDescriptorTable(list, RootSampler, scene.SamplerView(0));
			D12.DrawInstanced(list, 6, 1, 0, 0);
		}

		public void Dispose()
		{
			foreach (IntPtr pipeline in pipelines)
			{
				D12.Release(pipeline);
			}

			D12.Release(opaqueParticles);
			D12.Release(blendedParticles);
			D12.Release(rootSignature);
			for (int i = 0; i < buffers.Length; i++)
			{
				D12.Release(buffers[i]);
				foreach (IntPtr buffer in replaced[i])
				{
					D12.Release(buffer);
				}

				replaced[i].Clear();
			}
		}
	}
}
