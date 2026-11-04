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

// OBNS: not an upstream file. Upstream's JoystickManager64 for Windows on ARM64 (ARM64/README.md), without the RailDriver.

using System;
using OpenBveApi.Hosts;

namespace OpenBve.Input
{
	/// <summary>
	/// The joysticks of an ARM64 process: upstream's JoystickManager64 without the RailDriver, which needs PI Engineering's
	/// PIEHid64Net. That is built for x64 only, which an ARM64 process can't load: the game stopped before its main menu.
	/// </summary>
	internal class JoystickManagerArm64 : JoystickManager
	{
		internal override int RailDriverCount => 0;

		internal override void RefreshJoysticks()
		{
			for (int i = 0; i < 100; i++)
			{
				try
				{
					//Load the list of attached openTK joysticks
					var state = OpenTK.Input.Joystick.GetState(i);
					Guid foundGuid = OpenTK.Input.Joystick.GetGuid(i);
					var description = OpenTK.Input.Joystick.GetCapabilities(i);
					if (description.ButtonCount == 0 && description.HatCount == 0 && description.AxisCount == 0 && description.IsConnected)
					{
						// Broken joystick drivers- pointless attempting to go any further
						break;
					}
					// A joystick with 56 buttons and zero axis is likely the RailDriver, which is bugged in openTK
					// OpenTK also returns an empty GUID if we call GetState on an invalid joystick
					if (description.AxisCount == 0 && description.ButtonCount == 56 && description.IsConnected || foundGuid == Guid.Empty) continue;
					if (Program.CurrentHost.MonoRuntime)
					{
						if (description.AxisCount == 0 && description.ButtonCount == 0 && description.HatCount == 0)
						{
							continue;
						}
					}
					else
					{
						if (!state.IsConnected)
						{
							continue;
						}
					}

					StandardJoystick newJoystick = new StandardJoystick(i);

					if (AttachedJoysticks.ContainsKey(newJoystick.GetGuid()))
					{
						AttachedJoysticks[newJoystick.GetGuid()].Handle = i;
						AttachedJoysticks[newJoystick.GetGuid()].Disconnected = false;
					}
					else
					{
						AttachedJoysticks.Add(newJoystick.GetGuid(), newJoystick);
					}
				}
				catch
				{
					// ignored
				}
			}
		}
	}
}
