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
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace OpenBve.X86
{
	/// <summary>
	/// The few runtime functions the layer uses that .NET Framework 4.8 lacks: the Android build (net10.0) calls the
	/// runtime's own, the desktop build (which compiles these files into its TrainManager) the equivalents here.
	/// </summary>
	internal static class Compat
	{
#if NETCOREAPP
		public static Encoding Latin1 => Encoding.Latin1;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int SingleToInt32Bits(float value) => BitConverter.SingleToInt32Bits(value);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float Int32BitsToSingle(int value) => BitConverter.Int32BitsToSingle(value);

		public static bool IsNegative(double value) => double.IsNegative(value);

		public static bool IsSubnormal(double value) => double.IsSubnormal(value);

		public static double Log2(double value) => Math.Log2(value);

		public static float Sqrt(float value) => MathF.Sqrt(value);

		public static float Round(float value, MidpointRounding mode) => MathF.Round(value, mode);

		public static float BitIncrement(float value) => MathF.BitIncrement(value);

		public static float BitDecrement(float value) => MathF.BitDecrement(value);

		public static double BitIncrement(double value) => Math.BitIncrement(value);

		public static double BitDecrement(double value) => Math.BitDecrement(value);

		public static int LeadingZeroCount(uint value) => System.Numerics.BitOperations.LeadingZeroCount(value);

		public static int LeadingZeroCount(ulong value) => System.Numerics.BitOperations.LeadingZeroCount(value);

		public static int TrailingZeroCount(uint value) => System.Numerics.BitOperations.TrailingZeroCount(value);

		public static string GetRelativePath(string root, string path) => Path.GetRelativePath(root, path);
#else
		public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe int SingleToInt32Bits(float value) => *(int*)&value;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe float Int32BitsToSingle(int value) => *(float*)&value;

		public static bool IsNegative(double value) => BitConverter.DoubleToInt64Bits(value) < 0;

		public static bool IsSubnormal(double value)
		{
			long bits = BitConverter.DoubleToInt64Bits(value) & long.MaxValue;
			return bits != 0 && bits < 0x0010000000000000L;
		}

		/// <summary>The base 2 logarithm, exact for powers of two as the runtime's is.</summary>
		public static double Log2(double value)
		{
			if (double.IsNaN(value) || value < 0)
			{
				return double.NaN;
			}

			if (value == 0)
			{
				return double.NegativeInfinity;
			}

			if (double.IsPositiveInfinity(value))
			{
				return value;
			}

			long bits = BitConverter.DoubleToInt64Bits(value);
			int exponent = (int)(bits >> 52);
			if (exponent == 0)
			{
				// Subnormal: scaled up by 2^64 first.
				bits = BitConverter.DoubleToInt64Bits(value * 18446744073709551616.0);
				exponent = (int)(bits >> 52) - 64;
			}

			double mantissa = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
			return exponent - 1023 + Math.Log(mantissa) / 0.69314718055994530942;
		}

		public static float Sqrt(float value) => (float)Math.Sqrt(value);

		public static float Round(float value, MidpointRounding mode) => (float)Math.Round(value, mode);

		public static float BitIncrement(float value)
		{
			int bits = SingleToInt32Bits(value);
			if ((bits & 0x7F800000) >= 0x7F800000)
			{
				// NaN or infinity: +infinity and NaN stay, -infinity becomes the lowest finite value.
				return bits == unchecked((int)0xFF800000) ? float.MinValue : value;
			}

			if (bits == unchecked((int)0x80000000))
			{
				return float.Epsilon;
			}

			return Int32BitsToSingle(bits + (bits < 0 ? -1 : 1));
		}

		public static float BitDecrement(float value)
		{
			int bits = SingleToInt32Bits(value);
			if ((bits & 0x7F800000) >= 0x7F800000)
			{
				return bits == 0x7F800000 ? float.MaxValue : value;
			}

			if (bits == 0)
			{
				return -float.Epsilon;
			}

			return Int32BitsToSingle(bits + (bits < 0 ? 1 : -1));
		}

		public static double BitIncrement(double value)
		{
			long bits = BitConverter.DoubleToInt64Bits(value);
			if ((bits & 0x7FF0000000000000L) >= 0x7FF0000000000000L)
			{
				return bits == unchecked((long)0xFFF0000000000000UL) ? double.MinValue : value;
			}

			if (bits == long.MinValue)
			{
				return double.Epsilon;
			}

			return BitConverter.Int64BitsToDouble(bits + (bits < 0 ? -1 : 1));
		}

		public static double BitDecrement(double value)
		{
			long bits = BitConverter.DoubleToInt64Bits(value);
			if ((bits & 0x7FF0000000000000L) >= 0x7FF0000000000000L)
			{
				return bits == 0x7FF0000000000000L ? double.MaxValue : value;
			}

			if (bits == 0)
			{
				return -double.Epsilon;
			}

			return BitConverter.Int64BitsToDouble(bits + (bits < 0 ? 1 : -1));
		}

		public static int LeadingZeroCount(uint value)
		{
			if (value == 0)
			{
				return 32;
			}

			int count = 0;
			while ((value & 0x80000000u) == 0)
			{
				value <<= 1;
				count++;
			}

			return count;
		}

		public static int LeadingZeroCount(ulong value)
		{
			uint high = (uint)(value >> 32);
			return high != 0 ? LeadingZeroCount(high) : 32 + LeadingZeroCount((uint)value);
		}

		public static int TrailingZeroCount(uint value)
		{
			if (value == 0)
			{
				return 32;
			}

			int count = 0;
			while ((value & 1u) == 0)
			{
				value >>= 1;
				count++;
			}

			return count;
		}

		/// <summary>A path relative to a folder, or one starting "..", as the runtime's gives, for a path outside it.</summary>
		public static string GetRelativePath(string root, string path)
		{
			StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			string folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string full = Path.GetFullPath(path);
			if (string.Equals(folder, full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison))
			{
				return ".";
			}

			if (full.StartsWith(folder + Path.DirectorySeparatorChar, comparison))
			{
				return full.Substring(folder.Length + 1);
			}

			return ".." + Path.DirectorySeparatorChar + full;
		}
#endif
	}
}
