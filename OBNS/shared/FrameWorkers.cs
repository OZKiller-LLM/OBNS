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
using System.Threading;

// OBNS: shared by the Android port (OpenBve.Android) and the desktop build (compiled into TrainManager).

namespace OpenBve
{
	/*
	 * Worker threads for work inside a frame (ParallelUpdates). The .NET thread pool suits work
	 * measured in tens of milliseconds; a frame's share is a few milliseconds in small pieces, and on
	 * Android the pool kept few threads ready and took longer to hand work out than the work took:
	 * a Galaxy A73's train objects ran 10 ms in Parallel.For for ~6 ms of work, on 3-4 threads. These
	 * threads live for the game, take chunks of the work from a shared counter (the calling thread
	 * too), and between frames spin briefly before sleeping, so a frame wakes them in microseconds.
	 */
	public sealed class FrameWorkers
	{
		public static readonly FrameWorkers Shared = new FrameWorkers(Math.Max(0, Environment.ProcessorCount - 1));

		/// <summary>One Run's work: its own counters, so a worker late from the previous job only finds it finished.</summary>
		private sealed class Job
		{
			public Action<int, int> Body;
			public int Count, Chunk, Next, Pending;
			public Exception Failure;

			public void RunChunks()
			{
				int start;
				while ((start = Interlocked.Add(ref Next, Chunk) - Chunk) < Count)
				{
					try
					{
						Body(start, Math.Min(Count, start + Chunk));
					}
					catch (Exception ex)
					{
						Interlocked.CompareExchange(ref Failure, ex, null);
					}
					finally
					{
						Interlocked.Decrement(ref Pending);
					}
				}
			}
		}

		private readonly Thread[] threads;
		private readonly object gate = new object();
		private volatile Job current;
		private volatile int generation;

		private FrameWorkers(int workers)
		{
			threads = new Thread[workers];
			for (int i = 0; i < workers; i++)
			{
				threads[i] = new Thread(Work) { IsBackground = true, Name = "OpenBVE frame worker " + (i + 1) };
				threads[i].Start();
			}
		}

		/// <summary>Number of threads that work on a frame's job, the caller included.</summary>
		public int Threads => threads.Length + 1;

		/// <summary>
		/// Runs work(start, end) over [0, total) in chunks on every worker and the calling thread,
		/// and returns when all of it is done (rethrowing the first exception, if any). One job at a
		/// time, from one thread (the render thread).
		/// </summary>
		public void Run(int total, Action<int, int> work, int minimumChunk = 1)
		{
			if (total <= 0)
			{
				return;
			}

			if (threads.Length == 0 || total <= minimumChunk)
			{
				work(0, total);
				return;
			}

			int chunk = Math.Max(minimumChunk, total / (Threads * 4));
			Job job = new Job { Body = work, Count = total, Chunk = chunk, Pending = (total + chunk - 1) / chunk };
			current = job;
			lock (gate)
			{
				generation++;
				Monitor.PulseAll(gate);
			}

			job.RunChunks();
			Spinner spin = new Spinner();
			while (Volatile.Read(ref job.Pending) > 0)
			{
				spin.SpinOnce();
			}

			current = null;
			if (job.Failure != null)
			{
				throw new AggregateException(job.Failure);
			}
		}

		private void Work()
		{
			int seen = 0;
			while (true)
			{
				// Spin a little after a job (the next frame's may come soon), then sleep.
				Spinner spin = new Spinner();
				while (generation == seen && !spin.NextSpinWillYield)
				{
					spin.SpinOnce();
				}

				lock (gate)
				{
					while (generation == seen)
					{
						Monitor.Wait(gate);
					}

					seen = generation;
				}

				current?.RunChunks();
			}
		}

		/// <summary>
		/// A spin wait that never sleeps: busy-waits a little longer each time, then yields the processor. .NET's
		/// SpinWait.SpinOnce(-1) does this, but only from .NET Core 3.0; .NET Framework's SpinOnce() sleeps a whole
		/// millisecond every twentieth time, too long inside a frame.
		/// </summary>
		private struct Spinner
		{
			private int count;

			/// <summary>Whether the next SpinOnce yields the processor rather than busy-waiting.</summary>
			public bool NextSpinWillYield => count >= 10;

			public void SpinOnce()
			{
				if (count < 10)
				{
					Thread.SpinWait(4 << count);
				}
				else
				{
					Thread.Yield();
				}

				count++;
			}
		}
	}
}
