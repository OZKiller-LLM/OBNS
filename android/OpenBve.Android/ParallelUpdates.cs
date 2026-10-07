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
using System.Runtime.CompilerServices;
using OpenBveApi.FunctionScripting;
using OpenBveApi.Objects;

namespace OpenBve.Android
{
	/*
	 * Animated objects updated on several cores each frame. Upstream updates every train's parts
	 * and every animated object of the route one after another; on the Tuen Ma Line, with 36
	 * scripted trains, that is ~8-10 ms of an A73's exterior frame, and far more on slow phones.
	 * Each train only moves its own cars' objects, and each animated object its own, so they can
	 * be updated side by side (AndroidTrainManager.UpdateTrainObjectsParallel,
	 * AndroidHost.UpdateAnimatedWorldObjects), with three things made safe for it:
	 *  - Function scripts: upstream's FunctionScript.Clone is a MemberwiseClone, so every copy of a
	 *    script (the same door in every car of every train) shares one evaluation stack. While a
	 *    parallel update runs, each script is evaluated on a stack of the thread's own, swapped in
	 *    for the call (AndroidHost.ExecuteFunctionScript: UseThreadStack / Restore below).
	 *  - The scripts' random numbers come from the host's Random, which is not thread-safe: the
	 *    host uses .NET's thread-safe Random.Shared instead.
	 *  - Showing and hiding objects is queued and applied on the render thread afterwards (End).
	 * Kept on the calling (GL) thread: the player's train (its cab's LED elements rebuild GPU
	 * buffers), and route objects that play sounds or follow the track.
	 * --ez parallel_updates false updates everything one after another, as upstream.
	 */
	internal static class ParallelUpdates
	{
		public static bool Enabled = true;

		/// <summary>Whether a parallel update is running (scripts then use per-thread stacks).</summary>
		public static volatile bool Active;

		[ThreadStatic] private static double[] threadStack;

		/// <summary>
		/// While a parallel update runs, gives the script a stack of this thread's own for one
		/// evaluation, and returns the one it had (pass it to <see cref="Restore"/>); otherwise null.
		/// </summary>
		public static double[] UseThreadStack(FunctionScript script)
		{
			if (!Active || script.Stack == null)
			{
				return null;
			}

			double[] shared = script.Stack;
			double[] own = threadStack;
			if (own == null || own.Length < shared.Length)
			{
				own = new double[Math.Max(16, shared.Length)];
				threadStack = own;
			}

			Unsafe.AsRef(in script.Stack) = own;
			return shared;
		}

		/*
		 * Showing and hiding objects during a parallel update: queued, and applied by the render
		 * thread when the update ends (Flush). Showing an object for the first time builds its GPU
		 * buffers, which off the render thread upstream hands to the render thread and waits up to
		 * 2 s for - while the render thread waits for the update: every object coming into view
		 * stalled the frame for 2 s and the game was reported as not responding. Each object is
		 * updated by one thread, so its own requests keep their order.
		 */
		private static readonly System.Collections.Concurrent.ConcurrentQueue<(ObjectState State, ObjectType Type, bool Show)> visibility =
			new System.Collections.Concurrent.ConcurrentQueue<(ObjectState, ObjectType, bool)>();

		/// <summary>Queues a show or hide made during a parallel update.</summary>
		public static void Defer(ObjectState state, ObjectType type, bool show)
		{
			if (!show)
			{
				pendingHides[state] = 0;
				System.Threading.Interlocked.Increment(ref pendingHideCount);
			}

			visibility.Enqueue((state, type, show));
		}

		/// <summary>Objects with a hide queued in this update: a show of one of them must not be skipped as already done.</summary>
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<ObjectState, byte> pendingHides =
			new System.Collections.Concurrent.ConcurrentDictionary<ObjectState, byte>();

		/*
		 * Counted separately: ConcurrentDictionary.IsEmpty (and Count) take every one of its locks,
		 * and this is asked for each of the ~4,300 shows a frame from every worker - the update ran
		 * several times slower than on one thread. Hides are a few a frame; usually none is pending.
		 */
		private static int pendingHideCount;

		/// <summary>Whether a hide of this object is queued in the current update.</summary>
		public static bool HidePending(ObjectState state) => System.Threading.Volatile.Read(ref pendingHideCount) != 0 && pendingHides.ContainsKey(state);

		/// <summary>Ends a parallel update: the queued shows and hides applied, on the calling (render) thread.</summary>
		public static void End(OpenBveApi.Hosts.HostInterface host)
		{
			Active = false;
			if (pendingHideCount != 0)
			{
				pendingHides.Clear();
				pendingHideCount = 0;
			}
			while (visibility.TryDequeue(out (ObjectState State, ObjectType Type, bool Show) item))
			{
				if (item.Show)
				{
					host.ShowObject(item.State, item.Type);
				}
				else
				{
					host.HideObject(item.State);
				}
			}
		}

		/// <summary>Gives the script back the stack <see cref="UseThreadStack"/> returned.</summary>
		public static void Restore(FunctionScript script, double[] shared)
		{
			if (shared != null)
			{
				Unsafe.AsRef(in script.Stack) = shared;
			}
		}
	}
}
