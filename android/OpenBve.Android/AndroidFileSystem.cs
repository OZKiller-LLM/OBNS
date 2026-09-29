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
using Android.Content;
using Android.Util;
using OpenBveApi.Hosts;

namespace OpenBve.Android
{
	/// <summary>
	/// Builds the <see cref="OpenBveApi.FileSystem.FileSystem"/> the rest of OpenBVE reads paths
	/// from, and lays out the folders it expects under Android storage.
	/// </summary>
	/// <remarks>
	/// Upstream derives every path from the executable's directory and the roaming application
	/// data folder, neither of which means anything on Android. The data OpenBVE ships with
	/// (languages, flags, cursors) is packaged as Android assets and extracted once into the
	/// app's private files directory, which is then used as the data folder.
	/// </remarks>
	public static class AndroidFileSystem
	{
		private const string Tag = "OpenBVE";

		/// <summary>Creates the file system and makes sure the folders it names exist.</summary>
		public static OpenBveApi.FileSystem.FileSystem Create(Context context, HostInterface host)
		{
			string files = context.FilesDir.AbsolutePath;
			string data = Path.Combine(files, "Data");
			string settings = Path.Combine(files, "Settings");

			string content = ContentFolder(context);
			string railway = Path.Combine(content, "Railway");
			string train = Path.Combine(content, "Train");

			/*
			 * Extraction walks several hundred assets, so it runs once per install rather than on
			 * every launch. The marker is keyed on the install time, not the version code: during
			 * development the version code stays put while the packaged data changes, and keying
			 * on it left newly added data (such as the default plugin) unextracted.
			 */
			long installed = context.PackageManager.GetPackageInfo(context.PackageName, 0).LastUpdateTime;
			string marker = Path.Combine(data, ".extracted-" + installed);
			if (!File.Exists(marker))
			{
				ExtractAssets(context, "Data", data, overwrite: true);
				Directory.CreateDirectory(data);
				File.WriteAllText(marker, string.Empty);
				Log.Info(Tag, "extracted data assets for install " + installed);
			}

			foreach (string folder in new[] { data, settings, railway, Path.Combine(railway, "Route"), train })
			{
				Directory.CreateDirectory(folder);
			}

			OpenBveApi.FileSystem.FileSystem fileSystem = OpenBveApi.FileSystem.FileSystem.FromCommandLineArgs(new string[0], host);
			fileSystem.DataFolder = data;
			fileSystem.SettingsFolder = settings;
			SetContent(fileSystem, content);
			fileSystem.RestartProcess = string.Empty;
			fileSystem.RestartArguments = string.Empty;

			Log.Info(Tag, "data folder: " + data);
			Log.Info(Tag, "content folder: " + content);
			return fileSystem;
		}

		private static void SetContent(OpenBveApi.FileSystem.FileSystem fileSystem, string content)
		{
			string railway = Path.Combine(content, "Railway");
			string train = Path.Combine(content, "Train");
			fileSystem.InitialRouteFolder = Path.Combine(railway, "Route");
			fileSystem.RouteInstallationDirectory = railway;
			fileSystem.InitialTrainFolder = train;
			fileSystem.TrainInstallationDirectory = train;
			fileSystem.OtherInstallationDirectory = Path.Combine(content, "Other");
			fileSystem.LoksimPackageInstallationDirectory = Path.Combine(content, "Loksim3D");
			fileSystem.LoksimDataDirectory = fileSystem.LoksimPackageInstallationDirectory;
		}

