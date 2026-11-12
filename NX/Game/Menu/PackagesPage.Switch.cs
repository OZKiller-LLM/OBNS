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
using OpenBve.Android;
using OpenBveApi.Packages;

namespace OpenBve.Switch
{
	/// <summary>
	/// Package Management, as Android's PackagesPage for the console, on the same installer (Android's PackageInstaller:
	/// upstream's packages and plain archives alike). Archives put in /switch/OBNS/Packages are listed to install - their
	/// details, dependency and version checks, then the install with its progress - and the installed packages to
	/// uninstall. There is no file picker on the console, so the folder stands in for Android's.
	/// </summary>
	public sealed class PackagesPage
	{
		/// <summary>Where archives to install are put.</summary>
		public static string Folder = "/switch/OBNS/Packages";

		private readonly MenuList list;
		private readonly Action contentChanged;
		private IMenuHost host;
		private InstallCandidate candidate;
		private volatile string status;
		private volatile bool busy, rebuild, contentDirty;

		/// <param name="list">The page's list, filled here.</param>
		/// <param name="contentChanged">After an install or uninstall: the route and train lists read again.</param>
		public PackagesPage(MenuList list, Action contentChanged)
		{
			this.list = list;
			this.contentChanged = contentChanged;
		}

		private static string T(string group, string key, string fallback) => Menu.T(group, key, fallback);

		/// <summary>Each frame: the page rebuilt once a background read, install or uninstall has finished.</summary>
		public void Tick(IMenuHost menuHost)
		{
			host = menuHost;
			if (contentDirty)
			{
				// The route and train lists read again on the menu's thread, not the installer's.
				contentDirty = false;
				contentChanged?.Invoke();
			}

			if (rebuild)
			{
				rebuild = false;
				if (candidate != null)
				{
					ShowCandidate();
				}
				else
				{
					ShowOverview();
				}
			}
		}

