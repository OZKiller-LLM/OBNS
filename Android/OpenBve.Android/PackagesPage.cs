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
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using OpenBveApi.Packages;

namespace OpenBve.Android
{
	/// <summary>
	/// Package Management: install add-ons from archives and uninstall them again, following the
	/// Windows version's panels (select, details, dependency and version checks, progress, result).
	/// </summary>
	public class PackagesPage : LinearLayout
	{
		/// <summary>Request code for the system file picker.</summary>
		public const int PickArchiveRequest = 4101;

		private readonly Activity activity;
		private readonly Button installTab;
		private readonly Button installedTab;
		private readonly LinearLayout installPane;
		private readonly LinearLayout installedPane;
		private readonly FileBrowser browser;
		private readonly TextView details;
		private readonly LinearLayout encodingRow;
		private readonly Spinner encodingSpinner;
		private bool fillingEncodings;
		private readonly ImageView image;
		private readonly Button install;
		private readonly ProgressBar progress;
		private readonly TextView progressLabel;
		private readonly Spinner typeSpinner;
		private readonly ListView installedList;
		private readonly TextView installedDetails;
		private readonly Button uninstall;

		private InstallCandidate candidate;
		private string temporaryCopy;
		private bool busy;
		private List<Package> shownPackages = new List<Package>();
		private Package selectedInstalled;

		/// <summary>Raised after something was installed or uninstalled, so other pages can refresh.</summary>
		public event Action ContentChanged;

