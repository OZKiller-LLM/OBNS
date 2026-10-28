// OBNS: not an upstream file. Where a frame's time goes on the GPU and where the main thread waits, logged with the
// frame statistics when OBNS_GPUSTATS=1 (and OBNS_FRAMESTATS=1): for finding what the Vulkan world layer
// (OBNS/VULKAN.md) costs OpenGL's work. Off, each call is a flag test.

using System;
using System.Diagnostics;
using System.Globalization;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics
{
	internal static class GpuStats
	{
		internal static readonly bool Enabled = Environment.GetEnvironmentVariable("OBNS_GPUSTATS") == "1";

		/// <summary>OpenGL's timestamps in a frame.</summary>
		internal enum Point { FrameStart = 0, BackgroundDone = 1, WorldDone = 2, FrameEnd = 3 }

		private const int Points = 4, Frames = 4;
		private static int[] queries;
		private static readonly bool[] issued = new bool[Frames];
		private static int ring;

		// Totals since the statistics were last taken: OpenGL's GPU time between the points, Vulkan's frame on the GPU,
		// and the main thread's time in calls that may wait for the GPU or the Vulkan thread.
		private static double glBackground, glWorld, glAfter, glFrame, vulkanFrame;
		private static long glFrames, vulkanFrames;
		private static long clearTicks, backgroundTicks, joinTicks, cpuFrames;

		/// <summary>An OpenGL timestamp at a point in this frame (the GPU's time when it gets there).</summary>
		internal static void Mark(Point point)
		{
			if (!Enabled)
			{
				return;
			}

			if (queries == null)
			{
				queries = new int[Points * Frames];
				GL.GenQueries(queries.Length, queries);
			}

			if (point == Point.FrameStart)
			{
				// This ring slot's last frame (Frames ago) is read, if the GPU has got that far, before it is used again.
				ring = (ring + 1) % Frames;
				if (issued[ring])
				{
					Read(ring);
				}

				issued[ring] = true;
			}

			GL.QueryCounter(queries[ring * Points + (int)point], QueryCounterTarget.Timestamp);
		}

		private static void Read(int slot)
		{
			long[] times = new long[Points];
			for (int i = 0; i < Points; i++)
			{
				GL.GetQueryObject(queries[slot * Points + i], GetQueryObjectParam.QueryResultAvailable, out int available);
				if (available == 0)
				{
					return;
				}

				GL.GetQueryObject(queries[slot * Points + i], GetQueryObjectParam.QueryResult, out times[i]);
			}

			glBackground += (times[1] - times[0]) / 1e6;
			glWorld += (times[2] - times[1]) / 1e6;
			glAfter += (times[3] - times[2]) / 1e6;
			glFrame += (times[3] - times[0]) / 1e6;
			glFrames++;
		}

		/// <summary>The Vulkan world layer's frame on the GPU (from its timestamps), in milliseconds.</summary>
		internal static void VulkanFrame(double milliseconds)
		{
			vulkanFrame += milliseconds;
			vulkanFrames++;
		}

		/// <summary>Main thread time in glClear (where a frame waits for the GPU), drawing the background, and waiting for the Vulkan thread.</summary>
		internal static void Cpu(long clear, long background, long join)
		{
			if (!Enabled)
			{
				return;
			}

			clearTicks += clear;
			backgroundTicks += background;
			joinTicks += join;
			cpuFrames++;
		}

		/// <summary>The averages since the last call, for the frame statistics.</summary>
		internal static string Take()
		{
			if (!Enabled)
			{
				return "off";
			}

			double gl = Math.Max(1, glFrames), vk = Math.Max(1, vulkanFrames), cpu = Math.Max(1, cpuFrames), ms = 1000.0 / Stopwatch.Frequency;
			string text = string.Format(CultureInfo.InvariantCulture,
				"GPU: OpenGL background {0:0.00}, world {1:0.00}, after {2:0.00}, frame {3:0.00} ms; Vulkan frame {4:0.00} ms; main thread in glClear {5:0.00}, background {6:0.00}, waiting for Vulkan {7:0.00} ms",
				glBackground / gl, glWorld / gl, glAfter / gl, glFrame / gl, vulkanFrames == 0 ? 0.0 : vulkanFrame / vk, clearTicks * ms / cpu, backgroundTicks * ms / cpu, joinTicks * ms / cpu);
			glBackground = glWorld = glAfter = glFrame = vulkanFrame = 0;
			glFrames = vulkanFrames = cpuFrames = 0;
			clearTicks = backgroundTicks = joinTicks = 0;
			return text;
		}
	}
}
