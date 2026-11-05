// OBNS 1.31N: the first C# on the Switch, compiled ahead of time by Mono for libnx.
// Copyright (c) The OpenBVE Android port contributors.
//
// Measures what the plan needs: C#'s speed against the probe's C loop (0.392 s at 1,020 MHz, 0.224 s at 1,785), the
// garbage collector's pauses under a game-like allocation rate, and work spread over the three cores.

using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

static class Program
{
    [DllImport("__Internal")]
    static extern int obns_set_boost(int on);

    static volatile int sinkInt;
    static volatile float sinkFloat;

    // The probe's loop, line for line: 40 million rounds of an FNV-style hash and a multiply-add.
    static double CpuLoop()
    {
        var sw = Stopwatch.StartNew();
        double a = 1.0;
        ulong h = 1469598103934665603UL;
        for (uint i = 0; i < 40000000; i++)
        {
            h = (h ^ i) * 1099511628211UL;
            a = a * 1.0000001 + (h & 0xFF) * 1e-9;
        }
        sinkFloat = (float)(a + h);
        return sw.Elapsed.TotalSeconds;
    }

    // Game-like work: vectors and matrices over arrays of structs, as the renderer and train simulation do.
    struct Vector3 { public float X, Y, Z; }
    static double VectorLoop()
    {
        var points = new Vector3[100000];
        for (int i = 0; i < points.Length; i++) points[i] = new Vector3 { X = i, Y = i * 0.5f, Z = -i };
        float m00 = 0.8f, m01 = 0.6f, m10 = -0.6f, m11 = 0.8f;
        var sw = Stopwatch.StartNew();
        for (int pass = 0; pass < 100; pass++)
            for (int i = 0; i < points.Length; i++)
            {
                ref Vector3 p = ref points[i];
                float x = p.X * m00 + p.Z * m01, z = p.X * m10 + p.Z * m11;
                p.X = x; p.Z = z; p.Y += 0.001f;
            }
        sinkFloat = points[1234].X;
        return sw.Elapsed.TotalSeconds;
    }

    // About 4 MB a second of short-lived objects (strings, lists, small arrays) for 6 s, at 60 "frames" a second,
    // noting the longest frame and the collections.
    static void GcTest()
    {
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var keep = new List<byte[]>();
        for (int i = 0; i < 400; i++) keep.Add(new byte[256 * 1024]); // 100 MB held, as a route's data would be
        double worst = 0, total = 0;
        var frame = new Stopwatch();
        for (int f = 0; f < 360; f++)
        {
            frame.Restart();
            for (int j = 0; j < 400; j++)
            {
                var list = new List<int>(32);
                for (int k = 0; k < 32; k++) list.Add(k * j);
                var s = string.Concat("obj", j.ToString(), "_", f.ToString());
                sinkInt = list[7] + s.Length + new byte[64].Length;
            }
            double ms = frame.Elapsed.TotalMilliseconds;
            total += ms;
            if (ms > worst) worst = ms;
        }
        Console.WriteLine($"GC test: 360 frames of allocation, {total / 360:F2} ms a frame, worst {worst:F2} ms; " +
            $"collections gen0 {GC.CollectionCount(0) - gen0}, gen1 {GC.CollectionCount(1) - gen1}, gen2 {GC.CollectionCount(2) - gen2}; " +
            $"heap {GC.GetTotalMemory(false) >> 20} MiB");
        GC.KeepAlive(keep);
    }

    static double ParallelLoop(int threads)
    {
        var sw = Stopwatch.StartNew();
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                double a = 1.0;
                ulong h = 1469598103934665603UL;
                for (uint i = 0; i < 40000000 / threads; i++)
                {
                    h = (h ^ i) * 1099511628211UL;
                    a = a * 1.0000001 + (h & 0xFF) * 1e-9;
                }
                sinkFloat = (float)(a + h);
            });
            workers[t].Start();
        }
        foreach (var w in workers) w.Join();
        return sw.Elapsed.TotalSeconds;
    }

    static int Main()
    {
        Console.WriteLine("OBNS hello (1.31N): C# compiled ahead of time by Mono for libnx");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, " +
            $"{RuntimeInformation.ProcessArchitecture}; {Environment.ProcessorCount} processors; " +
            $"server GC {GCSettings.IsServerGC}, latency {GCSettings.LatencyMode}");
        Console.WriteLine($"Culture {System.Globalization.CultureInfo.CurrentCulture.Name}; 東涌 {"東涌".Length} chars; " +
            $"{(1234567.891).ToString("N2")}");

        Console.WriteLine($"CPU loop at stock: {CpuLoop():F3} s (C: 0.392 s)");
        Console.WriteLine($"Vector loop at stock: {VectorLoop():F3} s");
        int mhz = obns_set_boost(1);
        Console.WriteLine($"Clock set to {mhz} MHz");
        Console.WriteLine($"CPU loop at {mhz}: {CpuLoop():F3} s (C: 0.224 s)");
        Console.WriteLine($"Vector loop at {mhz}: {VectorLoop():F3} s");
        GcTest();
        // Threads last, each result printed before the next thread starts: a thread ending crashed the runtime before
        // the libnx TLS fix (NX/patches), and a crash must not take the other results with it.
        Console.Out.Flush();
        for (int threads = 1; threads <= 3; threads++)
        {
            Console.WriteLine($"Same loop split over {threads} thread(s): {ParallelLoop(threads):F3} s");
            Console.Out.Flush();
        }
        Console.WriteLine($"Clock back to {obns_set_boost(0)} MHz");
        Console.WriteLine("Done.");
        return 0;
    }
}
