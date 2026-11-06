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
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenBve.Android;
using OpenBveApi.Interface;

namespace OpenBve.Switch
{
	/// <summary>
	/// Android's touch controls (ControlOverlay.cs) on the Switch's touch screen: the same buttons, colours and places -
	/// the camera row and the displays top left, the emergency brake top right, the levers bottom right (one combined
	/// lever for a single-handle train), the reverser, horns and doors bottom left, the safety system's keys and the AI
	/// above them, the camera numpad beside the levers - drawn by deko3d over the scene, Android's dp at 1.5 px each.
	/// Handheld only, and off unless Options turn it on (SwitchSettings.GetTouchMode); its ATS mode shows the safety
	/// system's keys alone, all of them, larger, in the others' places, for driving with the controllers. Each finger to its own button,
	/// as Android's split touches: a button sends its command while held; the safety keys latch when held for
	/// LatchSeconds (some plugins' dead-man switches need a key held for the whole run), until tapped again.
	/// </summary>
	public sealed unsafe class TouchControls
	{
		[DllImport("__Internal", EntryPoint = "obns_input_touches")]
		private static extern int ReadTouches(int max, uint* ids, float* xs, float* ys, int* docked);

		private const double LatchSeconds = 0.6;
		private const int MaxTouches = 10;

		private sealed class Key
		{
			public string Label;
			public Translations.Command? Command;
			public Action Action;
			public uint Color;
			public bool Latching, Pressed, Latched, Unlatching;
			public double PressedAt;
			public float X, Y, Width, Height, TextSize;
			public bool Visible = true;
			public bool Pause;
		}

		private readonly AndroidControls controls;
		private readonly List<Key> keys = new List<Key>();
		/// <summary>ATS mode's keys: the safety system's alone, every one shown, larger, where the others would be.</summary>
		private readonly List<Key> atsLayout = new List<Key>();
		private List<Key> shown;
		private int mode = -1;
		private readonly List<Key> levers = new List<Key>(), unified = new List<Key>(), atsMore = new List<Key>(), numpad = new List<Key>();
		private readonly Dictionary<uint, Key> fingers = new Dictionary<uint, Key>();
		private readonly HashSet<uint> seen = new HashSet<uint>();
		private readonly Key aiKey;
		private readonly float scale, opacity, unit;
		private bool singleHandle, atsOpen, numpadOpen, aiActive;
		private readonly Stopwatch clock = Stopwatch.StartNew();

		/// <summary>Whether they are shown and taking touches this frame (on in Options, and handheld).</summary>
		public bool Active { get; private set; }

		/// <summary>The pause button (Android's ‖): opens the pause menu, as +.</summary>
		public event Action PauseRequested;

		/// <summary>VIEW: the next camera view.</summary>
		public event Action ViewRequested;

		/// <summary>TT: the timetable card.</summary>
		public event Action TimetableRequested;

