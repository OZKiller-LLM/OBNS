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
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

// ReSharper disable once CheckNamespace
namespace OpenTK.Graphics
{
	/// <summary>The graphics API the renderer runs on.</summary>
	public enum GraphicsBackend
	{
		/// <summary>The device's own OpenGL ES 3 driver.</summary>
		OpenGL = 0,

		/// <summary>
		/// Vulkan, through ANGLE: the renderer still issues OpenGL ES calls, and ANGLE's Vulkan
		/// back end turns them into Vulkan command buffers.
		/// </summary>
		Vulkan = 1
	}

	/// <summary>
	/// Decides which native libraries the GL and EGL bindings resolve to.
	/// </summary>
	/// <remarks>
	/// <para>
	/// LibRender2 is written against OpenGL throughout - buffers, VAOs, GLSL shaders, framebuffers -
	/// so a Vulkan renderer written from scratch would be a second renderer rather than a port.
	/// Instead the Vulkan backend loads ANGLE, which implements OpenGL ES on top of Vulkan (it is
	/// what Android itself uses to run GLES apps on Vulkan-only drivers). Every GL P/Invoke in this
	/// assembly names <c>libGLESv3.so</c> and every EGL one <c>libEGL.so</c>; a DllImport resolver
	/// sends both to ANGLE's libraries instead.
	/// </para>
	/// <para>
	/// A P/Invoke binds to its library on first call and stays bound, so the backend is fixed for
	/// the life of the process. <see cref="Select"/> must run before the first GL or EGL call, and a
	/// change of setting takes effect when the app next starts.
	/// </para>
	/// </remarks>
	public static class GraphicsLibraries
	{
		private static readonly object Sync = new object();
		private static bool selected;
		private static IntPtr angleGles;
		private static IntPtr angleEgl;

		/// <summary>The backend actually in use, which may differ from the one requested.</summary>
		public static GraphicsBackend Active { get; private set; } = GraphicsBackend.OpenGL;

		/// <summary>Where ANGLE was loaded from, or why it could not be.</summary>
		public static string Detail { get; private set; } = "system OpenGL ES driver";

		/// <summary>
		/// The app's private folder for its copy of the device's ANGLE (set by the app at start-up,
		/// e.g. <c>files/angle</c> in internal storage). See <see cref="EnsurePrivateCopy"/>.
		/// </summary>
		public static string PrivateFolder { get; set; }

		/*
		 * Where ANGLE is found on devices that ship it (Android 14 and later put it in the system
		 * image; some vendors keep it with their EGL drivers). The app does not ship ANGLE itself:
		 * it copies the device's own into its private folder on first use (EnsurePrivateCopy),
		 * because an app's linker namespace usually may not load libraries from these folders
		 * directly, although it may read them.
		 */
		private static readonly string[] SystemFolders =
		{
			Environment.Is64BitProcess ? "/system/lib64" : "/system/lib",
			Environment.Is64BitProcess ? "/system_ext/lib64" : "/system_ext/lib",
			Environment.Is64BitProcess ? "/vendor/lib64/egl" : "/vendor/lib/egl",
			Environment.Is64BitProcess ? "/vendor/lib64" : "/vendor/lib",
		};

		private const string EglName = "libEGL_angle.so", GlesName = "libGLESv2_angle.so";

		/*
		 * Where to try loading ANGLE from, in order: a copy packaged in the APK by whoever builds it
		 * (found by bare name, on the app's native library path), the app's private copy, and the
		 * system folders directly (which works where the device's linker allows it).
		 */
		private static IEnumerable<string[]> AngleCandidates()
		{
			yield return new[] { GlesName, EglName };
			if (!string.IsNullOrEmpty(PrivateFolder))
			{
				yield return new[] { Path.Combine(PrivateFolder, GlesName), Path.Combine(PrivateFolder, EglName) };
			}

			foreach (string folder in SystemFolders)
			{
				yield return new[] { folder + "/" + GlesName, folder + "/" + EglName };
			}
		}

