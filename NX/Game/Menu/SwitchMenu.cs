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
using OpenBve.Android;

namespace OpenBve.Switch
{
	/// <summary>
	/// OBNS 1.31N: the Switch's menu (OBNS/SWITCH.md, step 6) - Android's (MenuActivity.cs) redesigned for the console:
	/// the rail on the left (Start new game, Package Management, Controls, Options, About) and the section's page beside
	/// it, laid out at 1280x720 with 5% kept clear of the edges, every part reachable with the buttons alone and by touch
	/// in handheld mode. It draws through <see cref="IMenuCanvas"/> (deko3d's 2D drawing) and reads
	/// <see cref="MenuInput"/>; the game starts when <see cref="Launch"/> is set.
	/// </summary>
	/// <remarks>
	/// The buttons, as the console's own menus: the D-pad or either stick moves, A chooses, B goes back, L and R change
	/// the section, + starts the game once a route and a train are chosen. The hints along the bottom say what A, B, X
	/// and Y do where the focus is.
	/// </remarks>
	public sealed class SwitchMenu : IMenuHost
	{
		private enum Section { Start, Packages, Controls, Options, About }

		private static readonly Section[] Sections = { Section.Start, Section.Packages, Section.Controls, Section.Options, Section.About };

		private Section section = Section.Start;
		private bool inRail;
		private StartPage startPage;
		private OptionsPage optionsPage;
		private readonly MenuList packagesPage = new MenuList(), controlsPage = new MenuList(), aboutPage = new MenuList();

		// The list shown over the menu by a choice (IMenuHost.Pick).
		private MenuList picker;
		private string pickerTitle;

		// The message at the bottom (IMenuHost.Notify).
		private string message;
		private double messageSeconds;

		/// <summary>The route and train to start the game with, once chosen and started.</summary>
		public SwitchLaunch Launch => startPage.Launch;

		/// <summary>Whether the console is docked: the hints then leave out touch, which isn't there.</summary>
		public bool Docked;

		private static string T(string group, string key, string fallback) => Menu.T(group, key, fallback);

		public SwitchMenu()
		{
			Build();
		}

		/// <summary>Every page anew: at the start, and with the new labels after the language changed.</summary>
		private void Build()
		{
			startPage = new StartPage();
			optionsPage = new OptionsPage();
			optionsPage.LanguageChanged += () => rebuild = true;
			BuildPackages();
			BuildControls();
			BuildAbout();
		}

		private bool rebuild;

		private void BuildPackages()
		{
			packagesPage.Clear();
			packagesPage.Add(new HeadingItem(T("panel", "packages", "Package Management")));
			packagesPage.Add(new InfoItem(T("switch", "packages_note",
				"Installing route and train packages on the console comes in a later version. Until then, copy routes and trains to the SD card, into /switch/OBNS/LegacyContent/Railway and /switch/OBNS/LegacyContent/Train, as unpacked folders.")));
			packagesPage.Add(new InfoItem(() => T("options", "package_route_directory", "Route installation directory:") + "\n" + Menu.FileSystem?.RouteInstallationDirectory));
			packagesPage.Add(new InfoItem(() => T("options", "package_train_directory", "Train installation directory:") + "\n" + Menu.FileSystem?.TrainInstallationDirectory));
			packagesPage.Add(new ButtonItem(T("switch", "rescan", "Look for new content"), () =>
			{
				startPage.Refresh();
				Notify(T("switch", "rescanned", "The route and train lists are up to date."));
			}));
		}

		private void BuildControls()
		{
			controlsPage.Clear();
			controlsPage.Add(new HeadingItem(T("panel", "controls", "Controls")));
			controlsPage.Add(new InfoItem(T("switch", "controls_note",
				"The Joy-Con and Pro Controller layout, and changing it, comes with the in-game controls (OBNS/SWITCH.md, step 5).")));
		}

		private void BuildAbout()
		{
			aboutPage.Clear();
			aboutPage.Add(new HeadingItem("OpenBVE " + Menu.Version));
			aboutPage.Add(new InfoItem(T("switch", "about",
				"A free train simulator, running natively on the Nintendo Switch as homebrew (custom firmware, started from hbmenu with title takeover).\n\n" +
				"The simulation, the content formats and the renderer are OpenBVE's own code, built for the console through the Android port; graphics are drawn by deko3d, sound by OpenAL Soft.\n\n" +
				"This is unofficial software, not made or endorsed by Nintendo.")));
			aboutPage.Add(new HeadingItem(T("switch", "licences", "Licences")));
			aboutPage.Add(new InfoItem(T("switch", "licences_note",
				"OpenBVE and this port are free software under the Simplified BSD licence (BSD-2-Clause); the original OpenBVE code is public domain. The libraries built in (Mono and .NET, libnx, deko3d, OpenAL Soft and the codecs) have notices of their own, in /switch/OBNS/Data/Licenses.")));
		}

