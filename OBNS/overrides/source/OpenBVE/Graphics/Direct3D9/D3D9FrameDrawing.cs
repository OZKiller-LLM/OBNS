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

// OBNS: not an upstream file. The 2D part of a frame and its particles drawn by Direct3D 9 (OBNS/RENDERERS.md): shader model 3, six
// plain vertices a quad.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2.Frame;

namespace OpenBve.Graphics.Direct3D9
{
	internal sealed unsafe class D3D9FrameDrawing : WholeFrameDrawing, IDisposable
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct Vertex
		{
			public float X, Y, Z, U, V, R, G, B, A, AlphaFunction, AlphaValue, FogR, FogG, FogB, FogAmount;
		}

		private readonly D3D9Device device;
		private readonly D3D9Scene scene;
		private readonly D3D9Textures textures;
		private readonly IntPtr vertexShader, pixelShader, declaration;
		private IntPtr vertices;
		private int vertexCapacity;

		protected override WorldTextures Textures => textures;

		internal D3D9FrameDrawing(D3D9Device device, D3D9Scene scene, D3D9Textures textures, IFrameDrawing openGl) : base(openGl)
		{
			this.device = device;
			this.scene = scene;
			this.textures = textures;
			string source;
			using (Stream stream = typeof(D3D9FrameDrawing).Assembly.GetManifestResourceStream("OBNS.Direct3D9.frame2d9.hlsl"))
			{
				if (stream == null)
				{
					throw new InvalidOperationException("the Direct3D 9 2D shaders are missing from the program");
				}

				using (StreamReader reader = new StreamReader(stream))
				{
					source = reader.ReadToEnd();
				}
			}

			IntPtr d = device.Device;
			vertexShader = D9.CreateVertexShader(d, Direct3D11.Dx.Compile(source, "frame2d9.hlsl", "VS", "vs_3_0"));
			pixelShader = D9.CreatePixelShader(d, Direct3D11.Dx.Compile(source, "frame2d9.hlsl", "PS", "ps_3_0"));
			D9.VertexElement* elements = stackalloc D9.VertexElement[6];
			elements[0] = new D9.VertexElement { Offset = 0, Type = D9.DeclFloat3, Usage = D9.UsagePosition };
			elements[1] = new D9.VertexElement { Offset = 12, Type = D9.DeclFloat2, Usage = D9.UsageTexCoord };
			elements[2] = new D9.VertexElement { Offset = 20, Type = D9.DeclFloat4, Usage = D9.UsageColor };
			elements[3] = new D9.VertexElement { Offset = 36, Type = D9.DeclFloat2, Usage = D9.UsageTexCoord, UsageIndex = 1 };
			elements[4] = new D9.VertexElement { Offset = 44, Type = D9.DeclFloat4, Usage = D9.UsageTexCoord, UsageIndex = 2 };
			elements[5] = new D9.VertexElement { Stream = 0xFF, Type = D9.DeclUnused };
			declaration = D9.CreateVertexDeclaration(d, elements);
		}

		/// <summary>The shaders and render states of a quad.</summary>
		private void SetStates(Blend blend, bool depthTest, bool depthWrite, uint width, uint height)
		{
			IntPtr d = device.Device;
			D9.SetViewport(d, 0, 0, width, height);
			D9.SetVertexDeclaration(d, declaration);
			D9.SetVertexShader(d, vertexShader);
			D9.SetPixelShader(d, pixelShader);
			float* offset = stackalloc float[4];
			offset[0] = 1.0f / width;
			offset[1] = 1.0f / height;
			offset[2] = offset[3] = 0.0f;
			D9.SetVertexConstants(d, 0, offset, 1);
			D9.SetRenderState(d, D9.StateCullMode, D9.CullNone);
			D9.SetRenderState(d, D9.StateZEnable, depthTest ? 1u : 0u);
			D9.SetRenderState(d, D9.StateZWriteEnable, depthWrite ? 1u : 0u);
			D9.SetRenderState(d, D9.StateZFunc, D9.CompareLessEqual);
			SetBlend(blend);
		}

		private void SetBlend(Blend blend)
		{
			IntPtr d = device.Device;
			D9.SetRenderState(d, D9.StateAlphaBlendEnable, blend == Blend.None ? 0u : 1u);
			if (blend == Blend.None)
			{
				return;
			}

			uint source = blend == Blend.GlyphMask ? D9.BlendZero : D9.BlendSrcAlpha;
			uint destination = blend == Blend.Normal ? D9.BlendInvSrcAlpha : blend == Blend.GlyphMask ? D9.BlendInvSrcColor : D9.BlendOne;
			D9.SetRenderState(d, D9.StateSeparateAlphaBlendEnable, 1);
			D9.SetRenderState(d, D9.StateBlendOp, D9.BlendOpAdd);
			D9.SetRenderState(d, D9.StateBlendOpAlpha, D9.BlendOpAdd);
			D9.SetRenderState(d, D9.StateSrcBlend, source);
			D9.SetRenderState(d, D9.StateDestBlend, destination);
			// OpenGL's blend functions apply to alpha alike; the mask's colour factor comes to the source's alpha there.
			D9.SetRenderState(d, D9.StateSrcBlendAlpha, source);
			D9.SetRenderState(d, D9.StateDestBlendAlpha, destination == D9.BlendInvSrcColor ? D9.BlendInvSrcAlpha : destination);
		}

