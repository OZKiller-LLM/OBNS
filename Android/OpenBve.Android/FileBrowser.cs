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
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Environment = Android.OS.Environment;

namespace OpenBve.Android
{
	/// <summary>
	/// The in-built file finder: browses the device's storage for route files, train folders or
	/// package archives, as the Windows version's Browse Manually lists do.
	/// </summary>
	/// <remarks>
	/// Content can be picked from wherever it is - the app's own folders, internal storage,
	/// Downloads, an SD card - and is used in place: nothing has to be copied or imported first.
	/// Outside the app's own folders that needs Android's all-files access, which the list offers
	/// to request when a folder cannot be read.
	/// </remarks>
	public class FileBrowser : LinearLayout
	{
		/// <summary>What the browser is looking for.</summary>
		public enum Mode
		{
			Route,
			Train,
			Package
		}

		/// <summary>One row in the list.</summary>
		private class Entry
		{
			public string Path;
			public string Name;
			public EntryKind Kind;
		}

		private enum EntryKind
		{
			Parent,
			Folder,
			Route,
			Train,
			Package,
			PermissionNeeded,
			Message
		}

		private static readonly string[] PackageExtensions = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".l3dpack" };

		private readonly Mode mode;
		private readonly TextView pathLabel;
		private readonly ListView list;
		private readonly EntryAdapter adapter;
		private int generation;

		/// <summary>The folder being shown.</summary>
		public string CurrentFolder { get; private set; }

		/// <summary>The route file, train folder or package chosen, or null.</summary>
		public string SelectedPath { get; private set; }

		/// <summary>Raised when a route file, train folder or package is chosen.</summary>
		public event Action<string> Selected;

		/// <summary>Raised when the folder shown changes.</summary>
		public event Action<string> FolderChanged;

		public FileBrowser(Context context, Mode mode) : base(context)
		{
			this.mode = mode;
			Orientation = Orientation.Vertical;

			LinearLayout header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			header.SetGravity(GravityFlags.CenterVertical);

			Button up = Ui.SmallButton(context, "↑");
			up.Click += (s, e) => GoUp();
			header.AddView(up);

			Button places = Ui.SmallButton(context, Menu.T("android", "places", "Places ▾"));
			places.Click += (s, e) => ShowPlaces(places);
			header.AddView(places);

			pathLabel = new TextView(context) { TextSize = 12.0f, Ellipsize = TextUtils.TruncateAt.Start };
			pathLabel.SetSingleLine(true);
			pathLabel.SetTextColor(Ui.Secondary);
			pathLabel.SetPadding(Ui.Dp(context, 8), 0, 0, 0);
			header.AddView(pathLabel, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));
			AddView(header);

			adapter = new EntryAdapter(context);
			list = new ListView(context) { Adapter = adapter, ChoiceMode = ChoiceMode.Single };
			list.Divider = new global::Android.Graphics.Drawables.ColorDrawable(Color.Argb(40, 255, 255, 255));
			list.DividerHeight = 1;
			list.ItemClick += (s, e) => OnClick(adapter[e.Position], e.Position);
			list.ItemLongClick += (s, e) => OnLongClick(adapter[e.Position]);
			AddView(list, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
		}

		/// <summary>Shows a folder.</summary>
		public void Open(string folder)
		{
			if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
			{
				folder = DefaultFolder(Context, mode);
			}

			CurrentFolder = folder;
			pathLabel.Text = folder;
			FolderChanged?.Invoke(folder);

			int token = ++generation;
			adapter.Set(new List<Entry> { new Entry { Kind = EntryKind.Message, Name = Menu.T("packages", "processing", "Processing, please wait...") } });

			// Listing reads files (to tell routes from other .csv files) and asks the plugins, so it runs off the UI thread.
			Task.Run(() => List(folder)).ContinueWith(t =>
			{
				Activity activity = Context as Activity;
				activity?.RunOnUiThread(() =>
				{
					if (token != generation)
					{
						return;
					}

					adapter.Set(t.IsFaulted ? new List<Entry> { new Entry { Kind = EntryKind.Message, Name = t.Exception?.InnerException?.Message } } : t.Result);
					list.ClearChoices();
					int selected = adapter.IndexOf(SelectedPath);
					if (selected >= 0)
					{
						list.SetItemChecked(selected, true);
					}
				});
			});
		}

		/// <summary>Shows the folder containing a path and marks the path as chosen.</summary>
		public void Reveal(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			SelectedPath = path;
			Open(System.IO.Path.GetDirectoryName(path));
		}

		/// <summary>Forgets the selection.</summary>
		public void ClearSelection()
		{
			SelectedPath = null;
			list.ClearChoices();
			adapter.NotifyDataSetChanged();
		}

		/// <summary>The folder a browser opens in when it has not been anywhere yet.</summary>
		public static string DefaultFolder(Context context, Mode mode)
		{
			switch (mode)
			{
				case Mode.Route:
					return Menu.FileSystem.InitialRouteFolder;
				case Mode.Train:
					return Menu.FileSystem.InitialTrainFolder;
				default:
					string downloads = Environment.GetExternalStoragePublicDirectory(Environment.DirectoryDownloads)?.AbsolutePath;
					return downloads != null && CanList(downloads) ? downloads : Menu.FileSystem.RouteInstallationDirectory;
			}
		}

