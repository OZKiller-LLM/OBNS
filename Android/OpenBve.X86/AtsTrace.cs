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

namespace OpenBve.X86
{
	/// <summary>
	/// A recording of everything a plugin was asked and answered, and its replay.
	/// </summary>
	/// <remarks>
	/// The plugin's behaviour depends only on its inputs, so a recording made on the phone, replayed
	/// against the same DLL on a desktop, must give the same outputs frame for frame. Any difference
	/// is a fault in the emulator on one of the two machines; if there is none, whatever the train is
	/// doing is what the plugin does with those inputs, and the replay is the place to work out why.
	/// Elapse records the panel and sound arrays only where they differ from the previous frame.
	/// </remarks>
	public static class AtsTrace
	{
		public const byte OpLoad = 1, OpSetVehicleSpec = 2, OpInitialize = 3, OpSetPower = 4, OpSetBrake = 5, OpSetReverser = 6,
			OpKeyDown = 7, OpKeyUp = 8, OpHornBlow = 9, OpDoorOpen = 10, OpDoorClose = 11, OpSetSignal = 12, OpSetBeaconData = 13, OpElapse = 14;

		private const int Magic = 0x41545331;
		private const int ArraySize = 256;

		/// <summary>Writes a trace. Not thread-safe: the plugin is only ever called from one thread.</summary>
		public sealed class Writer : IDisposable
		{
			private readonly BinaryWriter writer;
			private readonly int[] lastPanel = new int[ArraySize], lastSound = new int[ArraySize];
			private readonly int[] inPanel = new int[ArraySize], inSound = new int[ArraySize];

			public Writer(Stream stream, int ansiCodePage)
			{
				writer = new BinaryWriter(stream);
				writer.Write(Magic);
				writer.Write(ansiCodePage);
			}

			internal void Op(byte op, params int[] args)
			{
				writer.Write(op);
				foreach (int a in args)
				{
					writer.Write(a);
				}
			}

			internal void ElapseInput(AtsVehicleState state, int[] panel, int[] sound)
			{
				writer.Write(OpElapse);
				writer.Write(state.Location);
				writer.Write(state.Speed);
				writer.Write(state.Time);
				writer.Write(state.BcPressure);
				writer.Write(state.MrPressure);
				writer.Write(state.ErPressure);
				writer.Write(state.BpPressure);
				writer.Write(state.SapPressure);
				writer.Write(state.Current);
				WriteDiff(panel, lastPanel);
				WriteDiff(sound, lastSound);
				Array.Copy(panel, inPanel, ArraySize);
				Array.Copy(sound, inSound, ArraySize);
			}

			internal void ElapseOutput(AtsHandles handles, int[] panel, int[] sound)
			{
				writer.Write(handles.Brake);
				writer.Write(handles.Power);
				writer.Write(handles.Reverser);
				writer.Write(handles.ConstantSpeed);
				// Outputs are recorded relative to this frame's inputs.
				Array.Copy(inPanel, lastPanel, ArraySize);
				Array.Copy(inSound, lastSound, ArraySize);
				WriteDiff(panel, lastPanel);
				WriteDiff(sound, lastSound);
			}

			private void WriteDiff(int[] values, int[] last)
			{
				int count = 0;
				for (int i = 0; i < ArraySize; i++)
				{
					if (values[i] != last[i]) count++;
				}

				writer.Write((short)count);
				for (int i = 0; i < ArraySize; i++)
				{
					if (values[i] != last[i])
					{
						writer.Write((byte)i);
						writer.Write(values[i]);
						last[i] = values[i];
					}
				}
			}

			public void Flush() => writer.Flush();

			public void Dispose() => writer.Dispose();
		}

		/// <summary>Reads a trace's header, returning the ANSI code page it was recorded with.</summary>
		public static int ReadCodePage(Stream stream)
		{
			BinaryReader reader = new BinaryReader(stream);
			if (reader.ReadInt32() != Magic) throw new InvalidDataException("not an ATS trace");
			return reader.ReadInt32();
		}

