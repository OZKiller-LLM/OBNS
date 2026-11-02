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

// OBNS: not an upstream file. The 2D part of a frame and its particles drawn by Direct3D 11 (OBNS/RENDERERS.md), and the swap chain.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2.Frame;

namespace OpenBve.Graphics.Direct3D11
{
	internal static unsafe partial class Dx
	{
		internal const uint BlendInvSrcColor = 4, UsageRenderTargetOutput = 0x20, MapRead = 1;

		/// <summary>DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING and DXGI_PRESENT_ALLOW_TEARING.</summary>
		internal const uint SwapChainAllowTearing = 2048, PresentAllowTearing = 0x200;

		[StructLayout(LayoutKind.Sequential)]
		internal struct SwapChainDesc
		{
			public uint Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanlineOrdering, Scaling;
			public uint SampleCount, SampleQuality;
			public uint BufferUsage, BufferCount;
			public IntPtr OutputWindow;
			public int Windowed;
			public uint SwapEffect, Flags;
		}

		private static readonly Guid ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
		private static readonly Guid IDXGIFactory5 = new Guid("7632e1f5-ee65-4dca-87fd-84cd75f8838d");

		/// <summary>
		/// Whether presenting can skip the wait for a refresh (IDXGIFactory5::CheckFeatureSupport, 28). A flip swap chain without it can only show
		/// as many frames a refresh as it has buffers to spare, whatever the sync interval.
		/// </summary>
		internal static bool TearingAllowed(IntPtr factory)
		{
			Guid id = IDXGIFactory5;
			IntPtr factory5;
			if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Table(factory)[0])(factory, &id, &factory5) < 0)
			{
				return false;
			}

