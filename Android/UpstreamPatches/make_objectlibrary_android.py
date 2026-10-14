"""Makes Android/LibRender2.Android/ObjectLibrary.Android.cs from upstream's
OpenBVE/source/LibRender2/Objects/ObjectLibrary.cs (1.14.1.2).

Usage (from the repository root): python Android/UpstreamPatches/make_objectlibrary_android.py

Upstream classifies a face's transparency from a decoded texture it caches (or the handle's
register-time decode). The Android TextureManager keeps transparency types only (see
make_texturemanager_android.py), so here the type is looked up, or worked out from a decode that
is then dropped - as in the Android port's 1.1. Each edit must match exactly once.
"""
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SOURCE = os.path.join(ROOT, "OpenBVE", "source", "LibRender2", "Objects", "ObjectLibrary.cs")
TARGET = os.path.join(ROOT, "Android", "LibRender2.Android", "ObjectLibrary.Android.cs")

HEADER = """// ANDROID PORT: a copy of upstream's LibRender2/Objects/ObjectLibrary.cs (OpenBVE 1.14.1.2), compiled in
// its place (LibRender2.Android.csproj), made by Android/UpstreamPatches/make_objectlibrary_android.py:
// transparency types come from the Android TextureManager's type cache. Do not edit by hand: change
// the script and run it again.
"""

text = open(SOURCE, encoding="utf-8-sig").read().replace("\r\n", "\n")


def replace(old, new):
    global text
    found = text.count(old)
    if found != 1:
        sys.exit("expected 1 of:\n%s\nfound %d" % (old, found))
    text = text.replace(old, new)


T = "\t"
# The day and night lookups: a type, not a decoded texture.
for which in ("daytime", "nighttime"):
    Which = which[0].upper() + which[1:]
    replace(
        "Texture " + which + "Texture = GetTransparencySourceTexture(State.Prototype.Mesh.Materials[face.Material]." + Which + "Texture);",
        "TextureTransparencyType " + which + "Type = GetTransparencyType(State.Prototype.Mesh.Materials[face.Material]." + Which + "Texture);")

replace("""						TextureTransparencyType transparencyType = TextureTransparencyType.Opaque;
						if (daytimeTexture != null)
						{
							// as loading the cached texture may have failed, e.g. corrupt file etc
							transparencyType = daytimeTexture.GetTransparencyType();
						}
""", """						TextureTransparencyType transparencyType = daytimeType;
""")
replace("""							TextureTransparencyType transparencyType = TextureTransparencyType.Opaque;
							if (nighttimeTexture != null)
							{
								// as loading the cached texture may have failed, e.g. corrupt file etc
								transparencyType = nighttimeTexture.GetTransparencyType();
							}
""", """							TextureTransparencyType transparencyType = nighttimeType;
""")

# The lookup itself.
start = text.index("		/// <summary>Gets a decoded texture for transparency classification without forcing a full re-decode.</summary>")
end = text.index("		public List<FaceState> GetSortedPolygons(", start)
text = text[:start] + """		/// <summary>A texture's transparency type: cached, or worked out from a decode that is then dropped.</summary>
		/// <param name="handle">The material texture handle, or null.</param>
		/// <returns>The type; opaque for no texture, or one that failed to load (e.g. a corrupt file).</returns>
		/// <remarks>ANDROID PORT: the Android TextureManager caches types only (see its textureCache).</remarks>
		private static TextureTransparencyType GetTransparencyType(Texture handle)
		{
			if (handle == null || handle.Origin == null)
			{
				return TextureTransparencyType.Opaque;
			}

			if (TextureManager.TryGetCachedType(handle.Origin, out TextureTransparencyType type))
			{
				return type;
			}

			Stopwatch sw = Stopwatch.StartNew();
			bool ok = handle.Origin.GetTexture(out Texture fresh);
			sw.Stop();
			TextureManager.TextureDecodeTime += sw.ElapsedMilliseconds;
			if (!ok || fresh == null)
			{
				// not cached: the file may be readable next time
				return TextureTransparencyType.Opaque;
			}

			type = fresh.GetTransparencyType();
			TextureManager.StoreCachedType(handle.Origin, type);
			return type;
		}

""" + text[end:]

text = HEADER + text
open(TARGET, "w", encoding="utf-8", newline="\r\n").write(text)
print("wrote " + os.path.relpath(TARGET, ROOT))
