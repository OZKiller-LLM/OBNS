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
		/// <summary>Fills the list: the help, Reset, then every command.</summary>
		public static void Build(MenuList list, Action rebuilt)
		{
			list.Add(new InfoItem(T("switch", "controls_help",
				"Choose a command with A, then press the button to give it (+ cancels). X takes a command's buttons off. + and − always open the pause menu and the timetable.")));
			list.Add(new ButtonItem(T("android", "controls_reset", "Reset to defaults"), () =>
			{
				PadBindings.Reset();
				rebuilt?.Invoke();
			}));
			foreach (Translations.CommandInfo info in Translations.CommandInfos.Values.Where(i => i.Type == Translations.CommandType.Digital))
			{
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
		public static bool Capturing { get; private set; }

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
}
