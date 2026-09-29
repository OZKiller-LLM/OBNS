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
using System.Linq;
using Android.App;
using Android.Views;
using Android.Widget;
using OpenBveApi.Interface;

namespace OpenBve.Android
{
	/// <summary>
	/// Controls: every command with its keyboard keys or controller buttons, as upstream's
	/// Customize Controls page. Select a command (tap, or A on a controller), press the key or
	/// button to add, or clear it; reset returns to the defaults. From the main menu changes apply
	/// from the next game; opened from the pause menu (with a Back button), on returning to it.
	/// </summary>
	public class ControlsPage : LinearLayout
	{
		private readonly Activity activity;
		private readonly LinearLayout list;
		private readonly Button keyboardTab, controllerTab;
		private ControlBindings bindings;
		private bool controller;

		/// <param name="activity">The activity it is shown in.</param>
		/// <param name="back">In a game: what its Back button does (leaves the page); null in the main menu, which has no such button.</param>
		public ControlsPage(Activity activity, Action back = null) : base(activity)
		{
			this.activity = activity;
			Orientation = Orientation.Vertical;
			int pad = Ui.Dp(activity, 12);
			SetPadding(pad, 0, pad, 0);

			AddView(Ui.Heading(activity, Menu.T("panel", "controls", "Controls")), Ui.Stacked(activity, top: 12));
			AddView(Ui.Label(activity, Menu.T("android", "controls_help",
				"Select a command, then press the key or controller button to give it. A command can have several; a key can serve several commands.") + " " +
				(back == null
					? Menu.T("android", "controls_apply_next", "Changes apply from the next game.")
					: Menu.T("android", "controls_apply_now", "Changes apply when you return to the game.")), 13.0f), Ui.Stacked(activity));

			LinearLayout bar = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
			if (back != null)
			{
				Button backButton = Ui.Button(activity, Menu.T("menu", "back", "Back"));
				backButton.Click += (s, e) => back();
				bar.AddView(backButton, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Ui.Dp(activity, 18) });
			}

