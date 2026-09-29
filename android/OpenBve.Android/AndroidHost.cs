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
using System.Threading.Tasks;
using Android.Util;
using LibRender2;
using OpenBveApi;
using OpenBveApi.Graphics;
using OpenBveApi.Hosts;
using OpenBveApi.Interface;
using OpenBveApi.Textures;
using OpenBveApi.Trains;
using OpenBveApi.Colors;
using RouteManager2.MessageManager;
// The Texture.Tga / Texture.Dds plugin assemblies put a namespace called Texture at the root,
// which otherwise shadows the texture type itself.
using ApiTexture = OpenBveApi.Textures.Texture;

namespace OpenBve.Android
{
	/// <summary>
	/// The Android host. Upstream's desktop host lives in the Windows Forms `OpenBVE` project;
	/// this is the equivalent for the Android front end.
	/// </summary>
	/// <remarks>
	/// Most of <see cref="HostInterface"/> is virtual with workable defaults, so this starts
	/// minimal and grows as the front end gains the ability to load content. Everything it does
	/// override is reported to logcat, which is how route loading problems will surface.
	/// </remarks>
	public partial class AndroidHost : HostInterface
	{
		private const string Tag = "OpenBVE";

		/// <summary>Creates the host.</summary>
		public AndroidHost() : base(HostApplication.OpenBve)
		{
		}

		/// <summary>The number of messages reported as errors so far.</summary>
		public int ErrorCount { get; private set; }

		/// <summary>
		/// The renderer, set once it exists. Texture loading routes through its texture manager,
		/// exactly as the desktop host routes through Program.Renderer.
		/// </summary>
		public BaseRenderer Renderer { get; set; }

		/// <summary>The options in force, needed for the interpolation and filtering settings.</summary>
		public AndroidOptions Options { get; set; }

		// --- textures ---

		/// <inheritdoc />
		public override bool LoadTexture(ref ApiTexture texture, OpenGlTextureWrapMode wrapMode)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("texture upload");
			if (Renderer == null)
			{
				return false;
			}

			if (StreamTextures && !(Renderer is AndroidRenderer { DrawingCab: true }) &&
			    texture != null && texture.OpenGlTextures != null && !texture.OpenGlTextures[(int)wrapMode].Valid &&
			    !texture.MultipleFrames && !texture.Ignore && texture.Origin is PathOrigin origin)
			{
				return StreamTexture(ref texture, wrapMode, origin);
			}

			return Renderer.TextureManager.LoadTexture(ref texture, wrapMode, CPreciseTimer.GetClockTicks(),
				Options.Interpolation, Options.AnisotropicFilteringLevel);
		}

		// --- texture streaming ---

		/// <summary>
		/// Whether textures first needed during the simulation are decoded in the background.
		/// Off while loading, so the first frame is complete; GameView turns it on once driving starts.
		/// </summary>
		public bool StreamTextures { get; set; }

		/// <summary>Milliseconds per frame that streamed textures may spend uploading to the GPU (at least one is always allowed).</summary>
		public double StreamUploadMillisecondsPerFrame { get; set; } = 6.0;

		/*
		 * At most this many textures decoding or decoded and waiting. Without a cap, a view that
		 * needs 400 textures decodes all of them at once and holds every decoded image in memory
		 * until its turn to upload: on the test phone that took resident memory from 550 MB to
		 * 1.15 GB, which is the low-memory killer's territory. Textures past the cap simply
		 * start decoding on a later frame.
		 */
		private const int MaxOutstandingDecodes = 16;

		/*
		 * Upstream decodes an image the first time a face needs it, on the render thread, and
		 * waits. A PNG takes about 13 ms to decode on the test phone, so a view that brings a
		 * few hundred new textures at once - the whole train from outside, for one - froze the
		 * simulation for seconds. Decoding is pure CPU work; only the upload needs the GL
		 * thread. So once driving has started, a texture that is not yet on the GPU is decoded
		 * on a worker thread while its faces are drawn untextured for those few frames, and
		 * uploaded, a few per frame, when ready. Render thread only, apart from the decoding.
		 */
		private readonly Dictionary<object, Task<ApiTexture>> decoding = new Dictionary<object, Task<ApiTexture>>(ReferenceEqualityComparer.Instance);

