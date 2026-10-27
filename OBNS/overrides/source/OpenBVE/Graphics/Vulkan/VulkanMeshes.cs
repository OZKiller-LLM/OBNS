// OBNS: not an upstream file. The Vulkan world layer's meshes (OBNS/VULKAN.md): upstream's vertex buffers, in storage
// shared with OpenGL, read as they are, and each mesh's faces as triangle lists (Vulkan has no quads or polygons).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibRender2;
using OpenBveApi.Objects;

namespace OpenBve.Graphics.Vulkan
{
	/// <summary>Where a mesh's vertices and triangle indices are, and where each face's indices are.</summary>
	internal sealed class VulkanMesh
	{
		/// <summary>The shared vertex buffer's block and the mesh's first vertex in it.</summary>
		internal int VertexBlock, VertexBase;
		/// <summary>The triangle indices: their range, block, and each face's first index there and count.</summary>
		internal GpuRange Indices;
		internal int IndexBlock;
		/// <summary>Each face's first vertex upstream (MeshFace.IboStartIndex, ascending).</summary>
		internal int[] FaceStarts, FaceFirstIndex, FaceIndexCount;
		internal WeakReference Owner;
		internal long FreedAt;

		/// <summary>Each face's textures' places in the bindless array as last looked up (daytime, nighttime).</summary>
		internal TextureSlot[] DaySlots, NightSlots;

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
	/// A face's texture's place in the bindless array, with the OpenGL texture and the texture table's generation it
	/// was looked up in. Never changed once made, so threads see a whole one or none (it is replaced, not updated).
	/// </summary>
	internal sealed class TextureSlot
	{
		internal readonly OpenBveApi.Textures.OpenGlTexture Texture;
		internal readonly int Index, Generation;

		internal TextureSlot(OpenBveApi.Textures.OpenGlTexture texture, int index, int generation)
		{
			Texture = texture;
			Index = index;
			Generation = generation;
		}
	}

	internal sealed unsafe class VulkanMeshes : IDisposable
	{
		internal static readonly int VertexSize = Marshal.SizeOf(typeof(LibRenderVertex));

		/// <summary>Vertex buffers' alignment in the shared heap: a whole vertex (for the draws' vertex offsets) and 64 bytes.</summary>
		private static readonly ulong vertexAlignment = (ulong)(VertexSize * 64 / Gcd(VertexSize, 64));

		private readonly Uploader uploader;

		/// <summary>The vertex buffers' storage, shared with OpenGL.</summary>
		internal readonly SharedHeap Vertices;

		/// <summary>The triangle indices (Vulkan's own).</summary>
		internal readonly GpuPool Indices;

		private readonly List<VulkanMesh> live = new List<VulkanMesh>();
		private readonly List<VulkanMesh> freedMeshes = new List<VulkanMesh>();
		private readonly List<KeyValuePair<long, GpuRange>> freedVertices = new List<KeyValuePair<long, GpuRange>>();
		private readonly List<uint> indices = new List<uint>();
		private int sweep;
		private long frame = 1;

		/// <summary>Whether OpenGL has written vertex buffers since the last frame.</summary>
		private bool written;
		private readonly HashSet<int> writtenBuffers = new HashSet<int>();

		internal VulkanMeshes(VulkanDevice device, Uploader uploader)
		{
			this.uploader = uploader;
			Vertices = new SharedHeap(device, 128UL << 20, Vk.BufferUsageVertexBuffer | Vk.BufferUsageTransferDst | Vk.BufferUsageTransferSrc, "vertices");
			Indices = new GpuPool(device, 64UL << 20, Vk.BufferUsageIndexBuffer | Vk.BufferUsageTransferDst, "indices");
		}

		private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

		/// <summary>Storage for a vertex buffer of so many bytes, in the shared heap.</summary>
		internal ObnsSharedMemory AllocateVertices(long bytes)
		{
			GpuRange range = Vertices.Allocate((ulong)bytes, vertexAlignment, ~0u);
			return new ObnsSharedMemory { GLMemory = Vertices.GLMemory(range.Block), Offset = range.Offset, Owner = range };
		}

		/// <summary>A vertex buffer's storage is let go of: back to the heap once the frames in flight are done with it.</summary>
		internal void FreeVertices(ObnsSharedMemory shared)
		{
			if (shared?.Owner is GpuRange range)
			{
				freedVertices.Add(new KeyValuePair<long, GpuRange>(frame, range));
				writtenBuffers.Remove(shared.GLBuffer);
			}
		}

