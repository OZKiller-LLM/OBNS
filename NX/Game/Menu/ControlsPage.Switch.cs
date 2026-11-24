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
using System.Linq;
using OpenBve.Android;
using OpenBveApi.Interface;

namespace OpenBve.Switch
{
	/// <summary>
	/// Controls, as Android's ControlsPage's Controller tab for the console: every command with its buttons. A on a command,
	/// then the button to give it (+ cancels); X takes its buttons off; a command can have several buttons and a button
	/// serve several commands. + and − stay the pause menu and the timetable. From the main menu and the pause menu alike;
	/// changes hold at once.
	/// </summary>
	public static class ControlsPage
	{
		/// <summary>Whether the page shows the keyboard's keys (a USB keyboard, KeyBindings) rather than the controller's buttons.</summary>
		private static bool keyboard;

		/// <summary>Fills the list: the controller or keyboard switch, the help, Reset, then every command.</summary>
		public static void Build(MenuList list, Action rebuilt)
		{
			// Android's Controller and Keyboard tabs, as one row that switches between them.
			list.Add(new ButtonItem(keyboard ? T("switch", "controls_show_controller", "Keyboard keys (A: show the controller's buttons)") : T("switch", "controls_show_keyboard", "Controller buttons (A: show a USB keyboard's keys)"), () =>
			{
				keyboard = !keyboard;
				rebuilt?.Invoke();
			}));
			if (keyboard)
			{
				list.Add(new InfoItem(T("switch", "controls_keyboard_help",
					"Choose a command with A, then press its key on a USB keyboard, with Shift, Ctrl or Alt if wanted (Escape or + cancels). X takes a command's keys off. Kept in UserData/1.5.0/controls.cfg, as upstream's; a desktop's own file works too.")));
				list.Add(new ButtonItem(T("android", "controls_reset", "Reset to defaults"), () =>
				{
					KeyBindings.Reset();
					rebuilt?.Invoke();
				}));
			}
			else
			{
				list.Add(new InfoItem(T("switch", "controls_help",
					"Choose a command with A, then press the button to give it (+ cancels). X takes a command's buttons off. + and − always open the pause menu and the timetable.")));
				list.Add(new ButtonItem(T("android", "controls_reset", "Reset to defaults"), () =>
				{
					PadBindings.Reset();
					rebuilt?.Invoke();
				}));
			}

			foreach (Translations.CommandInfo info in Translations.CommandInfos.Values.Where(i => i.Type == Translations.CommandType.Digital))
			{
				if (keyboard)
				{
					// The menus' own keys work the menus on the Switch; everything else as upstream.
					if (info.Command == Translations.Command.MenuUp || info.Command == Translations.Command.MenuDown || info.Command == Translations.Command.MenuEnter ||
					    info.Command == Translations.Command.MenuBack || info.Command == Translations.Command.MiscFullscreen)
					{
						continue;
					}

					list.Add(new KeyBindingItem(info.Command));
					continue;
				}

				if (info.Command == Translations.Command.MiscPause || info.Command == Translations.Command.TimetableToggle)
				{
					continue;
				}

				list.Add(new BindingItem(info.Command));
			}
		}

		internal static string T(string group, string key, string fallback) => Menu.T(group, key, fallback);

		/// <summary>The command as the player reads it: upstream's translated description, else its name (as Android's).</summary>
		internal static string Describe(Translations.Command command)
		{
			try
			{
				Translations.CommandInfo info = Translations.CommandInfos[command];
				return string.IsNullOrWhiteSpace(info.Description) ? info.Name : info.Description;
			}
			catch (Exception)
			{
				return command.ToString();
			}
		}
	}

	/// <summary>A command and its buttons; A waits for a button to add, X clears.</summary>
	public sealed class BindingItem : MenuItem
	{
		/// <summary>Whether a binding waits for its button now: the menu around it passes every button to it then.</summary>
		public static bool Capturing { get; internal set; }

		private readonly Translations.Command command;
		private bool capturing, released;

		public BindingItem(Translations.Command command) => this.command = command;

		public override float Measure(IMenuCanvas canvas, float width) => MenuStyle.RowHeight;

		public override void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused)
		{
			Row(canvas, x, y, width, MenuStyle.RowHeight, focused);
			float valueWidth = width * 0.34f;
			string name = ControlsPage.Describe(command);
			canvas.Text(MenuStyle.Fit(canvas, name, MenuStyle.BodySize, width - valueWidth - 40), x + 16, Middle(y, MenuStyle.RowHeight, MenuStyle.BodySize), MenuStyle.BodySize, MenuStyle.Primary);
			string value = capturing ? ControlsPage.T("switch", "controls_press", "Press a button… (+ cancels)") : PadBindings.Describe(command);
			value = MenuStyle.Fit(canvas, value, MenuStyle.SmallSize, valueWidth);
			canvas.Text(value, x + width - 16 - canvas.TextWidth(value, MenuStyle.SmallSize), Middle(y, MenuStyle.RowHeight, MenuStyle.SmallSize), MenuStyle.SmallSize,
				capturing ? MenuStyle.Accent : MenuStyle.Secondary);
		}

