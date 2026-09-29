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
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using OpenBveApi.Interface;
using OpenBveApi.Packages;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using Ude;
using ProgressReport = OpenBveApi.Packages.ProgressReport;

namespace OpenBve.Android
{
	/// <summary>An archive chosen for installation, and what the installer made of it.</summary>
	public class InstallCandidate
	{
		/// <summary>The archive file.</summary>
		public string File;

		/// <summary>The package: read from the archive's package.xml, or made up for a plain archive.</summary>
		public Package Package;

		/// <summary>Whether this is an OpenBVE package (it has a package.xml), as opposed to a plain archive.</summary>
		public bool IsPackage;

		/// <summary>For a plain archive: where it will go, in words.</summary>
		public string Layout;

		/// <summary>For a plain archive: the encoding its file names are in.</summary>
		public Encoding NameEncoding;

		/// <summary>Whether <see cref="NameEncoding"/> was detected rather than chosen.</summary>
		public bool EncodingDetected;

		/// <summary>For a plain archive: a few entry names as decoded, preferring non-ASCII ones, so the encoding can be checked by eye.</summary>
		public string[] SampleNames = new string[0];
	}

	/// <summary>
	/// Installs and uninstalls add-ons: the Windows version's Package Management, built on
	/// upstream's own package code (OpenBveApi.Packages), plus plain archives.
	/// </summary>
	/// <remarks>
	/// <para>
	/// OpenBVE packages - archives with a package.xml - go through exactly the Windows procedure:
	/// read the package, check its dependencies and recommendations against the package database,
	/// compare versions with any installed copy, extract to the route, train or other installation
	/// directory by package type, then record it in the database and its file list, so that it can
	/// be uninstalled later.
	/// </para>
	/// <para>
	/// Most BVE content is distributed as plain archives instead, which the Windows installer
	/// rejects and users extract by hand. There is no Explorer to do that with on a phone, so plain
	/// archives are also accepted: their layout decides where each entry goes (Railway/... into the
	/// Railway folder, Train/... or a folder holding train.dat into the Train folder), and they are
	/// recorded in the same database, so uninstalling works the same way for both.
	/// </para>
	/// </remarks>
	public static class PackageInstaller
	{
		private static readonly object Sync = new object();
		private static bool databaseLoaded;

		/// <summary>Messages from loading the database, as translation keys, if any.</summary>
		public static string[] DatabaseMessage { get; private set; } = new string[0];

		/// <summary>Loads the package database, as the Windows version does at startup.</summary>
		public static void EnsureDatabase()
		{
			lock (Sync)
			{
				if (databaseLoaded)
				{
					return;
				}

				string folder = Menu.FileSystem.PackageDatabaseFolder;
				Directory.CreateDirectory(folder);
				Database.LoadDatabase(folder, OpenBveApi.Path.CombineFile(folder, "packages.xml"), out string[] message);
				DatabaseMessage = message;
				databaseLoaded = true;
			}
		}

		/// <summary>The installed packages of a type.</summary>
		public static List<Package> Installed(PackageType type)
		{
			EnsureDatabase();
			switch (type)
			{
				case PackageType.Route:
					return Database.currentDatabase.InstalledRoutes;
				case PackageType.Train:
					return Database.currentDatabase.InstalledTrains;
				default:
					return Database.currentDatabase.InstalledOther;
			}
		}

		// --- reading ---

		/// <summary>Reads an archive: as an OpenBVE package if it is one, otherwise as a plain archive. Null if it is not an archive.</summary>
		public static InstallCandidate Read(string file) => Read(file, null);

		/// <summary>
		/// Reads an archive, with the file-name encoding of a plain archive chosen by the user
		/// (null to detect it) - the equivalent of the Windows Start page's encoding override.
		/// </summary>
		public static InstallCandidate Read(string file, Encoding nameEncoding)
		{
			EnsureDatabase();
			if (nameEncoding != null)
			{
				return ReadPlainArchive(file, nameEncoding);
			}

			Package package = null;
			try
			{
				package = Manipulation.ReadPackage(file);
			}
			catch (Exception)
			{
				// Not a package; may still be a plain archive.
			}

			if (package != null)
			{
				return new InstallCandidate { File = file, Package = package, IsPackage = true };
			}

			return ReadPlainArchive(file, null);
		}

