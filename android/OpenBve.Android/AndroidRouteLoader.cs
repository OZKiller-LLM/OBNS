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
using LibRender2;
using OpenBveApi;
using OpenBveApi.Hosts;
using RouteManager2;
using Path = System.IO.Path;

namespace OpenBve.Android
{
	/// <summary>
	/// Finds route content on the device and loads it through the registered route plugins.
	/// </summary>
	/// <remarks>
	/// This is the first end to end exercise of the ported content stack: plugin dispatch, the
	/// route parser, the object and texture plugins it calls into, object registration with the
	/// renderer, and file path handling on a case-sensitive file system.
	/// </remarks>
	public static class AndroidRouteLoader
	{
		private const string Tag = "OpenBVE";

		private static readonly string[] RouteExtensions = { ".csv", ".rw", ".txt", ".dat" };

		/// <summary>Loads a route and builds its scene.</summary>
		/// <param name="file">The route file chosen in the menu, or null for the first route under the content folder.</param>
		/// <param name="encoding">The route's text encoding, or null to detect it from the file as upstream does.</param>
		/// <param name="route">Receives the built route, or null if none loaded.</param>
		/// <returns>A one line summary for the status display.</returns>
		public static string LoadRoute(AndroidHost host, OpenBveApi.FileSystem.FileSystem fileSystem,
			BaseRenderer renderer, string file, Encoding encoding, out CurrentRoute route)
		{
			route = null;
			string routeFolder = fileSystem.InitialRouteFolder;
			file = string.IsNullOrEmpty(file) ? FindRoute(routeFolder) : file;
			if (file == null)
			{
				return "no routes found - copy a route folder into " + routeFolder;
			}

			if (!File.Exists(file))
			{
				return "the route file " + file + " no longer exists";
			}

			encoding = encoding ?? TextEncoding.GetSystemEncodingFromFile(file);

			ContentLoadingPlugin plugin = host.Plugins.FirstOrDefault(p => p.Route != null && SafeCanLoad(p, file));
			if (plugin == null)
			{
				return "no route plugin accepted " + Path.GetFileName(file);
			}

			Log.Info(Tag, "loading " + file + " with " + plugin.Title);

			/*
			 * Routes reference objects and sounds relative to the Railway folder's Object and Sound
			 * folders, not relative to the route file - and route files are often nested below
			 * Railway/Route (this route is at Railway/Route/EAL2023/...). Upstream finds the Railway
			 * folder by walking up from the route file; use the same method, which OpenBveApi already
			 * provides, rather than assuming a depth.
			 */
			string railwayFolder = fileSystem.GetRailwayFolder(file, global::System.Windows.Forms.Application.StartupPath);
			string objectFolder = OpenBveApi.Path.CombineDirectory(railwayFolder, "Object");
			string soundFolder = OpenBveApi.Path.CombineDirectory(railwayFolder, "Sound");
			Log.Info(Tag, "railway folder: " + railwayFolder);

			/*
			 * The route plugins cast this straight to CurrentRoute and populate it rather than
			 * creating it themselves. The host needs it too, before parsing starts: object
			 * creation during the parse reads its disposal mode and block length.
			 */
			CurrentRoute currentRoute = new CurrentRoute(host, renderer);
			host.Route = currentRoute;
			// Upstream sets this once at startup; the train and signal code reads it statically.
			global::TrainManager.TrainManagerBase.CurrentRoute = currentRoute;
			/*
			 * The animation evaluator reads the route too (signal aspects, station states), and
			 * signals are first evaluated while the route loads. A function that throws is
			 * disabled for good, so this has to be in place before the parse, not after it.
			 */
			Program.CurrentRoute = currentRoute;
			object routeObject = currentRoute;

			DateTime started = DateTime.UtcNow;
			try
			{
				// A full load, not a preview: objects are created and registered with the renderer.
				LoadProgress.Route = plugin.Route;
				bool loaded = plugin.Route.LoadRoute(file, encoding, null, objectFolder, soundFolder, false, ref routeObject);
				double seconds = (DateTime.UtcNow - started).TotalSeconds;

				if (!loaded || routeObject == null)
				{
					// The plugins catch parse failures and stash the exception rather than throwing.
					Exception cause = plugin.Route.LastException;
					if (cause != null)
					{
						Log.Error(Tag, "route load failed: " + cause);
						return "route " + Path.GetFileName(file) + " failed: " + cause.GetType().Name + ": " + cause.Message;
					}

					return "route " + Path.GetFileName(file) + " failed to load - see logcat";
				}

				route = (CurrentRoute)routeObject;
				return "loaded " + Path.GetFileName(file) + " in " + seconds.ToString("0.00") + " s: " +
				       renderer.StaticObjectStates.Count + " static objects";
			}
			catch (Exception ex)
			{
				Log.Error(Tag, "route load threw: " + ex);
				return "route " + Path.GetFileName(file) + " threw " + ex.GetType().Name + ": " + ex.Message;
			}
		}

		private static bool SafeCanLoad(ContentLoadingPlugin plugin, string file)
		{
			try
			{
				return plugin.Route.CanLoadRoute(file);
			}
			catch (Exception ex)
			{
				Log.Warn(Tag, plugin.Title + ".CanLoadRoute threw: " + ex.Message);
				return false;
			}
		}

		private static string FindRoute(string folder)
		{
			if (!Directory.Exists(folder))
			{
				return null;
			}

			try
			{
				/*
				 * Real routes keep include fragments (files pulled in with $Include) beside or below
				 * the route files proper, with the same extension. The route files are the shallowest,
				 * so take the least deeply nested candidate, then the first by name.
				 */
				return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
					.Where(f => RouteExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
					.OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
					.ThenBy(f => f, StringComparer.Ordinal)
					.FirstOrDefault();
			}
			catch (Exception ex)
			{
				Log.Warn(Tag, "could not search " + folder + ": " + ex.Message);
				return null;
			}
		}
	}
}
