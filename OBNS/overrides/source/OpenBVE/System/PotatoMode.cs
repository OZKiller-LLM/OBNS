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

// OBNS: not an upstream file. Potato mode on the desktop (the user's choice, 2026-11-09): Android's Potato mode (commit
// 3beb50a) less its 16-bit textures, for slow computers. Set in the options (ObnsOptions.PotatoMode, obns.cfg) and put in
// force when a game starts (MainLoop.StartLoopEx).

using System;
using System.Runtime.InteropServices;
using LibRender2.MotionBlurs;
using OpenBveApi.Graphics;
using OpenBveApi.Interface;

namespace OpenBve
{
	internal static class Potato
	{
		/// <summary>Whether Potato mode is in force for this game.</summary>
		internal static bool Active { get; private set; }

		/// <summary>The cap on textures' width and height in Potato mode, as Android's.</summary>
		private const int TextureLimit = 1024;

		/// <summary>The transparency mode the options hold, where this game uses another (saved in its place: Options.Save).</summary>
		private static TransparencyMode? optionsTransparency;
		private static TransparencyMode usedTransparency;

		/// <summary>
		/// The options' anti-aliasing, texture filtering and motion blur, where Potato mode uses its own for this game (saved in
		/// their place: Options.Save). On a slow GPU each costs memory bandwidth for little to see (the user, 2026-11-11, for a
		/// Broadwell MacBook): multisampling multiplies the frame's colour and depth traffic, trilinear and anisotropic
		/// filtering read more texels a pixel, and motion blur copies the whole frame back each frame.
		/// </summary>
		private static int? optionsAntiAliasing;
		private static InterpolationMode? optionsInterpolation;
		private static int? optionsAnisotropy;
		private static MotionBlurMode? optionsMotionBlur;
		private static ShadowMapResolution? optionsShadows;

		/// <summary>
		/// Puts Potato mode in force, or not, for the game about to start. OBNS_POTATO=1 or 0 overrides the option (for tests,
		/// as Android's potato_mode extra); OBNS_TRANSPARENCY=performance or quality sets the transparency for this game, and
		/// wins over Potato mode's, as an Android extra that sets one of its parts does.
		/// </summary>
		internal static void Apply()
		{
			// The options' transparency back, from a game before this one in the same process.
			if (optionsTransparency != null)
			{
				if (Interface.CurrentOptions.TransparencyMode == usedTransparency)
				{
					Interface.CurrentOptions.TransparencyMode = optionsTransparency.Value;
				}

				optionsTransparency = null;
			}

			// The options' anti-aliasing, filtering and motion blur back, likewise.
			RestoreQualityOptions();
			Graphics.SceneScale.Reset();

			string potatoSwitch = Environment.GetEnvironmentVariable("OBNS_POTATO");
			bool potato = potatoSwitch == "1" ? true : potatoSwitch == "0" ? false : ObnsOptions.PotatoMode;
			Active = potato;

			string transparencySwitch = (Environment.GetEnvironmentVariable("OBNS_TRANSPARENCY") ?? string.Empty).Trim().ToLowerInvariant();
			TransparencyMode? transparency = transparencySwitch == "performance" ? TransparencyMode.Performance
				: transparencySwitch == "quality" ? TransparencyMode.Quality
				: potato ? TransparencyMode.Performance : (TransparencyMode?)null;
			if (transparency != null && transparency.Value != Interface.CurrentOptions.TransparencyMode)
			{
				// The options keep theirs: what is saved during and after this game is the options' value (Options.Save).
				optionsTransparency = Interface.CurrentOptions.TransparencyMode;
				usedTransparency = transparency.Value;
				Interface.CurrentOptions.TransparencyMode = transparency.Value;
			}

			// No multisampling, bilinear mipmapped filtering and no motion blur (the window is made after this, Screen.Initialize).
			if (potato)
			{
				LowerQualityOptions();
			}

			// Potato mode's world shader for OpenGL (no shadows; lighting and fog per vertex), chosen as the renderer starts.
			LibRender2.BaseRenderer.ObnsPotatoShader = potato;

			// Textures at most 1024 px, as they are decoded (Host.LoadTexture): one place for every renderer.
			TextureSizeLimit.MaxSize = potato ? TextureLimit : 0;
			// Cut-outs kept cut-outs as they are reduced (OpenBveApi's rule, at most 3% edge pixels): else they are classed
			// translucent, drawn without depth in Sharp transparency, and their parts show through each other.
			TextureSizeLimit.KeepColourKeys = potato;
			// Colour-keyed faces in one cut-out pass (where the transparency is Quality: NewRenderer).
			Graphics.NewRenderer.CutoutKeyedFaces = potato;
			// Cars the camera hardly sees, and scripted trains far along the line, updated less often (TrainManagerBase).
			global::TrainManager.TrainManagerBase.CarUpdateLod = potato;

			string hints = potato ? PowerHints() : null;
			Program.FileSystem.AppendToLogFile(potato
				? "OBNS Potato mode: " + Interface.CurrentOptions.TransparencyMode + " transparency, textures at most " + TextureLimit + " px, unused textures unloaded, colour-keyed faces in one pass, far cars and trains updated less often, no anti-aliasing, " + Interface.CurrentOptions.Interpolation + " filtering, no motion blur, no shadows, OpenGL's simpler world shader, the OpenGL scene at most 1280x800's pixels" + (ObnsOptions.PotatoSceneScale > 0 ? " (obns.cfg: " + ObnsOptions.PotatoSceneScale + "%)" : string.Empty) + "; " + hints
				: "OBNS Potato mode: off");
		}