		/// <summary>
		/// Replays a trace (positioned after its header) into a freshly opened plugin, comparing
		/// every output with the recording, and reports each frame through <paramref name="frame"/>.
		/// </summary>
		/// <returns>The number of frames whose outputs differed from the recording.</returns>
		public static int Replay(AtsPlugin plugin, Stream stream, Action<string> report, Action<int, AtsVehicleState, AtsHandles, int[], string> frame = null, int maxFrames = int.MaxValue)
		{
			BinaryReader reader = new BinaryReader(stream);
			int[] panel = new int[ArraySize], sound = new int[ArraySize];
			int[] expectedPanel = new int[ArraySize], expectedSound = new int[ArraySize];
			int frames = 0, mismatches = 0;
			string events = string.Empty;
			try
			{
				while (stream.Position < stream.Length && frames < maxFrames)
				{
					byte op = reader.ReadByte();
					switch (op)
					{
						case OpLoad: plugin.Load(); break;
						case OpSetVehicleSpec: plugin.SetVehicleSpec(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()); break;
						case OpInitialize: { int v = reader.ReadInt32(); plugin.Initialize(v); events += " Initialize" + v; break; }
						case OpSetPower: { int v = reader.ReadInt32(); plugin.SetPower(v); events += " P" + v; break; }
						case OpSetBrake: { int v = reader.ReadInt32(); plugin.SetBrake(v); events += " B" + v; break; }
						case OpSetReverser: { int v = reader.ReadInt32(); plugin.SetReverser(v); events += " R" + v; break; }
						case OpKeyDown: { int v = reader.ReadInt32(); plugin.KeyDown(v); events += " KeyDown" + v; break; }
						case OpKeyUp: { int v = reader.ReadInt32(); plugin.KeyUp(v); events += " KeyUp" + v; break; }
						case OpHornBlow: { int v = reader.ReadInt32(); plugin.HornBlow(v); events += " Horn" + v; break; }
						case OpDoorOpen: plugin.DoorOpen(); events += " DoorOpen"; break;
						case OpDoorClose: plugin.DoorClose(); events += " DoorClose"; break;
						case OpSetSignal: { int v = reader.ReadInt32(); plugin.SetSignal(v); events += " Signal" + v; break; }
						case OpSetBeaconData:
						{
							int type = reader.ReadInt32(), signal = reader.ReadInt32();
							float distance = BitConverter.Int32BitsToSingle(reader.ReadInt32());
							int optional = reader.ReadInt32();
							plugin.SetBeaconData(type, signal, distance, optional);
							events += " Beacon(" + type + "," + signal + "," + distance.ToString("0") + "," + optional + ")";
							break;
						}
						case OpElapse:
						{
							AtsVehicleState state = new AtsVehicleState
							{
								Location = reader.ReadDouble(), Speed = reader.ReadSingle(), Time = reader.ReadInt32(),
								BcPressure = reader.ReadSingle(), MrPressure = reader.ReadSingle(), ErPressure = reader.ReadSingle(),
								BpPressure = reader.ReadSingle(), SapPressure = reader.ReadSingle(), Current = reader.ReadSingle()
							};
							ReadDiff(reader, panel);
							ReadDiff(reader, sound);
							AtsHandles expected = new AtsHandles { Brake = reader.ReadInt32(), Power = reader.ReadInt32(), Reverser = reader.ReadInt32(), ConstantSpeed = reader.ReadInt32() };
							Array.Copy(panel, expectedPanel, ArraySize);
							Array.Copy(sound, expectedSound, ArraySize);
							ReadDiff(reader, expectedPanel);
							ReadDiff(reader, expectedSound);
							AtsHandles actual = plugin.Elapse(state, panel, sound);
							bool same = actual.Brake == expected.Brake && actual.Power == expected.Power && actual.Reverser == expected.Reverser && actual.ConstantSpeed == expected.ConstantSpeed;
							List<string> differences = new List<string>();
							for (int i = 0; i < ArraySize; i++)
							{
								if (panel[i] != expectedPanel[i]) differences.Add("panel[" + i + "] " + panel[i] + " vs " + expectedPanel[i]);
								if (sound[i] != expectedSound[i]) differences.Add("sound[" + i + "] " + sound[i] + " vs " + expectedSound[i]);
							}

							if (!same || differences.Count != 0)
							{
								mismatches++;
								if (mismatches <= 10)
								{
									report("frame " + frames + ": B" + actual.Brake + " P" + actual.Power + " vs recorded B" + expected.Brake + " P" + expected.Power + "; " + string.Join(", ", differences));
								}
							}

							// Carry on from the recording, so that one difference does not cascade.
							Array.Copy(expectedPanel, panel, ArraySize);
							Array.Copy(expectedSound, sound, ArraySize);
							frame?.Invoke(frames, state, expected, panel, events);
							events = string.Empty;
							frames++;
							break;
						}
						default:
							report("unknown op " + op + " at byte " + stream.Position + "; stopping");
							return mismatches;
					}
				}
			}
			catch (EndOfStreamException)
			{
				// A recording cut short when the game ended: replay what there is.
			}

			report(frames + " frames replayed, " + mismatches + " with outputs different from the recording");
			return mismatches;
		}

		private static void ReadDiff(BinaryReader reader, int[] values)
		{
			int count = reader.ReadInt16();
			for (int i = 0; i < count; i++)
			{
				int index = reader.ReadByte();
				values[index] = reader.ReadInt32();
			}
		}
	}
}