		/// <summary>
		/// Copies the device's own ANGLE into <see cref="PrivateFolder"/> if it is not there yet
		/// (or the system's has changed, e.g. after an OS update). Nothing is downloaded or shipped:
		/// the libraries come from this device's system image, for this app's use only.
		/// </summary>
		/// <returns>Where the copy came from, or null if the device has no ANGLE to copy.</returns>
		public static string EnsurePrivateCopy()
		{
			if (string.IsNullOrEmpty(PrivateFolder))
			{
				return null;
			}

			foreach (string folder in SystemFolders)
			{
				string egl = folder + "/" + EglName, gles = folder + "/" + GlesName;
				try
				{
					if (!File.Exists(egl) || !File.Exists(gles))
					{
						continue;
					}

					Directory.CreateDirectory(PrivateFolder);
					foreach (string source in new[] { egl, gles })
					{
						string target = Path.Combine(PrivateFolder, Path.GetFileName(source));
						FileInfo from = new FileInfo(source), to = new FileInfo(target);
						if (!to.Exists || to.Length != from.Length || to.LastWriteTimeUtc < from.LastWriteTimeUtc)
						{
							string temporary = target + ".tmp";
							File.Copy(source, temporary, true);
							File.Move(temporary, target, true);
						}
					}

					return folder;
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
				{
					// Not readable here: try the next place.
				}
			}

			return null;
		}

		/// <summary>
		/// Chooses the backend. Falls back to OpenGL, and says why in <see cref="Detail"/>, if
		/// Vulkan was asked for but ANGLE cannot be loaded.
		/// </summary>
		/// <returns>The backend that will be used.</returns>
		public static GraphicsBackend Select(GraphicsBackend requested)
		{
			lock (Sync)
			{
				if (selected)
				{
					return Active;
				}

				selected = true;
				if (requested == GraphicsBackend.Vulkan)
				{
					string copiedFrom = EnsurePrivateCopy();
					List<string> failures = new List<string>();
					foreach (string[] candidate in AngleCandidates())
					{
						/*
						 * GLES first: ANGLE's libEGL names libGLESv2_angle.so as a dependency by bare
						 * name, which the linker can only satisfy from a folder on its search path or
						 * from a library of that name already loaded - the case for the private copy.
						 */
						if (NativeLibrary.TryLoad(candidate[0], out IntPtr gles))
						{
							if (NativeLibrary.TryLoad(candidate[1], out IntPtr egl))
							{
								angleEgl = egl;
								angleGles = gles;
								Active = GraphicsBackend.Vulkan;
								Detail = "ANGLE from " + candidate[1] + (copiedFrom != null && PrivateFolder != null && candidate[1].StartsWith(PrivateFolder, StringComparison.Ordinal) ? " (the device's own, copied from " + copiedFrom + ")" : string.Empty);
								break;
							}

							NativeLibrary.Free(gles);
						}

						failures.Add(candidate[1]);
					}

					if (Active != GraphicsBackend.Vulkan)
					{
						Detail = "Vulkan requested, but ANGLE could not be loaded (tried " + string.Join(", ", failures) + "); using OpenGL ES";
					}
				}

				NativeLibrary.SetDllImportResolver(typeof(GraphicsLibraries).Assembly, Resolve);
				return Active;
			}
		}

		/// <summary>
		/// Whether ANGLE can be loaded in this app, for the Options screen. Loads and releases the
		/// library without selecting anything, so it may be called from a process that never draws.
		/// </summary>
		public static bool IsAngleAvailable()
		{
			EnsurePrivateCopy();
			foreach (string[] candidate in AngleCandidates())
			{
				if (NativeLibrary.TryLoad(candidate[0], out IntPtr gles))
				{
					bool egl = NativeLibrary.TryLoad(candidate[1], out IntPtr eglHandle);
					if (egl) NativeLibrary.Free(eglHandle);
					NativeLibrary.Free(gles);
					if (egl) return true;
				}
			}

			return false;
		}

		private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
		{
			if (Active != GraphicsBackend.Vulkan)
			{
				// IntPtr.Zero falls through to the default probing, i.e. the system driver.
				return IntPtr.Zero;
			}

			switch (libraryName)
			{
				case "libGLESv3.so":
				case "libGLESv2.so":
					return angleGles;
				case "libEGL.so":
					return angleEgl;
				default:
					return IntPtr.Zero;
			}
		}
	}
}
