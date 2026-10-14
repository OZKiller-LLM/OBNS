"""One-off (1.2): installs the Android patches carried from 1.14.0.3 onto 1.14.1.2 as Android copies.

Input: a scratch tree of upstream 1.14.1.2 files with the old patches applied (patch -p1, see
README.md). Each becomes <Project>.Android/<Name>.Android.<ext> with a header saying what it is;
the projects compile the copy in place of upstream's file.
"""
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CARRIED = sys.argv[1]

# upstream path -> (Android project folder, what the patch does)
FILES = {
    "source/OpenBveApi/Interface/Translations/LanguageFile.cs": ("OpenBveApi.Android",
        "the embedded English is read from a stream (upstream's constructor takes a file path)"),
    "source/OpenBveApi/Objects/ObjectTypes/AnimatedObject/AnimatedObject.cs": ("OpenBveApi.Android",
        "the LED loop advances j, not v (upstream left all but one unused vertex in place)"),
    "source/OpenBveApi/System/FileSystem.cs": ("OpenBveApi.Android",
        "no entry assembly on Android: its folder falls back to the base directory; no ACL check"),
    "source/OpenBveApi/System/Hosts/HostInterface.cs": ("OpenBveApi.Android",
        "no named wait handles (the Win32 plugin proxy's), which Android cannot create"),
    "source/AssimpParser/Wavefront/ObjFileMtlImporter.cs": ("AssimpParser.Android",
        "an unused System.Runtime.Remoting import removed (not in .NET)"),
    "source/LibRender2/BaseRenderer.cs": ("LibRender2.Android",
        "no ReShade detection (a Windows DLL beside the entry assembly)"),
    "source/TrainManager/SafetySystems/Plugin/Plugin.Functions.cs": ("TrainManager.Android",
        "no WCF proxy for Win32 plugins (Android runs them in its x86 layer instead)"),
    "assets/Shaders/default.vert": ("LibRender2.Android",
        "float literals (GLSL ES has no implicit int to float conversion)"),
    "assets/Shaders/default.frag": ("LibRender2.Android",
        "explicit vec2 around textureSize (GLSL ES has no implicit ivec2 to vec2 conversion)"),
    "assets/Shaders/rectangle.frag": ("LibRender2.Android",
        "float literals in the alpha-test comparisons (GLSL ES has no implicit int to float conversion)"),
}

for path, (project, what) in FILES.items():
    name, ext = os.path.splitext(os.path.basename(path))
    folder = os.path.join(ROOT, "Android", project, "Shaders") if path.startswith("assets/") else os.path.join(ROOT, "Android", project)
    os.makedirs(folder, exist_ok=True)
    target = os.path.join(folder, name + ext if path.startswith("assets/") else name + ".Android" + ext)
    body = open(os.path.join(CARRIED, path), encoding="utf-8-sig").read().replace("\r\n", "\n")
    comment = "//"
    header = ("%s ANDROID PORT: a copy of upstream's %s (OpenBVE 1.14.1.2), used in its place (%s): %s.\n"
              "%s On an upstream update: copy the file again and reapply the change (Android/UpstreamPatches).\n"
              % (comment, path.replace("source/", ""), project + ".csproj", what, comment))
    if path.startswith("assets/"):
        # A GLSL file must start with #version: the note goes after it.
        first, rest = body.split("\n", 1)
        body = first + "\n" + header + rest
    else:
        body = header + body
    open(target, "w", encoding="utf-8", newline="\r\n").write(body)
    print(os.path.relpath(target, ROOT))
