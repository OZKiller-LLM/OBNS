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
using System.Reflection;
using Android.Util;
using OpenBveApi;
using OpenBveApi.Hosts;
using OpenBveApi.Objects;
using OpenBveApi.Routes;
using OpenBveApi.Sounds;
using OpenBveApi.Textures;
using OpenBveApi.Trains;

namespace OpenBve.Android
{
	/// <summary>
	/// Registers OpenBVE's content loading plugins.
	/// </summary>
	/// <remarks>
	/// Upstream discovers plugins by scanning a Plugins folder for assemblies and reflecting over
	/// each one (<see cref="HostInterface.LoadPlugins"/>). Inside an APK there is no such folder —
	/// the plugins are ordinary assemblies packaged with the app — so they are named explicitly
	/// here and loaded by assembly name. The reflection over each assembly's types, and the call
	/// into <see cref="ContentLoadingPlugin.Load"/>, match what upstream does.
	///
	/// Because nothing references these assemblies statically, the managed linker must not trim
	/// them: release builds need them kept (see PORTING.md).
	/// </remarks>
	public static class AndroidPlugins
	{
		private const string Tag = "OpenBVE";

		/// <summary>Every content plugin packaged with the app.</summary>
		private static readonly string[] PluginAssemblies =
		{
			// Routes
			"Route.CsvRw", "Route.Bve5", "Route.Mechanik",
			// Objects
			"Object.CsvB3d", "Object.Animated", "Object.DirectX", "Object.Wavefront", "Object.LokSim", "Object.Msts",
			// Textures
			"Texture.BmpGifJpegPngTiff", "Texture.Tga", "Texture.Dds", "Texture.Ace",
			// Sounds
			"Sound.RiffWave", "Sound.Vorbis", "Sound.MP3", "Sound.Flac",
			// Trains
			"Train.OpenBve", "Train.MsTs"
		};

		/// <summary>Loads and registers the content plugins with the host.</summary>
		/// <returns>A one line summary of what was registered.</returns>
		public static string Register(HostInterface host, OpenBveApi.FileSystem.FileSystem fileSystem, BaseOptions options,
			object trainManagerReference, object rendererReference)
		{
			List<ContentLoadingPlugin> plugins = new List<ContentLoadingPlugin>();

			foreach (string name in PluginAssemblies)
			{
				try
				{
					Assembly assembly = Assembly.Load(name);
					ContentLoadingPlugin plugin = Create(assembly, name);
					if (plugin == null)
					{
						Log.Warn(Tag, "plugin " + name + " implements no content loading interface");
						continue;
					}

					plugin.Load(host, fileSystem, options, trainManagerReference, rendererReference);
					plugins.Add(plugin);
				}
				catch (Exception ex)
				{
					Log.Error(Tag, "plugin " + name + " failed to load: " + ex.Message);
				}
			}

			host.Plugins = plugins.ToArray();

			string summary = plugins.Count + " plugins: " + host.AvailableRoutePluginCount + " route, " +
			                 host.AvailableObjectPluginCount + " object, " + host.AvailableSoundPluginCount + " sound";
			Log.Info(Tag, summary);
			return summary;
		}

		private static ContentLoadingPlugin Create(Assembly assembly, string name)
		{
			ContentLoadingPlugin plugin = new ContentLoadingPlugin(name);

			foreach (Type type in assembly.GetTypes())
			{
				if (type.FullName == null || type.IsAbstract)
				{
					continue;
				}

				if (type.IsSubclassOf(typeof(TextureInterface)))
				{
					plugin.Texture = (TextureInterface)assembly.CreateInstance(type.FullName);
				}
				else if (type.IsSubclassOf(typeof(SoundInterface)))
				{
					plugin.Sound = (SoundInterface)assembly.CreateInstance(type.FullName);
				}
				else if (type.IsSubclassOf(typeof(ObjectInterface)))
				{
					plugin.Object = (ObjectInterface)assembly.CreateInstance(type.FullName);
				}
				else if (type.IsSubclassOf(typeof(RouteInterface)))
				{
					plugin.Route = (RouteInterface)assembly.CreateInstance(type.FullName);
				}
				else if (type.IsSubclassOf(typeof(TrainInterface)))
				{
					plugin.Train = (TrainInterface)assembly.CreateInstance(type.FullName);
				}
			}

			bool any = plugin.Texture != null || plugin.Sound != null || plugin.Object != null ||
			           plugin.Route != null || plugin.Train != null;
			return any ? plugin : null;
		}
	}
}
