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
namespace Android.Util
{
	/// <summary>
	/// Stands in for Android's log in the Android files compiled for the Switch as they are (Compat's message boxes, the game's host):
	/// on the Switch the lines go to the console, which nxlink sends back to the PC, and to the runner's log file.
	/// </summary>
	public static class Log
	{
		/// <summary>The lines written, for the runner's log file as well (set by the Switch entry).</summary>
		public static Action<string> Sink;

		/*
		 * Written on a thread of their own (2026-11-08): the console is nxlink's socket over Wi-Fi when sent by nxlink, and
		 * UserData's log files are flushed line by line to the SD card - either can block for seconds, and the game's thread
		 * waited: frames of 3.2 s in the drive benchmark, at its 5-second reports. The game's thread only queues the line.
		 */
		private static readonly System.Collections.Concurrent.BlockingCollection<string> Pending = new System.Collections.Concurrent.BlockingCollection<string>();
		private static readonly System.Threading.Thread Writer = StartWriter();
		private static volatile int written, queued;

		private static System.Threading.Thread StartWriter()
		{
			System.Threading.Thread thread = new System.Threading.Thread(() =>
			{
				foreach (string line in Pending.GetConsumingEnumerable())
				{
					try
					{
						System.Console.WriteLine(line);
						Sink?.Invoke(line);
					}
					catch (Exception)
					{
						// A log line lost rather than the writer.
					}

					written++;
				}
			})
			{
				IsBackground = true,
				Name = "log writer"
			};
			thread.Start();
			return thread;
		}

		/// <summary>Waits until the lines queued so far are written, at most this long (before a deliberate crash, at the end).</summary>
		public static void Flush(int milliseconds)
		{
			int target = queued;
			System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
			while (written < target && waited.ElapsedMilliseconds < milliseconds)
			{
				System.Threading.Thread.Sleep(5);
			}
		}

		private static int Write(string level, string tag, string message)
		{
			string line = level.Length == 0 ? tag + ": " + message : tag + " (" + level + "): " + message;
			System.Threading.Interlocked.Increment(ref queued);
			Pending.Add(line);
			return 0;
		}

		/// <summary>A verbose line.</summary>
		public static int Verbose(string tag, string message) => Write("verbose", tag, message);

		/// <summary>A debug line.</summary>
		public static int Debug(string tag, string message) => Write("debug", tag, message);

		/// <summary>An information line.</summary>
		public static int Info(string tag, string message) => Write(string.Empty, tag, message);

		/// <summary>A warning.</summary>
		public static int Warn(string tag, string message) => Write("warning", tag, message);

		/// <summary>An error.</summary>
		public static int Error(string tag, string message) => Write("error", tag, message);

		/// <summary>An error with its exception.</summary>
		public static int Error(string tag, string message, Exception exception) => Write("error", tag, message + ": " + exception);
	}
}
