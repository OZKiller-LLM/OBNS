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
using System.Collections;
using System.Collections.Generic;

// ReSharper disable once CheckNamespace
namespace System.Windows.Forms
{
	/// <summary>Stands in for the Windows Forms window handle interface. Never implemented on Android.</summary>
	public interface IWin32Window
	{
		/// <summary>The native window handle.</summary>
		IntPtr Handle { get; }
	}

	/// <summary>
	/// Minimal stand-in for the Windows Forms combo box.
	/// Upstream helpers such as Translations.ListLanguages take one; on Android the in-game
	/// menu reads the language list directly, so these instances are never displayed.
	/// </summary>
	public class ComboBox
	{
		/// <summary>The item collection of a combo box.</summary>
		public class ObjectCollection : IEnumerable
		{
			private readonly List<object> items = new List<object>();

			/// <summary>The number of items.</summary>
			public int Count => items.Count;

			/// <summary>Gets the item at the given index.</summary>
			public object this[int index] => items[index];

			/// <summary>Adds an item.</summary>
			public int Add(object item)
			{
				items.Add(item);
				return items.Count - 1;
			}

			/// <summary>Adds a range of items.</summary>
			public void AddRange(object[] range)
			{
				items.AddRange(range);
			}

			/// <summary>Removes all items.</summary>
			public void Clear()
			{
				items.Clear();
			}

			/// <inheritdoc />
			public IEnumerator GetEnumerator()
			{
				return items.GetEnumerator();
			}
		}

		/// <summary>The items held by this combo box.</summary>
		public ObjectCollection Items { get; } = new ObjectCollection();

		/// <summary>The bound data source, if any.</summary>
		public object DataSource { get; set; }

		/// <summary>The property displayed for bound items.</summary>
		public string DisplayMember { get; set; }

		/// <summary>The property used as the value for bound items.</summary>
		public string ValueMember { get; set; }

		/// <summary>The index of the selected item, or -1.</summary>
		public int SelectedIndex { get; set; } = -1;

		/// <summary>The selected item, or a null reference.</summary>
		public object SelectedItem
		{
			get
			{
				if (DataSource is BindingSource source)
				{
					return SelectedIndex >= 0 && SelectedIndex < source.Count ? source[SelectedIndex] : null;
				}

				return SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;
			}
			set
			{
				for (int i = 0; i < Items.Count; i++)
				{
					if (Equals(Items[i], value))
					{
						SelectedIndex = i;
						return;
					}
				}
			}
		}

		/// <summary>The text of this combo box.</summary>
		public string Text { get; set; } = string.Empty;

		/// <summary>Whether this combo box is enabled.</summary>
		public bool Enabled { get; set; } = true;
	}

	/// <summary>Minimal stand-in for the Windows Forms binding source: an ordered view over a collection.</summary>
	public class BindingSource
	{
		private readonly List<object> items = new List<object>();

		/// <summary>Creates an empty binding source.</summary>
		public BindingSource()
		{
		}

		/// <summary>Creates a binding source over the given collection.</summary>
		/// <param name="dataSource">The collection to wrap.</param>
		/// <param name="dataMember">The member to bind to. Ignored by this implementation.</param>
		public BindingSource(object dataSource, string dataMember)
		{
			if (dataSource is IEnumerable enumerable)
			{
				foreach (object item in enumerable)
				{
					items.Add(item);
				}
			}
		}

		/// <summary>The number of items.</summary>
		public int Count => items.Count;

		/// <summary>Gets the item at the given index.</summary>
		public object this[int index] => items[index];
	}
}
