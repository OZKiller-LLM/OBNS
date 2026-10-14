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

using TrainManager.Handles;
using TrainManager.Trains;

namespace OpenBve.Android
{
	/// <summary>
	/// Drives the train on its own until touch controls exist: after a short pause it selects
	/// forward, releases the brake and applies half power up to a cruising speed, then holds that
	/// speed with power and brake. It exists to exercise the physics, handles and signalling on
	/// device, and will be replaced by the cab controls.
	/// </summary>
	public class DemoDriver
	{
		private const double StartDelay = 2.0;
		private const double CruiseKmh = 55.0;
		private const double MaximumKmh = 65.0;

		private double elapsed;

		/// <summary>Updates the handles for this frame.</summary>
		public void Update(TrainBase train, double timeElapsed)
		{
			elapsed += timeElapsed;
			if (elapsed < StartDelay)
			{
				return;
			}

			CabHandles handles = train.Handles;

			// Depending on the route's start mode the train may begin with the emergency brake on.
			if (handles.EmergencyBrake.Driver)
			{
				handles.EmergencyBrake.Release();
			}

			if (handles.Reverser.Driver != ReverserPosition.Forwards)
			{
				handles.Reverser.ApplyState(ReverserPosition.Forwards);
			}

			double kmh = train.CurrentSpeed * 3.6;
			int halfPower = (handles.Power.MaximumDriverNotch + 1) / 2;

			if (kmh > MaximumKmh)
			{
				handles.Power.ApplyState(0, false);
				handles.Brake.ApplyState(2, false);
			}
			else if (kmh < CruiseKmh)
			{
				handles.Brake.ApplyState(0, false);
				handles.Power.ApplyState(halfPower, false);
			}
			else
			{
				handles.Brake.ApplyState(0, false);
				handles.Power.ApplyState(0, false);
			}
		}
	}
}
