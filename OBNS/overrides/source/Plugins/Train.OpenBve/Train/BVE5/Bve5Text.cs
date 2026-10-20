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

// OBNS: BVE Trainsim 5 trains (OBNS/BVE5.md), part of upstream's train plugin in the OBNS build.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Train.OpenBve
{
	/// <summary>
	/// BVE Trainsim 5's text files: the header line ("BveTs Vehicle Parameters 2.01:shift_jis"), the
	/// text in its encoding, comments, INI sections and comma-separated tables.
	/// </summary>
	/// <remarks>
	/// The header names the file's kind, its version and, after a colon, its encoding (UTF-8 if none
	/// is named). Tables saved from spreadsheets add commas after it. Some files have no header at all
	/// (the vehicle performance file, the motor noise file); older trains write "Bvets" and versions
	/// from 0.01 on. Files without a named encoding are read as UTF-8, or as Shift_JIS where they are
	/// not valid UTF-8, as many Japanese trains are.
	/// </remarks>
	internal sealed class Bve5Text
	{
		/// <summary>The file read.</summary>
		internal readonly string FileName;

		/// <summary>The kind of file the header names ("Vehicle", "Vehicle Parameters" ...), or null with no header.</summary>
		internal readonly string Kind;

		/// <summary>The version the header names, or 0.</summary>
		internal readonly double Version;

		/// <summary>The lines after the header, without comments, trimmed; empty lines kept for line numbers.</summary>
		internal readonly string[] Lines;

		private static bool codePagesRegistered;

		private Bve5Text(string fileName, string kind, double version, string[] lines)
		{
			FileName = fileName;
			Kind = kind;
			Version = version;
			Lines = lines;
		}

		/// <summary>Reads a BVE5 text file.</summary>
		internal static Bve5Text Read(string fileName)
		{
			byte[] bytes = File.ReadAllBytes(fileName);
			int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

			// The header is ASCII, whatever the file's encoding.
			int end = start;
			while (end < bytes.Length && bytes[end] != '\n' && bytes[end] != '\r')
			{
				end++;
			}

			string header = Encoding.ASCII.GetString(bytes, start, end - start).Trim();
			ParseHeader(header, out string kind, out double version, out string encodingName);
			Encoding encoding = ChooseEncoding(bytes, start, encodingName);
			string text = encoding.GetString(bytes, start, bytes.Length - start);
			string[] all = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
			int first = kind != null ? 1 : 0;
			string[] lines = new string[Math.Max(0, all.Length - first)];
			for (int i = first; i < all.Length; i++)
			{
				lines[i - first] = StripComment(all[i]).Trim();
			}

			return new Bve5Text(fileName, kind, version, lines);
		}

		/// <summary>The kind and version a file's header names, without reading the whole file; null if it has none.</summary>
		internal static string ReadKind(string fileName, out double version)
		{
			version = 0;
			try
			{
				using (FileStream stream = File.OpenRead(fileName))
				{
					byte[] buffer = new byte[128];
					int read = stream.Read(buffer, 0, buffer.Length);
					int start = read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
					int end = start;
					while (end < read && buffer[end] != '\n' && buffer[end] != '\r')
					{
						end++;
					}

					ParseHeader(Encoding.ASCII.GetString(buffer, start, end - start).Trim(), out string kind, out version, out _);
					return kind;
				}
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static void ParseHeader(string header, out string kind, out double version, out string encodingName)
		{
			kind = null;
			version = 0;
			encodingName = null;
			if (!header.StartsWith("bvets ", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			string format = header;
			int colon = header.IndexOf(':');
			if (colon >= 0)
			{
				format = header.Substring(0, colon);
				encodingName = header.Substring(colon + 1).Split(',', ' ', '\t')[0].Trim();
			}

			// Spreadsheets add commas after a table's header.
			format = format.Split(',')[0].Trim();
			string[] words = format.Substring(6).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (words.Length == 0)
			{
				return;
			}

			int nameWords = words.Length;
			if (double.TryParse(words[words.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out version))
			{
				nameWords--;
			}

			kind = string.Join(" ", words, 0, nameWords);
		}

		private static Encoding ChooseEncoding(byte[] bytes, int start, string encodingName)
		{
			RegisterCodePages();
			if (!string.IsNullOrEmpty(encodingName))
			{
				try
				{
					return Encoding.GetEncoding(encodingName);
				}
				catch (Exception)
				{
					// an unknown name: decided by the bytes below
				}
			}

			try
			{
				new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
				return Encoding.UTF8;
			}
			catch (DecoderFallbackException)
			{
				try
				{
					return Encoding.GetEncoding(932);
				}
				catch (Exception)
				{
					return Encoding.UTF8;
				}
			}
		}

		/// <summary>Makes Shift_JIS and the other code pages available where the runtime leaves them out (.NET on Android).</summary>
		private static void RegisterCodePages()
		{
			if (codePagesRegistered)
			{
				return;
			}

			codePagesRegistered = true;
			try
			{
				Type provider = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
				object instance = provider?.GetProperty("Instance")?.GetValue(null);
				if (instance is EncodingProvider encodingProvider)
				{
					Encoding.RegisterProvider(encodingProvider);
				}
			}
			catch (Exception)
			{
				// .NET Framework has them built in
			}
		}

		/// <summary>Removes a comment: from '#' or ';' to the end of the line.</summary>
		private static string StripComment(string line)
		{
			int cut = line.Length;
			int hash = line.IndexOf('#');
			if (hash >= 0)
			{
				cut = hash;
			}

			int semicolon = line.IndexOf(';');
			if (semicolon >= 0 && semicolon < cut)
			{
				cut = semicolon;
			}

			return line.Substring(0, cut);
		}

		/// <summary>The file as INI sections ("" for keys before any section), names and keys ignoring case. A repeated key keeps its last value.</summary>
		internal Dictionary<string, Dictionary<string, string>> Sections()
		{
			Dictionary<string, Dictionary<string, string>> sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			sections[string.Empty] = current;
			foreach (string line in Lines)
			{
				if (line.Length == 0)
				{
					continue;
				}

				if (line[0] == '[')
				{
					int close = line.IndexOf(']');
					string name = (close > 0 ? line.Substring(1, close - 1) : line.Substring(1)).Trim();
					if (!sections.TryGetValue(name, out current))
					{
						current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
						sections[name] = current;
					}

					continue;
				}

				int equals = line.IndexOf('=');
				if (equals > 0)
				{
					current[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
				}
			}

			return sections;
		}

		/// <summary>The file's lines as comma-separated cells, without empty lines.</summary>
		internal List<string[]> Rows()
		{
			List<string[]> rows = new List<string[]>();
			foreach (string line in Lines)
			{
				if (line.TrimEnd(',').Length == 0)
				{
					continue;
				}

				string[] cells = line.Split(',');
				for (int i = 0; i < cells.Length; i++)
				{
					cells[i] = cells[i].Trim();
				}

				rows.Add(cells);
			}

			return rows;
		}

		/// <summary>A number in a BVE5 file (invariant culture), or the fallback where there is none.</summary>
		internal static double Number(string text, double fallback)
		{
			return !string.IsNullOrWhiteSpace(text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
		}

		/// <summary>A path a BVE5 file gives, relative to that file's folder; null if empty.</summary>
		internal static string ResolvePath(string fromFile, string relative)
		{
			if (string.IsNullOrWhiteSpace(relative))
			{
				return null;
			}

			string folder = System.IO.Path.GetDirectoryName(fromFile) ?? string.Empty;
			try
			{
				return OpenBveApi.Path.CombineFile(folder, relative.Trim().Trim('"'));
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
