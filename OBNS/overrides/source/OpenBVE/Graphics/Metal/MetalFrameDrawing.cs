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

// OBNS: not an upstream file. The 2D part of a frame and its particles drawn by Metal (OBNS/RENDERERS.md), with Shaders/frame2d.metal,
// compiled at the start: each quad's values given as bytes, as Vulkan's are.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2.Frame;

namespace OpenBve.Graphics.Metal
{
	internal sealed unsafe class MetalFrameDrawing : WholeFrameDrawing, IDisposable
	{
		private static readonly IntPtr
			selNewLibrary = Mtl.Sel("newLibraryWithSource:options:error:"), selNewFunction = Mtl.Sel("newFunctionWithName:"),
			selSetVertexFunction = Mtl.Sel("setVertexFunction:"), selSetFragmentFunction = Mtl.Sel("setFragmentFunction:"),
			selColorAttachments = Mtl.Sel("colorAttachments"), selObjectAtIndex = Mtl.Sel("objectAtIndexedSubscript:"),
			selSetPixelFormat = Mtl.Sel("setPixelFormat:"), selSetBlendingEnabled = Mtl.Sel("setBlendingEnabled:"),
			selSetSourceRgb = Mtl.Sel("setSourceRGBBlendFactor:"), selSetDestinationRgb = Mtl.Sel("setDestinationRGBBlendFactor:"),
			selSetRgbOperation = Mtl.Sel("setRgbBlendOperation:"), selSetSourceAlpha = Mtl.Sel("setSourceAlphaBlendFactor:"),
			selSetDestinationAlpha = Mtl.Sel("setDestinationAlphaBlendFactor:"), selSetAlphaOperation = Mtl.Sel("setAlphaBlendOperation:"),
			selSetDepthAttachmentPixelFormat = Mtl.Sel("setDepthAttachmentPixelFormat:"), selNewPipeline = Mtl.Sel("newRenderPipelineStateWithDescriptor:error:"),
			selSetPipeline = Mtl.Sel("setRenderPipelineState:"), selSetDepthStencilState = Mtl.Sel("setDepthStencilState:"), selSetViewport = Mtl.Sel("setViewport:"),
			selSetVertexBytes = Mtl.Sel("setVertexBytes:length:atIndex:"), selSetFragmentBytes = Mtl.Sel("setFragmentBytes:length:atIndex:"),
			selSetFragmentTexture = Mtl.Sel("setFragmentTexture:atIndex:"), selSetFragmentSampler = Mtl.Sel("setFragmentSamplerState:atIndex:"),
			selDraw = Mtl.Sel("drawPrimitives:vertexStart:vertexCount:");

		/// <summary>What the shaders read: the quad, to the size Metal gives the shaders' structure (its alignment is 16).</summary>
		[StructLayout(LayoutKind.Sequential)]
		private struct ShaderQuad
		{
			public QuadData Data;
			public float Pad0, Pad1, Pad2;
		}

		private readonly MetalDevice device;
		private readonly MetalScene scene;
		private readonly MetalTextures textures;
		private readonly IntPtr library;

		/// <summary>The pipelines by blending, for drawing with no depth; and the two for particles, tested against the world's depth.</summary>
		private readonly IntPtr[] pipelines = new IntPtr[4];
		private readonly IntPtr opaqueParticles, blendedParticles;

		protected override WorldTextures Textures => textures;

		/// <summary>Whether the frame has particles to draw (they need a pass with the world's depth).</summary>
		internal bool HasParticles => particles.Count > 0;

