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
using System.Runtime.InteropServices;

// ReSharper disable once CheckNamespace
namespace OpenTK.Audio.OpenAL
{
	/// <summary>
	/// The subset of OpenTK 3's AL class OpenBVE uses, bound to OpenAL Soft.
	/// </summary>
	/// <remarks>
	/// Android has no system OpenAL; libopenal.so is bundled into the APK. See PORTING.md.
	/// </remarks>
	public static class AL
	{
		internal const string Library = "openal";

		// --- native entry points ---

		[DllImport(Library, EntryPoint = "alGenSources")] private static extern void alGenSources(int n, int[] sources);
		[DllImport(Library, EntryPoint = "alDeleteSources")] private static extern void alDeleteSources(int n, int[] sources);
		[DllImport(Library, EntryPoint = "alGenBuffers")] private static extern void alGenBuffers(int n, int[] buffers);
		[DllImport(Library, EntryPoint = "alDeleteBuffers")] private static extern void alDeleteBuffers(int n, int[] buffers);
		[DllImport(Library, EntryPoint = "alBufferData")] private static extern void alBufferData(int buffer, int format, IntPtr data, int size, int frequency);
		[DllImport(Library, EntryPoint = "alSourcePlay")] private static extern void alSourcePlay(int source);
		[DllImport(Library, EntryPoint = "alSourcePause")] private static extern void alSourcePause(int source);
		[DllImport(Library, EntryPoint = "alSourceStop")] private static extern void alSourceStop(int source);
		[DllImport(Library, EntryPoint = "alSourceRewind")] private static extern void alSourceRewind(int source);
		[DllImport(Library, EntryPoint = "alSourceQueueBuffers")] private static extern void alSourceQueueBuffers(int source, int n, int[] buffers);
		[DllImport(Library, EntryPoint = "alSourceUnqueueBuffers")] private static extern void alSourceUnqueueBuffers(int source, int n, int[] buffers);
		[DllImport(Library, EntryPoint = "alSourcef")] private static extern void alSourcef(int source, int param, float value);
		[DllImport(Library, EntryPoint = "alSource3f")] private static extern void alSource3f(int source, int param, float v1, float v2, float v3);
		[DllImport(Library, EntryPoint = "alSourcei")] private static extern void alSourcei(int source, int param, int value);
		[DllImport(Library, EntryPoint = "alGetSourcei")] private static extern void alGetSourcei(int source, int param, out int value);
		[DllImport(Library, EntryPoint = "alGetSourcef")] private static extern void alGetSourcef(int source, int param, out float value);
		[DllImport(Library, EntryPoint = "alListener3f")] private static extern void alListener3f(int param, float v1, float v2, float v3);
		[DllImport(Library, EntryPoint = "alListenerf")] private static extern void alListenerf(int param, float value);
		[DllImport(Library, EntryPoint = "alListenerfv")] private static extern void alListenerfv(int param, float[] values);
		[DllImport(Library, EntryPoint = "alDistanceModel")] private static extern void alDistanceModel(int model);
		[DllImport(Library, EntryPoint = "alSpeedOfSound")] private static extern void alSpeedOfSound(float value);
		[DllImport(Library, EntryPoint = "alDopplerFactor")] private static extern void alDopplerFactor(float value);
		[DllImport(Library, EntryPoint = "alGetError")] private static extern int alGetError();
		[DllImport(Library, EntryPoint = "alIsSource")] private static extern bool alIsSource(int source);
		[DllImport(Library, EntryPoint = "alIsBuffer")] private static extern bool alIsBuffer(int buffer);

		// --- sources ---

		/// <summary>Generates a source.</summary>
		public static int GenSource()
		{
			int[] sources = new int[1];
			alGenSources(1, sources);
			return sources[0];
		}

		/// <summary>Generates sources.</summary>
		public static void GenSources(int n, out int sources)
		{
			int[] names = new int[n < 1 ? 1 : n];
			alGenSources(n, names);
			sources = names[0];
		}

		/// <summary>Generates sources.</summary>
		public static void GenSources(int n, int[] sources)
		{
			alGenSources(n, sources);
		}

		/// <summary>Deletes sources.</summary>
		public static void DeleteSources(int n, ref int sources)
		{
			alDeleteSources(n, new[] { sources });
		}

		/// <summary>Deletes sources.</summary>
		public static void DeleteSources(int n, int[] sources)
		{
			alDeleteSources(n, sources);
		}

