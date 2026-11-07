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
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using OpenBveApi.Colors;
using OpenBveApi.Hosts;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Textures;

namespace OpenBve.Android
{
	/*
	 * Parsed objects kept on the storage, for the next load of the same route (OBNS/SWITCH.md, steps 2 and 4). On the Switch
	 * the object parsers were ~590 s of the Tuen Ma Line's 680 s load: 141 ms a file to parse, 6 ms to read. A static object
	 * is stored as the host has it after the plugin and OptimizeObject - vertices, faces, materials, its textures by path and
	 * parameters (registered again on the way back, so the host hands out the same handles) - and read back instead of
	 * parsed when the file, its size and date, the text encoding, the optimisation settings and the parsers (their
	 * assemblies' identities) are the same.
	 *
	 * One pack and one index per route: records are appended to the pack as objects are parsed (from the loading thread and
	 * the prefetch workers alike), and the index is written once loading ends. A load stopped half-way leaves the pack longer
	 * than its index says, which costs only space; another parser build starts both again. Anything the format can't hold
	 * (a texture made in memory, a vertex type it doesn't know) is parsed every time, as before.
	 *
	 * Off unless a platform sets Folder (the Switch does: UserData/ObjectCache; UserData/nx-no-object-cache.txt keeps it off).
	 */
	internal static class ObjectDiskCache
	{
		/// <summary>Bumped when what is stored, or how the parsers' result is reached, changes outside the parsers' assemblies.</summary>
		private const int FormatVersion = 1;

		private const uint IndexMagic = 0x4F424E49, RecordMagic = 0x4F424E52; // "OBNI", "OBNR"

		/// <summary>Where the packs go; null: off.</summary>
		public static string Folder;

		private static readonly object gate = new object();
		private static FileStream pack;
		private static string packPath, indexPath, signature;
		private static Dictionary<string, (long Offset, int Length)> index;
		private static bool indexDirty;
		private static int hits, misses, stored, notStorable, failures;
		private static long hitTicks, storeTicks, writeTicks, packBytes;

		/// <summary>The pack is made longer in steps of this, ahead of the records: on the Switch a file that grows with each
		/// write is slow to write (99 MB took 310 s of the writer's time, 2026-11-07). packBytes is where the records end.</summary>
		private const long PackGrowth = 8L << 20;

		/*
		 * Records go to the SD card from a writer thread of their own: the parsing threads only turn an object into bytes (it
		 * may change once handed back) and queue them, rather than wait on the card and on each other for the pack. At most
		 * QueueLimit bytes wait; a thread that would go beyond waits for the writer (the card is slower than the parsers at
		 * times). Flush and Close wait for the queue to empty.
		 */
		private const long QueueLimit = 16L << 20;
		private static readonly Queue<(string Key, byte[] Payload)> queue = new Queue<(string, byte[])>();
		private static readonly HashSet<string> queuedKeys = new HashSet<string>(StringComparer.Ordinal);
		private static long queuedBytes;
		private static bool writing;
		private static System.Threading.Thread writer;

		/// <summary>The key TryLoad worked out for the file it didn't find, for Store to use rather than ask the card again.</summary>
		[ThreadStatic] private static string lastMissPath, lastMissKey;

		private static readonly FieldInfo OptimizedField = typeof(StaticObject).GetField("isOptimized", BindingFlags.Instance | BindingFlags.NonPublic);

		/// <summary>Whether a pack is open.</summary>
		public static bool Active
		{
			get
			{
				lock (gate)
				{
					return pack != null;
				}
			}
		}