		/// <summary>Potato mode's anti-aliasing, filtering and motion blur for this game, the options' own kept to be saved.</summary>
		private static void LowerQualityOptions()
		{
			Interface.Options o = Interface.CurrentOptions;
			if (o.AntiAliasingLevel != 0)
			{
				optionsAntiAliasing = o.AntiAliasingLevel;
				o.AntiAliasingLevel = 0;
			}

			// Trilinear and anisotropic filtering become bilinear between mipmaps; the plainer modes the options may hold stay.
			if (o.Interpolation == InterpolationMode.TrilinearMipmapped || o.Interpolation == InterpolationMode.AnisotropicFiltering)
			{
				optionsInterpolation = o.Interpolation;
				// The renderer clears the anisotropy level when the filtering isn't anisotropic (BaseRenderer.Initialize).
				optionsAnisotropy = o.AnisotropicFilteringLevel;
				o.Interpolation = InterpolationMode.BilinearMipmapped;
				o.AnisotropicFilteringLevel = 0;
			}

			if (o.MotionBlur != MotionBlurMode.None)
			{
				optionsMotionBlur = o.MotionBlur;
				o.MotionBlur = MotionBlurMode.None;
			}

			// No shadows: their pass draws the scene again into each cascade, and Potato mode's world shader has none.
			if (o.ShadowResolution != ShadowMapResolution.Off)
			{
				optionsShadows = o.ShadowResolution;
				o.ShadowResolution = ShadowMapResolution.Off;
			}
		}

		/// <summary>The options' own anti-aliasing, filtering and motion blur back (a game before this one used Potato mode's).</summary>
		private static void RestoreQualityOptions()
		{
			Interface.Options o = Interface.CurrentOptions;
			if (optionsAntiAliasing != null)
			{
				o.AntiAliasingLevel = optionsAntiAliasing.Value;
				optionsAntiAliasing = null;
			}

			if (optionsInterpolation != null)
			{
				o.Interpolation = optionsInterpolation.Value;
				optionsInterpolation = null;
			}

			if (optionsAnisotropy != null)
			{
				o.AnisotropicFilteringLevel = optionsAnisotropy.Value;
				optionsAnisotropy = null;
			}

			if (optionsMotionBlur != null)
			{
				o.MotionBlur = optionsMotionBlur.Value;
				optionsMotionBlur = null;
			}

			if (optionsShadows != null)
			{
				o.ShadowResolution = optionsShadows.Value;
				optionsShadows = null;
			}
		}

		/// <summary>The anti-aliasing level to save in options.cfg: the options' own while Potato mode uses none.</summary>
		internal static int AntiAliasingToSave(int current) => optionsAntiAliasing ?? current;

		/// <summary>The filtering to save in options.cfg: the options' own while Potato mode uses bilinear.</summary>
		internal static InterpolationMode InterpolationToSave(InterpolationMode current) => optionsInterpolation ?? current;

		/// <summary>The anisotropy level to save in options.cfg: the options' own while Potato mode uses none.</summary>
		internal static int AnisotropyToSave(int current) => optionsAnisotropy ?? current;

		/// <summary>The shadow resolution to save in options.cfg: the options' own while Potato mode has shadows off.</summary>
		internal static ShadowMapResolution ShadowsToSave(ShadowMapResolution current) => optionsShadows ?? current;

		/// <summary>The motion blur to save in options.cfg: the options' own while Potato mode uses none.</summary>
		internal static MotionBlurMode MotionBlurToSave(MotionBlurMode current) => optionsMotionBlur ?? current;

