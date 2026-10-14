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
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OpenBve.Android.NativePlugins
{
	/// <summary>The kind of binary a train plugin file turns out to be.</summary>
	public enum NativePluginKind
	{
		/// <summary>Not a Windows executable at all.</summary>
		NotAWindowsBinary,

		/// <summary>A .NET assembly: portable, and loadable directly.</summary>
		Managed,

		/// <summary>A 32-bit x86 Windows DLL: the usual BVE ATS plugin.</summary>
		NativeX86,

		/// <summary>A 64-bit Windows DLL.</summary>
		NativeX64,

		/// <summary>A Windows DLL for some other processor.</summary>
		NativeOther
	}

	/// <summary>
	/// What a train plugin file is, read from its headers alone.
	/// </summary>
	/// <remarks>
	/// Nothing here executes the file: the PE header, the export table and the version resource
	/// are parsed as data. That is the only safe way to look at a plugin from an untrusted train
	/// package, and it is enough to say what the plugin is, which ATS interface it exports, and
	/// whether this port has a translation for it.
	/// </remarks>
	public sealed class NativePluginImage
	{
		/// <summary>The functions a legacy (Win32) BVE ATS plugin exports.</summary>
		private static readonly string[] AtsExports =
		{
			"Elapse", "SetPower", "SetBrake", "SetReverser", "KeyDown", "KeyUp",
			"SetSignal", "SetBeaconData", "Initialize", "SetVehicleSpec"
		};

		private NativePluginImage(string path)
		{
			Path = path;
			FileName = System.IO.Path.GetFileName(path);
		}

		/// <summary>The file's full path.</summary>
		public string Path { get; }

		/// <summary>The file's name, with extension.</summary>
		public string FileName { get; }

		/// <summary>What kind of binary it is.</summary>
		public NativePluginKind Kind { get; private set; }

		/// <summary>The functions it exports, in the order the export table lists them.</summary>
		public IReadOnlyList<string> Exports { get; private set; } = Array.Empty<string>();

		/// <summary>The SHA-256 of the file, which is how a known plugin is recognised.</summary>
		public string Hash { get; private set; } = string.Empty;

		/// <summary>The file's size in bytes.</summary>
		public long Length { get; private set; }

		/// <summary>Whether it exports the legacy ATS plugin interface.</summary>
		public bool IsAtsPlugin
		{
			get
			{
				foreach (string name in AtsExports)
				{
					if (!Exports.Contains(name))
					{
						return false;
					}
				}

				return true;
			}
		}

		/// <summary>Whether it exports the ATS2 "Load"/"Dispose" pair, which a loader plugin uses.</summary>
		public bool HasLoadExport => Exports.Contains("Load") && Exports.Contains("Dispose");

		/// <summary>A line for the log and for the plugin report shown to the player.</summary>
		public override string ToString()
		{
			return FileName + " (" + Kind + ", " + Length + " bytes, sha256 " + (Hash.Length >= 16 ? Hash.Substring(0, 16) : Hash) +
			       ", " + (IsAtsPlugin ? "ATS plugin interface" : Exports.Count + " export(s)") + ")";
		}

		/// <summary>Reads a plugin file's headers. Never throws; an unreadable file comes back as not a Windows binary.</summary>
		public static NativePluginImage Read(string path)
		{
			NativePluginImage image = new NativePluginImage(path);
			try
			{
				byte[] data = File.ReadAllBytes(path);
				image.Length = data.Length;
				using (SHA256 sha = SHA256.Create())
				{
					image.Hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
				}

				image.Parse(data);
			}
			catch (Exception)
			{
				image.Kind = NativePluginKind.NotAWindowsBinary;
			}

			return image;
		}

		private void Parse(byte[] data)
		{
			if (data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z')
			{
				Kind = NativePluginKind.NotAWindowsBinary;
				return;
			}

			int headers = BitConverter.ToInt32(data, 0x3c);
			if (headers < 0 || headers + 0x78 > data.Length || BitConverter.ToUInt32(data, headers) != 0x00004550)
			{
				Kind = NativePluginKind.NotAWindowsBinary;
				return;
			}

			ushort machine = BitConverter.ToUInt16(data, headers + 4);
			int sectionCount = BitConverter.ToUInt16(data, headers + 6);
			int optional = headers + 24;
			ushort magic = BitConverter.ToUInt16(data, optional);
			bool pe32Plus = magic == 0x20b;
			int directories = optional + (pe32Plus ? 108 : 92);
			int directoryCount = BitConverter.ToInt32(data, directories);
			int directory = directories + 4;

			Kind = machine == 0x14c ? NativePluginKind.NativeX86
				: machine == 0x8664 ? NativePluginKind.NativeX64
				: NativePluginKind.NativeOther;

			// Section headers, so that a virtual address can be turned into a file offset.
			int sections = optional + (pe32Plus ? 240 : 224);
			List<(uint Virtual, uint Size, uint Raw)> map = new List<(uint, uint, uint)>();
			for (int i = 0; i < sectionCount; i++)
			{
				int entry = sections + 40 * i;
				if (entry + 40 > data.Length)
				{
					break;
				}

				uint virtualSize = BitConverter.ToUInt32(data, entry + 8);
				uint virtualAddress = BitConverter.ToUInt32(data, entry + 12);
				uint rawSize = BitConverter.ToUInt32(data, entry + 16);
				uint rawAddress = BitConverter.ToUInt32(data, entry + 20);
				map.Add((virtualAddress, Math.Max(virtualSize, rawSize), rawAddress));
			}

			int Offset(uint rva)
			{
				foreach ((uint start, uint size, uint raw) in map)
				{
					if (rva >= start && rva < start + size)
					{
						return (int)(raw + (rva - start));
					}
				}

				return -1;
			}

			// The CLI header directory (index 14) is what makes an assembly managed.
			if (directoryCount > 14 && BitConverter.ToUInt32(data, directory + 14 * 8) != 0)
			{
				Kind = NativePluginKind.Managed;
			}

			if (directoryCount < 1)
			{
				return;
			}

			uint exportRva = BitConverter.ToUInt32(data, directory);
			int exports = exportRva == 0 ? -1 : Offset(exportRva);
			if (exports < 0 || exports + 40 > data.Length)
			{
				return;
			}

			int nameCount = BitConverter.ToInt32(data, exports + 24);
			int nameTable = Offset(BitConverter.ToUInt32(data, exports + 32));
			if (nameTable < 0 || nameCount <= 0 || nameCount > 4096)
			{
				return;
			}

			List<string> names = new List<string>(nameCount);
			for (int i = 0; i < nameCount; i++)
			{
				int entry = nameTable + 4 * i;
				if (entry + 4 > data.Length)
				{
					break;
				}

				int start = Offset(BitConverter.ToUInt32(data, entry));
				if (start < 0)
				{
					continue;
				}

				int end = start;
				while (end < data.Length && data[end] != 0)
				{
					end++;
				}

				names.Add(Encoding.ASCII.GetString(data, start, end - start));
			}

			Exports = names;
		}
	}

	internal static class ListExtensions
	{
		/// <summary>Whether a read-only list holds a string, since IReadOnlyList has no Contains.</summary>
		internal static bool Contains(this IReadOnlyList<string> list, string value)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(list[i], value, StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}
	}
}