		public TouchControls(AndroidControls controls)
		{
			this.controls = controls;
			// The player's size (as Android, never past what fits: the top row is 680 dp, the left side 370 dp down).
			scale = Math.Min(SwitchSettings.GetTouchButtonScale(), Math.Max(0.7f, Math.Min(1280.0f / 1.5f / 680.0f, 720.0f / 1.5f / 370.0f)));
			opacity = SwitchSettings.GetTouchButtonOpacity();
			unit = 1.5f * scale;

			// Top left: pause, the camera, the displays (Android's MAP and SW have no Switch card yet).
			uint camera = Argb(150, 40, 40, 60), display = Argb(150, 30, 60, 60);
			float x = Dp(8), y = Dp(8);
			Key pause = Action("‖", Argb(190, 70, 70, 90), () => PauseRequested?.Invoke());
			pause.Pause = true;
			x = Place(Top(pause), x, y);
			x = Place(Top(Action("VIEW", camera, () => ViewRequested?.Invoke())), x, y);
			x = Place(Top(Command("CAB", Translations.Command.CameraInterior, camera)), x, y);
			x = Place(Top(Command("◀", Translations.Command.CameraPOIPrevious, camera)), x, y);
			x = Place(Top(Command("▶", Translations.Command.CameraPOINext, camera)), x, y);
			x = Place(Top(Command("RESET", Translations.Command.CameraReset, camera)), x, y);
			x = Place(Top(Action("CAM", camera, () => numpadOpen = !numpadOpen)), x, y);
			Place(Top(Action("TT", display, () => TimetableRequested?.Invoke())), x, y);

			// Top right: the emergency brake.
			Key emergency = Command("EMERGENCY", Translations.Command.BrakeEmergency, Argb(200, 190, 20, 20), wide: true);
			emergency.X = 1280 - Dp(12) - emergency.Width;
			emergency.Y = Dp(12);

			// Bottom right: the two levers, each a column of notch-up over notch-down; or the single handle.
			float right = 1280 - Dp(12), bottom = 720 - Dp(12);
			Key[] power = { Command("POWER\n▲", Translations.Command.PowerIncrease, Argb(170, 30, 90, 200)), Command("POWER\n▼", Translations.Command.PowerDecrease, Argb(170, 30, 60, 130)) };
			Key[] brake = { Command("BRAKE\n▲", Translations.Command.BrakeIncrease, Argb(170, 200, 110, 20)), Command("BRAKE\n▼", Translations.Command.BrakeDecrease, Argb(170, 130, 75, 20)) };
			float leversLeft = Columns(new[] { power, brake }, right, bottom, true);
			levers.AddRange(power);
			levers.AddRange(brake);
			Key[] single = { Command("POWER\n▲", Translations.Command.SinglePower, Argb(170, 30, 90, 200)), Command("N", Translations.Command.SingleNeutral, Argb(170, 70, 70, 70)), Command("BRAKE\n▼", Translations.Command.SingleBrake, Argb(170, 200, 110, 20)) };
			float singleLeft = Columns(new[] { single }, right, bottom, true);
			unified.AddRange(single);

			// Bottom left: reverser, horns, doors.
			Columns(new[]
			{
				new[] { Command("REV\nFWD", Translations.Command.ReverserForward, Argb(160, 60, 60, 60)), Command("REV\nBACK", Translations.Command.ReverserBackward, Argb(160, 60, 60, 60)) },
				new[] { Command("HORN", Translations.Command.HornPrimary, Argb(160, 60, 60, 60)), Command("HORN 2", Translations.Command.HornSecondary, Argb(160, 60, 60, 60)) },
				new[] { Command("DOORS\nLEFT", Translations.Command.DoorsLeft, Argb(160, 30, 120, 60)), Command("DOORS\nRIGHT", Translations.Command.DoorsRight, Argb(160, 30, 120, 60)) }
			}, Dp(12), bottom, false);

			// Above them: the safety system's keys S, A1, B1, the rest (shown by ATS…) and the AI.
			uint safety = Argb(160, 80, 60, 30);
			float atsBottom = 720 - Dp(160);
			x = Dp(12);
			Key s = Command("S", Translations.Command.SecurityS, Argb(190, 150, 100, 20), small: true, latching: true);
			foreach (Key key in new[] { s, Command("A1", Translations.Command.SecurityA1, safety, small: true, latching: true), Command("B1", Translations.Command.SecurityB1, safety, small: true, latching: true) })
			{
				x = Place(key, x, atsBottom - key.Height);
			}

			Translations.Command[][] rows =
			{
				new[] { Translations.Command.SecurityA2, Translations.Command.SecurityB2, Translations.Command.SecurityC1, Translations.Command.SecurityC2, Translations.Command.SecurityD, Translations.Command.SecurityE, Translations.Command.SecurityF, Translations.Command.SecurityG },
				new[] { Translations.Command.SecurityH, Translations.Command.SecurityI, Translations.Command.SecurityJ, Translations.Command.SecurityK, Translations.Command.SecurityL, Translations.Command.SecurityM, Translations.Command.SecurityN, Translations.Command.SecurityO, Translations.Command.SecurityP }
			};
			float rowsLeft = x, rowsRight = x;
			for (int r = 0; r < rows.Length; r++)
			{
				float rowX = rowsLeft;
				foreach (Translations.Command command in rows[r])
				{
					Key key = Command(command.ToString().Substring("Security".Length), command, safety, small: true, latching: true);
					// Android's column of two rows, its top row first, its bottom on the row of S, A1 and B1.
					rowX = Place(key, rowX, atsBottom - (rows.Length - r) * key.Height);
					atsMore.Add(key);
				}

				rowsRight = Math.Max(rowsRight, rowX);
			}

			Key more = Action("ATS…", safety, () => atsOpen = !atsOpen);
			Place(more, rowsLeft, atsBottom - more.Height);
			moreKey = more;
			moreClosedX = rowsLeft;
			moreOpenX = rowsRight;

			aiKey = Action("AI", Argb(160, 50, 70, 90), () => Tap(Translations.Command.MiscAI));
			aiKey.X = Dp(15);
			aiKey.Y = 720 - Dp(160 + 40 + 8) - aiKey.Height;

			/*
			 * The camera numpad (upstream's keypad camera keys, in the keypad's own layout), left of the levers shown.
			 */
			(string Label, Translations.Command Command)[][] pad =
			{
				new[] { ("/\n↺", Translations.Command.CameraRotateCCW), ("*\n↻", Translations.Command.CameraRotateCW) },
				new[] { ("7\nPOI ▶", Translations.Command.CameraPOINext), ("8\nUP", Translations.Command.CameraMoveUp), ("9\nFWD", Translations.Command.CameraMoveForward) },
				new[] { ("4\nLEFT", Translations.Command.CameraMoveLeft), ("5\nRESET", Translations.Command.CameraReset), ("6\nRIGHT", Translations.Command.CameraMoveRight) },
				new[] { ("1\n◀ POI", Translations.Command.CameraPOIPrevious), ("2\nDOWN", Translations.Command.CameraMoveDown), ("3\nBACK", Translations.Command.CameraMoveBackward) },
				new[] { ("0\nZOOM −", Translations.Command.CameraZoomOut), (".\nZOOM +", Translations.Command.CameraZoomIn) }
			};
			float padTop = 720 - Dp(16) - pad.Length * Dp(41);
			for (int r = 0; r < pad.Length; r++)
			{
				float padX = 0;
				foreach ((string label, Translations.Command command) in pad[r])
				{
					Key key = Command(label, command, camera, small: true);
					key.Width = Dp(label.StartsWith("0", StringComparison.Ordinal) ? 110 : 52);
					key.Height = Dp(38);
					key.TextSize = Math.Max(16, 10 * 1.5f * scale);
					key.X = padX + Dp(3);
					key.Y = padTop + r * Dp(41) + Dp(3);
					padX = key.X + key.Width + Dp(3);
					numpad.Add(key);
				}
			}

			/*
			 * ATS mode: the keys where upstream's default keyboard has them (Data/Controls/Default.controls), each with that
			 * key's name under its own. The number row along the top: D to L on 2 to 0, then M, N, O and P (no keys of their
			 * own) on as -, = and the rest would be. The navigation block bottom right: A1, B1, C1 on Insert, Home, Page Up
			 * over A2, B2, C2 on Delete, End, Page Down. S, the space bar, wide along the bottom left. The middle stays clear
			 * for the view and the driving information.
			 */
			(Translations.Command Command, string Name, string Key)[] numberRow =
			{
				(Translations.Command.SecurityD, "D", "2"), (Translations.Command.SecurityE, "E", "3"), (Translations.Command.SecurityF, "F", "4"),
				(Translations.Command.SecurityG, "G", "5"), (Translations.Command.SecurityH, "H", "6"), (Translations.Command.SecurityI, "I", "7"),
				(Translations.Command.SecurityJ, "J", "8"), (Translations.Command.SecurityK, "K", "9"), (Translations.Command.SecurityL, "L", "0"),
				(Translations.Command.SecurityM, "M", null), (Translations.Command.SecurityN, "N", null), (Translations.Command.SecurityO, "O", null),
				(Translations.Command.SecurityP, "P", null)
			};
			float edge = 18, gap = 8;
			float rowWidth = (1280 - 2 * edge - (numberRow.Length - 1) * gap) / numberRow.Length;
			for (int i = 0; i < numberRow.Length; i++)
			{
				Key key = AtsKey(numberRow[i].Name, numberRow[i].Key, numberRow[i].Command, safety);
				key.X = edge + i * (rowWidth + gap);
				key.Y = edge;
				key.Width = rowWidth;
				key.Height = 72;
			}

			(Translations.Command Command, string Name, string Key)[][] navigation =
			{
				new[] { (Translations.Command.SecurityA1, "A1", "Ins"), (Translations.Command.SecurityB1, "B1", "Home"), (Translations.Command.SecurityC1, "C1", "PgUp") },
				new[] { (Translations.Command.SecurityA2, "A2", "Del"), (Translations.Command.SecurityB2, "B2", "End"), (Translations.Command.SecurityC2, "C2", "PgDn") }
			};
			// The bottom row (and the space bar) level with the driving information's card, top and bottom.
			float navWidth = 108, navHeight = DriverCard.BaseHeight, navGap = 10;
			float navLeft = 1280 - edge - 3 * navWidth - 2 * navGap, navTop = DriverCard.BaseTop - navGap - navHeight;
			for (int r = 0; r < navigation.Length; r++)
			{
				for (int c = 0; c < navigation[r].Length; c++)
				{
					Key key = AtsKey(navigation[r][c].Name, navigation[r][c].Key, navigation[r][c].Command, safety);
					key.X = navLeft + c * (navWidth + navGap);
					key.Y = navTop + r * (navHeight + navGap);
					key.Width = navWidth;
					key.Height = navHeight;
				}
			}

			Key space = AtsKey("S", "Space", Translations.Command.SecurityS, Argb(190, 150, 100, 20));
			// A little narrower than the navigation block, centred between the screen's edge and the driving information's.
			space.Width = 300;
			space.X = (DriverCard.BaseLeft - space.Width) / 2;
			space.Height = navHeight;
			space.Y = DriverCard.BaseTop;

			numpadWidth = Dp(3 * 58);
			numpadRightOfLevers = leversLeft - Dp(8);
			numpadRightOfSingle = singleLeft - Dp(8);
			ShowLevers();
		}

