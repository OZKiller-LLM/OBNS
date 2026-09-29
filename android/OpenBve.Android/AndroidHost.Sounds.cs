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
using OpenBveApi;
using OpenBveApi.Hosts;
using OpenBveApi.Interface;
using OpenBveApi.Math;
using OpenBveApi.Sounds;
using SoundManager;

namespace OpenBve.Android
{
	/// <summary>Sound: decoding through the sound plugins, playback through the sound manager.</summary>
	/// <remarks>A port of upstream's Host sound methods, with Program.Sounds replaced by <see cref="Sounds"/>.</remarks>
	public partial class AndroidHost
	{
		/// <summary>The sound manager, or null before it is set up (sounds are then silently dropped).</summary>
		public AndroidSounds Sounds { get; set; }

		private readonly Dictionary<string, SoundHandle> registeredSounds = new Dictionary<string, SoundHandle>();

		/// <inheritdoc />
		public override bool LoadSound(string path, out Sound sound)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("sound");
			if (File.Exists(path) || Directory.Exists(path))
			{
				foreach (ContentLoadingPlugin plugin in Plugins ?? new ContentLoadingPlugin[0])
				{
					if (plugin.Sound == null)
					{
						continue;
					}

					try
					{
						if (plugin.Sound.CanLoadSound(path))
						{
							try
							{
								if (plugin.Sound.LoadSound(path, out sound))
								{
									return true;
								}

								AddMessage(MessageType.Error, false, "Plugin " + plugin.Title + " returned unsuccessfully at LoadSound for file " + path);
							}
							catch (Exception ex)
							{
								AddMessage(MessageType.Error, false, "Plugin " + plugin.Title + " raised the following exception at LoadSound:" + ex.Message);
							}
						}
					}
					catch (Exception ex)
					{
						AddMessage(MessageType.Error, false, "Plugin " + plugin.Title + " raised the following exception at CanLoadSound:" + ex.Message);
					}
				}

				AddMessage(MessageType.Error, false, "No plugin found that is capable of loading sound " + path);
			}
			else
			{
				ReportProblem(ProblemType.PathNotFound, path);
			}

			sound = null;
			return false;
		}

		/// <inheritdoc />
		public override bool RegisterSound(string path, out SoundHandle handle)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("sound");
			return RegisterSound(path, 0.0, out handle);
		}

		/// <inheritdoc />
		public override bool RegisterSound(string path, double radius, out SoundHandle handle)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("sound");
			if (PathExists(path))
			{
				/*
				 * Routes register the same few sounds over and over (173,000 calls on the MTR route,
				 * 12 s of its load), and each call would check the file exists and scan every buffer.
				 * The sound manager already returns the first buffer registered for a path whatever
				 * the radius, so caching by path returns exactly what it would.
				 */
				lock (registeredSounds)
				{
					if (!registeredSounds.TryGetValue(path, out handle))
					{
						handle = Sounds?.RegisterBuffer(path, radius);
						if (handle != null)
						{
							registeredSounds[path] = handle;
						}
					}
				}

				return handle != null;
			}

			ReportProblem(ProblemType.PathNotFound, path);
			handle = null;
			return false;
		}

		/// <inheritdoc />
		public override bool RegisterSound(Sound sound, out SoundHandle handle)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("sound");
			handle = Sounds?.RegisterBuffer(sound, 0.0);
			return handle != null;
		}

		/// <inheritdoc />
		public override bool SoundIsPlaying(object soundSource)
		{
			return Sounds != null && Sounds.IsPlaying(soundSource);
		}

		/// <inheritdoc />
		public override object PlaySound(SoundHandle buffer, double pitch, double volume, Vector3 position, object parent, bool looped)
		{
			return Sounds?.PlaySound(buffer, pitch, volume, position, parent, looped);
		}

		/// <inheritdoc />
		public override void StopSound(object soundSource)
		{
			Sounds?.StopSound(soundSource as SoundSource);
		}

		/// <inheritdoc />
		public override void StopAllSounds(object parent)
		{
			Sounds?.StopAllSounds(parent);
		}
	}
}
