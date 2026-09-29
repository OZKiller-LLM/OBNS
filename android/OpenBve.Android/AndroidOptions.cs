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

using OpenBveApi;
using OpenBveApi.Graphics;
using OpenBveApi.Routes;

namespace OpenBve.Android
{
	/// <summary>
	/// Runtime options for the Android build.
	/// </summary>
	/// <remarks>
	/// Defaults are chosen for a phone rather than a desktop: a shorter viewing distance, no
	/// anisotropic filtering and no shadow cascades, all of which cost more on a tile-based
	/// mobile GPU than they do on a desktop card. These become user settings later.
	/// </remarks>
	public class AndroidOptions : BaseOptions
	{
		/// <summary>
		/// Whether static objects drop back faces during optimisation. Upstream keeps this on its
		/// desktop options subclass rather than on BaseOptions; off matches the desktop default.
		/// </summary>
		public bool ObjectOptimizationVertexCulling;

		/// <summary>
		/// Upstream's "unloadtextures" option (also on its desktop options subclass): textures not
		/// drawn for 20 seconds are released. Off by default upstream, which turns it on itself when
		/// memory runs short; on by default here, because a phone's GPU memory is its system RAM
		/// and running out means the low-memory killer, not a slowdown.
		/// </summary>
		public bool UnloadUnusedTextures = true;

		/// <summary>The in-game interface (HUD) layout folder under Data/In-game, as the desktop's option.</summary>
		public string UserInterfaceFolder = "Default";

		/// <summary>The game mode before the last change, which upstream's score log reports.</summary>
		public OpenBveApi.GameMode PreviousGameMode = OpenBveApi.GameMode.Normal;

		/// <summary>No screen reader is driven on Android (upstream's is Windows-only).</summary>
		public bool ScreenReaderAvailable = false;

		/// <summary>Which timetable the timetable key shows first, as the desktop's option.</summary>
		public LibRender2.Overlays.TimeTableMode TimeTableStyle = LibRender2.Overlays.TimeTableMode.Default;

		/// <summary>Creates the options with mobile-appropriate defaults.</summary>
		public AndroidOptions()
		{
			Font = "sans-serif";
			Interpolation = InterpolationMode.BilinearMipmapped;
			AnisotropicFilteringLevel = 0;
			AnisotropicFilteringMaximum = 0;
			TransparencyMode = TransparencyMode.Quality;
			ViewingDistance = 400;
			QuadTreeLeafSize = 200;
			ObjectDisposalMode = ObjectDisposalMode.Accurate;
			ObjectOptimizationBasicThreshold = 1000;
			ObjectOptimizationFullThreshold = 250;
			VerticalSynchronization = true;
			// Upstream's default and minimum: the loudest 16 sources play, the rest are culled.
			SoundNumber = 16;
			/*
			 * Upstream's "gdiplus" option: decode PNGs through System.Drawing rather than the
			 * plugin's managed decoder. On Android System.Drawing is the Skia-backed shim, so this
			 * means native libpng. Upstream defaults it off on the desktop, where the two are close;
			 * here the managed decoder took ~18 ms a PNG, over a minute of the MTR route's load.
			 */
			UseGDIDecoders = true;
			LanguageCode = "en-US";
		}

		/// <summary>Settings are not yet persisted on Android.</summary>
		public override void Save(string fileName)
		{
		}
	}
}
