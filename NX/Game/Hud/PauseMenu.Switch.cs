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
using System.Collections.Generic;
using OpenBve.Android;

namespace OpenBve.Switch
{
	/// <summary>
	/// The in-game pause menu: Android's (PauseMenu.cs), with upstream's items - Resume, Jump to station, Exit to main
	/// menu, Customize controls, Quit - leading to the same sub-menus and questions, drawn over the scene by deko3d. The
	/// simulation stands still while it is open. A, or a tap, chooses; B goes back a level and from the top resumes, as +
	/// does.
	/// </summary>
	public sealed class PauseMenu : IMenuHost
	{
		// Android's colours (PauseMenu.cs).
		private const uint Backdrop = 0xAA000000, Panel = 0xEB161C28, PanelBorder = 0x78FFFFFF;
		private const uint ItemFill = 0xC8323C50, ItemChosen = 0xDC2864BE;

		private const float PanelWidth = 600, ItemHeight = 64, TitleSize = 30, ItemSize = 22, Padding = 26;

		/// <summary>The session's stations to jump to (index, name); set when the menu opens.</summary>
		public Func<IReadOnlyList<(int Index, string Name)>> Stations;

		/// <summary>The station the player last stopped at, to preselect the next one.</summary>
		public Func<int> LastStation;

		public event Action Resumed;
		public event Action<int> JumpRequested;
		public event Action ExitRequested;
		public event Action QuitRequested;

		/// <summary>Whether it is open.</summary>
		public bool Open { get; private set; }

		private string title = string.Empty;
		private readonly MenuList list = new MenuList();
		private bool topLevel;
		private bool primed;

		private static string T(string group, string key, string fallback) => Menu.T(group, key, fallback);

		/// <summary>Opens at the top level. The buttons held now (the + that opened it) don't count as presses.</summary>
		public void Show()
		{
			Open = true;
			primed = false;
			ShowTop();
		}

		private void Resume()
		{
			Open = false;
			Resumed?.Invoke();
		}

		private void Begin(string heading, bool top)
		{
			title = heading;
			topLevel = top;
			list.Clear();
		}

		private void Item(string text, Action action, bool chosen = false)
		{
			list.Add(new PauseItem(text, action, chosen));
		}

		private void ShowTop()
		{
			Begin(T("menu", "title", "Pause"), true);
			Item(T("menu", "resume", "Resume simulation"), Resume);
			if (Stations?.Invoke().Count > 0)
			{
				Item(T("menu", "jump", "Jump to station"), ShowStations);
			}

			Item(T("menu", "exit", "Exit to main menu"), () => Question(
				T("menu", "exit_question", "Do you really want to exit to the main menu?"),
				T("menu", "exit_no", "No"), T("menu", "exit_yes", "Yes"), () => ExitRequested?.Invoke()));
			Item(T("menu", "customize_controls", "Customize controls"), ShowControls);
			Item(T("menu", "quit", "Quit"), () => Question(
				T("menu", "quit_question", "Do you really want to quit?"),
				T("menu", "quit_no", "No"), T("menu", "quit_yes", "Yes"), () => QuitRequested?.Invoke()));
			list.FocusFirst();
		}

		private void ShowStations()
		{
			Begin(T("menu", "jump", "Jump to station"), false);
			Item(T("menu", "back", "Back"), ShowTop);
			int last = LastStation?.Invoke() ?? -1;
			int preselected = -1;
			foreach ((int index, string name) in Stations?.Invoke() ?? Array.Empty<(int, string)>())
			{
				int station = index;
				// As upstream: the first station after the one last stopped at is the suggested choice.
				bool suggested = preselected < 0 && index > last;
				Item(string.IsNullOrEmpty(name) ? "#" + index : name, () =>
				{
					Open = false;
					JumpRequested?.Invoke(station);
					Resumed?.Invoke();
				}, suggested);
				if (suggested)
				{
					preselected = list.Items.Count - 1;
				}
			}

			list.FocusFirst();
			if (preselected >= 0)
			{
				list.Focus = preselected;
			}
		}

		private void Question(string question, string no, string yes, Action confirmed)
		{
			Begin(question, false);
			Item(no, ShowTop);
			Item(yes, confirmed);
			list.FocusFirst();
		}