		private static string SectionName(Section section)
		{
			switch (section)
			{
				case Section.Start: return T("panel", "start", "Start new game");
				case Section.Packages: return T("panel", "packages", "Package Management");
				case Section.Controls: return T("panel", "controls", "Controls");
				case Section.Options: return T("panel", "options", "Options");
				default: return T("panel", "about", "About");
			}
		}

		// --- IMenuHost ---

		public void Pick(string title, string[] options, int selected, Action<int> picked)
		{
			pickerTitle = title;
			picker = new MenuList();
			for (int i = 0; i < options.Length; i++)
			{
				int index = i;
				picker.Add(new EntryItem(i == selected ? "✓" : string.Empty, options[i], null, () =>
				{
					picker = null;
					picked(index);
				}) { Chosen = i == selected });
			}

			picker.Focus = Math.Clamp(selected, 0, options.Length - 1);
		}

		public void Notify(string text)
		{
			message = text;
			messageSeconds = 4;
		}

		// --- the frame ---

		/// <summary>The frame's input (<paramref name="seconds"/> since the last).</summary>
		public void Update(MenuInput input, double seconds)
		{
			messageSeconds = Math.Max(0, messageSeconds - seconds);
			if (rebuild)
			{
				// After the language changed: the pages anew, the focus on the language option.
				rebuild = false;
				Build();
				section = Section.Options;
				inRail = false;
			}

			if (picker != null)
			{
				if (input.Is(MenuButtons.B) || (input.Tapped && !InPicker(input.TouchX, input.TouchY)))
				{
					picker = null;
				}
				else
				{
					picker.Update(input, this);
				}

				return;
			}

			if (input.Is(MenuButtons.L) || input.Is(MenuButtons.R))
			{
				int index = Array.IndexOf(Sections, section) + (input.Is(MenuButtons.R) ? 1 : -1);
				section = Sections[(index + Sections.Length) % Sections.Length];
				return;
			}

			if (TapRail(input))
			{
				return;
			}

			if (inRail)
			{
				if (input.Is(MenuButtons.Up) || input.Is(MenuButtons.Down))
				{
					int index = Array.IndexOf(Sections, section) + (input.Is(MenuButtons.Down) ? 1 : -1);
					section = Sections[Math.Clamp(index, 0, Sections.Length - 1)];
				}
				else if (input.Is(MenuButtons.A) || input.Is(MenuButtons.Right))
				{
					inRail = false;
				}
				else if (input.Is(MenuButtons.Plus) && section == Section.Start)
				{
					startPage.Update(input, this);
				}

				return;
			}

			bool used;
			switch (section)
			{
				case Section.Start:
					used = startPage.Update(input, this);
					break;
				case Section.Options:
					used = optionsPage.List.Update(input, this);
					break;
				default:
					used = Page(section).Update(input, this);
					break;
			}

			if (!used && (input.Is(MenuButtons.B) || input.Is(MenuButtons.Left)))
			{
				inRail = true;
			}
		}

		private MenuList Page(Section which) => which == Section.Packages ? packagesPage : which == Section.Controls ? controlsPage : which == Section.Options ? optionsPage.List : aboutPage;

		// The layout.
		private const float RailX = MenuStyle.SafeX, RailWidth = 250, ContentX = RailX + RailWidth + 24;
		private const float ContentWidth = MenuStyle.Width - MenuStyle.SafeX - ContentX;
		private const float TopY = MenuStyle.SafeY, HintsHeight = 40;
		private const float ContentHeight = MenuStyle.Height - MenuStyle.SafeY - HintsHeight - 8 - TopY;
		private const float RailButtonsY = TopY + 96;

		private bool TapRail(MenuInput input)
		{
			if (!input.Tapped || input.TouchX < RailX || input.TouchX > RailX + RailWidth)
			{
				return false;
			}

			for (int i = 0; i < Sections.Length; i++)
			{
				float y = RailButtonsY + i * (MenuStyle.RowHeight + 8);
				if (input.TouchY >= y && input.TouchY < y + MenuStyle.RowHeight)
				{
					section = Sections[i];
					inRail = false;
					return true;
				}
			}

			return false;
		}

		private const float PickerWidth = 560;

		private static float PickerHeight(int count) => Math.Min(MenuStyle.Height - 2 * MenuStyle.SafeY - 60, 70 + count * (MenuStyle.RowHeight + 4));

		private bool InPicker(float x, float y)
		{
			float height = PickerHeight(picker.Items.Count);
			float left = (MenuStyle.Width - PickerWidth) / 2, top = (MenuStyle.Height - height) / 2;
			return x >= left && x < left + PickerWidth && y >= top && y < top + height;
		}

