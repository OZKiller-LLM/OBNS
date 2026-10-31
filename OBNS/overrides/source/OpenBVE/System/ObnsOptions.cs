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
		/// <summary>Whether the renderer was picked in the options, rather than being the computer's default.</summary>
		private static bool chosen;
		private static RendererKind? defaultRenderer;
		private static RendererKind? lastDrawn;
		private static string lastNote;

		/// <summary>The renderer in force (taking effect when a game starts): the one chosen in the options, else the default.</summary>
		internal static RendererKind Renderer
		{
			get
			{
				Load();
				return chosen ? renderer : DefaultRenderer;
			}
			set
			{
				Load();
				renderer = value;
				chosen = true;
			}
		}

		/// <summary>Whether the renderer was picked in the options.</summary>
		internal static bool Chosen
		{
			get
			{
				Load();
				return chosen;
			}
		}

		/// <summary>
		/// The default (OBNS/GPU_SUPPORT.md): Metal on Apple silicon, else OpenGL on macOS; on Linux OpenGL on GPUs of Broadwell's
		/// age or older with no discrete GPU, else Vulkan; Vulkan on Windows.
		/// </summary>
		private static RendererKind DefaultRenderer
		{
			get
			{
				if (defaultRenderer == null)
				{
					switch (Program.CurrentHost.Platform)
					{
						case OpenBveApi.Hosts.HostPlatform.AppleOSX:
							defaultRenderer = Graphics.Metal.MetalDevice.AppleSilicon() ? RendererKind.Metal : RendererKind.OpenGL;
							break;
						case OpenBveApi.Hosts.HostPlatform.GNULinux:
							defaultRenderer = Graphics.Vulkan.VulkanDevice.OldIntegratedGpuAlone() ? RendererKind.OpenGL : RendererKind.Vulkan;
							break;
						default:
							defaultRenderer = RendererKind.Vulkan;
							break;
					}
				}

				return defaultRenderer.Value;
			}
		}

		/// <summary>
		/// Whether OpenGL is the default given the GPU OpenGL is on, which only a game's context can tell: on Linux a GPU of
		/// Broadwell's age or older, even with a discrete GPU beside it (a hybrid laptop not started on that GPU).
		/// </summary>
		internal static bool OpenGLByDefaultOn(string glRenderer)
		{
			Load();
			return !chosen && Program.CurrentHost.Platform == OpenBveApi.Hosts.HostPlatform.GNULinux && Graphics.Vulkan.VulkanDevice.OldIntegratedGpu(glRenderer);
		}

		/// <summary>What the options show: the choice, else what drew the last game, else the default.</summary>
		internal static RendererKind Shown
		{
			get
			{
				Load();
				return chosen ? renderer : lastDrawn ?? DefaultRenderer;
			}
		}

		/// <summary>The line for the options on what drew the last game and why, if it isn't what was asked for; null before the first game.</summary>
		internal static string LastDrawnText
		{
			get
			{
				Load();
				return lastDrawn == null ? null : "Last game: " + Name(lastDrawn.Value) + (string.IsNullOrEmpty(lastNote) ? "" : ". " + lastNote);
			}
		}

		/// <summary>Keeps what is drawing the world now, and why if it isn't what was asked for, for the options.</summary>
		internal static void SetLastDrawn(RendererKind drawn, string note)
		{
			Load();
			lastDrawn = drawn;
			lastNote = note;
			Save();
		}

		private static string Name(RendererKind kind)
		{
			switch (kind)
			{
				case RendererKind.Vulkan: return "Vulkan";
				case RendererKind.Direct3D9: return "Direct3D 9.0c";
				case RendererKind.Direct3D11: return "Direct3D 11";
				case RendererKind.Direct3D12: return "Direct3D 12";
				case RendererKind.Metal: return "Metal";
				default: return "OpenGL";
			}
		}

		private static RendererKind Parse(string value)
		{
			return value == "opengl" ? RendererKind.OpenGL : value == "direct3d11" ? RendererKind.Direct3D11 : value == "direct3d12" ? RendererKind.Direct3D12 : value == "direct3d9" ? RendererKind.Direct3D9 : value == "metal" ? RendererKind.Metal : RendererKind.Vulkan;
		}

		private static string Key(RendererKind kind)
		{
			return kind == RendererKind.OpenGL ? "opengl" : kind == RendererKind.Direct3D11 ? "direct3d11" : kind == RendererKind.Direct3D12 ? "direct3d12" : kind == RendererKind.Direct3D9 ? "direct3d9" : kind == RendererKind.Metal ? "metal" : "vulkan";
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

					string key = line.Substring(0, equals).Trim().ToLowerInvariant(), raw = line.Substring(equals + 1).Trim(), value = raw.ToLowerInvariant();
					if (key == "renderer")
					{
						renderer = Parse(value);
					}
					else if (key == "chosen" || key == "vulkanchosen")
					{
						chosen = value == "true";
					}
					else if (key == "lastdrawn")
					{
						lastDrawn = Parse(value);
					}
					else if (key == "lastnote")
					{
						lastNote = raw;
					}
				}

				// Files from before "chosen": only Vulkan was the default, but for macOS's menu, which saved OpenGL unasked.
				if (renderer != RendererKind.Vulkan && !(renderer == RendererKind.OpenGL && Program.CurrentHost.Platform == OpenBveApi.Hosts.HostPlatform.AppleOSX))
				{
					chosen = true;
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
				builder.AppendLine("renderer = " + Key(renderer));
				if (chosen)
				{
					builder.AppendLine("chosen = true");
				}

				if (lastDrawn != null)
				{
					builder.AppendLine("lastdrawn = " + Key(lastDrawn.Value));
					if (!string.IsNullOrEmpty(lastNote))
					{
						builder.AppendLine("lastnote = " + lastNote);
					}
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
