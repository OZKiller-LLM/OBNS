#!/bin/bash
# Usage: bash Android/UpstreamPatches/check_shaders.sh [glslang]
# Compiles the shaders the Android build embeds (the Android copies where there are any, otherwise
# upstream's) as GLSL ES 3.00, prepared as OpenBve.GLES's ShaderTranslator prepares them. Run it
# after every upstream update: desktop GLSL converts int to float and ivec2 to vec2 by itself, GLSL
# ES does not, and a shader that fails here leaves the OpenGL ES renderer without its shaders.
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
GLSLANG="${1:-${GLSLANG:-D:/dev/tools/glslang/bin/glslang.exe}}"
WORK="$(mktemp -d)"
failed=0
for upstream in "$ROOT"/OpenBVE/assets/Shaders/*.vert "$ROOT"/OpenBVE/assets/Shaders/*.frag; do
  name="$(basename "$upstream")"
  source="$upstream"
  [ -f "$ROOT/Android/LibRender2.Android/Shaders/$name" ] && source="$ROOT/Android/LibRender2.Android/Shaders/$name"
  {
    echo "#version 300 es"
    echo "precision highp float;"
    echo "precision highp int;"
    echo "precision highp sampler2D;"
    echo "precision highp sampler2DShadow;"
    # the version line is dropped and a byte-order mark removed, as the translator and the driver see it
    sed '1s/^\xEF\xBB\xBF//' "$source" | grep -v '^#version'
  } > "$WORK/$name"
  out="$("$GLSLANG" "$WORK/$name" 2>&1 | grep -E "ERROR" )"
  if [ -n "$out" ]; then
    echo "FAIL $name ($source)"; echo "$out" | head -5; failed=1
  else
    echo "ok   $name"
  fi
done
rm -rf "$WORK"
exit $failed