		/// <summary>Opens (or starts) the route's pack. Call before the route's objects load.</summary>
		public static void Open(string routeFile)
		{
			if (string.IsNullOrEmpty(Folder) || string.IsNullOrEmpty(routeFile) || OptimizedField == null)
			{
				return;
			}

			lock (gate)
			{
				CloseLocked();
				hits = misses = stored = notStorable = failures = 0;
				fileSizes.Clear();
				hitTicks = storeTicks = writeTicks = 0;
				try
				{
					Directory.CreateDirectory(Folder);
					string name = Hash(routeFile.ToLowerInvariant());
					packPath = Path.Combine(Folder, name + ".pack");
					indexPath = Path.Combine(Folder, name + ".index");
					signature = Signature();
					index = ReadIndex(indexPath, signature, out long packLength);
					if (index == null)
					{
						// Nothing, or another parser build's: started again.
						index = new Dictionary<string, (long, int)>();
						TryDelete(packPath);
						TryDelete(indexPath);
						packLength = 0;
					}

					pack = new FileStream(packPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
					// What the index doesn't know of (a load that stopped before writing it) is written over.
					pack.SetLength(Math.Min(pack.Length, packLength));
					packBytes = pack.Length;
					indexDirty = false;
					if (writer == null)
					{
						writer = new System.Threading.Thread(WriteQueue) { IsBackground = true, Name = "object cache writer" };
						writer.Start();
					}
				}
				catch (Exception e)
				{
					Trace("object cache: off (" + e.GetType().Name + ": " + e.Message + ")");
					CloseLocked();
				}
			}
		}

		/// <summary>Writes the index, so that what was added so far is found next time. The pack stays open.</summary>
		public static void Flush()
		{
			lock (gate)
			{
				DrainLocked();
				FlushLocked();
			}
		}

		/// <summary>Writes the index and closes the pack: the end of the load.</summary>
		public static void Close()
		{
			lock (gate)
			{
				DrainLocked();
				FlushLocked();
				CloseLocked();
			}
		}

		public static string Describe()
		{
			lock (gate)
			{
				return "object cache: " + hits + " read back (" + (hitTicks * 1000 / Stopwatch.Frequency) + " ms), " + misses + " parsed, " + stored +
				       " stored (" + (storeTicks * 1000 / Stopwatch.Frequency) + " ms made, " + (writeTicks * 1000 / Stopwatch.Frequency) + " ms written), " + notStorable + " not storable, " + failures + " failures; " +
				       (index?.Count ?? 0) + " objects, " + (packBytes >> 20) + " MB";
			}
		}

		/// <summary>Whether the file is one the cache takes: the static object formats.</summary>
		private static bool Storable(string path)
		{
			string extension = Path.GetExtension(path);
			return extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".b3d", StringComparison.OrdinalIgnoreCase) ||
			       extension.Equals(".x", StringComparison.OrdinalIgnoreCase) || extension.Equals(".obj", StringComparison.OrdinalIgnoreCase);
		}

		/*
		 * Each file's size, asked of the storage once per load (a stat on the SD card a time, and an object is looked up
		 * twice, read back or stored): content doesn't change while loading. Cleared at Open.
		 */
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> fileSizes =
			new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);

		private static string Key(string path, Encoding encoding, bool preserveVertices, int threshold, bool vertexCulling)
		{
			long size = fileSizes.GetOrAdd(path, p => new FileInfo(p).Length);
			return path.ToLowerInvariant() + "|" + size + "|" + OpenBveApi.Path.LastWriteTime(path).Ticks + "|" + (encoding?.CodePage ?? 0) + "|" +
			       (preserveVertices ? 1 : 0) + "|" + threshold + "|" + (vertexCulling ? 1 : 0);
		}

		/// <summary>The object as it was stored for these settings, or false (then parse it, and <see cref="Store"/> the result).</summary>
		public static bool TryLoad(HostInterface host, string path, Encoding encoding, bool preserveVertices, int threshold, bool vertexCulling, out StaticObject staticObject)
		{
			staticObject = null;
			if (!Active || !Storable(path))
			{
				return false;
			}

			long started = Stopwatch.GetTimestamp();
			byte[] payload = null;
			try
			{
				// Each step timed apart in the load profile (the Switch's read-backs took 85 ms each, 2026-11-07).
				string key;
				using (LoadProfile.Enter("object cache: key"))
				{
					key = Key(path, encoding, preserveVertices, threshold, vertexCulling);
				}

				(long Offset, int Length) entry;
				FileStream reader;
				using (LoadProfile.Enter("object cache: read"))
				{
					lock (gate)
					{
						if (pack == null || !index.TryGetValue(key, out entry))
						{
							misses++;
							lastMissPath = path;
							lastMissKey = key;
							return false;
						}

						reader = Reader();
						if (reader == null)
						{
							// No handle of its own: read under the lock, from the pack's.
							payload = ReadAt(pack, entry.Offset, entry.Length);
						}
					}

					// From the thread's own handle, outside the lock: the loading threads no longer wait for each other.
					payload ??= ReadAt(reader, entry.Offset, entry.Length);
				}

				using (LoadProfile.Enter("object cache: decode"))
				{
					staticObject = Read(host, payload, key);
				}
			}
			catch (Exception e)
			{
				Trace("object cache: " + path + " not read back (" + e.GetType().Name + ": " + e.Message + ")");
				staticObject = null;
			}

			lock (gate)
			{
				if (staticObject != null)
				{
					hits++;
					hitTicks += Stopwatch.GetTimestamp() - started;
				}
				else
				{
					failures++;
				}
			}

			return staticObject != null;
		}

