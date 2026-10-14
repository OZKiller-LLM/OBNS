// ANDROID PORT: a copy of upstream's LibRender2/Textures/TextureManager.cs (OpenBVE 1.14.1.2), compiled
// in its place (LibRender2.Android.csproj), made by Android/UpstreamPatches/make_texturemanager_android.py:
// the texture cache holds transparency types only, and no decoded texture is kept from registration
// to upload (see that script). Do not edit by hand: change the script and run it again.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using LibRender2.Screens;
using OpenBveApi;
using OpenBveApi.Hosts;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;
using InterpolationMode = OpenBveApi.Graphics.InterpolationMode;
using PixelFormat = OpenBveApi.Textures.PixelFormat;

namespace LibRender2.Textures
{
	/// <summary>Provides functions for dealing with textures.</summary>
	public class TextureManager
	{
		private readonly HostInterface currentHost;

		private readonly BaseRenderer renderer;

		/// <summary>Holds all currently registered textures.</summary>
		public static Texture[] RegisteredTextures;
		/// <summary>The transparency type of each texture classified so far, by origin.</summary>
		/// <remarks>
		/// ANDROID PORT: upstream caches the decoded texture, pixels included, to reuse it at upload;
		/// on a phone that kept every texture decoded (1.25 GB for one 9-car train). Only the type is
		/// cached, as the Android port's static batcher and texture classifier expect.
		/// </remarks>
		internal static Dictionary<TextureOrigin, TextureTransparencyType> textureCache = new Dictionary<TextureOrigin, TextureTransparencyType>();

		/// <summary>Total time spent decoding texture files, in milliseconds.</summary>
		public static long TextureDecodeTime;

		/// <summary>Number of texture upload requests handled.</summary>
		public static long UploadCount;

		/// <summary>Time spent in texture upload requests, including cached-handle early exits (ms).</summary>
		public static long UploadMs;

		private static Dictionary<TextureOrigin, Texture> animatedTextures;
		// Reused buffer for paletted GIF to avoid per-frame new byte[] leak (Can get StackOverflow in glTexSubImage2D)
		private static byte[] _palettedExpandBuffer;
		private static readonly object _expandLock = new object();

		/// <summary>Holds the registered path-based textures, indexed by path.</summary>
		private static readonly Dictionary<string, List<Texture>> RegisteredTextureLookup = new Dictionary<string, List<Texture>>(StringComparer.OrdinalIgnoreCase);

		private static readonly object TextureLookupLock = new object();

		/// <summary>Striped locks serializing same-path texture registration.</summary>
		private static readonly object[] PathRegisterStripes = CreateStripes();

		private static object[] CreateStripes()
		{
			object[] stripes = new object[256];
			for (int i = 0; i < stripes.Length; i++)
			{
				stripes[i] = new object();
			}
			return stripes;
		}

		/// <summary>A texture's cached transparency type, if classified (ANDROID PORT: types only).</summary>
		internal static bool TryGetCachedType(TextureOrigin origin, out TextureTransparencyType type)
		{
			lock (TextureLookupLock)
			{
				return textureCache.TryGetValue(origin, out type);
			}
		}

		/// <summary>Caches a texture's transparency type (ANDROID PORT: types only).</summary>
		internal static void StoreCachedType(TextureOrigin origin, TextureTransparencyType type)
		{
			if (origin == null) return;
			lock (TextureLookupLock)
			{
				textureCache[origin] = type;
			}
		}

		/// <summary>The lock guarding registration (ANDROID PORT: the Android host registers path textures itself, under it).</summary>
		internal static object RegistrationLock => TextureLookupLock;

		/// <summary>The number of currently registered textures.</summary>
		public int RegisteredTexturesCount;

		internal TextureManager(HostInterface CurrentHost, BaseRenderer Renderer)
		{
			currentHost = CurrentHost;
			RegisteredTextures = new Texture[16];
			RegisteredTexturesCount = 0;
			renderer = Renderer;
			animatedTextures = new Dictionary<TextureOrigin, Texture>();
		}


		// --- register texture ---

		/// <summary>Registers a texture and returns a handle to the texture.</summary>
		/// <param name="path">The path to the file or directory that contains the texture.</param>
		/// <param name="handle">Receives a handle to the texture.</param>
		/// <returns>Whether registering the texture was successful.</returns>
		public bool RegisterTexture(string path, out Texture handle)
		{
			return RegisterTexture(path, null, out handle);
		}

		/// <summary>Registers a texture and returns a handle to the texture.</summary>
		/// <param name="path">The path to the texture.</param>
		/// <param name="parameters">The parameters that specify how to process the texture.</param>
		/// <param name="handle">Receives a handle to the texture.</param>
		/// <returns>Whether registering the texture was successful.</returns>
		public bool RegisterTexture(string path, TextureParameters parameters, out Texture handle)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				// shouldn't happen, but handle gracefully
				handle = null;
				return false;
			}

