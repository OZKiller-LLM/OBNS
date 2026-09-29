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

// ReSharper disable once CheckNamespace
namespace OpenTK.Graphics.OpenGL
{
	/*
	 * The OpenTK 3 enum surface, restricted to the members LibRender2 and the OpenBVE renderers
	 * actually use. Values are the real GL token values, so they pass straight through to GLES.
	 *
	 * Some members are desktop-only (Lighting, Fog, Texture2D as an EnableCap, PolygonMode, ...).
	 * They are kept so the upstream legacy fallback paths compile; GL.cs drops or rejects them
	 * at the call, since those paths are unreachable once shaders are available.
	 */

	/// <summary>Texture targets.</summary>
	public enum TextureTarget
	{
		Texture2D = 0x0DE1
	}

	/// <summary>Capabilities that can be enabled or disabled.</summary>
	public enum EnableCap
	{
		Blend = 0x0BE2,
		CullFace = 0x0B44,
		DepthTest = 0x0B71,
		Dither = 0x0BD0,
		// Desktop-only below this line.
		DepthClamp = 0x864F,
		Fog = 0x0B60,
		Lighting = 0x0B50,
		Texture2D = 0x0DE1
	}

	/// <summary>Texture parameters.</summary>
	public enum TextureParameterName
	{
		TextureMagFilter = 0x2800,
		TextureMinFilter = 0x2801,
		TextureWrapS = 0x2802,
		TextureWrapT = 0x2803,
		TextureCompareMode = 0x884C,
		TextureCompareFunc = 0x884D,
		// Desktop-only: GLES 3 swizzles each channel separately (0x8E42..0x8E45).
		TextureBorderColor = 0x1004,
		TextureSwizzleRgba = 0x8E46
	}

	/// <summary>Pixel formats for transfers.</summary>
	public enum PixelFormat
	{
		DepthComponent = 0x1902,
		Red = 0x1903,
		Rgb = 0x1907,
		Rgba = 0x1908,
		Luminance = 0x1909,
		LuminanceAlpha = 0x190A
	}

	/// <summary>Primitive topologies.</summary>
	public enum PrimitiveType
	{
		Lines = 0x0001,
		LineStrip = 0x0003,
		Triangles = 0x0004,
		TriangleStrip = 0x0005,
		TriangleFan = 0x0006,
		// Desktop-only topologies: drawn as triangles, see GL.DrawElements.
		Quads = 0x0007,
		QuadStrip = 0x0008,
		Polygon = 0x0009
	}

	/// <summary>Buffer binding targets.</summary>
	public enum BufferTarget
	{
		ArrayBuffer = 0x8892,
		ElementArrayBuffer = 0x8893,
		UniformBuffer = 0x8A11
	}

	/// <summary>Indexed buffer binding targets.</summary>
	public enum BufferRangeTarget
	{
		UniformBuffer = 0x8A11
	}

	/// <summary>Framebuffer binding targets.</summary>
	public enum FramebufferTarget
	{
		Framebuffer = 0x8D40,
		DrawFramebuffer = 0x8CA9,
		ReadFramebuffer = 0x8CA8
	}

	/// <summary>Fixed-function matrix stacks. Desktop-only.</summary>
	public enum MatrixMode
	{
		Modelview = 0x1700,
		Projection = 0x1701
	}

	/// <summary>Expected usage pattern of a buffer's data store.</summary>
	public enum BufferUsageHint
	{
		StaticDraw = 0x88E4,
		DynamicDraw = 0x88E8
	}

	/// <summary>Internal texture formats.</summary>
	public enum PixelInternalFormat
	{
		DepthComponent16 = 0x81A5,
		DepthComponent24 = 0x81A6,
		R8 = 0x8229,
		R32f = 0x822E,
		Rgb = 0x1907,
		Rgb8 = 0x8051,
		Rgba8 = 0x8058,
		// Desktop-only.
		Luminance = 0x1909,
		LuminanceAlpha = 0x190A
	}

	/// <summary>Internal formats used by copy operations.</summary>
	public enum InternalFormat
	{
		Rgb = 0x1907,
		Rgb8 = 0x8051
	}

	/// <summary>Magnification filters.</summary>
	public enum TextureMagFilter
	{
		Nearest = 0x2600,
		Linear = 0x2601
	}