		/// <summary>Writes quads as six vertices each, and sets them as the input.</summary>
		private void Write(List<Quad> list)
		{
			int count = list.Count * 6;
			if (count > vertexCapacity || vertices == IntPtr.Zero)
			{
				D9.Release(vertices);
				vertexCapacity = Math.Max(count + count / 2, 24576);
				vertices = D9.CreateVertexBuffer(device.Device, (uint)(vertexCapacity * sizeof(Vertex)), D9.UsageDynamic | D9.UsageWriteOnly);
			}

			Vertex* to = (Vertex*)D9.LockBuffer(vertices, 0, (uint)(count * sizeof(Vertex)), D9.LockDiscard);
			for (int i = 0; i < list.Count; i++)
			{
				QuadData q = list[i].Data;
				for (int corner = 0; corner < 6; corner++)
				{
					// (0,0), (1,0), (1,1), (0,0), (0,1), (1,1), as Direct3D 11's shader has them.
					float x = corner == 1 || corner == 2 || corner == 5 ? 1.0f : 0.0f, y = corner == 2 || corner == 4 || corner == 5 ? 1.0f : 0.0f;
					*to++ = new Vertex { X = q.X + x * q.Width, Y = q.Y + y * q.Height, Z = q.Depth, U = q.U + x * q.UWidth, V = q.V + y * q.VHeight, R = q.R, G = q.G, B = q.B, A = q.A, AlphaFunction = q.AlphaFunction, AlphaValue = q.AlphaValue, FogR = q.FogR, FogG = q.FogG, FogB = q.FogB, FogAmount = q.FogAmount };
				}
			}

			D9.UnlockBuffer(vertices);
			D9.SetStreamSource(device.Device, 0, vertices, (uint)sizeof(Vertex));
		}

		/// <summary>Draws the frame's quads into the target set (no depth), runs of the same texture and blending together.</summary>
		internal void Record(uint width, uint height)
		{
			if (quads.Count == 0)
			{
				return;
			}

			IntPtr d = device.Device;
			SetStates(Blend.None, false, false, width, height);
			Write(quads);
			scene.QuadFiltering();
			Blend boundBlend = Blend.None;
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
					SetBlend(first.Blend);
					boundBlend = first.Blend;
				}

				D9.SetTexture(d, 0, textures.Texture(first.Data.Texture));
				scene.QuadWrapping(first.Data.Sampler);
				D9.DrawTriangles(d, (uint)run * 6, (uint)(next - run) * 2);
				run = next;
			}

			D9.SetTexture(d, 0, IntPtr.Zero);
		}

		/// <summary>Draws the frame's particles into the target and world depth set: opaque texels writing depth, then blended ones not.</summary>
		internal void RecordParticles(uint width, uint height)
		{
			if (particles.Count == 0)
			{
				return;
			}

			IntPtr d = device.Device;
			SetStates(Blend.None, true, true, width, height);
			Write(particles);
			scene.QuadFiltering();
			for (int i = 0; i < particles.Count; i++)
			{
				D9.SetRenderState(d, D9.StateZWriteEnable, particles[i].Blend == Blend.None ? 1u : 0u);
				SetBlend(particles[i].Blend);
				D9.SetTexture(d, 0, textures.Texture(particles[i].Data.Texture));
				scene.QuadWrapping(particles[i].Data.Sampler);
				D9.DrawTriangles(d, (uint)i * 6, 2);
			}

			D9.SetTexture(d, 0, IntPtr.Zero);
		}

		private readonly List<Quad> single = new List<Quad>();

		/// <summary>A whole image laid over the target set (no depth): with the given alpha and blending, and its rows running as asked.</summary>
		internal void DrawImage(IntPtr texture, uint width, uint height, float alpha, bool blended, bool upsideDown)
		{
			single.Clear();
			single.Add(new Quad
			{
				Data = new QuadData { X = -1.0f, Y = -1.0f, Width = 2.0f, Height = 2.0f, UWidth = 1.0f, V = upsideDown ? 1.0f : 0.0f, VHeight = upsideDown ? -1.0f : 1.0f, R = 1.0f, G = 1.0f, B = 1.0f, A = alpha }
			});
			IntPtr d = device.Device;
			SetStates(blended ? Blend.Normal : Blend.None, false, false, width, height);
			Write(single);
			scene.QuadFiltering();
			scene.QuadWrapping(0);
			D9.SetTexture(d, 0, texture);
			D9.DrawTriangles(d, 0, 2);
			D9.SetTexture(d, 0, IntPtr.Zero);
		}

		public void Dispose()
		{
			foreach (IntPtr com in new[] { vertexShader, pixelShader, declaration, vertices })
			{
				D9.Release(com);
			}
		}
	}
}
