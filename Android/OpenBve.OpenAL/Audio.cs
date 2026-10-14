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
using OpenTK.Audio.OpenAL;

// ReSharper disable once CheckNamespace
namespace OpenTK
{
	/// <summary>An opaque handle to an audio or graphics context.</summary>
	public struct ContextHandle : IEquatable<ContextHandle>
	{
		/// <summary>The null handle.</summary>
		public static readonly ContextHandle Zero = new ContextHandle(IntPtr.Zero);

		/// <summary>The underlying pointer.</summary>
		public IntPtr Handle { get; }

		/// <summary>Creates a handle.</summary>
		public ContextHandle(IntPtr handle)
		{
			Handle = handle;
		}

		/// <inheritdoc />
		public bool Equals(ContextHandle other)
		{
			return Handle == other.Handle;
		}

		/// <inheritdoc />
		public override bool Equals(object obj)
		{
			return obj is ContextHandle other && Equals(other);
		}

		/// <inheritdoc />
		public override int GetHashCode()
		{
			return Handle.GetHashCode();
		}

		/// <summary>Compares two handles.</summary>
		public static bool operator ==(ContextHandle left, ContextHandle right)
		{
			return left.Equals(right);
		}

		/// <summary>Compares two handles.</summary>
		public static bool operator !=(ContextHandle left, ContextHandle right)
		{
			return !left.Equals(right);
		}

		/// <summary>Converts a handle to its pointer.</summary>
		public static implicit operator IntPtr(ContextHandle handle)
		{
			return handle.Handle;
		}
	}
}

// ReSharper disable once CheckNamespace
namespace OpenTK.Audio
{
	/// <summary>
	/// Microphone capture, through OpenAL Soft's capture device (its OpenSL ES backend records from
	/// the phone's microphone): upstream's route microphone sounds (Track.MicSound), played back at
	/// their place on the line while the player has PLAY_MIC_SOUNDS on.
	/// </summary>
	/// <remarks>
	/// Upstream makes this when the sound system starts and starts it when microphone sounds are
	/// switched on. Here the device is opened only then, and only once the app holds the
	/// RECORD_AUDIO permission (<see cref="Permitted"/>, set by the app when the player grants it),
	/// so nothing records - and nothing asks for the permission - unless the player turns it on.
	/// Until it is open the microphone reports nothing to read.
	/// </remarks>
	public class AudioCapture : IDisposable
	{
		private const int ALC_CAPTURE_SAMPLES = 0x312;

		[DllImport(AL.Library, EntryPoint = "alcCaptureOpenDevice", CharSet = CharSet.Ansi)] private static extern IntPtr alcCaptureOpenDevice(string deviceName, uint frequency, int format, int bufferSize);
		[DllImport(AL.Library, EntryPoint = "alcCaptureCloseDevice")] private static extern bool alcCaptureCloseDevice(IntPtr device);
		[DllImport(AL.Library, EntryPoint = "alcCaptureStart")] private static extern void alcCaptureStart(IntPtr device);
		[DllImport(AL.Library, EntryPoint = "alcCaptureStop")] private static extern void alcCaptureStop(IntPtr device);
		[DllImport(AL.Library, EntryPoint = "alcCaptureSamples")] private static extern void alcCaptureSamples(IntPtr device, IntPtr buffer, int samples);
		[DllImport(AL.Library, EntryPoint = "alcGetIntegerv")] private static extern void alcGetIntegerv(IntPtr device, int param, int size, out int value);

		/// <summary>Whether the app holds the RECORD_AUDIO permission: until then the device is not opened.</summary>
		public static volatile bool Permitted;

		/// <summary>Called when microphone sounds are switched on without the permission, so the app can ask for it.</summary>
		public static event Action PermissionNeeded;

		private readonly string deviceName;
		private readonly int bufferSize;
		private IntPtr device;
		private bool running;

		/// <summary>The name of the default capture device.</summary>
		public static string DefaultDevice => null;

		/// <summary>Creates a capture device; it opens when first started.</summary>
		public AudioCapture(string deviceName, int frequency, ALFormat format, int bufferSize)
		{
			this.deviceName = string.IsNullOrEmpty(deviceName) ? null : deviceName;
			SampleFrequency = frequency;
			SampleFormat = format;
			this.bufferSize = bufferSize;
		}

		/// <summary>The sample format being captured.</summary>
		public ALFormat SampleFormat { get; }

		/// <summary>The sample rate being captured.</summary>
		public int SampleFrequency { get; }

		/// <summary>The number of samples ready to be read.</summary>
		public int AvailableSamples
		{
			get
			{
				if (!running)
				{
					return 0;
				}

				alcGetIntegerv(device, ALC_CAPTURE_SAMPLES, 1, out int samples);
				return samples;
			}
		}

		/// <summary>Whether capture is running.</summary>
		public bool IsRunning => running;

		/// <summary>Starts capturing: opens the microphone if the app may (otherwise asks for the permission).</summary>
		public void Start()
		{
			if (running)
			{
				return;
			}

			if (device == IntPtr.Zero)
			{
				if (!Permitted)
				{
					PermissionNeeded?.Invoke();
					return;
				}

				device = alcCaptureOpenDevice(deviceName, (uint)SampleFrequency, (int)SampleFormat, bufferSize);
				if (device == IntPtr.Zero)
				{
					return;
				}
			}

			alcCaptureStart(device);
			running = true;
		}

		/// <summary>Stops capturing and lets the microphone go.</summary>
		public void Stop()
		{
			if (device == IntPtr.Zero)
			{
				return;
			}

			if (running)
			{
				alcCaptureStop(device);
				running = false;
			}

			// Closed, not just stopped: the microphone indicator goes off while microphone sounds are off.
			alcCaptureCloseDevice(device);
			device = IntPtr.Zero;
		}

		/// <summary>Reads captured samples into an array (bytes for 16-bit mono: two a sample).</summary>
		public void ReadSamples(Array buffer, int sampleCount)
		{
			if (!running || buffer == null || sampleCount <= 0)
			{
				return;
			}

			int sampleBytes = SampleFormat == ALFormat.Mono8 ? 1 : SampleFormat == ALFormat.Stereo16 ? 4 : 2;
			int byteLength = Buffer.ByteLength(buffer);
			sampleCount = Math.Min(sampleCount, byteLength / sampleBytes);
			GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
			try
			{
				alcCaptureSamples(device, pin.AddrOfPinnedObject(), sampleCount);
			}
			finally
			{
				pin.Free();
			}
		}

		/// <inheritdoc />
		public void Dispose()
		{
			Stop();
		}
	}
}