		private readonly Key moreKey;
		private readonly float moreClosedX, moreOpenX, numpadWidth, numpadRightOfLevers, numpadRightOfSingle;
		private float numpadShift;

		/// <summary>Shows the lever set for the train's handles (Android's SetSingleHandle).</summary>
		public void SetSingleHandle(bool value)
		{
			singleHandle = value;
			ShowLevers();
		}

		/// <summary>Shows whether the AI drives the player's train (Android's SetAiActive).</summary>
		public void SetAiActive(bool active)
		{
			if (active == aiActive)
			{
				return;
			}

			aiActive = active;
			aiKey.Label = active ? "AI ●" : "AI";
			aiKey.Color = active ? Argb(210, 40, 150, 90) : Argb(160, 50, 70, 90);
		}

		private void ShowLevers()
		{
			foreach (Key key in levers)
			{
				key.Visible = !singleHandle;
			}

			foreach (Key key in unified)
			{
				key.Visible = singleHandle;
			}
		}

		/// <summary>Reads the touch screen and works the buttons. Call once a frame, before the scene's update.</summary>
		public void Update(bool paused)
		{
			const int max = MaxTouches;
			uint* ids = stackalloc uint[max];
			float* xs = stackalloc float[max];
			float* ys = stackalloc float[max];
			int docked;
			int count = ReadTouches(max, ids, xs, ys, &docked);
			int chosen = SwitchSettings.GetTouchMode();
			bool active = chosen != SwitchSettings.TouchOff && docked == 0;
			if (active != Active || chosen != mode)
			{
				// Off, docked, or the other set: nothing stays held.
				ReleaseAll();
				Active = active;
				mode = chosen;
				shown = mode == SwitchSettings.TouchAts ? atsLayout : keys;
			}

			Layout();
			if (!Active || paused)
			{
				return;
			}

			double now = clock.Elapsed.TotalSeconds;
			seen.Clear();
			for (int i = 0; i < count; i++)
			{
				seen.Add(ids[i]);
				if (fingers.ContainsKey(ids[i]))
				{
					continue;
				}

				// A new finger: the button under it, if any, is its own until it lifts.
				Key key = Hit(xs[i], ys[i]);
				fingers[ids[i]] = key;
				if (key != null)
				{
					Down(key, now);
				}
			}

			List<uint> lifted = null;
			foreach (KeyValuePair<uint, Key> finger in fingers)
			{
				if (!seen.Contains(finger.Key))
				{
					(lifted ??= new List<uint>()).Add(finger.Key);
				}
			}

			if (lifted != null)
			{
				foreach (uint id in lifted)
				{
					Key key = fingers[id];
					fingers.Remove(id);
					if (key != null)
					{
						Up(key, now);
					}
				}
			}
		}