		/// <summary>Whether unused textures are unloaded: upstream's option, or Potato mode.</summary>
		internal static bool UnloadUnusedTextures => Active || Interface.CurrentOptions.UnloadUnusedTextures;

		/// <summary>The transparency mode to save in options.cfg: the options' own while this game uses another.</summary>
		internal static TransparencyMode TransparencyToSave(TransparencyMode current)
		{
			return optionsTransparency != null && current == usedTransparency ? optionsTransparency.Value : current;
		}

		// --- asking the system for full speed ---

		private static bool hinted;
		private static IntPtr macActivity;

		/// <summary>Asks the system to run the game at full speed (once a process; it lasts until the process ends).</summary>
		private static string PowerHints()
		{
			if (hinted)
			{
				return "power hints already given";
			}

			hinted = true;
			try
			{
				switch (Program.CurrentHost.Platform)
				{
					case OpenBveApi.Hosts.HostPlatform.MicrosoftWindows:
						return WindowsPowerHints();
					case OpenBveApi.Hosts.HostPlatform.AppleOSX:
						return MacPowerHints();
					default:
						// Linux: nothing a program can safely ask for without privileges (the CPU governor and the GPU's power
						// profile are the system's; the high-performance GPU of a laptop is chosen with DRI_PRIME or the like).
						return "no power hints on this system";
				}
			}
			catch (Exception ex)
			{
				return "power hints failed: " + ex.Message;
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct ProcessPowerThrottlingState
		{
			public uint Version;
			public uint ControlMask;
			public uint StateMask;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref ProcessPowerThrottlingState information, uint size);

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentProcess();

		/// <summary>
		/// Windows: the process opted out of power throttling (EcoQoS: Windows 10 1709 and later run a process it judges to be
		/// in the background on efficient cores or at low clocks); the high-performance GPU for Direct3D drawing alone
		/// (D3D11Device and D3D12Device; Vulkan takes the discrete GPU there already). OpenGL's GPU is the system's choice.
		/// </summary>
		private static string WindowsPowerHints()
		{
			const int processPowerThrottling = 4; // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
			const uint executionSpeed = 1; // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
			ProcessPowerThrottlingState state = new ProcessPowerThrottlingState
			{
				Version = 1, // PROCESS_POWER_THROTTLING_CURRENT_VERSION
				ControlMask = executionSpeed,
				StateMask = 0 // the control taken, its state off: never throttled
			};
			bool done;
			try
			{
				done = SetProcessInformation(GetCurrentProcess(), processPowerThrottling, ref state, (uint)Marshal.SizeOf(typeof(ProcessPowerThrottlingState)));
			}
			catch (EntryPointNotFoundException)
			{
				// Windows 7: no power throttling to opt out of.
				return "no power throttling on this Windows; high-performance GPU for Direct3D";
			}

			return (done ? "power throttling off" : "power throttling not changed (error " + Marshal.GetLastWin32Error() + ")") + "; high-performance GPU for Direct3D";
		}

		/// <summary>
		/// macOS: an activity begun with NSProcessInfo, user-initiated and latency-critical: App Nap and timer coalescing kept
		/// off and the system told the game's work is urgent, for as long as the process runs.
		/// </summary>
		private static string MacPowerHints()
		{
			Graphics.Metal.Mtl.LoadFrameworks();
			IntPtr pool = Graphics.Metal.Mtl.objc_autoreleasePoolPush();
			try
			{
				const ulong userInitiated = 0x00FFFFFFUL; // NSActivityUserInitiated
				const ulong latencyCritical = 0xFF00000000UL; // NSActivityLatencyCritical
				IntPtr info = Graphics.Metal.Mtl.Id(Graphics.Metal.Mtl.Class("NSProcessInfo"), Graphics.Metal.Mtl.Sel("processInfo"));
				IntPtr reason = Graphics.Metal.Mtl.String("OpenBVE Potato mode: a game is running");
				// beginActivityWithOptions:reason: (NSActivityOptions is a 64-bit integer, passed as the pointer-sized argument it is).
				IntPtr activity = Graphics.Metal.Mtl.Id(info, Graphics.Metal.Mtl.Sel("beginActivityWithOptions:reason:"), (IntPtr)unchecked((long)(userInitiated | latencyCritical)), reason);
				Graphics.Metal.Mtl.Release(reason);
				// Kept (retained) while the process runs: the activity ends when its object goes.
				macActivity = Graphics.Metal.Mtl.Retain(activity);
				return macActivity != IntPtr.Zero ? "activity begun (user-initiated, latency-critical)" : "no activity begun";
			}
			finally
			{
				Graphics.Metal.Mtl.objc_autoreleasePoolPop(pool);
			}
		}
	}
}