		/// <summary>A decoded texture being handed to TextureManager, which asks the origin (and so this host) for it.</summary>
		[ThreadStatic] private static ApiTexture handoff;

		private int uploadsThisFrame;
		private readonly System.Diagnostics.Stopwatch uploadClock = new System.Diagnostics.Stopwatch();

		/// <summary>Resets the per-frame upload budget. Call once per frame, before drawing.</summary>
		public void BeginFrame()
		{
			uploadsThisFrame = 0;
			uploadClock.Reset();
		}

		/// <summary>Textures waiting for a background decode, for the performance log.</summary>
		public int TexturesDecoding => decoding.Count;

		/// <summary>
		/// Decodes a texture on a worker thread, and does there the per-pixel work that
		/// TextureManager would otherwise do on the render thread at upload time.
		/// </summary>
		/// <remarks>
		/// TextureManager scans every pixel to classify transparency (the result is cached on the
		/// texture, so computing it here is enough), and uploads an opaque RGBA image by copying it
		/// into an RGB buffer pixel by pixel. Handing it that RGB image instead takes its RGB branch,
		/// which uploads the same pixels as-is: upstream sets the unpack alignment explicitly in
		/// both branches, so the result on the GPU is identical.
		/// </remarks>
		private static ApiTexture DecodeForUpload(PathOrigin origin)
		{
			if (!origin.GetTexture(out ApiTexture decoded) || decoded == null)
			{
				return null;
			}

			if (decoded.MultipleFrames || decoded.GetTransparencyType() != TextureTransparencyType.Opaque || decoded.PixelFormat != PixelFormat.RGBAlpha)
			{
				return decoded;
			}

			byte[] rgba = decoded.Bytes;
			byte[] rgb = new byte[decoded.Width * decoded.Height * 3];
			for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
			{
				rgb[j] = rgba[i];
				rgb[j + 1] = rgba[i + 1];
				rgb[j + 2] = rgba[i + 2];
			}

			return new ApiTexture(decoded.Width, decoded.Height, PixelFormat.RGB, rgb, decoded.Palette);
		}

		private bool StreamTexture(ref ApiTexture texture, OpenGlTextureWrapMode wrapMode, PathOrigin origin)
		{
			if (!decoding.TryGetValue(origin, out Task<ApiTexture> task))
			{
				if (decoding.Count < MaxOutstandingDecodes)
				{
					decoding[origin] = Task.Run(() => DecodeForUpload(origin));
				}

				return false;
			}

			if (!task.IsCompleted || (uploadsThisFrame > 0 && uploadClock.Elapsed.TotalMilliseconds >= StreamUploadMillisecondsPerFrame))
			{
				return false;
			}

			decoding.Remove(origin);
			uploadsThisFrame++;
			ApiTexture result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
			if (result == null)
			{
				// It failed in the background; let upstream try, and report, as it always would.
				return Renderer.TextureManager.LoadTexture(ref texture, wrapMode, CPreciseTimer.GetClockTicks(),
					Options.Interpolation, Options.AnisotropicFilteringLevel);
			}

			handoff = result;
			uploadClock.Start();
			try
			{
				return Renderer.TextureManager.LoadTexture(ref texture, wrapMode, CPreciseTimer.GetClockTicks(),
					Options.Interpolation, Options.AnisotropicFilteringLevel);
			}
			finally
			{
				uploadClock.Stop();
				handoff = null;
			}
		}

		/// <inheritdoc />
		public override bool RegisterTexture(ApiTexture texture, TextureParameters parameters, out ApiTexture handle)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("texture register");
			if (Renderer == null)
			{
				handle = null;
				return false;
			}

