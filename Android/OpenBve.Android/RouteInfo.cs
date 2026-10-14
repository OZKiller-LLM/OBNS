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
using System.Linq;
using System.Text;
using Android.Util;
using OpenBveApi;
using OpenBveApi.Routes;
using OpenTK;
using RouteManager2;
using TrainManager;
using Path = OpenBveApi.Path;

namespace OpenBve.Android
{
	/// <summary>What the Start page shows about a route.</summary>
	public class RoutePreview
	{
		public string File;
		public string Description;
		public string ImageFile;
		/// <summary>The train the route suggests (its Train.Folder), or null.</summary>
		public string DefaultTrainName;
		/// <summary>Where that train was found, or null if it is not installed.</summary>
		public string DefaultTrainFolder;
		public Encoding Encoding;
		public string Error;
	}

	/// <summary>What the Start page shows about a train.</summary>
	public class TrainPreview
	{
		public string Folder;
		public string Description;
		public string ImageFile;
		public Encoding Encoding;
		public string Error;
	}

	/// <summary>
	/// The Start page's knowledge of content: which files are routes, which folders are trains,
	/// and what each one says about itself - the logic of upstream's formMain.Start, driven through
	/// the same content plugins the game uses.
	/// </summary>
	/// <remarks>
	/// Upstream previews a route by parsing it with the route plugin in preview mode, which reads
	/// the description, the image and the suggested train without building any objects. The menu
	/// hosts the plugins for that without a GL context: nothing in a preview load draws.
	/// Previews are not thread safe (the plugins keep static state), so callers serialise them.
	/// </remarks>
	public static class RouteInfo
	{
		private const string Tag = "OpenBVE";
		private static readonly object Sync = new object();
		private static AndroidOptions options;
		private static AndroidRenderer renderer;

		/// <summary>Registers the content plugins with the menu's host, once.</summary>
		private static void EnsurePlugins()
		{
			if (options != null)
			{
				return;
			}

			AndroidHost host = Menu.Host;
			options = new AndroidOptions();
			if (DisplayDevice.Default == null)
			{
				DisplayDevice.Default = new DisplayDevice(1920, 1080, 1.0f, 1.0f);
			}

			renderer = new AndroidRenderer(host, options, Menu.FileSystem);
			host.Renderer = renderer;
			host.Options = options;
			AndroidTrainManager trainManager = new AndroidTrainManager(host, renderer, options, Menu.FileSystem);
			AndroidPlugins.Register(host, Menu.FileSystem, options, trainManager, renderer);
		}

		// --- listing ---

		/// <summary>Whether a file should appear in the route list, by upstream's rules.</summary>
		public static bool IsRouteFile(string file)
		{
			string name = System.IO.Path.GetFileName(file);
			if (string.IsNullOrEmpty(name) || name[0] == '.')
			{
				return false;
			}

			switch (System.IO.Path.GetExtension(file).ToLowerInvariant())
			{
				case ".rw":
				case ".csv":
					// Upstream lists every .csv, marking only the ones that look like routes; on a
					// small screen only those are worth listing.
					return LooksLikeCsvRoute(file) || file.EndsWith(".rw", StringComparison.OrdinalIgnoreCase);
				case ".dat":
					return !Path.IsInvalidDatName(file) && PluginAccepts(file);
				case ".txt":
					return !Path.IsInvalidTxtName(file) && PluginAccepts(file);
				default:
					return false;
			}
		}