		/// <summary>Stores a parsed and optimised object for these settings.</summary>
		public static void Store(string path, Encoding encoding, bool preserveVertices, int threshold, bool vertexCulling, StaticObject staticObject)
		{
			if (staticObject?.Mesh == null || !Active || !Storable(path))
			{
				return;
			}

			long started = Stopwatch.GetTimestamp();
			try
			{
				string key = lastMissPath == path && lastMissKey != null && lastMissKey == KeyWith(lastMissKey, encoding, preserveVertices, threshold, vertexCulling)
					? lastMissKey
					: Key(path, encoding, preserveVertices, threshold, vertexCulling);
				lastMissPath = lastMissKey = null;
				byte[] payload = Write(staticObject, key);
				lock (gate)
				{
					storeTicks += Stopwatch.GetTimestamp() - started;
					if (payload == null)
					{
						notStorable++;
						return;
					}

					if (pack == null || index.ContainsKey(key) || !queuedKeys.Add(key))
					{
						return;
					}

					while (queuedBytes > 0 && queuedBytes + payload.Length > QueueLimit && pack != null)
					{
						System.Threading.Monitor.Wait(gate);
					}

					queue.Enqueue((key, payload));
					queuedBytes += payload.Length;
					System.Threading.Monitor.PulseAll(gate);
				}
			}
			catch (Exception e)
			{
				lock (gate)
				{
					failures++;
				}

				Trace("object cache: " + path + " not stored (" + e.GetType().Name + ": " + e.Message + ")");
			}
		}

		/// <summary>The key with these settings, from a key's file part (path, size, date).</summary>
		private static string KeyWith(string key, Encoding encoding, bool preserveVertices, int threshold, bool vertexCulling)
		{
			// The settings are the key's last four fields; the file part comes before them.
			int bar = key.Length;
			for (int i = 0; i < 4; i++)
			{
				bar = bar > 0 ? key.LastIndexOf('|', bar - 1) : -1;
				if (bar < 0)
				{
					return null;
				}
			}

			return key.Substring(0, bar + 1) + (encoding?.CodePage ?? 0) + "|" + (preserveVertices ? 1 : 0) + "|" + threshold + "|" + (vertexCulling ? 1 : 0);
		}

		/// <summary>The writer thread: appends the queued records to the pack.</summary>
		private static void WriteQueue()
		{
			while (true)
			{
				(string Key, byte[] Payload) record;
				lock (gate)
				{
					while (queue.Count == 0)
					{
						writing = false;
						System.Threading.Monitor.PulseAll(gate);
						System.Threading.Monitor.Wait(gate);
					}

					record = queue.Dequeue();
					writing = true;
				}

				long started = Stopwatch.GetTimestamp();
				bool done = false;
				try
				{
					lock (gate)
					{
						// The pack is written under the lock (TryLoad reads it under the lock too); the parsers no longer wait for it.
						if (pack != null && !index.ContainsKey(record.Key))
						{
							long offset = packBytes, end = offset + 8 + record.Payload.Length;
							if (end > pack.Length)
							{
								pack.SetLength(end + PackGrowth);
							}

							pack.Seek(offset, SeekOrigin.Begin);
							using (BinaryWriter header = new BinaryWriter(pack, Encoding.UTF8, true))
							{
								header.Write(RecordMagic);
								header.Write(record.Payload.Length);
							}

							pack.Write(record.Payload, 0, record.Payload.Length);
							index[record.Key] = (offset + 8, record.Payload.Length);
							packBytes = end;
							indexDirty = true;
							stored++;
							done = true;
						}
					}
				}
				catch (Exception e)
				{
					Trace("object cache: a record was not written (" + e.GetType().Name + ": " + e.Message + ")");
				}

				lock (gate)
				{
					if (!done && pack != null)
					{
						failures++;
					}

					queuedKeys.Remove(record.Key);
					queuedBytes -= record.Payload.Length;
					writeTicks += Stopwatch.GetTimestamp() - started;
					System.Threading.Monitor.PulseAll(gate);
				}
			}
		}