		public PackagesPage(Activity activity) : base(activity)
		{
			this.activity = activity;
			Orientation = Orientation.Vertical;

			LinearLayout tabs = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
			installTab = Ui.SmallButton(activity, Menu.T("packages", "install_header", "Install a Package"));
			installTab.Click += (s, e) => ShowPane(false);
			installedTab = Ui.SmallButton(activity, Menu.T("packages", "list", "Installed Packages"));
			installedTab.Click += (s, e) => ShowPane(true);
			tabs.AddView(installTab);
			tabs.AddView(installedTab);
			AddView(tabs);

			FrameLayout panes = new FrameLayout(activity);
			panes.SetPadding(0, Ui.Dp(activity, 8), 0, 0);
			AddView(panes, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
			int pad = Ui.Dp(activity, 8);

			// --- install ---
			installPane = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
			LinearLayout left = new LinearLayout(activity) { Orientation = Orientation.Vertical };
			left.Background = Ui.Panel(activity, Ui.Surface);
			left.SetPadding(pad, pad, pad, pad);
			left.AddView(Ui.Label(activity, Menu.T("packages", "install_select", "Select Package......")));
			browser = new FileBrowser(activity, FileBrowser.Mode.Package);
			browser.Selected += file => Select(file, false);
			left.AddView(browser, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
			Button picker = Ui.Button(activity, Menu.T("android", "system_picker", "Choose with the system file picker…"));
			picker.Click += (s, e) => OpenSystemPicker();
			left.AddView(picker, Ui.Stacked(activity));
			installPane.AddView(left, Ui.Weighted(1.0f, activity, 8));

			LinearLayout right = new LinearLayout(activity) { Orientation = Orientation.Vertical };
			right.Background = Ui.Panel(activity, Ui.Surface);
			right.SetPadding(pad, pad, pad, pad);
			image = new ImageView(activity);
			image.SetScaleType(ImageView.ScaleType.FitCenter);
			image.SetAdjustViewBounds(true);
			image.Visibility = ViewStates.Gone;
			right.AddView(image, new LayoutParams(ViewGroup.LayoutParams.MatchParent, Ui.Dp(activity, 100)));
			// File-name encoding of a plain archive, as the Windows Start page's encoding choice.
			encodingRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
			encodingRow.SetGravity(GravityFlags.CenterVertical);
			encodingRow.AddView(Ui.Label(activity, Menu.T("start", "route_settings_encoding", "Encoding:")));
			encodingSpinner = new Spinner(activity);
			encodingSpinner.ItemSelected += (s, e) => OnEncodingChosen(e.Position);
			encodingRow.AddView(encodingSpinner, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));
			right.AddView(encodingRow);
			ScrollView scroll = new ScrollView(activity);
			details = Ui.Body(activity, Menu.T("packages", "selection_none", "No package selected."));
			scroll.AddView(details);
			right.AddView(scroll, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
			progressLabel = Ui.Label(activity, string.Empty, 12.0f);
			progressLabel.SetSingleLine(true);
			progressLabel.Ellipsize = global::Android.Text.TextUtils.TruncateAt.Middle;
			right.AddView(progressLabel);
			progress = new ProgressBar(activity, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100, Visibility = ViewStates.Gone };
			right.AddView(progress, Ui.Stacked(activity));
			install = Ui.Button(activity, Menu.T("packages", "install_button", "Install Package"), prominent: true);
			install.Enabled = false;
			install.Alpha = 0.45f;
			install.Click += (s, e) => BeginInstall();
			right.AddView(install, Ui.Stacked(activity));
			installPane.AddView(right, Ui.Weighted(1.0f, activity));
			panes.AddView(installPane);

			// --- installed ---
			installedPane = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
			LinearLayout listSide = new LinearLayout(activity) { Orientation = Orientation.Vertical };
			listSide.Background = Ui.Panel(activity, Ui.Surface);
			listSide.SetPadding(pad, pad, pad, pad);
			listSide.AddView(Ui.Label(activity, Menu.T("packages", "list_type", "Select the type of packages you wish to view:")));
			typeSpinner = new Spinner(activity);
			typeSpinner.Adapter = new ArrayAdapter<string>(activity, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, new[]
			{
				Menu.T("packages", "type_route", "Route"),
				Menu.T("packages", "type_train", "Train"),
				Menu.T("packages", "type_other", "Other")
			});
			typeSpinner.ItemSelected += (s, e) => RefreshInstalled();
			listSide.AddView(typeSpinner);
			installedList = new ListView(activity) { ChoiceMode = ChoiceMode.Single };
			installedList.ItemClick += (s, e) => ShowInstalled(e.Position);
			listSide.AddView(installedList, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
			installedPane.AddView(listSide, Ui.Weighted(1.0f, activity, 8));

			LinearLayout infoSide = new LinearLayout(activity) { Orientation = Orientation.Vertical };
			infoSide.Background = Ui.Panel(activity, Ui.Surface);
			infoSide.SetPadding(pad, pad, pad, pad);
			ScrollView infoScroll = new ScrollView(activity);
			installedDetails = Ui.Body(activity, Menu.T("packages", "selection_none", "No package selected."));
			infoScroll.AddView(installedDetails);
			infoSide.AddView(infoScroll, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
			uninstall = Ui.Button(activity, Menu.T("packages", "uninstall_button", "Uninstall Package"));
			uninstall.Enabled = false;
			uninstall.Alpha = 0.45f;
			uninstall.Click += (s, e) => BeginUninstall();
			infoSide.AddView(uninstall, Ui.Stacked(activity));
			installedPane.AddView(infoSide, Ui.Weighted(1.0f, activity));
			panes.AddView(installedPane);

			browser.Open(FileBrowser.DefaultFolder(activity, FileBrowser.Mode.Package));
			ShowPane(false);
		}

		private void ShowPane(bool installed)
		{
			installTab.Background = Ui.Panel(activity, installed ? Ui.SurfaceRaised : Ui.Selection, 8);
			installedTab.Background = Ui.Panel(activity, installed ? Ui.Selection : Ui.SurfaceRaised, 8);
			installPane.Visibility = installed ? ViewStates.Gone : ViewStates.Visible;
			installedPane.Visibility = installed ? ViewStates.Visible : ViewStates.Gone;
			if (installed)
			{
				RefreshInstalled();
			}
		}

		/// <summary>Refreshes the browser, e.g. after storage access was granted.</summary>
		public void Refresh() => browser.Open(browser.CurrentFolder);

		// --- choosing an archive ---

		private void OpenSystemPicker()
		{
			Intent intent = new Intent(Intent.ActionOpenDocument);
			intent.AddCategory(Intent.CategoryOpenable);
			intent.SetType("*/*");
			intent.PutExtra(Intent.ExtraMimeTypes, new[] { "application/zip", "application/x-7z-compressed", "application/x-rar-compressed", "application/vnd.rar", "application/x-tar", "application/gzip", "application/octet-stream" });
			activity.StartActivityForResult(intent, PickArchiveRequest);
		}

		/// <summary>
		/// Receives the archive the system picker returned. A picked document is only a content URI,
		/// and the archive code needs a seekable file, so it is copied into the app's cache first.
		/// </summary>
		public void OnPicked(global::Android.Net.Uri uri)
		{
			if (uri == null || busy)
			{
				return;
			}

			string name = DisplayName(uri) ?? "package.zip";
			string target = System.IO.Path.Combine(activity.CacheDir.AbsolutePath, "picked", name);
			ShowBusy(Menu.T("packages", "processing", "Processing, please wait..."));
			Task.Run(() =>
			{
				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
				using (Stream source = activity.ContentResolver.OpenInputStream(uri))
				using (FileStream destination = File.Create(target))
				{
					source.CopyTo(destination);
				}
			}).ContinueWith(t => activity.RunOnUiThread(() =>
			{
				EndBusy();
				if (t.IsFaulted)
				{
					details.Text = t.Exception?.InnerException?.Message;
					return;
				}

				Select(target, true);
			}));
		}

		private string DisplayName(global::Android.Net.Uri uri)
		{
			try
			{
				using (global::Android.Database.ICursor cursor = activity.ContentResolver.Query(uri, new[] { global::Android.Provider.OpenableColumns.DisplayName }, null, null, null))
				{
					if (cursor != null && cursor.MoveToFirst())
					{
						return System.IO.Path.GetFileName(cursor.GetString(0));
					}
				}
			}
			catch (Exception)
			{
				// No name available.
			}

			return null;
		}

		/// <summary>Reads the chosen archive and shows what is in it (ShowPackageToInstall).</summary>
		private void Select(string file, bool isTemporaryCopy)
		{
			if (busy)
			{
				return;
			}

			DeleteTemporaryCopy();
			temporaryCopy = isTemporaryCopy ? file : null;
			Read(file, null);
		}

		/// <summary>The user picked a file-name encoding: read the archive again with it.</summary>
		private void OnEncodingChosen(int position)
		{
			if (fillingEncodings || candidate == null || candidate.IsPackage || busy)
			{
				return;
			}

			// Position 0 is "detected"; the rest follow SelectableCodePages.
			Encoding chosen = position == 0 ? null : Encoding.GetEncoding(PackageInstaller.SelectableCodePages[position - 1]);
			Read(candidate.File, chosen);
		}

		private void ShowEncodings()
		{
			if (candidate == null || candidate.IsPackage)
			{
				encodingRow.Visibility = ViewStates.Gone;
				return;
			}

			List<string> names = new List<string> { "Auto" + (candidate.EncodingDetected ? " (" + candidate.NameEncoding.WebName + ")" : string.Empty) };
			names.AddRange(PackageInstaller.SelectableCodePages.Select(cp => Encoding.GetEncoding(cp).WebName + " - " + cp));
			fillingEncodings = true;
			encodingSpinner.Adapter = new ArrayAdapter<string>(activity, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, names);
			int index = candidate.EncodingDetected ? 0 : Array.IndexOf(PackageInstaller.SelectableCodePages, candidate.NameEncoding.CodePage) + 1;
			encodingSpinner.SetSelection(Math.Max(0, index), false);
			// The spinner reports the selection asynchronously; ignore that echo.
			encodingSpinner.Post(() => fillingEncodings = false);
			encodingRow.Visibility = ViewStates.Visible;
		}

		private void Read(string file, Encoding nameEncoding)
		{
			candidate = null;
			SetEnabled(install, false);
			ShowBusy(Menu.T("packages", "processing", "Processing, please wait..."));
			Task.Run(() => PackageInstaller.Read(file, nameEncoding)).ContinueWith(t => activity.RunOnUiThread(() =>
			{
				EndBusy();
				if (t.IsFaulted)
				{
					global::Android.Util.Log.Warn("OpenBVE", "reading " + file + " failed: " + t.Exception);
				}

				candidate = t.IsFaulted ? null : t.Result;
				if (candidate == null)
				{
					details.Text = Menu.T("packages", "install_invalid", "This file does not appear to be a valid OpenBVE package.");
					details.SetTextColor(Ui.Danger);
					image.Visibility = ViewStates.Gone;
					encodingRow.Visibility = ViewStates.Gone;
					return;
				}

				details.SetTextColor(Ui.Primary);
				details.Text = Describe(candidate.Package) + (candidate.IsPackage
					? string.Empty
					: "\n\n" + candidate.Layout + "\n\n" + Menu.T("start", "route_settings_encoding_preview", "Preview:") + "\n" + string.Join("\n", candidate.SampleNames));
				ShowEncodings();
				ShowPackageImage(candidate.Package);
				SetEnabled(install, true);
			}));
		}

		private void ShowPackageImage(Package package)
		{
			Bitmap bitmap = null;
			if (package.PackageImage is System.Drawing.Bitmap skia)
			{
				try
				{
					using (MemoryStream stream = new MemoryStream())
					{
						skia.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
						byte[] bytes = stream.ToArray();
						bitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
					}
				}
				catch (Exception)
				{
					bitmap = null;
				}
			}

			image.SetImageBitmap(bitmap);
			image.Visibility = bitmap != null ? ViewStates.Visible : ViewStates.Gone;
		}

		/// <summary>The package details, labelled as the Windows install panel labels them.</summary>
		private static string Describe(Package package)
		{
			string type;
			switch (package.PackageType)
			{
				case PackageType.Route:
					type = Menu.T("packages", "type_route", "Route");
					break;
				case PackageType.Train:
					type = Menu.T("packages", "type_train", "Train");
					break;
				default:
					type = Menu.T("packages", "type_other", "Other");
					break;
			}

			return Menu.T("packages", "install_name", "Package Name:") + " " + package.Name + "\n" +
			       Menu.T("packages", "install_author", "Package Author:") + " " + package.Author + "\n" +
			       Menu.T("packages", "install_version", "Package Version:") + " " + package.PackageVersion + "\n" +
			       Menu.T("packages", "list_packagetype", "Type") + ": " + type + "\n" +
			       Menu.T("packages", "install_website", "Package Website:") + " " +
			       (string.IsNullOrEmpty(package.Website) ? Menu.T("packages", "selection_none_website", "No website provided.") : package.Website) + "\n\n" +
			       Menu.T("packages", "install_description", "Package Description:") + "\n" + (package.Description ?? string.Empty).Replace("\\r\\n", "\n");
		}

		// --- installing, as buttonInstall_Click ---

		private void BeginInstall()
		{
			if (candidate == null || busy)
			{
				return;
			}

			List<Package> missing = PackageInstaller.MissingDependencies(candidate);
			if (missing != null)
			{
				Confirm(Menu.T("packages", "install_dependancies_unmet_header", "Dependancy Error"),
					Menu.T("packages", "install_dependancies_unmet", "The current package has unmet dependancies.") + "\n\n" + List(missing),
					CheckRecommendations);
				return;
			}

			CheckRecommendations();
		}

		private void CheckRecommendations()
		{
			List<Package> missing = PackageInstaller.MissingRecommendations(candidate);
			if (missing != null)
			{
				Confirm(Menu.T("packages", "install_reccomends_unmet_header", "Recommended Packages"),
					Menu.T("packages", "install_reccomends_unmet", "The following packages are recommended, but not installed:") + "\n\n" + List(missing),
					CheckVersion);
				return;
			}

			CheckVersion();
		}

		private void CheckVersion()
		{
			VersionInformation info = PackageInstaller.CheckVersion(candidate, out Package installed);
			if (info == VersionInformation.NotFound)
			{
				Extract(null);
				return;
			}

			string message;
			switch (info)
			{
				case VersionInformation.NewerVersion:
					message = Menu.T("packages", "install_version_new", "The selected package is already installed, and is a newer version.");
					break;
				case VersionInformation.OlderVersion:
					message = Menu.T("packages", "install_version_old", "The selected package is already installed, and is an older version.");
					break;
				default:
					message = Menu.T("packages", "install_version_same", "The selected package is already installed, and is an identical version.");
					break;
			}

			message += "\n\n" + Menu.T("packages", "version_new", "New version:") + " " + candidate.Package.PackageVersion + "\n" +
			           Menu.T("packages", "version_current", "Current version:") + " " + installed?.PackageVersion;
			Confirm(Menu.T("packages", "install_version_error", "Package Version Error"), message, () => Extract(installed));
		}

		private void Extract(Package replacing)
		{
			InstallCandidate current = candidate;
			ShowBusy(Menu.T("packages", "processing", "Processing, please wait..."));
			progress.Visibility = ViewStates.Visible;
			Task.Run(() => PackageInstaller.Install(current, replacing, (percent, file) => activity.RunOnUiThread(() =>
			{
				progress.Progress = percent;
				progressLabel.Text = file;
			}))).ContinueWith(t => activity.RunOnUiThread(() =>
			{
				EndBusy();
				progress.Visibility = ViewStates.Gone;
				progressLabel.Text = string.Empty;
				if (t.IsFaulted)
				{
					details.SetTextColor(Ui.Danger);
					details.Text = Menu.T("packages", "install_failure_header", "Package Installation Failed") + "\n\n" +
					               Menu.T("packages", "install_failure", "Unfortunately, package installation failed.") + "\n\n" +
					               t.Exception?.InnerException?.Message;
					return;
				}

				string[] files = t.Result.Split('\n');
				details.SetTextColor(Ui.Primary);
				details.Text = Menu.T("packages", "install_success_header", "Installation Successful") + "\n\n" +
				               Menu.T("packages", "install_success", "Package installation was successful.") + "\n\n" +
				               Menu.T("packages", "install_success_files", "A list of files installed is shown below:") + " (" + files.Length + ")\n" +
				               string.Join("\n", files.Take(300)) + (files.Length > 300 ? "\n…" : string.Empty);
				candidate = null;
				SetEnabled(install, false);
				DeleteTemporaryCopy();
				ContentChanged?.Invoke();
			}));
		}

		// --- installed packages and uninstalling ---

		private void RefreshInstalled()
		{
			PackageType type = typeSpinner.SelectedItemPosition == 1 ? PackageType.Train : typeSpinner.SelectedItemPosition == 2 ? PackageType.Other : PackageType.Route;
			shownPackages = PackageInstaller.Installed(type).ToList();
			installedList.Adapter = new ArrayAdapter<string>(activity, global::Android.Resource.Layout.SimpleListItemActivated1,
				shownPackages.Select(p => p.Name + "  " + p.PackageVersion).ToList());
			selectedInstalled = null;
			SetEnabled(uninstall, false);
			installedDetails.Text = shownPackages.Count == 0
				? Menu.T("packages", "replace_noneavailable", "No packages are currently installed.")
				: Menu.T("packages", "selection_none", "No package selected.");
		}

		private void ShowInstalled(int position)
		{
			selectedInstalled = shownPackages[position];
			installedDetails.Text = Describe(selectedInstalled);
			SetEnabled(uninstall, true);
		}

		private void BeginUninstall()
		{
			Package package = selectedInstalled;
			if (package == null || busy)
			{
				return;
			}

			List<Package> broken = PackageInstaller.Dependants(package);
			string message = package.Name + "\n\n" + (broken.Count != 0
				? Menu.T("packages", "uninstall_broken", "Some existing packages may be broken by this action.") + "\n\n" + List(broken)
				: string.Empty);
			Confirm(Menu.T("packages", "uninstall_button", "Uninstall Package"), message, () =>
			{
				ShowBusy(Menu.T("packages", "processing", "Processing, please wait..."));
				bool success = false;
				Task.Run(() => PackageInstaller.Uninstall(package, out success)).ContinueWith(t => activity.RunOnUiThread(() =>
				{
					EndBusy();
					RefreshInstalled();
					installedDetails.Text = t.IsFaulted
						? t.Exception?.InnerException?.Message
						: (success ? Menu.T("packages", "uninstall_success_header", "Uninstallation Successful") : Menu.T("packages", "uninstall_failure_header", "Uninstallation Failed")) +
						  "\n\n" + Menu.T("packages", "uninstall_log", "A log is shown below:") + "\n" + t.Result;
					ContentChanged?.Invoke();
				}));
			}, broken.Count != 0);
		}

		// --- helpers ---

		private void Confirm(string title, string message, Action proceed, bool warning = true)
		{
			new AlertDialog.Builder(activity)
				.SetTitle(title)
				.SetMessage(message)
				.SetPositiveButton(warning ? Menu.T("packages", "proceed_anyway", "Proceed Anyway") : Menu.T("packages", "button_ok", "OK"), (s, e) => proceed())
				.SetNegativeButton(Menu.T("packages", "button_cancel", "Cancel"), (s, e) => { })
				.Show();
		}

		private static string List(IEnumerable<Package> packages) =>
			string.Join("\n", packages.Select(p => "• " + p.Name + (p.MinimumVersion != null ? " ≥ " + p.MinimumVersion : string.Empty)));

		private void ShowBusy(string text)
		{
			busy = true;
			progressLabel.Text = text;
		}

		private void EndBusy()
		{
			busy = false;
			progressLabel.Text = string.Empty;
		}

		private static void SetEnabled(Button button, bool enabled)
		{
			button.Enabled = enabled;
			button.Alpha = enabled ? 1.0f : 0.45f;
		}

		private void DeleteTemporaryCopy()
		{
			if (temporaryCopy != null)
			{
				try
				{
					File.Delete(temporaryCopy);
				}
				catch (Exception)
				{
					// The cache is cleared by the system in any case.
				}

				temporaryCopy = null;
			}
		}
	}
}
