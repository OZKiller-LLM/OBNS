# Native libraries for Windows on ARM64

An ARM64 process can't load the x64 DLLs upstream ships in `x64\`, so the ARM64 build has its own here, copied by
`ARM64/build-arm64.ps1` to `arm64\` beside the program. The game needs OpenAL to start, as upstream's does, so the build
stops without it. Tolk and the screen readers' DLLs have no ARM64 build here: the game runs without a screen reader.

## OpenAL Soft (`arm64/OpenAL32.dll`)

- What: OpenAL Soft 1.25.2, the library upstream ships for x64 and x86, built for ARM64. OpenAL Soft has no Windows
  ARM64 binary of its own.
- Licence: GNU LGPL, as upstream's copies (`licenses/OpenALSoft.txt`). It is a separate DLL loaded at run time, so OBNS's
  and OpenBVE's code keep their own licences.
- Source: https://github.com/kcat/openal-soft, tag `1.25.2`, unchanged
  (https://github.com/kcat/openal-soft/archive/refs/tags/1.25.2.zip).
- Built with Visual Studio 2022 Build Tools (MSVC 14.44.35207, cl 19.44.35229) and CMake 3.31.6-msvc6 on an ARM64
  laptop, from the source zip (1,650,355 bytes, sha256 `47e22c066dffa2f65a1747272978344db845dcef9c7ee118449ec560a44adc8f`):

      cmake -S openal-soft-1.25.2 -B build -G "Visual Studio 17 2022" -A ARM64 -DALSOFT_EXAMPLES=OFF -DALSOFT_UTILS=OFF -DALSOFT_TESTS=OFF -DCMAKE_BUILD_TYPE=Release -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
      cmake --build build --config Release

- The C and C++ runtime are linked in (`CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded`), so the DLL needs only Windows' own
  DLLs, as upstream's x64 and x86 copies do; a player needs no Visual C++ redistributable. OpenAL Soft's own
  `FORCE_STATIC_VCRT` does nothing in 1.25.2 (its CMakeLists tests the CMake version against 15.0).
- Backends: WASAPI, DirectSound, WinMM, WaveFile, Null; NEON; EAX on; HRTF data embedded.
- `OpenAL32.dll`: 2,066,432 bytes, machine ARM64 (0xAA64), sha256
  `0754daf16827b2a8297cdcf49b54db58fc10f793ae16d8e9591edd6775e13e0c`.
