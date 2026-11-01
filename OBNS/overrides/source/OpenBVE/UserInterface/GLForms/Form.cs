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

// OBNS: not an upstream file. The in-window main menu (OBNS/GLFORMS.md): forms, and the dialogs Windows would give them
// (message boxes, file and folder choosers), drawn in the menu's window; context menus, tool tips and timers.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	public interface IWin32Window
	{
		IntPtr Handle { get; }
	}

	public interface IButtonControl
	{
		DialogResult DialogResult { get; set; }

		void NotifyDefault(bool value);

		void PerformClick();
	}

	public class Form : Control, IWin32Window
	{
		/// <summary>A dialog's frame, in form units: its title bar and border.</summary>
		internal const int TitleHeight = 26, FrameBorder = 1;

		public Form()
		{
			SetVisibleCore(false);
		}

		private void SetVisibleCore(bool value)
		{
			// A form is shown by ShowDialog; until then it is not on screen, but its controls count as visible.
		}

		protected override Size DefaultSize => new Size(300, 300);

		protected override Color DefaultBackColor => ControlColor;

		internal override bool DrawsItself => true;

		internal bool IsOpen { get; private set; }

		public bool Modal { get; private set; }

		public Form Owner { get; set; }

		public bool KeyPreview { get; set; }

		public SizeF AutoScaleDimensions { get; set; }

		public AutoScaleMode AutoScaleMode { get; set; }

		public FormStartPosition StartPosition { get; set; } = FormStartPosition.WindowsDefaultLocation;

		public FormBorderStyle FormBorderStyle { get; set; } = FormBorderStyle.Sizable;

		public bool MaximizeBox { get; set; } = true;

		public bool MinimizeBox { get; set; } = true;

		public bool ControlBox { get; set; } = true;

		public bool ShowIcon { get; set; } = true;

		public bool ShowInTaskbar { get; set; } = true;

		public bool TopMost { get; set; }

		public bool HelpButton { get; set; }

		public SizeGripStyle SizeGripStyle { get; set; }

		public double Opacity { get; set; } = 1.0;

		public IButtonControl AcceptButton { get; set; }

		public IButtonControl CancelButton { get; set; }

		private Icon icon;

		public Icon Icon
		{
			get { return icon; }
			set
			{
				icon = value;
				if (this == Host.MainForm && Host.Window != null && value != null)
				{
					try
					{
						Host.Window.Icon = value;
					}
					catch (Exception)
					{
						// not every platform takes a window icon
					}
				}
			}
		}

		public override string Text
		{
			get { return base.Text; }
			set
			{
				base.Text = value;
				frameDirty = true;
				if (this == Host.MainForm && Host.Window != null)
				{
					Host.Window.Title = value ?? string.Empty;
				}
			}
		}

		private FormWindowState windowState = FormWindowState.Normal;

		public FormWindowState WindowState
		{
			get
			{
				if (this == Host.MainForm && Host.Window != null)
				{
					switch (Host.Window.WindowState)
					{
						case OpenTK.WindowState.Maximized: return FormWindowState.Maximized;
						case OpenTK.WindowState.Minimized: return FormWindowState.Minimized;
						default: return FormWindowState.Normal;
					}
				}

				return windowState;
			}
			set
			{
				windowState = value;
				if (this == Host.MainForm && Host.Window != null)
				{
					Host.Window.WindowState = value == FormWindowState.Maximized ? OpenTK.WindowState.Maximized : value == FormWindowState.Minimized ? OpenTK.WindowState.Minimized : OpenTK.WindowState.Normal;
				}
			}
		}

		private DialogResult dialogResult;

		public DialogResult DialogResult
		{
			get { return dialogResult; }
			set
			{
				dialogResult = value;
				if (IsOpen && Modal && value != DialogResult.None)
				{
					RequestClose(CloseReason.UserClosing);
				}
			}
		}

		// --- size: the main form is the window's content; a dialog is drawn at its place in the window ---

		/// <summary>The main form's size follows the window's: setting it resizes the window.</summary>
		public new Size Size
		{
			get { return base.Size; }
			set
			{
				if (this == Host.MainForm && Host.Window != null)
				{
					Host.Window.ClientSize = new Size((int)(value.Width * Host.Scale), (int)(value.Height * Host.Scale));
				}
				else
				{
					base.Size = value;
				}
			}
		}

		public new int Width
		{
			get { return base.Width; }
			set { Size = new Size(value, Height); }
		}

		public new int Height
		{
			get { return base.Height; }
			set { Size = new Size(Width, value); }
		}

		internal void SetSizeFromWindow(int width, int height)
		{
			base.Size = new Size(width, height);
		}

		protected override void SetClientSizeCore(int x, int y)
		{
			Size = new Size(x, y);
		}

		internal Rectangle FrameBounds => new Rectangle(Left, Top, Width + 2 * FrameBorder, Height + TitleHeight + FrameBorder);

		internal Rectangle CloseBoxBounds => new Rectangle(Left + Width + 2 * FrameBorder - TitleHeight - 4, Top, TitleHeight + 4, TitleHeight);

		public void CenterToScreen()
		{
			if (this == Host.MainForm && Host.Window != null)
			{
				Rectangle screen = Screen.PrimaryScreen.Bounds;
				Host.Window.Location = new Point(screen.X + (screen.Width - Host.Window.Width) / 2, screen.Y + (screen.Height - Host.Window.Height) / 2);
			}
			else
			{
				PlaceDialog(Host.MainForm);
			}
		}

		public void CenterToParent()
		{
			PlaceDialog(Host.MainForm);
		}

		internal void PlaceDialog(Form main)
		{
			if (main == null)
			{
				return;
			}

			Rectangle frame = FrameBounds;
			Location = new Point(Math.Max(0, (main.Width - frame.Width) / 2), Math.Max(0, (main.Height - frame.Height) / 2));
		}

		private Surface frame;
		private bool frameDirty = true;

		/// <summary>The dialog's frame: border and title bar with its text and close box.</summary>
		internal Surface GetFrameSurface(float scale)
		{
			Rectangle bounds = FrameBounds;
			int pw = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), ph = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
			if (frame != null && !frameDirty && frame.Width == pw && frame.Height == ph)
			{
				return frame;
			}

			frame?.Dispose();
			frame = new Surface(pw, ph);
			using (Graphics g = Graphics.FromImage(frame.Bitmap))
			{
				g.ScaleTransform(scale, scale);
				g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
				g.Clear(Color.FromArgb(255, 252, 252, 252));
				using (Pen pen = new Pen(Color.FromArgb(120, 120, 120)))
				{
					g.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
				}

				using (Font title = new Font(DefaultFont.FontFamily, 9.0f))
				{
					DrawText(g, Text, title, Color.Black, new RectangleF(8, 0, bounds.Width - TitleHeight - 12, TitleHeight), ContentAlignment.MiddleLeft, false, true);
				}

				if (ControlBox)
				{
					float cx = bounds.Width - TitleHeight / 2f - 2, cy = TitleHeight / 2f;
					using (Pen pen = new Pen(Color.FromArgb(60, 60, 60), 1.2f))
					{
						g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
						g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
						g.DrawLine(pen, cx - 5, cy + 5, cx + 5, cy - 5);
					}
				}
			}

			frame.Changed = true;
			frameDirty = false;
			return frame;
		}

		// --- showing and closing ---

		public DialogResult ShowDialog()
		{
			return ShowDialog(null);
		}

		public DialogResult ShowDialog(IWin32Window owner)
		{
			Owner = owner as Form;
			Modal = true;
			dialogResult = DialogResult.None;
			Visible = true; // as Windows Forms': a form hidden earlier (SanYingInput hides its own when it loads) shows again
			IsOpen = true;
			Host.RunModal(this);
			IsOpen = false;
			Modal = false;
			if (dialogResult == DialogResult.None)
			{
				dialogResult = DialogResult.Cancel;
			}

			return dialogResult;
		}

		/// <summary>Shows a form without waiting for it (it is drawn and used over the others until closed).</summary>
		public void Show(IWin32Window owner)
		{
			Show();
		}

		public new void Show()
		{
			if (IsOpen)
			{
				return;
			}

			Visible = true;
			if (Host.NativeActive)
			{
				IsOpen = true;
				Host.ShowNative(this);
				return;
			}

			if (Host.Window == null)
			{
				ShowDialog();
				return;
			}

			IsOpen = true;
			Host.Forms.Add(this);
			RecordAnchorsRecursive();
			PlaceDialog(Host.MainForm);
			RaiseLoad();
			SelectFirstControl();
			RaiseShown();
			Host.RequestRender();
		}

		public void Activate()
		{
		}

		public void Close()
		{
			RequestClose(CloseReason.UserClosing);
		}

		private bool closing;

		/// <summary>Closes the form unless FormClosing cancels it.</summary>
		internal bool RequestClose(CloseReason reason)
		{
			if (!IsOpen || closing)
			{
				return !IsOpen;
			}

			closing = true;
			try
			{
				FormClosingEventArgs e = new FormClosingEventArgs(reason, false);
				OnFormClosing(e);
				if (e.Cancel)
				{
					return false;
				}

				IsOpen = false;
				OnFormClosed(new FormClosedEventArgs(reason));
				if (!Modal)
				{
					Host.Forms.Remove(this);
				}

				Host.RequestRender();
				return true;
			}
			finally
			{
				closing = false;
			}
		}

		/// <summary>The window's close box: the main form's closing, which may be refused.</summary>
		internal void CloseFromWindow(CancelEventArgs e)
		{
			if (!RequestClose(CloseReason.UserClosing))
			{
				e.Cancel = true;
			}
		}

		public event EventHandler Load;
		public event EventHandler Shown;
		public event EventHandler Activated;
		public event EventHandler Deactivate;
		public event FormClosingEventHandler FormClosing;
		public event FormClosedEventHandler FormClosed;

		protected virtual void OnLoad(EventArgs e) => Load?.Invoke(this, e);
		protected virtual void OnShown(EventArgs e) => Shown?.Invoke(this, e);
		protected virtual void OnActivated(EventArgs e) => Activated?.Invoke(this, e);
		protected virtual void OnDeactivate(EventArgs e) => Deactivate?.Invoke(this, e);
		protected virtual void OnFormClosing(FormClosingEventArgs e) => FormClosing?.Invoke(this, e);
		protected virtual void OnFormClosed(FormClosedEventArgs e) => FormClosed?.Invoke(this, e);

		internal void RaiseLoad() => OnLoad(EventArgs.Empty);

		internal void RaiseShown()
		{
			OnShown(EventArgs.Empty);
			OnActivated(EventArgs.Empty);
		}

		// --- focus order ---

		private static void Collect(Control control, List<Control> list)
		{
			foreach (Control child in control.Controls.OrderBy(c => c.TabIndex))
			{
				if (!child.OwnVisible)
				{
					continue;
				}

				if (child.CanTakeFocus && child.TabStop && child.Enabled)
				{
					list.Add(child);
				}

				Collect(child, list);
			}
		}

		internal void SelectFirstControl()
		{
			if (Host.FocusedControl != null && Host.FocusedControl.FindForm() == this)
			{
				return;
			}

			SelectNextControl(null, true);
		}

		internal void SelectNextControl(Control from, bool forward)
		{
			List<Control> list = new List<Control>();
			Collect(this, list);
			if (list.Count == 0)
			{
				return;
			}

			int index = from == null ? -1 : list.IndexOf(from);
			index = forward ? index + 1 : (index < 0 ? list.Count - 1 : index - 1);
			index = (index % list.Count + list.Count) % list.Count;
			list[index].Focus();
		}

		public bool SelectNextControl(Control ctl, bool forward, bool tabStopOnly, bool nested, bool wrap)
		{
			SelectNextControl(ctl, forward);
			return true;
		}

		public Control ActiveControl
		{
			get { return Host.FocusedControl != null && Host.FocusedControl.FindForm() == this ? Host.FocusedControl : null; }
			set { value?.Focus(); }
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				frame?.Dispose();
				frame = null;
			}

			base.Dispose(disposing);
		}

		IntPtr IWin32Window.Handle => IntPtr.Zero;
	}

	// --- message boxes ---

	public static class MessageBox
	{
		public static DialogResult Show(string text) => Show(null, text, string.Empty, MessageBoxButtons.OK, MessageBoxIcon.None);

		public static DialogResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);

		public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) => Show(null, text, caption, buttons, MessageBoxIcon.None);

		public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) => Show(null, text, caption, buttons, icon);

		public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton) => Show(null, text, caption, buttons, icon);

		public static DialogResult Show(IWin32Window owner, string text) => Show(owner, text, string.Empty, MessageBoxButtons.OK, MessageBoxIcon.None);

		public static DialogResult Show(IWin32Window owner, string text, string caption) => Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);

		public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons) => Show(owner, text, caption, buttons, MessageBoxIcon.None);

		public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
		{
			if (Host.InvokeRequired)
			{
				return (DialogResult)Host.Invoke(() => Show(owner, text, caption, buttons, icon));
			}

			if (Host.NativeActive)
			{
				return Host.NativeMessage(text ?? string.Empty, caption ?? string.Empty, buttons, icon);
			}

			using (MessageBoxForm form = new MessageBoxForm(text ?? string.Empty, caption ?? string.Empty, buttons, icon))
			{
				return form.ShowDialog(owner);
			}
		}
	}

	internal sealed class MessageBoxForm : Form
	{
		private readonly MessageBoxIcon icon;

		internal MessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
		{
			this.icon = icon;
			Text = caption;
			BackColor = Color.White;
			StartPosition = FormStartPosition.CenterParent;
			int iconWidth = icon == MessageBoxIcon.None ? 0 : 44;
			SizeF textSize = MeasureText(text, DefaultFont, 420);
			int textWidth = Math.Max(160, (int)Math.Ceiling(textSize.Width) + 4);
			int textHeight = Math.Max(32, (int)Math.Ceiling(textSize.Height) + 4);
			Label label = new Label
			{
				Text = text,
				AutoSize = false,
				Location = new Point(16 + iconWidth, 18),
				Size = new Size(textWidth, textHeight)
			};
			Controls.Add(label);
			DialogResult[] results;
			string[] names;
			switch (buttons)
			{
				case MessageBoxButtons.OKCancel: results = new[] { DialogResult.OK, DialogResult.Cancel }; break;
				case MessageBoxButtons.AbortRetryIgnore: results = new[] { DialogResult.Abort, DialogResult.Retry, DialogResult.Ignore }; break;
				case MessageBoxButtons.YesNoCancel: results = new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel }; break;
				case MessageBoxButtons.YesNo: results = new[] { DialogResult.Yes, DialogResult.No }; break;
				case MessageBoxButtons.RetryCancel: results = new[] { DialogResult.Retry, DialogResult.Cancel }; break;
				default: results = new[] { DialogResult.OK }; break;
			}

			names = results.Select(r => r.ToString()).ToArray();
			int width = Math.Max(16 + iconWidth + textWidth + 16, results.Length * 84 + 24);
			int buttonsTop = 18 + textHeight + 18;
			int x = width - 12 - results.Length * 84;
			Button first = null;
			for (int i = 0; i < results.Length; i++)
			{
				Button button = new Button
				{
					Text = names[i],
					DialogResult = results[i],
					Location = new Point(x + i * 84, buttonsTop),
					Size = new Size(76, 24),
					TabIndex = i
				};
				Controls.Add(button);
				first = first ?? button;
				if (results[i] == DialogResult.Cancel || results[i] == DialogResult.No && results.Length == 2)
				{
					CancelButton = button;
				}
			}

			AcceptButton = first;
			ClientSize = new Size(width, buttonsTop + 24 + 12);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			if (icon == MessageBoxIcon.None)
			{
				return;
			}

			Color color;
			string glyph;
			switch (icon)
			{
				case MessageBoxIcon.Error: color = Color.FromArgb(232, 17, 35); glyph = "×"; break;
				case MessageBoxIcon.Warning: color = Color.FromArgb(255, 185, 0); glyph = "!"; break;
				case MessageBoxIcon.Question: color = Color.FromArgb(0, 120, 215); glyph = "?"; break;
				default: color = Color.FromArgb(0, 120, 215); glyph = "i"; break;
			}

			e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
			using (SolidBrush brush = new SolidBrush(color))
			{
				e.Graphics.FillEllipse(brush, 16, 16, 32, 32);
			}

			using (Font font = new Font(DefaultFont.FontFamily, 16f, FontStyle.Bold))
			{
				DrawText(e.Graphics, glyph, font, Color.White, new RectangleF(16, 16, 32, 32), ContentAlignment.MiddleCenter);
			}
		}
	}

	// --- file and folder dialogs ---

	public abstract class CommonDialog : Component
	{
		public object Tag { get; set; }

		public DialogResult ShowDialog() => ShowDialog(null);

		public abstract DialogResult ShowDialog(IWin32Window owner);

		public virtual void Reset()
		{
		}
	}

	public abstract class FileDialog : CommonDialog
	{
		public string FileName { get; set; } = string.Empty;

		public string[] FileNames { get; protected set; } = new string[0];

		public string Filter { get; set; } = string.Empty;

		public int FilterIndex { get; set; } = 1;

		public string InitialDirectory { get; set; } = string.Empty;

		public string Title { get; set; } = string.Empty;

		public string DefaultExt { get; set; } = string.Empty;

		public bool AddExtension { get; set; } = true;

		public bool CheckFileExists { get; set; }

		public bool CheckPathExists { get; set; } = true;

		public bool RestoreDirectory { get; set; }

		public bool ValidateNames { get; set; } = true;

		public bool DereferenceLinks { get; set; } = true;

		public bool ShowHelp { get; set; }

		public bool SupportMultiDottedExtensions { get; set; }

		public event CancelEventHandler FileOk;

		internal abstract bool Saving { get; }

		internal virtual bool MultiselectFiles => false;

		public override DialogResult ShowDialog(IWin32Window owner)
		{
			if (Host.InvokeRequired)
			{
				return (DialogResult)Host.Invoke(() => ShowDialog(owner));
			}

			if (Host.NativeActive)
			{
				string[] chosen = Saving ? (Host.NativeSave(this) is string path ? new[] { path } : null) : Host.NativeOpen(this);
				if (chosen == null)
				{
					return DialogResult.Cancel;
				}

				FileName = chosen.Length > 0 ? chosen[0] : string.Empty;
				FileNames = chosen;
				CancelEventArgs e = new CancelEventArgs();
				FileOk?.Invoke(this, e);
				return e.Cancel ? DialogResult.Cancel : DialogResult.OK;
			}

			using (FileBrowserForm form = new FileBrowserForm(this, false))
			{
				DialogResult result = form.ShowDialog(owner);
				if (result == DialogResult.OK)
				{
					FileName = form.Chosen.Length > 0 ? form.Chosen[0] : string.Empty;
					FileNames = form.Chosen;
					CancelEventArgs e = new CancelEventArgs();
					FileOk?.Invoke(this, e);
					if (e.Cancel)
					{
						return DialogResult.Cancel;
					}
				}

				return result;
			}
		}

		/// <summary>The filter's entries: a description and its patterns.</summary>
		internal List<KeyValuePair<string, string[]>> ParseFilter()
		{
			List<KeyValuePair<string, string[]>> filters = new List<KeyValuePair<string, string[]>>();
			string[] parts = (Filter ?? string.Empty).Split('|');
			for (int i = 0; i + 1 < parts.Length; i += 2)
			{
				filters.Add(new KeyValuePair<string, string[]>(parts[i], parts[i + 1].Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray()));
			}

			if (filters.Count == 0)
			{
				filters.Add(new KeyValuePair<string, string[]>("*", new[] { "*" }));
			}

			return filters;
		}
	}

	public class OpenFileDialog : FileDialog
	{
		public OpenFileDialog()
		{
			CheckFileExists = true;
		}

		public bool Multiselect { get; set; }

		public bool ReadOnlyChecked { get; set; }

		public bool ShowReadOnly { get; set; }

		internal override bool Saving => false;

		internal override bool MultiselectFiles => Multiselect;

		public Stream OpenFile() => File.OpenRead(FileName);
	}

	public class SaveFileDialog : FileDialog
	{
		public bool OverwritePrompt { get; set; } = true;

		public bool CreatePrompt { get; set; }

		internal override bool Saving => true;

		public Stream OpenFile() => File.Create(FileName);
	}

	public class FolderBrowserDialog : CommonDialog
	{
		public string SelectedPath { get; set; } = string.Empty;

		public string Description { get; set; } = string.Empty;

		public bool ShowNewFolderButton { get; set; } = true;

		public Environment.SpecialFolder RootFolder { get; set; } = Environment.SpecialFolder.Desktop;

		public bool UseDescriptionForTitle { get; set; }

		public override DialogResult ShowDialog(IWin32Window owner)
		{
			if (Host.InvokeRequired)
			{
				return (DialogResult)Host.Invoke(() => ShowDialog(owner));
			}

			if (Host.NativeActive)
			{
				string folder = Host.NativeFolder(this);
				if (folder == null)
				{
					return DialogResult.Cancel;
				}

				SelectedPath = folder;
				return DialogResult.OK;
			}

			using (FileBrowserForm form = new FileBrowserForm(this))
			{
				DialogResult result = form.ShowDialog(owner);
				if (result == DialogResult.OK)
				{
					SelectedPath = form.Chosen.Length > 0 ? form.Chosen[0] : SelectedPath;
				}

				return result;
			}
		}
	}

	/// <summary>Windows' file and folder choosers, as a form: the places on the left, the folder's contents, a name and a filter.</summary>
	internal sealed class FileBrowserForm : Form
	{
		private readonly FileDialog fileDialog;
		private readonly bool folders;
		private readonly TextBox pathBox = new TextBox();
		private readonly Button upButton = new Button();
		private readonly ListView places = new ListView();
		private readonly ListView list = new ListView();
		private readonly TextBox nameBox = new TextBox();
		private readonly ComboBox filterBox = new ComboBox();
		private readonly Button okButton = new Button();
		private readonly Button cancelButton = new Button();
		private readonly Button newFolderButton = new Button();
		private readonly List<KeyValuePair<string, string[]>> filters;
		private string current;

		internal string[] Chosen = new string[0];

		internal FileBrowserForm(FileDialog dialog, bool unused)
		{
			fileDialog = dialog;
			filters = dialog.ParseFilter();
			Text = !string.IsNullOrEmpty(dialog.Title) ? dialog.Title : dialog.Saving ? "Save As" : "Open";
			Build(dialog.Saving ? "Save" : "Open");
			string start = !string.IsNullOrEmpty(dialog.InitialDirectory) ? dialog.InitialDirectory : !string.IsNullOrEmpty(dialog.FileName) ? SafeDirectory(dialog.FileName) : null;
			nameBox.Text = string.IsNullOrEmpty(dialog.FileName) ? string.Empty : System.IO.Path.GetFileName(dialog.FileName);
			foreach (KeyValuePair<string, string[]> filter in filters)
			{
				filterBox.Items.Add(filter.Key);
			}

			filterBox.SelectedIndex = Math.Max(0, Math.Min(filters.Count - 1, dialog.FilterIndex - 1));
			Navigate(start ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal));
		}

		internal FileBrowserForm(FolderBrowserDialog dialog)
		{
			folders = true;
			filters = new List<KeyValuePair<string, string[]>>();
			Text = !string.IsNullOrEmpty(dialog.Description) ? dialog.Description : "Select Folder";
			Build("Select Folder");
			newFolderButton.Visible = dialog.ShowNewFolderButton;
			Navigate(!string.IsNullOrEmpty(dialog.SelectedPath) && Directory.Exists(dialog.SelectedPath) ? dialog.SelectedPath : Environment.GetFolderPath(Environment.SpecialFolder.Personal));
		}

		private static string SafeDirectory(string file)
		{
			try
			{
				return System.IO.Path.GetDirectoryName(file);
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		private void Build(string okText)
		{
			BackColor = Color.White;
			ClientSize = new Size(720, 460);
			upButton.Text = "Up";
			upButton.SetBounds(12, 12, 52, 24);
			upButton.Click += (s, e) =>
			{
				DirectoryInfo parent = current == null ? null : Directory.GetParent(current);
				if (parent != null)
				{
					Navigate(parent.FullName);
				}
			};
			pathBox.SetBounds(70, 14, 638, 20);
			pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			pathBox.KeyDown += (s, e) =>
			{
				if (e.KeyCode == Keys.Return)
				{
					Navigate(pathBox.Text);
					e.Handled = true;
				}
			};
			places.SetBounds(12, 44, 150, 340);
			places.View = View.Details;
			places.HeaderStyle = ColumnHeaderStyle.None;
			places.FullRowSelect = true;
			places.Columns.Add("Place", 146);
			places.SelectedIndexChanged += (s, e) =>
			{
				if (places.SelectedItems.Count > 0)
				{
					Navigate((string)places.SelectedItems[0].Tag);
				}
			};
			foreach (KeyValuePair<string, string> place in Places())
			{
				places.Items.Add(new ListViewItem(place.Key) { Tag = place.Value });
			}

			list.SetBounds(170, 44, 538, 340);
			list.SmallImageList = Icons;
			list.View = View.Details;
			list.FullRowSelect = true;
			list.MultiSelect = fileDialog != null && fileDialog.MultiselectFiles;
			list.Columns.Add("Name", 300);
			list.Columns.Add("Size", 80);
			list.Columns.Add("Modified", 140);
			list.DoubleClick += (s, e) => Open();
			list.SelectedIndexChanged += (s, e) =>
			{
				if (list.SelectedItems.Count > 0 && !(bool)((object[])list.SelectedItems[0].Tag)[1] != folders)
				{
					nameBox.Text = list.SelectedItems[0].Text;
				}
			};
			list.KeyDown += (s, e) =>
			{
				if (e.KeyCode == Keys.Return)
				{
					Open();
					e.Handled = true;
				}
				else if (e.KeyCode == Keys.Back)
				{
					upButton.PerformClick();
					e.Handled = true;
				}
			};
			Label nameLabel = new Label { Text = folders ? "Folder:" : "File name:", AutoSize = false, Location = new Point(170, 396), Size = new Size(80, 20), TextAlign = ContentAlignment.MiddleLeft };
			nameBox.SetBounds(254, 396, folders ? 454 : 270, 20);
			filterBox.SetBounds(532, 396, 176, 21);
			filterBox.DropDownStyle = ComboBoxStyle.DropDownList;
			filterBox.Visible = !folders;
			filterBox.SelectedIndexChanged += (s, e) => Navigate(current);
			newFolderButton.Text = "New Folder";
			newFolderButton.SetBounds(12, 426, 100, 24);
			newFolderButton.Visible = false;
			newFolderButton.Click += (s, e) => NewFolder();
			okButton.Text = okText;
			okButton.SetBounds(520, 426, 92, 24);
			okButton.Click += (s, e) => Accept();
			cancelButton.Text = "Cancel";
			cancelButton.DialogResult = DialogResult.Cancel;
			cancelButton.SetBounds(616, 426, 92, 24);
			Controls.AddRange(new Control[] { upButton, pathBox, places, list, nameLabel, nameBox, filterBox, newFolderButton, okButton, cancelButton });
			AcceptButton = null;
			CancelButton = cancelButton;
			nameBox.KeyDown += (s, e) =>
			{
				if (e.KeyCode == Keys.Return)
				{
					Accept();
					e.Handled = true;
				}
			};
		}

		private static ImageList icons;

		/// <summary>A folder's and a file's icon, drawn.</summary>
		private static ImageList Icons
		{
			get
			{
				if (icons != null)
				{
					return icons;
				}

				icons = new ImageList();
				Bitmap folder = new Bitmap(16, 16);
				using (Graphics g = Graphics.FromImage(folder))
				{
					using (SolidBrush tab = new SolidBrush(Color.FromArgb(230, 180, 60)))
					using (SolidBrush body = new SolidBrush(Color.FromArgb(252, 210, 100)))
					{
						g.FillRectangle(tab, 1, 3, 6, 3);
						g.FillRectangle(body, 1, 5, 14, 9);
					}
				}

				Bitmap file = new Bitmap(16, 16);
				using (Graphics g = Graphics.FromImage(file))
				{
					g.FillRectangle(Brushes.White, 3, 1, 10, 14);
					g.DrawRectangle(Pens.Gray, 3, 1, 9, 13);
					g.DrawLine(Pens.LightGray, 5, 5, 10, 5);
					g.DrawLine(Pens.LightGray, 5, 8, 10, 8);
					g.DrawLine(Pens.LightGray, 5, 11, 10, 11);
				}

				icons.Images.Add("folder", folder);
				icons.Images.Add("file", file);
				return icons;
			}
		}

		/// <summary>The places offered on the left: the home folders, then the drives or volumes.</summary>
		private static IEnumerable<KeyValuePair<string, string>> Places()
		{
			List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
			void Add(string name, string path)
			{
				if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && result.All(r => r.Value != path))
				{
					result.Add(new KeyValuePair<string, string>(name, path));
				}
			}

			Add("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
			Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
			Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.Personal));
			Add("OpenBVE data", Program.FileSystem?.DataFolder);
			Add("Routes", Program.FileSystem?.InitialRouteFolder);
			Add("Trains", Program.FileSystem?.InitialTrainFolder);
			try
			{
				foreach (DriveInfo drive in DriveInfo.GetDrives())
				{
					if (drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable || drive.DriveType == DriveType.Network))
					{
						Add(drive.Name, drive.RootDirectory.FullName);
					}
				}
			}
			catch (Exception)
			{
				// a drive that cannot be asked is left out
			}

			if (Directory.Exists("/Volumes"))
			{
				foreach (string volume in Directory.GetDirectories("/Volumes"))
				{
					Add(System.IO.Path.GetFileName(volume), volume);
				}
			}

			return result;
		}

		private bool Matches(string file)
		{
			if (filters.Count == 0)
			{
				return true;
			}

			string[] patterns = filters[Math.Max(0, filterBox.SelectedIndex)].Value;
			string name = System.IO.Path.GetFileName(file);
			foreach (string pattern in patterns)
			{
				if (pattern == "*" || pattern == "*.*")
				{
					return true;
				}

				if (pattern.StartsWith("*", StringComparison.Ordinal) && name.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}

				if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		private void Navigate(string path)
		{
			if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
			{
				return;
			}

			string[] directories, files;
			try
			{
				directories = Directory.GetDirectories(path);
				files = folders ? new string[0] : Directory.GetFiles(path);
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
			{
				MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			current = System.IO.Path.GetFullPath(path);
			pathBox.Text = current;
			list.BeginUpdate();
			list.Items.Clear();
			foreach (string directory in directories.OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase))
			{
				string name = System.IO.Path.GetFileName(directory);
				if (name.StartsWith(".", StringComparison.Ordinal))
				{
					continue;
				}

				ListViewItem item = new ListViewItem(name, "folder") { Tag = new object[] { directory, true } };
				item.SubItems.Add(string.Empty);
				item.SubItems.Add(SafeTime(directory, true));
				list.Items.Add(item);
			}

			foreach (string file in files.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
			{
				string name = System.IO.Path.GetFileName(file);
				if (name.StartsWith(".", StringComparison.Ordinal) || !Matches(file))
				{
					continue;
				}

				ListViewItem item = new ListViewItem(name, "file") { Tag = new object[] { file, false } };
				long length = 0;
				try
				{
					length = new FileInfo(file).Length;
				}
				catch (Exception)
				{
					// shown without its size
				}

				item.SubItems.Add(length >= 1048576 ? (length / 1048576.0).ToString("0.0") + " MB" : Math.Ceiling(length / 1024.0) + " KB");
				item.SubItems.Add(SafeTime(file, false));
				list.Items.Add(item);
			}

			list.EndUpdate();
			if (folders)
			{
				nameBox.Text = current;
			}
		}

		private static string SafeTime(string path, bool directory)
		{
			try
			{
				return (directory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path)).ToString("yyyy-MM-dd HH:mm");
			}
			catch (Exception)
			{
				return string.Empty;
			}
		}

		private void Open()
		{
			if (list.SelectedItems.Count == 0)
			{
				return;
			}

			object[] tag = (object[])list.SelectedItems[0].Tag;
			if ((bool)tag[1])
			{
				Navigate((string)tag[0]);
			}
			else
			{
				Accept();
			}
		}

		private void NewFolder()
		{
			if (current == null)
			{
				return;
			}

			string name = "New Folder";
			for (int i = 2; Directory.Exists(System.IO.Path.Combine(current, name)); i++)
			{
				name = "New Folder (" + i + ")";
			}

			try
			{
				Directory.CreateDirectory(System.IO.Path.Combine(current, name));
				Navigate(current);
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
			{
				MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}

		private void Accept()
		{
			if (folders)
			{
				string folder = list.SelectedItems.Count > 0 ? (string)((object[])list.SelectedItems[0].Tag)[0] : nameBox.Text.Trim();
				if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
				{
					folder = current;
				}

				Chosen = new[] { folder };
				DialogResult = DialogResult.OK;
				return;
			}

			if (list.MultiSelect && list.SelectedItems.Count > 1)
			{
				Chosen = list.SelectedItems.Cast<ListViewItem>().Select(i => (string)((object[])i.Tag)[0]).Where(File.Exists).ToArray();
				DialogResult = DialogResult.OK;
				return;
			}

			string name = nameBox.Text.Trim();
			if (name.Length == 0 || current == null)
			{
				return;
			}

			string file;
			try
			{
				file = System.IO.Path.IsPathRooted(name) ? name : System.IO.Path.Combine(current, name);
			}
			catch (ArgumentException)
			{
				return;
			}

			if (Directory.Exists(file))
			{
				Navigate(file);
				nameBox.Text = string.Empty;
				return;
			}

			if (fileDialog.Saving)
			{
				if (fileDialog.AddExtension && string.IsNullOrEmpty(System.IO.Path.GetExtension(file)))
				{
					string extension = fileDialog.DefaultExt;
					if (string.IsNullOrEmpty(extension) && filters.Count > 0)
					{
						string pattern = filters[Math.Max(0, filterBox.SelectedIndex)].Value.FirstOrDefault(p => p.StartsWith("*.", StringComparison.Ordinal) && p != "*.*");
						extension = pattern?.Substring(2);
					}

					if (!string.IsNullOrEmpty(extension))
					{
						file += "." + extension.TrimStart('.');
					}
				}

				if (File.Exists(file) && ((SaveFileDialog)fileDialog).OverwritePrompt &&
				    MessageBox.Show(System.IO.Path.GetFileName(file) + " already exists.\nDo you want to replace it?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
				{
					return;
				}
			}
			else if (fileDialog.CheckFileExists && !File.Exists(file))
			{
				MessageBox.Show(System.IO.Path.GetFileName(file) + "\nFile not found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			Chosen = new[] { file };
			DialogResult = DialogResult.OK;
		}
	}

	// --- context menus ---

	public abstract class ToolStripItem : Component
	{
		private string text = string.Empty;

		public string Text
		{
			get { return text; }
			set { text = value ?? string.Empty; }
		}

		public string Name { get; set; } = string.Empty;

		public Size Size { get; set; }

		public bool Enabled { get; set; } = true;

		public bool Visible { get; set; } = true;

		public Image Image { get; set; }

		public object Tag { get; set; }

		public string ToolTipText { get; set; }

		/// <summary>The menu the item is in.</summary>
		public Control Owner { get; internal set; }

		public event EventHandler Click;

		internal void PerformClickInternal() => Click?.Invoke(this, EventArgs.Empty);

		public void PerformClick() => PerformClickInternal();
	}

	public class ToolStripMenuItem : ToolStripItem
	{
		public ToolStripMenuItem()
		{
		}

		public ToolStripMenuItem(string text)
		{
			Text = text;
		}

		public ToolStripMenuItem(string text, Image image, EventHandler onClick)
		{
			Text = text;
			Image = image;
			if (onClick != null)
			{
				Click += onClick;
			}
		}

		public bool Checked { get; set; }

		public Keys ShortcutKeys { get; set; }
	}

	public class ToolStripSeparator : ToolStripItem
	{
	}

	public class ToolStripItemCollection : IEnumerable<ToolStripItem>
	{
		private readonly List<ToolStripItem> items = new List<ToolStripItem>();
		private readonly Control owner;

		internal ToolStripItemCollection(Control owner)
		{
			this.owner = owner;
		}

		public int Count => items.Count;

		public ToolStripItem this[int index] => items[index];

		public int Add(ToolStripItem item)
		{
			item.Owner = owner;
			items.Add(item);
			return items.Count - 1;
		}

		public ToolStripItem Add(string text)
		{
			ToolStripMenuItem item = new ToolStripMenuItem(text) { Owner = owner };
			items.Add(item);
			return item;
		}

		public void AddRange(ToolStripItem[] range)
		{
			foreach (ToolStripItem item in range)
			{
				Add(item);
			}
		}

		public void Clear() => items.Clear();

		public void Remove(ToolStripItem item) => items.Remove(item);

		public IEnumerator<ToolStripItem> GetEnumerator() => items.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public class ContextMenuStrip : Control
	{
		public ContextMenuStrip()
		{
			Items = new ToolStripItemCollection(this);
		}

		public ContextMenuStrip(IContainer container) : this()
		{
			container?.Add(this);
		}

		public ToolStripItemCollection Items { get; }

		public bool ShowImageMargin { get; set; } = true;

		public Control SourceControl { get; private set; }

		public event CancelEventHandler Opening;

		public void Show(Control control, Point position)
		{
			CancelEventArgs e = new CancelEventArgs();
			SourceControl = control;
			Opening?.Invoke(this, e);
			if (e.Cancel || Items.Count == 0)
			{
				return;
			}

			if (Host.NativeActive)
			{
				Host.NativeMenu(this, control, position);
				return;
			}

			Point at = control.PointToScreen(position);
			MenuPopup popup = new MenuPopup(this);
			popup.Location = at;
			popup.FlipTop = at.Y;
			Host.ShowPopup(popup);
		}

		public void Show(Point position)
		{
			MenuPopup popup = new MenuPopup(this);
			popup.Location = position;
			popup.FlipTop = position.Y;
			Host.ShowPopup(popup);
		}
	}

	internal sealed class MenuPopup : Popup
	{
		private const int ItemHeight = 22;
		private readonly List<ToolStripItem> items;
		private int hot = -1;

		internal MenuPopup(ContextMenuStrip menu)
		{
			items = menu.Items.Where(i => i.Visible).ToList();
			int width = 120;
			foreach (ToolStripItem item in items)
			{
				width = Math.Max(width, (int)MeasureText(item.Text, DefaultFont).Width + 48);
			}

			Size = new Size(width, items.Count * ItemHeight + 4);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			g.FillRectangle(Brushes.White, 0, 0, Width, Height);
			DrawBorder(g, ClientRectangle, Color.FromArgb(160, 160, 160));
			for (int i = 0; i < items.Count; i++)
			{
				Rectangle row = new Rectangle(2, 2 + i * ItemHeight, Width - 4, ItemHeight);
				if (items[i] is ToolStripSeparator)
				{
					g.DrawLine(Pens.LightGray, row.Left + 4, row.Top + ItemHeight / 2, row.Right - 4, row.Top + ItemHeight / 2);
					continue;
				}

				if (i == hot && items[i].Enabled)
				{
					using (SolidBrush brush = new SolidBrush(Color.FromArgb(204, 232, 255)))
					{
						g.FillRectangle(brush, row);
					}
				}

				DrawText(g, items[i].Text, DefaultFont, items[i].Enabled ? Color.Black : GrayTextColor, new RectangleF(row.Left + 28, row.Top, row.Width - 32, row.Height), ContentAlignment.MiddleLeft);
			}
		}

		private int ItemAt(Point p) => p.Y < 2 ? -1 : Math.Min(items.Count - 1, (p.Y - 2) / ItemHeight);

		protected override void OnMouseMove(MouseEventArgs e)
		{
			int item = ItemAt(e.Location);
			if (item != hot)
			{
				hot = item;
				Invalidate();
			}
		}

		protected override void OnClick(EventArgs e)
		{
			if (hot >= 0 && hot < items.Count && items[hot].Enabled && !(items[hot] is ToolStripSeparator))
			{
				ToolStripItem item = items[hot];
				Host.ClosePopup();
				item.PerformClickInternal();
			}
		}

		internal override bool HandleKey(KeyEventArgs e)
		{
			switch (e.KeyCode)
			{
				case Keys.Escape:
					Host.ClosePopup();
					return true;
				case Keys.Down:
					hot = Math.Min(items.Count - 1, hot + 1);
					Invalidate();
					return true;
				case Keys.Up:
					hot = Math.Max(0, hot - 1);
					Invalidate();
					return true;
				case Keys.Return:
					OnClick(EventArgs.Empty);
					return true;
				default:
					return false;
			}
		}
	}

	// --- tool tips and timers ---

	public class ToolTip : Component
	{
		private static readonly Dictionary<Control, string> Texts = new Dictionary<Control, string>();

		public ToolTip()
		{
		}

		public ToolTip(IContainer container)
		{
			container?.Add(this);
		}

		public int AutoPopDelay { get; set; } = 5000;

		public int InitialDelay { get; set; } = 500;

		public int ReshowDelay { get; set; } = 100;

		public int AutomaticDelay { get; set; } = 500;

		public bool ShowAlways { get; set; }

		public bool Active { get; set; } = true;

		public bool IsBalloon { get; set; }

		public string ToolTipTitle { get; set; }

		public void SetToolTip(Control control, string caption)
		{
			if (control == null)
			{
				return;
			}

			if (string.IsNullOrEmpty(caption))
			{
				Texts.Remove(control);
			}
			else
			{
				Texts[control] = caption;
			}
		}

		public string GetToolTip(Control control) => control != null && Texts.TryGetValue(control, out string text) ? text : string.Empty;

		public void RemoveAll()
		{
			Texts.Clear();
		}

		internal static string TextFor(Control control)
		{
			for (Control c = control; c != null; c = c.Parent)
			{
				if (Texts.TryGetValue(c, out string text))
				{
					return text;
				}
			}

			return null;
		}
	}

	public class Timer : Component
	{
		private bool enabled;

		public Timer()
		{
		}

		public Timer(IContainer container)
		{
			container?.Add(this);
		}

		public int Interval { get; set; } = 100;

		internal long NextTick;

		public bool Enabled
		{
			get { return enabled; }
			set
			{
				enabled = value;
				if (value)
				{
					NextTick = Host.Now + Math.Max(1, Interval);
					if (!Host.Timers.Contains(this))
					{
						Host.Timers.Add(this);
					}
				}
				else
				{
					Host.Timers.Remove(this);
				}
			}
		}

		public object Tag { get; set; }

		public event EventHandler Tick;

		internal void RaiseTick() => Tick?.Invoke(this, EventArgs.Empty);

		public void Start() => Enabled = true;

		public void Stop() => Enabled = false;

		protected override void Dispose(bool disposing)
		{
			Enabled = false;
			base.Dispose(disposing);
		}
	}
}
