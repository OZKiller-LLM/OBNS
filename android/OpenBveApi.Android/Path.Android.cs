#pragma warning disable 0659, 0661

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace OpenBveApi {

	/// <summary>Provides path-related functions for accessing files and directories in a cross-platform manner.</summary>
	public static partial class Path {
		/*
		 * ANDROID PORT: this file is a copy of upstream's OpenBveApi/System/Path.cs, compiled in its
		 * place (OpenBveApi.Android.csproj). The only change: the case-insensitive fallbacks below list
		 * a folder through ListFiles / ListDirectories, which, while a game loads, remember each
		 * folder's listing. A file that is missing (a train's motor table naming Motor0.wav, 1,000
		 * times a car) made upstream list the folder every time - on Android each listing costs
		 * milliseconds through the storage layer: 300,000 of them, 15 minutes of the Tuen Ma Line's
		 * load. Content does not change while loading; outside a load (CacheListings false, as in
		 * the menu, which installs packages) nothing is remembered and it is upstream's code.
		 * While loading, CombineFile also answers its File.Exists / Directory.Exists checks from the
		 * listings (Listed): ~0.1-0.5 ms each through the storage layer, and the Tuen Ma Line's 36
		 * scripted trains resolve ~540,000 sound paths. The answers are the ones upstream's code
		 * reaches: the file system still decides whenever a name is listed in another letter case.
		 */

		/// <summary>Whether folder listings are remembered (set by the Android port while a game loads).</summary>
		public static bool CacheListings
		{
			get => cacheListings;
			set
			{
				cacheListings = value;
				fileListings.Clear();
				directoryListings.Clear();
				fileNames.Clear();
				directoryNames.Clear();
				writeTimes.Clear();
			}
		}

		private static volatile bool cacheListings;
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> fileListings = new System.Collections.Concurrent.ConcurrentDictionary<string, string[]>();
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> directoryListings = new System.Collections.Concurrent.ConcurrentDictionary<string, string[]>();

		private static string[] ListFiles(string folder)
		{
			return cacheListings ? fileListings.GetOrAdd(folder, Directory.GetFiles) : Directory.GetFiles(folder);
		}

		/// <summary>Per folder while a game loads: each file's (or subfolder's) name, any letter case, to its spelling in the listing.</summary>
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> fileNames =
			new System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>>();
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> directoryNames =
			new System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>>();

		private static Dictionary<string, string> NameMap(string[] entries)
		{
			Dictionary<string, string> names = new Dictionary<string, string>(entries.Length, StringComparer.OrdinalIgnoreCase);
			foreach (string entry in entries)
			{
				string name = System.IO.Path.GetFileName(entry);
				if (!string.IsNullOrEmpty(name) && !names.ContainsKey(name))
				{
					// The first in listing order, as upstream's case-insensitive search finds it.
					names.Add(name, name);
				}
			}

			return names;
		}

		/// <summary>
		/// While a game loads: whether the folder's remembered listing has <paramref name="path"/>'s
		/// name - -1 not known (not loading, no such folder), 0 not under any letter case, 1 yes, with
		/// its spelling in the listing in <paramref name="listedName"/>.
		/// </summary>
		private static int Listed(string path, bool directory, out string listedName)
		{
			listedName = null;
			if (!cacheListings || string.IsNullOrEmpty(path))
			{
				return -1;
			}

			string folder = System.IO.Path.GetDirectoryName(path);
			string name = System.IO.Path.GetFileName(path);
			if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name))
			{
				return -1;
			}

			Dictionary<string, string> names;
			try
			{
				names = directory
					? directoryNames.GetOrAdd(folder, f => NameMap(ListDirectories(f)))
					: fileNames.GetOrAdd(folder, f => NameMap(ListFiles(f)));
			}
			catch (Exception)
			{
				return -1;
			}

			return names.TryGetValue(name, out listedName) ? 1 : 0;
		}

		/// <summary>
		/// File.Exists, answered from the remembered listing while a game loads (the Android port's
		/// addition). A train with no sound.cfg makes a CarSound for every entry of its motor sound
		/// tables, ~1,900 a car, each checking its file with File.Exists - through Android's storage
		/// layer ~0.5 ms each on an SD card; the Tuen Ma Line's 36 scripted trains spent ~150 s of
		/// their load there. Present in the listing under this exact spelling: true; absent under
		/// any spelling: false; present in another letter case: the file system decides (storage may
		/// or may not be case-sensitive). Outside a load it is File.Exists.
		/// </summary>
		public static bool FileExists(string path)
		{
			switch (Listed(path, false, out string listedName))
			{
				case 0:
					return false;
				case 1:
					return string.Equals(listedName, System.IO.Path.GetFileName(path), StringComparison.Ordinal) || File.Exists(path);
				default:
					return File.Exists(path);
			}
		}

		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> writeTimes =
			new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();

		/// <summary>
		/// File.GetLastWriteTime, remembered while a game loads (the Android port's addition): the
		/// host's object cache is keyed on it, so every cache hit - ~13,700 on the Tuen Ma Line -
		/// asked the storage layer again. Outside a load it is File.GetLastWriteTime.
		/// </summary>
		public static DateTime LastWriteTime(string path)
		{
			return cacheListings ? writeTimes.GetOrAdd(path, File.GetLastWriteTime) : File.GetLastWriteTime(path);
		}

		private static string[] ListDirectories(string folder)
		{
			return cacheListings ? directoryListings.GetOrAdd(folder, Directory.GetDirectories) : Directory.GetDirectories(folder);
		}

		
		/// <summary>The list of characters that are invalid in platform-independent relative paths.</summary>
		private static readonly char[] InvalidPathChars = { ':', '*', '?', '"', '<', '>', '|' };
		
		/// <summary>The list of characters at which relative paths are separated into parts.</summary>
		private static readonly char[] PathSeparationChars = { '/', '\\' };
		
		/// <summary>Combines a platform-specific absolute path with a platform-independent relative path that points to a directory.</summary>
		/// <param name="absolute">The platform-specific absolute path.</param>
		/// <param name="relative">The platform-independent relative path.</param>
		/// <param name="allowQueryStr">If a part similar to a URL query string at the end of the path should be preserved.</param>
		/// <returns>A platform-specific absolute path to the specified directory.</returns>
		/// <exception cref="System.Exception">Raised when combining the paths failed, for example due to malformed paths or due to unauthorized access.</exception>
		public static string CombineDirectory(string absolute, string relative, bool allowQueryStr) {
			if (string.IsNullOrEmpty(absolute))
			{
				throw new ArgumentException("The absolute path was empty.");
			}
			if (string.IsNullOrEmpty(relative))
			{
				throw new ArgumentException("The relative path was empty.");
			}
            int index = relative.IndexOf("??", StringComparison.Ordinal);
			if (index >= 0) {
				string directory = CombineDirectory(absolute, relative.Substring(0, index).TrimEnd());
				if (Directory.Exists(directory)) {
					return directory;
				}
				return CombineDirectory(absolute, relative.Substring(index + 2).TrimStart());
			}
			string queryString = "";
			int questionMarkIndex = relative.IndexOf("?", StringComparison.Ordinal);
			if (allowQueryStr && questionMarkIndex >= 0) {
				queryString = relative.Substring(questionMarkIndex);
				relative = relative.Substring(0, questionMarkIndex);
			}
			if (relative.IndexOfAny(InvalidPathChars) >= 0) {
				throw new ArgumentException("The relative path contains invalid characters.");
			}
			string[] parts = relative.Split(PathSeparationChars, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < parts.Length; i++) {
				if (parts[i].Length != 0) {
					/*
					 * Consider only non-empty parts.
					 * */
					if (IsAllPeriods(parts[i])) {
						/*
						 * A string of periods is a reference to an
						 * upper directory. A single period is the
						 * current directory. For each additional
						 * period, jump one directory up.
						 * */
						for (int j = 1; j < parts[i].Length; j++) {
							absolute = Path.GetDirectoryName(absolute);
						}
					} else {
						/*
						 * This part references a directory.
						 * */
					    if (absolute != null)
					    {
					        string directory = System.IO.Path.Combine(absolute, parts[i]);
					        if (Directory.Exists(directory)) {
					            absolute = directory;
					        } else {
					            /*
							 * Try to find the directory case-insensitively.
							 * */
					            bool found = false;
					            if (Directory.Exists(absolute)) {
						            try
						            {
							            string[] directories = ListDirectories(absolute);
							            for (int j = 0; j < directories.Length; j++)
							            {
								            string name = System.IO.Path.GetFileName(directories[j]);
								            if (name != null && name.Equals(parts[i], StringComparison.OrdinalIgnoreCase))
								            {
									            absolute = directories[j];
									            found = true;
									            break;
								            }
							            }
						            }
						            catch (Exception e)
						            {
										if (e is UnauthorizedAccessException)
										{
											//If we don't have access to the path, this causes an infinite recursion loop...
											throw;
										}
						            }
					            }
					            if (!found) {
					                absolute = directory;
					            }
					        }
					    }
					}
				}
			}
			return absolute + queryString;
		}

		/// <summary>Combines a platform-specific absolute path with a platform-independent relative path that points to a directory.</summary>
		/// <param name="absolute">The platform-specific absolute path.</param>
		/// <param name="relative">The platform-independent relative path.</param>
		/// <returns>A platform-specific absolute path to the specified directory.</returns>
		/// <exception cref="System.Exception">Raised when combining the paths failed, for example due to malformed paths or due to unauthorized access.</exception>
		public static string CombineDirectory(string absolute, string relative) {
			return CombineDirectory(absolute, relative, false);
		}

		/// <summary>Combines a platform-specific absolute path with a platform-independent relative path that points to a file.</summary>
		/// <param name="absolute">The platform-specific absolute path.</param>
		/// <param name="relative">The platform-independent relative path.</param>
		/// <returns>Whether the operation succeeded and the specified file was found.</returns>
		/// <exception cref="System.Exception">Raised when combining the paths failed, for example due to malformed paths or due to unauthorized access.</exception>
		public static string CombineFile(string absolute, string relative) {
			if (System.IO.Path.IsPathRooted(relative) && char.IsLetter(relative[0]))
			{
				throw new ArgumentException("The relative path must not be rooted.");
			}
			if (string.IsNullOrEmpty(absolute))
			{
				throw new ArgumentException("The absolute path was empty.");
			}
			if (string.IsNullOrEmpty(relative))
			{
				throw new ArgumentException("The relative path was empty.");
			}
            int index = relative.IndexOf("??", StringComparison.Ordinal);
			if (index >= 0) {
				string file = CombineFile(absolute, relative.Substring(0, index).TrimEnd());
				if (File.Exists(file)) {
					return file;
				}
				return CombineFile(absolute, relative.Substring(index + 2).TrimStart());
			}
			if (relative.IndexOfAny(InvalidPathChars) >= 0) {
				throw new ArgumentException("The relative path contains invalid characters.");
			}
			string[] parts = relative.Split(PathSeparationChars, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < parts.Length; i++) {
				if (parts[i].Length != 0) {
					/*
					 * Consider only non-empty parts.
					 * */
					if (IsAllPeriods(parts[i]))
					{
						if (i == parts.Length - 1) {
							/*
							 * The last part must not be all periods because
							 * it would reference a directory then, not a file.
							 * */
							throw new ArgumentException("The relative path is malformed.");
						}
						/*
						 * A string of periods is a reference to an
						 * upper directory. A single period is the
						 * current directory. For each additional
						 * period, jump one directory up.
						 * */
						for (int j = 1; j < parts[i].Length; j++) {
							absolute = Path.GetDirectoryName(absolute);
						}
					} else if (i == parts.Length - 1) {
						/*
						 * The last part references a file.
						 * */
					    if (absolute == null) continue;
					    string file = System.IO.Path.Combine(absolute, parts[i]);
					    // ANDROID PORT: from the listing while loading - the same answer as below.
					    int listedFile = Listed(file, false, out string listedFileName);
					    if (listedFile >= 0) {
						    if (listedFile == 0 || string.Equals(listedFileName, parts[i], StringComparison.Ordinal) || File.Exists(file)) {
							    return file;
						    }
						    return System.IO.Path.Combine(absolute, listedFileName);
					    }
					    if (File.Exists(file)) {
					        return file;
					    }
				        /*
						 * Try to find the file case-insensitively.
						 * */
				        if (Directory.Exists(absolute))
				        {
					        string[] files = ListFiles(absolute);
					        for (int j = 0; j < files.Length; j++)
					        {
						        string name = System.IO.Path.GetFileName(files[j]);
						        if (name != null && name.Equals(parts[i], StringComparison.OrdinalIgnoreCase))
						        {
							        return files[j];
						        }
					        }
				        }
				        return file;
					} else {
						/*
						 * This part references a directory.
						 * */
					    if (absolute == null) continue;
					    string directory = System.IO.Path.Combine(absolute, parts[i]);
					    // ANDROID PORT: from the listing while loading - the same answer as below.
					    int listedDirectory = Listed(directory, true, out string listedDirectoryName);
					    if (listedDirectory >= 0) {
						    absolute = listedDirectory == 0 || string.Equals(listedDirectoryName, parts[i], StringComparison.Ordinal) || Directory.Exists(directory)
							    ? directory
							    : System.IO.Path.Combine(absolute, listedDirectoryName);
					    } else if (Directory.Exists(directory)) {
					        absolute = directory;
					    } else {
					        /*
							 * Try to find the directory case-insensitively.
							 * */
					        bool found = false;
					        if (Directory.Exists(absolute)) {
					            string[] directories = ListDirectories(absolute);
					            for (int j = 0; j < directories.Length; j++) {
					                string name = System.IO.Path.GetFileName(directories[j]);
					                if (name != null && name.Equals(parts[i], StringComparison.OrdinalIgnoreCase)) {
					                    absolute = directories[j];
					                    found = true;
					                    break;
					                }
					            }
					        }
					        if (!found) {
					            absolute = directory;
					        }
					    }
					}
				}
			}
			throw new ArgumentException("The reference to the file is malformed.");
		}

		
		internal static HashSet<char> GetInvalidPathChars() => new HashSet<char>
		{
			/* Licensed to the .NET Foundation under one or more agreements.
			 * The .NET Foundation licenses this file to you under the MIT license.
			 *
			 * https://github.com/dotnet/runtime/blob/5535e31a712343a63f5d7d796cd874e563e5ac14/src/libraries/System.Private.CoreLib/src/System/IO/Path.Windows.cs
			 *
			 * Use the Windows invalid path characters for a greater subset
			 * Mono only returns '\0'
			 *
			 * https://github.com/leezer3/OpenBVE/issues/1073
			 */
			'|', '\0',
			(char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
			(char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
			(char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
			(char)31
		};

		internal static HashSet<char> GetInvalidFileNameChars() => new HashSet<char>
		{
			/* Licensed to the .NET Foundation under one or more agreements.
			 * The .NET Foundation licenses this file to you under the MIT license.
			 *
			 * https://github.com/dotnet/runtime/blob/5535e31a712343a63f5d7d796cd874e563e5ac14/src/libraries/System.Private.CoreLib/src/System/IO/Path.Windows.cs
			 */
			'\"', '<', '>', '|', '\0',
			(char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
			(char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
			(char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
			(char)31, ':', '*', '?', '\\', '/'
		};

		/// <summary>Tests whether a string contains characters invalid for use in a file name or path</summary>
		/// <param name="Expression">The string to test</param>
		/// <returns>True if this string contains invalid characters, false otherwise</returns>
		public static bool ContainsInvalidChars(string Expression)
		{
			HashSet<char> a = GetInvalidFileNameChars();
			HashSet<char> b = GetInvalidPathChars();

			if (!IsAbsolutePath(Expression))
			{
				if (Expression.IndexOfAny(InvalidPathChars) != -1)
				{
					// Check our platform independent list first
					return true;
				}
			}
			

			for (int i = 0; i < Expression.Length; i++)
			{
				if (a.Contains(Expression[i]) && b.Contains(Expression[i]))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Checks whether the specified path is an absolute path.
		/// </summary>
		/// <param name="path">The path to test</param>
		/// <returns>True if this path is an absolute path, false otherwise</returns>
		public static bool IsAbsolutePath(string path)
		{
			if (path.Length < 1)
			{
				return false;
			}

			if (path[0] == PathSeparationChars[0] || path[0] == PathSeparationChars[1])
			{
				// e.g.
				// \Test\Foo.txt (Windows)
				// /Test/Foo.txt (Windows, Unix)
				return true;
			}

			if (path.Length < 3)
			{
				return false;
			}

			if (path[1] == ':' && (path[2] == PathSeparationChars[0] || path[2] == PathSeparationChars[1]))
			{
				// e.g.
				// C:\Test\Foo.txt (Windows)
				// C:/Test/Foo.txt (Windows)
				return true;
			}

			return false;
		}


		// --- private functions ---
		
		/// <summary>Checks whether the specified string consists only of periods.</summary>
		/// <param name="text">The string to check.</param>
		/// <returns>Whether the string consists only of periods.</returns>
		private static bool IsAllPeriods(string text) {
			for (int i = 0; i < text.Length; i++) {
				if (text[i] != '.') {
					return false;
				}
			}
			return true;
		}

		/// <summary>Gets the SHA-256 checksum for a file</summary>
		/// <param name="file">The file to hash</param>
		/// <returns>The SHA-256 hash, or an empty string if not valid</returns>
		public static string GetChecksum(string file)
		{
			if (string.IsNullOrEmpty(file) || !File.Exists(file))
			{
				return string.Empty;
			}
			using (FileStream stream = File.OpenRead(file))
			{
				using (SHA256Managed sha = new SHA256Managed())
				{
					byte[] checksum = sha.ComputeHash(stream);
					return BitConverter.ToString(checksum).Replace("-", string.Empty);
				}
			}
		}

		/*
		 * Provide easy mirrors to the System.IO.Path functions
		 */

		/// <summary>Returns the directory information for the specified path string</summary>
		/// <param name="path">The path string</param>
		public static string GetDirectoryName(string path)
		{
			return System.IO.Path.GetDirectoryName(path);
		}

		/// <summary>Returns the file name and extension for the specified path string</summary>
		/// <param name="path">The path string</param>
		public static string GetFileName(string path)
		{
			return System.IO.Path.GetFileName(path);
		}

		/// <summary>Returns the file name for the specified path string</summary>
		/// <param name="path">The path string</param>
		public static string GetFileNameWithoutExtension(string path)
		{
			return System.IO.Path.GetFileNameWithoutExtension(path);
		}

		/// <summary>Determines whether a path contains a filename extension</summary>
		/// <param name="path">The path string</param>
		public static bool HasExtension(string path)
		{
			return System.IO.Path.HasExtension(path);
		}

		/// <summary>Gets a value containing whether the specified path string contains a root</summary>
		/// <param name="path"></param>
		/// <returns></returns>
		public static bool IsPathRooted(string path)
		{
			return System.IO.Path.IsPathRooted(path);
		}
	}
}