		public override bool Handle(MenuInput input, IMenuHost host)
		{
			if (capturing)
			{
				// The A that started it let go first; then the next button down is the one.
				if (!released)
				{
					released = input.Held == MenuButtons.None;
					return true;
				}

				MenuButtons pressed = input.Pressed;
				if (pressed == MenuButtons.None)
				{
					return true;
				}

				if ((pressed & (MenuButtons.Plus | MenuButtons.Minus)) == 0)
				{
					ulong button = PadButton(pressed);
					if (button != 0)
					{
						PadBindings.Add(command, button);
						host.Notify(ControlsPage.Describe(command) + ": " + PadBindings.Describe(command));
					}
				}

				Stop();
				input.Consume();
				return true;
			}

			if (input.Is(MenuButtons.A))
			{
				capturing = true;
				Capturing = true;
				released = false;
				return true;
			}

			if (input.Is(MenuButtons.X))
			{
				PadBindings.Clear(command);
				return true;
			}

			return false;
		}

		public override void Tap(float x, float width, IMenuHost host)
		{
			capturing = true;
			Capturing = true;
			released = false;
		}

		private void Stop()
		{
			capturing = false;
			Capturing = false;
		}

		/// <summary>The first of the menu's buttons as the pad's (SwitchPad's bits).</summary>
		private static ulong PadButton(MenuButtons buttons)
		{
			(MenuButtons Menu, ulong Pad)[] map =
			{
				(MenuButtons.A, SwitchPad.A), (MenuButtons.B, SwitchPad.B), (MenuButtons.X, SwitchPad.X), (MenuButtons.Y, SwitchPad.Y),
				(MenuButtons.L, SwitchPad.L), (MenuButtons.R, SwitchPad.R), (MenuButtons.ZL, SwitchPad.ZL), (MenuButtons.ZR, SwitchPad.ZR),
				(MenuButtons.StickL, SwitchPad.StickL), (MenuButtons.StickR, SwitchPad.StickR),
				(MenuButtons.Up, SwitchPad.Up), (MenuButtons.Down, SwitchPad.Down), (MenuButtons.Left, SwitchPad.Left), (MenuButtons.Right, SwitchPad.Right)
			};
			foreach ((MenuButtons menu, ulong pad) in map)
			{
				if ((buttons & menu) != 0)
				{
					return pad;
				}
			}

			return 0;
		}
	}

	/// <summary>A command and its keys on a USB keyboard; A waits for a key (with its modifiers) to add, X clears.</summary>
	public sealed class KeyBindingItem : MenuItem
	{
		private readonly Translations.Command command;
		private readonly ulong[] keys = new ulong[4];
		private bool capturing;

		public KeyBindingItem(Translations.Command command) => this.command = command;

		public override float Measure(IMenuCanvas canvas, float width) => MenuStyle.RowHeight;

		public override void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused)
		{
			Row(canvas, x, y, width, MenuStyle.RowHeight, focused);
			float valueWidth = width * 0.34f;
			string name = ControlsPage.Describe(command);
			canvas.Text(MenuStyle.Fit(canvas, name, MenuStyle.BodySize, width - valueWidth - 40), x + 16, Middle(y, MenuStyle.RowHeight, MenuStyle.BodySize), MenuStyle.BodySize, MenuStyle.Primary);
			string value = capturing ? ControlsPage.T("switch", "controls_press_key", "Press a key… (Escape cancels)") : KeyBindings.Describe(command);
			value = MenuStyle.Fit(canvas, value, MenuStyle.SmallSize, valueWidth);
			canvas.Text(value, x + width - 16 - canvas.TextWidth(value, MenuStyle.SmallSize), Middle(y, MenuStyle.RowHeight, MenuStyle.SmallSize), MenuStyle.SmallSize,
				capturing ? MenuStyle.Accent : MenuStyle.Secondary);
		}

		public override bool Handle(MenuInput input, IMenuHost host)
		{
			if (capturing)
			{
				// The keyboard read itself (the menus' mapping of it aside): the next key down is the one.
				if (SwitchKeyboard.NextKey(keys, out string key, out int modifiers))
				{
					if (key != "escape" || modifiers != 0)
					{
						KeyBindings.Add(command, key, modifiers);
						host.Notify(ControlsPage.Describe(command) + ": " + KeyBindings.Describe(command));
					}

					capturing = false;
					BindingItem.Capturing = false;
				}
				else if (input.Is(MenuButtons.Plus))
				{
					capturing = false;
					BindingItem.Capturing = false;
				}

				input.Consume();
				return true;
			}

			if (input.Is(MenuButtons.A))
			{
				Start();
				return true;
			}

			if (input.Is(MenuButtons.X))
			{
				KeyBindings.Clear(command);
				return true;
			}

			return false;
		}

		public override void Tap(float x, float width, IMenuHost host) => Start();

		private void Start()
		{
			capturing = true;
			// (The menu around it passes every button to it while it waits, as a controller binding's.)
			BindingItem.Capturing = true;
			// The keys down now (the Enter that chose it, say) aren't the one.
			SwitchKeyboard.NextKey(keys, out _, out _);
		}
	}
}
