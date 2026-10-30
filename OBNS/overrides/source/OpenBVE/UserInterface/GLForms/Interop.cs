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

// OBNS: not an upstream file. The in-window main menu (OBNS/GLFORMS.md): what upstream's forms ask of the rest of
// OpenBVE through Windows Forms types - the language and cursor lists fill a combo box, an input device plugin opens
// its settings over the form - done here for OBNS's controls. OBNS/glforms.ps1 points the copies' calls here.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using LibRender2;
using OpenBveApi.Interface;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	internal static class Interop
	{
		/// <summary>Upstream's language list (Translations.AvailableNewLanguages), which only OpenBveApi itself may use.</summary>
		private static IDictionary Languages => (IDictionary)typeof(Translations).GetField("AvailableNewLanguages", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

		private static string Field(object language, string name) => language?.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(language) as string;

		private static bool blockComboBox;

		/// <summary>As Translations.ListLanguages: every language, the current one chosen.</summary>
		internal static void ListLanguages(ComboBox comboboxLanguages)
		{
			blockComboBox = true;
			try
			{
				comboboxLanguages.Items.Clear();
				List<object> pairs = new List<object>();
				IDictionary languages = Languages;
				int index = -1;
				if (languages != null)
				{
					// In the order of their file names (the language codes), as Windows lists the folder for upstream; other
					// file systems (APFS, ext4) list it in no particular order.
					List<DictionaryEntry> entries = new List<DictionaryEntry>();
					foreach (DictionaryEntry entry in languages)
					{
						entries.Add(entry);
					}

					entries.Sort((x, y) => string.Compare((string)x.Key, (string)y.Key, StringComparison.OrdinalIgnoreCase));
					foreach (DictionaryEntry entry in entries)
					{
						// The items are the dictionary's own pairs, as upstream's binding gives them.
						Type pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(string), entry.Value.GetType());
						pairs.Add(Activator.CreateInstance(pairType, entry.Key, entry.Value));
						if ((string)entry.Key == Translations.CurrentLanguageCode)
						{
							index = pairs.Count - 1;
						}
					}
				}

				comboboxLanguages.DataSource = pairs;
				comboboxLanguages.DisplayMember = "Value";
				comboboxLanguages.ValueMember = "Key";
				if (index != -1)
				{
					comboboxLanguages.SelectedIndex = index;
				}
			}
			finally
			{
				blockComboBox = false;
			}
		}

		private static object SelectedLanguageObject(ComboBox comboboxLanguages)
		{
			object pair = comboboxLanguages.SelectedItem;
			return pair?.GetType().GetProperty("Value")?.GetValue(pair, null);
		}

		/// <summary>As Translations.SelectedLanguage: the chosen language becomes the current one, with its flag.</summary>
		internal static bool SelectedLanguage(string flagFolder, ref string currentLanguageCodeArgument, ComboBox comboboxLanguages, out string languageImage)
		{
			languageImage = OpenBveApi.Path.CombineFile(flagFolder, "unknown.png");
			object language = SelectedLanguageObject(comboboxLanguages);
			if (language == null)
			{
				return false;
			}

			if (!blockComboBox)
			{
				Translations.CurrentLanguageCode = Field(language, "Code");
				currentLanguageCodeArgument = Translations.CurrentLanguageCode;
			}

			string flag = Field(language, "Flag");
			languageImage = OpenBveApi.Path.CombineFile(flagFolder, flag ?? "unknown.png");
			if (!System.IO.File.Exists(languageImage))
			{
				languageImage = OpenBveApi.Path.CombineFile(flagFolder, "unknown.png");
			}

			return true;
		}

		internal static bool SelectedLanguage(ref string currentLanguageCodeArgument, ComboBox comboboxLanguages)
		{
			object language = SelectedLanguageObject(comboboxLanguages);
			if (language == null)
			{
				return false;
			}

			if (!blockComboBox)
			{
				Translations.CurrentLanguageCode = Field(language, "Code");
				currentLanguageCodeArgument = Translations.CurrentLanguageCode;
			}

			return true;
		}

		/// <summary>As upstream's Cursors.ListCursors.</summary>
		internal static void ListCursors(ComboBox comboBoxCursor)
		{
			comboBoxCursor.Items.Clear();
			int index = -1;
			for (int i = 0; i < AvailableCursors.CursorList.Count; i++)
			{
				comboBoxCursor.Items.Add(AvailableCursors.CursorList[i]);
				if (AvailableCursors.CursorList[i].FileName == Interface.CurrentOptions.CursorFileName)
				{
					index = i;
				}
			}

			if (index != -1)
			{
				comboBoxCursor.SelectedIndex = index;
			}
		}

		/// <summary>As upstream's Cursors.SelectedCursor.</summary>
		internal static void SelectedCursor(ComboBox comboboxCursor, PictureBox pictureboxCursor)
		{
			int i = comboboxCursor.SelectedIndex;
			if (i == -1 || !(comboboxCursor.Items[i] is MouseCursor c))
			{
				return;
			}

			Interface.CurrentOptions.CursorFileName = c.FileName;
			AvailableCursors.CurrentCursor = c.MyCursor;
			AvailableCursors.CurrentCursorPlus = c.MyCursorPlus;
			AvailableCursors.CurrentCursorMinus = c.MyCursorMinus;
			pictureboxCursor.Image = c.Image;
		}

		/// <summary>
		/// An input device plugin's own settings window. It is a Windows Forms window of the plugin's: shown where Windows
		/// Forms can show one, else the menu says it cannot be.
		/// </summary>
		internal static void Config(IInputDevice plugin)
		{
			try
			{
				plugin.Config(null);
			}
			catch (Exception ex)
			{
				MessageBox.Show("This input device's settings window cannot be shown here: " + ex.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
		}

		/// <summary>As InputDevicePlugin.CallPluginConfig, with the plugin's window shown as Config does.</summary>
		internal static void CallPluginConfig(int index)
		{
			if (index < 0 || index >= InputDevicePlugin.AvailablePlugins.Count || InputDevicePlugin.AvailablePluginInfos[index].Status != InputDevicePlugin.PluginInfo.PluginStatus.Enable)
			{
				return;
			}

			Config(InputDevicePlugin.AvailablePlugins[index]);
		}
	}

	/// <summary>A list given to a combo box, as Windows Forms' BindingSource gives it (its items, in order).</summary>
	public class BindingSource : IList
	{
		private readonly List<object> items = new List<object>();

		public BindingSource()
		{
		}

		public BindingSource(object dataSource, string dataMember)
		{
			DataSource = dataSource;
			DataMember = dataMember;
			if (dataSource is IEnumerable list)
			{
				foreach (object item in list)
				{
					items.Add(item);
				}
			}
		}

		public object DataSource { get; set; }

		public string DataMember { get; set; }

		public int Count => items.Count;

		public object this[int index]
		{
			get { return items[index]; }
			set { items[index] = value; }
		}

		public int Add(object value)
		{
			items.Add(value);
			return items.Count - 1;
		}

		public void Clear() => items.Clear();

		public bool Contains(object value) => items.Contains(value);

		public int IndexOf(object value) => items.IndexOf(value);

		public void Insert(int index, object value) => items.Insert(index, value);

		public void Remove(object value) => items.Remove(value);

		public void RemoveAt(int index) => items.RemoveAt(index);

		public bool IsReadOnly => false;

		public bool IsFixedSize => false;

		public void CopyTo(Array array, int index) => ((ICollection)items).CopyTo(array, index);

		public bool IsSynchronized => false;

		public object SyncRoot => items;

		public IEnumerator GetEnumerator() => items.GetEnumerator();
	}

	[Flags]
	public enum TextFormatFlags
	{
		Default = 0, Left = 0, Top = 0, HorizontalCenter = 1, Right = 2, VerticalCenter = 4, Bottom = 8, WordBreak = 16, SingleLine = 32,
		NoPrefix = 2048, EndEllipsis = 32768, NoPadding = 268435456
	}

	/// <summary>Text measured and drawn as the controls draw it.</summary>
	public static class TextRenderer
	{
		public static Size MeasureText(string text, Font font)
		{
			SizeF size = Control.MeasureText(text, font);
			return new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
		}

		public static Size MeasureText(string text, Font font, Size proposedSize)
		{
			SizeF size = Control.MeasureText(text, font, proposedSize.Width > 0 ? proposedSize.Width : 0);
			return new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
		}

		public static Size MeasureText(string text, Font font, Size proposedSize, TextFormatFlags flags) => MeasureText(text, font, (flags & TextFormatFlags.WordBreak) != 0 ? proposedSize : Size.Empty);

		public static Size MeasureText(IDeviceContext dc, string text, Font font) => MeasureText(text, font);

		public static void DrawText(IDeviceContext dc, string text, Font font, Point pt, Color foreColor)
		{
			if (dc is Graphics g)
			{
				Control.DrawText(g, text, font, foreColor, new RectangleF(pt.X, pt.Y, 4000, 4000), ContentAlignment.TopLeft);
			}
		}

		public static void DrawText(IDeviceContext dc, string text, Font font, Rectangle bounds, Color foreColor)
		{
			if (dc is Graphics g)
			{
				Control.DrawText(g, text, font, foreColor, bounds, ContentAlignment.TopLeft, true);
			}
		}

		public static void DrawText(IDeviceContext dc, string text, Font font, Rectangle bounds, Color foreColor, TextFormatFlags flags)
		{
			if (dc is Graphics g)
			{
				ContentAlignment align = (flags & TextFormatFlags.HorizontalCenter) != 0 ? ((flags & TextFormatFlags.VerticalCenter) != 0 ? ContentAlignment.MiddleCenter : ContentAlignment.TopCenter)
					: (flags & TextFormatFlags.Right) != 0 ? ((flags & TextFormatFlags.VerticalCenter) != 0 ? ContentAlignment.MiddleRight : ContentAlignment.TopRight)
					: (flags & TextFormatFlags.VerticalCenter) != 0 ? ContentAlignment.MiddleLeft : ContentAlignment.TopLeft;
				Control.DrawText(g, text, font, foreColor, bounds, align, (flags & TextFormatFlags.WordBreak) != 0, (flags & TextFormatFlags.EndEllipsis) != 0);
			}
		}
	}

	public enum SizeGripStyle { Auto, Show, Hide }
}
