// OBNS 1.32 (Switch): a copy of upstream's LibRender2/openGL/IndexBufferObject.cs (OpenBVE 1.14.1.2), used in its place in the
// Switch build (NX/Switch.targets): with NativeMeshes, no GL buffer, and the indices let go once in the device's memory.
// On an upstream update: copy the file again and reapply the change.
using System;
using OpenTK.Graphics.OpenGL;

namespace LibRender2
{
	/// <summary>
	/// Class representing the abstract IBO
	/// </summary>
	public abstract class IndexBufferObject : IDisposable
	{
		/// <summary>The openGL buffer name</summary>
		private int handle;
		protected readonly BufferUsageHint drawType;
		private bool disposed;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="DrawType">The hint representing how the object is to be used and therefore guides OpenGL's optimization of the object</param>
		protected IndexBufferObject(BufferUsageHint DrawType)
		{
			// OBNS: the GL name made when first bound (the GL way only): the VAO decides which way its buffers go.
			drawType = DrawType;
		}

		/// <summary>OBNS (Switch): the indices into the device's memory for array <paramref name="array"/>; no copy kept here.</summary>
		internal abstract void Upload(INativeMeshes native, int array);

		/// <summary>
		/// Binds the IBO/EBO ready for use
		/// </summary>
		internal void Bind()
		{
			if (handle == 0)
			{
				GL.GenBuffers(1, out handle);
			}

			GL.BindBuffer(BufferTarget.ElementArrayBuffer, handle);
		}

		/// <summary>Copies the Indices into the IBO/EBO</summary>
		/// <remarks>Must be called before attempting to use the IBO/ EBO</remarks>
		internal abstract void BufferData();

		/// <summary>
		/// Draws the object
		/// </summary>
		/// <param name="DrawMode">Specifies the primitive or primitives that will be created from vertices</param>
		internal abstract void Draw(PrimitiveType DrawMode);

		/// <summary>
		/// Draws the object
		/// </summary>
		/// <param name="DrawMode">Specifies the primitive or primitives that will be created from vertices</param>
		/// <param name="Start">Start position of vertex index</param>
		/// <param name="Count">Number of vertex indices to use</param>
		internal abstract void Draw(PrimitiveType DrawMode, int Start, int Count);

		/// <summary>
		/// Dispose method to clean up the IBO/EBO releases the OpenGL Buffer
		/// </summary>
		public void Dispose()
		{
			if (disposed)
			{
				return;
			}

			if (handle != 0)
			{
				GL.DeleteBuffer(handle);
			}

			GC.SuppressFinalize(this);
			disposed = true;
		}

		~IndexBufferObject()
		{
			if (disposed || handle == 0)
			{
				return;
			}

			lock (BaseRenderer.iboToDelete)
			{
				BaseRenderer.iboToDelete.Add(handle);
			}
		}
	}

	public abstract class IndexBufferObject<T> : IndexBufferObject where T : struct
	{
		// Not readonly: dropped once uploaded to the device's own memory (the Switch); IndexCount stays.
		protected T[] indexData;

		/// <summary>The number of indices.</summary>
		protected readonly int IndexCount;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="IndexData">An int array of vertex indices that make the object</param>
		/// <param name="DrawType">The hint representing how the object is to be used and therefore guides OpenGL's optimization of the object</param>
		protected IndexBufferObject(T[] IndexData, BufferUsageHint DrawType) : base(DrawType)
		{
			indexData = IndexData;
			IndexCount = IndexData.Length;
		}

		internal override void Upload(INativeMeshes native, int array)
		{
			if (indexData == null)
			{
				return;
			}

			native.Indices(array, System.Runtime.InteropServices.MemoryMarshal.AsBytes(indexData.AsSpan()), System.Runtime.CompilerServices.Unsafe.SizeOf<T>() == 4);
			indexData = null;
		}
	}

	public class IndexBufferObjectUS : IndexBufferObject<ushort>
	{
		public IndexBufferObjectUS(ushort[] IndexData, BufferUsageHint DrawType) : base(IndexData, DrawType)
		{
		}

		internal override void BufferData()
		{
			GL.BufferData(BufferTarget.ElementArrayBuffer, indexData.Length * sizeof(ushort), indexData, drawType);
		}

		internal override void Draw(PrimitiveType DrawMode)
		{
			GL.DrawElements(DrawMode, IndexCount, DrawElementsType.UnsignedShort, 0);
		}

		internal override void Draw(PrimitiveType DrawMode, int Start, int Count)
		{
			GL.DrawElements(DrawMode, Count, DrawElementsType.UnsignedShort, Start * sizeof(ushort));
		}
	}

	public class IndexBufferObjectUI : IndexBufferObject<uint>
	{
		public IndexBufferObjectUI(uint[] IndexData, BufferUsageHint DrawType) : base(IndexData, DrawType)
		{
		}

		internal override void BufferData()
		{
			GL.BufferData(BufferTarget.ElementArrayBuffer, indexData.Length * sizeof(uint), indexData, drawType);
		}

		internal override void Draw(PrimitiveType DrawMode)
		{
			GL.DrawElements(DrawMode, IndexCount, DrawElementsType.UnsignedInt, 0);
		}

		internal override void Draw(PrimitiveType DrawMode, int Start, int Count)
		{
			GL.DrawElements(DrawMode, Count, DrawElementsType.UnsignedInt, Start * sizeof(uint));
		}
	}
}