		/// <summary>Draws the frame (<paramref name="seconds"/> since the last, for the scrolling's easing).</summary>
		public void Draw(IMenuCanvas canvas, double seconds)
		{
			canvas.Fill(0, 0, MenuStyle.Width, MenuStyle.Height, MenuStyle.Background);

			// The rail: the title, the version, the sections.
			canvas.Text("OpenBVE", RailX + 4, TopY + 4, MenuStyle.TitleSize, MenuStyle.Primary, true);
			canvas.Text(MenuStyle.Fit(canvas, Menu.Version.Replace("-nx", " · Switch"), MenuStyle.SmallSize, RailWidth), RailX + 4, TopY + 4 + MenuStyle.TitleSize + 10, MenuStyle.SmallSize, MenuStyle.Secondary);
			for (int i = 0; i < Sections.Length; i++)
			{
				float y = RailButtonsY + i * (MenuStyle.RowHeight + 8);
				bool selected = Sections[i] == section;
				canvas.Fill(RailX, y, RailWidth, MenuStyle.RowHeight, selected ? MenuStyle.Selection : MenuStyle.SurfaceRaised, 8);
				if (inRail && selected && picker == null)
				{
					canvas.Outline(RailX, y, RailWidth, MenuStyle.RowHeight, MenuStyle.FocusRing, 3, 8);
				}

				canvas.Text(MenuStyle.Fit(canvas, SectionName(Sections[i]), MenuStyle.BodySize, RailWidth - 32), RailX + 16, y + (MenuStyle.RowHeight - MenuStyle.BodySize) / 2,
					MenuStyle.BodySize, MenuStyle.Primary, selected);
			}

			// The page.
			bool active = !inRail && picker == null;
			if (section == Section.Start)
			{
				startPage.Draw(canvas, ContentX, TopY, ContentWidth, ContentHeight, active, seconds);
			}
			else
			{
				Page(section).Draw(canvas, ContentX, TopY, ContentWidth - 14, ContentHeight, active, seconds);
			}

			DrawHints(canvas);
			if (messageSeconds > 0 && !string.IsNullOrEmpty(message))
			{
				string text = MenuStyle.Fit(canvas, message, MenuStyle.BodySize, 760);
				float width = canvas.TextWidth(text, MenuStyle.BodySize) + 48;
				float y = MenuStyle.Height - MenuStyle.SafeY - HintsHeight - 76;
				canvas.Fill((MenuStyle.Width - width) / 2, y, width, 56, MenuStyle.SurfaceRaised, 28);
				canvas.Text(text, (MenuStyle.Width - width) / 2 + 24, y + (56 - MenuStyle.BodySize) / 2, MenuStyle.BodySize, MenuStyle.Primary);
			}

			if (picker != null)
			{
				// Over the menu, the rest dimmed.
				canvas.Fill(0, 0, MenuStyle.Width, MenuStyle.Height, 0xA0000000);
				float height = PickerHeight(picker.Items.Count);
				float left = (MenuStyle.Width - PickerWidth) / 2, top = (MenuStyle.Height - height) / 2;
				canvas.Fill(left, top, PickerWidth, height, MenuStyle.Surface, MenuStyle.Radius);
				canvas.Text(MenuStyle.Fit(canvas, pickerTitle, MenuStyle.HeadingSize, PickerWidth - 40, true), left + 20, top + 18, MenuStyle.HeadingSize, MenuStyle.Accent, true);
				picker.Draw(canvas, left + 12, top + 60, PickerWidth - 38, height - 72, true, seconds);
			}
		}

		/// <summary>What the buttons do here, along the bottom, as the console's own menus show it.</summary>
		private void DrawHints(IMenuCanvas canvas)
		{
			float y = MenuStyle.Height - MenuStyle.SafeY - HintsHeight;
			canvas.Fill(0, y - 8, MenuStyle.Width, 1, MenuStyle.SurfaceRaised);
			string hints = picker != null
				? "Ⓐ " + T("switch", "hint_choose", "Choose") + "    Ⓑ " + T("switch", "hint_back", "Back")
				: "Ⓐ " + T("switch", "hint_choose", "Choose") + "    Ⓑ " + T("switch", "hint_back", "Back") + "    Ⓛ Ⓡ " + T("switch", "hint_sections", "Sections") +
				  (section == Section.Start && !inRail ? "    Ⓧ " + T("start", "route_recently", "Recently used") + "    Ⓨ " + T("switch", "hint_up", "Up a folder") : string.Empty) +
				  (section == Section.Start && startPage.Ready ? "    ⊕ " + T("start", "start_start", "Start") : string.Empty);
			hints = MenuStyle.Fit(canvas, hints, MenuStyle.SmallSize, MenuStyle.Width - 2 * MenuStyle.SafeX);
			canvas.Text(hints, MenuStyle.Width - MenuStyle.SafeX - canvas.TextWidth(hints, MenuStyle.SmallSize), y + (HintsHeight - MenuStyle.SmallSize) / 2, MenuStyle.SmallSize, MenuStyle.Secondary);
		}
	}
}
