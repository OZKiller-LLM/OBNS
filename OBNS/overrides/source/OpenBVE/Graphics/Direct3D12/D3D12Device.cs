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

// OBNS: not an upstream file. The Direct3D 12 world layer's device (OBNS/DIRECT3D.md): made on the GPU OpenGL draws on (by
// LUID), with its queue, and one fence shared with OpenGL (GL_EXT_semaphore_win32) ordering the two: OpenGL signals that it
// is done with the images, Direct3D's queue waits for that, draws, and signals; OpenGL waits for that before reading them.
// The images are Direct3D's, shared with OpenGL as Vulkan's are (GL_EXT_memory_object_win32).

using System;
using System.Runtime.InteropServices;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using Dx = OpenBve.Graphics.Direct3D11.Dx;

namespace OpenBve.Graphics.Direct3D12
{
	internal sealed unsafe class D3D12Device : IDisposable
	{
		internal IntPtr Device { get; private set; }
		internal IntPtr Queue { get; private set; }
		internal string Description { get; private set; }

		/// <summary>The fence shared with OpenGL, OpenGL's semaphore for it, and the last value either signalled.</summary>
		private IntPtr exchange;
		private int glExchange;
		private ulong exchangeValue;
		private IntPtr waitEvent;

		[DllImport("kernel32.dll")]
		private static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string name);

		[DllImport("kernel32.dll")]
		private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		private delegate void GetUnsignedBytev(uint target, byte* data);

		private D3D12Device()
		{
		}

		/// <summary>The device on OpenGL's GPU, with the fence shared; null, with the reason, if it cannot be.</summary>
		internal static D3D12Device Create(out string reason)
		{
			D3D12Device device = new D3D12Device();
			try
			{
				reason = device.Initialize();
			}
			catch (DllNotFoundException)
			{
				reason = "no Direct3D 12 (d3d12.dll)";
			}
			catch (EntryPointNotFoundException)
			{
				reason = "no Direct3D 12 (d3d12.dll is too old)";
			}
			catch (Exception ex)
			{
				reason = ex.Message;
			}

			if (reason != null)
			{
				device.Dispose();
				return null;
			}

			return device;
		}

		private static bool HasExtension(string name)
		{
			int count = GL.GetInteger(GetPName.NumExtensions);
			for (int i = 0; i < count; i++)
			{
				if (GL.GetString(StringNameIndexed.Extensions, i) == name)
				{
					return true;
				}
			}

			return false;
		}

		private string Initialize()
		{
			if (Environment.OSVersion.Platform != PlatformID.Win32NT)
			{
				return "Direct3D is Windows' only";
			}

			D12.CheckLayouts();
			foreach (string extension in new[] { "GL_EXT_memory_object_win32", "GL_EXT_semaphore_win32" })
			{
				if (!HasExtension(extension))
				{
					return "OpenGL has no " + extension;
				}
			}

			// OpenGL's adapter, by LUID (GL_EXT_memory_object_win32): images can only be shared on the same GPU.
			IntPtr address = ((IGraphicsContextInternal)GraphicsContext.CurrentContext).GetAddress("glGetUnsignedBytevEXT");
			if (address == IntPtr.Zero)
			{
				return "OpenGL has no glGetUnsignedBytevEXT";
			}

			byte[] luid = new byte[8];
			fixed (byte* data = luid)
			{
				const uint deviceLuid = 0x9599; // GL_DEVICE_LUID_EXT
				((GetUnsignedBytev)Marshal.GetDelegateForFunctionPointer(address, typeof(GetUnsignedBytev)))(deviceLuid, data);
			}

			while (GL.GetError() != ErrorCode.NoError)
			{
			}

			IntPtr factory = Dx.Factory();
			IntPtr chosen = IntPtr.Zero;
			string name = null;
			try
			{
				for (uint i = 0; Dx.EnumAdapters1(factory, i, out IntPtr adapter); i++)
				{
					Dx.AdapterDesc1 desc = Dx.GetDesc1(adapter);
					bool software = (desc.Flags & 2) != 0; // DXGI_ADAPTER_FLAG_SOFTWARE
					if (chosen == IntPtr.Zero && !software && BitConverter.ToUInt32(luid, 0) == desc.LuidLow && BitConverter.ToInt32(luid, 4) == desc.LuidHigh)
					{
						chosen = adapter;
						name = Dx.Name(desc).TrimEnd('\0');
					}
					else
					{
						Dx.Release(adapter);
					}
				}
			}
			finally
			{
				Dx.Release(factory);
			}

			if (chosen == IntPtr.Zero)
			{
				return "no Direct3D adapter is the GPU OpenGL is on";
			}

			int hr = D12.CreateDevice(chosen, out IntPtr device);
			Dx.Release(chosen);
			if (hr < 0)
			{
				return "D3D12CreateDevice failed (0x" + hr.ToString("X8") + ") on " + name;
			}

			Device = device;
			Description = name + ", feature level 11.0 or later";
			Queue = D12.CreateCommandQueue(Device);
			exchange = D12.CreateFence(Device, true);
			IntPtr handle = D12.CreateSharedHandle(Device, exchange);
			GL.Ext.GenSemaphores(1, out glExchange);
			GL.Ext.ImportSemaphoreWin32Handle(glExchange, ExternalHandleType.HandleTypeD3D12FenceExt, handle);
			ErrorCode error = GL.GetError();
			CloseHandle(handle);
			if (error != ErrorCode.NoError)
			{
				return "OpenGL could not import the Direct3D 12 fence (" + error + ")";
			}

			waitEvent = CreateEventW(IntPtr.Zero, false, false, null);
			return null;
		}

