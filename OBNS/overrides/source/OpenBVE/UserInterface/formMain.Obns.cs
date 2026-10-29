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
			comboboxObnsRenderer.Items.Add("OpenGL");
			ToolTip tip = new ToolTip();
			if (System.Environment.OSVersion.Platform == System.PlatformID.Win32NT)
			{
				comboboxObnsRenderer.Items.Add("Vulkan");
				comboboxObnsRenderer.SelectedIndex = ObnsOptions.Renderer == ObnsOptions.RendererKind.Vulkan ? 1 : 0;
				tip.SetToolTip(comboboxObnsRenderer, "Vulkan draws the world (scenery and trains) where the graphics card can share it with OpenGL, which draws the rest; OpenGL draws everything otherwise. Takes effect when a game starts.");
			}
			else
			{
				// Vulkan draws only on Windows so far (OBNS/VULKAN.md): OpenGL is shown, and the saved choice is kept as it is.
				comboboxObnsRenderer.SelectedIndex = 0;
				comboboxObnsRenderer.Enabled = false;
				tip.SetToolTip(comboboxObnsRenderer, "OpenGL draws everything here: the Vulkan renderer is available on Windows only so far.");
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

		/// <summary>Saves OBNS's options, with upstream's.</summary>
		private void ObnsSaveOptions()
		{
			if (comboboxObnsRenderer != null && comboboxObnsRenderer.Enabled)
			{
				ObnsOptions.Renderer = comboboxObnsRenderer.SelectedIndex == 0 ? ObnsOptions.RendererKind.OpenGL : ObnsOptions.RendererKind.Vulkan;
				ObnsOptions.Save();
			}
		}
	}
}