		/// <summary>The archives to install and the packages installed.</summary>
		public void ShowOverview()
		{
			candidate = null;
			list.Clear();
			list.Add(new HeadingItem(T("packages", "install_header", "Install a Package")));
			list.Add(new InfoItem(string.Format(T("switch", "packages_folder", "Put .zip, .7z or .rar archives in {0} on the SD card, then choose one here. OpenBVE packages and plain route and train archives are both accepted."), Folder)));
			if (status != null)
			{
				list.Add(new InfoItem(() => status));
			}

			string[] archives = Array.Empty<string>();
			try
			{
				Directory.CreateDirectory(Folder);
				archives = Directory.GetFiles(Folder).Where(f => new[] { ".zip", ".7z", ".rar", ".tar", ".gz" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
					.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
			}
			catch (Exception)
			{
				// No folder: nothing to list.
			}

			if (archives.Length == 0)
			{
				list.Add(new InfoItem(T("switch", "packages_none", "No archives there yet.")));
			}

			foreach (string archive in archives)
			{
				long size = 0;
				try
				{
					size = new FileInfo(archive).Length;
				}
				catch (Exception)
				{
					// Listed without its size.
				}

				string file = archive;
				list.Add(new EntryItem("▣", Path.GetFileName(archive), Size(size), () => Read(file), archive));
			}

			list.Add(new HeadingItem(T("packages", "list_header", "Installed Packages")));
			PackageInstaller.EnsureDatabase();
			bool any = false;
			foreach ((PackageType type, string heading) in new[]
			         {
				         (PackageType.Route, T("packages", "list_routes", "Routes")), (PackageType.Train, T("packages", "list_trains", "Trains")),
				         (PackageType.Other, T("packages", "list_other", "Other"))
			         })
			{
				List<Package> installed = PackageInstaller.Installed(type);
				foreach (Package package in installed)
				{
					any = true;
					Package chosen = package;
					list.Add(new EntryItem("●", package.Name ?? package.GUID, heading + " · " + package.Version + (string.IsNullOrEmpty(package.Author) ? string.Empty : " · " + package.Author),
						() => AskUninstall(chosen)));
				}
			}

			if (!any)
			{
				list.Add(new InfoItem(T("switch", "packages_installed_none", "None installed through Package Management yet.")));
			}

			list.Add(new HeadingItem(T("switch", "content", "Content")));
			list.Add(new InfoItem(() => T("options", "package_route_directory", "Route installation directory:") + "\n" + Menu.FileSystem?.RouteInstallationDirectory));
			list.Add(new InfoItem(() => T("options", "package_train_directory", "Train installation directory:") + "\n" + Menu.FileSystem?.TrainInstallationDirectory));
			list.Add(new ButtonItem(T("switch", "rescan", "Look for new content"), () =>
			{
				contentChanged?.Invoke();
				host?.Notify(T("switch", "rescanned", "The route and train lists are up to date."));
			}));
			list.Focus = -1;
			list.FocusFirst();
		}

		private void Read(string file)
		{
			if (busy)
			{
				return;
			}

			busy = true;
			status = T("packages", "install_reading", "Reading the archive…");
			ShowOverview();
			Task.Run(() =>
			{
				try
				{
					candidate = PackageInstaller.Read(file);
					status = candidate == null ? T("packages", "install_invalid", "This file isn't a package or an archive that can be installed.") : null;
				}
				catch (Exception e)
				{
					candidate = null;
					status = e.Message;
				}

				busy = false;
				rebuild = true;
			});
		}

		/// <summary>The chosen archive: what it is, its checks, and Install.</summary>
		private void ShowCandidate()
		{
			Package package = candidate.Package;
			list.Clear();
			list.Add(new HeadingItem(package.Name ?? Path.GetFileName(candidate.File)));
			List<string> lines = new List<string>();
			if (!string.IsNullOrEmpty(package.Version))
			{
				lines.Add(T("packages", "install_version", "Version:") + " " + package.Version);
			}

			if (!string.IsNullOrEmpty(package.Author))
			{
				lines.Add(T("packages", "install_author", "Author:") + " " + package.Author);
			}

			if (!candidate.IsPackage && !string.IsNullOrEmpty(candidate.Layout))
			{
				lines.Add(candidate.Layout);
			}

			foreach (string line in lines)
			{
				list.Add(new InfoItem(line));
			}

			if (!string.IsNullOrEmpty(package.Description))
			{
				list.Add(new InfoItem(package.Description.Replace("\\r\\n", "\n")));
			}

			// Upstream's checks: dependencies missing, recommendations missing, a version already there.
			List<Package> missing = PackageInstaller.MissingDependencies(candidate);
			if (missing != null && missing.Count > 0)
			{
				list.Add(new InfoItem(T("packages", "install_dependencies_unmet", "Missing dependencies:") + " " + string.Join(", ", missing.Select(p => p.Name))));
			}

			List<Package> recommended = PackageInstaller.MissingRecommendations(candidate);
			if (recommended != null && recommended.Count > 0)
			{
				list.Add(new InfoItem(T("packages", "install_reccomends_unmet", "Recommended, not installed:") + " " + string.Join(", ", recommended.Select(p => p.Name))));
			}

			VersionInformation version = PackageInstaller.CheckVersion(candidate, out Package installed);
			if (installed != null && version != VersionInformation.NotFound)
			{
				string note = version == VersionInformation.NewerVersion ? T("packages", "install_version_new", "A newer version than the one installed.") :
					version == VersionInformation.OlderVersion ? T("packages", "install_version_old", "An older version than the one installed.") :
					T("packages", "install_version_same", "This version is installed already.");
				list.Add(new InfoItem(note + " (" + installed.Version + ")"));
			}

			if (status != null)
			{
				list.Add(new InfoItem(() => status));
			}

			list.Add(new ButtonItem(T("packages", "install_button", "Install"), () => Install(installed), true, () => !busy));
			list.Add(new ButtonItem(T("menu", "back", "Back"), () =>
			{
				status = null;
				ShowOverview();
			}, false, () => !busy));
			list.Focus = -1;
			list.FocusFirst();
		}

		private void Install(Package replacing)
		{
			if (busy || candidate == null)
			{
				return;
			}

			busy = true;
			InstallCandidate installing = candidate;
			status = T("packages", "install_progress", "Installing…");
			ShowCandidate();
			Task.Run(() =>
			{
				// The installer returns the files it wrote, one per line, and throws when it fails.
				try
				{
					string files = PackageInstaller.Install(installing, replacing, (percent, file) => status = T("packages", "install_progress", "Installing…") + " " + percent + "%  " + file);
					int count = string.IsNullOrEmpty(files) ? 0 : files.Split('\n').Count(line => line.Trim().Length != 0);
					status = T("packages", "install_success_header", "Installation Successful") + " (" + count + " " + T("switch", "files", "files") + ")";
				}
				catch (Exception e)
				{
					status = T("packages", "install_failure_header", "Installation Failed") + ": " + e.Message;
				}

				// Back to the overview with the result; the menu's lists read again.
				candidate = null;
				busy = false;
				rebuild = true;
				contentDirty = true;
			});
		}

		private void AskUninstall(Package package)
		{
			if (busy || host == null)
			{
				return;
			}

			List<Package> broken = PackageInstaller.Dependants(package);
			string title = T("packages", "uninstall_button", "Uninstall Package") + ": " + (package.Name ?? package.GUID) +
			               (broken != null && broken.Count > 0 ? "\n" + T("packages", "uninstall_breaks", "Needed by:") + " " + string.Join(", ", broken.Select(p => p.Name)) : string.Empty);
			host.Pick(title, new[] { T("packages", "uninstall_button", "Uninstall Package"), T("menu", "back", "Back") }, 1, choice =>
			{
				if (choice != 0)
				{
					return;
				}

				busy = true;
				status = T("packages", "uninstall_progress", "Uninstalling…");
				ShowOverview();
				Task.Run(() =>
				{
					bool success = false;
					string failure = null;
					try
					{
						PackageInstaller.Uninstall(package, out success);
					}
					catch (Exception e)
					{
						failure = e.Message;
					}

					status = success ? T("packages", "uninstall_success_header", "Uninstallation Successful") :
						T("packages", "uninstall_failure_header", "Uninstallation Failed") + (failure == null ? string.Empty : ": " + failure);
					busy = false;
					rebuild = true;
					contentDirty = true;
				});
			});
		}

		private static string Size(long bytes) =>
			bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" :
			bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.0") + " MB" : (bytes / 1024.0).ToString("0") + " KB";
	}
}