		private static InstallCandidate ReadPlainArchive(string file, Encoding chosen)
		{
			Encoding encoding = chosen ?? DetectNameEncoding(file);
			List<string> keys;
			try
			{
				keys = EntryKeys(file, encoding);
			}
			catch (Exception ex)
			{
				global::Android.Util.Log.Warn("OpenBVE", "not a readable archive: " + file + ": " + ex);
				return null;
			}

			if (keys.Count == 0)
			{
				return null;
			}

			Plan plan = MakePlan(keys, System.IO.Path.GetFileNameWithoutExtension(file));
			Package package = new Package
			{
				Name = System.IO.Path.GetFileNameWithoutExtension(file),
				Author = Menu.T("packages", "unknown_file", "Unknown"),
				GUID = Guid.NewGuid().ToString(),
				PackageType = plan.Type,
				PackageFile = file,
				PackageVersion = new Version(1, 0, 0),
				Description = Menu.T("android", "plain_archive", "A plain archive (no package.xml). Its contents will be placed by their folder layout."),
				Dependancies = new List<Package>(),
				Reccomendations = new List<Package>(),
				DependantPackages = new List<string>()
			};

			return new InstallCandidate
			{
				File = file,
				Package = package,
				IsPackage = false,
				Layout = plan.Describe(),
				NameEncoding = encoding,
				EncodingDetected = chosen == null,
				SampleNames = keys.Where(k => k.Any(c => c > 0x7F)).Concat(keys).Distinct().Take(4).ToArray()
			};
		}

		// --- checks, as buttonInstall_Click ---

		/// <summary>Installed packages this one needs but that are missing, or null.</summary>
		public static List<Package> MissingDependencies(InstallCandidate candidate) =>
			Database.CheckDependsReccomends(candidate.Package.Dependancies?.ToList());

		/// <summary>Recommended packages that are not installed, or null.</summary>
		public static List<Package> MissingRecommendations(InstallCandidate candidate) =>
			Database.CheckDependsReccomends(candidate.Package.Reccomendations?.ToList());

		/// <summary>How the package compares with an installed copy.</summary>
		public static VersionInformation CheckVersion(InstallCandidate candidate, out Package installed)
		{
			installed = null;
			return Information.CheckVersion(candidate.Package, Installed(candidate.Package.PackageType), ref installed);
		}

		// --- installing ---

		/// <summary>
		/// Installs the candidate. Runs on the calling thread (call it off the UI thread); progress
		/// arrives through <paramref name="progress"/> as a percentage and the current entry.
		/// </summary>
		/// <returns>The files installed, one per line.</returns>
		public static string Install(InstallCandidate candidate, Package replacing, Action<int, string> progress)
		{
			string files = string.Empty;
			if (candidate.IsPackage)
			{
				// Upstream's extraction, to the directory for the package's type, reporting through its events.
				string directory;
				switch (candidate.Package.PackageType)
				{
					case PackageType.Route:
						directory = Menu.FileSystem.RouteInstallationDirectory;
						break;
					case PackageType.Train:
						directory = Menu.FileSystem.TrainInstallationDirectory;
						break;
					case PackageType.Loksim3D:
						directory = Menu.FileSystem.LoksimPackageInstallationDirectory;
						break;
					default:
						directory = Menu.FileSystem.OtherInstallationDirectory;
						break;
				}

				Exception problem = null;
				EventHandler<ProgressReport> onProgress = (s, e) => progress?.Invoke(e.Progress, e.CurrentFile);
				EventHandler<ProblemReport> onProblem = (s, e) => problem = e.Exception;
				Manipulation.ProgressChanged += onProgress;
				Manipulation.ProblemReport += onProblem;
				try
				{
					Manipulation.ExtractPackage(candidate.Package, directory, Menu.FileSystem.PackageDatabaseFolder, ref files);
				}
				finally
				{
					Manipulation.ProgressChanged -= onProgress;
					Manipulation.ProblemReport -= onProblem;
				}

				if (problem != null)
				{
					throw problem;
				}
			}
			else
			{
				files = ExtractPlainArchive(candidate, progress);
			}

			Record(candidate.Package, replacing);
			return files;
		}