		/// <summary>Android shows its Controls page here; the Switch's controller layout and changing it come with the
		/// Controls section (OBNS/SWITCH.md, step 5): for now, the layout.</summary>
		private void ShowControls()
		{
			Begin(T("menu", "customize_controls", "Customize controls"), false);
			Item(T("menu", "back", "Back"), ShowTop);
			string[] lines =
			{
				"ZR / ZL: " + T("switch", "pad_power_brake", "power and brake up"),
				"R / L: " + T("switch", "pad_power_brake_back", "power and brake back"),
				"R stick (click): " + T("switch", "pad_emergency", "emergency brake"),
				"A: " + T("switch", "pad_ats", "safety system (S)") + "    B: " + T("switch", "pad_horn", "horn"),
				"X / Y: " + T("switch", "pad_doors", "doors left and right"),
				"D-pad up / down: " + T("switch", "pad_reverser", "reverser"),
				"D-pad left / right: " + T("switch", "pad_views", "views"),
				"L stick (click): " + T("switch", "pad_cab", "cab view") + "    +: " + T("menu", "title", "Pause") + "    −: " + T("switch", "pad_timetable", "timetable")
			};
			foreach (string line in lines)
			{
				list.Add(new InfoItem(line));
			}

			list.FocusFirst();
		}

		// --- IMenuHost: the pause menu has no pickers or messages of its own ---

		public void Pick(string pickTitle, string[] options, int selected, Action<int> picked)
		{
		}

		public void Notify(string message)
		{
		}

		/// <summary>The frame's input while open.</summary>
		public void Update(MenuInput input)
		{
			if (!Open)
			{
				return;
			}

			if (!primed)
			{
				// The press that opened it isn't one of its own.
				primed = true;
				return;
			}

			if (input.Is(MenuButtons.Plus) || input.Is(MenuButtons.B))
			{
				if (topLevel || input.Is(MenuButtons.Plus))
				{
					Resume();
				}
				else
				{
					ShowTop();
				}

				return;
			}

			list.Update(input, this);
		}

		public void Draw(IMenuCanvas canvas, double seconds)
		{
			if (!Open)
			{
				return;
			}

			canvas.Fill(0, 0, MenuStyle.Width, MenuStyle.Height, Backdrop);
			float listHeight = 0;
			foreach (MenuItem item in list.Items)
			{
				listHeight += item.Measure(canvas, PanelWidth - 2 * Padding) + 4;
			}

			List<string> heading = MenuStyle.Wrap(canvas, title, TitleSize, PanelWidth - 2 * Padding);
			float titleHeight = heading.Count * (TitleSize + 8) + 10;
			float most = MenuStyle.Height - 2 * MenuStyle.SafeY;
			float height = Math.Min(most, titleHeight + listHeight + 2 * Padding);
			float x = (MenuStyle.Width - PanelWidth) / 2, y = (MenuStyle.Height - height) / 2;
			canvas.Fill(x, y, PanelWidth, height, Panel, 20);
			canvas.Outline(x, y, PanelWidth, height, PanelBorder, 1.5f, 20);
			float lineY = y + Padding - 4;
			foreach (string line in heading)
			{
				canvas.Text(line, x + (PanelWidth - canvas.TextWidth(line, TitleSize)) / 2, lineY, TitleSize, 0xFFFFFFFF);
				lineY += TitleSize + 8;
			}

			list.Draw(canvas, x + Padding, y + Padding + titleHeight - 4, PanelWidth - 2 * Padding, height - titleHeight - 2 * Padding + 4, true, seconds);
		}

		/// <summary>One of the pause menu's buttons, as Android's: full width, its text centred; the suggested station highlighted.</summary>
		private sealed class PauseItem : MenuItem
		{
			private readonly string text;
			private readonly Action action;
			private readonly bool chosen;

			public PauseItem(string text, Action action, bool chosen)
			{
				this.text = text;
				this.action = action;
				this.chosen = chosen;
			}

			public override float Measure(IMenuCanvas canvas, float width) => ItemHeight + 6;

			public override void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused)
			{
				canvas.Fill(x, y + 3, width, ItemHeight, chosen ? ItemChosen : ItemFill, 12);
				if (focused)
				{
					canvas.Outline(x, y + 3, width, ItemHeight, MenuStyle.FocusRing, 3, 12);
				}

				string shown = MenuStyle.Fit(canvas, text, ItemSize, width - 32);
				canvas.Text(shown, x + (width - canvas.TextWidth(shown, ItemSize)) / 2, y + 3 + (ItemHeight - ItemSize) / 2 - 1, ItemSize, 0xFFFFFFFF);
			}

			public override bool Handle(MenuInput input, IMenuHost host)
			{
				if (input.Is(MenuButtons.A))
				{
					action();
					return true;
				}

				return false;
			}

			public override void Tap(float x, float width, IMenuHost host) => action();
		}
	}
}
