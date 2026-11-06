// OBNS: a copy of upstream's source/Plugins/Formats.OpenBve/EnumCache.cs (OpenBVE 1.14.1.2), laid over the desktop build's
// mirror (OBNS/build-desktop.ps1) and compiled by Android (Formats.OpenBve.Android.csproj): a word that isn't one of the
// enum's names is no longer handed to Enum.TryParse, which can only fail for it - the CSV/B3D reader tries every line's
// command as a section and as a key, so one of the two misses on every line (on the Switch the parsers set the load time).
// On an upstream update: copy the file again and reapply the parts marked OBNS.
using System;
using System.Collections.Generic;

namespace Formats.OpenBve
{
	/// <summary>Provides fast case-insensitive enum name parsing via a cached dictionary lookup.</summary>
	/// <remarks>Falls back to Enum.TryParse on a dictionary miss so that numeric strings and surrounding whitespace keep their original semantics.</remarks>
	internal static class EnumCache<T> where T : struct, Enum
	{
		private static readonly Dictionary<string, T> Names = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

		static EnumCache()
		{
			foreach (string name in Enum.GetNames(typeof(T)))
			{
				Names[name] = (T)Enum.Parse(typeof(T), name);
			}
		}

		internal static bool TryParse(string value, out T result)
		{
			if (value != null && Names.TryGetValue(value, out result))
			{
				return true;
			}

			// OBNS: Enum.TryParse matches the names case-insensitively, as the dictionary does; beyond them it only takes a
			// number, a list of names (flags) or a name with spaces around it. Anything else fails there too.
			if (value != null && !MayParseOtherwise(value))
			{
				result = default;
				return false;
			}

			return Enum.TryParse(value, true, out result);
		}

		/// <summary>OBNS: whether Enum.TryParse could take the text as something other than one of the names as it stands.</summary>
		private static bool MayParseOtherwise(string value)
		{
			if (value.Length == 0)
			{
				return true;
			}

			if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
			{
				return true;
			}

			char first = value[0];
			if (char.IsDigit(first) || first == '-' || first == '+')
			{
				return true;
			}

			return value.IndexOf(',') >= 0;
		}
	}
}