		/// <summary>OpenGL has written a vertex buffer (it is handed over at the next frame).</summary>
		internal void Written(int buffer)
		{
			written = true;
			writtenBuffers.Add(buffer);
		}

		/// <summary>
		/// The vertex buffers written since the last frame, handed over now: their OpenGL names added for OpenGL's
		/// semaphore signal; whether there were any, for the frame's barrier. Then forgotten.
		/// </summary>
		internal bool TakeWritten(List<int> names)
		{
			names.AddRange(writtenBuffers);
			bool any = written;
			written = false;
			writtenBuffers.Clear();
			return any;
		}

		/// <summary>Records making vertex writes OpenGL handed over visible to the frame's vertex input.</summary>
		internal void AcquireWritten(IntPtr commandBuffer, bool handed)
		{
			if (!handed)
			{
				return;
			}

			Vk.MemoryBarrier barrier = new Vk.MemoryBarrier { SType = Vk.StructureTypeMemoryBarrier, DstAccessMask = Vk.AccessVertexAttributeRead };
			Vk.vkCmdPipelineBarrier(commandBuffer, Vk.PipelineStageTopOfPipe, Vk.PipelineStageVertexInput, 0, 1, &barrier, 0, null, 0, null);
		}

		/// <summary>Indexes a mesh upstream has made (or updated) a VAO for, if its vertex buffer is in shared storage.</summary>
		internal void Uploaded(Mesh mesh)
		{
			if (!(mesh?.VAO is VertexArrayObject vao))
			{
				return;
			}

			if (vao.ObnsVulkanMesh is VulkanMesh old)
			{
				Release(old);
				vao.ObnsVulkanMesh = null;
			}

			if (!(vao.ObnsVbo?.ObnsShared?.Owner is GpuRange vertices) || mesh.Faces == null)
			{
				return;
			}

			// The faces as triangle lists, over the mesh's vertices (one per face corner, from each face's IboStartIndex).
			indices.Clear();
			int faceCount = mesh.Faces.Length;
			VulkanMesh copy = new VulkanMesh
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
				ulong bytes = (ulong)indices.Count * 4;
				copy.Indices = Indices.Allocate(bytes, 4);
				copy.IndexBlock = copy.Indices.Block;
				int indexBase = (int)(copy.Indices.Offset / 4);
				for (int f = 0; f < faceCount; f++)
				{
					copy.FaceFirstIndex[f] += indexBase;
				}

				uint[] indexArray = indices.ToArray();
				fixed (uint* data = indexArray)
				{
					ulong staging = uploader.Stage(data, bytes, out ulong stagingOffset);
					Vk.BufferCopy region = new Vk.BufferCopy { SrcOffset = stagingOffset, DstOffset = copy.Indices.Offset, Size = bytes };
					Vk.vkCmdCopyBuffer(uploader.Commands, staging, Indices.Buffers[copy.IndexBlock], 1, &region);
				}
			}

			vao.ObnsVulkanMesh = copy;
			live.Add(copy);
			if (uploader.Full)
			{
				uploader.Submit(true);
			}
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
			if (vao?.ObnsVulkanMesh is VulkanMesh mesh)
			{
				Release(mesh);
				vao.ObnsVulkanMesh = null;
			}
		}

		private void Release(VulkanMesh mesh)
		{
			if (mesh.FreedAt == 0)
			{
				mesh.FreedAt = frame;
				freedMeshes.Add(mesh);
			}
		}

		/// <summary>Called each frame: frees what the frames in flight can no longer use, and looks for collected VAOs.</summary>
		internal void FrameStarted(long frameNumber)
		{
			frame = frameNumber;
			for (int i = freedMeshes.Count - 1; i >= 0; i--)
			{
				if (frameNumber - freedMeshes[i].FreedAt > 3)
				{
					Indices.Free(freedMeshes[i].Indices);
					freedMeshes[i].Indices = default(GpuRange);
					freedMeshes.RemoveAt(i);
				}
			}

			for (int i = freedVertices.Count - 1; i >= 0; i--)
			{
				if (frameNumber - freedVertices[i].Key > 3)
				{
					Vertices.Free(freedVertices[i].Value);
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

				VulkanMesh mesh = live[sweep];
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

		internal string Describe() => live.Count + " meshes, " + Vertices.Describe() + ", " + Indices.Describe();

		public void Dispose()
		{
			Vertices.Dispose();
			Indices.Dispose();
			live.Clear();
			freedMeshes.Clear();
			freedVertices.Clear();
		}
	}
}
