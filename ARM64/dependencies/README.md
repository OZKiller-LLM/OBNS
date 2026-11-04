# Native libraries for Windows on ARM64

An ARM64 process can't load the x64 DLLs upstream ships in `x64\`, so the ARM64 build has its own here, copied by
`ARM64/build-arm64.ps1` to `arm64\` beside the program. The game needs OpenAL to start, as upstream's does, so the build
stops without it. Tolk is here too (screen readers); without it the game runs, with none.

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

## Tolk (`arm64/Tolk.dll`)

- What: Davy Kager's Tolk, the screen reader library upstream ships for x64 and x86, built for ARM64. Without it the game
  runs with no screen reader (OBNS's MainLoop).
- Licence: GNU LGPL 3, as upstream's copies (`licenses/TOLK.txt`); a separate DLL loaded at run time.
- Source: https://github.com/dkager/tolk, which has no releases: master at `e5149f0cb6ef9b941673017e0e7b7c409e485fbe`
  (2024-06-08), unchanged (https://github.com/dkager/tolk/archive/e5149f0cb6ef9b941673017e0e7b7c409e485fbe.zip,
  557,864 bytes, sha256 `2f685cac7bd3824eb7c3b632898dd8e443f112ffc1f81f9a2b4b5f06f00a95a1`).
- Built with Visual Studio 2022 Build Tools (MSVC 14.44.35207) on an ARM64 laptop, with upstream's `src\Makefile`:

      vcvarsall.bat arm64
      nmake /nologo OUTDIR=<out> CFLAGS="/nologo /O2 /EHsc /LD /Gw /W4 /WL /MT /D_EXPORTING /DUNICODE"

  Against upstream's flags, `/MT` (the runtime linked in) and no `/D_WITH_JNI` (the Java bridge, which needs a JDK).
- `Tolk.dll`: 120,320 bytes, machine ARM64 (0xAA64), sha256 `e77a3228814e16712c3a6855c1878c2912c4969d0893aa065eae9d297c2292cd`;
  imports only USER32, ole32, OLEAUT32 and KERNEL32.
- JAWS and SAPI work through COM. NVDA needs its controller client, which NV Access ships for ARM64 (not here yet; Tolk
  loads it as `nvdaControllerClient64.dll` in a 64-bit process). The other readers' clients are x86 and x64 only.