		internal MetalFrameDrawing(MetalDevice device, MetalScene scene, MetalTextures textures, IFrameDrawing openGl) : base(openGl)
		{
			if (sizeof(ShaderQuad) != 96)
			{
				throw new InvalidOperationException("the Metal 2D quad is " + sizeof(ShaderQuad) + " bytes here, not 96");
			}

			this.device = device;
			this.scene = scene;
			this.textures = textures;
			string source;
			using (Stream stream = typeof(MetalFrameDrawing).Assembly.GetManifestResourceStream("OBNS.Metal.frame2d.metal"))
			{
				if (stream == null)
				{
					throw new InvalidOperationException("the Metal 2D shaders are missing from the program");
				}

				using (StreamReader reader = new StreamReader(stream))
				{
					source = reader.ReadToEnd();
				}
			}

			IntPtr text = Mtl.String(source);
			IntPtr error = IntPtr.Zero;
			library = Mtl.Id(device.Device, selNewLibrary, text, IntPtr.Zero, &error);
			Mtl.Release(text);
			if (library == IntPtr.Zero)
			{
				throw new InvalidOperationException("compiling frame2d.metal failed: " + Mtl.ErrorText(error));
			}

			IntPtr vertex = Function("FrameVS"), fragment = Function("FramePS");
			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				pipelines[(int)Blend.None] = MakePipeline(vertex, fragment, Blend.None, false);
				pipelines[(int)Blend.Normal] = MakePipeline(vertex, fragment, Blend.Normal, false);
				pipelines[(int)Blend.GlyphMask] = MakePipeline(vertex, fragment, Blend.GlyphMask, false);
				pipelines[(int)Blend.GlyphAdd] = MakePipeline(vertex, fragment, Blend.GlyphAdd, false);
				opaqueParticles = MakePipeline(vertex, fragment, Blend.None, true);
				blendedParticles = MakePipeline(vertex, fragment, Blend.Normal, true);
			}
			finally
			{
				Mtl.objc_autoreleasePoolPop(pool);
				Mtl.Release(vertex);
				Mtl.Release(fragment);
			}
		}

		private IntPtr Function(string name)
		{
			IntPtr text = Mtl.String(name);
			IntPtr function = Mtl.Id(library, selNewFunction, text);
			Mtl.Release(text);
			if (function == IntPtr.Zero)
			{
				throw new InvalidOperationException("frame2d.metal has no function " + name);
			}

			return function;
		}

		private IntPtr MakePipeline(IntPtr vertex, IntPtr fragment, Blend blend, bool withDepth)
		{
			IntPtr desc = Mtl.New("MTLRenderPipelineDescriptor");
			Mtl.Void(desc, selSetVertexFunction, vertex);
			Mtl.Void(desc, selSetFragmentFunction, fragment);
			IntPtr color = Mtl.Id(Mtl.Id(desc, selColorAttachments), selObjectAtIndex, 0UL);
			Mtl.Void(color, selSetPixelFormat, Mtl.PixelBGRA8Unorm);
			if (blend != Blend.None)
			{
				ulong source = blend == Blend.GlyphMask ? Mtl.BlendZero : Mtl.BlendSourceAlpha;
				ulong destination = blend == Blend.Normal ? Mtl.BlendOneMinusSourceAlpha : blend == Blend.GlyphMask ? Mtl.BlendOneMinusSourceColor : Mtl.BlendOne;
				Mtl.Void(color, selSetBlendingEnabled, (byte)1);
				Mtl.Void(color, selSetRgbOperation, Mtl.BlendAdd);
				Mtl.Void(color, selSetAlphaOperation, Mtl.BlendAdd);
				Mtl.Void(color, selSetSourceRgb, source);
				Mtl.Void(color, selSetDestinationRgb, destination);
				// OpenGL's blend functions apply to alpha alike; the mask's colour factor comes to the source's alpha there.
				Mtl.Void(color, selSetSourceAlpha, source);
				Mtl.Void(color, selSetDestinationAlpha, destination == Mtl.BlendOneMinusSourceColor ? Mtl.BlendOneMinusSourceAlpha : destination);
			}

			if (withDepth)
			{
				Mtl.Void(desc, selSetDepthAttachmentPixelFormat, Mtl.PixelDepth32Float);
			}

			IntPtr error = IntPtr.Zero;
			IntPtr pipeline = Mtl.Id(device.Device, selNewPipeline, desc, &error);
			Mtl.Release(desc);
			if (pipeline == IntPtr.Zero)
			{
				throw new InvalidOperationException("a Metal 2D pipeline could not be made: " + Mtl.ErrorText(error));
			}

			return pipeline;
		}