		// --- ordering with OpenGL, and the CPU ---

		/// <summary>OpenGL signals the shared fence after what it has queued (it is done with the images); the value signalled.</summary>
		internal ulong SignalFromGL()
		{
			ulong value = ++exchangeValue;
			GL.Ext.SemaphoreParameter((uint)glExchange, SemaphoreParameterName.D3D12FenceValueExt, ref value);
			GL.Ext.SignalSemaphore(glExchange, 0, (int[])null, 0, (int[])null, (TextureLayout[])null);
			GL.Flush();
			return value;
		}

		/// <summary>OpenGL waits (on the GPU) for the shared fence to reach a value before what it queues next.</summary>
		internal void WaitInGL(ulong value)
		{
			GL.Ext.SemaphoreParameter((uint)glExchange, SemaphoreParameterName.D3D12FenceValueExt, ref value);
			GL.Ext.WaitSemaphore(glExchange, 0, (int[])null, 0, (int[])null, (TextureLayout[])null);
		}

		/// <summary>Direct3D's queue waits for a value of the shared fence (OpenGL's signal).</summary>
		internal void QueueWait(ulong value) => D12.Wait(Queue, exchange, value);

		/// <summary>Direct3D's queue signals the shared fence after what it has been given; the value signalled.</summary>
		internal ulong QueueSignal()
		{
			ulong value = ++exchangeValue;
			D12.Signal(Queue, exchange, value);
			return value;
		}

		/// <summary>The CPU waits until the shared fence has reached a value.</summary>
		internal void WaitFor(ulong value) => WaitFor(exchange, value);

		/// <summary>The CPU waits until a fence has reached a value.</summary>
		internal void WaitFor(IntPtr fence, ulong value)
		{
			if (D12.CompletedValue(fence) >= value)
			{
				return;
			}

			D12.SetEventOnCompletion(fence, value, waitEvent);
			if (WaitForSingleObject(waitEvent, 10000) != 0)
			{
				int removed = D12.GetDeviceRemovedReason(Device);
				throw new InvalidOperationException("Direct3D 12 did not finish its work in 10 s" + (removed < 0 ? " (device removed, 0x" + removed.ToString("X8") + ")" : ""));
			}
		}

		/// <summary>Everything given to the queue is done (before images or buffers the frames in flight use are replaced).</summary>
		internal void WaitIdle() => WaitFor(QueueSignal());

		// --- images shared with OpenGL ---

		/// <summary>A 2D image Direct3D draws into and OpenGL reads, in the common state.</summary>
		internal IntPtr CreateSharedImage(uint width, uint height, uint format)
		{
			D12.ResourceDesc desc = new D12.ResourceDesc { Dimension = D12.DimensionTexture2D, Width = width, Height = height, DepthOrArraySize = 1, MipLevels = 1, Format = format, SampleCount = 1, Flags = D12.ResourceAllowRenderTarget };
			return D12.CreateCommittedResource(Device, D12.HeapDefault, D12.HeapFlagShared, desc, D12.StateCommon);
		}

