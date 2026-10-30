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

			const int rowHeight = 26;
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
				int chosen = obnsRendererKinds.IndexOf(ObnsOptions.Renderer);
				comboboxObnsRenderer.SelectedIndex = chosen >= 0 ? chosen : 1;

				tip.SetToolTip(comboboxObnsRenderer, "Vulkan or Direct3D draws the world (scenery and trains) where the graphics card can share it with OpenGL, which draws the rest; OpenGL draws everything otherwise. Takes effect when a game starts.");
			}
			else if (Program.CurrentHost.Platform == OpenBveApi.Hosts.HostPlatform.AppleOSX)
			{
				// Metal on macOS (OBNS/METAL.md).
				ObnsAddRenderer("Metal", ObnsOptions.RendererKind.Metal);
				comboboxObnsRenderer.SelectedIndex = ObnsOptions.Renderer == ObnsOptions.RendererKind.Metal ? 1 : 0;
				tip.SetToolTip(comboboxObnsRenderer, "Metal draws the world (scenery and trains), sharing it with OpenGL, which draws the rest; OpenGL draws everything otherwise. Takes effect when a game starts.");
			}
			else
			{
				// Linux and the like: Vulkan where the driver can share images with OpenGL (OBNS/VULKAN.md).
				ObnsAddRenderer("Vulkan", ObnsOptions.RendererKind.Vulkan);
				int chosen = obnsRendererKinds.IndexOf(ObnsOptions.Renderer);
				comboboxObnsRenderer.SelectedIndex = chosen >= 0 ? chosen : 1;
				tip.SetToolTip(comboboxObnsRenderer, "Vulkan draws the world (scenery and trains) where the graphics card can share it with OpenGL, which draws the rest; OpenGL draws everything otherwise. Takes effect when a game starts.");
			}

			// Room for the row: the group grows, and what is below it in the panel moves down.
			int groupBottom = groupboxDisplayMode.Bottom;
			groupboxDisplayMode.Height += rowHeight + 6;
			foreach (Control control in groupboxDisplayMode.Parent.Controls)
			{
				if (control != groupboxDisplayMode && control.Top >= groupBottom)
				{
					control.Top += rowHeight + 6;
				}
			}

			groupboxDisplayMode.Controls.Add(labelObnsRenderer);
			groupboxDisplayMode.Controls.Add(comboboxObnsRenderer);
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
				ObnsOptions.Renderer = obnsRendererKinds[comboboxObnsRenderer.SelectedIndex];
				ObnsOptions.Save();
			}
		}
	}
}