			int allowed = 0;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int*, uint, int>)Table(factory5)[28])(factory5, 0, &allowed, sizeof(int));
			Release(factory5);
			return hr >= 0 && allowed != 0;
		}

		/// <summary>IDXGIFactory::MakeWindowAssociation (8).</summary>
		internal static void MakeWindowAssociation(IntPtr factory, IntPtr window, uint flags) => Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Table(factory)[8])(factory, window, flags), "IDXGIFactory::MakeWindowAssociation");

		/// <summary>IDXGIFactory::CreateSwapChain (10).</summary>
		internal static int CreateSwapChain(IntPtr factory, IntPtr device, SwapChainDesc desc, out IntPtr swapChain)
		{
			IntPtr made;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, SwapChainDesc*, IntPtr*, int>)Table(factory)[10])(factory, device, &desc, &made);
			swapChain = hr < 0 ? IntPtr.Zero : made;
			return hr;
		}

		/// <summary>IDXGISwapChain::Present (8).</summary>
		internal static int Present(IntPtr swapChain, uint syncInterval, uint flags) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Table(swapChain)[8])(swapChain, syncInterval, flags);

		/// <summary>IDXGISwapChain::GetBuffer (9), as a texture.</summary>
		internal static IntPtr GetBuffer(IntPtr swapChain)
		{
			Guid id = ID3D11Texture2D;
			IntPtr buffer;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)Table(swapChain)[9])(swapChain, 0, &id, &buffer), "IDXGISwapChain::GetBuffer");
			return buffer;
		}

		/// <summary>IDXGISwapChain::ResizeBuffers (13), keeping their number and format.</summary>
		internal static void ResizeBuffers(IntPtr swapChain, uint width, uint height, uint flags) => Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int>)Table(swapChain)[13])(swapChain, 0, width, height, 0, flags), "IDXGISwapChain::ResizeBuffers");

		/// <summary>ID3D11DeviceContext::DrawInstanced (21).</summary>
		internal static void DrawInstanced(IntPtr context, uint vertices, uint instances, uint startVertex, uint startInstance) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, void>)Table(context)[21])(context, vertices, instances, startVertex, startInstance);

		/// <summary>ID3D11DeviceContext::CopyResource (47).</summary>
		internal static void CopyResource(IntPtr context, IntPtr destination, IntPtr source) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Table(context)[47])(context, destination, source);

		/// <summary>ID3D11DeviceContext::Map (14), for reading, with the rows' pitch.</summary>
		internal static void* MapForReading(IntPtr context, IntPtr resource, out uint rowPitch)
		{
			MappedSubresource mapped;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)Table(context)[14])(context, resource, 0, MapRead, 0, &mapped), "ID3D11DeviceContext::Map");
			rowPitch = mapped.RowPitch;
			return mapped.Data;
		}
	}

	internal sealed unsafe class D3D11FrameDrawing : WholeFrameDrawing, IDisposable
	{
		private readonly D3D11Device device;
		private readonly D3D11Scene scene;
		private readonly D3D11Textures textures;
		private readonly IntPtr vertexShader, pixelShader, inputLayout;
		private readonly IntPtr[] blendStates = new IntPtr[4];
		private IntPtr instances;
		private int instanceCapacity;

		protected override WorldTextures Textures => textures;

		internal D3D11FrameDrawing(D3D11Device device, D3D11Scene scene, D3D11Textures textures, IFrameDrawing openGl) : base(openGl)
		{
			this.device = device;
			this.scene = scene;
			this.textures = textures;
			string source;
			using (Stream stream = typeof(D3D11FrameDrawing).Assembly.GetManifestResourceStream("OBNS.Direct3D11.frame2d.hlsl"))
			{
				if (stream == null)
				{
					throw new InvalidOperationException("the Direct3D 11 2D shaders are missing from the program");
				}

				using (StreamReader reader = new StreamReader(stream))
				{
					source = reader.ReadToEnd();
				}
			}

			byte[] vertexCode = Dx.Compile(source, "frame2d.hlsl", "VS", "vs_5_0");
			vertexShader = Dx.CreateVertexShader(device.Device, vertexCode);
			pixelShader = Dx.CreatePixelShader(device.Device, Dx.Compile(source, "frame2d.hlsl", "PS", "ps_5_0"));
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

				inputLayout = Dx.CreateInputLayout(device.Device, elements, (uint)names.Length, vertexCode);
			}
			finally
			{
				foreach (IntPtr text in nameTexts)
				{
					Marshal.FreeHGlobal(text);
				}
			}

			blendStates[(int)Blend.None] = MakeBlend(false, Dx.BlendOne, Dx.BlendZero);
			blendStates[(int)Blend.Normal] = MakeBlend(true, Dx.BlendSrcAlpha, Dx.BlendInvSrcAlpha);
			blendStates[(int)Blend.GlyphMask] = MakeBlend(true, Dx.BlendZero, Dx.BlendInvSrcColor);
			blendStates[(int)Blend.GlyphAdd] = MakeBlend(true, Dx.BlendSrcAlpha, Dx.BlendOne);
		}

		private IntPtr MakeBlend(bool enabled, uint source, uint destination)
		{
			// OpenGL's blend functions apply to alpha alike.
			Dx.RenderTargetBlendDesc target = new Dx.RenderTargetBlendDesc { BlendEnable = enabled ? 1 : 0, SrcBlend = source, DestBlend = destination, BlendOp = Dx.BlendOpAdd, SrcBlendAlpha = AlphaFactor(source), DestBlendAlpha = AlphaFactor(destination), BlendOpAlpha = Dx.BlendOpAdd, RenderTargetWriteMask = 0xF };
			return Dx.CreateBlendState(device.Device, new Dx.BlendDesc { Target0 = target });
		}

		/// <summary>A blend factor as allowed for alpha: Direct3D has no colour factors there, and the source's alpha is what OpenGL's come to.</summary>
		private static uint AlphaFactor(uint factor) => factor == Dx.BlendInvSrcColor ? Dx.BlendInvSrcAlpha : factor;

		/// <summary>The shared state for drawing quads: the frame's size, the shaders, no depth unless asked.</summary>
		private void Bind(uint width, uint height, bool withDepth, bool depthWrite)
		{
			IntPtr context = device.Context;
			Dx.RSSetViewport(context, new Dx.Viewport { Width = width, Height = height, MaxDepth = 1.0f });
			Dx.IASetPrimitiveTopology(context, Dx.TopologyTriangleList);
			Dx.IASetInputLayout(context, inputLayout);
			Dx.VSSetShader(context, vertexShader);
			Dx.PSSetShader(context, pixelShader);
			Dx.RSSetState(context, scene.RasterState(false));
			Dx.OMSetDepthStencilState(context, scene.DepthState(withDepth, depthWrite));
		}

		private void Write(List<Quad> list)
		{
			if (list.Count > instanceCapacity || instances == IntPtr.Zero)
			{
				Dx.Release(instances);
				instanceCapacity = Math.Max(list.Count + list.Count / 2, 4096);
				instances = Dx.CreateBuffer(device.Device, new Dx.BufferDesc { ByteWidth = (uint)(instanceCapacity * sizeof(QuadData)), Usage = Dx.UsageDynamic, BindFlags = Dx.BindVertexBuffer, CpuAccessFlags = Dx.CpuAccessWrite }, null);
			}

			QuadData* mapped = (QuadData*)Dx.Map(device.Context, instances, Dx.MapWriteDiscard);
			for (int i = 0; i < list.Count; i++)
			{
				mapped[i] = list[i].Data;
			}

			Dx.Unmap(device.Context, instances);
			Dx.IASetVertexBuffer(device.Context, 0, instances, (uint)sizeof(QuadData), 0);
		}

		/// <summary>Draws quads, runs of the same texture and blending together.</summary>
		private void Draw(List<Quad> list)
		{
			IntPtr context = device.Context;
			int run = 0;
			while (run < list.Count)
			{
				Quad first = list[run];
				int next = run + 1;
				while (next < list.Count && list[next].Blend == first.Blend && list[next].Data.Texture == first.Data.Texture && list[next].Data.Sampler == first.Data.Sampler)
				{
					next++;
				}

				Dx.OMSetBlendState(context, blendStates[(int)first.Blend]);
				Dx.PSSetShaderResource(context, 0, textures.View(first.Data.Texture));
				Dx.PSSetSampler(context, 0, scene.Sampler(first.Data.Sampler));
				Dx.DrawInstanced(context, 6, (uint)(next - run), 0, (uint)run);
				run = next;
			}

			Dx.PSSetShaderResource(context, 0, IntPtr.Zero);
		}

		/// <summary>Draws the frame's quads into the targets bound.</summary>
		internal void Record(uint width, uint height)
		{
			if (quads.Count == 0)
			{
				return;
			}

			Bind(width, height, false, false);
			Write(quads);
			Draw(quads);
		}

		/// <summary>Draws the frame's particles into the targets bound, tested against the world's depth: opaque texels writing depth, then blended ones not.</summary>
		internal void RecordParticles(uint width, uint height)
		{
			if (particles.Count == 0)
			{
				return;
			}

			Bind(width, height, true, true);
			Write(particles);
			IntPtr context = device.Context;
			for (int i = 0; i < particles.Count; i++)
			{
				Dx.OMSetDepthStencilState(context, scene.DepthState(true, particles[i].Blend == Blend.None));
				Dx.OMSetBlendState(context, blendStates[(int)particles[i].Blend]);
				Dx.PSSetShaderResource(context, 0, textures.View(particles[i].Data.Texture));
				Dx.PSSetSampler(context, 0, scene.Sampler(particles[i].Data.Sampler));
				Dx.DrawInstanced(context, 6, 1, 0, (uint)i);
			}

			Dx.PSSetShaderResource(context, 0, IntPtr.Zero);
		}

		private readonly List<Quad> single = new List<Quad>();

		/// <summary>A whole image laid over the targets bound: with the given alpha and blending, and its rows running as asked.</summary>
		internal void DrawImage(IntPtr view, uint width, uint height, float alpha, bool blended, bool upsideDown)
		{
			single.Clear();
			single.Add(new Quad
			{
				Blend = blended ? Blend.Normal : Blend.None,
				Data = new QuadData { X = -1.0f, Y = -1.0f, Width = 2.0f, Height = 2.0f, UWidth = 1.0f, V = upsideDown ? 1.0f : 0.0f, VHeight = upsideDown ? -1.0f : 1.0f, R = 1.0f, G = 1.0f, B = 1.0f, A = alpha }
			});
			Bind(width, height, false, false);
			Write(single);
			IntPtr context = device.Context;
			Dx.OMSetBlendState(context, blendStates[(int)single[0].Blend]);
			Dx.PSSetShaderResource(context, 0, view);
			Dx.PSSetSampler(context, 0, scene.Sampler(0));
			Dx.DrawInstanced(context, 6, 1, 0, 0);
			Dx.PSSetShaderResource(context, 0, IntPtr.Zero);
		}

		public void Dispose()
		{
			foreach (IntPtr com in new[] { vertexShader, pixelShader, inputLayout, instances })
			{
				Dx.Release(com);
			}

			foreach (IntPtr state in blendStates)
			{
				Dx.Release(state);
			}
		}
	}
}