		/// <summary>Deletes sources.</summary>
		public static void DeleteSources(int[] sources)
		{
			alDeleteSources(sources.Length, sources);
		}

		/// <summary>Deletes a source.</summary>
		public static void DeleteSource(int source)
		{
			alDeleteSources(1, new[] { source });
		}

		/// <summary>Whether the name is a source.</summary>
		public static bool IsSource(int source)
		{
			return alIsSource(source);
		}

		/// <summary>Sets a float source property.</summary>
		public static void Source(int source, ALSourcef param, float value)
		{
			alSourcef(source, (int)param, value);
		}

		/// <summary>Sets a three-component source property.</summary>
		public static void Source(int source, ALSource3f param, float v1, float v2, float v3)
		{
			alSource3f(source, (int)param, v1, v2, v3);
		}

		/// <summary>Sets a boolean source property.</summary>
		public static void Source(int source, ALSourceb param, bool value)
		{
			alSourcei(source, (int)param, value ? 1 : 0);
		}

		/// <summary>Sets an integer source property.</summary>
		public static void Source(int source, ALSourcei param, int value)
		{
			alSourcei(source, (int)param, value);
		}

		/// <summary>Reads an integer source property.</summary>
		public static void GetSource(int source, ALGetSourcei param, out int value)
		{
			alGetSourcei(source, (int)param, out value);
		}

		/// <summary>Reads a float source property.</summary>
		public static void GetSource(int source, ALSourcef param, out float value)
		{
			alGetSourcef(source, (int)param, out value);
		}

		/// <summary>Starts or resumes playback of a source.</summary>
		public static void SourcePlay(int source)
		{
			alSourcePlay(source);
		}

		/// <summary>Pauses a source.</summary>
		public static void SourcePause(int source)
		{
			alSourcePause(source);
		}

		/// <summary>Stops a source.</summary>
		public static void SourceStop(int source)
		{
			alSourceStop(source);
		}

		/// <summary>Rewinds a source.</summary>
		public static void SourceRewind(int source)
		{
			alSourceRewind(source);
		}

		/// <summary>Queues a buffer on a source.</summary>
		public static void SourceQueueBuffer(int source, int buffer)
		{
			alSourceQueueBuffers(source, 1, new[] { buffer });
		}

		/// <summary>Queues buffers on a source.</summary>
		public static void SourceQueueBuffers(int source, int n, int[] buffers)
		{
			alSourceQueueBuffers(source, n, buffers);
		}

		/// <summary>Removes processed buffers from a source's queue.</summary>
		public static int[] SourceUnqueueBuffers(int source, int n)
		{
			if (n <= 0)
			{
				return new int[0];
			}

			int[] buffers = new int[n];
			alSourceUnqueueBuffers(source, n, buffers);
			return buffers;
		}

		/// <summary>Removes processed buffers from a source's queue.</summary>
		public static void SourceUnqueueBuffers(int source, int n, int[] buffers)
		{
			alSourceUnqueueBuffers(source, n, buffers);
		}

		// --- buffers ---

		/// <summary>Generates a buffer.</summary>
		public static int GenBuffer()
		{
			int[] buffers = new int[1];
			alGenBuffers(1, buffers);
			return buffers[0];
		}

		/// <summary>Generates buffers.</summary>
		public static void GenBuffers(int n, out int buffers)
		{
			int[] names = new int[n < 1 ? 1 : n];
			alGenBuffers(n, names);
			buffers = names[0];
		}

		/// <summary>Generates buffers.</summary>
		public static void GenBuffers(int n, int[] buffers)
		{
			alGenBuffers(n, buffers);
		}

		/// <summary>Deletes buffers.</summary>
		public static void DeleteBuffers(int n, ref int buffers)
		{
			alDeleteBuffers(n, new[] { buffers });
		}

		/// <summary>Deletes buffers.</summary>
		public static void DeleteBuffers(int n, int[] buffers)
		{
			alDeleteBuffers(n, buffers);
		}

		/// <summary>Deletes buffers.</summary>
		public static void DeleteBuffers(int[] buffers)
		{
			alDeleteBuffers(buffers.Length, buffers);
		}

		/// <summary>Deletes a buffer.</summary>
		public static void DeleteBuffer(int buffer)
		{
			alDeleteBuffers(1, new[] { buffer });
		}

		/// <summary>Whether the name is a buffer.</summary>
		public static bool IsBuffer(int buffer)
		{
			return alIsBuffer(buffer);
		}