		private static bool LooksLikeCsvRoute(string file)
		{
			try
			{
				using (StreamReader reader = new StreamReader(file, Encoding.UTF8))
				{
					string text = reader.ReadToEnd();
					return text.IndexOf("With Track", StringComparison.OrdinalIgnoreCase) >= 0 |
					       text.IndexOf("Track.", StringComparison.OrdinalIgnoreCase) >= 0 |
					       text.IndexOf("$Include", StringComparison.OrdinalIgnoreCase) >= 0;
				}
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool PluginAccepts(string file)
		{
			lock (Sync)
			{
				EnsurePlugins();
				foreach (ContentLoadingPlugin plugin in Menu.Host.Plugins)
				{
					try
					{
						if (plugin.Route != null && plugin.Route.CanLoadRoute(file))
						{
							return true;
						}
					}
					catch (Exception)
					{
						// A plugin that cannot tell is not accepting it.
					}
				}

				return false;
			}
		}

		/// <summary>Whether a folder is a train, by upstream's rule (it holds train.dat or train.xml).</summary>
		public static bool IsTrainFolder(string folder)
		{
			return File.Exists(Path.CombineFile(folder, "train.dat")) || File.Exists(Path.CombineFile(folder, "train.xml"));
		}

		// --- previews ---

		/// <summary>Reads a route's description, image and suggested train. Call off the UI thread.</summary>
		public static RoutePreview PreviewRoute(string file)
		{
			RoutePreview preview = new RoutePreview { File = file };
			lock (Sync)
			{
				try
				{
					EnsurePlugins();
					AndroidHost host = Menu.Host;
					RouteInterface routeInterface = host.Plugins.Select(x => x.Route).FirstOrDefault(x => x != null && x.CanLoadRoute(file));
					if (routeInterface == null)
					{
						throw new Exception($"No plugins capable of loading route file {file} were found.");
					}

					preview.Encoding = TextEncoding.GetSystemEncodingFromFile(file);
					options.TrainName = string.Empty;

					CurrentRoute currentRoute = new CurrentRoute(host, renderer);
					host.Route = currentRoute;
					TrainManagerBase.CurrentRoute = currentRoute;
					object route = currentRoute;
					string railwayFolder = Menu.FileSystem.GetRailwayFolder(file, global::System.Windows.Forms.Application.StartupPath);
					string objectFolder = Path.CombineDirectory(railwayFolder, "Object");
					string soundFolder = Path.CombineDirectory(railwayFolder, "Sound");

					if (!routeInterface.LoadRoute(file, preview.Encoding, null, objectFolder, soundFolder, true, ref route))
					{
						throw routeInterface.LastException ?? new Exception($"An unknown error was encountered whilst attempting to parser the route file {file}");
					}

					currentRoute = (CurrentRoute)route;
					string comment = currentRoute.Comment ?? string.Empty;
					preview.Description = comment.Length != 0 ? comment : System.IO.Path.GetFileNameWithoutExtension(file);
					preview.ImageFile = !string.IsNullOrEmpty(currentRoute.Image) && File.Exists(currentRoute.Image)
						? currentRoute.Image
						: FindImageBesideRoute(file);
					preview.DefaultTrainName = string.IsNullOrEmpty(options.TrainName) ? null : options.TrainName;
					preview.DefaultTrainFolder = GetDefaultTrainFolder(file, options.TrainName);
				}
				catch (Exception ex)
				{
					Log.Warn(Tag, "route preview failed for " + file + ": " + ex.Message);
					preview.Error = ex.Message;
				}
			}

			return preview;
		}

		/// <summary>Reads a train's description and image through its plugin. Call off the UI thread.</summary>
		public static TrainPreview PreviewTrain(string folder)
		{
			TrainPreview preview = new TrainPreview { Folder = folder };
			lock (Sync)
			{
				try
				{
					EnsurePlugins();
					ContentLoadingPlugin plugin = Menu.Host.Plugins.FirstOrDefault(p => p.Train != null && p.Train.CanLoadTrain(folder));
					if (plugin == null)
					{
						preview.Error = "No plugin is capable of loading this train.";
						return preview;
					}

					// As upstream: the encoding of the description file, whichever of these exists.
					string descriptionFile = Path.CombineFile(folder, "train.txt");
					if (!File.Exists(descriptionFile))
					{
						descriptionFile = Path.CombineFile(folder, "readme.txt");
					}

					if (!File.Exists(descriptionFile))
					{
						descriptionFile = Path.CombineFile(folder, "read me.txt");
					}

					preview.Encoding = TextEncoding.GetSystemEncodingFromFile(descriptionFile);
					preview.Description = plugin.Train.GetDescription(folder, preview.Encoding);
					string image = plugin.Train.GetImage(folder);
					preview.ImageFile = !string.IsNullOrEmpty(image) && File.Exists(image) ? image : null;
				}
				catch (Exception ex)
				{
					preview.Error = ex.Message;
				}
			}

			return preview;
		}

		/// <summary>An image beside the route file with the same name, as upstream looks for when the route names none.</summary>
		public static string FindImageBesideRoute(string routeFile)
		{
			if (string.IsNullOrEmpty(routeFile))
			{
				return null;
			}

			string[] extensions = { ".png", ".bmp", ".gif", ".tiff", ".tif", ".jpeg", ".jpg" };
			try
			{
				string folder = System.IO.Path.GetDirectoryName(routeFile);
				string name = System.IO.Path.GetFileNameWithoutExtension(routeFile);
				foreach (string extension in extensions)
				{
					string candidate = Path.CombineFile(folder, name + extension);
					if (File.Exists(candidate))
					{
						return candidate;
					}
				}
			}
			catch (Exception)
			{
				// Invalid path.
			}

			return null;
		}

		/// <summary>
		/// Finds the train a route suggests: a port of upstream's Loading.GetDefaultTrainFolder,
		/// which walks up from the route file looking for a Train folder holding a train of that name.
		/// </summary>
		public static string GetDefaultTrainFolder(string routeFile, string trainName)
		{
			if (string.IsNullOrEmpty(routeFile) || string.IsNullOrEmpty(trainName))
			{
				return null;
			}

			string currentFolder;
			try
			{
				currentFolder = System.IO.Path.GetDirectoryName(routeFile);
				if (trainName[0] == '$')
				{
					currentFolder = Path.CombineDirectory(currentFolder, trainName);
					if (Directory.Exists(currentFolder) && File.Exists(Path.CombineFile(currentFolder, "train.dat")))
					{
						return currentFolder;
					}
				}
			}
			catch
			{
				currentFolder = null;
			}

			bool recursionTest = false;
			string lastFolder = null;
			try
			{
				while (true)
				{
					string trainFolder = Path.CombineDirectory(currentFolder, "Train");
					string oldFolder = currentFolder;
					if (Directory.Exists(trainFolder))
					{
						try
						{
							currentFolder = Path.CombineDirectory(trainFolder, trainName);
						}
						catch (Exception ex)
						{
							if (ex is ArgumentException)
							{
								break; // Invalid character in path causes infinite recursion
							}

							currentFolder = null;
						}

						if (currentFolder != null)
						{
							char c = System.IO.Path.DirectorySeparatorChar;
							if (Directory.Exists(currentFolder))
							{
								if (File.Exists(Path.CombineFile(currentFolder, "train.dat")))
								{
									return currentFolder;
								}

								if (lastFolder == currentFolder || recursionTest)
								{
									break;
								}

								lastFolder = currentFolder;
							}
							else if (currentFolder.ToLowerInvariant().Contains(c + "railway" + c))
							{
								// A misplaced Train folder inside Railway: carry on upwards.
								recursionTest = true;
								currentFolder = oldFolder;
							}
							else
							{
								break;
							}
						}
					}

					if (currentFolder == null)
					{
						continue;
					}

					DirectoryInfo directoryInfo = Directory.GetParent(currentFolder);
					if (directoryInfo != null)
					{
						currentFolder = directoryInfo.FullName;
					}
					else
					{
						break;
					}
				}
			}
			catch
			{
				// Not found.
			}

			// Beyond upstream: the app's own train folder, where the menu installs trains.
			try
			{
				string installed = Path.CombineDirectory(Menu.FileSystem.TrainInstallationDirectory, trainName);
				if (Directory.Exists(installed) && IsTrainFolder(installed))
				{
					return installed;
				}
			}
			catch
			{
				// Invalid name.
			}

			return null;
		}
	}
}
