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
using System.IO;
using Android.Content;
using Android.OS;

namespace OpenBve.Android
{
	/*
	 * Keeping native Vulkan from stopping the game on a device whose driver cannot run it. Native
	 * Vulkan is the default; OpenGL ES takes over when:
	 *
	 *   - the device reports no Vulkan 1.1 (VulkanUsable);
	 *   - making the Vulkan device fails (GameView.CreateVulkanOrFallBack records it here);
	 *   - the last start with native Vulkan did not get through making the device, or through its
	 *     first frames: a marker file is written before each and removed after, so a crash in the
	 *     driver (which takes the process with it) leaves the marker behind for the next start.
	 *
	 * The failure is kept per device and system build (Build.Fingerprint), so an Android update
	 * tries Vulkan again; choosing native Vulkan in Options also tries again. Files rather than
	 * preferences, since the menu and the game are separate processes.
	 */
	internal static class VulkanStartup
	{
		private const string MarkerName = "vulkan-native-starting";
		private const string FailedName = "vulkan-native-failed";

		private static string PathOf(Context context, string name) => Path.Combine(context.FilesDir.AbsolutePath, name);

		/// <summary>Whether the device reports Vulkan 1.1 or later (what the native renderer needs).</summary>
		public static bool VulkanUsable(Context context) =>
			context.PackageManager?.HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureVulkanHardwareVersion, 0x00401000) == true;

		/// <summary>Why native Vulkan failed on this device and system build, or null if it has not.</summary>
		public static string FailureOnThisDevice(Context context)
		{
			try
			{
				string path = PathOf(context, FailedName);
				if (!File.Exists(path))
				{
					return null;
				}

				string[] lines = File.ReadAllLines(path);
				return lines.Length > 0 && lines[0] == Build.Fingerprint ? (lines.Length > 1 ? lines[1] : "unknown") : null;
			}
			catch (IOException)
			{
				return null;
			}
		}

		/// <summary>Records that native Vulkan failed on this device (OpenGL ES from now on, until retried).</summary>
		public static void MarkFailed(Context context, string reason)
		{
			try
			{
				File.WriteAllLines(PathOf(context, FailedName), new[] { Build.Fingerprint, reason.Replace('\n', ' ') });
			}
			catch (IOException)
			{
			}
		}

		/// <summary>Forgets a failure (the player chose native Vulkan again).</summary>
		public static void ClearFailed(Context context)
		{
			try
			{
				File.Delete(PathOf(context, FailedName));
				File.Delete(PathOf(context, MarkerName));
			}
			catch (IOException)
			{
			}
		}

		/// <summary>A risky step begins (making the device, the first frames): left behind if the process dies in it.</summary>
		public static void Begin(Context context, string step)
		{
			try
			{
				File.WriteAllText(PathOf(context, MarkerName), step);
			}
			catch (IOException)
			{
			}
		}

		/// <summary>The risky step is over.</summary>
		public static void Done(Context context)
		{
			try
			{
				File.Delete(PathOf(context, MarkerName));
			}
			catch (IOException)
			{
			}
		}

		/// <summary>
		/// If the last start with native Vulkan died in a risky step, records the failure (and clears
		/// the marker). Returns the step it died in, or null.
		/// </summary>
		public static string CheckInterrupted(Context context)
		{
			try
			{
				string path = PathOf(context, MarkerName);
				if (!File.Exists(path))
				{
					return null;
				}

				string step = File.ReadAllText(path);
				File.Delete(path);
				MarkFailed(context, "the game stopped during " + step + " with native Vulkan");
				return step;
			}
			catch (IOException)
			{
				return null;
			}
		}
	}
}
