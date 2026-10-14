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
using OpenTK;
using OpenTK.Audio.OpenAL;

namespace OpenBve.SmokeTest
{
	/// <summary>
	/// Opens OpenAL Soft on the device and plays a short tone through the shim, the same way
	/// SoundManager does. Proves the bundled libopenal.so loads and that the P/Invoke
	/// signatures line up, which is the part most likely to be silently wrong.
	/// </summary>
	public static class AudioTest
	{
		/// <summary>Runs the audio check.</summary>
		public static string Run()
		{
			IntPtr device = Alc.OpenDevice(null);
			if (device == IntPtr.Zero)
			{
				throw new Exception("alcOpenDevice returned null: no audio device");
			}

			ContextHandle context = ContextHandle.Zero;
			int buffer = 0;
			int source = 0;

			try
			{
				context = Alc.CreateContext(device, (int[])null);
				if (context == ContextHandle.Zero)
				{
					throw new Exception("alcCreateContext failed");
				}

				if (!Alc.MakeContextCurrent(context))
				{
					throw new Exception("alcMakeContextCurrent failed");
				}

				string deviceName = Alc.GetString(device, AlcGetString.DefaultDeviceSpecifier);

				// The same global setup SoundManager performs.
				AL.DistanceModel(ALDistanceModel.None);
				AL.SpeedOfSound(343.0f);

				// A quarter second of 440 Hz, 16 bit mono.
				const int sampleRate = 44100;
				const int samples = sampleRate / 4;
				short[] tone = new short[samples];
				for (int i = 0; i < samples; i++)
				{
					tone[i] = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate) * 8000.0);
				}

				buffer = AL.GenBuffer();
				AL.BufferData(buffer, ALFormat.Mono16, tone, tone.Length * sizeof(short), sampleRate);
				CheckError("after buffer upload");

				AL.GenSources(1, out source);
				AL.Source(source, ALSourcei.Buffer, buffer);
				AL.Source(source, ALSourceb.SourceRelative, true);
				AL.Source(source, ALSourcef.Gain, 0.2f);
				AL.Source(source, ALSource3f.Position, 0.0f, 0.0f, 0.0f);
				AL.Listener(ALListener3f.Position, 0.0f, 0.0f, 0.0f);
				CheckError("after source setup");

				AL.SourcePlay(source);
				CheckError("after play");

				// Give the mixer a moment, then confirm the source really entered the playing state.
				global::System.Threading.Thread.Sleep(50);
				AL.GetSource(source, ALGetSourcei.SourceState, out int state);
				if (state != (int)ALSourceState.Playing && state != (int)ALSourceState.Stopped)
				{
					throw new Exception("source did not start: state is 0x" + state.ToString("x"));
				}

				AL.SourceStop(source);
				return "played a 440 Hz tone through OpenAL Soft on \"" + deviceName + "\"";
			}
			finally
			{
				if (source != 0)
				{
					AL.DeleteSources(1, ref source);
				}

				if (buffer != 0)
				{
					AL.DeleteBuffers(1, ref buffer);
				}

				Alc.MakeContextCurrent(ContextHandle.Zero);
				if (context != ContextHandle.Zero)
				{
					Alc.DestroyContext(context);
				}

				Alc.CloseDevice(device);
			}
		}

		private static void CheckError(string stage)
		{
			ALError error = AL.GetError();
			if (error != ALError.NoError)
			{
				throw new Exception("OpenAL error " + error + " " + stage);
			}
		}
	}
}
