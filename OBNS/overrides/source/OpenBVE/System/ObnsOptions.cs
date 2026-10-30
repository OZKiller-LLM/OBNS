// OBNS: not an upstream file. OBNS's own options (OBNS/VULKAN.md), kept in Settings/1.5.0/obns.cfg beside upstream's
// options.cfg, so upstream's options file and code stay as they are.

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenBve
{
	internal static class ObnsOptions
	{
		/// <summary>What draws the world layer.</summary>
		internal enum RendererKind
		{
			/// <summary>OpenGL, as upstream.</summary>
			OpenGL,
			/// <summary>Vulkan where the computer can (OpenGL draws the rest of the frame, and the world layer where Vulkan cannot).</summary>
			Vulkan,
			/// <summary>Direct3D 11 where the computer can (64-bit Windows; else as Vulkan).</summary>
			Direct3D11,
			/// <summary>Direct3D 12 where the computer can (64-bit Windows; else as Direct3D 11).</summary>
			Direct3D12,
			/// <summary>Direct3D 9.0c where the computer can (64-bit Windows; else as Vulkan).</summary>
			Direct3D9,
			/// <summary>Metal where the computer can (macOS; else OpenGL).</summary>
			Metal
		}

		private static bool loaded;
		private static RendererKind renderer = RendererKind.Vulkan;
		/// <summary>Whether Vulkan was picked in the options, rather than being the default (the file says "renderer = vulkan" for both).</summary>
		private static bool vulkanChosen;
		private static bool? oldIntelAlone;

		/// <summary>The renderer in force (taking effect when a game starts): the one chosen in the options, else the default.</summary>
		internal static RendererKind Renderer
		{
			get
			{
				Load();
				return OpenGLByDefault ? RendererKind.OpenGL : renderer;
			}
			set
			{
				Load();
				// The menu gives back what it was shown when nothing was changed: that is no choice.
				if (value != Renderer)
				{
					renderer = value;
					vulkanChosen = value == RendererKind.Vulkan;
				}
			}
		}

		/// <summary>OpenGL by default on Linux on Intel's integrated GPUs from Broadwell back with no discrete GPU; Vulkan by default everywhere else.</summary>
		internal static bool OpenGLByDefault
		{
			get
			{
				Load();
				if (renderer != RendererKind.Vulkan || vulkanChosen || Program.CurrentHost.Platform != OpenBveApi.Hosts.HostPlatform.GNULinux)
				{
					return false;
				}

				if (oldIntelAlone == null)
				{
					oldIntelAlone = Graphics.Vulkan.VulkanDevice.OldIntelWithoutDiscreteGpu();
				}

				return oldIntelAlone.Value;
			}
		}

		private static string FileName => OpenBveApi.Path.CombineFile(Program.FileSystem.SettingsFolder, "1.5.0/obns.cfg");

		private static void Load()
		{
			if (loaded)
			{
				return;
			}

			loaded = true;
			try
			{
				if (!File.Exists(FileName))
				{
					return;
				}

				foreach (string line in File.ReadAllLines(FileName, Encoding.UTF8))
				{
					int equals = line.IndexOf('=');
					if (equals < 0 || line.TrimStart().StartsWith(";"))
					{
						continue;
					}

					string key = line.Substring(0, equals).Trim().ToLowerInvariant(), value = line.Substring(equals + 1).Trim().ToLowerInvariant();
					if (key == "renderer")
					{
						renderer = value == "opengl" ? RendererKind.OpenGL : value == "direct3d11" ? RendererKind.Direct3D11 : value == "direct3d12" ? RendererKind.Direct3D12 : value == "direct3d9" ? RendererKind.Direct3D9 : value == "metal" ? RendererKind.Metal : RendererKind.Vulkan;
					}
					else if (key == "vulkanchosen")
					{
						vulkanChosen = value == "true";
					}
				}
			}
			catch (Exception ex)
			{
				Program.FileSystem.AppendToLogFile("OBNS options not read: " + ex.Message);
			}
		}

		internal static void Save()
		{
			try
			{
				StringBuilder builder = new StringBuilder();
				builder.AppendLine("; OBNS options (OpenBVE's are in options.cfg)");
				Load();
				builder.AppendLine("renderer = " + (renderer == RendererKind.OpenGL ? "opengl" : renderer == RendererKind.Direct3D11 ? "direct3d11" : renderer == RendererKind.Direct3D12 ? "direct3d12" : renderer == RendererKind.Direct3D9 ? "direct3d9" : renderer == RendererKind.Metal ? "metal" : "vulkan"));
				if (renderer == RendererKind.Vulkan && vulkanChosen)
				{
					builder.AppendLine("vulkanchosen = true");
				}

				Directory.CreateDirectory(Path.GetDirectoryName(FileName));
				File.WriteAllText(FileName, builder.ToString(), new UTF8Encoding(false));
			}
			catch (Exception ex)
			{
				Program.FileSystem.AppendToLogFile("OBNS options not saved: " + ex.Message);
			}
		}
	}
}
