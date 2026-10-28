// OBNS: not an upstream file. Redundant GL state elimination for the desktop build (from the Android port's
// OpenBve.GLES GL.cs), put between upstream's renderer and the driver by replacing OpenTK's GL entry points.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics
{
	/*
	 * Upstream's renderer sets most of its state for every face it draws - cull face on or off, material flags,
	 * shininess, opacity - whether or not it changed, and a busy scene has around ten thousand faces. GL state and
	 * uniform values persist until changed, so skipping a set to the value already in place is exact, provided
	 * every change goes through here.
	 *
	 * OpenTK calls each GL function through a table of the driver's entry points (GL.EntryPoints), read on every
	 * call; the functions filtered here have their entries replaced by this class's, which pass a change on to
	 * the driver's function and skip a set that changes nothing. Every caller in the process - LibRender2, the
	 * game, plugins drawing through OpenTK - goes through the table, so nothing changes state behind the caches.
	 * The game has one GL context, current on the main thread, which is where the filter runs.
	 *
	 * Measured on the Tuen Ma Line it does not pay on the desktop: each filtered call goes from OpenTK into a
	 * managed delegate and out again to the driver, two transitions where there was one, and with OpenGL drawing
	 * the world frames were 1-4 ms slower with it (no difference with Vulkan drawing it). So it is off unless
	 * OBNS_GL_FILTER=1, for comparisons.
	 */
	internal static class GLStateFilter
	{
		// The driver's functions, called when a set changes something. SuppressUnmanagedCodeSecurity skips the
		// security stack walk on each call, as OpenTK's own calls do.
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void Enum1(uint a);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void Enum2(uint a, uint b);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void Bool1(byte a);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void ProgramInt(uint program, int location, int v0);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void ProgramFloat1(uint program, int location, float v0);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void ProgramFloat2(uint program, int location, float v0, float v1);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void ProgramFloat3(uint program, int location, float v0, float v1, float v2);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void ProgramFloat4(uint program, int location, float v0, float v1, float v2, float v3);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private unsafe delegate void ProgramMatrix(uint program, int location, int count, byte transpose, float* value);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void UniformInt(int location, int v0);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private delegate void UniformFloat(int location, float v0);
		[UnmanagedFunctionPointer(CallingConvention.Winapi), SuppressUnmanagedCodeSecurity] private unsafe delegate void UniformMatrix(int location, int count, byte transpose, float* value);

		private static Enum1 enable, disable, depthFunc, cullFace, useProgram, linkProgram, deleteProgram;
		private static Enum2 blendFunc;
		private static Bool1 depthMask;
		private static ProgramInt programUniform1i;
		private static ProgramFloat1 programUniform1f;
		private static ProgramFloat2 programUniform2f;
		private static ProgramFloat3 programUniform3f;
		private static ProgramFloat4 programUniform4f;
		private static ProgramMatrix programUniformMatrix4fv;
		private static UniformInt uniform1i;
		private static UniformFloat uniform1f;
		private static UniformMatrix uniformMatrix4fv;

		/// <summary>The replacements handed to OpenTK, kept alive here (the table holds only their addresses).</summary>
		private static readonly List<Delegate> replacements = new List<Delegate>();

		/// <summary>Whether the filter is in place.</summary>
		internal static bool Installed { get; private set; }

		/// <summary>Sets passed on to the driver, and sets skipped, since the counts were last taken.</summary>
		private static long passed, skipped;

		/// <summary>Replaces OpenTK's entry points for the filtered functions; once the context is current and OpenTK has loaded them.</summary>
		internal static void Install()
		{
			if (Installed || Environment.GetEnvironmentVariable("OBNS_GL_FILTER") != "1")
			{
				return;
			}

			IntPtr[] entryPoints;
			Dictionary<string, int> slots;
			try
			{
				entryPoints = (IntPtr[])typeof(GL).GetField("EntryPoints", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(null);
				slots = EntryPointSlots(entryPoints.Length);
			}
			catch (Exception ex)
			{
				Program.FileSystem.AppendToLogFile("OBNS GL state filter not installed: OpenTK's entry point table was not found (" + ex.Message + ")");
				return;
			}

			string[] needed =
			{
				"glEnable", "glDisable", "glBlendFunc", "glDepthMask", "glDepthFunc", "glCullFace", "glUseProgram", "glLinkProgram", "glDeleteProgram",
				"glProgramUniform1i", "glProgramUniform1f", "glProgramUniform2f", "glProgramUniform3f", "glProgramUniform4f", "glProgramUniformMatrix4fv",
				"glUniform1i", "glUniform1f", "glUniformMatrix4fv"
			};
			foreach (string name in needed)
			{
				if (!slots.TryGetValue(name, out int slot) || entryPoints[slot] == IntPtr.Zero)
				{
					Program.FileSystem.AppendToLogFile("OBNS GL state filter not installed: the driver has no " + name);
					return;
				}
			}

			T Driver<T>(string name) where T : class => Marshal.GetDelegateForFunctionPointer(entryPoints[slots[name]], typeof(T)) as T;
			enable = Driver<Enum1>("glEnable");
			disable = Driver<Enum1>("glDisable");
			blendFunc = Driver<Enum2>("glBlendFunc");
			depthMask = Driver<Bool1>("glDepthMask");
			depthFunc = Driver<Enum1>("glDepthFunc");
			cullFace = Driver<Enum1>("glCullFace");
			useProgram = Driver<Enum1>("glUseProgram");
			linkProgram = Driver<Enum1>("glLinkProgram");
			deleteProgram = Driver<Enum1>("glDeleteProgram");
			programUniform1i = Driver<ProgramInt>("glProgramUniform1i");
			programUniform1f = Driver<ProgramFloat1>("glProgramUniform1f");
			programUniform2f = Driver<ProgramFloat2>("glProgramUniform2f");
			programUniform3f = Driver<ProgramFloat3>("glProgramUniform3f");
			programUniform4f = Driver<ProgramFloat4>("glProgramUniform4f");
			programUniformMatrix4fv = Driver<ProgramMatrix>("glProgramUniformMatrix4fv");
			uniform1i = Driver<UniformInt>("glUniform1i");
			uniform1f = Driver<UniformFloat>("glUniform1f");
			uniformMatrix4fv = Driver<UniformMatrix>("glUniformMatrix4fv");

			void Replace(string name, Delegate replacement)
			{
				replacements.Add(replacement);
				entryPoints[slots[name]] = Marshal.GetFunctionPointerForDelegate(replacement);
			}

			Replace("glEnable", new Enum1(Enable));
			Replace("glDisable", new Enum1(Disable));
			Replace("glBlendFunc", new Enum2(BlendFunc));
			Replace("glDepthMask", new Bool1(DepthMask));
			Replace("glDepthFunc", new Enum1(DepthFunc));
			Replace("glCullFace", new Enum1(CullFace));
			Replace("glUseProgram", new Enum1(UseProgram));
			Replace("glLinkProgram", new Enum1(LinkProgram));
			Replace("glDeleteProgram", new Enum1(DeleteProgram));
			Replace("glProgramUniform1i", new ProgramInt(ProgramUniform1i));
			Replace("glProgramUniform1f", new ProgramFloat1(ProgramUniform1f));
			Replace("glProgramUniform2f", new ProgramFloat2(ProgramUniform2f));
			Replace("glProgramUniform3f", new ProgramFloat3(ProgramUniform3f));
			Replace("glProgramUniform4f", new ProgramFloat4(ProgramUniform4f));
			unsafe
			{
				Replace("glProgramUniformMatrix4fv", new ProgramMatrix(ProgramUniformMatrix4fv));
				Replace("glUniformMatrix4fv", new UniformMatrix(UniformMatrix4fv));
			}
			Replace("glUniform1i", new UniformInt(Uniform1i));
			Replace("glUniform1f", new UniformFloat(Uniform1f));
			Installed = true;
			Program.FileSystem.AppendToLogFile("OBNS GL state filter installed: " + needed.Length + " GL functions skip sets that change nothing");
		}

		/// <summary>OpenTK's GL function names by their slot in the entry point table.</summary>
		private static Dictionary<string, int> EntryPointSlots(int count)
		{
			const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
			byte[] names = (byte[])typeof(GL).GetField("EntryPointNames", flags).GetValue(null);
			int[] offsets = (int[])typeof(GL).GetField("EntryPointNameOffsets", flags).GetValue(null);
			Dictionary<string, int> slots = new Dictionary<string, int>(count);
			for (int i = 0; i < Math.Min(count, offsets.Length); i++)
			{
				int end = Array.IndexOf(names, (byte)0, offsets[i]);
				slots[System.Text.Encoding.ASCII.GetString(names, offsets[i], (end < 0 ? names.Length : end) - offsets[i])] = i;
			}

			return slots;
		}

		/// <summary>The sets passed on and skipped since the last call, for the frame statistics.</summary>
		internal static void TakeCounts(out long passedSets, out long skippedSets)
		{
			passedSets = passed;
			skippedSets = skipped;
			passed = 0;
			skipped = 0;
		}

		private static bool Changes(bool changes)
		{
			if (changes)
			{
				passed++;
			}
			else
			{
				skipped++;
			}

			return changes;
		}

		// --- fixed-function state ---

		private static readonly Dictionary<uint, bool> capabilities = new Dictionary<uint, bool>();
		private static uint blendSource = uint.MaxValue, blendDestination = uint.MaxValue, depthFunction = uint.MaxValue, cullMode = uint.MaxValue;
		private static int depthWrites = -1;

		private static bool CapabilityChanges(uint cap, bool enabled)
		{
			if (capabilities.TryGetValue(cap, out bool current) && current == enabled)
			{
				return false;
			}

			capabilities[cap] = enabled;
			return true;
		}

		private static void Enable(uint cap)
		{
			if (Changes(CapabilityChanges(cap, true)))
			{
				enable(cap);
			}
		}

		private static void Disable(uint cap)
		{
			if (Changes(CapabilityChanges(cap, false)))
			{
				disable(cap);
			}
		}

		private static void BlendFunc(uint source, uint destination)
		{
			if (Changes(source != blendSource || destination != blendDestination))
			{
				blendSource = source;
				blendDestination = destination;
				blendFunc(source, destination);
			}
		}

		private static void DepthMask(byte flag)
		{
			int value = flag != 0 ? 1 : 0;
			if (Changes(value != depthWrites))
			{
				depthWrites = value;
				depthMask(flag);
			}
		}

		private static void DepthFunc(uint function)
		{
			if (Changes(function != depthFunction))
			{
				depthFunction = function;
				depthFunc(function);
			}
		}

		private static void CullFace(uint mode)
		{
			if (Changes(mode != cullMode))
			{
				cullMode = mode;
				cullFace(mode);
			}
		}

		// --- programs and uniforms ---

		/// <summary>A uniform's last value: which setter set it, and up to four components as raw bits.</summary>
		private struct UniformValue
		{
			internal int Kind, A, B, C, D;
		}

		/// <summary>Per program: scalar and vector uniforms by location, and matrices by location.</summary>
		private sealed class ProgramUniforms
		{
			internal readonly Dictionary<int, UniformValue> Values = new Dictionary<int, UniformValue>();
			internal readonly Dictionary<int, float[]> Matrices = new Dictionary<int, float[]>();
		}

		private static readonly Dictionary<uint, ProgramUniforms> uniformCache = new Dictionary<uint, ProgramUniforms>();

		/// <summary>The program glUseProgram made current, which glUniform* sets.</summary>
		private static uint currentProgram;

		private const int KindInt = 1, KindFloat1 = 2, KindFloat2 = 3, KindFloat3 = 4, KindFloat4 = 5;

		private static unsafe int Bits(float value) => *(int*)&value;

		private static void UseProgram(uint program)
		{
			// Not filtered: the caches are per program, so the current one only needs to be known.
			currentProgram = program;
			passed++;
			useProgram(program);
		}

		private static void LinkProgram(uint program)
		{
			// Linking resets the program's uniforms.
			uniformCache.Remove(program);
			passed++;
			linkProgram(program);
		}

		private static void DeleteProgram(uint program)
		{
			uniformCache.Remove(program);
			passed++;
			deleteProgram(program);
		}

		/// <summary>Whether setting a uniform would change it, recording the new value.</summary>
		/// <remarks>
		/// Values are compared bit for bit, so -0 and +0, or two NaNs, count as different and are sent. Location -1
		/// (a uniform the compiler removed) is sent too: GL ignores it, and it is not worth a special case.
		/// </remarks>
		private static bool UniformChanges(uint program, int location, int kind, int a, int b = 0, int c = 0, int d = 0)
		{
			if (location < 0 || program == 0)
			{
				return Changes(true);
			}

			if (!uniformCache.TryGetValue(program, out ProgramUniforms uniforms))
			{
				uniforms = new ProgramUniforms();
				uniformCache[program] = uniforms;
			}

			if (uniforms.Values.TryGetValue(location, out UniformValue last) && last.Kind == kind && last.A == a && last.B == b && last.C == c && last.D == d)
			{
				return Changes(false);
			}

			uniforms.Values[location] = new UniformValue { Kind = kind, A = a, B = b, C = c, D = d };
			return Changes(true);
		}

		/// <summary>Whether setting one matrix uniform would change it, recording the new value; a set of several is always sent.</summary>
		private static unsafe bool MatrixChanges(uint program, int location, int count, byte transpose, float* values)
		{
			if (location < 0 || program == 0 || count != 1 || values == null)
			{
				if (uniformCache.TryGetValue(program, out ProgramUniforms stale))
				{
					for (int i = 0; i < count; i++)
					{
						stale.Matrices.Remove(location + i);
					}
				}

				return Changes(true);
			}

			if (!uniformCache.TryGetValue(program, out ProgramUniforms uniforms))
			{
				uniforms = new ProgramUniforms();
				uniformCache[program] = uniforms;
			}

			float transposed = transpose != 0 ? 1.0f : 0.0f;
			if (uniforms.Matrices.TryGetValue(location, out float[] last))
			{
				bool same = last[16] == transposed;
				for (int i = 0; same && i < 16; i++)
				{
					same = Bits(last[i]) == Bits(values[i]);
				}

				if (same)
				{
					return Changes(false);
				}
			}
			else
			{
				last = new float[17];
				uniforms.Matrices[location] = last;
			}

			for (int i = 0; i < 16; i++)
			{
				last[i] = values[i];
			}

			last[16] = transposed;
			return Changes(true);
		}

		private static void ProgramUniform1i(uint program, int location, int v0)
		{
			if (UniformChanges(program, location, KindInt, v0))
			{
				programUniform1i(program, location, v0);
			}
		}

		private static void ProgramUniform1f(uint program, int location, float v0)
		{
			if (UniformChanges(program, location, KindFloat1, Bits(v0)))
			{
				programUniform1f(program, location, v0);
			}
		}

		private static void ProgramUniform2f(uint program, int location, float v0, float v1)
		{
			if (UniformChanges(program, location, KindFloat2, Bits(v0), Bits(v1)))
			{
				programUniform2f(program, location, v0, v1);
			}
		}

		private static void ProgramUniform3f(uint program, int location, float v0, float v1, float v2)
		{
			if (UniformChanges(program, location, KindFloat3, Bits(v0), Bits(v1), Bits(v2)))
			{
				programUniform3f(program, location, v0, v1, v2);
			}
		}

		private static void ProgramUniform4f(uint program, int location, float v0, float v1, float v2, float v3)
		{
			if (UniformChanges(program, location, KindFloat4, Bits(v0), Bits(v1), Bits(v2), Bits(v3)))
			{
				programUniform4f(program, location, v0, v1, v2, v3);
			}
		}

		private static unsafe void ProgramUniformMatrix4fv(uint program, int location, int count, byte transpose, float* value)
		{
			if (MatrixChanges(program, location, count, transpose, value))
			{
				programUniformMatrix4fv(program, location, count, transpose, value);
			}
		}

		private static void Uniform1i(int location, int v0)
		{
			if (UniformChanges(currentProgram, location, KindInt, v0))
			{
				uniform1i(location, v0);
			}
		}

		private static void Uniform1f(int location, float v0)
		{
			if (UniformChanges(currentProgram, location, KindFloat1, Bits(v0)))
			{
				uniform1f(location, v0);
			}
		}

		private static unsafe void UniformMatrix4fv(int location, int count, byte transpose, float* value)
		{
			if (MatrixChanges(currentProgram, location, count, transpose, value))
			{
				uniformMatrix4fv(location, count, transpose, value);
			}
		}
	}
}
