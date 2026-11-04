# Native libraries for Windows on ARM64

An ARM64 process can't load the x64 DLLs upstream ships in `x64\`, so the ARM64 build has its own here, copied by
`ARM64/build-arm64.ps1` to `arm64\` beside the program.

## OpenAL Soft (`arm64/OpenAL32.dll`)

- What: OpenAL Soft 1.25.2, the library upstream ships for x64 and x86, built for ARM64. OpenAL Soft has no Windows
  ARM64 binary of its own.
- Licence: GNU LGPL, as upstream's copies (`licenses/OpenALSoft.txt`). It is a separate DLL loaded at run time, so OBNS's
  and OpenBVE's code keep their own licences.
- Source: https://github.com/kcat/openal-soft, tag `1.25.2`, unchanged
  (https://github.com/kcat/openal-soft/archive/refs/tags/1.25.2.zip).
- Built with Visual Studio 2022 Build Tools (MSVC 14.44, ARM64) on an ARM64 laptop:
  `cmake -A ARM64 -DALSOFT_EXAMPLES=OFF -DALSOFT_UTILS=OFF -DALSOFT_TESTS=OFF`, then the Release configuration.
- Not added yet: it is being built.