	/// <summary>Minification filters.</summary>
	public enum TextureMinFilter
	{
		Nearest = 0x2600,
		Linear = 0x2601,
		NearestMipmapNearest = 0x2700,
		LinearMipmapNearest = 0x2701,
		NearestMipmapLinear = 0x2702,
		LinearMipmapLinear = 0x2703
	}

	/// <summary>Pixel component types.</summary>
	public enum PixelType
	{
		UnsignedByte = 0x1401,
		Float = 0x1406
	}

	/// <summary>Renderbuffer targets.</summary>
	public enum RenderbufferTarget
	{
		Renderbuffer = 0x8D41
	}

	/// <summary>Texture wrapping modes.</summary>
	public enum TextureWrapMode
	{
		Repeat = 0x2901,
		ClampToEdge = 0x812F,
		// Desktop-only; GLES 3 has no border clamp.
		ClampToBorder = 0x812D
	}

	/// <summary>Framebuffer attachment points.</summary>
	public enum FramebufferAttachment
	{
		ColorAttachment0 = 0x8CE0,
		DepthAttachment = 0x8D00,
		StencilAttachment = 0x8D20
	}

	/// <summary>Shader stages.</summary>
	public enum ShaderType
	{
		FragmentShader = 0x8B30,
		VertexShader = 0x8B31
	}

	/// <summary>Texture units.</summary>
	public enum TextureUnit
	{
		Texture0 = 0x84C0,
		Texture1 = 0x84C1,
		Texture2 = 0x84C2,
		Texture3 = 0x84C3,
		Texture4 = 0x84C4
	}

	/// <summary>Pixel storage parameters.</summary>
	public enum PixelStoreParameter
	{
		UnpackAlignment = 0x0CF5,
		PackAlignment = 0x0D05
	}

	/// <summary>Hint targets. Only mipmap generation is meaningful on GLES.</summary>
	public enum HintTarget
	{
		GenerateMipmapHint = 0x8192,
		FragmentShaderDerivativeHint = 0x8B8B,
		// Desktop-only.
		PerspectiveCorrectionHint = 0x0C50,
		PointSmoothHint = 0x0C51,
		LineSmoothHint = 0x0C52,
		PolygonSmoothHint = 0x0C53,
		FogHint = 0x0C54
	}

	/// <summary>Hint modes.</summary>
	public enum HintMode
	{
		DontCare = 0x1100,
		Fastest = 0x1101,
		Nicest = 0x1102
	}

	/// <summary>Index types for indexed draws.</summary>
	public enum DrawElementsType
	{
		UnsignedShort = 0x1403,
		UnsignedInt = 0x1405
	}

	/// <summary>Face culling modes.</summary>
	public enum CullFaceMode
	{
		Front = 0x0404,
		Back = 0x0405,
		FrontAndBack = 0x0408
	}

	/// <summary>Vertex attribute component types.</summary>
	public enum VertexAttribPointerType
	{
		Float = 0x1406,
		UnsignedByte = 0x1401,
		Short = 0x1402
	}

	/// <summary>Integer vertex attribute component types.</summary>
	public enum VertexAttribIntegerType
	{
		Int = 0x1404,
		UnsignedInt = 0x1405,
		Short = 0x1402
	}

	/// <summary>Polygon faces. Desktop-only.</summary>
	public enum MaterialFace
	{
		Front = 0x0404,
		Back = 0x0405,
		FrontAndBack = 0x0408
	}

	/// <summary>Depth texture comparison modes.</summary>
	public enum TextureCompareMode
	{
		None = 0,
		CompareRefToTexture = 0x884E
	}

	/// <summary>Polygon rasterisation modes. Desktop-only.</summary>
	public enum PolygonMode
	{
		Point = 0x1B00,
		Line = 0x1B01,
		Fill = 0x1B02
	}

	/// <summary>Mipmap generation targets.</summary>
	public enum GenerateMipmapTarget
	{
		Texture2D = 0x0DE1
	}

	/// <summary>Source buffers for reads.</summary>
	public enum ReadBufferMode
	{
		None = 0,
		Back = 0x0405,
		ColorAttachment0 = 0x8CE0
	}

	/// <summary>Destination buffers for draws.</summary>
	public enum DrawBufferMode
	{
		None = 0,
		Back = 0x0405,
		ColorAttachment0 = 0x8CE0
	}

