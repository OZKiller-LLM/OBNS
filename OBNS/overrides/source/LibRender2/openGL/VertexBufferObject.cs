// OBNS: a copy of upstream's source/LibRender2/openGL/VertexBufferObject.cs (OpenBVE 1.14.1.2), laid over the desktop
// build's mirror (OBNS/build-desktop.ps1): vertex buffers in memory shared with the Vulkan world layer when it is in use
// (OBNS/VULKAN.md). On an upstream update: copy the file again and reapply the parts marked OBNS.
using System;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace LibRender2
{
	/// <summary>
	/// OBNS: device memory shared with the Vulkan world layer (OBNS/VULKAN.md): a range of a block that OpenGL has
	/// imported as a memory object and Vulkan reads the same data from.
	/// </summary>
	public sealed class ObnsSharedMemory
	{
		/// <summary>The block's OpenGL memory object.</summary>
		public int GLMemory;
		/// <summary>The range's offset in the block.</summary>
		public ulong Offset;
		/// <summary>The Vulkan world layer's record of the range.</summary>
		public object Owner;
		/// <summary>The OpenGL buffer whose storage it is (buffers only).</summary>
		public int GLBuffer;
		/// <summary>
		/// Not shared: OpenGL keeps its own storage, and the world layer a copy, written through WriteVertices as well
		/// (the Direct3D 11 world layer, OBNS/DIRECT3D.md).
		/// </summary>
		public bool Mirror;
	}

	/// <summary>
	/// OBNS: where vertex buffers get their storage when the Vulkan world layer is in use, so the GPU has one copy of
	/// each that both APIs use, and what OpenGL writes into it, which it hands over to Vulkan each frame; and what the
	/// Vulkan world layer, which keeps its own copy of textures, is told of them. Unset (OpenGL alone), nothing changes.
	/// </summary>
	public static class ObnsSharing
	{
		/// <summary>Storage for a vertex buffer of so many bytes, or null for OpenGL's own.</summary>
		public static Func<long, ObnsSharedMemory> AllocateBuffer;

		/// <summary>Called when a buffer's storage is let go of.</summary>
		public static Action<ObnsSharedMemory> FreeBuffer;

		/// <summary>Called with an OpenGL buffer written into shared storage.</summary>
		public static Action<int> BufferWritten;

		/// <summary>
		/// Writes vertices into a buffer's shared storage the sharer's way (Vulkan: a copy on its own queue, ordered with its
		/// draws), the offset in bytes; false if it does not, and OpenGL writes them. Animated objects and scrolling
		/// displays rewrite theirs every frame: through OpenGL each write made the two APIs wait on each other (on the
		/// Tuen Ma Line, frames of 0.1 to 1.5 s near stations).
		/// </summary>
		public static Func<ObnsSharedMemory, LibRenderVertex[], long, bool> WriteVertices;

		/// <summary>
		/// Called with an animated texture's pixels when OpenGL has uploaded them (its first frame, and each new one): the
		/// handle, the decoded texture, its pixels in its own format, and whether it is used as opaque.
		/// </summary>
		public static Action<OpenBveApi.Textures.Texture, OpenBveApi.Textures.Texture, byte[], bool> AnimatedFrame;

		/// <summary>Called when a texture is about to be unloaded (its OpenGL textures deleted, its handle made again).</summary>
		public static Action<OpenBveApi.Textures.Texture> TextureUnloading;

		/// <summary>Reports an OpenGL error after making shared storage (the first few), for the log.</summary>
		public static Action<string> Error;

		private static int errors;

		/// <summary>Reports OpenGL's errors, if any, after a call on shared storage (or, before one, errors left by others).</summary>
		public static void CheckError(string call)
		{
			for (ErrorCode error = GL.GetError(); error != ErrorCode.NoError; error = GL.GetError())
			{
				if (errors < 20)
				{
					errors++;
					Error?.Invoke(call + ": " + error);
				}
			}
		}
	}

	/// <summary>
	/// Class that represents an OpenGL/OpenTK vertex buffer object 
	/// </summary>
	public class VertexBufferObject : IDisposable
	{
		private readonly int handle;
		private readonly LibRenderVertex[] vertexData;
		private readonly BufferUsageHint drawType;
		private bool disposed;
		private readonly int vertexSize;

		/// <summary>OBNS: the storage shared with the Vulkan world layer, if it has some.</summary>
		public ObnsSharedMemory ObnsShared;

		/// <summary>OBNS: the OpenGL buffer.</summary>
		public int ObnsHandle => handle;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="VertexData">An array containing the actual vertex coordinates and texture coordinates as X, Y, Z, U, V</param>
		/// <param name="DrawType">The hint representing how the object is to be used and therefore guides OpenGL's optimization of the object</param>
		public VertexBufferObject(LibRenderVertex[] VertexData, BufferUsageHint DrawType)
		{
			GL.GenBuffers(1, out handle);
			vertexData = VertexData;
			drawType = DrawType;
			/*
			 * Getting the size of the vertex type using marshal is slow, so cache it here
			 * This allows us to meddle with the vertex contents without having to remember to update a const
			 */
			vertexSize = LibRenderVertex.SizeInBytes;
		}

		/// <summary>
		/// Binds the VBO ready for use
		/// </summary>
		internal void Bind()
		{
			GL.BindBuffer(BufferTarget.ArrayBuffer, handle);
		}

		/// <summary>Copies the VertexData into the the VBO</summary>
		/// <remarks>This method must be called before attempting to use the VBO</remarks>
		internal void BufferData()
		{
			// OBNS: in storage shared with the Vulkan world layer, when it gives some.
			long bytes = (long)vertexData.Length * vertexSize;
			if (bytes > 0 && ObnsShared == null && ObnsSharing.AllocateBuffer != null)
			{
				ObnsShared = ObnsSharing.AllocateBuffer(bytes);
				if (ObnsShared != null && ObnsShared.Mirror)
				{
					ObnsShared.GLBuffer = handle;
					GL.BufferData(BufferTarget.ArrayBuffer, new IntPtr(vertexData.Length * vertexSize), vertexData, drawType);
					ObnsSharing.WriteVertices?.Invoke(ObnsShared, vertexData, 0);
					return;
				}

				if (ObnsShared != null)
				{
					ObnsShared.GLBuffer = handle;
					ObnsSharing.CheckError("before glBufferStorageMemEXT (left by an earlier call)");
					GL.Ext.BufferStorageMem(BufferTargetArb.ArrayBuffer, new IntPtr(bytes), ObnsShared.GLMemory, (long)ObnsShared.Offset);
					ObnsSharing.CheckError("glBufferStorageMemEXT");
					ObnsWrite(vertexData, 0);
					return;
				}
			}

			GL.BufferData(BufferTarget.ArrayBuffer, new IntPtr(vertexData.Length * vertexSize), vertexData, drawType);
		}

		/// <summary>Updates the VertexData contained within the VBO</summary>
		internal void BufferSubData(LibRenderVertex[] VertexData, int Offset = 0)
		{
			if (ObnsShared != null && !ObnsShared.Mirror)
			{
				ObnsWrite(VertexData, Offset);
				return;
			}

			if (ObnsShared != null)
			{
				ObnsSharing.WriteVertices?.Invoke(ObnsShared, VertexData, (long)Offset * vertexSize);
			}

			GL.BufferSubData(BufferTarget.ArrayBuffer, new IntPtr(Offset * vertexSize), new IntPtr(VertexData.Length * vertexSize), VertexData);
		}

		/// <summary>
		/// OBNS: writes vertices into the shared storage through a temporary buffer and a copy on the GPU (storage from a
		/// memory object is not guaranteed to take BufferSubData); the buffer stays bound as the array buffer.
		/// </summary>
		private void ObnsWrite(LibRenderVertex[] data, int offset)
		{
			if (ObnsSharing.WriteVertices != null && ObnsSharing.WriteVertices(ObnsShared, data, (long)offset * vertexSize))
			{
				return;
			}

			IntPtr bytes = new IntPtr(data.Length * vertexSize);
			GL.GenBuffers(1, out int staging);
			GL.BindBuffer(BufferTarget.CopyReadBuffer, staging);
			GL.BufferData(BufferTarget.CopyReadBuffer, bytes, data, BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.CopyWriteBuffer, handle);
			GL.CopyBufferSubData(BufferTarget.CopyReadBuffer, BufferTarget.CopyWriteBuffer, IntPtr.Zero, new IntPtr(offset * vertexSize), bytes);
			GL.BindBuffer(BufferTarget.CopyReadBuffer, 0);
			GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
			GL.DeleteBuffer(staging);
			ObnsSharing.CheckError("writing a shared vertex buffer");
			ObnsSharing.BufferWritten?.Invoke(handle);
		}

		/// <summary>Enables a specific vertex attribute array</summary>
		internal void EnableAttribute(VertexLayout VertexLayout)
		{
			if (VertexLayout.Position >= 0)
			{
				GL.EnableVertexAttribArray(VertexLayout.Position);
			}

			if (VertexLayout.Normal >= 0)
			{
				GL.EnableVertexAttribArray(VertexLayout.Normal);
			}

			if (VertexLayout.UV >= 0)
			{
				GL.EnableVertexAttribArray(VertexLayout.UV);
			}

			if (VertexLayout.Color >= 0)
			{
				GL.EnableVertexAttribArray(VertexLayout.Color);
			}

			if (VertexLayout.MatrixChain >= 0)
			{
				GL.EnableVertexAttribArray(VertexLayout.MatrixChain);
			}
		}

		/// <summary>
		/// Sets the attribute ready for use in the VAO
		/// </summary>
		internal void SetAttribute(VertexLayout VertexLayout)
		{
			int offset = 0;
			if (VertexLayout.Position >= 0)
			{
				GL.VertexAttribPointer(VertexLayout.Position, 3, VertexAttribPointerType.Float, false, vertexSize, offset);
				offset += Vector3.SizeInBytes;
			}

			if (VertexLayout.Normal >= 0)
			{
				GL.VertexAttribPointer(VertexLayout.Normal, 3, VertexAttribPointerType.Float, false, vertexSize, offset);
				offset += Vector3.SizeInBytes;
			}

			if (VertexLayout.UV >= 0)
			{
				GL.VertexAttribPointer(VertexLayout.UV, 2, VertexAttribPointerType.Float, false, vertexSize, offset);
				offset += Vector2.SizeInBytes; //equivalent to API Vector2
			}

			if (VertexLayout.Color >= 0)
			{
				GL.VertexAttribPointer(VertexLayout.Color, 4, VertexAttribPointerType.Float, false, vertexSize, offset);
				offset += Vector4.SizeInBytes;
			}

			if (VertexLayout.MatrixChain >= 0)
			{
				// NOTE: Must use VertexAttribIPointer here, as VertexAttribPointer actually converts to float and normalizes.....
				GL.VertexAttribIPointer(VertexLayout.MatrixChain, 3, VertexAttribIntegerType.Int, vertexSize, (IntPtr)offset);
			}
		}

		/// <summary>
		/// Disables the attribute
		/// </summary>
		internal void DisableAttribute(VertexLayout VertexLayout)
		{
			if (VertexLayout.Position >= 0)
			{
				GL.DisableVertexAttribArray(VertexLayout.Position);
			}

			if (VertexLayout.Normal >= 0)
			{
				GL.DisableVertexAttribArray(VertexLayout.Normal);
			}

			if (VertexLayout.UV >= 0)
			{
				GL.DisableVertexAttribArray(VertexLayout.UV);
			}

			if (VertexLayout.Color >= 0)
			{
				GL.DisableVertexAttribArray(VertexLayout.Color);
			}

			if (VertexLayout.MatrixChain >= 0)
			{
				GL.DisableVertexAttribArray(VertexLayout.MatrixChain);
			}
		}

		/// <summary>
		/// Dispose method to clean up the VBO releases the OpenGL Buffer
		/// </summary>
		public void Dispose()
		{
			if (disposed)
			{
				return;
			}

			GL.DeleteBuffer(handle);
			// OBNS: the shared storage goes back (a buffer collected without being disposed keeps its range).
			if (ObnsShared != null)
			{
				ObnsSharing.FreeBuffer?.Invoke(ObnsShared);
				ObnsShared = null;
			}

			GC.SuppressFinalize(this);
			disposed = true;
		}

		~VertexBufferObject()
		{
			if (disposed)
			{
				return;
			}

			lock (BaseRenderer.vboToDelete)
			{
				BaseRenderer.vboToDelete.Add(handle);
			}
		}
	}
}
