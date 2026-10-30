// OBNS: not an upstream file. The Direct3D 11 world layer's textures (OBNS/DIRECT3D.md): the images of WorldTextures'
// copies, each a Direct3D texture with its view (bound one per draw), its mipmap levels made by Direct3D.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OpenBve.Graphics.Direct3D11
{
	internal sealed unsafe class D3D11Textures : WorldTextures
	{
		internal sealed class D3D11Entry : Entry
		{
			internal IntPtr Texture, View;
		}

		private readonly D3D11Device device;

		/// <summary>Each texture's view by its index (null where there is none).</summary>
		private IntPtr[] views = new IntPtr[1024];

		// Pixels written later: into one block of memory (grown as needed), on every core when the uploads are sent, then
		// handed to Direct3D.
		private sealed class Pending
		{
			internal D3D11Entry Entry;
			internal Pixels Pixels;
			internal long Offset;
		}

		private readonly List<Pending> pending = new List<Pending>();
		private IntPtr staging;
		private long stagingSize, stagingUsed;

		/// <summary>Bytes of pixels written later after which they are sent at once (loading makes many).</summary>
		private const long FlushThreshold = 128L << 20;

		private long imageBytes;

		internal D3D11Textures(D3D11Device device) : base("Direct3D 11", "OBNS_D3D11")
		{
			this.device = device;
			MakeWhite();
		}

		/// <summary>A texture's view (the white one's where there is none).</summary>
		internal IntPtr View(int index) => index >= 0 && index < views.Length && views[index] != IntPtr.Zero ? views[index] : views[White];

		protected override Entry NewRecord() => new D3D11Entry();

		protected override void MakeImage(Entry record)
		{
			D3D11Entry entry = (D3D11Entry)record;
			bool mipmapped = entry.Levels > 1;
			Dx.Texture2DDesc desc = new Dx.Texture2DDesc
			{
				Width = entry.Width,
				Height = entry.Height,
				MipLevels = entry.Levels,
				ArraySize = 1,
				Format = Dx.FormatR8G8B8A8Unorm,
				SampleCount = 1,
				Usage = Dx.UsageDefault,
				// Direct3D makes the levels by drawing them: a render target.
				BindFlags = Dx.BindShaderResource | (mipmapped ? Dx.BindRenderTarget : 0),
				MiscFlags = mipmapped ? Dx.MiscGenerateMips : 0
			};
			entry.Texture = Dx.CreateTexture2D(device.Device, desc);
			entry.View = Dx.CreateShaderResourceView(device.Device, entry.Texture);
			imageBytes += (long)entry.Width * entry.Height * 4 * (mipmapped ? 4 : 3) / 3;
			if (logTextures)
			{
				Program.FileSystem.AppendToLogFile("OBNS Direct3D 11 texture " + entry.Index + ": " + entry.Width + "x" + entry.Height + ", " + entry.Levels + " levels");
			}
		}

		protected override void Write(Entry record, Pixels pixels, bool later)
		{
			D3D11Entry entry = (D3D11Entry)record;
			long bytes = (long)pixels.Width * pixels.Height * 4;
			if (!later)
			{
				// Taken now (upstream may reuse an animated frame's bytes): written and sent at once.
				IntPtr own = Marshal.AllocHGlobal(new IntPtr(bytes));
				try
				{
					pixels.Fill((byte*)own, 0, (int)(pixels.Width * pixels.Height));
					Send(entry, (byte*)own);
				}
				finally
				{
					Marshal.FreeHGlobal(own);
				}

				return;
			}

			if (stagingUsed + bytes > stagingSize)
			{
				Flush();
				if (bytes > stagingSize)
				{
					if (staging != IntPtr.Zero)
					{
						Marshal.FreeHGlobal(staging);
					}

					stagingSize = Math.Max(bytes, 64L << 20);
					staging = Marshal.AllocHGlobal(new IntPtr(stagingSize));
				}
			}

			pending.Add(new Pending { Entry = entry, Pixels = pixels, Offset = stagingUsed });
			stagingUsed += (bytes + 15) / 16 * 16;
		}

		/// <summary>The first level from RGBA pixels, and the others made from it.</summary>
		private void Send(D3D11Entry entry, byte* rgba)
		{
			Dx.UpdateSubresource(device.Context, entry.Texture, 0, rgba, entry.Width * 4);
			if (entry.Levels > 1)
			{
				Dx.GenerateMips(device.Context, entry.View);
			}
		}

		protected override void Published(Entry record)
		{
			D3D11Entry entry = (D3D11Entry)record;
			if (entry.Index >= views.Length)
			{
				Array.Resize(ref views, Math.Max(entry.Index + 1, views.Length * 2));
			}

			views[entry.Index] = entry.View;
		}

		protected override void Unpublished(Entry entry)
		{
			if (entry.Index >= 0 && entry.Index < views.Length)
			{
				views[entry.Index] = IntPtr.Zero;
			}
		}

		protected override void Uploaded()
		{
			if (stagingUsed >= FlushThreshold)
			{
				Flush();
			}
		}

		/// <summary>The pixels written later: written on every core, then sent.</summary>
		protected override void Flush()
		{
			if (pending.Count == 0)
			{
				return;
			}

			List<KeyValuePair<Pending, int>> pieces = new List<KeyValuePair<Pending, int>>();
			foreach (Pending item in pending)
			{
				int count = (int)(item.Pixels.Width * item.Pixels.Height);
				for (int first = 0; first < count; first += FillPiece)
				{
					pieces.Add(new KeyValuePair<Pending, int>(item, first));
				}
			}

			byte* block = (byte*)staging;
			Parallel.For(0, pieces.Count, i =>
			{
				Pending item = pieces[i].Key;
				int first = pieces[i].Value;
				int count = (int)(item.Pixels.Width * item.Pixels.Height);
				item.Pixels.Fill(block + item.Offset, first, Math.Min(FillPiece, count - first));
			});
			foreach (Pending item in pending)
			{
				if (item.Entry.Texture != IntPtr.Zero)
				{
					Send(item.Entry, block + item.Offset);
				}
			}

			pending.Clear();
			stagingUsed = 0;
		}

		/// <summary>Called each frame before the draws: the uploads made since the last are sent.</summary>
		internal void Send() => Flush();

		protected override void Destroy(Entry record)
		{
			if (!(record is D3D11Entry entry))
			{
				return;
			}

			if (entry.Index >= 0 && entry.Index < views.Length && views[entry.Index] == entry.View)
			{
				views[entry.Index] = IntPtr.Zero;
			}

			Dx.Release(entry.View);
			Dx.Release(entry.Texture);
			if (entry.Texture != IntPtr.Zero)
			{
				imageBytes -= (long)entry.Width * entry.Height * 4 * (entry.Levels > 1 ? 4 : 3) / 3;
			}

			entry.View = entry.Texture = IntPtr.Zero;
		}

		protected override string DescribeStorage() => "Direct3D textures " + (imageBytes >> 20) + " MB";

		public override void Dispose()
		{
			pending.Clear();
			base.Dispose();
			if (staging != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(staging);
				staging = IntPtr.Zero;
			}
		}
	}
}