		private void DrawQuad(IntPtr encoder, QuadData data)
		{
			ShaderQuad quad = new ShaderQuad { Data = data };
			Mtl.Void(encoder, selSetVertexBytes, &quad, 96UL, 0UL);
			Mtl.Void(encoder, selSetFragmentBytes, &quad, 96UL, 0UL);
			Mtl.Void(encoder, selDraw, Mtl.PrimitiveTriangle, 0UL, 6UL);
		}

		/// <summary>Draws the frame's quads with an encoder of a pass with no depth.</summary>
		internal void Record(IntPtr encoder, uint width, uint height)
		{
			if (quads.Count == 0)
			{
				return;
			}

			Mtl.Void(encoder, selSetViewport, new Mtl.Viewport { Width = width, Height = height, Far = 1.0 });
			int boundBlend = -1, boundTexture = -1, boundSampler = -1;
			foreach (Quad quad in quads)
			{
				if ((int)quad.Blend != boundBlend)
				{
					Mtl.Void(encoder, selSetPipeline, pipelines[(int)quad.Blend]);
					boundBlend = (int)quad.Blend;
				}

				Bind(encoder, quad.Data, ref boundTexture, ref boundSampler);
				DrawQuad(encoder, quad.Data);
			}
		}

		/// <summary>Draws the frame's particles with an encoder of a pass with the world's depth: opaque texels writing depth, then blended ones not.</summary>
		internal void RecordParticles(IntPtr encoder, uint width, uint height)
		{
			Mtl.Void(encoder, selSetViewport, new Mtl.Viewport { Width = width, Height = height, Far = 1.0 });
			int boundTexture = -1, boundSampler = -1;
			foreach (Quad quad in particles)
			{
				bool opaque = quad.Blend == Blend.None;
				Mtl.Void(encoder, selSetPipeline, opaque ? opaqueParticles : blendedParticles);
				Mtl.Void(encoder, selSetDepthStencilState, scene.ParticleDepthState(opaque));
				Bind(encoder, quad.Data, ref boundTexture, ref boundSampler);
				DrawQuad(encoder, quad.Data);
			}
		}

		private void Bind(IntPtr encoder, QuadData data, ref int boundTexture, ref int boundSampler)
		{
			if (data.Texture != boundTexture)
			{
				Mtl.Void(encoder, selSetFragmentTexture, textures.Texture(data.Texture), 0UL);
				boundTexture = data.Texture;
			}

			if (data.Sampler != boundSampler)
			{
				Mtl.Void(encoder, selSetFragmentSampler, scene.Sampler(data.Sampler), 0UL);
				boundSampler = data.Sampler;
			}
		}

		/// <summary>A whole image laid over the target of a pass with no depth: with the given alpha and blending, and its rows running as asked.</summary>
		internal void DrawImage(IntPtr encoder, IntPtr texture, uint width, uint height, float alpha, bool blended, bool upsideDown)
		{
			Mtl.Void(encoder, selSetViewport, new Mtl.Viewport { Width = width, Height = height, Far = 1.0 });
			Mtl.Void(encoder, selSetPipeline, pipelines[(int)(blended ? Blend.Normal : Blend.None)]);
			Mtl.Void(encoder, selSetFragmentTexture, texture, 0UL);
			Mtl.Void(encoder, selSetFragmentSampler, scene.Sampler(0), 0UL);
			DrawQuad(encoder, new QuadData { X = -1.0f, Y = -1.0f, Width = 2.0f, Height = 2.0f, UWidth = 1.0f, V = upsideDown ? 1.0f : 0.0f, VHeight = upsideDown ? -1.0f : 1.0f, R = 1.0f, G = 1.0f, B = 1.0f, A = alpha });
		}

		public void Dispose()
		{
			foreach (IntPtr pipeline in pipelines)
			{
				Mtl.Release(pipeline);
			}

			Mtl.Release(opaqueParticles);
			Mtl.Release(blendedParticles);
			Mtl.Release(library);
		}
	}
}
