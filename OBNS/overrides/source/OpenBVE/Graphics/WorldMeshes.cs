// OBNS: not an upstream file. The world layer's meshes (OBNS/VULKAN.md, OBNS/DIRECT3D.md), whichever API draws it:
// each mesh's faces as triangle lists (the APIs have no quads or polygons), over upstream's vertex buffers; where the
// vertices and indices are kept is the API's (WorldMeshes' subclasses).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibRender2;
using OpenBveApi.Objects;

namespace OpenBve.Graphics
{
	/// <summary>Where a mesh's vertices and triangle indices are, and where each face's indices are.</summary>
	internal sealed class WorldMesh
	{
		/// <summary>The vertex storage's block and the mesh's first vertex in it.</summary>
		internal int VertexBlock, VertexBase;
		/// <summary>The triangle indices: their range, block, and each face's first index there and count.</summary>
		internal GpuRange Indices;
		internal int IndexBlock;
		/// <summary>Each face's first vertex upstream (MeshFace.IboStartIndex, ascending).</summary>
		internal int[] FaceStarts, FaceFirstIndex, FaceIndexCount;
		internal WeakReference Owner;
		internal long FreedAt;

		/// <summary>Each face's textures' places in the layer's textures as last looked up (daytime, nighttime).</summary>
		internal TextureSlot[] DaySlots, NightSlots;

		/// <summary>The mesh's bounding sphere in its own space (its vertices' box's centre, the farthest vertex); a radius below 0 until worked out.</summary>
		internal double BoundsX, BoundsY, BoundsZ, BoundsRadius = -1.0;

		/// <summary>The face starting at a vertex (its slot here) and its triangle indices: false if the mesh has no such face.</summary>
		internal bool Find(int iboStart, out int slot, out uint firstIndex, out uint indexCount)
		{
			slot = Array.BinarySearch(FaceStarts, iboStart);
			if (slot < 0 || FaceIndexCount[slot] == 0)
			{
				firstIndex = indexCount = 0;
				return false;
			}

			firstIndex = (uint)FaceFirstIndex[slot];
			indexCount = (uint)FaceIndexCount[slot];
			return true;
		}
	}

	/// <summary>
	/// A face's texture's place in the layer's textures, with the OpenGL texture and the texture table's generation it
	/// was looked up in. Never changed once made, so threads see a whole one or none (it is replaced, not updated).
	/// </summary>
	internal sealed class TextureSlot
	{
		internal readonly OpenBveApi.Textures.Texture Texture;
		internal readonly int Index, Generation;

		internal TextureSlot(OpenBveApi.Textures.Texture texture, int index, int generation)
		{
			Texture = texture;
			Index = index;
			Generation = generation;
		}
	}

	/// <summary>
	/// The meshes' records and triangle indices, and the vertex buffers' storage (ObnsSharing's hooks), kept by the API:
	/// what is let go of is freed once the frames in flight are done with it.
	/// </summary>
	internal abstract class WorldMeshes : IDisposable
	{
		internal static readonly int VertexSize = Marshal.SizeOf(typeof(LibRenderVertex));

		private readonly List<WorldMesh> live = new List<WorldMesh>();
		private readonly List<WorldMesh> freedMeshes = new List<WorldMesh>();
		private readonly List<KeyValuePair<long, GpuRange>> freedVertices = new List<KeyValuePair<long, GpuRange>>();
		private readonly List<uint> indices = new List<uint>();
		private int sweep;

		/// <summary>The frame being made (what is let go of during it is freed three frames later).</summary>
		protected long Frame { get; private set; } = 1;

		/// <summary>Storage for a vertex buffer of so many bytes (null: OpenGL's own, and the layer does not draw it).</summary>
		internal abstract ObnsSharedMemory AllocateVertices(long bytes);

		/// <summary>Writes vertices into a buffer's storage the API's way (false: OpenGL writes them, as storage shared with it).</summary>
		internal abstract bool WriteVertices(ObnsSharedMemory shared, LibRenderVertex[] data, long byteOffset);

		/// <summary>OpenGL has written a vertex buffer in storage shared with it.</summary>
		internal virtual void Written(int buffer)
		{
		}

		/// <summary>A vertex buffer's storage is let go of: back to the API once the frames in flight are done with it.</summary>
		internal virtual void FreeVertices(ObnsSharedMemory shared)
		{
			if (shared?.Owner is GpuRange range)
			{
				freedVertices.Add(new KeyValuePair<long, GpuRange>(Frame, range));
			}
		}

		/// <summary>The vertex range of a VAO's buffer, if the layer keeps it.</summary>
		protected static bool VertexRange(VertexArrayObject vao, out GpuRange range)
		{
			if (vao?.ObnsVbo?.ObnsShared?.Owner is GpuRange owned)
			{
				range = owned;
				return true;
			}

			range = default(GpuRange);
			return false;
		}

		/// <summary>Stores triangle indices (so many bytes), returning their range (its block is the index storage's).</summary>
		protected abstract GpuRange StoreIndices(uint[] data, ulong bytes);

		protected abstract void FreeIndices(GpuRange range);

		protected abstract void FreeVertexRange(GpuRange range);

		/// <summary>Called after a mesh's indices are stored (the API may send its uploads when it has enough).</summary>
		protected virtual void Stored()
		{
		}

