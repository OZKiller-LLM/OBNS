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
	/// Microphone capture.
	/// </summary>
	/// <remarks>
	/// Not implemented on Android. Capture would need the RECORD_AUDIO permission, and OpenBVE
	/// uses it only for the optional microphone-driven horn. Upstream constructs this inside a
	/// try/catch and carries on without a microphone, so throwing here disables the feature
	/// cleanly rather than requiring a permission the user has no reason to grant.
	/// </remarks>
	public class AudioCapture : IDisposable
	{
		/// <summary>The name of the default capture device.</summary>
		public static string DefaultDevice => string.Empty;

		/// <summary>Creates a capture device.</summary>
		public AudioCapture(string deviceName, int frequency, ALFormat format, int bufferSize)
		{
			throw new NotSupportedException("Microphone capture is not available in the Android port.");
		}

		/// <summary>The sample format being captured.</summary>
		public ALFormat SampleFormat => ALFormat.Mono16;

		/// <summary>The sample rate being captured.</summary>
		public int SampleFrequency => 44100;

		/// <summary>The number of samples ready to be read.</summary>
		public int AvailableSamples => 0;

		/// <summary>Whether capture is running.</summary>
		public bool IsRunning => false;

		/// <summary>Starts capturing.</summary>
		public void Start()
		{
		}

		/// <summary>Stops capturing.</summary>
		public void Stop()
		{
		}

		/// <summary>Reads captured samples.</summary>
		public void ReadSamples(Array buffer, int sampleCount)
		{
		}

		/// <inheritdoc />
		public void Dispose()
		{
		}
	}
}
