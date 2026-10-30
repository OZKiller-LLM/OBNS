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

// OBNS: not an upstream file. The in-window main menu (OBNS/GLFORMS.md): the enumerations, event data and delegates of
// System.Windows.Forms that upstream's forms use, with the same names and values, so the forms' copies compile against
// OBNS's controls unchanged.

using System;
using System.ComponentModel;
using System.Drawing;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	[Flags]
	public enum AnchorStyles { None = 0, Top = 1, Bottom = 2, Left = 4, Right = 8 }

	public enum DockStyle { None, Top, Bottom, Left, Right, Fill }

	public enum FormWindowState { Normal, Minimized, Maximized }

	public enum FormStartPosition { Manual, CenterScreen, WindowsDefaultLocation, WindowsDefaultBounds, CenterParent }

	public enum FormBorderStyle { None, FixedSingle, Fixed3D, FixedDialog, Sizable, FixedToolWindow, SizableToolWindow }

	public enum AutoScaleMode { None, Font, Dpi, Inherit }

	public enum BorderStyle { None, FixedSingle, Fixed3D }

	public enum FlatStyle { Flat, Popup, Standard, System }

	public enum Appearance { Normal, Button }

	public enum CheckState { Unchecked, Checked, Indeterminate }

	public enum HorizontalAlignment { Left, Right, Center }

	public enum View { LargeIcon, Details, SmallIcon, List, Tile }

	public enum ColumnHeaderStyle { None, Nonclickable, Clickable }

	public enum ColumnHeaderAutoResizeStyle { None, HeaderSize, ColumnContent }

	public enum SortOrder { None, Ascending, Descending }

	public enum ComboBoxStyle { Simple, DropDown, DropDownList }

	public enum DrawMode { Normal, OwnerDrawFixed, OwnerDrawVariable }

	[Flags]
	public enum DrawItemState { None = 0, Selected = 1, Grayed = 2, Disabled = 4, Checked = 8, Focus = 16, Default = 32, HotLight = 64, Inactive = 128, NoAccelerator = 256, NoFocusRect = 512, ComboBoxEdit = 4096 }

	public enum PictureBoxSizeMode { Normal, StretchImage, AutoSize, CenterImage, Zoom }

	public enum ScrollBars { None, Horizontal, Vertical, Both }

	public enum RichTextBoxScrollBars { None, Horizontal, Vertical, Both, ForcedHorizontal = 17, ForcedVertical = 18, ForcedBoth = 19 }

	public enum TickStyle { None, TopLeft, BottomRight, Both }

	public enum Orientation { Horizontal, Vertical }

	public enum LinkBehavior { SystemDefault, AlwaysUnderline, HoverUnderline, NeverUnderline }

	public enum ImeMode { Inherit = -1, NoControl = 0, On = 1, Off = 2, Disable = 3 }

	public enum RightToLeft { No, Yes, Inherit }

	public enum ImageLayout { None, Tile, Center, Stretch, Zoom }

	public enum TabAlignment { Top, Bottom, Left, Right }

	public enum TabSizeMode { Normal, FillToRight, Fixed }

	public enum DataGridViewAutoSizeColumnsMode { None = 1, ColumnHeader = 2, AllCellsExceptHeader = 4, AllCells = 6, DisplayedCellsExceptHeader = 8, DisplayedCells = 10, Fill = 16 }

	public enum DataGridViewAutoSizeColumnMode { NotSet = 0, None = 1, ColumnHeader = 2, AllCellsExceptHeader = 4, AllCells = 6, DisplayedCellsExceptHeader = 8, DisplayedCells = 10, Fill = 16 }

	public enum DataGridViewSelectionMode { CellSelect, FullRowSelect, FullColumnSelect, RowHeaderSelect, ColumnHeaderSelect }

	public enum DataGridViewColumnHeadersHeightSizeMode { EnableResizing, DisableResizing, AutoSize }

	public enum DataGridViewColumnSortMode { NotSortable, Automatic, Programmatic }

	public enum DialogResult { None, OK, Cancel, Abort, Retry, Ignore, Yes, No }

	public enum MessageBoxButtons { OK, OKCancel, AbortRetryIgnore, YesNoCancel, YesNo, RetryCancel }

	public enum MessageBoxIcon { None = 0, Hand = 16, Stop = 16, Error = 16, Question = 32, Exclamation = 48, Warning = 48, Asterisk = 64, Information = 64 }

	public enum MessageBoxDefaultButton { Button1 = 0, Button2 = 256, Button3 = 512 }

	public enum CloseReason { None, WindowsShutDown, MdiFormClosing, UserClosing, TaskManagerClosing, FormOwnerClosing, ApplicationExitCall }

	[Flags]
	public enum DragDropEffects { None = 0, Copy = 1, Move = 2, Link = 4, Scroll = int.MinValue, All = Copy | Move | Scroll }

	[Flags]
	public enum MouseButtons { None = 0, Left = 0x100000, Right = 0x200000, Middle = 0x400000, XButton1 = 0x800000, XButton2 = 0x1000000 }

	/// <summary>The keys, with System.Windows.Forms' values.</summary>
	[Flags]
	public enum Keys
	{
		None = 0, Back = 8, Tab = 9, Enter = 13, Return = 13, ShiftKey = 16, ControlKey = 17, Menu = 18, Pause = 19, CapsLock = 20,
		Escape = 27, Space = 32, PageUp = 33, Prior = 33, PageDown = 34, Next = 34, End = 35, Home = 36, Left = 37, Up = 38,
		Right = 39, Down = 40, Insert = 45, Delete = 46,
		D0 = 48, D1, D2, D3, D4, D5, D6, D7, D8, D9,
		A = 65, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
		NumPad0 = 96, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
		Multiply = 106, Add = 107, Separator = 108, Subtract = 109, Decimal = 110, Divide = 111,
		F1 = 112, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
		KeyCode = 0xFFFF, Shift = 0x10000, Control = 0x20000, Alt = 0x40000, Modifiers = unchecked((int)0xFFFF0000)
	}

	public struct Padding
	{
		public int Left, Top, Right, Bottom;

		public Padding(int all)
		{
			Left = Top = Right = Bottom = all;
		}

		public Padding(int left, int top, int right, int bottom)
		{
			Left = left;
			Top = top;
			Right = right;
			Bottom = bottom;
		}

		public int All
		{
			get { return Left; }
			set { Left = Top = Right = Bottom = value; }
		}

		public int Horizontal => Left + Right;

		public int Vertical => Top + Bottom;

		public static readonly Padding Empty = new Padding(0);
	}

	public delegate void MethodInvoker();

	public delegate void KeyEventHandler(object sender, KeyEventArgs e);

	public delegate void KeyPressEventHandler(object sender, KeyPressEventArgs e);

	public delegate void MouseEventHandler(object sender, MouseEventArgs e);

	public delegate void PaintEventHandler(object sender, PaintEventArgs e);

	public delegate void FormClosingEventHandler(object sender, FormClosingEventArgs e);

	public delegate void FormClosedEventHandler(object sender, FormClosedEventArgs e);

	public delegate void DragEventHandler(object sender, DragEventArgs e);

	public delegate void DrawItemEventHandler(object sender, DrawItemEventArgs e);

	public delegate void LinkLabelLinkClickedEventHandler(object sender, LinkLabelLinkClickedEventArgs e);

	public delegate void LinkClickedEventHandler(object sender, LinkClickedEventArgs e);

	public delegate void DataGridViewCellEventHandler(object sender, DataGridViewCellEventArgs e);

	public delegate void ScrollEventHandler(object sender, ScrollEventArgs e);

	public class KeyEventArgs : EventArgs
	{
		public KeyEventArgs(Keys keyData)
		{
			KeyData = keyData;
		}

		public Keys KeyData { get; }

		public Keys KeyCode => KeyData & Keys.KeyCode;

		public Keys Modifiers => KeyData & Keys.Modifiers;

		public int KeyValue => (int)KeyCode;

		public bool Shift => (KeyData & Keys.Shift) != 0;

		public bool Control => (KeyData & Keys.Control) != 0;

		public bool Alt => (KeyData & Keys.Alt) != 0;

		public bool Handled { get; set; }

		private bool suppress;

		public bool SuppressKeyPress
		{
			get { return suppress; }
			set
			{
				suppress = value;
				Handled = value;
			}
		}
	}

	public class KeyPressEventArgs : EventArgs
	{
		public KeyPressEventArgs(char keyChar)
		{
			KeyChar = keyChar;
		}

		public char KeyChar { get; set; }

		public bool Handled { get; set; }
	}

	public class MouseEventArgs : EventArgs
	{
		public MouseEventArgs(MouseButtons button, int clicks, int x, int y, int delta)
		{
			Button = button;
			Clicks = clicks;
			X = x;
			Y = y;
			Delta = delta;
		}

		public MouseButtons Button { get; }

		public int Clicks { get; }

		public int X { get; }

		public int Y { get; }

		public int Delta { get; }

		public Point Location => new Point(X, Y);
	}

	public class PaintEventArgs : EventArgs, IDisposable
	{
		public PaintEventArgs(Graphics graphics, Rectangle clipRectangle)
		{
			Graphics = graphics;
			ClipRectangle = clipRectangle;
		}

		public Graphics Graphics { get; }

		public Rectangle ClipRectangle { get; }

		public void Dispose()
		{
		}
	}

	public class FormClosingEventArgs : CancelEventArgs
	{
		public FormClosingEventArgs(CloseReason closeReason, bool cancel) : base(cancel)
		{
			CloseReason = closeReason;
		}

		public CloseReason CloseReason { get; }
	}

	public class FormClosedEventArgs : EventArgs
	{
		public FormClosedEventArgs(CloseReason closeReason)
		{
			CloseReason = closeReason;
		}

		public CloseReason CloseReason { get; }
	}

	public interface IDataObject
	{
		object GetData(string format);

		object GetData(Type format);

		bool GetDataPresent(string format);

		bool GetDataPresent(Type format);

		string[] GetFormats();
	}

	/// <summary>The files dropped on the window.</summary>
	internal sealed class FileDropData : IDataObject
	{
		private readonly string[] files;

		internal FileDropData(string[] files)
		{
			this.files = files;
		}

		public object GetData(string format) => format == DataFormats.FileDrop ? files : null;

		public object GetData(Type format) => format == typeof(string[]) ? files : null;

		public bool GetDataPresent(string format) => format == DataFormats.FileDrop;

		public bool GetDataPresent(Type format) => format == typeof(string[]);

		public string[] GetFormats() => new[] { DataFormats.FileDrop };
	}

	public static class DataFormats
	{
		public static readonly string FileDrop = "FileDrop";
		public static readonly string Text = "Text";
		public static readonly string UnicodeText = "UnicodeText";
	}

	public class DragEventArgs : EventArgs
	{
		public DragEventArgs(IDataObject data, int keyState, int x, int y, DragDropEffects allowedEffect, DragDropEffects effect)
		{
			Data = data;
			KeyState = keyState;
			X = x;
			Y = y;
			AllowedEffect = allowedEffect;
			Effect = effect;
		}

		public IDataObject Data { get; }

		public int KeyState { get; }

		public int X { get; }

		public int Y { get; }

		public DragDropEffects AllowedEffect { get; }

		public DragDropEffects Effect { get; set; }
	}

	public class DrawItemEventArgs : EventArgs
	{
		public DrawItemEventArgs(Graphics graphics, Font font, Rectangle rect, int index, DrawItemState state, Color foreColor, Color backColor)
		{
			Graphics = graphics;
			Font = font;
			Bounds = rect;
			Index = index;
			State = state;
			ForeColor = foreColor;
			BackColor = backColor;
		}

		public Graphics Graphics { get; }

		public Font Font { get; }

		public Rectangle Bounds { get; }

		public int Index { get; }

		public DrawItemState State { get; }

		public Color ForeColor { get; }

		public Color BackColor { get; }

		public virtual void DrawBackground()
		{
			using (SolidBrush brush = new SolidBrush(BackColor))
			{
				Graphics.FillRectangle(brush, Bounds);
			}
		}

		public virtual void DrawFocusRectangle()
		{
		}
	}

	public class LinkLabelLinkClickedEventArgs : EventArgs
	{
		public LinkLabelLinkClickedEventArgs(LinkLabel.Link link) : this(link, MouseButtons.Left)
		{
		}

		public LinkLabelLinkClickedEventArgs(LinkLabel.Link link, MouseButtons button)
		{
			Link = link;
			Button = button;
		}

		public LinkLabel.Link Link { get; }

		public MouseButtons Button { get; }
	}

	public class LinkClickedEventArgs : EventArgs
	{
		public LinkClickedEventArgs(string linkText)
		{
			LinkText = linkText;
		}

		public string LinkText { get; }
	}

	public class DataGridViewCellEventArgs : EventArgs
	{
		public DataGridViewCellEventArgs(int columnIndex, int rowIndex)
		{
			ColumnIndex = columnIndex;
			RowIndex = rowIndex;
		}

		public int ColumnIndex { get; }

		public int RowIndex { get; }
	}

	public enum ScrollEventType { SmallDecrement, SmallIncrement, LargeDecrement, LargeIncrement, ThumbPosition, ThumbTrack, First, Last, EndScroll }

	public class ScrollEventArgs : EventArgs
	{
		public ScrollEventArgs(ScrollEventType type, int newValue)
		{
			Type = type;
			NewValue = newValue;
		}

		public ScrollEventType Type { get; }

		public int NewValue { get; set; }
	}

	/// <summary>
	/// The resources of the original form (formMain's for GLformMain): the copies' Designer code asks for them by its own
	/// type, but the compiled resources carry the original's name.
	/// </summary>
	public class ComponentResourceManager : System.Resources.ResourceManager
	{
		public ComponentResourceManager(Type type) : base(OriginalName(type), type.Assembly)
		{
		}

		private static string OriginalName(Type type)
		{
			string name = type.Name.StartsWith("GL", StringComparison.Ordinal) ? type.Name.Substring(2) : type.Name;
			return string.IsNullOrEmpty(type.Namespace) ? name : type.Namespace + "." + name;
		}

		/// <summary>Sets the properties a localized form keeps in its resources ("name.Property").</summary>
		public void ApplyResources(object value, string objectName)
		{
			System.Resources.ResourceSet set = GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true);
			if (set == null)
			{
				return;
			}

			foreach (System.Collections.DictionaryEntry entry in set)
			{
				string key = entry.Key as string;
				if (key == null || !key.StartsWith(objectName + ".", StringComparison.Ordinal))
				{
					continue;
				}

				System.Reflection.PropertyInfo property = value.GetType().GetProperty(key.Substring(objectName.Length + 1));
				if (property != null && property.CanWrite && (entry.Value == null || property.PropertyType.IsInstanceOfType(entry.Value)))
				{
					property.SetValue(value, entry.Value, null);
				}
			}
		}
	}
}