		/// <summary>What shows of the optional parts: the rest of the safety keys, the numpad (beside the levers shown).</summary>
		private void Layout()
		{
			foreach (Key key in atsMore)
			{
				key.Visible = atsOpen;
			}

			moreKey.X = atsOpen ? moreOpenX : moreClosedX;
			foreach (Key key in numpad)
			{
				key.Visible = numpadOpen;
			}

			// The numpad just left of whichever lever set shows.
			float shift = (singleHandle ? numpadRightOfSingle : numpadRightOfLevers) - numpadWidth;
			if (shift != numpadShift)
			{
				foreach (Key key in numpad)
				{
					key.X += shift - numpadShift;
				}

				numpadShift = shift;
			}
		}

		/// <summary>For NX/MenuPreview: shows a mode's buttons, without the touch screen.</summary>
		public void Preview(int previewMode, bool safetyKeysOpen, bool numpadShown)
		{
			mode = previewMode;
			shown = mode == SwitchSettings.TouchAts ? atsLayout : keys;
			Active = mode != SwitchSettings.TouchOff;
			atsOpen = safetyKeysOpen;
			numpadOpen = numpadShown;
			Layout();
		}

		private Key Hit(float x, float y)
		{
			for (int i = shown.Count - 1; i >= 0; i--)
			{
				Key key = shown[i];
				if (key.Visible && x >= key.X && x < key.X + key.Width && y >= key.Y && y < key.Y + key.Height)
				{
					return key;
				}
			}

			return null;
		}