		private void GoUp()
		{
			string parent = CurrentFolder == null ? null : System.IO.Path.GetDirectoryName(CurrentFolder.TrimEnd('/'));
			if (!string.IsNullOrEmpty(parent))
			{
				Open(parent);
			}
		}

		private void OnClick(Entry entry, int position)
		{
			switch (entry.Kind)
			{
				case EntryKind.Parent:
				case EntryKind.Folder:
					Open(entry.Path);
					break;
				case EntryKind.Route:
				case EntryKind.Train:
				case EntryKind.Package:
					SelectedPath = entry.Path;
					list.SetItemChecked(position, true);
					Selected?.Invoke(entry.Path);
					break;
				case EntryKind.PermissionNeeded:
					StorageAccess.Request(Context as Activity);
					break;
			}
		}

		/// <summary>A long press on a train opens it as a folder, for trains nested inside others.</summary>
		private void OnLongClick(Entry entry)
		{
			if (entry.Kind == EntryKind.Train)
			{
				Open(entry.Path);
			}
		}

		private void ShowPlaces(View anchor)
		{
			List<(string Name, string Path)> places = Places(Context, mode);
			PopupMenu popup = new PopupMenu(Context, anchor);
			for (int i = 0; i < places.Count; i++)
			{
				popup.Menu.Add(0, i, i, places[i].Name);
			}

			popup.MenuItemClick += (s, e) => Open(places[e.Item.ItemId].Path);
			popup.Show();
		}

		/// <summary>The starting points offered in the Places menu.</summary>
		private static List<(string Name, string Path)> Places(Context context, Mode mode)
		{
			List<(string, string)> places = new List<(string, string)>
			{
				(Menu.T("android", "place_routes", "OpenBVE routes"), Menu.FileSystem.InitialRouteFolder),
				(Menu.T("android", "place_trains", "OpenBVE trains"), Menu.FileSystem.InitialTrainFolder),
				(Menu.T("android", "place_railway", "OpenBVE Railway folder"), Menu.FileSystem.RouteInstallationDirectory),
				// Documents/OpenBVE on the SD card (or the phone), or the app's own folder without all-files access.
				(Menu.T("android", "place_app", "OpenBVE folder"), System.IO.Path.GetDirectoryName(Menu.FileSystem.RouteInstallationDirectory.TrimEnd('/')))
			};

			// Earlier versions kept content in the app's own folder: still offered while it has any.
			string own = AndroidFileSystem.OwnContentFolder(context);
			string content = System.IO.Path.GetDirectoryName(Menu.FileSystem.RouteInstallationDirectory.TrimEnd('/'));
			if (own != content && (System.IO.Directory.Exists(System.IO.Path.Combine(own, "Railway")) || System.IO.Directory.Exists(System.IO.Path.Combine(own, "Train"))))
			{
				places.Add((Menu.T("android", "place_app_own", "App folder (earlier content)"), own));
			}

			string storage = Environment.ExternalStorageDirectory?.AbsolutePath;
			if (storage != null)
			{
				places.Add((Menu.T("android", "place_storage", "Internal storage"), storage));
			}

			string downloads = Environment.GetExternalStoragePublicDirectory(Environment.DirectoryDownloads)?.AbsolutePath;
			if (downloads != null)
			{
				places.Add((Menu.T("android", "place_downloads", "Downloads"), downloads));
			}

			// Removable volumes: each app-specific external directory sits four levels below its volume's root.
			Java.IO.File[] volumes = context.GetExternalFilesDirs(null);
			if (volumes != null)
			{
				foreach (Java.IO.File volume in volumes.Skip(1).Where(v => v != null))
				{
					string root = volume.AbsolutePath;
					int index = root.IndexOf("/Android/data/", StringComparison.Ordinal);
					if (index > 0)
					{
						root = root.Substring(0, index);
						places.Add((Menu.T("android", "place_sdcard", "SD card") + " (" + System.IO.Path.GetFileName(root) + ")", root));
					}
				}
			}

			return places;
		}