			keyboardTab = Ui.Button(activity, Menu.T("android", "controls_keyboard", "Keyboard"));
			keyboardTab.Click += (s, e) => ShowTab(false);
			controllerTab = Ui.Button(activity, Menu.T("android", "controls_controller", "Controller"));
			controllerTab.Click += (s, e) => ShowTab(true);
			Button reset = Ui.Button(activity, Menu.T("android", "controls_reset", "Reset to defaults"));
			reset.Click += (s, e) => new AlertDialog.Builder(activity)
				.SetMessage(Menu.T("android", "controls_reset_question", "Put every key and controller button back as it was?"))
				.SetPositiveButton(Menu.T("android", "controls_reset", "Reset to defaults"), (a, b) =>
				{
					ControlBindings.Reset(Menu.FileSystem.SettingsFolder);
					Reload();
				})
				.SetNegativeButton(global::Android.Resource.String.Cancel, (a, b) => { })
				.Show();
			bar.AddView(keyboardTab, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Ui.Dp(activity, 6) });
			bar.AddView(controllerTab, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Ui.Dp(activity, 18) });
			bar.AddView(reset);
			AddView(bar, Ui.Stacked(activity, top: 6, bottom: 6));

			ScrollView scroll = new ScrollView(activity);
			list = new LinearLayout(activity) { Orientation = Orientation.Vertical };
			scroll.AddView(list);
			AddView(scroll, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
			Reload();
		}

		private void Reload()
		{
			bindings = ControlBindings.Load(Menu.FileSystem.SettingsFolder, Menu.FileSystem.GetDataFolder("Controls"));
			ShowTab(controller);
		}

		private void ShowTab(bool showController)
		{
			controller = showController;
			keyboardTab.Background = Ui.Panel(activity, controller ? Ui.SurfaceRaised : Ui.Selection, 8);
			controllerTab.Background = Ui.Panel(activity, controller ? Ui.Selection : Ui.SurfaceRaised, 8);
			list.RemoveAllViews();
			// Keys and buttons are on or off: the analog axis commands belong to sticks and levers.
			foreach (Translations.CommandInfo info in Translations.CommandInfos.Values.Where(i => i.Type == Translations.CommandType.Digital))
			{
				Translations.Command command = info.Command;
				Button row = Ui.Button(activity, string.Empty);
				row.Gravity = GravityFlags.CenterVertical | GravityFlags.Start;
				row.Text = Label(command);
				row.Click += (s, e) => Capture(command, row);
				list.AddView(row, Ui.Stacked(activity, top: 2, bottom: 2));
			}
		}

		/// <summary>"Power: increase    Z" - the description, and what it is bound to.</summary>
		private string Label(Translations.Command command)
		{
			string bound = controller
				? string.Join(", ", bindings.Pad.Where(b => b.Command == command).Select(b => ControlBindings.Describe(b.Button)))
				: string.Join(", ", bindings.Keys.Where(b => b.Command == command).Select(ControlBindings.Describe));
			return ControlBindings.Describe(command) + "\n" + (bound.Length > 0 ? bound : "—");
		}

		/// <summary>Waits for the key or button to give a command.</summary>
		private void Capture(Translations.Command command, Button row)
		{
			AlertDialog dialog = null;
			AlertDialog.Builder builder = new AlertDialog.Builder(activity)
				.SetTitle(ControlBindings.Describe(command))
				.SetMessage(controller
					? Menu.T("android", "controls_press_button", "Press the controller button to add.")
					: Menu.T("android", "controls_press_key", "Press the key (with Ctrl, Shift or Alt if wanted) to add."))
				.SetNeutralButton(Menu.T("android", "controls_clear", "Clear"), (s, e) =>
				{
					if (controller)
					{
						bindings.Pad.RemoveAll(b => b.Command == command);
					}
					else
					{
						bindings.Keys.RemoveAll(b => b.Command == command);
					}

					Save(row, command);
				})
				.SetNegativeButton(global::Android.Resource.String.Cancel, (s, e) => { });
			dialog = builder.Create();
			dialog.KeyPress += (s, e) =>
			{
				KeyEvent key = e.Event;
				e.Handled = false;
				if (key.Action != KeyEventActions.Down || key.RepeatCount > 0 || IsModifier(key.KeyCode) || key.KeyCode == Keycode.Back)
				{
					return;
				}

				bool fromPad = (key.Source & InputSourceType.Gamepad) == InputSourceType.Gamepad || (key.Source & InputSourceType.Joystick) == InputSourceType.Joystick;
				if (controller)
				{
					bool dpad = key.KeyCode >= Keycode.DpadUp && key.KeyCode <= Keycode.DpadRight;
					if (!ControlBindings.IsPadButton(key.KeyCode) && !(fromPad && dpad))
					{
						return;
					}

					if (!bindings.Pad.Any(b => b.Command == command && b.Button == key.KeyCode))
					{
						bindings.Pad.Add(new PadBinding(command, key.KeyCode));
					}
				}
				else
				{
					string name = ControlBindings.KeyName(key.KeyCode);
					if (name == null || fromPad || ControlBindings.IsPadButton(key.KeyCode))
					{
						return;
					}

					int modifiers = (key.IsShiftPressed ? 1 : 0) | (key.IsCtrlPressed ? 2 : 0) | (key.IsAltPressed ? 4 : 0);
					if (!bindings.Keys.Any(b => b.Command == command && ControlBindings.Normalise(b.Key) == ControlBindings.Normalise(name) && b.Modifiers == modifiers))
					{
						bindings.Keys.Add(new KeyBinding(command, name, modifiers));
					}
				}

				e.Handled = true;
				Save(row, command);
				dialog.Dismiss();
			};
			dialog.Show();
		}

		private void Save(Button row, Translations.Command command)
		{
			try
			{
				bindings.Save(Menu.FileSystem.SettingsFolder);
			}
			catch (Exception ex)
			{
				Toast.MakeText(activity, ex.Message, ToastLength.Long)?.Show();
			}

			row.Text = Label(command);
		}

		private static bool IsModifier(Keycode code) => code == Keycode.ShiftLeft || code == Keycode.ShiftRight || code == Keycode.CtrlLeft ||
		                                                  code == Keycode.CtrlRight || code == Keycode.AltLeft || code == Keycode.AltRight ||
		                                                  code == Keycode.MetaLeft || code == Keycode.MetaRight;
	}
}
