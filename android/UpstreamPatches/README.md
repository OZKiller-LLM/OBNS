# Android patches once made inside the upstream tree

`android-patches-1.14.0.3.diff` is every change the Android port made directly in upstream's
OpenBVE 1.14.0.3 source (the `OpenBVE-1.14.0.3/` folder, committed with 1.0), against upstream's
own `src-1.14.0.3.zip`: 15 files, about 210 changed lines (three shaders, LibRender2, OpenBveApi,
TrainManager's CarBase and plugin functions, the PNG decoder, the Wavefront material importer).

From 1.2 the upstream folder (`OpenBVE/`) is kept exactly as upstream publishes it; these changes
live in the Android projects instead (their own copies of the files concerned, upstream's left out
of the build). This file is the record of what had to be carried over.
