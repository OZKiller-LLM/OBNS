# Makes the app's launcher icons from openbve_logo_classic.png (the classic OpenBVE logo, 250 x 250).
# Run from anywhere: powershell -ExecutionPolicy Bypass -File android\artwork\make_icons.ps1
#
# The logo carries its own rounded frame: 3 px of transparency, a 5 px bevel, then the artwork,
# whose gradient square spans x/y 8-241 with ~31 px corners. Two kinds of icon come out:
#
#  * ic_launcher (Android 7): the logo as it is, frame, rounded corners and all, at 48 dp.
#  * ic_launcher_background (Android 8+, adaptive): the launcher cuts the icon to its own shape
#    (circle, squircle, rounded square), so the logo's frame and corners would show as a second,
#    smaller outline inside it. Instead the artwork alone fills the 66 dp safe zone, and
#    beyond it, out to the full 108 dp layer, carries on with its nearest edge colour (the corners
#    rounded off along the artwork's own curve), so whatever shape the launcher cuts, it cuts
#    artwork - never the frame or a transparent corner.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;

public static class IconMaker
{
	static int w, h;
	static float[] a, r, g, b; // premultiplied

	public static void Load(string path)
	{
		using (Bitmap source = new Bitmap(path))
		{
			w = source.Width;
			h = source.Height;
			a = new float[w * h]; r = new float[w * h]; g = new float[w * h]; b = new float[w * h];
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					Color c = source.GetPixel(x, y);
					int i = y * w + x;
					float alpha = c.A / 255f;
					a[i] = alpha; r[i] = c.R * alpha; g[i] = c.G * alpha; b[i] = c.B * alpha;
				}
			}
		}
	}

	// Bilinear, premultiplied; coordinates in pixels, pixel centres at +0.5.
	static void Sample(double u, double v, double[] sum)
	{
		u -= 0.5; v -= 0.5;
		int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
		double fx = u - x0, fy = v - y0;
		for (int j = 0; j < 2; j++)
		{
			for (int k = 0; k < 2; k++)
			{
				int x = Math.Max(0, Math.Min(w - 1, x0 + k)), y = Math.Max(0, Math.Min(h - 1, y0 + j));
				double weight = (k == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
				int i = y * w + x;
				sum[0] += a[i] * weight; sum[1] += r[i] * weight; sum[2] += g[i] * weight; sum[3] += b[i] * weight;
			}
		}
	}

	static Bitmap Render(int size, Func<double, double, double[]> map)
	{
		const int n = 4; // n x n samples a pixel, enough for scaling either way
		Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				double[] sum = new double[4];
				for (int j = 0; j < n; j++)
				{
					for (int k = 0; k < n; k++)
					{
						double[] uv = map((x + (k + 0.5) / n) / size, (y + (j + 0.5) / n) / size);
						Sample(uv[0], uv[1], sum);
					}
				}

				double alpha = sum[0] / (n * n);
				int A = (int)Math.Round(alpha * 255);
				Func<double, int> channel = s => alpha <= 0 ? 0 : Math.Max(0, Math.Min(255, (int)Math.Round(s / (n * n) / alpha)));
				result.SetPixel(x, y, Color.FromArgb(Math.Max(0, Math.Min(255, A)), channel(sum[1]), channel(sum[2]), channel(sum[3])));
			}
		}

		return result;
	}

	/// <summary>The whole logo, frame included (legacy icon).</summary>
	public static void Legacy(string path, int size)
	{
		using (Bitmap bitmap = Render(size, (s, t) => new[] { s * w, t * h }))
		{
			bitmap.Save(path, ImageFormat.Png);
		}
	}

	/// <summary>
	/// The artwork [lo, hi) filling the middle 66/108 of the square, carried on beyond by its
	/// nearest edge colour; clamping stays a little inside the edge, clear of the bevel's shading.
	/// </summary>
	public static void Adaptive(string path, int size, double lo, double hi, double radius, double inset)
	{
		// Android's 66 dp safe zone, so a round mask keeps the whole ring.
		double view = 66.0 / 108.0, offset = (1 - view) / 2;
		double cLo = lo + inset, cHi = hi - inset, cr = radius - inset;
		using (Bitmap bitmap = Render(size, (s, t) =>
		{
			double u = lo + (s - offset) / view * (hi - lo);
			double v = lo + (t - offset) / view * (hi - lo);
			u = Math.Max(cLo, Math.Min(cHi, u));
			v = Math.Max(cLo, Math.Min(cHi, v));
			// In a corner, onto the artwork's own curve.
			double cx = u < cLo + cr ? cLo + cr : u > cHi - cr ? cHi - cr : u;
			double cy = v < cLo + cr ? cLo + cr : v > cHi - cr ? cHi - cr : v;
			double dx = u - cx, dy = v - cy, d = Math.Sqrt(dx * dx + dy * dy);
			if (d > cr)
			{
				u = cx + dx / d * cr;
				v = cy + dy / d * cr;
			}

			return new[] { u, v };
		}))
		{
			bitmap.Save(path, ImageFormat.Png);
		}
	}
}
'@

$artwork = Split-Path -Parent $MyInvocation.MyCommand.Path
$resources = Join-Path (Split-Path -Parent $artwork) 'OpenBve.Android\Resources'
[IconMaker]::Load((Join-Path $artwork 'openbve_logo_classic.png'))
$densities = [ordered]@{ 'mdpi' = 1.0; 'hdpi' = 1.5; 'xhdpi' = 2.0; 'xxhdpi' = 3.0; 'xxxhdpi' = 4.0 }
foreach ($density in $densities.Keys) {
	$folder = Join-Path $resources "mipmap-$density"
	New-Item -ItemType Directory -Force $folder | Out-Null
	$scale = $densities[$density]
	[IconMaker]::Legacy((Join-Path $folder 'ic_launcher.png'), [int](48 * $scale))
	[IconMaker]::Adaptive((Join-Path $folder 'ic_launcher_background.png'), [int](108 * $scale), 8.0, 242.0, 31.0, 2.0)
	"$density done"
}