	/// <summary>Destination buffers for multiple-render-target draws.</summary>
	public enum DrawBuffersEnum
	{
		None = 0,
		ColorAttachment0 = 0x8CE0,
		ColorAttachment1 = 0x8CE1
	}

	/// <summary>Program object queries.</summary>
	public enum GetProgramParameterName
	{
		LinkStatus = 0x8B82,
		InfoLogLength = 0x8B84,
		ValidateStatus = 0x8B83
	}

	/// <summary>Shader object queries.</summary>
	public enum ShaderParameter
	{
		CompileStatus = 0x8B81,
		InfoLogLength = 0x8B84
	}

	/// <summary>Buffers cleared by glClear.</summary>
	[Flags]
	public enum ClearBufferMask
	{
		None = 0,
		DepthBufferBit = 0x00000100,
		StencilBufferBit = 0x00000400,
		ColorBufferBit = 0x00004000
	}

	/// <summary>Blending factors.</summary>
	public enum BlendingFactor
	{
		Zero = 0,
		One = 1,
		SrcColor = 0x0300,
		OneMinusSrcColor = 0x0301,
		SrcAlpha = 0x0302,
		OneMinusSrcAlpha = 0x0303,
		DstAlpha = 0x0304,
		OneMinusDstAlpha = 0x0305,
		DstColor = 0x0306,
		OneMinusDstColor = 0x0307
	}

	/// <summary>
	/// Alpha test comparison functions. The fixed-function alpha test is gone from both modern
	/// GL and GLES; OpenBVE passes this to its shaders, which do the comparison themselves.
	/// </summary>
	public enum AlphaFunction
	{
		Never = 0x0200,
		Less = 0x0201,
		Equal = 0x0202,
		Lequal = 0x0203,
		Greater = 0x0204,
		Notequal = 0x0205,
		Gequal = 0x0206,
		Always = 0x0207
	}

	/// <summary>Depth comparison functions.</summary>
	public enum DepthFunction
	{
		Never = 0x0200,
		Less = 0x0201,
		Equal = 0x0202,
		Lequal = 0x0203,
		Greater = 0x0204,
		Notequal = 0x0205,
		Gequal = 0x0206,
		Always = 0x0207
	}

	/// <summary>Strings queryable from the driver.</summary>
	public enum StringName
	{
		Vendor = 0x1F00,
		Renderer = 0x1F01,
		Version = 0x1F02,
		Extensions = 0x1F03
	}

	/// <summary>State queryable by glGet.</summary>
	public enum GetPName
	{
		MaxTextureSize = 0x0D33,
		Viewport = 0x0BA2,
		CurrentProgram = 0x8B8D,
		MaxTextureImageUnits = 0x8872,
		MaxVertexAttribs = 0x8869
	}

	/// <summary>Error codes.</summary>
	public enum ErrorCode
	{
		NoError = 0,
		InvalidEnum = 0x0500,
		InvalidValue = 0x0501,
		InvalidOperation = 0x0502,
		StackOverflow = 0x0503,
		StackUnderflow = 0x0504,
		OutOfMemory = 0x0505,
		InvalidFramebufferOperation = 0x0506,
		TableTooLargeExt = 0x8031
	}

	/// <summary>Framebuffer completeness status.</summary>
	public enum FramebufferErrorCode
	{
		FramebufferComplete = 0x8CD5,
		FramebufferIncompleteAttachment = 0x8CD6,
		FramebufferIncompleteMissingAttachment = 0x8CD7,
		FramebufferUnsupported = 0x8CDD
	}

	/// <summary>
	/// OpenTK's catch-all token enum. Upstream only reaches for it where a parameter's proper
	/// enum does not carry the value it needs, so only those members are defined here.
	/// </summary>
	public enum All
	{
		Never = 0x0200,
		Less = 0x0201,
		Equal = 0x0202,
		Lequal = 0x0203,
		Greater = 0x0204,
		Notequal = 0x0205,
		Gequal = 0x0206,
		Always = 0x0207,
		None = 0
	}

	/// <summary>Tokens from the EXT_texture_filter_anisotropic extension, which GLES also exposes.</summary>
	public enum ExtTextureFilterAnisotropic
	{
		TextureMaxAnisotropyExt = 0x84FE,
		MaxTextureMaxAnisotropyExt = 0x84FF
	}
}