			handle = Renderer.TextureManager.RegisterTexture(texture.ApplyParameters(parameters));
			return true;
		}

		/// <inheritdoc />
		public override bool RegisterTexture(global::System.Drawing.Bitmap texture, TextureParameters parameters, out ApiTexture handle)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("texture register");
			handle = new ApiTexture(texture, parameters);
			return true;
		}

		/*
		 * Registration results by path, for this process (one game). Upstream's TextureManager
		 * checks the file exists and then scans every texture registered so far, comparing paths
		 * case-insensitively - quadratic over a load, and each existence check is a round trip
		 * through Android's shared-storage layer. The MTR route registers 36,000 textures, most
		 * of them repeats: 18 s of its load went here. The cache matches exactly as that scan
		 * does (same path ignoring case, equal parameters), so it returns the same handles.
		 */
		private readonly Dictionary<string, List<(TextureParameters Parameters, ApiTexture Handle)>> registeredTextures =
			new Dictionary<string, List<(TextureParameters, ApiTexture)>>(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, bool> existingPaths = new Dictionary<string, bool>();

		/*
		 * Set while TextureManager.RegisterTexture runs. Its only call back into LoadTexture(path) is
		 * the Texture(path) constructor decoding the whole image to read its PixelFormat, which only
		 * ever fills in the handle's PixelFormat; the pixels are thrown away and decoded again when
		 * the texture is first drawn (the upload uses that second decode's format). For the image
		 * formats below, with native decoding on (AndroidOptions.UseGDIDecoders), every path of the
		 * BMP/GIF/JPEG/PNG/TIFF plugin builds an RGBAlpha texture, so the answer is known without
		 * decoding. That probe was 40 of the MTR route's 76 s of loading.
		 */
		[ThreadStatic] private static bool probingFormat;

		private static readonly HashSet<string> AlwaysRgbaExtensions =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".bmp", ".jpg", ".jpeg", ".gif", ".tif", ".tiff" };

		/// <summary>The stand-in handed back to a format probe: right format, no pixels decoded.</summary>
		private bool TryAnswerFormatProbe(string path, out ApiTexture texture)
		{
			texture = null;
			if (!probingFormat || Options == null || !Options.UseGDIDecoders || !AlwaysRgbaExtensions.Contains(System.IO.Path.GetExtension(path)))
			{
				return false;
			}

			texture = new ApiTexture(1, 1, PixelFormat.RGBAlpha, new byte[4], null);
			return true;
		}

		/// <summary>Whether a file or folder exists, remembered for the process: content does not change mid-game.</summary>
		internal bool PathExists(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}

			lock (existingPaths)
			{
				if (!existingPaths.TryGetValue(path, out bool exists))
				{
					exists = File.Exists(path) || Directory.Exists(path);
					existingPaths[path] = exists;
				}

				return exists;
			}
		}

		/// <inheritdoc />
		public override bool RegisterTexture(string path, TextureParameters parameters, out ApiTexture handle, bool loadTexture = false, int timeout = 1000)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("texture register");
			handle = null;
			if (Renderer == null || !PathExists(path))
			{
				return false;
			}

			ApiTexture data = null;
			lock (registeredTextures)
			{
				if (registeredTextures.TryGetValue(path, out var known))
				{
					foreach (var entry in known)
					{
						if (entry.Parameters == parameters)
						{
							data = entry.Handle;
							break;
						}
					}
				}

				if (data == null)
				{
					bool registered;
					probingFormat = true;
					try
					{
						registered = Renderer.TextureManager.RegisterTexture(path, parameters, out data);
					}
					finally
					{
						probingFormat = false;
					}

					if (!registered)
					{
						return false;
					}

					if (!registeredTextures.TryGetValue(path, out known))
					{
						known = new List<(TextureParameters, ApiTexture)>();
						registeredTextures[path] = known;
					}

					known.Add((parameters, data));
				}
			}

			handle = data;
			if (loadTexture)
			{
				/*
				 * Upstream loads on a separate thread and queues the upload for the render thread,
				 * waiting up to the timeout. Android loads on the render thread itself, where that
				 * queue is never serviced while the load runs: every caller waited the full timeout
				 * and then went without its texture - a 2D panel spent 20 s per element and built
				 * each one at zero size. On the render thread, upload directly.
				 */
				if (OpenTK.AndroidGraphicsContext.RenderThreadId == Environment.CurrentManagedThreadId)
				{
					LoadTexture(ref data, OpenGlTextureWrapMode.ClampClamp);
				}
				else
				{
					Renderer.RunInRenderThread(() => LoadTexture(ref data, OpenGlTextureWrapMode.ClampClamp), timeout);
				}
			}

			return true;
		}

		/// <inheritdoc />
		public override void AddMessage(MessageType type, bool fileNotFound, string text)
		{
			switch (type)
			{
				case MessageType.Critical:
				case MessageType.Error:
					ErrorCount++;
					Log.Error(Tag, text);
					break;
				case MessageType.Warning:
					Log.Warn(Tag, text);
					break;
				default:
					Log.Info(Tag, text);
					break;
			}
		}

		/*
		 * The rest of upstream's Host overrides that reach the game: without them the base
		 * versions silently do nothing, so score events, route markers and a cab's custom
		 * timetable object were lost.
		 */

		/// <inheritdoc />
		public override void AddScore(int Score, string Message, MessageColor Color, double Timeout)
		{
			Game.ScoreMessages.Add(new ScoreMessage(Score, Message, Color, Timeout));
		}

		/// <inheritdoc />
		public override void AddMarker(ApiTexture MarkerTexture, OpenBveApi.Math.Vector2 Size)
		{
			Renderer?.Marker.AddMarker(MarkerTexture, Size);
		}

		/// <inheritdoc />
		public override void RemoveMarker(ApiTexture MarkerTexture)
		{
			Renderer?.Marker.RemoveMarker(MarkerTexture);
		}

		/// <inheritdoc />
		public override void AddObjectForCustomTimeTable(OpenBveApi.Objects.AnimatedObject animatedObject)
		{
			Timetable.AddObjectForCustomTimetable(animatedObject);
		}

		/// <inheritdoc />
		public override void CameraAtWorldEnd()
		{
			if (Renderer != null)
			{
				Renderer.Camera.AtWorldEnd = !Renderer.Camera.AtWorldEnd;
			}
		}

		/// <inheritdoc />
		/// <remarks>An in-game message (from the simulation or a plugin), shown by the HUD, as upstream's host.</remarks>
		public override void AddMessage(object message)
		{
			if (message is string text)
			{
				MessageManager.AddMessage(text, MessageDependency.None, GameMode.Expert, MessageColor.Black, 10, null);
			}
			else if (message is RouteManager2.MessageManager.AbstractMessage abstractMessage)
			{
				MessageManager.AddMessage(abstractMessage);
			}
			else if (message != null)
			{
				Log.Warn(Tag, "unexpected in-game message object: " + message.GetType().FullName);
			}
		}

		/// <inheritdoc />
		/// <remarks>An in-game message with its conditions (station, signal, doors...), shown by the HUD.</remarks>
		public override void AddMessage(string message, object dependency, GameMode mode, MessageColor color, double timeout, string key)
		{
			MessageManager.AddMessage(message, (MessageDependency)dependency, mode, color, timeout, key);
		}

		/// <inheritdoc />
		/// <remarks>A route's own timetable image for the station just reached (upstream's Host).</remarks>
		public override void UpdateCustomTimetable(ApiTexture daytime, ApiTexture nighttime)
		{
			Timetable.UpdateCustomTimetable(daytime, nighttime);
		}

		/// <inheritdoc />
		public override void ReportProblem(ProblemType type, string text)
		{
			ErrorCount++;
			Log.Error(Tag, type + ": " + text);
		}

		/// <summary>The file system, for resolving the train folders track following objects name.</summary>
		public OpenBveApi.FileSystem.FileSystem FileSystem { get; set; }

		/// <summary>The player's train folder, the last place a track following object's train is looked for.</summary>
		public string CurrentTrainFolder { get; set; } = string.Empty;

		/// <summary>For measurements: load no track following objects (the <c>skip_tfo</c> intent extra).</summary>
		public bool SkipTrackFollowingObjects { get; set; }

		/// <inheritdoc />
		public override AbstractTrain ParseTrackFollowingObject(string objectPath, string tfoFile)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("scripted trains");
			if (SkipTrackFollowingObjects)
			{
				return null;
			}

			try
			{
				return TrackFollowingObjectParser.Parse(this, objectPath, tfoFile);
			}
			catch (Exception ex)
			{
				AddMessage(MessageType.Error, false, "Could not load the track following object " + tfoFile + ": " + ex.Message);
				return null;
			}
		}
	}
}
