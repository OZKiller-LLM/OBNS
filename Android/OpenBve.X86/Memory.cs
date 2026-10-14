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
using System.Buffers.Binary;
using System.Text;

namespace OpenBve.X86
{
	/// <summary>Raised when emulated code does something the emulator cannot or will not do.</summary>
	public class X86Exception : Exception
	{
		public X86Exception(string message) : base(message)
		{
		}
	}

	/// <summary>
	/// The emulated process's 32-bit address space: 4 KB pages, allocated when mapped. Accessing
	/// an unmapped page is a fault (the equivalent of an access violation), reported as an
	/// exception rather than touching anything outside the emulator.
	/// </summary>
	public sealed class Memory
	{
		private const int PageBits = 12;
		private const uint PageSize = 1u << PageBits;
		private const uint PageMask = PageSize - 1;

		private readonly byte[][] pages = new byte[1 << (32 - PageBits)][];

		/// <summary>Maps (and zeroes, if new) every page overlapping the range.</summary>
		public void Map(uint address, uint size)
		{
			if (size == 0)
			{
				return;
			}

			uint first = address >> PageBits;
			uint last = (uint)(((ulong)address + size - 1) >> PageBits);
			for (uint page = first; page <= last; page++)
			{
				pages[page] ??= new byte[PageSize];
			}
		}

		/// <summary>Unmaps every page wholly inside the range.</summary>
		public void Unmap(uint address, uint size)
		{
			uint first = (address + PageMask) >> PageBits;
			uint end = (uint)(((ulong)address + size) >> PageBits);
			for (uint page = first; page < end; page++)
			{
				pages[page] = null;
			}
		}

		public bool IsMapped(uint address)
		{
			return pages[address >> PageBits] != null;
		}

		private byte[] Page(uint address)
		{
			return pages[address >> PageBits] ?? throw new X86Exception("access violation at 0x" + address.ToString("X8"));
		}

		public byte Read8(uint address)
		{
			return Page(address)[address & PageMask];
		}

		public void Write8(uint address, byte value)
		{
			Page(address)[address & PageMask] = value;
		}

		public ushort Read16(uint address)
		{
			uint offset = address & PageMask;
			if (offset <= PageSize - 2)
			{
				return BinaryPrimitives.ReadUInt16LittleEndian(Page(address).AsSpan((int)offset));
			}

			return (ushort)(Read8(address) | (Read8(address + 1) << 8));
		}

		public void Write16(uint address, ushort value)
		{
			uint offset = address & PageMask;
			if (offset <= PageSize - 2)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(Page(address).AsSpan((int)offset), value);
				return;
			}

			Write8(address, (byte)value);
			Write8(address + 1, (byte)(value >> 8));
		}

		public uint Read32(uint address)
		{
			uint offset = address & PageMask;
			if (offset <= PageSize - 4)
			{
				return BinaryPrimitives.ReadUInt32LittleEndian(Page(address).AsSpan((int)offset));
			}

			return Read16(address) | ((uint)Read16(address + 2) << 16);
		}

		public void Write32(uint address, uint value)
		{
			uint offset = address & PageMask;
			if (offset <= PageSize - 4)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(Page(address).AsSpan((int)offset), value);
				return;
			}

			Write16(address, (ushort)value);
			Write16(address + 2, (ushort)(value >> 16));
		}

		public ulong Read64(uint address)
		{
			return Read32(address) | ((ulong)Read32(address + 4) << 32);
		}

		public void Write64(uint address, ulong value)
		{
			Write32(address, (uint)value);
			Write32(address + 4, (uint)(value >> 32));
		}

		public float ReadSingle(uint address)
		{
			return BitConverter.Int32BitsToSingle((int)Read32(address));
		}

		public void WriteSingle(uint address, float value)
		{
			Write32(address, (uint)BitConverter.SingleToInt32Bits(value));
		}

		public double ReadDouble(uint address)
		{
			return BitConverter.Int64BitsToDouble((long)Read64(address));
		}

		public void WriteDouble(uint address, double value)
		{
			Write64(address, (ulong)BitConverter.DoubleToInt64Bits(value));
		}

		public void ReadBytes(uint address, Span<byte> destination)
		{
			for (int i = 0; i < destination.Length; i++)
			{
				destination[i] = Read8(address + (uint)i);
			}
		}

		public void WriteBytes(uint address, ReadOnlySpan<byte> source)
		{
			for (int i = 0; i < source.Length; i++)
			{
				Write8(address + (uint)i, source[i]);
			}
		}

		public void Fill(uint address, byte value, uint count)
		{
			for (uint i = 0; i < count; i++)
			{
				Write8(address + i, value);
			}
		}

		/// <summary>Reads a NUL-terminated byte string, decoded with the given code page.</summary>
		public string ReadCString(uint address, Encoding encoding = null, int maxLength = 65536)
		{
			if (address == 0)
			{
				return null;
			}

			byte[] buffer = new byte[256];
			int length = 0;
			while (length < maxLength)
			{
				byte b = Read8(address + (uint)length);
				if (b == 0)
				{
					break;
				}

				if (length == buffer.Length)
				{
					Array.Resize(ref buffer, buffer.Length * 2);
				}

				buffer[length++] = b;
			}

			return (encoding ?? Encoding.Latin1).GetString(buffer, 0, length);
		}

		/// <summary>Reads a NUL-terminated UTF-16 string.</summary>
		public string ReadWString(uint address, int maxLength = 32768)
		{
			if (address == 0)
			{
				return null;
			}

			StringBuilder builder = new StringBuilder();
			for (int i = 0; i < maxLength; i++)
			{
				char c = (char)Read16(address + (uint)(2 * i));
				if (c == 0)
				{
					break;
				}

				builder.Append(c);
			}

			return builder.ToString();
		}

		/// <summary>Writes a NUL-terminated byte string, returning the bytes written excluding the terminator.</summary>
		public int WriteCString(uint address, string text, Encoding encoding = null)
		{
			byte[] bytes = (encoding ?? Encoding.Latin1).GetBytes(text);
			WriteBytes(address, bytes);
			Write8(address + (uint)bytes.Length, 0);
			return bytes.Length;
		}

		/// <summary>Writes a NUL-terminated UTF-16 string, returning the characters written excluding the terminator.</summary>
		public int WriteWString(uint address, string text)
		{
			for (int i = 0; i < text.Length; i++)
			{
				Write16(address + (uint)(2 * i), text[i]);
			}

			Write16(address + (uint)(2 * text.Length), 0);
			return text.Length;
		}
	}
}
