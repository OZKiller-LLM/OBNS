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

using OpenBveApi.Routes;
using OpenBveApi.Trains;

namespace OpenBve.Android
{
	/// <summary>
	/// The plugins loading content right now, for the loading screen's progress and to ask them to stop. Android's, which
	/// lives in LaunchParameters.cs (left out on the Switch: it reads Android's Intent), copied as it is.
	/// </summary>
	public static class LoadProgress
	{
		/// <summary>The route plugin while a route loads.</summary>
		public static volatile RouteInterface Route;

		/// <summary>The train plugin while a train loads.</summary>
		public static volatile TrainInterface Train;

		/// <summary>Overall progress, 0 to 1: the route the first part of the bar and the train the rest, as upstream's.</summary>
		public static double Value
		{
			get
			{
				TrainInterface train = Train;
				if (train != null)
				{
					return 0.8 + 0.2 * Clamp(train.CurrentProgress);
				}

				RouteInterface route = Route;
				return route != null ? 0.8 * Clamp(route.CurrentProgress) : 0.0;
			}
		}

		/// <summary>Asks whichever plugin is loading to stop.</summary>
		public static void Cancel()
		{
			RouteInterface route = Route;
			if (route != null)
			{
				route.Cancel = true;
			}

			TrainInterface train = Train;
			if (train != null)
			{
				train.Cancel = true;
			}
		}

		private static double Clamp(double value)
		{
			return value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;
		}
	}
}