		/*
		 * Where routes and trains live (Railway, Train, and packages' other folders):
		 * Documents/OpenBVE on the SD card when the phone has one mounted - content runs to
		 * gigabytes, and phones fill up - else Documents/OpenBVE in the phone's own storage. Both
		 * are ordinary folders a file manager or a PC reaches, and they outlive uninstalling the
		 * app. Writing there needs all-files access (the storage permission before Android 11):
		 * until it is granted, or if the folder cannot be made, the app's own folder in
		 * Android/data is used, as before - where earlier versions kept content, which the file
		 * finder still offers (OwnContentFolder).
		 */
		private static string ContentFolder(Context context)
		{
			string own = OwnContentFolder(context);
			if (!StorageAccess.IsGranted)
			{
				return own;
			}

			foreach (string candidate in new[] { SdCardDocuments(context), PhoneDocuments() })
			{
				if (candidate == null)
				{
					continue;
				}

				try
				{
					Directory.CreateDirectory(Path.Combine(candidate, "Railway", "Route"));
					Directory.CreateDirectory(Path.Combine(candidate, "Train"));
					return candidate;
				}
				catch (Exception ex)
				{
					Log.Warn(Tag, "content folder " + candidate + " not usable: " + ex.Message);
				}
			}

			return own;
		}

		/// <summary>The app's own external folder: earlier versions' content folder, and the one used without all-files access.</summary>
		public static string OwnContentFolder(Context context) =>
			context.GetExternalFilesDir(null)?.AbsolutePath ?? Path.Combine(context.FilesDir.AbsolutePath, "Content");

		/// <summary>Documents/OpenBVE on the first mounted removable volume (SD card), or null if there is none.</summary>
		private static string SdCardDocuments(Context context)
		{
			Java.IO.File[] volumes = context.GetExternalFilesDirs(null);
			if (volumes == null)
			{
				return null;
			}

			foreach (Java.IO.File volume in volumes)
			{
				if (volume == null || !global::Android.OS.Environment.InvokeIsExternalStorageRemovable(volume) ||
				    global::Android.OS.Environment.GetExternalStorageState(volume) != global::Android.OS.Environment.MediaMounted)
				{
					continue;
				}

				// Each volume's app folder sits at <root>/Android/data/<package>/files.
				string path = volume.AbsolutePath;
				int index = path.IndexOf("/Android/data/", StringComparison.Ordinal);
				if (index > 0)
				{
					return Path.Combine(path.Substring(0, index), global::Android.OS.Environment.DirectoryDocuments, "OpenBVE");
				}
			}

			return null;
		}

		private static string PhoneDocuments()
		{
			string storage = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
			return storage == null ? null : Path.Combine(storage, global::Android.OS.Environment.DirectoryDocuments, "OpenBVE");
		}

		/// <summary>
		/// Moves the content folders to the shared one once all-files access has been granted in
		/// this session (the menu calls it on resuming); true if they moved.
		/// </summary>
		public static bool UpdateContentFolder(Context context, OpenBveApi.FileSystem.FileSystem fileSystem)
		{
			if (fileSystem?.RouteInstallationDirectory == null)
			{
				return false;
			}

			string content = ContentFolder(context);
			string current = Path.GetDirectoryName(fileSystem.RouteInstallationDirectory.TrimEnd('/'));
			if (string.Equals(content, current, StringComparison.Ordinal))
			{
				return false;
			}

			SetContent(fileSystem, content);
			Log.Info(Tag, "content folder now: " + content);
			return true;
		}

		/// <summary>Copies an asset folder to disk.</summary>
		private static void ExtractAssets(Context context, string assetPath, string targetPath, bool overwrite)
		{
			string[] entries;
			try
			{
				entries = context.Assets.List(assetPath);
			}
			catch (IOException)
			{
				return;
			}

			if (entries == null || entries.Length == 0)
			{
				// A leaf: copy the file itself.
				CopyAsset(context, assetPath, targetPath, overwrite);
				return;
			}

			Directory.CreateDirectory(targetPath);
			foreach (string entry in entries)
			{
				ExtractAssets(context, assetPath + "/" + entry, Path.Combine(targetPath, entry), overwrite);
			}
		}

		private static void CopyAsset(Context context, string assetPath, string targetPath, bool overwrite)
		{
			if (!overwrite && File.Exists(targetPath))
			{
				return;
			}

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
				using (Stream source = context.Assets.Open(assetPath))
				using (FileStream target = File.Create(targetPath))
				{
					source.CopyTo(target);
				}
			}
			catch (IOException ex)
			{
				Log.Warn(Tag, "could not extract asset " + assetPath + ": " + ex.Message);
			}
		}
	}
}
