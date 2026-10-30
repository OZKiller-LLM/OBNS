// OBNS: not an upstream file. The main menu's native host on macOS (OBNS/GLFORMS.md, "Native"): instead of drawing the
// controls' bitmaps with OpenGL, it mirrors the control tree into AppKit controls (OBNS/mac/MenuKit, in Swift) and gives
// their events to the controls as a user's would be. Upstream's forms, OBNS's control classes and their layout are the
// same as under the OpenGL host, so every page and control is where the Windows menu has it; only the looks are the Mac's.
// A control that paints itself (and anything without a native counterpart) is shown as a picture of its bitmap.
// Used on macOS when libOBNSMenuKit.dylib is beside the program (OBNS_MENU_NATIVE=0: never; else the OpenGL host).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	internal static partial class Host
	{
		private const string Kit = "libOBNSMenuKit.dylib";

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate void NativeEvent(int view, int kind, int a, int b, IntPtr text);

		[DllImport(Kit)] private static extern void mk_init(NativeEvent events);
		[DllImport(Kit)] private static extern void mk_shutdown();
		[DllImport(Kit)] private static extern void mk_pump(double wait);
		[DllImport(Kit)] private static extern int mk_window_create(byte[] title, int width, int height, int resizable);
		[DllImport(Kit)] private static extern void mk_window_show(int id, int parent);
		[DllImport(Kit)] private static extern void mk_window_destroy(int id);
		[DllImport(Kit)] private static extern void mk_window_title(int id, byte[] title);
		[DllImport(Kit)] private static extern void mk_window_size(int id, int width, int height, int minWidth, int minHeight);
		[DllImport(Kit)] private static extern int mk_view_create(int parent, int kind);
		[DllImport(Kit)] private static extern void mk_view_destroy(int id);
		[DllImport(Kit)] private static extern void mk_view_frame(int id, int x, int y, int width, int height);
		[DllImport(Kit)] private static extern void mk_view_visible(int id, int visible);
		[DllImport(Kit)] private static extern void mk_view_enabled(int id, int enabled);
		[DllImport(Kit)] private static extern void mk_view_tooltip(int id, byte[] text);
		[DllImport(Kit)] private static extern void mk_view_fill(int id, int red, int green, int blue, int alpha);
		[DllImport(Kit)] private static extern void mk_view_colors(int id, int red, int green, int blue, int fillRed, int fillGreen, int fillBlue, int fillAlpha);
		[DllImport(Kit)] private static extern void mk_view_text(int id, byte[] text);
		[DllImport(Kit)] private static extern void mk_view_align(int id, int alignment);
		[DllImport(Kit)] private static extern void mk_view_font(int id, float size, int bold);
		[DllImport(Kit)] private static extern void mk_view_focus(int id);
		[DllImport(Kit)] private static extern void mk_button_state(int id, int state);
		[DllImport(Kit)] private static extern void mk_button_key(int id, int key);
		[DllImport(Kit)] private static extern void mk_button_image(int id, int image);
		[DllImport(Kit)] private static extern void mk_items(int id, IntPtr[] items, int count);
		[DllImport(Kit)] private static extern void mk_selected(int id, int index);
		[DllImport(Kit)] private static extern void mk_slider(int id, int minimum, int maximum, int value, int ticks);
		[DllImport(Kit)] private static extern void mk_stepper(int id, double minimum, double maximum, double value, double increment, int decimals);
		[DllImport(Kit)] private static extern void mk_text_options(int id, int readOnly, int links);
		[DllImport(Kit)] private static extern void mk_scroll_content(int id, int width, int height);
		[DllImport(Kit)] private static extern void mk_table_columns(int id, IntPtr[] names, int[] widths, int count, int header);
		[DllImport(Kit)] private static extern void mk_table_rows(int id, IntPtr[] cells, int rows, int columns, int[] icons);
		[DllImport(Kit)] private static extern void mk_table_selection(int id, int[] selected, int count, int multiple);
		[DllImport(Kit)] private static extern void mk_table_show_row(int id, int row);
		[DllImport(Kit)] private static extern int mk_image_create(IntPtr pixels, int width, int height, int stride, float scale);
		[DllImport(Kit)] private static extern void mk_image_free(int id);
		[DllImport(Kit)] private static extern void mk_image_set(int id, int image, int mode);
		[DllImport(Kit)] private static extern int mk_alert(byte[] title, byte[] text, IntPtr[] buttons, int count, int style);
		[DllImport(Kit)] private static extern void mk_free(IntPtr text);
		[DllImport(Kit)] private static extern IntPtr mk_open_panel(byte[] title, byte[] folder, IntPtr[] extensions, int count, int multiple, int folders);
		[DllImport(Kit)] private static extern IntPtr mk_save_panel(byte[] title, byte[] folder, byte[] name, IntPtr[] extensions, int count);
		[DllImport(Kit)] private static extern int mk_menu_popup(int id, int x, int y, IntPtr[] items, int[] enabled, int count);

		// The kinds of control and the events (MenuKit.swift has the same numbers).
		private const int KindContainer = 0, KindLabel = 1, KindLink = 2, KindButton = 3, KindCheck = 4, KindRadio = 5, KindGroup = 6, KindField = 7, KindArea = 8, KindPopup = 9, KindCombo = 10,
			KindStepper = 11, KindSlider = 12, KindTable = 13, KindTabs = 14, KindImage = 15, KindScroll = 16, KindToggle = 17;

		private const int EventClick = 1, EventDoubleClick = 2, EventText = 3, EventSelect = 4, EventClose = 5, EventResize = 6, EventColumn = 7, EventLink = 8, EventDrop = 9, EventMouseDown = 10,
			EventMouseUp = 11, EventMouseMove = 12, EventValue = 13, EventActivate = 14, EventKey = 15;

		/// <summary>Whether the native host is running the menu.</summary>
		internal static bool NativeActive { get; private set; }

		private static bool nativeTried, nativeAvailable;
		private static NativeEvent nativeEvents;

		/// <summary>A control's AppKit counterpart, and what was last given to it (only what changes is sent).</summary>
		private sealed class Peer
		{
			internal Control Control;
			internal int Id, Kind, Parent;
			internal Rectangle Frame = new Rectangle(int.MinValue, 0, 0, 0);
			internal int Visible = -1, Enabled = -1, State = -1, Selected = -2, Key = -1, Align = -1, Value = int.MinValue;
			internal string Text, ToolTip, Items;
			internal float FontSize;
			internal bool Bold, Optioned;
			internal Color Fore = Color.Empty, Back = Color.Empty;
			internal int Image;
			internal object ImageSource;
			internal Size Content;
			internal string Rows, Columns, Selection;
			internal decimal Number = decimal.MinValue;
		}

		private static readonly Dictionary<Control, Peer> peers = new Dictionary<Control, Peer>();
		private static readonly Dictionary<int, Peer> peersById = new Dictionary<int, Peer>();
		private static readonly Dictionary<Image, int> imageIds = new Dictionary<Image, int>();
		private static readonly List<Form> nativeForms = new List<Form>();

		private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes((text ?? string.Empty) + "\0");

		/// <summary>Whether to use the native host: on macOS with the library there, once it has started.</summary>
		internal static bool UseNative()
		{
			if (nativeTried)
			{
				return nativeAvailable;
			}

			nativeTried = true;
			string asked = Environment.GetEnvironmentVariable("OBNS_MENU_NATIVE");
			if (asked == "0" || Program.CurrentHost.Platform != OpenBveApi.Hosts.HostPlatform.AppleOSX)
			{
				return false;
			}

			try
			{
				nativeEvents = OnNativeEvent;
				mk_init(nativeEvents);
				nativeAvailable = true;
				Log("native menu (AppKit, " + Kit + ")");
			}
			catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
			{
				Log("native menu not used, the OpenGL one is: " + ex.Message);
			}

			return nativeAvailable;
		}

		/// <summary>Shows a form in a window of its own and runs the menu until it closes: a dialog is a sheet on the main form's window.</summary>
		private static void RunModalNative(Form form)
		{
			bool main = !NativeActive;
			if (main)
			{
				NativeActive = true;
				uiThread = Thread.CurrentThread;
				previousContext = SynchronizationContext.Current;
				SynchronizationContext.SetSynchronizationContext(new MenuSynchronizationContext());
				Control.AnchorsLive = true;
			}

			Forms.Add(form);
			try
			{
				OpenNativeForm(form, main);
				while (form.IsOpen)
				{
					PumpNative(true);
				}
			}
			finally
			{
				Forms.Remove(form);
				CloseNativeForm(form);
				FocusedControl = null;
				RequestRender();
				if (main)
				{
					foreach (Form other in nativeForms.ToArray())
					{
						CloseNativeForm(other);
					}

					Forms.Clear();
					Control.AnchorsLive = false;
					NativeActive = false;
					// Pictures of closed forms' lists and the menu bar go; the game's window library makes its own.
					foreach (int image in imageIds.Values)
					{
						mk_image_free(image);
					}

					imageIds.Clear();
					mk_pump(0);
					mk_shutdown();
					nativeTried = false;
					SynchronizationContext.SetSynchronizationContext(previousContext);
					uiThread = null;
				}
			}
		}

		/// <summary>The keyboard focus moved by the form: AppKit's follows.</summary>
		private static void FocusNative(Control control)
		{
			if (NativeActive && control != null && peers.TryGetValue(control, out Peer peer))
			{
				mk_view_focus(peer.Id);
			}
		}

		/// <summary>A form opened without waiting for it (Form.Show).</summary>
		internal static void ShowNative(Form form)
		{
			Forms.Add(form);
			OpenNativeForm(form, false);
		}

		private static void OpenNativeForm(Form form, bool main)
		{
			bool sizable = form.FormBorderStyle == FormBorderStyle.Sizable || form.FormBorderStyle == FormBorderStyle.SizableToolWindow;
			int id = mk_window_create(Utf8(form.Text), Math.Max(1, form.Width), Math.Max(1, form.Height), sizable ? 1 : 0);
			Peer peer = new Peer { Control = form, Id = id, Kind = KindContainer, Text = form.Text, Frame = new Rectangle(0, 0, form.Width, form.Height) };
			peers[form] = peer;
			peersById[id] = peer;
			nativeForms.Add(form);
			form.RecordAnchorsRecursive();
			form.RaiseLoad();
			SyncNative();
			mk_window_size(id, Math.Max(1, form.Width), Math.Max(1, form.Height), form.MinimumSize.Width, form.MinimumSize.Height);
			peer.Frame = new Rectangle(0, 0, form.Width, form.Height);
			Peer owner = null;
			if (!main && MainForm != null && MainForm != form)
			{
				peers.TryGetValue(MainForm, out owner);
			}

			mk_window_show(id, form.Modal ? owner?.Id ?? 0 : 0);
			form.SelectFirstControl();
			form.RaiseShown();
			RequestRender();
		}

		private static void CloseNativeForm(Form form)
		{
			if (!nativeForms.Remove(form) || !peers.TryGetValue(form, out Peer peer))
			{
				return;
			}

			foreach (Control child in form.Controls)
			{
				ForgetPeers(child);
			}

			peers.Remove(form);
			peersById.Remove(peer.Id);
			mk_window_destroy(peer.Id);
			ReleaseSurfaces(form);
		}

		/// <summary>Forgets a control's peers (its views go with their window or parent).</summary>
		private static void ForgetPeers(Control control)
		{
			foreach (Control child in control.Controls)
			{
				ForgetPeers(child);
			}

			if (peers.TryGetValue(control, out Peer peer))
			{
				peers.Remove(control);
				peersById.Remove(peer.Id);
				mk_view_destroy(peer.Id);
				if (peer.Image != 0 && !(peer.ImageSource is Image))
				{
					mk_image_free(peer.Image);
				}
			}
		}

		/// <summary>One turn of the loop: AppKit's events, posted calls, timers, and the controls mirrored if anything changed.</summary>
		private static void PumpNative(bool wait)
		{
			lastPump = Now;
			mk_pump(wait ? 0.01 : 0);
			RunPosted();
			RunTimers();
			foreach (Form form in nativeForms.ToArray())
			{
				if (!form.Modal && !Forms.Contains(form))
				{
					CloseNativeForm(form);
				}
			}

			if (renderRequested)
			{
				renderRequested = false;
				SyncNative();
			}
		}

		// --- the mirror ---

		private static void SyncNative()
		{
			foreach (Form form in nativeForms.ToArray())
			{
				try
				{
					Peer peer = peers[form];
					if (peer.Text != form.Text)
					{
						peer.Text = form.Text;
						mk_window_title(peer.Id, Utf8(form.Text));
					}

					if (peer.Frame.Width != form.Width || peer.Frame.Height != form.Height)
					{
						peer.Frame = new Rectangle(0, 0, form.Width, form.Height);
						mk_window_size(peer.Id, Math.Max(1, form.Width), Math.Max(1, form.Height), form.MinimumSize.Width, form.MinimumSize.Height);
					}

					SyncColors(peer);
					SyncChildren(form, peer.Id);
				}
				catch (Exception ex)
				{
					string message = ex.GetType().Name + ": " + ex.Message;
					if (message != lastRenderError)
					{
						lastRenderError = message;
						Log("mirroring failed: " + ex);
					}
				}
			}
		}

		/// <summary>The children, the last first: in Windows Forms the first child is in front, in AppKit the last added.</summary>
		private static void SyncChildren(Control control, int parentId)
		{
			Control[] children = control.Controls.ToArray();
			for (int i = children.Length - 1; i >= 0; i--)
			{
				SyncControl(children[i], parentId);
			}
		}

		private static int KindOf(Control control)
		{
			if (control.HasPaintHandler)
			{
				return KindImage;
			}

			switch (control)
			{
				case TabControl _: return KindTabs;
				case GroupBox _: return KindGroup;
				case LinkLabel _: return KindLink;
				case Label label: return label.Image != null ? KindImage : KindLabel;
				case Button _: return KindButton;
				case CheckBox check: return check.Appearance == Appearance.Button ? KindToggle : KindCheck;
				case RadioButton radio: return radio.Appearance == Appearance.Button ? KindToggle : KindRadio;
				case RichTextBox _: return KindArea;
				case TextBoxBase text: return text.Multiline ? KindArea : KindField;
				case ComboBox combo: return combo.DropDownStyle == ComboBoxStyle.DropDownList ? KindPopup : KindCombo;
				case NumericUpDown _: return KindStepper;
				case TrackBar _: return KindSlider;
				case PictureBox _: return KindImage;
				case ListView _: return KindTable;
				case DataGridView _: return KindTable;
				case ScrollableControl scrollable: return scrollable.AutoScroll ? KindScroll : KindContainer;
				case SplitContainer _: return KindContainer;
				default: return control.DrawsItself ? KindImage : KindContainer;
			}
		}

		private static void SyncControl(Control control, int parentId)
		{
			int kind = KindOf(control);
			if (peers.TryGetValue(control, out Peer peer) && (peer.Kind != kind || peer.Parent != parentId))
			{
				ForgetPeers(control);
				peer = null;
			}

			if (peer == null)
			{
				peer = new Peer { Control = control, Kind = kind, Parent = parentId, Id = mk_view_create(parentId, kind) };
				peers[control] = peer;
				peersById[peer.Id] = peer;
			}

			int visible = control.OwnVisible ? 1 : 0;
			if (visible != peer.Visible)
			{
				peer.Visible = visible;
				mk_view_visible(peer.Id, visible);
			}

			Rectangle frame = control.Bounds;
			if (frame != peer.Frame)
			{
				peer.Frame = frame;
				mk_view_frame(peer.Id, frame.X, frame.Y, frame.Width, frame.Height);
			}

			// What is hidden is brought up to date when it is shown (a page's bitmaps and long lists cost time).
			if (visible == 0)
			{
				return;
			}

			int enabled = control.Enabled ? 1 : 0;
			if (enabled != peer.Enabled)
			{
				peer.Enabled = enabled;
				mk_view_enabled(peer.Id, enabled);
			}

			string tip = ToolTip.TextFor(control);
			if ((tip ?? string.Empty) != (peer.ToolTip ?? string.Empty))
			{
				peer.ToolTip = tip;
				mk_view_tooltip(peer.Id, Utf8(tip));
			}

			SyncFont(peer);
			SyncContent(peer);
			SyncChildren(control, peer.Id);
		}

		private static void SyncFont(Peer peer)
		{
			Font font = peer.Control.Font;
			// System.Drawing's points at Windows' 96 dots an inch, as pixels and so as the Mac's points.
			float size = font.Size * (font.Unit == GraphicsUnit.Point ? 96f / 72f : 1f);
			if (size != peer.FontSize || font.Bold != peer.Bold)
			{
				peer.FontSize = size;
				peer.Bold = font.Bold;
				mk_view_font(peer.Id, size, font.Bold ? 1 : 0);
			}
		}

		/// <summary>A control's own colours, where the form gives it any: upstream's light scheme, the Mac's controls otherwise.</summary>
		private static void SyncColors(Peer peer)
		{
			Control control = peer.Control;
			Color back = control.OwnBackColor;
			Color fore = control.ForeColor;
			if (back == peer.Back && fore == peer.Fore)
			{
				return;
			}

			peer.Back = back;
			peer.Fore = fore;
			if (peer.Kind == KindContainer || peer.Kind == KindGroup || peer.Kind == KindTabs)
			{
				mk_view_fill(peer.Id, back.R, back.G, back.B, back.IsEmpty || control is TabPage ? 0 : back.A);
			}
			else if (peer.Kind == KindLabel || peer.Kind == KindLink)
			{
				mk_view_colors(peer.Id, fore.R, fore.G, fore.B, back.R, back.G, back.B, back.IsEmpty ? 0 : back.A);
			}
		}

		private static string StripMnemonic(string text)
		{
			return (text ?? string.Empty).Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&");
		}

		private static void SyncText(Peer peer, string text)
		{
			if (text != peer.Text)
			{
				peer.Text = text;
				mk_view_text(peer.Id, Utf8(text));
			}
		}

		private static int AlignmentOf(ContentAlignment alignment)
		{
			switch (alignment)
			{
				case ContentAlignment.TopCenter: case ContentAlignment.MiddleCenter: case ContentAlignment.BottomCenter:
					return 1;
				case ContentAlignment.TopRight: case ContentAlignment.MiddleRight: case ContentAlignment.BottomRight:
					return 2;
				default:
					return 0;
			}
		}

		private static void SyncContent(Peer peer)
		{
			SyncColors(peer);
			Control control = peer.Control;
			switch (peer.Kind)
			{
				case KindLabel:
				case KindLink:
				{
					Label label = (Label)control;
					SyncText(peer, label.UseMnemonic ? StripMnemonic(label.Text) : label.Text);
					int align = AlignmentOf(label.TextAlign);
					if (align != peer.Align)
					{
						peer.Align = align;
						mk_view_align(peer.Id, align);
					}

					break;
				}
				case KindButton:
				case KindCheck:
				case KindRadio:
				case KindToggle:
				{
					ButtonBase button = (ButtonBase)control;
					SyncText(peer, button.DisplayText);
					int state = control is CheckBox check ? (check.CheckState == CheckState.Indeterminate ? 2 : check.Checked ? 1 : 0) : control is RadioButton radio ? (radio.Checked ? 1 : 0) : 0;
					if (state != peer.State)
					{
						peer.State = state;
						mk_button_state(peer.Id, state);
					}

					Form form = control.FindForm();
					int key = form != null && form.AcceptButton == control ? 1 : form != null && form.CancelButton == control ? 2 : 0;
					if (key != peer.Key)
					{
						peer.Key = key;
						mk_button_key(peer.Id, key);
					}

					if (!ReferenceEquals(button.Image, peer.ImageSource))
					{
						peer.ImageSource = button.Image;
						mk_button_image(peer.Id, button.Image == null ? 0 : ImageId(button.Image));
					}

					break;
				}
				case KindGroup:
					SyncText(peer, StripMnemonic(control.Text));
					break;
				case KindField:
				case KindArea:
				{
					TextBoxBase text = (TextBoxBase)control;
					if (!peer.Optioned)
					{
						peer.Optioned = true;
						mk_text_options(peer.Id, text.ReadOnly ? 1 : 0, control is RichTextBox ? 1 : 0);
					}

					SyncText(peer, text.Text);
					break;
				}
				case KindPopup:
				case KindCombo:
					SyncCombo(peer, (ComboBox)control);
					break;
				case KindStepper:
				{
					NumericUpDown number = (NumericUpDown)control;
					if (number.Value != peer.Number)
					{
						peer.Number = number.Value;
						mk_stepper(peer.Id, (double)number.Minimum, (double)number.Maximum, (double)number.Value, (double)number.Increment, number.DecimalPlaces);
					}

					break;
				}
				case KindSlider:
				{
					TrackBar slider = (TrackBar)control;
					int ticks = slider.TickStyle == TickStyle.None || slider.TickFrequency <= 0 ? 0 : (slider.Maximum - slider.Minimum) / slider.TickFrequency + 1;
					if (slider.Value != peer.Value)
					{
						peer.Value = slider.Value;
						mk_slider(peer.Id, slider.Minimum, slider.Maximum, slider.Value, ticks);
					}

					break;
				}
				case KindTabs:
				{
					TabControl tabs = (TabControl)control;
					string[] titles = tabs.TabPages.Cast<TabPage>().Select(page => page.Text).ToArray();
					string signature = string.Join("\n", titles);
					if (signature != peer.Items)
					{
						peer.Items = signature;
						SendStrings(titles, (list, count) => mk_items(peer.Id, list, count));
						peer.Selected = -2;
					}

					if (tabs.SelectedIndex != peer.Selected)
					{
						peer.Selected = tabs.SelectedIndex;
						mk_selected(peer.Id, tabs.SelectedIndex);
					}

					break;
				}
				case KindTable:
					if (control is ListView list)
					{
						SyncListView(peer, list);
					}
					else
					{
						SyncGrid(peer, (DataGridView)control);
					}

					break;
				case KindImage:
					SyncImage(peer);
					break;
				case KindScroll:
				{
					Size content = new Size(0, ((ScrollableControl)control).AutoScrollMinSize.Height);
					foreach (Control child in control.Controls)
					{
						if (child.OwnVisible)
						{
							content.Width = Math.Max(content.Width, child.Right);
							content.Height = Math.Max(content.Height, child.Bottom);
						}
					}

					content.Width = Math.Max(content.Width, control.Width - 17);
					if (content != peer.Content)
					{
						peer.Content = content;
						mk_scroll_content(peer.Id, content.Width, content.Height);
					}

					break;
				}
			}
		}

		private static void SyncCombo(Peer peer, ComboBox combo)
		{
			string[] items = new string[combo.Items.Count];
			for (int i = 0; i < items.Length; i++)
			{
				items[i] = combo.GetItemText(combo.Items[i]);
			}

			string signature = string.Join("\n", items);
			if (signature != peer.Items)
			{
				peer.Items = signature;
				SendStrings(items, (list, count) => mk_items(peer.Id, list, count));
				peer.Selected = -2;
			}

			if (combo.SelectedIndex != peer.Selected)
			{
				peer.Selected = combo.SelectedIndex;
				mk_selected(peer.Id, combo.SelectedIndex);
			}

			if (peer.Kind == KindCombo)
			{
				SyncText(peer, combo.Text);
			}
		}

		private static void SyncListView(Peer peer, ListView list)
		{
			bool details = list.View == View.Details && list.Columns.Count > 0;
			string[] names = details ? list.Columns.Cast<ColumnHeader>().Select(column => column.Text).ToArray() : new[] { string.Empty };
			int[] widths = details ? list.Columns.Cast<ColumnHeader>().Select(column => column.Width < 0 ? 120 : column.Width).ToArray() : new[] { Math.Max(1, list.Width - 20) };
			string columns = string.Join("\n", names) + "|" + string.Join(",", widths);
			if (columns != peer.Columns)
			{
				peer.Columns = columns;
				SendStrings(names, (texts, count) => mk_table_columns(peer.Id, texts, widths, count, details && list.HeaderStyle != ColumnHeaderStyle.None ? 1 : 0));
				peer.Rows = null;
			}

			int columnCount = names.Length, rowCount = list.Items.Count;
			string[] cells = new string[rowCount * columnCount];
			int[] icons = new int[rowCount];
			ImageList images = list.SmallImageList ?? list.LargeImageList;
			System.Text.StringBuilder signature = new System.Text.StringBuilder();
			for (int row = 0; row < rowCount; row++)
			{
				ListViewItem item = list.Items[row];
				for (int column = 0; column < columnCount; column++)
				{
					cells[row * columnCount + column] = column < item.SubItems.Count ? item.SubItems[column].Text : string.Empty;
					signature.Append(cells[row * columnCount + column]).Append('\u0001');
				}

				Image icon = images != null && images.Images.Count > 0 ? (item.ImageIndex >= 0 && item.ImageIndex < images.Images.Count ? images.Images[item.ImageIndex] : !string.IsNullOrEmpty(item.ImageKey) ? images.Images[item.ImageKey] : null) : null;
				icons[row] = icon == null ? 0 : ImageId(icon);
				signature.Append(icons[row]).Append('\u0002');
			}

			string rows = signature.ToString();
			if (rows != peer.Rows)
			{
				peer.Rows = rows;
				SendStrings(cells, (texts, count) => mk_table_rows(peer.Id, texts, rowCount, columnCount, icons));
				peer.Selection = null;
			}

			int[] selected = list.Items.Cast<ListViewItem>().Where(item => item.Selected).Select(item => item.Index).ToArray();
			string selection = string.Join(",", selected);
			if (selection != peer.Selection)
			{
				peer.Selection = selection;
				mk_table_selection(peer.Id, selected, selected.Length, list.MultiSelect ? 1 : 0);
				if (selected.Length > 0)
				{
					mk_table_show_row(peer.Id, selected[0]);
				}
			}
		}

		private static void SyncGrid(Peer peer, DataGridView grid)
		{
			DataGridViewColumn[] gridColumns = grid.Columns.Cast<DataGridViewColumn>().ToArray();
			string[] names = gridColumns.Select(column => column.HeaderText ?? string.Empty).ToArray();
			int[] widths = gridColumns.Select(column => Math.Max(20, column.Width)).ToArray();
			string columns = string.Join("\n", names) + "|" + string.Join(",", widths);
			if (columns != peer.Columns)
			{
				peer.Columns = columns;
				SendStrings(names, (texts, count) => mk_table_columns(peer.Id, texts, widths, count, grid.ColumnHeadersVisible ? 1 : 0));
				peer.Rows = null;
			}

			DataGridViewRow[] gridRows = grid.Rows.Cast<DataGridViewRow>().Where(row => row.Visible).ToArray();
			string[] cells = new string[gridRows.Length * names.Length];
			for (int row = 0; row < gridRows.Length; row++)
			{
				for (int column = 0; column < names.Length; column++)
				{
					cells[row * names.Length + column] = column < gridRows[row].Cells.Count ? Convert.ToString(gridRows[row].Cells[column].Value, CultureInfo.CurrentCulture) ?? string.Empty : string.Empty;
				}
			}

			string rows = string.Join("\u0001", cells);
			if (rows != peer.Rows)
			{
				peer.Rows = rows;
				SendStrings(cells, (texts, count) => mk_table_rows(peer.Id, texts, gridRows.Length, names.Length, null));
				peer.Selection = null;
			}

			int[] selected = Enumerable.Range(0, gridRows.Length).Where(row => gridRows[row].Selected).ToArray();
			string selection = string.Join(",", selected);
			if (selection != peer.Selection)
			{
				peer.Selection = selection;
				mk_table_selection(peer.Id, selected, selected.Length, grid.MultiSelect ? 1 : 0);
			}
		}

		/// <summary>A picture box's picture; or the bitmap of a control that paints itself (at twice its size, for Retina).</summary>
		private static void SyncImage(Peer peer)
		{
			if (peer.Control is PictureBox box && !box.HasPaintHandler)
			{
				if (!ReferenceEquals(box.Image, peer.ImageSource) || box.Image != null && box.ImageVersion != peer.Value)
				{
					peer.ImageSource = box.Image;
					peer.Value = box.ImageVersion;
					int mode = box.SizeMode == PictureBoxSizeMode.StretchImage ? 1 : box.SizeMode == PictureBoxSizeMode.Zoom ? 2 : box.SizeMode == PictureBoxSizeMode.CenterImage ? 3 : 0;
					mk_image_set(peer.Id, box.Image == null ? 0 : ImageId(box.Image, true), mode);
				}

				return;
			}

			const float scale = 2.0f;
			Surface surface = peer.Control.GetSurface(scale);
			if (!surface.Changed)
			{
				return;
			}

			surface.Changed = false;
			int previous = peer.Image;
			peer.Image = SendBitmap(surface.Bitmap, scale);
			mk_image_set(peer.Id, peer.Image, 1);
			if (previous != 0)
			{
				mk_image_free(previous);
			}
		}

		/// <summary>An image's picture in AppKit, kept while the image lives (again if it is asked for afresh: a preview changes).</summary>
		private static int ImageId(Image image, bool fresh = false)
		{
			if (imageIds.TryGetValue(image, out int id))
			{
				if (!fresh)
				{
					return id;
				}

				mk_image_free(id);
			}

			lock (image)
			{
				using (Bitmap copy = new Bitmap(Math.Max(1, image.Width), Math.Max(1, image.Height), PixelFormat.Format32bppPArgb))
				{
					using (Graphics g = Graphics.FromImage(copy))
					{
						g.DrawImage(image, 0, 0, copy.Width, copy.Height);
					}

					id = SendBitmap(copy, 1.0f);
				}
			}

			imageIds[image] = id;
			return id;
		}

		private static int SendBitmap(Bitmap bitmap, float scale)
		{
			BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
			try
			{
				return mk_image_create(data.Scan0, data.Width, data.Height, data.Stride, scale);
			}
			finally
			{
				bitmap.UnlockBits(data);
			}
		}

		/// <summary>Strings as a C array of UTF-8, for one call.</summary>
		private static void SendStrings(string[] strings, Action<IntPtr[], int> call)
		{
			IntPtr[] native = new IntPtr[strings.Length];
			try
			{
				for (int i = 0; i < strings.Length; i++)
				{
					byte[] bytes = Utf8(strings[i]);
					native[i] = Marshal.AllocHGlobal(bytes.Length);
					Marshal.Copy(bytes, 0, native[i], bytes.Length);
				}

				call(native, strings.Length);
			}
			finally
			{
				foreach (IntPtr text in native)
				{
					Marshal.FreeHGlobal(text);
				}
			}
		}

		// --- events: as a user's, through the same ways in as the OpenGL host's ---

		private static void OnNativeEvent(int view, int kind, int a, int b, IntPtr text)
		{
			// Nothing may cross back into native code as an exception.
			try
			{
				if (peersById.TryGetValue(view, out Peer peer))
				{
					string value = text == IntPtr.Zero ? null : PtrToUtf8(text);
					HandleEvent(peer, kind, a, b, value);
					RequestRender();
				}
			}
			catch (Exception ex)
			{
				Log("a native event failed: " + ex);
			}
		}

		private static string PtrToUtf8(IntPtr text)
		{
			int length = 0;
			while (Marshal.ReadByte(text, length) != 0)
			{
				length++;
			}

			byte[] bytes = new byte[length];
			Marshal.Copy(text, bytes, 0, length);
			return System.Text.Encoding.UTF8.GetString(bytes);
		}

		private static void HandleEvent(Peer peer, int kind, int a, int b, string value)
		{
			Control control = peer.Control;
			switch (kind)
			{
				case EventClick:
					FocusedControl = control;
					if (control is ButtonBase button)
					{
						// The model decides (a check box toggles, a radio button checks): the native state follows it.
						peer.State = -1;
						button.PerformClick();
					}

					break;
				case EventDoubleClick:
					control.RaiseDoubleClick();
					break;
				case EventText:
					peer.Text = value;
					if (control is NumericUpDown number)
					{
						if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
						{
							peer.Number = Math.Max(number.Minimum, Math.Min(number.Maximum, parsed));
							number.Value = peer.Number;
						}
					}
					else
					{
						control.Text = value;
					}

					break;
				case EventSelect:
					if (control is ComboBox combo)
					{
						peer.Selected = a;
						combo.NativeSelect(a);
					}
					else if (control is TabControl tabs)
					{
						// A page may refuse (Selecting): the strip follows the model.
						peer.Selected = -2;
						tabs.SelectedIndex = a;
					}
					else
					{
						HashSet<int> rows = new HashSet<int>(string.IsNullOrEmpty(value) ? new int[0] : value.Split(',').Select(int.Parse));
						peer.Selection = string.Join(",", rows.OrderBy(row => row));
						if (control is ListView list)
						{
							list.NativeSelect(rows);
						}
						else
						{
							((DataGridView)control).NativeSelect(rows);
						}
					}

					break;
				case EventClose:
					if (control is Form form)
					{
						form.RequestClose(CloseReason.UserClosing);
					}

					break;
				case EventResize:
					if (control is Form resized)
					{
						peer.Frame = new Rectangle(0, 0, a, b);
						resized.SetSizeFromWindow(a, b);
					}

					break;
				case EventColumn:
					(control as ListView)?.NativeColumnClick(a);
					break;
				case EventLink:
					if (control is RichTextBox rich)
					{
						rich.NativeLink(value);
					}
					else
					{
						control.RaiseClick();
					}

					break;
				case EventDrop:
				{
					Control target = control;
					Control dropTarget = FindDropTarget(target);
					if (dropTarget != null)
					{
						DragEventArgs e = new DragEventArgs(new FileDropData(new[] { value }), 0, 0, 0, DragDropEffects.All, DragDropEffects.None);
						dropTarget.RaiseDragEnter(e);
						if (e.Effect != DragDropEffects.None)
						{
							dropTarget.RaiseDragDrop(e);
						}
					}

					break;
				}
				case EventMouseDown:
					control.RaiseMouseDown(new MouseEventArgs(MouseButtons.Left, 1, a, b, 0));
					break;
				case EventMouseUp:
				{
					MouseEventArgs e = new MouseEventArgs(MouseButtons.Left, 1, a, b, 0);
					control.RaiseMouseUp(e);
					control.RaiseClick();
					control.RaiseMouseClick(e);
					break;
				}
				case EventMouseMove:
					control.RaiseMouseMove(new MouseEventArgs(MouseButtons.Left, 0, a, b, 0));
					break;
				case EventValue:
					peer.Value = a;
					(control as TrackBar)?.NativeValue(a);
					break;
				case EventActivate:
					if (control is ListView activated)
					{
						activated.RaiseDoubleClick();
					}
					else if (control is DataGridView grid)
					{
						grid.NativeActivate(a);
					}

					break;
				case EventKey:
				{
					Keys keys = MapNativeKey(a, b);
					if (keys != Keys.None)
					{
						control.RaiseKeyDown(new KeyEventArgs(keys));
					}

					break;
				}
			}
		}

		/// <summary>The visible control that takes dropped files (the page showing with AllowDrop, as the form has only the one).</summary>
		private static Control FindDropTarget(Control control)
		{
			foreach (Control child in control.Controls)
			{
				if (child.OwnVisible)
				{
					Control found = FindDropTarget(child);
					if (found != null)
					{
						return found;
					}
				}
			}

			return control.AllowDrop ? control : null;
		}

		/// <summary>A Mac key code and its modifiers (NSEvent's flags, shifted down 16 bits), as the keys the forms ask about.</summary>
		private static Keys MapNativeKey(int code, int flags)
		{
			Keys key;
			switch (code)
			{
				case 36: case 76: key = Keys.Return; break;
				case 48: key = Keys.Tab; break;
				case 49: key = Keys.Space; break;
				case 51: key = Keys.Back; break;
				case 53: key = Keys.Escape; break;
				case 117: key = Keys.Delete; break;
				case 115: key = Keys.Home; break;
				case 119: key = Keys.End; break;
				case 116: key = Keys.PageUp; break;
				case 121: key = Keys.PageDown; break;
				case 123: key = Keys.Left; break;
				case 124: key = Keys.Right; break;
				case 125: key = Keys.Down; break;
				case 126: key = Keys.Up; break;
				default:
				{
					// Letters and digits, in the Mac's key code order.
					const string letters = "ASDFHGZXCV\0BQWERYT123465\0\097\0\080\0OU\0IP\0LJ\0K\0\0\0\0NM";
					char c = code < letters.Length ? letters[code] : '\0';
					key = c == '\0' ? Keys.None : (Keys)c;
					break;
				}
			}

			if (key == Keys.None)
			{
				return Keys.None;
			}

			if ((flags & 2) != 0) key |= Keys.Shift;
			if ((flags & (4 | 16)) != 0) key |= Keys.Control; // Command as Control, as the OpenGL host takes it
			if ((flags & 8) != 0) key |= Keys.Alt;
			return key;
		}

		// --- dialogs: the Mac's own ---

		internal static DialogResult NativeMessage(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
		{
			string[] labels;
			DialogResult[] results;
			switch (buttons)
			{
				case MessageBoxButtons.OKCancel:
					labels = new[] { "OK", "Cancel" };
					results = new[] { DialogResult.OK, DialogResult.Cancel };
					break;
				case MessageBoxButtons.YesNo:
					labels = new[] { "Yes", "No" };
					results = new[] { DialogResult.Yes, DialogResult.No };
					break;
				case MessageBoxButtons.YesNoCancel:
					labels = new[] { "Yes", "No", "Cancel" };
					results = new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel };
					break;
				case MessageBoxButtons.RetryCancel:
					labels = new[] { "Retry", "Cancel" };
					results = new[] { DialogResult.Retry, DialogResult.Cancel };
					break;
				case MessageBoxButtons.AbortRetryIgnore:
					labels = new[] { "Abort", "Retry", "Ignore" };
					results = new[] { DialogResult.Abort, DialogResult.Retry, DialogResult.Ignore };
					break;
				default:
					labels = new[] { "OK" };
					results = new[] { DialogResult.OK };
					break;
			}

			int style = icon == MessageBoxIcon.Error ? 2 : icon == MessageBoxIcon.Warning ? 1 : 0;
			int pressed = 0;
			// The title line is the caption, as a window's title; the text below it.
			SendStrings(labels, (list, count) => pressed = mk_alert(Utf8(caption), Utf8(text), list, count, style));
			return results[Math.Max(0, Math.Min(results.Length - 1, pressed))];
		}

		/// <summary>The filters' extensions, for the panels ("*.csv" as "csv"; any "*" means every file).</summary>
		private static string[] Extensions(FileDialog dialog)
		{
			List<KeyValuePair<string, string[]>> filters = dialog.ParseFilter();
			IEnumerable<string> patterns = dialog.FilterIndex >= 1 && dialog.FilterIndex <= filters.Count ? filters[dialog.FilterIndex - 1].Value : filters.SelectMany(filter => filter.Value);
			return patterns.Select(pattern => pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern.Substring(2) : "*").Distinct().ToArray();
		}

		/// <summary>Chooses files with the Mac's panel: the paths, or null if cancelled.</summary>
		internal static string[] NativeOpen(FileDialog dialog)
		{
			IntPtr result = IntPtr.Zero;
			SendStrings(Extensions(dialog), (list, count) => result = mk_open_panel(Utf8(dialog.Title), Utf8(dialog.InitialDirectory), list, count, dialog.MultiselectFiles ? 1 : 0, 0));
			return TakePaths(result);
		}

		/// <summary>Chooses where to save with the Mac's panel: the path, or null if cancelled.</summary>
		internal static string NativeSave(FileDialog dialog)
		{
			IntPtr result = IntPtr.Zero;
			SendStrings(Extensions(dialog), (list, count) => result = mk_save_panel(Utf8(dialog.Title), Utf8(dialog.InitialDirectory), Utf8(System.IO.Path.GetFileName(dialog.FileName ?? string.Empty)), list, count));
			return TakePaths(result)?[0];
		}

		/// <summary>Chooses a folder with the Mac's panel: the path, or null if cancelled.</summary>
		internal static string NativeFolder(FolderBrowserDialog dialog)
		{
			IntPtr result = mk_open_panel(Utf8(dialog.Description), Utf8(dialog.SelectedPath), new IntPtr[0], 0, 0, 1);
			return TakePaths(result)?[0];
		}

		private static string[] TakePaths(IntPtr result)
		{
			if (result == IntPtr.Zero)
			{
				return null;
			}

			string paths = PtrToUtf8(result);
			mk_free(result);
			return paths.Split('\n');
		}

		/// <summary>A context menu with AppKit's: the item chosen is clicked.</summary>
		internal static void NativeMenu(ContextMenuStrip strip, Control control, Point position)
		{
			if (control == null || !peers.TryGetValue(control, out Peer peer))
			{
				return;
			}

			ToolStripItem[] items = strip.Items.Cast<ToolStripItem>().Where(item => item.Visible).ToArray();
			string[] titles = items.Select(item => item is ToolStripSeparator ? string.Empty : StripMnemonic(item.Text)).ToArray();
			int[] enabled = items.Select(item => item.Enabled ? 1 : 0).ToArray();
			int chosen = -1;
			SendStrings(titles, (list, count) => chosen = mk_menu_popup(peer.Id, position.X, position.Y, list, enabled, count));
			if (chosen >= 0)
			{
				items[chosen].PerformClick();
			}
		}
	}
}