		private static bool CanList(string folder)
		{
			try
			{
				Directory.GetFileSystemEntries(folder);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>Lists a folder: parent, sub-folders, then the files this mode is looking for, as upstream's lists.</summary>
		private List<Entry> List(string folder)
		{
			List<Entry> entries = new List<Entry>();
			string parent = System.IO.Path.GetDirectoryName(folder.TrimEnd('/'));
			if (!string.IsNullOrEmpty(parent))
			{
				entries.Add(new Entry { Kind = EntryKind.Parent, Name = "..", Path = parent });
			}

			string[] folders;
			string[] files;
			try
			{
				folders = Directory.GetDirectories(folder);
				files = Directory.GetFiles(folder);
			}
			catch (UnauthorizedAccessException)
			{
				entries.Add(new Entry { Kind = StorageAccess.IsGranted ? EntryKind.Message : EntryKind.PermissionNeeded, Name = StorageAccess.IsGranted
					? Menu.T("errors", "security_checkaccess", "Please check that this path is accessible.")
					: Menu.T("android", "storage_permission", "This folder needs access to all files. Tap to allow it.") });
				return entries;
			}

			Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
			Array.Sort(files, StringComparer.OrdinalIgnoreCase);

			foreach (string sub in folders)
			{
				string name = System.IO.Path.GetFileName(sub);
				if (string.IsNullOrEmpty(name) || name[0] == '.')
				{
					continue;
				}

				bool train = mode == Mode.Train && RouteInfo.IsTrainFolder(sub);
				entries.Add(new Entry { Kind = train ? EntryKind.Train : EntryKind.Folder, Name = name, Path = sub });
			}

			foreach (string file in files)
			{
				string name = System.IO.Path.GetFileName(file);
				switch (mode)
				{
					case Mode.Route when RouteInfo.IsRouteFile(file):
						entries.Add(new Entry { Kind = EntryKind.Route, Name = name, Path = file });
						break;
					case Mode.Package when PackageExtensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant()):
						entries.Add(new Entry { Kind = EntryKind.Package, Name = name, Path = file });
						break;
				}
			}

			if (entries.Count == (parent == null ? 0 : 1))
			{
				entries.Add(new Entry { Kind = EntryKind.Message, Name = Menu.T("android", "folder_empty", "Nothing to show in this folder.") });
			}

			return entries;
		}

		/// <summary>Rows: an icon and a name.</summary>
		private class EntryAdapter : BaseAdapter<Entry>
		{
			private readonly Context context;
			private List<Entry> entries = new List<Entry>();

			public EntryAdapter(Context context)
			{
				this.context = context;
			}

			public void Set(List<Entry> newEntries)
			{
				entries = newEntries;
				NotifyDataSetChanged();
			}

			public int IndexOf(string path) => path == null ? -1 : entries.FindIndex(e => e.Path == path);

			public override Entry this[int position] => entries[position];

			public override int Count => entries.Count;

			public override long GetItemId(int position) => position;

			public override View GetView(int position, View convertView, ViewGroup parent)
			{
				TextView row = convertView as TextView ?? new TextView(context) { TextSize = 15.0f };
				int pad = Ui.Dp(context, 10);
				row.SetPadding(pad, pad, pad, pad);
				row.SetSingleLine(true);
				row.Ellipsize = TextUtils.TruncateAt.Middle;
				row.SetBackgroundResource(global::Android.Resource.Drawable.ListSelectorBackground);
				row.SetBackgroundColor(Color.Transparent);

				Entry entry = entries[position];
				string icon;
				switch (entry.Kind)
				{
					case EntryKind.Parent:
						icon = "⬑  ";
						break;
					case EntryKind.Folder:
						icon = "📁  ";
						break;
					case EntryKind.Route:
						icon = "🛤  ";
						break;
					case EntryKind.Train:
						icon = "🚆  ";
						break;
					case EntryKind.Package:
						icon = "📦  ";
						break;
					case EntryKind.PermissionNeeded:
						icon = "🔒  ";
						break;
					default:
						icon = string.Empty;
						break;
				}

				row.Text = icon + entry.Name;
				row.SetTextColor(entry.Kind == EntryKind.Message ? Ui.Secondary : entry.Kind == EntryKind.PermissionNeeded ? Ui.Accent : Ui.Primary);
				bool chosen = entry.Path != null && ((ListView)parent).IsItemChecked(position);
				row.SetBackgroundColor(chosen ? Ui.Selection : Color.Transparent);
				return row;
			}
		}
	}

	/// <summary>Android's all-files access, needed to read content outside the app's own folders.</summary>
	public static class StorageAccess
	{
		/// <summary>Whether the app may read any folder on shared storage.</summary>
		public static bool IsGranted
		{
			get
			{
				if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
				{
					return Environment.IsExternalStorageManager;
				}

				Context context = Application.Context;
				return context.CheckSelfPermission(global::Android.Manifest.Permission.ReadExternalStorage) == global::Android.Content.PM.Permission.Granted;
			}
		}

		/// <summary>
		/// Opens the system page where the user allows it (Android 11 and later), or asks for the
		/// storage permission (earlier versions). The user decides; the app only asks.
		/// </summary>
		public static void Request(Activity activity)
		{
			if (activity == null)
			{
				return;
			}

			if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
			{
				try
				{
					Intent intent = new Intent(global::Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
						global::Android.Net.Uri.Parse("package:" + activity.PackageName));
					activity.StartActivity(intent);
				}
				catch (ActivityNotFoundException)
				{
					activity.StartActivity(new Intent(global::Android.Provider.Settings.ActionManageAllFilesAccessPermission));
				}
			}
			else
			{
				activity.RequestPermissions(new[] { global::Android.Manifest.Permission.ReadExternalStorage, global::Android.Manifest.Permission.WriteExternalStorage }, 1);
			}
		}
	}
}