		/// <summary>OpenGL's texture (and memory object) for a shared image, in OpenGL's format for it.</summary>
		internal int ImportImage(IntPtr image, uint width, uint height, uint format, SizedInternalFormat glFormat, out int memory)
		{
			D12.ResourceDesc desc = new D12.ResourceDesc { Dimension = D12.DimensionTexture2D, Width = width, Height = height, DepthOrArraySize = 1, MipLevels = 1, Format = format, SampleCount = 1, Flags = D12.ResourceAllowRenderTarget };
			ulong size = D12.GetResourceAllocationInfo(Device, desc).SizeInBytes;
			IntPtr handle = D12.CreateSharedHandle(Device, image);
			GL.Ext.CreateMemoryObjects(1, out memory);
			int dedicated = 1;
			GL.Ext.MemoryObjectParameter(memory, MemoryObjectParameterName.DedicatedMemoryObjectExt, ref dedicated);
			GL.Ext.ImportMemoryWin32Handle(memory, (long)size, ExternalHandleType.HandleTypeD3D12ResourceExt, handle);
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, texture);
			GL.Ext.TexStorageMem2D(TextureTarget.Texture2D, 1, (ExtMemoryObject)glFormat, (int)width, (int)height, memory, 0);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			// The handle is not taken over by the import: closed once OpenGL has done it (GetError waits for the driver).
			ErrorCode error = GL.GetError();
			CloseHandle(handle);
			if (error != ErrorCode.NoError)
			{
				GL.DeleteTexture(texture);
				GL.Ext.DeleteMemoryObjects(1, ref memory);
				throw new InvalidOperationException("OpenGL could not import a Direct3D 12 image (" + error + ")");
			}

			return texture;
		}

		/// <summary>
		/// Checks the whole path once: Direct3D clears a shared image and signals, OpenGL waits and reads it back. Null if
		/// the colour arrived, else what went wrong.
		/// </summary>
		internal string SelfTest()
		{
			const uint size = 16;
			IntPtr image = IntPtr.Zero, heap = IntPtr.Zero, allocator = IntPtr.Zero, list = IntPtr.Zero;
			int glTexture = 0, memory = 0;
			try
			{
				image = CreateSharedImage(size, size, Dx.FormatR8G8B8A8Unorm);
				heap = D12.CreateDescriptorHeap(Device, D12.DescriptorHeapRenderTargets, 1, false);
				UIntPtr view = D12.CpuStart(heap);
				D12.CreateRenderTargetView(Device, image, view);
				glTexture = ImportImage(image, size, size, Dx.FormatR8G8B8A8Unorm, SizedInternalFormat.Rgba8, out memory);
				allocator = D12.CreateCommandAllocator(Device);
				list = D12.CreateCommandList(Device, allocator);
				D12.ResourceBarrier* barrier = stackalloc D12.ResourceBarrier[1];
				barrier[0] = D12.Transition(image, D12.StateCommon, D12.StateRenderTarget);
				D12.ResourceBarriers(list, barrier, 1);
				D12.ClearRenderTargetView(list, view, 0.25f, 0.5f, 0.75f, 1.0f);
				barrier[0] = D12.Transition(image, D12.StateRenderTarget, D12.StateCommon);
				D12.ResourceBarriers(list, barrier, 1);
				D12.Close(list);
				QueueWait(SignalFromGL());
				D12.ExecuteCommandList(Queue, list);
				WaitInGL(QueueSignal());
				byte[] pixels = new byte[size * size * 4];
				GL.BindTexture(TextureTarget.Texture2D, glTexture);
				GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
				GL.BindTexture(TextureTarget.Texture2D, 0);
				ErrorCode error = GL.GetError();
				WaitIdle();
				bool right = Math.Abs(pixels[0] - 64) <= 1 && Math.Abs(pixels[1] - 128) <= 1 && Math.Abs(pixels[2] - 191) <= 1 && pixels[3] == 255;
				return right && error == ErrorCode.NoError ? null : "the colour Direct3D 12 cleared to read back as " + pixels[0] + ", " + pixels[1] + ", " + pixels[2] + ", " + pixels[3] + " (OpenGL error " + error + ")";
			}
			catch (Exception ex)
			{
				return ex.Message;
			}
			finally
			{
				if (glTexture != 0) GL.DeleteTexture(glTexture);
				if (memory != 0) GL.Ext.DeleteMemoryObjects(1, ref memory);
				D12.Release(list);
				D12.Release(allocator);
				D12.Release(heap);
				D12.Release(image);
			}
		}

		public void Dispose()
		{
			if (Queue != IntPtr.Zero && exchange != IntPtr.Zero)
			{
				try
				{
					WaitIdle();
				}
				catch (Exception)
				{
					// A removed device: its work is gone anyway.
				}
			}

			if (glExchange != 0)
			{
				GL.Ext.DeleteSemaphores(1, ref glExchange);
				glExchange = 0;
			}

			if (waitEvent != IntPtr.Zero)
			{
				CloseHandle(waitEvent);
				waitEvent = IntPtr.Zero;
			}

			D12.Release(exchange);
			D12.Release(Queue);
			D12.Release(Device);
			exchange = Queue = Device = IntPtr.Zero;
		}
	}
}
