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

// OBNS: not an upstream file. Metal, IOSurface and CGL called from C# (OBNS/METAL.md), for the Metal world layer on macOS;
// no wrapper library. Objective-C methods are sent through objc_msgSend declared once per argument list (arm64 calls it with
// the method's own argument registers, so each list needs its own declaration); selectors and classes looked up once.
// The enumerations' values are the SDK's (Metal/MTL*.h).

using System;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Metal
{
	internal static unsafe class Mtl
	{
		private const string Objc = "/usr/lib/libobjc.dylib";
		private const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";
		private const string IOSurfaceFramework = "/System/Library/Frameworks/IOSurface.framework/IOSurface";
		private const string OpenGLFramework = "/System/Library/Frameworks/OpenGL.framework/OpenGL";
		private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
		private const string LibSystem = "/usr/lib/libSystem.dylib";

		// --- the runtime ---

		[DllImport(Objc)] private static extern IntPtr objc_getClass(string name);
		[DllImport(Objc)] private static extern IntPtr sel_registerName(string name);
		[DllImport(Objc)] internal static extern IntPtr objc_autoreleasePoolPush();
		[DllImport(Objc)] internal static extern void objc_autoreleasePoolPop(IntPtr pool);
		[DllImport(LibSystem)] private static extern IntPtr dlopen(string path, int mode);

		internal static IntPtr Class(string name)
		{
			IntPtr c = objc_getClass(name);
			if (c == IntPtr.Zero)
			{
				throw new InvalidOperationException("no Objective-C class " + name);
			}

			return c;
		}

		internal static IntPtr Sel(string name) => sel_registerName(name);

		/// <summary>Foundation and Metal loaded (their classes are found only once their frameworks are).</summary>
		internal static void LoadFrameworks()
		{
			dlopen("/System/Library/Frameworks/Foundation.framework/Foundation", 1);
			dlopen(MetalFramework, 1);
		}

		// objc_msgSend, by argument list: Id* return an object (or a pointer), Void* nothing.
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, IntPtr a);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, ulong a);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, ulong a, ulong b);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, IntPtr a, ulong b);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, ulong c);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr* error);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr* error);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, ulong format, ulong width, ulong height, byte mipmapped);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, byte* utf8);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern IntPtr Id(IntPtr receiver, IntPtr selector, int value);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern ulong Number(IntPtr receiver, IntPtr selector);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool Flag(IntPtr receiver, IntPtr selector, IntPtr a);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool Flag(IntPtr receiver, IntPtr selector, ulong a);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, IntPtr a);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, ulong a);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, ulong a, ulong b);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, byte a);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, float a);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, double a);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, IntPtr a, ulong b);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, IntPtr a, ulong b, ulong c);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, void* bytes, ulong length, ulong index);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, ClearColor color);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, Viewport viewport);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, Region region, ulong level, void* bytes, ulong bytesPerRow);
		[DllImport(Objc, EntryPoint = "objc_msgSend")] internal static extern void Void(IntPtr receiver, IntPtr selector, ulong primitive, ulong indexCount, ulong indexType, IntPtr indexBuffer, ulong indexOffset, ulong instances, long baseVertex, ulong baseInstance);

		[StructLayout(LayoutKind.Sequential)]
		internal struct ClearColor
		{
			public double R, G, B, A;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Viewport
		{
			public double X, Y, Width, Height, Near, Far;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Region
		{
			public ulong X, Y, Z, Width, Height, Depth;
		}

		// --- Foundation ---

		private static readonly IntPtr selRelease = Sel("release"), selRetain = Sel("retain"), selAlloc = Sel("alloc"), selInit = Sel("init"),
			selInitWithUtf8 = Sel("initWithUTF8String:"), selUtf8 = Sel("UTF8String"), selDescription = Sel("localizedDescription"),
			selNumberWithInt = Sel("numberWithInt:"), selSetObjectForKey = Sel("setObject:forKey:"), selDictionary = Sel("dictionary");

		internal static void Release(IntPtr obj)
		{
			if (obj != IntPtr.Zero)
			{
				Void(obj, selRelease);
			}
		}

		internal static IntPtr Retain(IntPtr obj) => obj == IntPtr.Zero ? obj : Id(obj, selRetain);

		/// <summary>[[Class alloc] init] (to be released).</summary>
		internal static IntPtr New(string className) => Id(Id(Class(className), selAlloc), selInit);

		/// <summary>An NSString (to be released).</summary>
		internal static IntPtr String(string text)
		{
			byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text + "\0");
			fixed (byte* data = utf8)
			{
				return Id(Id(Class("NSString"), selAlloc), selInitWithUtf8, data);
			}
		}

		internal static string Text(IntPtr nsString) => nsString == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(Id(nsString, selUtf8));

		/// <summary>An NSError's description.</summary>
		internal static string ErrorText(IntPtr error) => error == IntPtr.Zero ? "no description" : Text(Id(error, selDescription));

		/// <summary>An autoreleased NSMutableDictionary of NSString keys and NSNumber (int) values.</summary>
		internal static IntPtr Dictionary(string[] keys, int[] values)
		{
			IntPtr dictionary = Id(Class("NSMutableDictionary"), selDictionary);
			for (int i = 0; i < keys.Length; i++)
			{
				IntPtr key = String(keys[i]);
				Void(dictionary, selSetObjectForKey, Id(Class("NSNumber"), selNumberWithInt, values[i]), key);
				Release(key);
			}

			return dictionary;
		}

		// --- Metal, IOSurface, CGL ---

		[DllImport(MetalFramework)] internal static extern IntPtr MTLCreateSystemDefaultDevice();
		[DllImport(MetalFramework)] internal static extern IntPtr MTLCopyAllDevices();
		[DllImport(IOSurfaceFramework)] internal static extern IntPtr IOSurfaceCreate(IntPtr properties);
		[DllImport(IOSurfaceFramework)] internal static extern ulong IOSurfaceAlignProperty(IntPtr property, ulong value);
		[DllImport(CoreFoundation)] internal static extern void CFRelease(IntPtr obj);
		[DllImport(OpenGLFramework)] internal static extern IntPtr CGLGetCurrentContext();
		[DllImport(OpenGLFramework)] internal static extern int CGLTexImageIOSurface2D(IntPtr context, uint target, uint internalFormat, int width, int height, uint format, uint type, IntPtr surface, uint plane);

		// MTLPixelFormat
		internal const ulong PixelRGBA8Unorm = 70, PixelBGRA8Unorm = 80, PixelR32Float = 55, PixelDepth32Float = 252;
		// MTLTextureUsage, MTLStorageMode, MTLResourceOptions (storage mode << 4), MTLTextureType
		internal const ulong UsageShaderRead = 1, UsageRenderTarget = 4;
		internal const ulong StorageShared = 0, StorageManaged = 1, StoragePrivate = 2;
		internal const ulong ResourceShared = 0, ResourceManaged = 1 << 4, ResourcePrivate = 2 << 4;
		internal const ulong TextureType2DArray = 3;
		// MTLLoadAction, MTLStoreAction, MTLPrimitiveType, MTLIndexType, MTLCullMode
		internal const ulong LoadDontCare = 0, LoadClear = 2, StoreDontCare = 0, StoreStore = 1;
		internal const ulong PrimitiveTriangle = 3, IndexUInt32 = 1;
		internal const ulong CullNone = 0, CullFront = 1;
		// MTLCompareFunction, MTLBlendFactor, MTLBlendOperation, MTLColorWriteMask
		internal const ulong CompareLess = 1, CompareLessEqual = 3, CompareAlways = 7;
		internal const ulong BlendZero = 0, BlendOne = 1, BlendSourceAlpha = 4, BlendOneMinusSourceAlpha = 5, BlendAdd = 0;
		internal const ulong WriteNone = 0, WriteRed = 8, WriteAll = 15;
		// MTLSamplerMinMagFilter, MTLSamplerMipFilter, MTLSamplerAddressMode
		internal const ulong FilterNearest = 0, FilterLinear = 1, MipNone = 0, MipNearest = 1, MipLinear = 2, AddressClampToEdge = 0, AddressRepeat = 2;
		// MTLVertexFormat, MTLVertexStepFunction; MTLCommandBufferStatus
		internal const ulong VertexFloat2 = 29, VertexFloat3 = 30, VertexFloat4 = 31, VertexInt3 = 34, StepPerVertex = 1;
		internal const ulong CommandBufferError = 5;

		// The IOSurface pixel formats: 'BGRA', and one 32-bit float ('L00f').
		internal const int SurfaceBGRA = 0x42475241, SurfaceR32Float = 0x4C303066;

		// OpenGL's names for the IOSurface textures (rectangles)
		internal const uint GLTextureRectangle = 0x84F5, GLRgba8 = 0x8058, GLBgra = 0x80E1, GLUnsignedInt8888Rev = 0x8367, GLR32F = 0x822E, GLRed = 0x1903, GLFloat = 0x1406;
	}
}