		/// <summary>Adds the package to the database, replacing an older copy, and saves it, as the Windows version does.</summary>
		private static void Record(Package package, Package replacing)
		{
			lock (Sync)
			{
				List<Package> list = Installed(package.PackageType);
				if (replacing != null)
				{
					list.RemoveAll(p => p.GUID == package.GUID);
				}

				package.DependantPackages = package.DependantPackages ?? new List<string>();
				list.Add(package);
				Database.currentDatabase.AddDependancies(package);
				if (!Database.SaveDatabase())
				{
					throw new IOException(Menu.T("packages", "database_save_error", "An error occured whilst saving the package database."));
				}
			}
		}

		// --- uninstalling ---

		/// <summary>Packages that depend on this one, and would break if it went.</summary>
		public static List<Package> Dependants(Package package) =>
			package.DependantPackages == null || package.DependantPackages.Count == 0
				? new List<Package>()
				: Database.CheckUninstallDependancies(package.DependantPackages);

		/// <summary>Uninstalls a package through upstream's code: deletes its recorded files and removes it from the database.</summary>
		/// <returns>The uninstall log.</returns>
		public static string Uninstall(Package package, out bool success)
		{
			string log = string.Empty;
			success = Manipulation.UninstallPackage(package, Menu.FileSystem.PackageDatabaseFolder, ref log);
			if (log == null)
			{
				log = Menu.T("packages", "uninstall_missing_xml", "Unable to uninstall the selected package.\nXML file list missing.");
			}

			lock (Sync)
			{
				Installed(package.PackageType).RemoveAll(p => p.GUID == package.GUID);
				Database.SaveDatabase();
			}

			// Leave no empty folders behind.
			string clean = string.Empty;
			foreach (string root in new[] { Menu.FileSystem.RouteInstallationDirectory, Menu.FileSystem.TrainInstallationDirectory, Menu.FileSystem.OtherInstallationDirectory })
			{
				if (Directory.Exists(root))
				{
					DatabaseFunctions.CleanDirectory(root, ref clean);
				}
			}

			return log;
		}

		// --- plain archives ---

		/// <summary>Where a plain archive's entries go.</summary>
		private class Plan
		{
			public PackageType Type;
			public string StripPrefix = string.Empty;
			/// <summary>Where entries outside Railway/ and Train/ go, relative to the installation directories.</summary>
			public string LooseTarget;
			public string LooseRoot;
			public bool HasRailway;
			public bool HasTrain;

			public string Describe()
			{
				List<string> parts = new List<string>();
				if (HasRailway)
				{
					parts.Add("Railway → " + Menu.FileSystem.RouteInstallationDirectory);
				}

				if (HasTrain)
				{
					parts.Add("Train → " + Menu.FileSystem.TrainInstallationDirectory);
				}

				if (LooseRoot != null)
				{
					// CombineDirectory refuses an empty relative path.
					parts.Add("→ " + (string.IsNullOrEmpty(LooseTarget) ? LooseRoot : OpenBveApi.Path.CombineDirectory(LooseRoot, LooseTarget)));
				}

				return string.Join("\n", parts);
			}
		}

		private static readonly string[] RailwayParts = { "route", "object", "sound" };

