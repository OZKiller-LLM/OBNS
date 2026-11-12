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
using System.Runtime.InteropServices;

namespace OpenBve.NX
{
	/// <summary>What the console's applet messages said since the last poll (runner/source/obns_applet.c's Event bits).</summary>
	[Flags]
	internal enum AppletEvents : uint
	{
		None = 0,
		/// <summary>Out of focus: the HOME menu, or an applet over the game. The system suspends the game soon after.</summary>
		FocusLost = 1 << 0,
		/// <summary>In focus again, after the HOME menu or an applet.</summary>
		FocusBack = 1 << 1,
		/// <summary>Woken from sleep.</summary>
		Resumed = 1 << 2,
		/// <summary>Docked or undocked.</summary>
		Docking = 1 << 3,
		/// <summary>The performance mode changed (docking, or the power).</summary>
		Performance = 1 << 4,
		/// <summary>The system asks the game to close.</summary>
		Exit = 1 << 5
	}

	/*
	 * The console's ways (OBNS/SWITCH.md, step 5; the user, 2026-11-12: "work on sleep and resume"). Until now nothing took
	 * the applet messages - the HOME menu, sleep, wake-up, docking - from the system. The menu, the loading screen and the
	 * game's frames poll here once a frame (one thread at a time; the native side has a lock). The game pauses when it
	 * loses the focus or wakes from sleep, and leaves the time it was away out of the simulation: the system suspends an
	 * application out of focus, so the first frame after would otherwise run minutes of simulation at once.
	 */
	internal static class Applet
	{
		[DllImport("__Internal", EntryPoint = "obns_applet_poll")]
		private static extern uint NativePoll();

		[DllImport("__Internal", EntryPoint = "obns_applet_away_count")]
		private static extern uint NativeAwayCount();

		/// <summary>What happened since the last poll; takes the messages waiting now.</summary>
		internal static AppletEvents Poll() => (AppletEvents)NativePoll();

		/// <summary>How many times the game has been away (out of focus or asleep): the watchdog's clock starts again when it changes.</summary>
		internal static uint AwayCount => NativeAwayCount();

		/// <summary>The events, for the log.</summary>
		internal static string Describe(AppletEvents events)
		{
			System.Text.StringBuilder text = new System.Text.StringBuilder();
			void Add(AppletEvents flag, string name)
			{
				if ((events & flag) != 0)
				{
					text.Append(text.Length == 0 ? string.Empty : ", ").Append(name);
				}
			}

			Add(AppletEvents.FocusLost, "focus lost (HOME menu or an applet)");
			Add(AppletEvents.FocusBack, "focus back");
			Add(AppletEvents.Resumed, "woken from sleep");
			Add(AppletEvents.Docking, Docked() ? "docked" : "undocked");
			Add(AppletEvents.Performance, "performance mode changed");
			Add(AppletEvents.Exit, "the system asks to close");
			return text.ToString();
		}

		[DllImport("__Internal", EntryPoint = "obns_docked")]
		private static extern int NativeDocked();

		private static bool Docked() => NativeDocked() != 0;
	}
}