		private void Down(Key key, double now)
		{
			key.Pressed = true;
			key.PressedAt = now;
			if (key.Command == null)
			{
				return;
			}

			if (key.Latched)
			{
				// The tap that ends a latch: released when this finger lifts.
				key.Latched = false;
				key.Unlatching = true;
			}
			else
			{
				controls.Press(key.Command.Value);
			}
		}

		private void Up(Key key, double now)
		{
			key.Pressed = false;
			if (key.Command == null)
			{
				// An action runs once per tap, as Android's Click.
				key.Action?.Invoke();
				return;
			}

			if (key.Latching && now - key.PressedAt >= LatchSeconds && !key.Unlatching)
			{
				key.Latched = true;
			}
			else
			{
				key.Latched = false;
				controls.Release(key.Command.Value);
			}

			key.Unlatching = false;
		}

		/// <summary>Lets go of every held and latched button (docked, or the controls turned off).</summary>
		public void ReleaseAll()
		{
			foreach (Key key in System.Linq.Enumerable.Concat(keys, atsLayout))
			{
				if (key.Command != null && (key.Pressed || key.Latched))
				{
					controls.Release(key.Command.Value);
				}

				key.Pressed = key.Latched = key.Unlatching = false;
			}

			fingers.Clear();
		}

		/// <summary>Draws the buttons. Call inside the overlay, under the pause menu.</summary>
		public void Draw(IMenuCanvas canvas)
		{
			if (!Active || shown == null)
			{
				return;
			}

			foreach (Key key in shown)
			{
				if (!key.Visible)
				{
					continue;
				}

				// Dimmed when pressed; a faded button brightens instead, to be seen. Latched ones stay lit. The pause button is
				// never faint: it is the way back to every setting.
				float alpha = key.Latched ? 1.0f : key.Pressed ? (opacity > 0.99f ? 0.6f : Math.Min(1.0f, opacity + 0.4f)) : key.Pause ? Math.Max(opacity, 0.75f) : opacity;
				canvas.Fill(key.X, key.Y, key.Width, key.Height, Faded(key.Color, alpha), Dp(10));
				canvas.Outline(key.X, key.Y, key.Width, key.Height, Faded(Argb(140, 255, 255, 255), alpha), Math.Max(1, Dp(1)), Dp(10));
				string label = key.Latched ? key.Label + " ●" : key.Label;
				string[] lines = label.Split('\n');
				float lineHeight = key.TextSize * 1.15f;
				float top = key.Y + (key.Height - lines.Length * lineHeight) / 2 + (lineHeight - key.TextSize) / 2;
				uint text = Faded(0xFFFFFFFF, Math.Max(alpha, 0.85f));
				for (int i = 0; i < lines.Length; i++)
				{
					float width = canvas.TextWidth(lines[i], key.TextSize, true);
					canvas.Text(lines[i], key.X + (key.Width - width) / 2, top + i * lineHeight, key.TextSize, text, true);
				}
			}
		}