		private static Plan MakePlan(List<string> keys, string archiveName)
		{
			Plan plan = new Plan();
			List<string[]> paths = keys.Select(k => k.Split('/')).ToList();

			// A single wrapping folder that is not itself content (not Railway, Train, Route..., nor a train) is stripped.
			string[] tops = paths.Select(p => p[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			if (tops.Length == 1 && paths.All(p => p.Length > 1))
			{
				string top = tops[0].ToLowerInvariant();
				bool contentFolder = top == "railway" || top == "train" || RailwayParts.Contains(top);
				bool isTrain = paths.Any(p => p.Length == 2 && IsTrainFile(p[1]));
				if (!contentFolder && !isTrain)
				{
					plan.StripPrefix = tops[0] + "/";
					paths = paths.Select(p => p.Skip(1).ToArray()).ToList();
				}
			}

			plan.HasRailway = paths.Any(p => p.Length > 1 && (p[0].Equals("railway", StringComparison.OrdinalIgnoreCase) || RailwayParts.Contains(p[0].ToLowerInvariant())));
			plan.HasTrain = paths.Any(p => p.Length > 1 && p[0].Equals("train", StringComparison.OrdinalIgnoreCase));

			List<string[]> loose = paths.Where(p =>
				!(p.Length > 1 && (p[0].Equals("railway", StringComparison.OrdinalIgnoreCase) ||
				                   p[0].Equals("train", StringComparison.OrdinalIgnoreCase) ||
				                   RailwayParts.Contains(p[0].ToLowerInvariant())))).ToList();

			if (loose.Count != 0)
			{
				if (loose.Any(p => IsTrainFile(p[p.Length - 1])))
				{
					// Train folders: kept as they are, or wrapped in a folder named after the archive
					// if train.dat sits at the archive's root.
					plan.LooseRoot = Menu.FileSystem.TrainInstallationDirectory;
					plan.LooseTarget = loose.Any(p => p.Length == 1 && IsTrainFile(p[0])) ? archiveName : null;
					plan.Type = PackageType.Train;
				}
				else if (loose.Any(p => p.Length == 1 && IsRouteExtension(p[0])))
				{
					// Route files without a Railway layout: their own folder under Railway/Route.
					plan.LooseRoot = OpenBveApi.Path.CombineDirectory(Menu.FileSystem.RouteInstallationDirectory, "Route");
					plan.LooseTarget = archiveName;
					plan.Type = PackageType.Route;
				}
				else if (!plan.HasRailway && !plan.HasTrain)
				{
					plan.LooseRoot = Menu.FileSystem.OtherInstallationDirectory;
					plan.LooseTarget = archiveName;
					plan.Type = PackageType.Other;
				}
			}

			if (plan.Type == PackageType.NotFound)
			{
				plan.Type = plan.HasRailway ? PackageType.Route : plan.HasTrain ? PackageType.Train : PackageType.Other;
			}

			return plan;
		}

		private static bool IsTrainFile(string name) =>
			name.Equals("train.dat", StringComparison.OrdinalIgnoreCase) || name.Equals("train.xml", StringComparison.OrdinalIgnoreCase);

		private static bool IsRouteExtension(string name)
		{
			string extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
			return extension == ".csv" || extension == ".rw";
		}

		/// <summary>Where one entry goes, or null to skip it (upstream's skipped files, or an entry outside the plan).</summary>
		private static string Target(Plan plan, string key)
		{
			if (plan.StripPrefix.Length != 0)
			{
				if (!key.StartsWith(plan.StripPrefix, StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}

				key = key.Substring(plan.StripPrefix.Length);
			}

			string[] parts = key.Split('/');
			string first = parts[0].ToLowerInvariant();
			if (parts.Length > 1 && first == "railway")
			{
				return OpenBveApi.Path.CombineFile(Menu.FileSystem.RouteInstallationDirectory, string.Join("/", parts.Skip(1)));
			}

			if (parts.Length > 1 && first == "train")
			{
				return OpenBveApi.Path.CombineFile(Menu.FileSystem.TrainInstallationDirectory, string.Join("/", parts.Skip(1)));
			}

			if (parts.Length > 1 && RailwayParts.Contains(first))
			{
				return OpenBveApi.Path.CombineFile(Menu.FileSystem.RouteInstallationDirectory, key);
			}

			if (plan.LooseRoot == null)
			{
				return null;
			}

			string root = plan.LooseTarget != null ? OpenBveApi.Path.CombineDirectory(plan.LooseRoot, plan.LooseTarget) : plan.LooseRoot;
			return OpenBveApi.Path.CombineFile(root, key);
		}

		private static readonly string[] SkippedFiles = { "thumbs.db", "desktop.ini", ".ds_store" };

		private static string ExtractPlainArchive(InstallCandidate candidate, Action<int, string> progress)
		{
			List<string> keys = EntryKeys(candidate.File, candidate.NameEncoding);
			Plan plan = MakePlan(keys, System.IO.Path.GetFileNameWithoutExtension(candidate.File));
			List<string> installed = new List<string>();

			using (PlainArchive archive = new PlainArchive(candidate.File, candidate.NameEncoding))
			{
				List<PlainArchive.Entry> entries = archive.Entries;
				for (int i = 0; i < entries.Count; i++)
				{
					PlainArchive.Entry entry = entries[i];
					string key = entry.Key;
					progress?.Invoke((int)(100.0 * i / Math.Max(1, entries.Count)), key);
					if (entry.Size == 0 || SkippedFiles.Contains(System.IO.Path.GetFileName(key).ToLowerInvariant()))
					{
						continue;
					}

					string target = Target(plan, key);
					if (target == null)
					{
						continue;
					}

					// Refuse entries that would escape the installation folders ("zip slip").
					string full = System.IO.Path.GetFullPath(target);
					if (!IsInside(full, Menu.FileSystem.RouteInstallationDirectory) && !IsInside(full, Menu.FileSystem.TrainInstallationDirectory) &&
					    !IsInside(full, Menu.FileSystem.OtherInstallationDirectory))
					{
						continue;
					}

					Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
					using (Stream source = entry.Open())
					using (FileStream destination = File.Create(full))
					{
						source.CopyTo(destination);
					}

					installed.Add(full);
				}
			}

			progress?.Invoke(100, string.Empty);

			// The same file list upstream writes, so that UninstallPackage can remove these files.
			string listFolder = OpenBveApi.Path.CombineDirectory(Menu.FileSystem.PackageDatabaseFolder, "Installed");
			Directory.CreateDirectory(listFolder);
			using (StreamWriter writer = new StreamWriter(OpenBveApi.Path.CombineFile(listFolder, candidate.Package.GUID.ToUpper() + ".xml")))
			{
				new XmlSerializer(typeof(List<string>)).Serialize(writer, installed);
			}

			return string.Join("\n", installed);
		}

		private static bool IsInside(string path, string folder)
		{
			string root = System.IO.Path.GetFullPath(folder).TrimEnd('/') + "/";
			return path.StartsWith(root, StringComparison.Ordinal);
		}

		private static string Normalise(string key) => key.Replace('\\', '/').TrimStart('/');

		private static List<string> EntryKeys(string file, Encoding encoding)
		{
			using (PlainArchive archive = new PlainArchive(file, encoding))
			{
				return archive.Entries.Select(e => e.Key).ToList();
			}
		}

		/// <summary>
		/// The files in a plain archive, with their names decoded in a given encoding.
		/// </summary>
		/// <remarks>
		/// Zip files - nearly all BVE content - are read with .NET's own zip support, which takes an
		/// explicit encoding for names not flagged as UTF-8; SharpCompress 0.50 decodes those as
		/// CP437 whatever its options say. Other formats (7z, RAR, tar) go through SharpCompress,
		/// as upstream's installer does; 7z and RAR5 store names in Unicode anyway.
		/// </remarks>
		private sealed class PlainArchive : IDisposable
		{
			public sealed class Entry
			{
				public string Key;
				public long Size;
				public Func<Stream> Open;
			}

			private readonly Stream stream;
			private readonly IDisposable archive;

			public List<Entry> Entries { get; } = new List<Entry>();

			public PlainArchive(string file, Encoding nameEncoding)
			{
				stream = File.OpenRead(file);
				byte[] magic = new byte[4];
				int read = stream.Read(magic, 0, 4);
				stream.Position = 0;
				if (read == 4 && magic[0] == 'P' && magic[1] == 'K' && (magic[2] == 3 || magic[2] == 5 || magic[2] == 7))
				{
					System.IO.Compression.ZipArchive zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read, true, nameEncoding ?? Encoding.UTF8);
					archive = zip;
					foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
					{
						// Directories are entries whose names end in a separator.
						if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
						{
							continue;
						}

						System.IO.Compression.ZipArchiveEntry captured = entry;
						Entries.Add(new Entry { Key = Normalise(entry.FullName), Size = entry.Length, Open = captured.Open });
					}
				}
				else
				{
					IArchive other = ArchiveFactory.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true });
					archive = other;
					foreach (IArchiveEntry entry in other.Entries.Where(e => !e.IsDirectory && e.Key != null))
					{
						IArchiveEntry captured = entry;
						Entries.Add(new Entry { Key = Normalise(entry.Key), Size = entry.Size, Open = captured.OpenEntryStream });
					}
				}
			}

			public void Dispose()
			{
				archive?.Dispose();
				stream.Dispose();
			}
		}