		/// <summary>Waits (holding the lock between waits) until the writer has written everything queued.</summary>
		private static void DrainLocked()
		{
			while (queue.Count > 0 || writing)
			{
				System.Threading.Monitor.Wait(gate);
			}
		}

		/*
		 * Each loading thread reads the pack through a handle of its own (the pack is opened sharing reads), so a read
		 * back no longer queues behind the others' - on the Switch 4,810 of them took 420 s of the threads' time under one
		 * lock (2026-11-07). Only records the index already lists are read, and the writer only appends past them.
		 * A storage that refuses the second handle leaves the thread on the shared one, under the lock.
		 */
		[ThreadStatic] private static FileStream threadReader;
		[ThreadStatic] private static bool threadReaderFailed;
		[ThreadStatic] private static int threadReaderGeneration;
		// Bumped whenever the handles are closed: a thread's handle from an earlier pack is not used again.
		private static int readerGeneration = 1;
		private static readonly List<FileStream> readers = new List<FileStream>();

		/// <summary>The calling thread's read handle on the pack (null: none; read under the lock). Call under the lock.</summary>
		private static FileStream Reader()
		{
			if (threadReaderGeneration != readerGeneration)
			{
				threadReader = null;
				threadReaderFailed = false;
				threadReaderGeneration = readerGeneration;
			}

			if (threadReader != null || threadReaderFailed || packPath == null)
			{
				return threadReader;
			}

			try
			{
				threadReader = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
				readers.Add(threadReader);
			}
			catch (Exception)
			{
				threadReaderFailed = true;
			}

			return threadReader;
		}

		private static byte[] ReadAt(FileStream stream, long offset, int length)
		{
			byte[] payload = new byte[length];
			stream.Seek(offset, SeekOrigin.Begin);
			int read = 0;
			while (read < length)
			{
				int n = stream.Read(payload, read, length - read);
				if (n <= 0)
				{
					throw new EndOfStreamException();
				}

				read += n;
			}

			return payload;
		}

		// --- the index ---

