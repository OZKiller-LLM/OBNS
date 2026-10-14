"""Makes Android/LibRender2.Android/TextureManager.Android.cs from upstream's
OpenBVE/source/LibRender2/Textures/TextureManager.cs (1.14.1.2).

Usage (from the repository root): python Android/UpstreamPatches/make_texturemanager_android.py

Upstream 1.14.1.2 decodes every texture when it is registered and keeps the decoded copy - in the
texture cache, and on the handle (Texture.DecodedTexture) - to reuse it at upload. On a phone that
is gigabytes for a heavy route (the Android port's 1.01 work brought it down to nothing), and the
Android host answers the registration-time decode of PNG/BMP/JPEG/GIF/TIFF with a 1x1 stand-in of
the right format (AndroidHost.TryAnswerFormatProbe), which upstream would then upload. So, as in
1.1, the texture cache holds each texture's transparency type only, and nothing decoded is kept or
reused between registration and upload. Each edit below must match exactly once; on an upstream
update, re-run this and check the edits still say what they should.
"""
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SOURCE = os.path.join(ROOT, "OpenBVE", "source", "LibRender2", "Textures", "TextureManager.cs")
TARGET = os.path.join(ROOT, "Android", "LibRender2.Android", "TextureManager.Android.cs")

HEADER = """// ANDROID PORT: a copy of upstream's LibRender2/Textures/TextureManager.cs (OpenBVE 1.14.1.2), compiled
// in its place (LibRender2.Android.csproj), made by Android/UpstreamPatches/make_texturemanager_android.py:
// the texture cache holds transparency types only, and no decoded texture is kept from registration
// to upload (see that script). Do not edit by hand: change the script and run it again.
"""

text = open(SOURCE, encoding="utf-8-sig").read().replace("\r\n", "\n")


def replace(old, new, count=1):
    global text
    found = text.count(old)
    if found != count:
        sys.exit("expected %d of:\n%s\nfound %d" % (count, old, found))
    text = text.replace(old, new)


def replace_between(start, end, new):
    """Replaces from the start text up to (not including) the end text, which must follow it."""
    global text
    i = text.find(start)
    if i < 0 or text.find(start, i + 1) >= 0:
        sys.exit("start not found exactly once:\n" + start)
    j = text.find(end, i + len(start))
    if j < 0:
        sys.exit("end not found after start:\n" + end)
    text = text[:i] + new + text[j:]


# 1. The cache: transparency types.
replace("""		/// <summary>Holds cached texture origins</summary>
		internal static Dictionary<TextureOrigin, Texture> textureCache = new Dictionary<TextureOrigin, Texture>();""",
"""		/// <summary>The transparency type of each texture classified so far, by origin.</summary>
		/// <remarks>
		/// ANDROID PORT: upstream caches the decoded texture, pixels included, to reuse it at upload;
		/// on a phone that kept every texture decoded (1.25 GB for one 9-car train). Only the type is
		/// cached, as the Android port's static batcher and texture classifier expect.
		/// </remarks>
		internal static Dictionary<TextureOrigin, TextureTransparencyType> textureCache = new Dictionary<TextureOrigin, TextureTransparencyType>();""")

replace_between("""		internal static bool TryGetCachedTexture(TextureOrigin origin, out Texture cached)""",
"""		/// <summary>The number of currently registered textures.</summary>""",
"""		/// <summary>A texture's cached transparency type, if classified (ANDROID PORT: types only).</summary>
		internal static bool TryGetCachedType(TextureOrigin origin, out TextureTransparencyType type)
		{
			lock (TextureLookupLock)
			{
				return textureCache.TryGetValue(origin, out type);
			}
		}

		/// <summary>Caches a texture's transparency type (ANDROID PORT: types only).</summary>
		internal static void StoreCachedType(TextureOrigin origin, TextureTransparencyType type)
		{
			if (origin == null) return;
			lock (TextureLookupLock)
			{
				textureCache[origin] = type;
			}
		}

		/// <summary>The lock guarding registration (ANDROID PORT: the Android host registers path textures itself, under it).</summary>
		internal static object RegistrationLock => TextureLookupLock;

""")

# 2. Registration: no pre-seeding of the cache with the decoded texture.
replace_between("""				/*
				 * Pre-seed the texture cache with the decoded texture (not the handle).""",
"""				/*
				 * Maintain the registration lookup table.""",
"""				// ANDROID PORT: the register-time decode is not kept (see textureCache).

""")

# 3. Animated textures: no reuse of a cached or register-time decode.
replace_between("""				texture = null;
				// Reuse register-time decode from textureCache where possible""",
"""				if (texture == null)
				{
					if (handle.Origin == null || !handle.Origin.GetTexture(out texture))""",
"""				texture = null;
				// ANDROID PORT: no cached or register-time decode to reuse (see textureCache).
""")

# 4. Still textures: the same.
replace_between("""				/*
				 * Reuse the register-time decode held by the texture cache where possible,""",
"""			if (texture == null)
			{
				if (handle.Origin == null)
				{
					handle.Ignore = true;
					return false;
				}""",
"""				// ANDROID PORT: no cached or register-time decode to reuse (see textureCache); the
				// handle's DecodedTexture may be the Android host's 1x1 format stand-in.
			if (texture == null && handle.Origin != null)
			{
				lock (TextureLookupLock)
				{
					if (animatedTextures.TryGetValue(handle.Origin, out Texture animatedTexture) && animatedTexture != null && animatedTexture.MultipleFrames)
					{
						texture = animatedTexture;
					}
				}
			}
""")

# 5. After upload: release the upload copy and anything the handle holds; the cache has no pixels.
replace_between("""						texture.ReleaseBytes();
						Texture cachedTexture = null;""",
"""					}
					return true;""",
"""						texture.ReleaseBytes();
						// ANDROID PORT: the cache holds types only; record this one while the pixels are known.
						if (handle.Origin != null)
						{
							StoreCachedType(handle.Origin, handle.Transparency);
						}
						handle.DecodedTexture?.ReleaseBytes();
""")

# 6. The Android host allocates registration slots itself (under RegistrationLock).
replace("""		private int GetNextFreeTexture()""", """		internal int GetNextFreeTexture()""")

text = HEADER + text
open(TARGET, "w", encoding="utf-8", newline="\r\n").write(text)
print("wrote " + os.path.relpath(TARGET, ROOT))