		/// <summary>
		/// The encoding of an archive's file names. Zip archives made on Windows without the UTF-8
		/// flag store names in the maker's ANSI code page - Big5, Shift-JIS, GBK - with nothing to
		/// say which; the raw name bytes are run through the same detector OpenBVE uses for text files.
		/// </summary>
		private static Encoding DetectNameEncoding(string file)
		{
			/*
			 * The raw name bytes: read the names as Latin-1, which maps every byte to the character
			 * of the same value, then turn them back into bytes. Names the archive flags as UTF-8 are
			 * decoded as such regardless, and show up with characters above 0xFF; those are already
			 * right and say nothing about the rest.
			 */
			List<byte> raw = new List<byte>();
			try
			{
				foreach (string key in EntryKeys(file, Encoding.Latin1))
				{
					if (key.All(c => c <= 0xFF))
					{
						raw.AddRange(Encoding.Latin1.GetBytes(key));
						raw.Add((byte)'\n');
					}
				}
			}
			catch (Exception)
			{
				return Encoding.UTF8;
			}

			byte[] names = raw.ToArray();
			if (names.All(b => b < 0x80))
			{
				return Encoding.UTF8;
			}

			try
			{
				new UTF8Encoding(false, true).GetString(names);
				return Encoding.UTF8;
			}
			catch (DecoderFallbackException)
			{
				// Not UTF-8: detect.
			}

			/*
			 * File names are short, often too short for the detector to be sure, and it happily
			 * calls a handful of Big5 bytes Latin-1. So its answer is only taken when it is confident
			 * and names a multi-byte encoding; otherwise the legacy code pages are tried in the order
			 * the user's language makes likely, and the first that decodes every name cleanly wins.
			 */
			List<int> candidates = new List<int>();
			CharsetDetector detector = new CharsetDetector();
			detector.Feed(names, 0, names.Length);
			detector.DataEnd();
			try
			{
				if (detector.Charset != null && detector.Confidence >= 0.5f)
				{
					int detected = Encoding.GetEncoding(detector.Charset).CodePage;
					if (MultiByteCodePages.Contains(detected))
					{
						candidates.Add(detected);
					}
				}
			}
			catch (ArgumentException)
			{
				// Unknown name.
			}

			candidates.AddRange(LanguageCodePages(Translations.CurrentLanguageCode));
			candidates.AddRange(LanguageCodePages(Java.Util.Locale.Default.ToLanguageTag()));
			candidates.AddRange(MultiByteCodePages);
			foreach (int codePage in candidates.Distinct())
			{
				try
				{
					Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(names);
					return Encoding.GetEncoding(codePage);
				}
				catch (DecoderFallbackException)
				{
					// Not this one.
				}
			}

			// Every byte sequence is valid in the old DOS code page, the zip format's default.
			return Encoding.GetEncoding(437);
		}

		/// <summary>The ANSI code pages Windows uses for the languages BVE content is mostly made in: Big5, GBK, Shift-JIS, EUC-KR.</summary>
		private static readonly int[] MultiByteCodePages = { 950, 936, 932, 949 };

		/// <summary>The code pages a Windows machine set to this language would have written names in, most likely first.</summary>
		private static IEnumerable<int> LanguageCodePages(string language)
		{
			language = (language ?? string.Empty).ToLowerInvariant();
			if (language.StartsWith("zh-hk") || language.StartsWith("zh-tw") || language.StartsWith("zh-mo") || language.Contains("hant"))
			{
				return new[] { 950, 936 };
			}

			if (language.StartsWith("zh"))
			{
				return new[] { 936, 950 };
			}

			if (language.StartsWith("ja"))
			{
				return new[] { 932 };
			}

			if (language.StartsWith("ko"))
			{
				return new[] { 949 };
			}

			return new int[0];
		}

		/// <summary>The encodings offered when the user overrides detection, as the Windows encoding lists offer.</summary>
		public static readonly int[] SelectableCodePages = { 65001, 950, 936, 932, 949, 1252, 437 };
	}
}
