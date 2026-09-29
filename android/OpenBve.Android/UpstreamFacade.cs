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

using System.Collections.Generic;
using LibRender2;
using LibRender2.Overlays;
using OpenBveApi;
using OpenBveApi.FileSystem;
using OpenBveApi.Hosts;
using OpenBveApi.Interface;
using OpenBveApi.Math;
using RouteManager2;
using SoundManager;
using TrainManager;

namespace OpenBve
{
	/*
	 * A few files from the desktop application are compiled into the Android app unchanged (see
	 * the upstream links in OpenBve.Android.csproj). They reach the simulation through the
	 * desktop's statics - Program.Renderer, Program.CurrentRoute, TrainManager.PlayerTrain and so
	 * on - so these two types stand in for them. They hold references only; GameView fills them
	 * in as it creates the real objects.
	 *
	 * Within namespace OpenBve (and so OpenBve.Android), the name TrainManager now means this
	 * class rather than the TrainManager namespace, exactly as it does on desktop; Android code
	 * that needs the namespace in a qualified name writes global::TrainManager.
	 */

	/// <summary>The desktop application's global state, as the upstream files compiled here use it.</summary>
	internal static class Program
	{
		/// <summary>The host: plugin services, messages, and the shared random number generator.</summary>
		internal static OpenBveApi.Hosts.HostInterface CurrentHost;

		/// <summary>The renderer, for the camera, lighting, overlays and timetable state.</summary>
		internal static Graphics.NewRenderer Renderer;

		/// <summary>The file system, for the log and the interface's data folder.</summary>
		internal static FileSystem FileSystem;

		/// <summary>The route being driven.</summary>
		internal static CurrentRoute CurrentRoute;

		/// <summary>The sound system, to ask whether a sound is playing.</summary>
		internal static SoundsBase Sounds;

		/// <summary>The train manager, whose train list the AI drivers look along for the train ahead.</summary>
		internal static Android.AndroidTrainManager TrainManager;
	}

	/// <summary>
	/// The desktop's interface state: the options in force, and its message log. Partial, as on
	/// desktop: upstream's System/Logging/Score.cs adds the score texts.
	/// </summary>
	internal static partial class Interface
	{
		/// <summary>The options, as the desktop's Interface.CurrentOptions.</summary>
		internal static Android.AndroidOptions CurrentOptions;

		/// <summary>The desktop's load-error log, which here is the host's.</summary>
		internal static void AddMessage(MessageType type, bool fileNotFound, string text)
		{
			Program.CurrentHost.AddMessage(type, fileNotFound, text);
		}
	}

	/// <summary>The parts of the desktop's Game class the in-game overlays use (the rest is in the linked files).</summary>
	internal static partial class Game
	{
		/// <summary>The score messages being shown (arcade mode).</summary>
		internal static List<ScoreMessage> ScoreMessages = new List<ScoreMessage>();

		/// <summary>The on-screen size of the score message area.</summary>
		internal static Vector2 ScoreMessagesRendererSize = new Vector2(16.0, 16.0);

		/// <summary>The in-game route map and gradient profile overlay.</summary>
		internal static readonly RouteInfoOverlay RouteInfoOverlay = new RouteInfoOverlay();
	}

	/// <summary>
	/// The desktop's train manager type, so that <c>TrainManager.PlayerTrain</c> in upstream code
	/// reaches the shared static on <see cref="TrainManagerBase"/>. Never instantiated: the
	/// Android train manager is <see cref="Android.AndroidTrainManager"/>.
	/// </summary>
	internal abstract class TrainManager : TrainManagerBase
	{
		private TrainManager() : base(null, null, null, null)
		{
		}
	}
}

namespace OpenBve.Graphics
{
	/// <summary>
	/// The desktop renderer's in-game display options, which upstream's overlays read. The Android
	/// renderer derives from this rather than straight from <see cref="BaseRenderer"/>.
	/// </summary>
	public abstract class NewRenderer : BaseRenderer
	{
		protected NewRenderer(HostInterface host, BaseOptions options, FileSystem fileSystem) : base(host, options, fileSystem)
		{
		}

		internal bool OptionClock = false;
		internal GradientDisplayMode OptionGradient = GradientDisplayMode.None;
		internal SpeedDisplayMode OptionSpeed = SpeedDisplayMode.None;
		internal DistanceToNextStationDisplayMode OptionDistanceToNextStation = DistanceToNextStationDisplayMode.None;
		internal bool OptionFrameRates = false;
		internal bool OptionBrakeSystems = false;
	}
}

namespace OpenBve.Graphics.Renderers
{
	/// <summary>
	/// The developer overlays of upstream's Overlays.Debug.cs (frame statistics, the ATS plugin's
	/// panel values, brake system gauges), which read desktop-only renderer and option state and
	/// are not compiled here. The HUD, messages, score, timetable and route information are.
	/// </summary>
	internal partial class Overlays
	{
		private void RenderDebugOverlays()
		{
		}

		private void RenderATSDebugOverlay()
		{
		}

		private void RenderBrakeSystemDebug()
		{
		}
	}
}

namespace DavyKager
{
	/// <summary>Upstream's screen-reader bridge (Windows only). There is no screen reader to speak to here.</summary>
	internal static class Tolk
	{
		internal static bool Output(string text, bool interrupt = false) => false;
	}
}