		/// <summary>Fills a buffer with sample data.</summary>
		public static void BufferData<T>(int buffer, ALFormat format, T[] data, int size, int frequency) where T : struct
		{
			GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
			try
			{
				alBufferData(buffer, (int)format, handle.AddrOfPinnedObject(), size, frequency);
			}
			finally
			{
				handle.Free();
			}
		}

		/// <summary>Fills a buffer with sample data.</summary>
		public static void BufferData(int buffer, ALFormat format, IntPtr data, int size, int frequency)
		{
			alBufferData(buffer, (int)format, data, size, frequency);
		}

		// --- listener and global state ---

		/// <summary>Sets a three-component listener property.</summary>
		public static void Listener(ALListener3f param, float v1, float v2, float v3)
		{
			alListener3f((int)param, v1, v2, v3);
		}

		/// <summary>Sets a float listener property.</summary>
		public static void Listener(ALListenerf param, float value)
		{
			alListenerf((int)param, value);
		}

		/// <summary>Sets a float vector listener property.</summary>
		public static void Listener(ALListenerfv param, ref float[] values)
		{
			alListenerfv((int)param, values);
		}

		/// <summary>Sets a float vector listener property.</summary>
		public static void Listener(ALListenerfv param, float[] values)
		{
			alListenerfv((int)param, values);
		}

		/// <summary>Selects the distance attenuation model.</summary>
		public static void DistanceModel(ALDistanceModel model)
		{
			alDistanceModel((int)model);
		}

		/// <summary>Sets the speed of sound, used for the doppler effect.</summary>
		public static void SpeedOfSound(float value)
		{
			alSpeedOfSound(value);
		}

		/// <summary>Sets the doppler factor.</summary>
		public static void DopplerFactor(float value)
		{
			alDopplerFactor(value);
		}

		/// <summary>Returns and clears the most recent error.</summary>
		public static ALError GetError()
		{
			return (ALError)alGetError();
		}
	}

	/// <summary>The subset of OpenTK 3's Alc class OpenBVE uses.</summary>
	public static class Alc
	{
		[DllImport(AL.Library, EntryPoint = "alcOpenDevice", CharSet = CharSet.Ansi)] private static extern IntPtr alcOpenDevice(string deviceName);
		[DllImport(AL.Library, EntryPoint = "alcCloseDevice")] private static extern bool alcCloseDevice(IntPtr device);
		[DllImport(AL.Library, EntryPoint = "alcCreateContext")] private static extern IntPtr alcCreateContext(IntPtr device, int[] attributes);
		[DllImport(AL.Library, EntryPoint = "alcMakeContextCurrent")] private static extern bool alcMakeContextCurrent(IntPtr context);
		[DllImport(AL.Library, EntryPoint = "alcDestroyContext")] private static extern void alcDestroyContext(IntPtr context);
		[DllImport(AL.Library, EntryPoint = "alcGetString")] private static extern IntPtr alcGetString(IntPtr device, int param);
		[DllImport(AL.Library, EntryPoint = "alcGetError")] private static extern int alcGetError(IntPtr device);

		/// <summary>Opens a playback device. Pass a null reference for the default device.</summary>
		public static IntPtr OpenDevice(string deviceName)
		{
			return alcOpenDevice(deviceName);
		}

		/// <summary>Closes a playback device.</summary>
		public static bool CloseDevice(IntPtr device)
		{
			return alcCloseDevice(device);
		}

		/// <summary>Creates a context on a device.</summary>
		public static ContextHandle CreateContext(IntPtr device, int[] attributes)
		{
			return new ContextHandle(alcCreateContext(device, attributes));
		}

		/// <summary>Makes a context current on the calling thread.</summary>
		public static bool MakeContextCurrent(ContextHandle context)
		{
			return alcMakeContextCurrent(context.Handle);
		}

		/// <summary>Destroys a context.</summary>
		public static void DestroyContext(ContextHandle context)
		{
			alcDestroyContext(context.Handle);
		}

		/// <summary>Reads a string property of a device.</summary>
		public static string GetString(IntPtr device, AlcGetString param)
		{
			IntPtr pointer = alcGetString(device, (int)param);
			return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(pointer);
		}

		/// <summary>Returns and clears the most recent device error.</summary>
		public static int GetError(IntPtr device)
		{
			return alcGetError(device);
		}
	}
}