			// Serialize same-path registration: the decode below runs outside the
			// global lookup lock, so without this every worker sharing a texture
			// would decode it concurrently and discard all but one result.
			// Stripes keep unrelated paths decoding in parallel.
			object stripe = PathRegisterStripes[(uint)path.ToLowerInvariant().GetHashCode() % (uint)PathRegisterStripes.Length];
			lock (stripe)
			{
				if (TryFindRegisteredTexture(path, parameters, out handle))
				{
					return true;
				}

				// Decode outside the global lock (CPU-only, no GL). Same-path
				// workers wait on the stripe instead of decoding twice.
				Texture decoded = new Texture(path, parameters, currentHost);

				// Double-check: a case-variant path may have registered it on another stripe.
				if (TryFindRegisteredTexture(path, parameters, out handle))
				{
					return true;
				}

				if (RegisteredTexturesCount > RegisteredTextures.Length)
				{
					RegisteredTexturesCount = RegisteredTextures.Length;
				}

				int idx = GetNextFreeTexture();
				RegisteredTextures[idx] = decoded;
				RegisteredTexturesCount++;
				handle = RegisteredTextures[idx];

				// ANDROID PORT: the register-time decode is not kept (see textureCache).

				/*
				 * Maintain the registration lookup table.
				 * */
				if (!RegisteredTextureLookup.TryGetValue(path, out List<Texture> list))
				{
					list = new List<Texture>();
					RegisteredTextureLookup[path] = list;
				}
				list.Add(handle);
			}
			return true;
		}

		/// <summary>Finds an already-registered texture. Caller must hold the path stripe.</summary>
		private static bool TryFindRegisteredTexture(string path, TextureParameters parameters, out Texture handle)
		{
			lock (TextureLookupLock)
			{
				if (RegisteredTextureLookup.TryGetValue(path, out List<Texture> candidates))
				{
					for (int i = 0; i < candidates.Count; i++)
					{
						try
						{
							PathOrigin source = candidates[i].Origin as PathOrigin;

							if (source != null && source.Parameters == parameters)
							{
								handle = candidates[i];
								return true;
							}
						}
						catch
						{
							// ignored
						}
					}
				}
			}
			handle = null;
			return false;
		}

		/// <summary>Registers a texture and returns a handle to the texture.</summary>
		/// <param name="texture">The texture data.</param>
		/// <returns>The handle to the texture.</returns>
		public Texture RegisterTexture(Texture texture)
		{
			/*
			 * Register the texture and return the newly created handle.
			 * Locked: shares the index allocator with parallel path registration.
			 * */
			lock (TextureLookupLock)
			{
				int idx = GetNextFreeTexture();
				RegisteredTextures[idx] = new Texture(texture);
				RegisteredTexturesCount++;
				return RegisteredTextures[idx];
			}
		}

		/// <summary>Registers a texture and returns a handle to the texture.</summary>
		/// <param name="bitmap">The bitmap that contains the texture.</param>
		/// <param name="parameters">The parameters that specify how to process the texture.</param>
		/// <returns>The handle to the texture.</returns>
		/// <remarks>Be sure not to dispose of the bitmap after calling this function.</remarks>
		public Texture RegisterTexture(Bitmap bitmap, TextureParameters parameters)
		{
			/*
			 * Register the texture and return the newly created handle.
			 * Locked: shares the index allocator with parallel path registration.
			 * */
			lock (TextureLookupLock)
			{
				int idx = GetNextFreeTexture();
				RegisteredTextures[idx] = new Texture(bitmap, parameters);
				RegisteredTexturesCount++;
				return RegisteredTextures[idx];
			}
		}

		/// <summary>Registers a texture and returns a handle to the texture.</summary>
		/// <param name="bitmap">The bitmap that contains the texture.</param>
		/// <returns>The handle to the texture.</returns>
		/// <remarks>Be sure not to dispose of the bitmap after calling this function.</remarks>
		public Texture RegisterTexture(Bitmap bitmap)
		{
			/*
			 * Register the texture and return the newly created handle.
			 * Locked: shares the index allocator with parallel path registration.
			 * */
			lock (TextureLookupLock)
			{
				int idx = GetNextFreeTexture();
				RegisteredTextures[idx] = new Texture(bitmap);
				RegisteredTexturesCount++;
				return RegisteredTextures[idx];
			}
		}


		// --- load texture ---

		/// <summary>Loads the specified texture into OpenGL if not already loaded.</summary>
		/// <param name="handle">The handle to the registered texture.</param>
		/// <param name="wrap">The texture type indicating the clamp mode.</param>
		/// <param name="currentTicks">The current system clock-ticks</param>
		/// <param name="Interpolation">The interpolation mode to use when loading the texture</param>
		/// <param name="AnisotropicFilteringLevel">The anisotropic filtering level to use when loading the texture</param>
		/// <returns>Whether loading the texture was successful.</returns>
		public bool LoadTexture(ref Texture handle, OpenGlTextureWrapMode wrap, int currentTicks, InterpolationMode Interpolation, int AnisotropicFilteringLevel)
		{
			Stopwatch uploadTimer = Stopwatch.StartNew();
			bool result = LoadTextureInternal(ref handle, wrap, currentTicks, Interpolation, AnisotropicFilteringLevel);
			UploadCount++;
			UploadMs += uploadTimer.ElapsedMilliseconds;
			return result;
		}

		private bool LoadTextureInternal(ref Texture handle, OpenGlTextureWrapMode wrap, int currentTicks, InterpolationMode Interpolation, int AnisotropicFilteringLevel)
		{

			Texture texture = null;
			//Don't try to load a texture to a null handle, this is a seriously bad idea....
			if (handle == null || handle.OpenGlTextures == null)
			{
				return false;
			}
			
			if (handle.MultipleFrames)
			{
			bool animatedHit = false;
			if (handle.Origin != null)
			{
				lock (TextureLookupLock)
				{
					animatedHit = animatedTextures.TryGetValue(handle.Origin, out texture) && texture != null;
				}
			}
			if (!animatedHit)
			{
				texture = null;
				// ANDROID PORT: no cached or register-time decode to reuse (see textureCache).
				if (texture == null)
				{
					if (handle.Origin == null || !handle.Origin.GetTexture(out texture))
					{
						//Loading animated texture barfed
						return false;
					}
				}
				if (handle.Origin != null && texture != null)
				{
					lock (TextureLookupLock)
					{
						animatedTextures[handle.Origin] = texture;
					}
				}
			}
				
				double elapsedTime = CPreciseTimer.GetElapsedTime(handle.LastAccess, currentTicks);
				int elapsedFrames = (int)(elapsedTime / texture.FrameInterval);
				if (elapsedFrames > 0)
				{
					int oldFrame = texture.CurrentFrame;
					texture.CurrentFrame += elapsedFrames;
					texture.CurrentFrame %= texture.TotalFrames;
					handle.LastAccess = currentTicks;
					// If frame changed and GL texture already uploaded, update in-place via TexSubImage2D to avoid creating a lot of GL calls and per-frame alloc leak
					if (oldFrame != texture.CurrentFrame && texture.OpenGlTextures[(int)wrap].Valid && handle.OpenGlTextures[(int)wrap].Valid)
					{
						// Reuse same GL name across frames, update existing texture
						GL.BindTexture(TextureTarget.Texture2D, handle.OpenGlTextures[(int)wrap].Name);
						byte[] subBytes = texture.Bytes; // current frame's bytes (paletted or RGBA)
						// For paletted, expand via reused static buffer to avoid per-frame new byte[] GC pressure. (I think this how web browser do frame discarding)
						if (texture.PixelFormat == PixelFormat.Paletted)
						{
							bool opaque = texture.GetTransparencyType() == TextureTransparencyType.Opaque;
							int need = texture.Width * texture.Height * (opaque ? 3 : 4);
							// Hold the lock across fill + upload: the static buffer is shared, so releasing
							// the lock before TexSubImage2D would let a concurrent upload corrupt the data.
							lock (_expandLock)
							{
								if (_palettedExpandBuffer == null || _palettedExpandBuffer.Length < need) _palettedExpandBuffer = new byte[need];
								byte[] pooled = _palettedExpandBuffer;
								var pal = texture.Palette32;
								if (opaque)
								{
									for (int p = 0; p < texture.Width * texture.Height; p++)
									{
										int idx = subBytes[p] & 0xFF;
										var c = pal != null && idx < pal.Length ? pal[idx] : new OpenBveApi.Colors.Color32(0,0,0,255);
										pooled[p*3] = c.R; pooled[p*3+1] = c.G; pooled[p*3+2] = c.B;
									}
									GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
									GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, texture.Width, texture.Height, OpenTK.Graphics.OpenGL.PixelFormat.Rgb, PixelType.UnsignedByte, pooled);
								}
								else
								{
									for (int p = 0; p < texture.Width * texture.Height; p++)
									{
										int idx = subBytes[p] & 0xFF;
										var c = pal != null && idx < pal.Length ? pal[idx] : new OpenBveApi.Colors.Color32(0,0,0,255);
										pooled[p*4] = c.R; pooled[p*4+1] = c.G; pooled[p*4+2] = c.B; pooled[p*4+3] = c.A;
									}
									GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
									GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, texture.Width, texture.Height, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, pooled);
								}
							}
							GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
						}
						else
						{
							// RGB/RGBA direct – no expansion
							if (texture.PixelFormat == PixelFormat.RGBAlpha || texture.GetTransparencyType() != TextureTransparencyType.Opaque)
							{
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
								GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, texture.Width, texture.Height, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, subBytes);
							}
							else if (texture.PixelFormat == PixelFormat.RGB)
							{
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
								GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, texture.Width, texture.Height, OpenTK.Graphics.OpenGL.PixelFormat.Rgb, PixelType.UnsignedByte, subBytes);
							}
							else
							{
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
								GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, texture.Width, texture.Height, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, subBytes);
							}
							GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
						}
						// Keep the stable handle in sync without swapping identity: the handle keeps
						// its origin so the animated cache keeps hitting every tick (swapping to the
						// decoded texture gives it a fresh ByteArrayOrigin and the cache never hits again)
						handle.CurrentFrame = texture.CurrentFrame;
						return true;
					}
				}
			}
			else
			{
				handle.LastAccess = currentTicks;
			}
			//Set last access time

			// Only early-out when there is nothing new to upload: a freshly decoded texture
			// whose GL slot is not uploaded yet must fall through to the upload section,
			// even if the handle still carries a stale Valid flag from before an unload.
			if (handle.OpenGlTextures[(int)wrap].Valid && (texture == null || texture.OpenGlTextures[(int)wrap].Valid))
			{
				return true;
			}

			
			
			if (handle.Ignore)
			{
				return false;
			}

			if (texture == null)
			{
				// ANDROID PORT: no cached or register-time decode to reuse (see textureCache); the
				// handle's DecodedTexture may be the Android host's 1x1 format stand-in.
			if (texture == null && handle.Origin != null)
			{
				lock (TextureLookupLock)
				{
					if (animatedTextures.TryGetValue(handle.Origin, out Texture animatedTexture) && animatedTexture != null && animatedTexture.MultipleFrames)
					{
						texture = animatedTexture;
					}
				}
			}
			if (texture == null)
			{
				if (handle.Origin == null)
				{
					handle.Ignore = true;
					return false;
				}
				handle.Origin.GetTexture(out texture);
			}
			}
			if (texture != null)
			{
				if (texture.MultipleFrames)
				{
					handle.MultipleFrames = true;
				}
				//if (texture.BitsPerPixel == 32)
				{
					int[] names = new int[1];
					GL.GenTextures(1, names);
					GL.BindTexture(TextureTarget.Texture2D, names[0]);
					handle.OpenGlTextures[(int)wrap].Name = names[0];
					if (texture.MultipleFrames)
					{
						texture.OpenGlTextures[(int)wrap].Name = names[0];
					}

					handle.Size = texture.Size;
					handle.Transparency = texture.GetTransparencyType();
					// Fetch the pixel data once; the getter may lazily re-decode released instances, which must not happen per access
					byte[] textureBytes = texture.Bytes;
					if (texture.Width <= 0 || texture.Height <= 0 || textureBytes == null || textureBytes.Length == 0)
					{
						// Nothing valid to upload: release the half-created GL texture and get out
						// before GenerateMipmap below can raise GL_INVALID_OPERATION on it
						GL.BindTexture(TextureTarget.Texture2D, 0);
						GL.DeleteTexture(names[0]);
						handle.OpenGlTextures[(int)wrap].Name = 0;
						return false;
					}
					switch (Interpolation)
					{
						case InterpolationMode.NearestNeighbor:
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (float)TextureMinFilter.Nearest);
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (float)TextureMagFilter.Nearest);
							break;
						case InterpolationMode.Bilinear:
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (float)TextureMinFilter.Linear);
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (float)TextureMagFilter.Linear);
							break;
						case InterpolationMode.NearestNeighborMipmapped:
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (float)TextureMinFilter.NearestMipmapNearest);
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (float)TextureMagFilter.Nearest);
							break;
						case InterpolationMode.BilinearMipmapped:
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (float)TextureMinFilter.NearestMipmapLinear);
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (float)TextureMagFilter.Linear);
							break;
						case InterpolationMode.TrilinearMipmapped:
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (float)TextureMinFilter.LinearMipmapLinear);
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (float)TextureMagFilter.Linear);
							break;
						default:
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (float)TextureMinFilter.LinearMipmapLinear);
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (float)TextureMagFilter.Linear);
							break;
					}

					if ((wrap & OpenGlTextureWrapMode.RepeatClamp) != 0)
					{
						GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (float)TextureWrapMode.Repeat);
					}
					else
					{
						GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (float)TextureWrapMode.ClampToEdge);
					}

					if ((wrap & OpenGlTextureWrapMode.ClampRepeat) != 0)
					{
						GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (float)TextureWrapMode.Repeat);
					}
					else
					{
						GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (float)TextureWrapMode.ClampToEdge);
					}

					if (Interpolation == InterpolationMode.AnisotropicFiltering && AnisotropicFilteringLevel > 0)
					{
						GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)ExtTextureFilterAnisotropic.TextureMaxAnisotropyExt, AnisotropicFilteringLevel);
					}
					
					if (handle.Transparency == TextureTransparencyType.Opaque)
					{
						switch (texture.PixelFormat)
						{
							case PixelFormat.Paletted:
								{
									// Expand indexed to RGB (alpha discarded for opaque)
									byte[] expanded = new byte[texture.Width * texture.Height * 3];
									var pal = texture.Palette32;
									for (int p = 0; p < texture.Width * texture.Height; p++)
									{
										int idx = textureBytes[p] & 0xFF;
										var c = pal != null && idx < pal.Length ? pal[idx] : new OpenBveApi.Colors.Color32(0,0,0,255);
										expanded[p*3] = c.R; expanded[p*3+1] = c.G; expanded[p*3+2] = c.B;
									}
									GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
									GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, texture.Width, texture.Height, 0, OpenTK.Graphics.OpenGL.PixelFormat.Rgb, PixelType.UnsignedByte, expanded);
									break;
								}
						case PixelFormat.Grayscale:
							// LUMINANCE was removed from GL core profiles (3.1+), and our shaders are
							// #version 410 core, so always upload via the Red channel with a swizzle
							// n.b. Make sure to set the unpack alignment as otherwise we corrupt textures where stride > width
							GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
							GL.TexImage2D(TextureTarget.Texture2D, 0,
								PixelInternalFormat.R8,
								texture.Width, texture.Height, 0,
								OpenTK.Graphics.OpenGL.PixelFormat.Red,
								PixelType.UnsignedByte, textureBytes);

							// Replicate the single red channel into RGB and force alpha to 1 (opaque).
							// TEXTURE_SWIZZLE_* only accepts the symbolic values ZERO/ONE/RED/GREEN/BLUE/ALPHA
							// (see the glTexParameter reference); All.Red is 0x1903 (was hardcoded as 6403) and All.One is 1.
							GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleRgba, new[] { (int)All.Red, (int)All.Red, (int)All.Red, (int)All.One });
							break;
						case PixelFormat.GrayscaleAlpha:
							// Opaque use: alpha channel is discarded, upload the gray bytes via the Red channel
							{
								byte[] gray = new byte[texture.Width * texture.Height];
								for (int p = 0; p < gray.Length; p++)
								{
									gray[p] = textureBytes[2 * p];
								}
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
								GL.TexImage2D(TextureTarget.Texture2D, 0,
									PixelInternalFormat.R8,
									texture.Width, texture.Height, 0,
									OpenTK.Graphics.OpenGL.PixelFormat.Red,
									PixelType.UnsignedByte, gray);
								GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleRgba, new[] { (int)All.Red, (int)All.Red, (int)All.Red, (int)All.One });
								break;
							}
							case PixelFormat.RGB:
								// send as is
								// n.b. Make sure to set the unpack alignment as otherwise we corrupt textures where stride > width
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
								GL.TexImage2D(TextureTarget.Texture2D, 0,
									PixelInternalFormat.Rgb8,
									texture.Width, texture.Height, 0,
									OpenTK.Graphics.OpenGL.PixelFormat.Rgb,
									PixelType.UnsignedByte, textureBytes);
								break;
							case PixelFormat.RGBAlpha:
								/*
								 * Opaque texture, so the alpha channel is discarded by the RGB8 internal format.
								 * Upload the RGBA data directly rather than stripping it CPU-side.
								 */
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
								GL.TexImage2D(TextureTarget.Texture2D, 0,
									PixelInternalFormat.Rgb8,
									texture.Width, texture.Height, 0,
									OpenTK.Graphics.OpenGL.PixelFormat.Rgba,
									PixelType.UnsignedByte, textureBytes);
								break;
							default:
								// Unknown / invalid format: must not reach GenerateMipmap with no level-0 image
								GL.BindTexture(TextureTarget.Texture2D, 0);
								GL.DeleteTexture(names[0]);
								handle.OpenGlTextures[(int)wrap].Name = 0;
								return false;
						}
					}
					else
					{
						switch (texture.PixelFormat)
						{
						case PixelFormat.Paletted:
							{
								byte[] expanded = new byte[texture.Width * texture.Height * 4];
								var pal = texture.Palette32;
								for (int p = 0; p < texture.Width * texture.Height; p++)
								{
									int idx = textureBytes[p] & 0xFF;
									var c = pal != null && idx < pal.Length ? pal[idx] : new OpenBveApi.Colors.Color32(0,0,0,255);
									expanded[p*4] = c.R; expanded[p*4+1] = c.G; expanded[p*4+2] = c.B; expanded[p*4+3] = c.A;
								}
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
								GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, texture.Width, texture.Height, 0, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, expanded);
								break;
							}
					case PixelFormat.GrayscaleAlpha:
						// NOTE: LuminanceAlpha was removed from GL core profiles (3.1+), so always upconvert to RGBA
						{
							int stride = (4 * (texture.Width + 1) >> 2) << 2;
							byte[] newBytes = new byte[stride * texture.Height];
							int i = 0, j = 0;

							for (int y = 0; y < texture.Height; y++)
							{
								for (int x = 0; x < texture.Width; x++)
								{
									newBytes[j + 0] = textureBytes[i + 0];
									newBytes[j + 1] = textureBytes[i + 0];
									newBytes[j + 2] = textureBytes[i + 0];
									newBytes[j + 3] = textureBytes[i + 1];
									i += 2;
									j += 4;
								}

								j += stride - 4 * texture.Width;
							}
							GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
							GL.TexImage2D(TextureTarget.Texture2D, 0,
								PixelInternalFormat.Rgba8,
								texture.Width, texture.Height, 0,
								OpenTK.Graphics.OpenGL.PixelFormat.Rgba,
								PixelType.UnsignedByte, newBytes);
						}
						break;
						case PixelFormat.Grayscale:
							// Transparent use of a gray image: expand to RGBA, fully opaque
							{
								byte[] expanded = new byte[texture.Width * texture.Height * 4];
								for (int p = 0; p < texture.Width * texture.Height; p++)
								{
									expanded[p * 4] = textureBytes[p];
									expanded[p * 4 + 1] = textureBytes[p];
									expanded[p * 4 + 2] = textureBytes[p];
									expanded[p * 4 + 3] = 255;
								}
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
								GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, texture.Width, texture.Height, 0, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, expanded);
								break;
							}
						case PixelFormat.RGB:
							// Transparent use of an RGB image: expand to RGBA, fully opaque
							{
								byte[] expanded = new byte[texture.Width * texture.Height * 4];
								for (int p = 0; p < texture.Width * texture.Height; p++)
								{
									expanded[p * 4] = textureBytes[p * 3];
									expanded[p * 4 + 1] = textureBytes[p * 3 + 1];
									expanded[p * 4 + 2] = textureBytes[p * 3 + 2];
									expanded[p * 4 + 3] = 255;
								}
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
								GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, texture.Width, texture.Height, 0, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, expanded);
								break;
							}
							case PixelFormat.RGBAlpha:
								/*
								* The texture uses its alpha channel, so send the bitmap data
								* in 32-bits per channel as-is.
								* */
								// n.b. Must reset the unpack alignment in case of changes
								GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
								GL.TexImage2D(TextureTarget.Texture2D, 0,
									PixelInternalFormat.Rgba8,
									texture.Width, texture.Height, 0,
									OpenTK.Graphics.OpenGL.PixelFormat.Rgba,
									PixelType.UnsignedByte, textureBytes);
								break;
							default:
								// Unknown / invalid format: must not reach GenerateMipmap with no level-0 image
								GL.BindTexture(TextureTarget.Texture2D, 0);
								GL.DeleteTexture(names[0]);
								handle.OpenGlTextures[(int)wrap].Name = 0;
								return false;
						}
						
					}
					GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                    handle.OpenGlTextures[(int)wrap].Valid = true;
					if (texture.MultipleFrames)
					{
						texture.OpenGlTextures[(int)wrap].Valid = true;
						// Cache the decode under the stable handle origin (upsert: the animated
						// block above may already have added it) so later ticks hit the cache
						// instead of re-decoding after a reload cleared everything.
						if (handle.Origin != null)
						{
							lock (TextureLookupLock)
							{
								animatedTextures[handle.Origin] = texture;
							}
						}
					}
					else
					{
						/*
						 * The pixel data now lives in OpenGL, so release the retained CPU-side copies:
						 * the transient upload instance, the register-time decode held by the handle,
						 * and the entry in the texture cache.
						 * The data is lazily re-decoded from the origin if required again.
						 */
						texture.ReleaseBytes();
						// ANDROID PORT: the cache holds types only; record this one while the pixels are known.
						if (handle.Origin != null)
						{
							StoreCachedType(handle.Origin, handle.Transparency);
						}
						handle.DecodedTexture?.ReleaseBytes();
					}
					return true;
				}
			}

			handle.Ignore = true;
			return false;
		}

		/// <summary>Unloads the specified texture from OpenGL if loaded.</summary>
		/// <param name="handle">The handle to the registered texture.</param>
		/// <param name="preserveUnchangedCache">When true (route/object reload), decoded cache entries whose source file is unchanged are kept so the reloaded scene reuses them instead of decoding again.</param>
		/// <param name="releaseBytes">When true (route switch), handles are recreated hollow so decoded pixel bytes are also released; they re-decode from disk on demand.</param>
		public static void UnloadTexture(ref Texture handle, bool preserveUnchangedCache = false, bool releaseBytes = false)
		{
			//Null check the texture handle, as otherwise this can cause OpenGL to throw a fit
			if (handle == null)
			{
				return;
			}

		if (handle.MultipleFrames)
		{
			// Single GL name design: one pass per frame slot, at least one pass.
			// (TotalFrames is 0 on re-created handles, which previously skipped deletion
			// entirely and leaked the live GL names with stale Valid flags.)
			int passes = handle.TotalFrames > 0 ? handle.TotalFrames : 1;
			for (int i = 0; i < passes; i++)
			{
				handle.CurrentFrame = i;
				foreach (OpenGlTexture t in handle.OpenGlTextures)
				{
					if (t.Valid)
					{
						GL.DeleteTextures(1, new[] { t.Name });
						t.Valid = false;
					}
				}
			}
			/*
			 * Clone the ref for the search and then re-create the original in the texturemanager array
			 * This allows it to be re-loaded from disk
			 */
			var texture = handle;
			// Preserve the origin directly so the texture can be re-loaded from disk.
			// (Do not gate this on animatedTextures.ContainsKey: that cache may already have
			// been cleared by UnloadAllTextures, which previously produced hollow handles with
			// a null origin here and killed animated GIFs after a reload / filtering change.)
			handle = new Texture(texture.Origin);
			}
			else
			{
				foreach (OpenGlTexture t in handle.OpenGlTextures)
				{
					if (t.Valid)
					{
						GL.DeleteTextures(1, new[] { t.Name });
						t.Valid = false;
					}
				}
			}
			handle.Ignore = false;
			if (handle.Origin != null)
			{
				// On reload, keep cache entries whose source file is unchanged: the reloaded
				// scene reuses the decode instead of decoding the same file again.
				// (UnloadAllTextures prunes changed files separately below.)
				if (!preserveUnchangedCache || !TextureFileUnchanged(handle.Origin))
				{
					lock (TextureLookupLock)
					{
						textureCache.Remove(handle.Origin);
					}
				}
				if (releaseBytes)
				{
					// Drop decoded pixel bytes pinned by the handle itself
					// (DecodedTexture is readonly and survives cache clears).
					handle = new Texture(handle.Origin);
				}
			}
		}

		/// <summary>Loads all registered textures.</summary>
		public void LoadAllTextures()
		{
			lock (TextureLookupLock)
			{
				for (int i = 0; i < RegisteredTexturesCount && i < RegisteredTextures.Length; i++)
				{
					for (int j = 0; j < 4; j++)
					{
						if (RegisteredTextures[i] != null && RegisteredTextures[i].OpenGlTextures[j].Used)
						{
							LoadTexture(ref RegisteredTextures[i], (OpenGlTextureWrapMode)j, CPreciseTimer.GetClockTicks(), renderer.currentOptions.Interpolation, renderer.currentOptions.AnisotropicFilteringLevel);
						}

					}

				}
			}
		}

		/// <summary>Unloads all registered textures.</summary>
		/// <param name="currentlyReloading">When true, textures whose source file is unchanged are preserved.</param>
		/// <param name="releaseBytes">When true, decoded pixel bytes are also released (route switch).</param>
		public void UnloadAllTextures(bool currentlyReloading, bool releaseBytes = false)
		{
			// Always clear animated texture cache to prevent memory leak on reload:
			// animatedTextures retains decoded frame data for every GIF ever loaded,
			// doubling memory on each reload because old entries are never removed.
			lock (TextureLookupLock)
			{
				animatedTextures.Clear();
			}

			lock (TextureLookupLock)
			{
				for (int i = 0; i < RegisteredTexturesCount && i < RegisteredTextures.Length; i++)
				{
					/*
					 * On a route reload, preserve textures whose source file is unchanged,
					 * so that the first frame after the reload does not re-upload every texture.
					 */
				if (currentlyReloading && RegisteredTextures[i] != null && !RegisteredTextures[i].MultipleFrames && TextureFileUnchanged(RegisteredTextures[i].Origin))
				{
					continue;
				}
				UnloadTexture(ref RegisteredTextures[i], currentlyReloading, releaseBytes);
				}
			}
			if (currentlyReloading)
			{
				lock (TextureLookupLock)
				{
					foreach (TextureOrigin origin in textureCache.Keys.ToList())
					{
						if (origin is PathOrigin && !TextureFileUnchanged(origin))
						{
							textureCache.Remove(origin);
						}
					}
				}
			}
			else
			{
				lock (TextureLookupLock)
				{
					textureCache.Clear();
				}
			}

			/*
			 * Rebuild the registration lookup table from the surviving textures,
			 * so that it does not retain handles which have been unloaded.
			 * */
			lock (TextureLookupLock)
			{
				RegisteredTextureLookup.Clear();
				for (int i = 0; i < RegisteredTexturesCount; i++)
				{
					Texture texture = RegisteredTextures[i];
					if (texture != null && texture.Origin is PathOrigin pathOrigin)
					{
						if (!RegisteredTextureLookup.TryGetValue(pathOrigin.Path, out List<Texture> list))
						{
							list = new List<Texture>();
							RegisteredTextureLookup[pathOrigin.Path] = list;
						}
						list.Add(texture);
					}
				}
			}

			if (!currentlyReloading)
			{
				// Only force GC on full unload, not on route reload
				GC.Collect(0, GCCollectionMode.Optimized);
			}
			
		}

		/// <summary>Checks whether the on-disk source file of the given texture origin is unchanged.</summary>
		internal static bool TextureFileUnchanged(TextureOrigin origin)
		{
			if (!(origin is PathOrigin pathOrigin))
			{
				return false;
			}
			// Refresh() first, as FileSystemInfo caches size/last write time.
			try
			{
				FileInfo info = new FileInfo(pathOrigin.Path);
				info.Refresh();
				return info.Exists && pathOrigin.FileSize == info.Length && pathOrigin.LastModificationTime == info.LastWriteTime;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>Unloads any textures which have not been accessed</summary>
		/// <param name="TimeElapsed">The time elapsed since the last call to this function</param>
		public void UnloadUnusedTextures(double TimeElapsed)
		{
#if DEBUG
			//HACK: If when running in debug mode the frame time exceeds 1s, we can assume VS has hit a breakpoint
			//Don't unload textures in this case, as it just causes texture bugs
			if (TimeElapsed > 1000)
			{
				foreach (var Texture in RegisteredTextures)
				{
					if (Texture != null)
					{
						Texture.LastAccess = CPreciseTimer.GetClockTicks();
					}
				}
			}
#endif
			if (renderer.CurrentInterface == InterfaceType.Normal)
			{
				lock (TextureLookupLock)
				{
					for (int i = 0; i < RegisteredTextures.Length; i++)
					{
						if (RegisteredTextures[i] != null && RegisteredTextures[i].AvailableToUnload && (CPreciseTimer.GetClockTicks() - RegisteredTextures[i].LastAccess) > 20000)
						{
							UnloadTexture(ref RegisteredTextures[i]);
						}
					}
				}
			}
			else
			{
				//Don't unload textures if we are in a menu/ paused, as they may be required immediately after unpause
				lock (TextureLookupLock)
				{
					foreach (Texture Texture in RegisteredTextures)
					{
						//Texture can be null in certain cases....
						if (Texture != null)
						{
							Texture.LastAccess = CPreciseTimer.GetClockTicks();
						}
					}
				}
			}
		}


		// --- statistics ---

		/// <summary>Gets the number of registered textures.</summary>
		/// <returns>The number of registered textures.</returns>
		public int GetNumberOfRegisteredTextures()
		{
			lock (TextureLookupLock)
			{
				return RegisteredTexturesCount;
			}
		}

		/// <summary>Gets the number of loaded textures.</summary>
		/// <returns>The number of loaded textures.</returns>
		public int GetNumberOfLoadedTextures()
		{
			int count = 0;

			Texture[] snapshot = GetRegisteredSnapshot(out int registered);
			for (int i = 0; i < registered && i < snapshot.Length; i++)
			{
				if (snapshot[i] == null || snapshot[i].MultipleFrames)
				{
					continue;
				}

				if (snapshot[i].OpenGlTextures.Any(t => t.Valid))
				{
					count++;
				}
			}
			return count;
		}

		public int GetNumberOfLoadedAnimatedTextures()
		{
			int count = 0;
			Texture[] snapshot = GetRegisteredSnapshot(out int registered);
			for (int i = 0; i < registered && i < snapshot.Length; i++)
			{
				if (snapshot[i] == null || snapshot[i].MultipleFrames == false)
				{
					continue;
				}

				if (snapshot[i].OpenGlTextures.Any(t => t.Valid))
				{
					count++;
				}
			}
			return count;
		}


		/// <summary>Gets the next free texture, resizing the base textures array if appropriate</summary>
		/// <returns>The index of the next free texture</returns>
		internal int GetNextFreeTexture()
		{
			// Caller must hold TextureLookupLock.
			if (RegisteredTextures.Length == RegisteredTexturesCount)
			{
				Array.Resize(ref RegisteredTextures, RegisteredTextures.Length << 1);
			}
			else if (RegisteredTexturesCount > RegisteredTextures.Length)
			{
				RegisteredTexturesCount = RegisteredTextures.Length;
				Array.Resize(ref RegisteredTextures, RegisteredTextures.Length << 1);
			}

			return RegisteredTexturesCount;
		}

		private Texture[] GetRegisteredSnapshot(out int count)
		{
			lock (TextureLookupLock)
			{
				count = RegisteredTexturesCount;
				Texture[] copy = new Texture[RegisteredTextures.Length];
				Array.Copy(RegisteredTextures, copy, RegisteredTextures.Length);
				return copy;
			}
		}


		// --- functions ---

		/// <summary>Takes a positive value and rounds it up to the next highest power of two.</summary>
		/// <param name="value">The value.</param>
		/// <returns>The next highest power of two, or the original value if already a power of two.</returns>
		public int RoundUpToPowerOfTwo(int value)
		{
			if (value <= 0)
			{
				throw new ArgumentException("The specified value is not positive.");
			}

			value -= 1;

			for (int i = 1; i < sizeof(int) * 8; i <<= 1)
			{
				value |= value >> i;
			}

			return value + 1;
		}
	}
}