		// --- building ---

		private Key Command(string label, Translations.Command command, uint color, bool wide = false, bool small = false, bool latching = false) =>
			Add(new Key { Label = label, Command = command, Color = color, Latching = latching }, wide, small);

		/// <summary>An ATS mode key: its letter over its keyboard key's name, latching.</summary>
		private Key AtsKey(string name, string keyboard, Translations.Command command, uint color)
		{
			Key key = Command(keyboard == null ? name : name + "\n" + keyboard, command, color, latching: true);
			keys.Remove(key);
			key.TextSize = 20;
			atsLayout.Add(key);
			return key;
		}

		private Key Action(string label, uint color, Action action) => Add(new Key { Label = label, Action = action, Color = color }, false, true);

		private void Tap(Translations.Command command)
		{
			controls.Press(command);
			controls.Release(command);
		}

		private Key Add(Key key, bool wide, bool small)
		{
			key.Width = Dp(wide ? 150 : small ? 52 : 78);
			key.Height = Dp(wide ? 52 : small ? 40 : 64);
			// Android's 11 and 12 sp, at the console's 18 px at least.
			key.TextSize = Math.Max(18, (small ? 11 : 12) * 1.5f * scale);
			keys.Add(key);
			return key;
		}

		/// <summary>Android's top row: a little smaller than the other buttons.</summary>
		private Key Top(Key key)
		{
			key.Width = Math.Max(Dp(46), 16 + key.Label.Length * 11);
			key.Height = Dp(36);
			key.TextSize = Math.Max(16, 10 * 1.5f * scale);
			return key;
		}

		/// <summary>Places a key at the row's x (3 dp either side, as Android's Spaced); returns where the next goes.</summary>
		private float Place(Key key, float x, float y)
		{
			key.X = x + Dp(3);
			key.Y = y;
			return key.X + key.Width + Dp(3);
		}

		/// <summary>Lays out columns of keys (4 dp round each), bottom aligned at <paramref name="bottom"/>, from the left edge
		/// <paramref name="edge"/> rightwards, or from the right edge leftwards; returns their far edge.</summary>
		private float Columns(Key[][] columns, float edge, float bottom, bool fromRight)
		{
			float x = edge;
			for (int c = 0; c < columns.Length; c++)
			{
				Key[] column = fromRight ? columns[columns.Length - 1 - c] : columns[c];
				float width = 0, height = 0;
				foreach (Key key in column)
				{
					width = Math.Max(width, key.Width);
					height += key.Height + Dp(8);
				}

				float columnX = fromRight ? x - width - Dp(8) : x;
				float y = bottom - height;
				foreach (Key key in column)
				{
					key.X = columnX + Dp(4);
					key.Y = y + Dp(4);
					y += key.Height + Dp(8);
				}

				x = fromRight ? columnX : x + width + Dp(8);
			}

			return x;
		}

		private float Dp(float value) => value * unit;

		private static uint Argb(int a, int r, int g, int b) => (uint)a << 24 | (uint)r << 16 | (uint)g << 8 | (uint)b;

		private static uint Faded(uint color, float alpha) => (uint)Math.Clamp((int)((color >> 24) * alpha + 0.5f), 0, 255) << 24 | (color & 0xFFFFFF);
	}
}
