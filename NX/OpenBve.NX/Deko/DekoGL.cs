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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.NX.Deko
{
	/*
	 * OBNS 1.31N (OBNS/SWITCH.md, step 3): the renderer's GL calls on deko3d, as Android's native Vulkan renderer does
	 * them on Vulkan (Android/OpenBve.Vulkan/VulkanGL, whose structure and GL semantics this follows). Upstream's
	 * renderer and the port's own drawing talk to OpenBve.GLES's GL class; with GL.Device set to this, their calls come
	 * here and no GL driver is involved: deko3d draws everything.
	 *
	 *   - programs: upstream's default, rectangle and text, recognised by their source and drawn with Android's native
	 *     Vulkan shaders compiled by uam at build time; their uniforms are one block each, a GL uniform location being
	 *     the uniform's byte offset in it divided by four (as VulkanGL);
	 *   - buffers by GL name in CPU-visible memory written directly, copied on write when the frame being recorded has
	 *     drawn with them; uniform buffers (the animation matrices) kept on the CPU and copied into the frame's uniform
	 *     ring when drawn with;
	 *   - textures by GL name: converted into staging memory by glTexImage2D, made into an image when first drawn with
	 *     (by then glGenerateMipmap has said whether to make mipmaps), the copy and the mipmap blits recorded before the
	 *     frame's drawing; each texture is a slot in one image descriptor set, its sampler a slot in another;
	 *   - state as deko3d binds it, applied by the native side only where it changed (deko3d has no pipeline objects).
	 *
	 * Frames begin at the first clear or draw after EndFrame. For now each frame is waited for at its end, so nothing
	 * the GPU used is still in use after it; frames in flight come with the CPU work of step 4. Not yet here: shadows
	 * (framebuffers), the picking shader, VulkanGL's native face drawing.
	 */
	public sealed unsafe partial class DekoGL : IGLDevice, IDisposable
	{
		// GL's enum values used here.
		private const int GL_TEXTURE_2D = 0x0DE1, GL_TEXTURE0 = 0x84C0;
		private const int GL_ARRAY_BUFFER = 0x8892, GL_ELEMENT_ARRAY_BUFFER = 0x8893, GL_UNIFORM_BUFFER = 0x8A11;
		private const int GL_CULL_FACE = 0x0B44, GL_DEPTH_TEST = 0x0B71, GL_BLEND = 0x0BE2;
		private const int GL_FRONT = 0x0404, GL_BACK = 0x0405;
		private const int GL_LESS = 0x0201, GL_ONE = 1, GL_ZERO = 0;
		private const int GL_COLOR_BUFFER_BIT = 0x4000, GL_DEPTH_BUFFER_BIT = 0x0100;
		private const int GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01, GL_VERSION = 0x1F02, GL_EXTENSIONS = 0x1F03, GL_SHADING_LANGUAGE_VERSION = 0x8B8C;
		private const int GL_MAX_TEXTURE_SIZE = 0x0D33, GL_VIEWPORT = 0x0BA2, GL_MAX_VERTEX_ATTRIBS = 0x8869, GL_MAX_TEXTURE_IMAGE_UNITS = 0x8872;
		private const int GL_MAX_TEXTURE_MAX_ANISOTROPY = 0x84FF, GL_UNPACK_ALIGNMENT = 0x0CF5;
		private const int GL_POINTS = 0x0000;
		private const int GL_FLOAT = 0x1406, GL_INT = 0x1404, GL_UNSIGNED_INT = 0x1405, GL_SHORT = 0x1402, GL_UNSIGNED_SHORT = 0x1403, GL_BYTE = 0x1400, GL_UNSIGNED_BYTE = 0x1401;
		private const int GL_VERTEX_SHADER = 0x8B31, GL_FRAGMENT_SHADER = 0x8B30, GL_COMPILE_STATUS = 0x8B81, GL_LINK_STATUS = 0x8B82;
		private const int GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_TEXTURE_MAX_ANISOTROPY = 0x84FE;
		private const int GL_NEAREST = 0x2600, GL_LINEAR = 0x2601;
		private const int GL_NEAREST_MIPMAP_NEAREST = 0x2700, GL_LINEAR_MIPMAP_NEAREST = 0x2701, GL_NEAREST_MIPMAP_LINEAR = 0x2702, GL_LINEAR_MIPMAP_LINEAR = 0x2703;
		private const int GL_REPEAT = 0x2901, GL_MIRRORED_REPEAT = 0x8370;
		private const int GL_TEXTURE_SWIZZLE_R = 0x8E42, GL_TEXTURE_SWIZZLE_RGBA = 0x8E46;
		private const int GL_RED = 0x1903, GL_GREEN = 0x1904, GL_BLUE = 0x1905, GL_ALPHA = 0x1906, GL_RGB = 0x1907, GL_RGBA = 0x1908, GL_LUMINANCE = 0x1909, GL_LUMINANCE_ALPHA = 0x190A, GL_RG = 0x8227;
		private const int GL_UNSIGNED_SHORT_5_6_5 = 0x8363, GL_UNSIGNED_SHORT_4_4_4_4 = 0x8033, GL_UNSIGNED_SHORT_5_5_5_1 = 0x8034;

		private const int Attributes = 8, Units = 8, DefaultLocations = 5;

		/// <summary>The framebuffer's size (GL's window), for turning GL's bottom-left viewports into deko3d's top-left ones.</summary>
		private readonly int framebufferWidth, framebufferHeight;

		private readonly DekoHeap bufferHeap = new DekoHeap(false);
		private readonly DekoHeap imageHeap = new DekoHeap(true);
		private int nextName = 1;
		private readonly Dictionary<int, IntPtr> strings = new Dictionary<int, IntPtr>();
		private readonly HashSet<string> reported = new HashSet<string>();

		/// <summary>Messages about what isn't supported (each once), and the frame log: the host logs them.</summary>
		public static Action<string> Log = message => { };

		// GL's fixed-function state.
		private float clearRed, clearGreen, clearBlue, clearAlpha;
		private int viewportX, viewportY, viewportWidth, viewportHeight;
		private bool depthTest, blend, cullFace;
		private int cullMode = GL_BACK;
		private int blendSource = GL_ONE, blendDestination = GL_ZERO;
		private bool depthMask = true;
		private int depthFunc = GL_LESS;
		private int unpackAlignment = 4;

		// Frames: the serial of the frame being recorded (if any), and of the last one finished (and waited for).
		private bool inFrame;
		private ulong serial, completedSerial;

		/*
		 * Frames in flight (OBNS/SWITCH.md, step 4): the native end of a frame no longer waits for it, only for the frame
		 * before, so the GPU draws one frame while the CPU records the next. What a frame used is let go of one frame later:
		 * what is let go of while frame N is recorded (afterGpu) runs at the end of frame N + 1, when N is done. Between
		 * frames nothing runs at once while a frame may still be drawing (inFlight). The ring's two halves take turns.
		 * Off (UserData/nx-serial-frames.txt): each frame waited for, as before.
		 */
		public static bool FramesInFlight { get; private set; } = true;

		/// <summary>Frames in flight on or off, native side included; before the game's frames start.</summary>
		public static void SetFramesInFlight(bool enabled)
		{
			FramesInFlight = enabled;
			DekoNative.SetFramesInFlight(enabled ? 1 : 0);
		}
		private List<Action> afterGpu = new List<Action>(), afterGpuNext = new List<Action>();
		private bool inFlight;

		/// <summary>Draws in the last finished frame, and the time spent preparing them, for the log.</summary>
		public int LastFrameDraws { get; private set; }

		public double LastFrameMilliseconds { get; private set; }

		/// <summary>The last frame's submit, present and wait for the GPU (the native end), for the log.</summary>
		public double LastEndMilliseconds { get; private set; }

		private int frameDraws;
		private long frameTicks;

		/// <summary>Of <see cref="LastFrameMilliseconds"/>: the native side recording the world records, and textures made
		/// (uploads recorded) for them, in the last finished frame.</summary>
		public double LastRecordMilliseconds { get; private set; }

		public double LastRealizeMilliseconds { get; private set; }

		private long recordTicks, realizeTicks;

		/// <param name="width">The framebuffer's width (1280 handheld).</param>
		/// <param name="height">Its height (720).</param>
		public DekoGL(int width, int height)
		{
			framebufferWidth = width;
			framebufferHeight = height;
			viewportWidth = width;
			viewportHeight = height;
			ring = bufferHeap.Allocate(RingSize, 256);
			zeroBlock = bufferHeap.Allocate(AnimationBlockSize, 256);
			new Span<byte>(zeroBlock.Pointer, (int)AnimationBlockSize).Clear();
			defaultAttributes = bufferHeap.Allocate(64, 256);
			float* constant = (float*)defaultAttributes.Pointer;
			constant[0] = constant[1] = constant[2] = 0.0f;
			constant[3] = 1.0f;
			new Span<byte>(defaultAttributes.Pointer + 16, 48).Clear();
			CreateBlackTexture();
		}

		private void ReportOnce(string what)
		{
			if (reported.Add(what))
			{
				Log(what);
			}
		}

		/// <summary>Memory and counts, for the log.</summary>
		public string Describe() =>
			LastFrameDraws + " draws, " + LastFrameFaces + " of them faces as world records (" + LastFrameMilliseconds.ToString("0.0") + " ms preparing them, " + LastRecordMilliseconds.ToString("0.0") + " ms of it recording, " + LastRealizeMilliseconds.ToString("0.0") + " ms textures made), " + textures.Count + " textures (" + (imageHeap.UsedBytes >> 20) + " MB, " +
			(imageHeap.ReservedBytes >> 20) + " MB reserved), " + glBuffers.Count + " buffers (" + (bufferHeap.UsedBytes >> 20) + " MB, " + (bufferHeap.ReservedBytes >> 20) + " MB reserved)";

		// --- frames ---

		/// <summary>
		/// Whether clears and draws make frames: off until the game's frames start, so that a clear while the renderer
		/// sets up or the route loads doesn't take a swapchain image while the loading screen draws on the same swapchain.
		/// </summary>
		public bool Drawing { get; set; }

		private bool EnsureFrame()
		{
			if (inFrame)
			{
				return true;
			}

			if (!Drawing)
			{
				return false;
			}

			if (DekoNative.Begin() != 0)
			{
				ReportOnce("deko3d: a frame could not begin (the GPU's queue failed)");
				return false;
			}

			inFrame = true;
			serial++;
			// The ring's half this frame writes: the other one may be read by the frame before, still drawing.
			ringBase = FramesInFlight ? (uint)(serial & 1) * (ring.Size / 2) : 0;
			ringLimit = FramesInFlight ? ringBase + ring.Size / 2 : ring.Size;
			ringPosition = ringBase;
			return true;
		}

		/// <summary>Ends and presents the frame (the swap); between frames (loading) runs the uploads recorded.</summary>
		public void EndFrame()
		{
			if (inFrame)
			{
				FlushWorld();
				LastFrameDraws = frameDraws;
				LastFrameMilliseconds = frameTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				LastRecordMilliseconds = recordTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				LastRealizeMilliseconds = realizeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				recordTicks = 0;
				realizeTicks = 0;
				frameDraws = 0;
				frameTicks = 0;
				inFrame = false;
				long ending = System.Diagnostics.Stopwatch.GetTimestamp();
				if (DekoNative.End() != 0)
				{
					ReportOnce("deko3d: the GPU's queue failed at the end of a frame");
				}

				LastEndMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - ending) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

				WorldFrameEnded();
				// The uploads ran in the native end; the frame itself may still be drawing (with frames in flight), the one
				// before it is done.
				stagingPending = 0;
				if (FramesInFlight)
				{
					completedSerial = serial - 1;
					inFlight = true;
					// What frames up to the one before let go of.
					List<Action> done = afterGpuNext;
					afterGpuNext = afterGpu;
					afterGpu = done;
					Run(done);
				}
				else
				{
					completedSerial = serial;
					RunAfterGpu();
				}
			}
			else if (stagingPending > 0 || inFlight)
			{
				DekoNative.FlushUploads();
				RunAfterGpu();
			}
		}

		/// <summary>Everything let go of, run: the GPU is idle (the native flush waits for it).</summary>
		private void RunAfterGpu()
		{
			completedSerial = serial;
			inFlight = false;
			Run(afterGpuNext);
			Run(afterGpu);
			stagingPending = 0;
		}

		private static void Run(List<Action> actions)
		{
			foreach (Action action in actions)
			{
				action();
			}

			actions.Clear();
		}

		/// <summary>Done once the GPU can no longer be using what it lets go of: now between frames with no uploads
		/// waiting and no frame drawing, else after the frame (or the uploads).</summary>
		private void AfterGpu(Action action)
		{
			if (!inFrame && stagingPending == 0 && !inFlight)
			{
				action();
			}
			else
			{
				afterGpu.Add(action);
			}
		}

		// --- the uniform ring: each frame's uniform blocks ---

		private const uint RingSize = 64u << 20, AnimationBlockSize = 128 * 64;
		private readonly DekoHeap.Allocation ring, zeroBlock, defaultAttributes;
		private uint ringPosition, ringBase, ringLimit = RingSize;

		/// <summary>Copies a uniform block into the ring with room for <paramref name="range"/> bytes read from it; its GPU address, or 0 when the ring is full.</summary>
		private ulong RingWrite(byte[] data, int length, uint range)
		{
			uint start = (ringPosition + 255u) & ~255u;
			if (start + Math.Max(range, (uint)length) > ringLimit)
			{
				ReportOnce("deko3d: the frame's uniform ring is full; further draws are left out");
				return 0;
			}

			fixed (byte* source = data)
			{
				System.Buffer.MemoryCopy(source, ring.Pointer + start, length, length);
			}

			ringPosition = start + Math.Max(range, (uint)length);
			return ring.Gpu + start;
		}

		// --- state ---

		public void glEnable(int cap) => SetCapability(cap, true);

		public void glDisable(int cap) => SetCapability(cap, false);

		private void SetCapability(int cap, bool enabled)
		{
			switch (cap)
			{
				case GL_DEPTH_TEST:
					depthTest = enabled;
					break;
				case GL_BLEND:
					blend = enabled;
					break;
				case GL_CULL_FACE:
					cullFace = enabled;
					break;
			}
		}

		public void glClearColor(float r, float g, float b, float a)
		{
			clearRed = r;
			clearGreen = g;
			clearBlue = b;
			clearAlpha = a;
		}

		public void glClear(int mask)
		{
			int which = ((mask & GL_COLOR_BUFFER_BIT) != 0 ? 1 : 0) | ((mask & GL_DEPTH_BUFFER_BIT) != 0 ? 2 : 0);
			if (which == 0 || boundFramebuffer != 0 || !EnsureFrame())
			{
				return;
			}

			FlushWorld();
			DekoNative.Clear(which, clearRed, clearGreen, clearBlue, clearAlpha);
		}

		public void glViewport(int x, int y, int width, int height)
		{
			viewportX = x;
			viewportY = y;
			viewportWidth = width;
			viewportHeight = height;
		}

		public void glBlendFunc(int src, int dst)
		{
			blendSource = src;
			blendDestination = dst;
		}

		public void glDepthMask(byte flag) => depthMask = flag != 0;

		public void glDepthFunc(int func) => depthFunc = func;

		public void glCullFace(int mode) => cullMode = mode;

		public void glLineWidth(float width)
		{
		}

		public void glHint(int target, int mode)
		{
		}

		public void glPixelStorei(int pname, int param)
		{
			if (pname == GL_UNPACK_ALIGNMENT)
			{
				unpackAlignment = Math.Max(1, param);
			}
		}

		public int glGetError() => 0;

		public IntPtr glGetString(int name)
		{
			if (!strings.TryGetValue(name, out IntPtr text))
			{
				string value;
				switch (name)
				{
					case GL_VENDOR:
						value = "OpenBVE";
						break;
					case GL_RENDERER:
						value = "deko3d (Nintendo Switch, Tegra X1)";
						break;
					case GL_VERSION:
						value = "OpenGL ES 3.0 (deko3d renderer)";
						break;
					case GL_SHADING_LANGUAGE_VERSION:
						value = "OpenGL ES GLSL ES 3.00 (uam)";
						break;
					case GL_EXTENSIONS:
						value = "GL_EXT_texture_filter_anisotropic";
						break;
					default:
						value = string.Empty;
						break;
				}

				text = Marshal.StringToHGlobalAnsi(value);
				strings[name] = text;
			}

			return text;
		}

		public void glGetFloatv(int pname, float[] parameters)
		{
			if (pname == GL_MAX_TEXTURE_MAX_ANISOTROPY)
			{
				parameters[0] = 16.0f;
				return;
			}

			int[] values = new int[parameters.Length];
			glGetIntegerv(pname, values);
			for (int i = 0; i < parameters.Length; i++)
			{
				parameters[i] = values[i];
			}
		}

		public void glGetIntegerv(int pname, int[] parameters)
		{
			switch (pname)
			{
				case GL_MAX_TEXTURE_SIZE:
					parameters[0] = 16384;
					break;
				case GL_VIEWPORT:
					parameters[0] = viewportX;
					parameters[1] = viewportY;
					parameters[2] = viewportWidth;
					parameters[3] = viewportHeight;
					break;
				case GL_MAX_VERTEX_ATTRIBS:
					parameters[0] = Attributes;
					break;
				case GL_MAX_TEXTURE_IMAGE_UNITS:
					parameters[0] = Units;
					break;
				default:
					parameters[0] = 0;
					break;
			}
		}

		public void glReadPixels(int x, int y, int width, int height, int format, int type, byte[] pixels)
		{
			ReportOnce("glReadPixels is not supported by the deko3d renderer: the pixels read as zero");
			Array.Clear(pixels, 0, pixels.Length);
		}

		// --- programs (as VulkanGL.Programs) ---

		private const int SamplerLocation = 1000;

		private enum ProgramKind
		{
			Unsupported,
			Default,
			Rectangle,
			Text
		}

		private sealed class GLShader
		{
			internal int Type;
			internal string Source = string.Empty;
		}

		private sealed class GLProgram
		{
			internal readonly List<GLShader> Shaders = new List<GLShader>();
			internal ProgramKind Kind;
			internal byte[] Data = Array.Empty<byte>();
			internal bool Dirty = true;
			internal bool NormalDirty = true;
			internal int BlockBinding;
			internal ulong RingSerial;
			internal ulong RingAddress;
		}

		// Byte offsets in default.vert's DrawBlock.
		private const int DefaultBlockSize = 464, ModelViewOffset = 64, NormalMatrixOffset = 192;

		private static readonly Dictionary<string, int> DefaultUniforms = new Dictionary<string, int>
		{
			{ "uCurrentProjectionMatrix", 0 },
			{ "uCurrentModelViewMatrix", ModelViewOffset },
			{ "uCurrentTextureMatrix", 128 },
			{ "uLight.position", 256 },
			{ "uLight.ambient", 272 },
			{ "uLight.diffuse", 288 },
			{ "uLight.specular", 304 },
			{ "uLight.lightModel", 320 },
			{ "uMaterial.ambient", 336 },
			{ "uMaterial.diffuse", 352 },
			{ "uMaterial.specular", 368 },
			{ "uMaterial.emission", 384 },
			{ "uMaterial.shininess", 396 },
			{ "uMaterialFlags", 400 },
			{ "uIsLight", 404 },
			{ "uIsFog", 408 },
			{ "uFogIsLinear", 412 },
			{ "uBrightness", 416 },
			{ "uOpacity", 420 },
			{ "uAlphaTest", 424 },
			{ "uFogStart", 432 },
			{ "uFogEnd", 436 },
			{ "uFogDensity", 440 },
			{ "uFogColor", 448 }
		};

		// Byte offsets in rectangle.vert's Block2D (rectangle and text).
		private const int Block2DSize = 208;

		private static readonly Dictionary<string, int> Uniforms2D = new Dictionary<string, int>
		{
			{ "uCurrentProjectionMatrix", 0 },
			{ "uCurrentModelViewMatrix", 64 },
			{ "uPoint", 128 },
			{ "uSize", 136 },
			{ "uCoordinates", 144 },
			{ "uAtlasLocation", 160 },
			{ "uColor", 176 },
			{ "uAlphaTest", 192 }
		};

		private static readonly string[] DefaultAttributes = { "iPosition", "iNormal", "iUv", "iColor", "iMatrixChain" };

		private readonly Dictionary<int, GLShader> glShaders = new Dictionary<int, GLShader>();
		private readonly Dictionary<int, GLProgram> glPrograms = new Dictionary<int, GLProgram>();
		private GLProgram currentProgram;

		public int glCreateShader(int type)
		{
			int name = nextName++;
			glShaders[name] = new GLShader { Type = type };
			return name;
		}

		public void glDeleteShader(int shader) => glShaders.Remove(shader);

		public void glShaderSource(int shader, int count, IntPtr[] str, int[] length)
		{
			if (!glShaders.TryGetValue(shader, out GLShader target))
			{
				return;
			}

			StringBuilder source = new StringBuilder();
			for (int i = 0; i < count; i++)
			{
				source.Append(length != null && length[i] >= 0 ? Marshal.PtrToStringAnsi(str[i], length[i]) : Marshal.PtrToStringAnsi(str[i]));
			}

			target.Source = source.ToString();
		}

		public void glCompileShader(int shader)
		{
		}

		public void glGetShaderiv(int shader, int pname, int[] parameters) => parameters[0] = pname == GL_COMPILE_STATUS ? 1 : 0;

		public void glGetShaderInfoLog(int shader, int bufferSize, int[] length, StringBuilder infoLog) => infoLog?.Clear();

		public int glCreateProgram()
		{
			int name = nextName++;
			glPrograms[name] = new GLProgram();
			return name;
		}

		public void glDeleteProgram(int program)
		{
			if (glPrograms.TryGetValue(program, out GLProgram deleted) && currentProgram == deleted)
			{
				currentProgram = null;
			}

			glPrograms.Remove(program);
		}

		public void glAttachShader(int program, int shader)
		{
			if (glPrograms.TryGetValue(program, out GLProgram target) && glShaders.TryGetValue(shader, out GLShader attached))
			{
				target.Shaders.Add(attached);
			}
		}

		public void glLinkProgram(int program)
		{
			if (!glPrograms.TryGetValue(program, out GLProgram target))
			{
				return;
			}

			string vertex = string.Empty, fragment = string.Empty;
			foreach (GLShader shader in target.Shaders)
			{
				if (shader.Type == GL_VERTEX_SHADER)
				{
					vertex = shader.Source;
				}
				else if (shader.Type == GL_FRAGMENT_SHADER)
				{
					fragment = shader.Source;
				}
			}

			// Upstream's programs, by what their sources declare (as VulkanGL).
			if (fragment.Contains("uObjectIndex"))
			{
				target.Kind = ProgramKind.Unsupported;
				ReportOnce("the picking shader is not supported by the deko3d renderer");
			}
			else if (fragment.Contains("uAlphaCutoff"))
			{
				target.Kind = ProgramKind.Unsupported;
				ReportOnce("shadows are not drawn by the deko3d renderer yet");
			}
			else if (vertex.Contains("uAnimationMatricies") && fragment.Contains("uTexture"))
			{
				target.Kind = ProgramKind.Default;
			}
			else if (vertex.Contains("uAtlasLocation"))
			{
				target.Kind = ProgramKind.Text;
			}
			else if (vertex.Contains("uCoordinates"))
			{
				target.Kind = ProgramKind.Rectangle;
			}
			else
			{
				target.Kind = ProgramKind.Unsupported;
				ReportOnce("a shader program the deko3d renderer does not know is not drawn");
			}

			target.Data = new byte[target.Kind == ProgramKind.Default ? DefaultBlockSize : Block2DSize];
			target.Dirty = true;
			target.NormalDirty = true;
		}

		public void glUseProgram(int program) => currentProgram = program != 0 && glPrograms.TryGetValue(program, out GLProgram used) ? used : null;

		public void glGetProgramiv(int program, int pname, int[] parameters) => parameters[0] = pname == GL_LINK_STATUS ? 1 : 0;

		public void glGetProgramInfoLog(int program, int bufferSize, int[] length, StringBuilder infoLog) => infoLog?.Clear();

		public int glGetAttribLocation(int program, string name) =>
			glPrograms.TryGetValue(program, out GLProgram target) && target.Kind == ProgramKind.Default ? Array.IndexOf(DefaultAttributes, name) : -1;

		public int glGetUniformLocation(int program, string name)
		{
			if (!glPrograms.TryGetValue(program, out GLProgram target) || target.Kind == ProgramKind.Unsupported)
			{
				return -1;
			}

			if (name == "uTexture" || name.StartsWith("uShadowMap", StringComparison.Ordinal))
			{
				return SamplerLocation;
			}

			Dictionary<string, int> table = target.Kind == ProgramKind.Default ? DefaultUniforms : Uniforms2D;
			return table.TryGetValue(name, out int offset) ? offset / 4 : -1;
		}

		public int glGetUniformBlockIndex(int program, string name) =>
			glPrograms.TryGetValue(program, out GLProgram target) && target.Kind == ProgramKind.Default && name == "uAnimationMatricies" ? 0 : -1;

		public void glUniformBlockBinding(int program, int blockIndex, int binding)
		{
			if (blockIndex == 0 && glPrograms.TryGetValue(program, out GLProgram target))
			{
				target.BlockBinding = binding;
			}
		}

		private byte[] uniformData;

		/// <summary>Where a uniform write goes in the current program's block, or -1 if nowhere.</summary>
		private int UniformTarget(int location, int bytes)
		{
			GLProgram program = currentProgram;
			if (program == null || location < 0 || location == SamplerLocation)
			{
				return -1;
			}

			int offset = location * 4;
			if (offset + bytes > program.Data.Length)
			{
				return -1;
			}

			uniformData = program.Data;
			program.Dirty = true;
			if (program.Kind == ProgramKind.Default)
			{
				// The world's frame block is made from this program's values (DekoGL.World).
				worldFrameDirty = true;
			}
			if (program.Kind == ProgramKind.Default && offset < ModelViewOffset + 64 && offset + bytes > ModelViewOffset)
			{
				program.NormalDirty = true;
			}

			return offset;
		}

		private void WriteUniform<T>(int offset, int index, T value) where T : unmanaged => Unsafe.WriteUnaligned(ref uniformData[offset + index * 4], value);

		public void glUniform1i(int location, int v0)
		{
			int offset = UniformTarget(location, 4);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
			}
		}

		public void glUniform1f(int location, float v0)
		{
			int offset = UniformTarget(location, 4);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
			}
		}

		public void glUniform2f(int location, float v0, float v1)
		{
			int offset = UniformTarget(location, 8);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
				WriteUniform(offset, 1, v1);
			}
		}

		public void glUniform3f(int location, float v0, float v1, float v2)
		{
			int offset = UniformTarget(location, 12);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
				WriteUniform(offset, 1, v1);
				WriteUniform(offset, 2, v2);
			}
		}

		public void glUniform4f(int location, float v0, float v1, float v2, float v3)
		{
			int offset = UniformTarget(location, 16);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
				WriteUniform(offset, 1, v1);
				WriteUniform(offset, 2, v2);
				WriteUniform(offset, 3, v3);
			}
		}

		public void glUniform1fv(int location, int count, float[] value)
		{
			if (count == 1)
			{
				glUniform1f(location, value[0]);
			}
		}

		public void glUniformMatrix4fv(int location, int count, byte transpose, float[] value)
		{
			int offset = UniformTarget(location, 64);
			if (offset < 0)
			{
				return;
			}

			for (int column = 0; column < 4; column++)
			{
				for (int row = 0; row < 4; row++)
				{
					WriteUniform(offset, column * 4 + row, transpose != 0 ? value[row * 4 + column] : value[column * 4 + row]);
				}
			}
		}

		/// <summary>The default program's normal matrix, transpose(inverse(mat3(modelview))), into its block.</summary>
		private static void UpdateNormalMatrix(GLProgram program)
		{
			fixed (byte* data = program.Data)
			{
				float* m = (float*)(data + ModelViewOffset);
				float* n = (float*)(data + NormalMatrixOffset);
				float a00 = m[0], a10 = m[1], a20 = m[2];
				float a01 = m[4], a11 = m[5], a21 = m[6];
				float a02 = m[8], a12 = m[9], a22 = m[10];
				float c00 = a11 * a22 - a12 * a21, c01 = a12 * a20 - a10 * a22, c02 = a10 * a21 - a11 * a20;
				float c10 = a02 * a21 - a01 * a22, c11 = a00 * a22 - a02 * a20, c12 = a01 * a20 - a00 * a21;
				float c20 = a01 * a12 - a02 * a11, c21 = a02 * a10 - a00 * a12, c22 = a00 * a11 - a01 * a10;
				float determinant = a00 * c00 + a01 * c01 + a02 * c02;
				float scale = Math.Abs(determinant) > 1e-20f ? 1.0f / determinant : 1.0f;
				n[0] = c00 * scale;
				n[1] = c10 * scale;
				n[2] = c20 * scale;
				n[3] = 0.0f;
				n[4] = c01 * scale;
				n[5] = c11 * scale;
				n[6] = c21 * scale;
				n[7] = 0.0f;
				n[8] = c02 * scale;
				n[9] = c12 * scale;
				n[10] = c22 * scale;
				n[11] = 0.0f;
				n[12] = n[13] = n[14] = 0.0f;
				n[15] = 1.0f;
			}

			program.NormalDirty = false;
		}

		// --- buffers and vertex arrays (as VulkanGL.Buffers) ---

		private sealed class GLBuffer
		{
			internal DekoHeap.Allocation Memory;
			internal int Size;
			/// <summary>The serial of the frame that last drew with it.</summary>
			internal ulong LastUse;
			internal bool Uniform;
			internal byte[] Shadow;
			internal int Version;
			internal int RingVersion = -1;
			internal ulong RingSerial;
			internal ulong RingAddress;
		}

		private struct Attribute
		{
			internal bool Enabled;
			internal GLBuffer Buffer;
			internal int Size;
			internal int Type;
			internal bool Normalized;
			internal bool Integer;
			internal int Stride;
			internal long Offset;
		}

		private sealed class GLVertexArray
		{
			internal readonly Attribute[] Attributes = new Attribute[DekoGL.Attributes];
			internal GLBuffer Elements;

			// The world program's view of the attributes (DekoGL.World's VertexSource), worked out again only when they change.
			internal int Version, SourceVersion = -1, SourceLayout;
			internal GLBuffer SourceVertices;
			internal uint SourceStride;
			internal bool SourceFound;
		}

		private readonly Dictionary<int, GLBuffer> glBuffers = new Dictionary<int, GLBuffer>();
		private readonly Dictionary<int, GLVertexArray> vertexArrays = new Dictionary<int, GLVertexArray>();
		private readonly GLVertexArray defaultVertexArray = new GLVertexArray();
		private GLVertexArray vertexArray;
		private GLBuffer arrayBuffer, uniformBuffer;
		private readonly GLBuffer[] uniformBindings = new GLBuffer[8];

		public void glGenBuffers(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				names[i] = nextName++;
				glBuffers[names[i]] = new GLBuffer();
			}
		}

		public void glDeleteBuffers(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				if (names[i] == 0 || !glBuffers.TryGetValue(names[i], out GLBuffer buffer))
				{
					continue;
				}

				glBuffers.Remove(names[i]);
				if (arrayBuffer == buffer)
				{
					arrayBuffer = null;
				}

				if (uniformBuffer == buffer)
				{
					uniformBuffer = null;
				}

				for (int binding = 0; binding < uniformBindings.Length; binding++)
				{
					if (uniformBindings[binding] == buffer)
					{
						uniformBindings[binding] = null;
					}
				}

				if (vertexArray != null && vertexArray.Elements == buffer)
				{
					vertexArray.Elements = null;
				}

				ReleaseMemory(buffer);
				buffer.Shadow = null;
			}
		}

		private void ReleaseMemory(GLBuffer buffer)
		{
			if (buffer.Memory.IsNull)
			{
				return;
			}

			DekoHeap.Allocation memory = buffer.Memory;
			if (buffer.LastUse <= completedSerial)
			{
				bufferHeap.Free(memory);
			}
			else
			{
				afterGpu.Add(() => bufferHeap.Free(memory));
			}

			buffer.Memory = default;
		}

		private GLBuffer Named(int name)
		{
			if (name == 0)
			{
				return null;
			}

			if (!glBuffers.TryGetValue(name, out GLBuffer buffer))
			{
				buffer = new GLBuffer();
				glBuffers[name] = buffer;
			}

			return buffer;
		}

		private GLVertexArray CurrentVertexArray => vertexArray ?? defaultVertexArray;

		public void glBindBuffer(int target, int buffer)
		{
			switch (target)
			{
				case GL_ARRAY_BUFFER:
					arrayBuffer = Named(buffer);
					break;
				case GL_ELEMENT_ARRAY_BUFFER:
					CurrentVertexArray.Elements = Named(buffer);
					break;
				case GL_UNIFORM_BUFFER:
					uniformBuffer = Named(buffer);
					break;
			}
		}

		public void glBindBufferBase(int target, int index, int buffer)
		{
			if (target != GL_UNIFORM_BUFFER || index < 0 || index >= uniformBindings.Length)
			{
				return;
			}

			uniformBindings[index] = Named(buffer);
			uniformBuffer = uniformBindings[index];
		}

		private GLBuffer Target(int target)
		{
			switch (target)
			{
				case GL_ARRAY_BUFFER:
					return arrayBuffer;
				case GL_ELEMENT_ARRAY_BUFFER:
					return CurrentVertexArray.Elements;
				case GL_UNIFORM_BUFFER:
					return uniformBuffer;
				default:
					return null;
			}
		}

		public void glBufferData(int target, IntPtr size, IntPtr data, int usage)
		{
			GLBuffer buffer = Target(target);
			int length = (int)size;
			if (buffer == null || length < 0)
			{
				return;
			}

			if (target == GL_UNIFORM_BUFFER || buffer.Uniform)
			{
				buffer.Uniform = true;
				if (buffer.Shadow == null || buffer.Shadow.Length != length)
				{
					buffer.Shadow = new byte[length];
				}

				if (data != IntPtr.Zero)
				{
					new ReadOnlySpan<byte>((void*)data, length).CopyTo(buffer.Shadow);
				}

				buffer.Size = length;
				buffer.Version++;
				return;
			}

			// The same memory if it is big enough and this frame hasn't drawn with it; otherwise new.
			if (buffer.Memory.IsNull || buffer.Memory.Size < (uint)length || buffer.LastUse > completedSerial)
			{
				ReleaseMemory(buffer);
				buffer.Memory = bufferHeap.Allocate((ulong)Math.Max(length, 4));
				buffer.LastUse = 0;
			}

			buffer.Size = length;
			if (data != IntPtr.Zero && length > 0)
			{
				System.Buffer.MemoryCopy((void*)data, buffer.Memory.Pointer, length, length);
			}
		}

		public void glBufferSubData(int target, IntPtr offset, IntPtr size, IntPtr data)
		{
			GLBuffer buffer = Target(target);
			long start = (long)offset;
			int length = (int)size;
			if (buffer == null || data == IntPtr.Zero || length <= 0 || start < 0 || start + length > buffer.Size)
			{
				return;
			}

			if (buffer.Uniform)
			{
				new ReadOnlySpan<byte>((void*)data, length).CopyTo(new Span<byte>(buffer.Shadow, (int)start, length));
				buffer.Version++;
				return;
			}

			if (buffer.Memory.IsNull)
			{
				return;
			}

			if (buffer.LastUse > completedSerial)
			{
				// Copy on write: this frame has drawn with what is there.
				DekoHeap.Allocation copy = bufferHeap.Allocate(buffer.Memory.Size);
				System.Buffer.MemoryCopy(buffer.Memory.Pointer, copy.Pointer, buffer.Size, buffer.Size);
				ReleaseMemory(buffer);
				buffer.Memory = copy;
				buffer.LastUse = 0;
			}

			System.Buffer.MemoryCopy((void*)data, buffer.Memory.Pointer + start, length, length);
		}

		public void glGenVertexArrays(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				names[i] = nextName++;
				vertexArrays[names[i]] = new GLVertexArray();
			}
		}

		public void glDeleteVertexArrays(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				if (vertexArrays.TryGetValue(names[i], out GLVertexArray array))
				{
					vertexArrays.Remove(names[i]);
					if (vertexArray == array)
					{
						vertexArray = null;
					}
				}
			}
		}

		public void glBindVertexArray(int array)
		{
			if (array == 0)
			{
				vertexArray = null;
				return;
			}

			if (!vertexArrays.TryGetValue(array, out vertexArray))
			{
				vertexArray = new GLVertexArray();
				vertexArrays[array] = vertexArray;
			}
		}

		public void glEnableVertexAttribArray(int index)
		{
			if (index >= 0 && index < Attributes)
			{
				CurrentVertexArray.Attributes[index].Enabled = true;
				CurrentVertexArray.Version++;
			}
		}

		public void glDisableVertexAttribArray(int index)
		{
			if (index >= 0 && index < Attributes)
			{
				CurrentVertexArray.Attributes[index].Enabled = false;
				CurrentVertexArray.Version++;
			}
		}

		public void glVertexAttribPointer(int index, int size, int type, byte normalized, int stride, IntPtr pointer) => SetAttribute(index, size, type, normalized != 0, false, stride, pointer);

		public void glVertexAttribIPointer(int index, int size, int type, int stride, IntPtr pointer) => SetAttribute(index, size, type, false, true, stride, pointer);

		private void SetAttribute(int index, int size, int type, bool normalized, bool integer, int stride, IntPtr pointer)
		{
			if (index < 0 || index >= Attributes)
			{
				return;
			}

			CurrentVertexArray.Version++;
			ref Attribute attribute = ref CurrentVertexArray.Attributes[index];
			attribute.Buffer = arrayBuffer;
			attribute.Size = size;
			attribute.Type = type;
			attribute.Normalized = normalized;
			attribute.Integer = integer;
			attribute.Stride = stride != 0 ? stride : size * TypeBytes(type);
			attribute.Offset = (long)pointer;
		}

		private static int TypeBytes(int type)
		{
			switch (type)
			{
				case GL_BYTE:
				case GL_UNSIGNED_BYTE:
					return 1;
				case GL_SHORT:
				case GL_UNSIGNED_SHORT:
					return 2;
				default:
					return 4;
			}
		}

		/// <summary>A GL vertex attribute's format as deko3d's (DkVtxAttribSize | DkVtxAttribType &lt;&lt; 8), 0 if not supported.</summary>
		private static uint AttributeFormat(int type, int size, bool normalized, bool integer)
		{
			if (size < 1 || size > 4)
			{
				return 0;
			}

			int n = size - 1;
			switch (type)
			{
				case GL_FLOAT:
					return DekoNative.Size32[n] | DekoNative.AttribFloat << 8;
				case GL_INT:
					return integer ? DekoNative.Size32[n] | DekoNative.AttribSint << 8 : 0;
				case GL_UNSIGNED_INT:
					return integer ? DekoNative.Size32[n] | DekoNative.AttribUint << 8 : 0;
				case GL_UNSIGNED_BYTE:
					return normalized ? DekoNative.Size8[n] | DekoNative.AttribUnorm << 8 : integer ? DekoNative.Size8[n] | DekoNative.AttribUint << 8 : 0;
				case GL_BYTE:
					return normalized ? DekoNative.Size8[n] | DekoNative.AttribSnorm << 8 : 0;
				case GL_UNSIGNED_SHORT:
					return normalized ? DekoNative.Size16[n] | DekoNative.AttribUnorm << 8 : 0;
				case GL_SHORT:
					return normalized ? DekoNative.Size16[n] | DekoNative.AttribSnorm << 8 : 0;
				default:
					return 0;
			}
		}

		// --- textures (as VulkanGL.Textures) ---

		private sealed class GLTexture
		{
			internal int MinFilter = GL_NEAREST_MIPMAP_LINEAR, MagFilter = GL_LINEAR, WrapS = GL_REPEAT, WrapT = GL_REPEAT;
			internal float Anisotropy = 1.0f;
			// Level 0, waiting to be uploaded.
			internal DekoHeap.Allocation Staging;
			internal bool Pending, WantMips;
			internal int Width, Height, Format;
			internal byte[] Swizzle = { DekoNative.SwizzleRed, DekoNative.SwizzleGreen, DekoNative.SwizzleBlue, DekoNative.SwizzleAlpha };
			internal int[] GLSwizzle;
			// On the GPU: its descriptor slot (0 for none: the black texture's).
			internal DekoHeap.Allocation Image;
			internal int Slot;
			internal bool HasMips;
		}

		private const int TextureSlots = 32768, SamplerSlots = 256;
		private readonly Dictionary<int, GLTexture> textures = new Dictionary<int, GLTexture>();
		private readonly GLTexture[] units = new GLTexture[Units];
		private int activeUnit;
		private GLTexture blackTexture;
		private readonly Stack<int> freeSlots = new Stack<int>();
		private int nextSlot = 1;
		private readonly Dictionary<int, int> samplerSlots = new Dictionary<int, int>();
		private readonly List<GLTexture> waitingTextures = new List<GLTexture>();
		private ulong waitingBytes, stagingPending;
		private const ulong StagingBudget = 64UL << 20;

		public void glGenTextures(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				names[i] = nextName++;
				textures[names[i]] = new GLTexture();
			}
		}

		public void glDeleteTextures(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				if (names[i] == 0 || !textures.TryGetValue(names[i], out GLTexture texture))
				{
					continue;
				}

				textures.Remove(names[i]);
				for (int unit = 0; unit < Units; unit++)
				{
					if (units[unit] == texture)
					{
						units[unit] = null;
					}
				}

				ReleaseTexture(texture);
			}
		}

		private void ReleaseTexture(GLTexture texture)
		{
			if (texture.Pending)
			{
				// Never recorded: free now.
				bufferHeap.Free(texture.Staging);
				texture.Staging = default;
				texture.Pending = false;
			}

			ReleaseImage(texture);
		}

		private void ReleaseImage(GLTexture texture)
		{
			if (texture.Slot == 0)
			{
				return;
			}

			int slot = texture.Slot;
			DekoHeap.Allocation image = texture.Image;
			AfterGpu(() =>
			{
				imageHeap.Free(image);
				freeSlots.Push(slot);
			});
			texture.Slot = 0;
			texture.Image = default;
			texture.HasMips = false;
		}

		public void glBindTexture(int target, int texture)
		{
			if (target != GL_TEXTURE_2D)
			{
				return;
			}

			GLTexture bound = null;
			if (texture != 0 && !textures.TryGetValue(texture, out bound))
			{
				bound = new GLTexture();
				textures[texture] = bound;
			}

			units[activeUnit] = bound;
		}

		public void glActiveTexture(int texture) => activeUnit = Math.Max(0, Math.Min(Units - 1, texture - GL_TEXTURE0));

		public void glGenerateMipmap(int target)
		{
			GLTexture texture = units[activeUnit];
			if (texture == null)
			{
				return;
			}

			if (texture.Pending)
			{
				texture.WantMips = true;
			}
			else if (texture.Slot != 0 && !texture.HasMips)
			{
				ReportOnce("glGenerateMipmap on a texture already drawn with is not supported by the deko3d renderer");
			}
		}

		public void glTexParameteri(int target, int pname, int param) => TexParameter(pname, param);

		public void glTexParameterf(int target, int pname, float param) => TexParameter(pname, param);

		public void glTexParameteriv(int target, int pname, int[] parameters)
		{
			if (pname == GL_TEXTURE_SWIZZLE_RGBA && parameters.Length >= 4)
			{
				for (int i = 0; i < 4; i++)
				{
					TexParameter(GL_TEXTURE_SWIZZLE_R + i, parameters[i]);
				}

				return;
			}

			if (parameters.Length > 0)
			{
				TexParameter(pname, parameters[0]);
			}
		}

		public void glTexParameterfv(int target, int pname, float[] parameters)
		{
			if (parameters.Length > 0)
			{
				TexParameter(pname, parameters[0]);
			}
		}

		private void TexParameter(int pname, float value)
		{
			GLTexture texture = units[activeUnit];
			if (texture == null)
			{
				return;
			}

			switch (pname)
			{
				case GL_TEXTURE_MIN_FILTER:
					texture.MinFilter = (int)value;
					break;
				case GL_TEXTURE_MAG_FILTER:
					texture.MagFilter = (int)value;
					break;
				case GL_TEXTURE_WRAP_S:
					texture.WrapS = (int)value;
					break;
				case GL_TEXTURE_WRAP_T:
					texture.WrapT = (int)value;
					break;
				case GL_TEXTURE_MAX_ANISOTROPY:
					texture.Anisotropy = Math.Max(1.0f, value);
					break;
				case GL_TEXTURE_SWIZZLE_R:
				case GL_TEXTURE_SWIZZLE_R + 1:
				case GL_TEXTURE_SWIZZLE_R + 2:
				case GL_TEXTURE_SWIZZLE_R + 3:
					if (texture.Slot != 0 && !texture.Pending)
					{
						ReportOnce("a texture swizzle set on a texture already drawn with is not supported by the deko3d renderer");
					}

					texture.GLSwizzle = texture.GLSwizzle ?? new[] { GL_RED, GL_GREEN, GL_BLUE, GL_ALPHA };
					texture.GLSwizzle[pname - GL_TEXTURE_SWIZZLE_R] = (int)value;
					break;
			}
		}

		/// <summary>The view's swizzle: GL's own (if any) read through the format's.</summary>
		private static byte[] ViewSwizzle(GLTexture texture)
		{
			if (texture.GLSwizzle == null)
			{
				return texture.Swizzle;
			}

			byte[] result = new byte[4];
			for (int i = 0; i < 4; i++)
			{
				switch (texture.GLSwizzle[i])
				{
					case GL_RED:
						result[i] = texture.Swizzle[0];
						break;
					case GL_GREEN:
						result[i] = texture.Swizzle[1];
						break;
					case GL_BLUE:
						result[i] = texture.Swizzle[2];
						break;
					case GL_ALPHA:
						result[i] = texture.Swizzle[3];
						break;
					case GL_ONE:
						result[i] = DekoNative.SwizzleOne;
						break;
					default:
						result[i] = DekoNative.SwizzleZero;
						break;
				}
			}

			return result;
		}

		public void glTexSubImage2D(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, IntPtr pixels)
		{
			GLTexture texture = units[activeUnit];
			if (texture == null || target != GL_TEXTURE_2D)
			{
				return;
			}

			if (level != 0 || xoffset != 0 || yoffset != 0 || width != texture.Width || height != texture.Height)
			{
				ReportOnce("glTexSubImage2D other than the whole of level 0 is not supported by the deko3d renderer");
				return;
			}

			glTexImage2D(target, level, format, width, height, 0, format, type, pixels);
		}

		public void glCopyTexImage2D(int target, int level, int internalFormat, int x, int y, int width, int height, int border) =>
			ReportOnce("glCopyTexImage2D is not supported by the deko3d renderer");

		public void glTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr pixels)
		{
			GLTexture texture = units[activeUnit];
			if (texture == null || target != GL_TEXTURE_2D || width <= 0 || height <= 0)
			{
				return;
			}

			if (level != 0)
			{
				ReportOnce("texture levels other than 0 are not supported by the deko3d renderer");
				return;
			}

			RealizeWaiting();
			if (texture.Pending)
			{
				bufferHeap.Free(texture.Staging);
				texture.Staging = default;
			}

			texture.Staging = Convert(format, type, width, height, pixels, out texture.Format, texture.Swizzle);
			texture.Width = width;
			texture.Height = height;
			texture.Pending = true;
			texture.WantMips = false;
			waitingTextures.Add(texture);
			waitingBytes += texture.Staging.Size;
		}

		/// <summary>Textures given an image but not drawn with yet: past a budget, made there and then (upstream loads
		/// many ahead of drawing them), their staging going with the uploads.</summary>
		private void RealizeWaiting()
		{
			if (waitingBytes <= StagingBudget)
			{
				return;
			}

			foreach (GLTexture waiting in waitingTextures)
			{
				if (waiting.Pending)
				{
					Realize(waiting);
				}
			}

			waitingTextures.Clear();
			waitingBytes = 0;
		}

		/// <summary>Copies a GL image into staging memory in a format deko3d samples the same way: tightly packed rows, RGB
		/// widened to RGBA, luminance and alpha as one- and two-channel images read through a swizzle, 16-bit kept.</summary>
		private DekoHeap.Allocation Convert(int format, int type, int width, int height, IntPtr pixels, out int dkFormat, byte[] swizzle)
		{
			int sourceBytes, targetBytes, conversion = 0;
			SetSwizzle(swizzle, DekoNative.SwizzleRed, DekoNative.SwizzleGreen, DekoNative.SwizzleBlue, DekoNative.SwizzleAlpha);
			if (type == GL_UNSIGNED_BYTE)
			{
				switch (format)
				{
					case GL_RGBA:
						dkFormat = DekoNative.FormatRGBA8;
						sourceBytes = targetBytes = 4;
						break;
					case GL_RGB:
						dkFormat = DekoNative.FormatRGBA8;
						sourceBytes = 3;
						targetBytes = 4;
						conversion = 1;
						break;
					case GL_LUMINANCE:
						dkFormat = DekoNative.FormatR8;
						sourceBytes = targetBytes = 1;
						SetSwizzle(swizzle, DekoNative.SwizzleRed, DekoNative.SwizzleRed, DekoNative.SwizzleRed, DekoNative.SwizzleOne);
						break;
					case GL_LUMINANCE_ALPHA:
						dkFormat = DekoNative.FormatRG8;
						sourceBytes = targetBytes = 2;
						SetSwizzle(swizzle, DekoNative.SwizzleRed, DekoNative.SwizzleRed, DekoNative.SwizzleRed, DekoNative.SwizzleGreen);
						break;
					case GL_ALPHA:
						dkFormat = DekoNative.FormatR8;
						sourceBytes = targetBytes = 1;
						SetSwizzle(swizzle, DekoNative.SwizzleZero, DekoNative.SwizzleZero, DekoNative.SwizzleZero, DekoNative.SwizzleRed);
						break;
					case GL_RED:
						dkFormat = DekoNative.FormatR8;
						sourceBytes = targetBytes = 1;
						break;
					case GL_RG:
						dkFormat = DekoNative.FormatRG8;
						sourceBytes = targetBytes = 2;
						break;
					default:
						return Blank(format, type, width, height, out dkFormat, swizzle);
				}
			}
			else if (type == GL_UNSIGNED_SHORT_5_6_5)
			{
				dkFormat = DekoNative.FormatRGB565;
				sourceBytes = targetBytes = 2;
			}
			else if (type == GL_UNSIGNED_SHORT_4_4_4_4)
			{
				dkFormat = DekoNative.FormatRGBA4;
				sourceBytes = targetBytes = 2;
			}
			else if (type == GL_UNSIGNED_SHORT_5_5_5_1)
			{
				dkFormat = DekoNative.FormatRGB5A1;
				sourceBytes = targetBytes = 2;
			}
			else
			{
				return Blank(format, type, width, height, out dkFormat, swizzle);
			}

			ulong size = (ulong)width * (ulong)height * (ulong)targetBytes;
			DekoHeap.Allocation staging = bufferHeap.Allocate(size, 256);
			byte* target = staging.Pointer;
			if (pixels == IntPtr.Zero)
			{
				new Span<byte>(target, (int)size).Clear();
				return staging;
			}

			int sourceStride = (width * sourceBytes + unpackAlignment - 1) / unpackAlignment * unpackAlignment;
			int targetStride = width * targetBytes;
			byte* source = (byte*)pixels;
			for (int y = 0; y < height; y++)
			{
				byte* from = source + (long)y * sourceStride;
				byte* to = target + (long)y * targetStride;
				if (conversion == 0)
				{
					System.Buffer.MemoryCopy(from, to, targetStride, targetStride);
					continue;
				}

				for (int x = 0; x < width; x++)
				{
					to[0] = from[0];
					to[1] = from[1];
					to[2] = from[2];
					to[3] = 255;
					from += 3;
					to += 4;
				}
			}

			return staging;
		}

		private static void SetSwizzle(byte[] swizzle, byte r, byte g, byte b, byte a)
		{
			swizzle[0] = r;
			swizzle[1] = g;
			swizzle[2] = b;
			swizzle[3] = a;
		}

		private DekoHeap.Allocation Blank(int format, int type, int width, int height, out int dkFormat, byte[] swizzle)
		{
			ReportOnce("texture format 0x" + format.ToString("X") + "/0x" + type.ToString("X") + " is not supported by the deko3d renderer: drawn black");
			dkFormat = DekoNative.FormatRGBA8;
			SetSwizzle(swizzle, DekoNative.SwizzleZero, DekoNative.SwizzleZero, DekoNative.SwizzleZero, DekoNative.SwizzleOne);
			DekoHeap.Allocation staging = bufferHeap.Allocate((ulong)width * (ulong)height * 4, 256);
			new Span<byte>(staging.Pointer, width * height * 4).Clear();
			return staging;
		}

		private int TakeSlot()
		{
			if (freeSlots.Count > 0)
			{
				return freeSlots.Pop();
			}

			if (nextSlot >= TextureSlots)
			{
				ReportOnce("deko3d: more than " + TextureSlots + " textures at once; the rest are drawn black");
				return 0;
			}

			return nextSlot++;
		}

		/// <summary>Makes a pending texture's image: its slot, its memory, the copy of level 0 and the mipmaps, recorded with the uploads.</summary>
		private void Realize(GLTexture texture)
		{
			ReleaseImage(texture);
			int levels = 1;
			if (texture.WantMips)
			{
				int largest = Math.Max(texture.Width, texture.Height);
				while ((largest >>= 1) > 0)
				{
					levels++;
				}
			}

			int slot = texture == blackTexture ? 0 : TakeSlot();
			if (slot == 0 && texture != blackTexture)
			{
				bufferHeap.Free(texture.Staging);
				texture.Staging = default;
				texture.Pending = false;
				return;
			}

			DekoNative.ImageSize(texture.Width, texture.Height, levels, texture.Format, out ulong size, out uint alignment);
			texture.Image = imageHeap.Allocate(size, Math.Max(alignment, 256u));
			byte[] swizzle = ViewSwizzle(texture);
			fixed (byte* components = swizzle)
			{
				DekoNative.ImageInit(slot, texture.Width, texture.Height, levels, texture.Format, texture.Image.Block.Handle, texture.Image.Offset, components);
			}

			DekoNative.ImageUpload(slot, texture.Staging.Gpu, texture.Width, texture.Height);
			if (levels > 1)
			{
				DekoNative.ImageMipmaps(slot, texture.Width, texture.Height, levels);
			}

			DekoHeap.Allocation staging = texture.Staging;
			afterGpu.Add(() => bufferHeap.Free(staging));
			stagingPending += staging.Size;
			texture.Staging = default;
			texture.Pending = false;
			texture.HasMips = levels > 1;
			texture.Slot = slot;
			if (!inFrame && stagingPending > StagingBudget)
			{
				// Loading: the uploads run now, their staging comes back.
				DekoNative.FlushUploads();
				RunAfterGpu();
			}
		}

		/// <summary>The texture's sampler state as a number: filters, wrapping, anisotropy, whether it has mipmaps.</summary>
		private static int SamplerKey(GLTexture texture)
		{
			int min;
			switch (texture.MinFilter)
			{
				case GL_NEAREST:
					min = 0;
					break;
				case GL_LINEAR:
					min = 1;
					break;
				case GL_NEAREST_MIPMAP_NEAREST:
					min = 2;
					break;
				case GL_LINEAR_MIPMAP_NEAREST:
					min = 3;
					break;
				case GL_NEAREST_MIPMAP_LINEAR:
					min = 4;
					break;
				default:
					min = 5;
					break;
			}

			int anisotropy = texture.HasMips ? Math.Min(16, (int)Math.Round(texture.Anisotropy)) : 1;
			return min | (texture.MagFilter == GL_NEAREST ? 0 : 1) << 3 | Wrap(texture.WrapS) << 4 | Wrap(texture.WrapT) << 6 | anisotropy << 8 | (texture.HasMips ? 1 : 0) << 13;
		}

		private static int Wrap(int mode) => mode == GL_REPEAT ? 0 : mode == GL_MIRRORED_REPEAT ? 2 : 1;

		private int SamplerSlot(int key)
		{
			if (samplerSlots.TryGetValue(key, out int slot))
			{
				return slot;
			}

			if (samplerSlots.Count >= SamplerSlots)
			{
				ReportOnce("deko3d: more than " + SamplerSlots + " kinds of sampler; the first is used for the rest");
				return 0;
			}

			slot = samplerSlots.Count;
			int min = key & 7;
			bool mips = (key >> 13 & 1) != 0 && min >= 2;
			int[] wraps = { DekoNative.WrapRepeat, DekoNative.WrapClampToEdge, DekoNative.WrapMirroredRepeat };
			DekoNative.Sampler(slot,
				min == 1 || min == 3 || min == 5 ? DekoNative.FilterLinear : DekoNative.FilterNearest,
				(key >> 3 & 1) != 0 ? DekoNative.FilterLinear : DekoNative.FilterNearest,
				!mips ? DekoNative.MipNone : min == 4 || min == 5 ? DekoNative.MipLinear : DekoNative.MipNearest,
				wraps[key >> 4 & 3], wraps[key >> 6 & 3], mips ? key >> 8 & 31 : 1);
			samplerSlots[key] = slot;
			return slot;
		}

		/// <summary>The 1x1 opaque black texture in slot 0: what an unbound or empty texture samples as in GL.</summary>
		private void CreateBlackTexture()
		{
			blackTexture = new GLTexture { MinFilter = GL_NEAREST, MagFilter = GL_NEAREST };
			blackTexture.Staging = bufferHeap.Allocate(4, 256);
			byte* pixel = blackTexture.Staging.Pointer;
			pixel[0] = pixel[1] = pixel[2] = 0;
			pixel[3] = 255;
			blackTexture.Width = blackTexture.Height = 1;
			blackTexture.Format = DekoNative.FormatRGBA8;
			blackTexture.Pending = true;
			Realize(blackTexture);
		}

		// --- framebuffers: shadows and picking, not drawn yet ---

		private int boundFramebuffer;

		public void glGenFramebuffers(int n, int[] framebuffers)
		{
			for (int i = 0; i < n && i < framebuffers.Length; i++)
			{
				framebuffers[i] = nextName++;
			}
		}

		public void glDeleteFramebuffers(int n, int[] framebuffers)
		{
		}

		public void glBindFramebuffer(int target, int framebuffer) => boundFramebuffer = framebuffer;

		public void glFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level)
		{
		}

		public void glFramebufferRenderbuffer(int target, int attachment, int renderbuffertarget, int renderbuffer)
		{
		}

		public int glCheckFramebufferStatus(int target)
		{
			ReportOnce("framebuffers (shadows, picking) are not supported by the deko3d renderer yet");
			return 0;
		}

		public void glGenRenderbuffers(int n, int[] renderbuffers)
		{
			for (int i = 0; i < n && i < renderbuffers.Length; i++)
			{
				renderbuffers[i] = nextName++;
			}
		}

		public void glDeleteRenderbuffers(int n, int[] renderbuffers)
		{
		}

		public void glBindRenderbuffer(int target, int renderbuffer)
		{
		}

		public void glRenderbufferStorage(int target, int internalFormat, int width, int height)
		{
		}

		public void glDrawBuffers(int n, int[] buffers)
		{
		}

		public void glReadBuffer(int mode)
		{
		}

		// --- draws (as VulkanGL.Draw's PrepareDraw) ---

		public void glDrawArrays(int mode, int first, int count)
		{
			FlushWorld();
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			Draw3D draw = default;
			if (count > 0 && Prepare(mode, ref draw))
			{
				draw.First = (uint)Math.Max(0, first);
				draw.Count = (uint)count;
				DekoNative.Draw(&draw);
				frameDraws++;
			}

			frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
		}

		public void glDrawElements(int mode, int count, int type, IntPtr indices)
		{
			FlushWorld();
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			GLBuffer elements = CurrentVertexArray.Elements;
			int indexBytes = type == GL_UNSIGNED_INT ? 4 : type == GL_UNSIGNED_SHORT ? 2 : type == GL_UNSIGNED_BYTE ? 1 : 0;
			long offset = (long)indices;
			Draw3D draw = default;
			if (count > 0 && elements != null && !elements.Memory.IsNull && indexBytes != 0 && offset % indexBytes == 0 && Prepare(mode, ref draw))
			{
				elements.LastUse = serial;
				draw.IndexAddress = elements.Memory.Gpu;
				draw.IndexFormat = indexBytes == 1 ? 1u : indexBytes == 2 ? 2u : 3u;
				draw.First = (uint)(offset / indexBytes);
				draw.Count = (uint)count;
				DekoNative.Draw(&draw);
				frameDraws++;
			}

			frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
		}

		/// <summary>Everything a draw needs but its vertices' range: false when it can't be drawn.</summary>
		private bool Prepare(int mode, ref Draw3D draw)
		{
			GLProgram program = currentProgram;
			if (program == null || program.Kind == ProgramKind.Unsupported || boundFramebuffer != 0)
			{
				return false;
			}

			if (mode == GL_POINTS)
			{
				ReportOnce("points are not supported by the deko3d renderer");
				return false;
			}

			GLVertexArray array = CurrentVertexArray;
			bool usesVertices = program.Kind == ProgramKind.Default;
			if (usesVertices)
			{
				for (int i = 0; i < DefaultLocations; i++)
				{
					Attribute attribute = array.Attributes[i];
					if (attribute.Enabled && attribute.Buffer != null && attribute.Buffer.Memory.IsNull)
					{
						return false;
					}
				}
			}

			if (!EnsureFrame())
			{
				return false;
			}

			// The texture first: realizing it records uploads, which run before the frame's drawing.
			GLTexture texture = units[0];
			if (texture == null || (!texture.Pending && texture.Slot == 0))
			{
				texture = blackTexture;
			}

			if (texture.Pending)
			{
				Realize(texture);
			}

			draw.Texture = (uint)texture.Slot;
			draw.Sampler = (uint)SamplerSlot(SamplerKey(texture));
			draw.Program = program.Kind == ProgramKind.Default ? 0u : program.Kind == ProgramKind.Rectangle ? 1u : 2u;
			draw.Primitive = (uint)mode; // GL's modes 0 to 6 are deko3d's DkPrimitive values.

			// Uniforms: the program's block, and for the default program the animation matrices, in the ring.
			if (usesVertices)
			{
				GLBuffer matrices = uniformBindings[Math.Max(0, Math.Min(uniformBindings.Length - 1, program.BlockBinding))];
				if (matrices != null && matrices.Uniform && matrices.Shadow != null && matrices.Shadow.Length > 0)
				{
					if (matrices.RingSerial != serial || matrices.RingVersion != matrices.Version)
					{
						matrices.RingAddress = RingWrite(matrices.Shadow, (int)Math.Min((uint)matrices.Shadow.Length, AnimationBlockSize), AnimationBlockSize);
						matrices.RingSerial = serial;
						matrices.RingVersion = matrices.Version;
					}

					draw.MatricesAddress = matrices.RingAddress != 0 ? matrices.RingAddress : zeroBlock.Gpu;
				}
				else
				{
					draw.MatricesAddress = zeroBlock.Gpu;
				}

				draw.MatricesSize = AnimationBlockSize;
				if (program.NormalDirty)
				{
					UpdateNormalMatrix(program);
				}
			}

			if (program.Dirty || program.RingSerial != serial)
			{
				program.RingAddress = RingWrite(program.Data, program.Data.Length, 512);
				program.RingSerial = serial;
				program.Dirty = false;
			}

			if (program.RingAddress == 0)
			{
				return false;
			}

			draw.BlockAddress = program.RingAddress;
			draw.BlockSize = (uint)program.Data.Length;

			// State.
			draw.DepthTest = depthTest ? 1u : 0u;
			draw.DepthWrite = depthTest && depthMask ? 1u : 0u;
			draw.DepthCompare = depthTest ? DekoNative.Compare(depthFunc) : 8u;
			draw.Blend = blend ? 1u : 0u;
			draw.BlendSource = DekoNative.BlendFactor(blend ? blendSource : GL_ONE);
			draw.BlendDestination = DekoNative.BlendFactor(blend ? blendDestination : GL_ZERO);
			draw.Cull = !cullFace ? DekoNative.FaceNone : cullMode == GL_FRONT ? DekoNative.FaceFront : cullMode == GL_BACK ? DekoNative.FaceBack : DekoNative.FaceFrontAndBack;
			// GL's viewport from the bottom left; deko3d's from the top left.
			draw.Viewport[0] = viewportX;
			draw.Viewport[1] = framebufferHeight - (viewportY + Math.Max(1, viewportHeight));
			draw.Viewport[2] = Math.Max(1, viewportWidth);
			draw.Viewport[3] = Math.Max(1, viewportHeight);

			// Vertices: each attribute its own buffer binding; those not read come from the constant buffer.
			if (usesVertices)
			{
				draw.AttributeCount = DefaultLocations;
				for (int i = 0; i < DefaultLocations; i++)
				{
					Attribute attribute = array.Attributes[i];
					uint format = attribute.Enabled && attribute.Buffer != null ? AttributeFormat(attribute.Type, attribute.Size, attribute.Normalized, attribute.Integer) : 0;
					if (format != 0)
					{
						GLBuffer source = attribute.Buffer;
						source.LastUse = serial;
						draw.VertexAddress[i] = source.Memory.Gpu + (ulong)attribute.Offset;
						draw.VertexSize[i] = (uint)Math.Max(0, source.Size - (int)attribute.Offset);
						draw.VertexStride[i] = (uint)attribute.Stride;
						draw.Attribute[i] = format;
					}
					else
					{
						// GL's value for an attribute without an array: (0, 0, 0, 1) as floats, zeros as integers.
						draw.VertexAddress[i] = defaultAttributes.Gpu + (i == 4 ? 16UL : 0UL);
						draw.VertexSize[i] = 16;
						draw.VertexStride[i] = 0;
						draw.Attribute[i] = i == 4 ? DekoNative.Size32[2] | DekoNative.AttribSint << 8 : DekoNative.Size32[3] | DekoNative.AttribFloat << 8;
					}
				}
			}

			return true;
		}

		public void Dispose()
		{
			if (inFrame)
			{
				EndFrame();
			}

			DekoNative.FlushUploads();
			RunAfterGpu();
			bufferHeap.Dispose();
			imageHeap.Dispose();
			foreach (IntPtr text in strings.Values)
			{
				Marshal.FreeHGlobal(text);
			}

			strings.Clear();
		}
	}
}