		/// <summary>Indexes a mesh upstream has made (or updated) a VAO for, if the layer keeps its vertex buffer.</summary>
		internal void Uploaded(Mesh mesh)
		{
			if (!(mesh?.VAO is VertexArrayObject vao))
			{
				return;
			}

			if (vao.ObnsWorldMesh is WorldMesh old)
			{
				Release(old);
				vao.ObnsWorldMesh = null;
			}

			if (!VertexRange(vao, out GpuRange vertices) || mesh.Faces == null)
			{
				return;
			}

			// The faces as triangle lists, over the mesh's vertices (one per face corner, from each face's IboStartIndex).
			indices.Clear();
			int faceCount = mesh.Faces.Length;
			WorldMesh copy = new WorldMesh
			{
				VertexBlock = vertices.Block,
				VertexBase = (int)(vertices.Offset / (ulong)VertexSize),
				FaceStarts = new int[faceCount],
				FaceFirstIndex = new int[faceCount],
				FaceIndexCount = new int[faceCount],
				DaySlots = new TextureSlot[faceCount],
				NightSlots = new TextureSlot[faceCount],
				Owner = new WeakReference(vao)
			};
			bool sorted = true;
			for (int f = 0; f < faceCount; f++)
			{
				MeshFace face = mesh.Faces[f];
				int start = face.IboStartIndex;
				copy.FaceStarts[f] = start;
				sorted &= f == 0 || start > copy.FaceStarts[f - 1];
				copy.FaceFirstIndex[f] = indices.Count;
				Triangulate(face.Flags & FaceFlags.FaceTypeMask, start, face.Vertices?.Length ?? 0);
				copy.FaceIndexCount[f] = indices.Count - copy.FaceFirstIndex[f];
			}

			if (!sorted)
			{
				Array.Sort((int[])copy.FaceStarts.Clone(), copy.FaceFirstIndex);
				Array.Sort(copy.FaceStarts, copy.FaceIndexCount);
			}

			if (indices.Count > 0)
			{
				copy.Indices = StoreIndices(indices.ToArray(), (ulong)indices.Count * 4);
				copy.IndexBlock = copy.Indices.Block;
				int indexBase = (int)(copy.Indices.Offset / 4);
				for (int f = 0; f < faceCount; f++)
				{
					copy.FaceFirstIndex[f] += indexBase;
				}
			}

			vao.ObnsWorldMesh = copy;
			live.Add(copy);
			Stored();
		}

		/// <summary>Appends a face's triangles, in the winding OpenGL would draw them with.</summary>
		private void Triangulate(FaceFlags type, int s, int n)
		{
			switch (type)
			{
				case FaceFlags.Triangles:
					for (int k = 0; k + 2 < n; k += 3)
					{
						Add(s + k, s + k + 1, s + k + 2);
					}

					break;
				case FaceFlags.TriangleStrip:
					for (int k = 0; k + 2 < n; k++)
					{
						if ((k & 1) == 0) Add(s + k, s + k + 1, s + k + 2);
						else Add(s + k + 1, s + k, s + k + 2);
					}

					break;
				case FaceFlags.Quads:
					for (int k = 0; k + 3 < n; k += 4)
					{
						Add(s + k, s + k + 1, s + k + 2);
						Add(s + k, s + k + 2, s + k + 3);
					}

					break;
				case FaceFlags.QuadStrip:
					// Quad i has the corners 2i, 2i+1, 2i+3, 2i+2 in order.
					for (int k = 0; k + 3 < n; k += 2)
					{
						Add(s + k, s + k + 1, s + k + 3);
						Add(s + k, s + k + 3, s + k + 2);
					}

					break;
				default:
					// A polygon, as a fan.
					for (int k = 1; k + 1 < n; k++)
					{
						Add(s, s + k, s + k + 1);
					}

					break;
			}
		}

		private void Add(int a, int b, int c)
		{
			indices.Add((uint)a);
			indices.Add((uint)b);
			indices.Add((uint)c);
		}

		/// <summary>Lets go of a disposed VAO's record, once the frames in flight are done with it.</summary>
		internal void Disposed(VertexArrayObject vao)
		{
			if (vao?.ObnsWorldMesh is WorldMesh mesh)
			{
				Release(mesh);
				vao.ObnsWorldMesh = null;
			}
		}

		private void Release(WorldMesh mesh)
		{
			if (mesh.FreedAt == 0)
			{
				mesh.FreedAt = Frame;
				freedMeshes.Add(mesh);
			}
		}

		/// <summary>Called each frame: frees what the frames in flight can no longer use, and looks for collected VAOs.</summary>
		internal void FrameStarted(long frameNumber)
		{
			Frame = frameNumber;
			for (int i = freedMeshes.Count - 1; i >= 0; i--)
			{
				if (frameNumber - freedMeshes[i].FreedAt > 3)
				{
					FreeIndices(freedMeshes[i].Indices);
					freedMeshes[i].Indices = default(GpuRange);
					freedMeshes.RemoveAt(i);
				}
			}

			for (int i = freedVertices.Count - 1; i >= 0; i--)
			{
				if (frameNumber - freedVertices[i].Key > 3)
				{
					FreeVertexRange(freedVertices[i].Value);
					freedVertices.RemoveAt(i);
				}
			}

			// A few hundred a frame: VAOs collected without being disposed, and records already let go of.
			for (int checkedCount = 0; checkedCount < 256 && live.Count > 0; checkedCount++)
			{
				if (sweep >= live.Count)
				{
					sweep = 0;
				}

				WorldMesh mesh = live[sweep];
				if (mesh.FreedAt != 0 || !mesh.Owner.IsAlive)
				{
					Release(mesh);
					live[sweep] = live[live.Count - 1];
					live.RemoveAt(live.Count - 1);
				}
				else
				{
					sweep++;
				}
			}
		}

		/// <summary>Where the vertices and indices are, for the statistics.</summary>
		protected abstract string DescribeStorage();

		internal string Describe() => live.Count + " meshes, " + DescribeStorage();

		public virtual void Dispose()
		{
			live.Clear();
			freedMeshes.Clear();
			freedVertices.Clear();
		}
	}
}