		private static Dictionary<string, (long, int)> ReadIndex(string path, string expectedSignature, out long packLength)
		{
			packLength = 0;
			if (!File.Exists(path))
			{
				return null;
			}

			try
			{
				using (BinaryReader reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), Encoding.UTF8))
				{
					if (reader.ReadUInt32() != IndexMagic || reader.ReadString() != expectedSignature)
					{
						return null;
					}

					packLength = reader.ReadInt64();
					int count = reader.ReadInt32();
					Dictionary<string, (long, int)> entries = new Dictionary<string, (long, int)>(count);
					for (int i = 0; i < count; i++)
					{
						string key = reader.ReadString();
						long offset = reader.ReadInt64();
						int length = reader.ReadInt32();
						if (offset < 0 || length < 0 || offset + length > packLength)
						{
							return null;
						}

						entries[key] = (offset, length);
					}

					return entries;
				}
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static void FlushLocked()
		{
			if (pack == null || !indexDirty)
			{
				return;
			}

			try
			{
				pack.Flush(true);
				string temporary = indexPath + ".new";
				using (BinaryWriter writer = new BinaryWriter(new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), Encoding.UTF8))
				{
					writer.Write(IndexMagic);
					writer.Write(signature);
					writer.Write(packBytes);
					writer.Write(index.Count);
					foreach (KeyValuePair<string, (long Offset, int Length)> entry in index)
					{
						writer.Write(entry.Key);
						writer.Write(entry.Value.Offset);
						writer.Write(entry.Value.Length);
					}
				}

				TryDelete(indexPath);
				File.Move(temporary, indexPath);
				indexDirty = false;
			}
			catch (Exception e)
			{
				Trace("object cache: the index was not written (" + e.GetType().Name + ": " + e.Message + ")");
			}
		}

		private static void CloseLocked()
		{
			// The threads' read handles (the loading is over: none is reading).
			foreach (FileStream reader in readers)
			{
				reader.Dispose();
			}

			readers.Clear();
			readerGeneration++;
			if (pack != null)
			{
				try
				{
					// The space grown ahead, given back.
					if (pack.Length > packBytes)
					{
						pack.SetLength(packBytes);
					}
				}
				catch (Exception)
				{
					// the index says where the records end; the rest is only space
				}

				pack.Dispose();
			}

			pack = null;
		}

		private static void TryDelete(string path)
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch (Exception)
			{
				// left as it is
			}
		}

		/// <summary>The format's version and the identities of the assemblies that make an object what it is: the API (its
		/// OptimizeObject included) and the parsers. A changed parser build starts the cache again.</summary>
		private static string Signature()
		{
			List<string> parts = new List<string> { "v" + FormatVersion };
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				string name = assembly.GetName().Name ?? string.Empty;
				if (name.StartsWith("OpenBveApi", StringComparison.Ordinal) || name.StartsWith("Object.", StringComparison.Ordinal) ||
				    name.StartsWith("Formats.", StringComparison.Ordinal) || name.StartsWith("AssimpParser", StringComparison.Ordinal))
				{
					parts.Add(name + ":" + assembly.ManifestModule.ModuleVersionId);
				}
			}

			parts.Sort(StringComparer.Ordinal);
			return Hash(string.Join(";", parts));
		}

		/// <summary>FNV-1a over the text's UTF-16 units, as 16 hex digits (no cryptography: the Switch's runtime has no native
		/// provider for it, and nothing here needs one).</summary>
		private static string Hash(string text)
		{
			ulong hash = 14695981039346656037UL;
			foreach (char c in text)
			{
				hash = (hash ^ (byte)c) * 1099511628211UL;
				hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
			}

			return hash.ToString("x16");
		}

		private static void Trace(string message)
		{
			global::Android.Util.Log.Info("OpenBVE", message);
		}

		// --- an object, written and read ---

		private const byte VertexPlain = 0, VertexColored = 1, VertexLightMapped = 2, VertexAnimated = 3;

		/// <summary>The object's bytes, or null when the format can't hold it.</summary>
		private static byte[] Write(StaticObject staticObject, string key)
		{
			Mesh mesh = staticObject.Mesh;
			using (MemoryStream stream = new MemoryStream(4096))
			using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
			{
				// The key, so a record is never taken for another file's.
				writer.Write(key);
				writer.Write(staticObject.StartingTrackDistance);
				writer.Write(staticObject.EndingTrackDistance);
				writer.Write(staticObject.Dynamic);
				WriteString(writer, staticObject.Author);
				WriteString(writer, staticObject.Copyright);
				writer.Write(OptimizedField.GetValue(staticObject) is bool optimized && optimized);

				VertexTemplate[] vertices = mesh.Vertices ?? Array.Empty<VertexTemplate>();
				writer.Write(vertices.Length);
				foreach (VertexTemplate vertex in vertices)
				{
					switch (vertex)
					{
						case ColoredVertex colored when colored.GetType() == typeof(ColoredVertex):
							writer.Write(VertexColored);
							WriteVertex(writer, vertex);
							writer.Write(colored.Color.R);
							writer.Write(colored.Color.G);
							writer.Write(colored.Color.B);
							writer.Write(colored.Color.A);
							break;
						case LightMappedVertex lightMapped when lightMapped.GetType() == typeof(LightMappedVertex):
							writer.Write(VertexLightMapped);
							WriteVertex(writer, vertex);
							writer.Write(lightMapped.LightMapCoordinates.X);
							writer.Write(lightMapped.LightMapCoordinates.Y);
							break;
						case AnimatedVertex animated when animated.GetType() == typeof(AnimatedVertex):
							writer.Write(VertexAnimated);
							WriteVertex(writer, vertex);
							int[] chain = animated.MatrixChain ?? Array.Empty<int>();
							writer.Write(animated.MatrixChain == null ? -1 : chain.Length);
							foreach (int link in chain)
							{
								writer.Write(link);
							}

							break;
						case Vertex plain when plain.GetType() == typeof(Vertex):
							writer.Write(VertexPlain);
							WriteVertex(writer, vertex);
							break;
						default:
							// A vertex type this format doesn't know.
							return null;
					}
				}

				MeshMaterial[] materials = mesh.Materials ?? Array.Empty<MeshMaterial>();
				writer.Write(materials.Length);
				foreach (MeshMaterial material in materials)
				{
					writer.Write((int)material.Flags);
					WriteColor(writer, material.Color);
					WriteColor(writer, material.TransparentColor);
					WriteColor(writer, material.EmissiveColor);
					WriteColor(writer, material.SpecularColor);
					writer.Write((int)material.BlendMode);
					writer.Write(material.GlowAttenuationData);
					writer.Write(material.WrapMode.HasValue ? (int)material.WrapMode.Value : -1);
					if (!WriteTexture(writer, material.DaytimeTexture) || !WriteTexture(writer, material.NighttimeTexture) || !WriteTexture(writer, material.LightMapTexture))
					{
						return null;
					}
				}

				MeshFace[] faces = mesh.Faces ?? Array.Empty<MeshFace>();
				writer.Write(faces.Length);
				foreach (MeshFace face in faces)
				{
					writer.Write(face.Material);
					writer.Write((byte)face.Flags);
					writer.Write(face.IboStartIndex);
					writer.Write(face.NormalsIboStartIndex);
					MeshFaceVertex[] faceVertices = face.Vertices;
					writer.Write(faceVertices == null ? -1 : faceVertices.Length);
					if (faceVertices != null)
					{
						foreach (MeshFaceVertex faceVertex in faceVertices)
						{
							writer.Write(faceVertex.Index);
							WriteVector(writer, faceVertex.Normal);
						}
					}
				}

				Vector3[] box = mesh.BoundingBox;
				writer.Write(box == null ? -1 : box.Length);
				if (box != null)
				{
					foreach (Vector3 corner in box)
					{
						WriteVector(writer, corner);
					}
				}

				writer.Flush();
				return stream.ToArray();
			}
		}

		private static StaticObject Read(HostInterface host, byte[] payload, string key)
		{
			using (BinaryReader reader = new BinaryReader(new MemoryStream(payload, false), Encoding.UTF8))
			{
				if (reader.ReadString() != key)
				{
					throw new InvalidDataException("the record is another file's");
				}

				StaticObject staticObject = new StaticObject(host)
				{
					StartingTrackDistance = reader.ReadSingle(),
					EndingTrackDistance = reader.ReadSingle(),
					Dynamic = reader.ReadBoolean(),
					Author = ReadString(reader),
					Copyright = ReadString(reader)
				};
				bool optimized = reader.ReadBoolean();
				Mesh mesh = staticObject.Mesh;

				int vertexCount = reader.ReadInt32();
				mesh.Vertices = new VertexTemplate[vertexCount];
				for (int i = 0; i < vertexCount; i++)
				{
					byte type = reader.ReadByte();
					Vector3 coordinates = ReadVector(reader);
					Vector2 textureCoordinates = new Vector2(reader.ReadDouble(), reader.ReadDouble());
					switch (type)
					{
						case VertexPlain:
							mesh.Vertices[i] = new Vertex(coordinates, textureCoordinates);
							break;
						case VertexColored:
							mesh.Vertices[i] = new ColoredVertex(coordinates, textureCoordinates,
								new Color128(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
							break;
						case VertexLightMapped:
							mesh.Vertices[i] = new LightMappedVertex(coordinates, textureCoordinates, new Vector2(reader.ReadDouble(), reader.ReadDouble()));
							break;
						case VertexAnimated:
							int length = reader.ReadInt32();
							int[] chain = length < 0 ? null : new int[length];
							for (int j = 0; j < length; j++)
							{
								chain[j] = reader.ReadInt32();
							}

							mesh.Vertices[i] = new AnimatedVertex(coordinates, textureCoordinates, chain);
							break;
						default:
							throw new InvalidDataException("unknown vertex type " + type);
					}
				}

				int materialCount = reader.ReadInt32();
				mesh.Materials = new MeshMaterial[materialCount];
				for (int i = 0; i < materialCount; i++)
				{
					MeshMaterial material = new MeshMaterial
					{
						Flags = (MaterialFlags)reader.ReadInt32(),
						Color = ReadColor32(reader),
						TransparentColor = ReadColor24(reader),
						EmissiveColor = ReadColor32(reader),
						SpecularColor = ReadColor24(reader),
						BlendMode = (MeshMaterialBlendMode)reader.ReadInt32(),
						GlowAttenuationData = reader.ReadUInt16()
					};
					int wrap = reader.ReadInt32();
					material.WrapMode = wrap < 0 ? (OpenGlTextureWrapMode?)null : (OpenGlTextureWrapMode)wrap;
					material.DaytimeTexture = ReadTexture(host, reader);
					material.NighttimeTexture = ReadTexture(host, reader);
					material.LightMapTexture = ReadTexture(host, reader);
					mesh.Materials[i] = material;
				}

				int faceCount = reader.ReadInt32();
				mesh.Faces = new MeshFace[faceCount];
				for (int i = 0; i < faceCount; i++)
				{
					MeshFace face = new MeshFace
					{
						Material = reader.ReadUInt16(),
						Flags = (FaceFlags)reader.ReadByte(),
						IboStartIndex = reader.ReadInt32(),
						NormalsIboStartIndex = reader.ReadInt32()
					};
					int length = reader.ReadInt32();
					if (length >= 0)
					{
						face.Vertices = new MeshFaceVertex[length];
						for (int j = 0; j < length; j++)
						{
							face.Vertices[j].Index = reader.ReadInt32();
							face.Vertices[j].Normal = ReadVector(reader);
						}
					}

					mesh.Faces[i] = face;
				}

				int corners = reader.ReadInt32();
				if (corners >= 0)
				{
					mesh.BoundingBox = new Vector3[corners];
					for (int i = 0; i < corners; i++)
					{
						mesh.BoundingBox[i] = ReadVector(reader);
					}
				}
				else
				{
					mesh.BoundingBox = null;
				}

				OptimizedField.SetValue(staticObject, optimized);
				return staticObject;
			}
		}

		private static void WriteVertex(BinaryWriter writer, VertexTemplate vertex)
		{
			WriteVector(writer, vertex.Coordinates);
			writer.Write(vertex.TextureCoordinates.X);
			writer.Write(vertex.TextureCoordinates.Y);
		}

		private static void WriteVector(BinaryWriter writer, Vector3 vector)
		{
			writer.Write(vector.X);
			writer.Write(vector.Y);
			writer.Write(vector.Z);
		}

		private static Vector3 ReadVector(BinaryReader reader) => new Vector3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

		private static void WriteColor(BinaryWriter writer, Color32 color)
		{
			writer.Write(color.R);
			writer.Write(color.G);
			writer.Write(color.B);
			writer.Write(color.A);
		}

		private static void WriteColor(BinaryWriter writer, Color24 color)
		{
			writer.Write(color.R);
			writer.Write(color.G);
			writer.Write(color.B);
		}

		private static Color32 ReadColor32(BinaryReader reader) => new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());

		private static Color24 ReadColor24(BinaryReader reader) => new Color24(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());

		private static void WriteString(BinaryWriter writer, string text)
		{
			writer.Write(text != null);
			if (text != null)
			{
				writer.Write(text);
			}
		}

		private static string ReadString(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

		/// <summary>A texture by its file and parameters; false for one the format can't hold (not from a file, or with a
		/// transparency texture of its own).</summary>
		private static bool WriteTexture(BinaryWriter writer, Texture texture)
		{
			if (texture == null)
			{
				writer.Write(false);
				return true;
			}

			if (!(texture.Origin is PathOrigin origin) || origin.GetType() != typeof(PathOrigin) || string.IsNullOrEmpty(origin.Path))
			{
				return false;
			}

			TextureParameters parameters = origin.Parameters;
			if (parameters != null && parameters.TransparencyTexture != null)
			{
				return false;
			}

			writer.Write(true);
			writer.Write(origin.Path);
			writer.Write(parameters != null);
			if (parameters != null)
			{
				TextureClipRegion clip = parameters.ClipRegion;
				writer.Write(clip != null);
				if (clip != null)
				{
					writer.Write(clip.Left);
					writer.Write(clip.Top);
					writer.Write(clip.Width);
					writer.Write(clip.Height);
				}

				writer.Write(parameters.TransparentColor.HasValue);
				if (parameters.TransparentColor.HasValue)
				{
					WriteColor(writer, parameters.TransparentColor.Value);
				}

				writer.Write(parameters.FirstColorTransparent);
			}

			return true;
		}

		private static Texture ReadTexture(HostInterface host, BinaryReader reader)
		{
			if (!reader.ReadBoolean())
			{
				return null;
			}

			string path = reader.ReadString();
			TextureParameters parameters = null;
			if (reader.ReadBoolean())
			{
				TextureClipRegion clip = reader.ReadBoolean() ? new TextureClipRegion(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()) : null;
				Color24? transparent = reader.ReadBoolean() ? ReadColor24(reader) : (Color24?)null;
				parameters = new TextureParameters(clip, transparent) { FirstColorTransparent = reader.ReadBoolean() };
			}

			// Registered again: the host gives the handle it gave the parser.
			if (!host.RegisterTexture(path, parameters ?? TextureParameters.NoChange, out Texture handle) || handle == null)
			{
				throw new InvalidDataException("texture " + path + " was not registered");
			}

			return handle;
		}
	}
}
