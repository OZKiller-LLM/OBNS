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

// ReSharper disable once CheckNamespace
namespace OpenTK.Audio.OpenAL
{
	/*
	 * The OpenTK 3 OpenAL enum surface, restricted to what OpenBVE uses.
	 * Values are the real AL token values and pass straight through to OpenAL Soft.
	 */

	/// <summary>Float source properties.</summary>
	public enum ALSourcef
	{
		Pitch = 0x1003,
		Gain = 0x100A,
		MinGain = 0x100D,
		MaxGain = 0x100E,
		ReferenceDistance = 0x1020,
		RolloffFactor = 0x1021,
		ConeOuterGain = 0x1022,
		MaxDistance = 0x1023,
		SecOffset = 0x1024
	}

	/// <summary>Three-component float source properties.</summary>
	public enum ALSource3f
	{
		Position = 0x1004,
		Velocity = 0x1006,
		Direction = 0x1005
	}

	/// <summary>Boolean source properties.</summary>
	public enum ALSourceb
	{
		SourceRelative = 0x0202,
		Looping = 0x1007
	}

	/// <summary>Integer source properties.</summary>
	public enum ALSourcei
	{
		SourceRelative = 0x0202,
		ConeInnerAngle = 0x1001,
		ConeOuterAngle = 0x1002,
		Looping = 0x1007,
		Buffer = 0x1009,
		SourceState = 0x1010,
		SourceType = 0x1027
	}

	/// <summary>Queryable integer source properties.</summary>
	public enum ALGetSourcei
	{
		SourceRelative = 0x0202,
		Buffer = 0x1009,
		SourceState = 0x1010,
		BuffersQueued = 0x1015,
		BuffersProcessed = 0x1016,
		SourceType = 0x1027
	}

	/// <summary>Playback states of a source.</summary>
	public enum ALSourceState
	{
		Initial = 0x1011,
		Playing = 0x1012,
		Paused = 0x1013,
		Stopped = 0x1014
	}

	/// <summary>Three-component float listener properties.</summary>
	public enum ALListener3f
	{
		Position = 0x1004,
		Velocity = 0x1006
	}

	/// <summary>Float listener properties.</summary>
	public enum ALListenerf
	{
		Gain = 0x100A
	}

	/// <summary>Float vector listener properties.</summary>
	public enum ALListenerfv
	{
		Orientation = 0x100F
	}

	/// <summary>Buffer sample formats.</summary>
	public enum ALFormat
	{
		Mono8 = 0x1100,
		Mono16 = 0x1101,
		Stereo8 = 0x1102,
		Stereo16 = 0x1103
	}

	/// <summary>Distance attenuation models.</summary>
	public enum ALDistanceModel
	{
		None = 0,
		InverseDistance = 0xD001,
		InverseDistanceClamped = 0xD002,
		LinearDistance = 0xD003,
		LinearDistanceClamped = 0xD004,
		ExponentDistance = 0xD005,
		ExponentDistanceClamped = 0xD006
	}

	/// <summary>Error codes.</summary>
	public enum ALError
	{
		NoError = 0,
		InvalidName = 0xA001,
		InvalidEnum = 0xA002,
		InvalidValue = 0xA003,
		InvalidOperation = 0xA004,
		OutOfMemory = 0xA005
	}

	/// <summary>Strings queryable from a device.</summary>
	public enum AlcGetString
	{
		DefaultDeviceSpecifier = 0x1004,
		DeviceSpecifier = 0x1005,
		Extensions = 0x1006,
		CaptureDeviceSpecifier = 0x0310,
		CaptureDefaultDeviceSpecifier = 0x0311,
		DefaultAllDevicesSpecifier = 0x1012,
		AllDevicesSpecifier = 0x1013
	}

	/// <summary>Integer values queryable from a device.</summary>
	public enum AlcGetInteger
	{
		MajorVersion = 0x1000,
		MinorVersion = 0x1001,
		AttributesSize = 0x1002,
		AllAttributes = 0x1003,
		CaptureSamples = 0x0312
	}
}
