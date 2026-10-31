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

// OBNS: not an upstream file. OBNS's rows in the options (OBNS/VULKAN.md): the renderer, added below the display
// group's own rows when the form loads (the groups below are moved down to make room), saved with the options.

using System.Drawing;
using System.Windows.Forms;

namespace OpenBve
{
	internal partial class formMain
	{
		private Label labelObnsRenderer;
		private ComboBox comboboxObnsRenderer;
		private Label labelObnsRendererDrawn;
		private ObnsOptions.RendererKind obnsRendererShown;

		/// <summary>The renderer each of the list's items chooses.</summary>
		private readonly System.Collections.Generic.List<ObnsOptions.RendererKind> obnsRendererKinds = new System.Collections.Generic.List<ObnsOptions.RendererKind>();

		/// <summary>Adds the renderer choice to the display group, showing the saved one.</summary>
		private void ObnsAddOptions()
		{
			if (comboboxObnsRenderer != null)
			{
				return;
			}

			// A row as VSync's, below everything in the group.
			int bottom = 0;
			foreach (Control control in groupboxDisplayMode.Controls)
			{
				bottom = System.Math.Max(bottom, control.Bottom);
			}

			const int rowHeight = 26, statusHeight = 32;
			int top = bottom + 6;
			labelObnsRenderer = new Label
			{
				Anchor = labelVSync.Anchor,
				AutoEllipsis = true,
				Location = new Point(labelVSync.Left, top),
				Size = labelVSync.Size,
				Text = "Renderer:",
				TextAlign = ContentAlignment.TopRight
			};
			comboboxObnsRenderer = new ComboBox
			{
				Anchor = comboboxVSync.Anchor,
				DropDownStyle = ComboBoxStyle.DropDownList,
				Location = new Point(comboboxVSync.Left, top),
				Size = comboboxVSync.Size
			};
			ObnsAddRenderer("OpenGL", ObnsOptions.RendererKind.OpenGL);
			ToolTip tip = new ToolTip();
			if (System.Environment.OSVersion.Platform == System.PlatformID.Win32NT)
			{
				ObnsAddRenderer("Vulkan", ObnsOptions.RendererKind.Vulkan);
				// Direct3D 9.0c, 11 and 12 in 64-bit Windows programs (x86-64 and ARM64; OBNS/DIRECT3D.md).
				if (System.Environment.Is64BitProcess)
				{
					ObnsAddRenderer("Direct3D 9.0c", ObnsOptions.RendererKind.Direct3D9);
					ObnsAddRenderer("Direct3D 11", ObnsOptions.RendererKind.Direct3D11);
					ObnsAddRenderer("Direct3D 12", ObnsOptions.RendererKind.Direct3D12);
				}

				// A renderer not listed here (Direct3D in a 32-bit program) is drawn as Vulkan.
				int chosen = obnsRendererKinds.IndexOf(ObnsOptions.Shown);
				comboboxObnsRenderer.SelectedIndex = chosen >= 0 ? chosen : 1;

				tip.SetToolTip(comboboxObnsRenderer, "Vulkan or Direct3D draws the world (scenery and trains) where the graphics card can share it with OpenGL, which draws the rest; OpenGL draws everything otherwise. Takes effect when a game starts.");
			}
			else if (Program.CurrentHost.Platform == OpenBveApi.Hosts.HostPlatform.AppleOSX)
			{
				// Metal on macOS, on Apple's GPUs (OBNS/METAL.md).
				if (Graphics.Metal.MetalDevice.AppleSilicon())
				{
					ObnsAddRenderer("Metal", ObnsOptions.RendererKind.Metal);
					tip.SetToolTip(comboboxObnsRenderer, "Metal draws the world (scenery and trains), sharing it with OpenGL, which draws the rest; OpenGL draws everything otherwise. Takes effect when a game starts.");
				}
				else
				{
					tip.SetToolTip(comboboxObnsRenderer, "OpenGL draws everything: Metal is not supported yet on Intel's and AMD's GPUs.");
				}

				comboboxObnsRenderer.SelectedIndex = System.Math.Max(obnsRendererKinds.IndexOf(ObnsOptions.Shown), 0);
			}
			else
			{
				// Linux and the like: Vulkan where the driver can share images with OpenGL (OBNS/VULKAN.md).
				ObnsAddRenderer("Vulkan", ObnsOptions.RendererKind.Vulkan);
				int chosen = obnsRendererKinds.IndexOf(ObnsOptions.Shown);
				comboboxObnsRenderer.SelectedIndex = chosen >= 0 ? chosen : 1;
				tip.SetToolTip(comboboxObnsRenderer, "Vulkan draws the world (scenery and trains) where the graphics card can share it with OpenGL, which draws the rest; OpenGL draws everything otherwise. Takes effect when a game starts.");
			}

			// What was shown: only a change of it is a choice.
			obnsRendererShown = obnsRendererKinds[comboboxObnsRenderer.SelectedIndex];

			// What drew the last game, and why if it isn't what was asked for, on a row below.
			string drawn = ObnsOptions.LastDrawnText;
			labelObnsRendererDrawn = new Label
			{
				Anchor = comboboxVSync.Anchor,
				AutoEllipsis = true,
				Location = new Point(labelVSync.Left, top + rowHeight),
				Size = new Size(comboboxVSync.Right - labelVSync.Left, statusHeight),
				Text = drawn ?? "Last game: none yet",
				TextAlign = ContentAlignment.TopLeft
			};
			tip.SetToolTip(labelObnsRendererDrawn, drawn ?? "What draws the game is decided when it starts.");

			// Room for the rows: the group grows, and what is below it in the panel moves down.
			int groupBottom = groupboxDisplayMode.Bottom;
			groupboxDisplayMode.Height += rowHeight + statusHeight + 6;
			foreach (Control control in groupboxDisplayMode.Parent.Controls)
			{
				if (control != groupboxDisplayMode && control.Top >= groupBottom)
				{
					control.Top += rowHeight + statusHeight + 6;
				}
			}

			groupboxDisplayMode.Controls.Add(labelObnsRenderer);
			groupboxDisplayMode.Controls.Add(comboboxObnsRenderer);
			groupboxDisplayMode.Controls.Add(labelObnsRendererDrawn);
		}

		private void ObnsAddRenderer(string name, ObnsOptions.RendererKind kind)
		{
			comboboxObnsRenderer.Items.Add(name);
			obnsRendererKinds.Add(kind);
		}

		/// <summary>Saves OBNS's options, with upstream's.</summary>
		private void ObnsSaveOptions()
		{
			if (comboboxObnsRenderer != null && comboboxObnsRenderer.Enabled)
			{
				ObnsOptions.RendererKind selected = obnsRendererKinds[comboboxObnsRenderer.SelectedIndex];
				if (selected != obnsRendererShown)
				{
					ObnsOptions.Renderer = selected;
				}

				ObnsOptions.Save();
			}
		}
	}
}
