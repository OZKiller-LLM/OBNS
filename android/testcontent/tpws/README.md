# TPWS test (OS_Ats1 train stop)

Reproduces the on-phone TPWS test of 2026-09-26 without shipping third-party content.

**Route**: a copy of `Railway/Route/EAL2023/東鐵線南行 (羅湖→金鐘).csv` (Big5) with two edits:

1. Lo Wu's `.sta` holds its departure signal red and leaves later:
   `;10.3200;10.3329;0;-1;0;0;` → `;10.3200;10.3829;0;-1;1;0;`
2. A TPWS train stop sensor 18 m before the starting signal (at 4628 m), inserted before the
   `4628` / `.sigf` block:

   ```
   4610
   .beacon 44003;0;1;0
   ```

   The section argument must be **1** (the next signal). Section 0 is the beacon's own section,
   and for the section a train is in upstream reports the aspect it would show without that train
   (`Section.GetPluginSignal`), which here is green.

**Train**: a folder holding only `train.dat` and `ats.cfg` from `MTR R-Train EMU 2023`, plus its
`Plugin` folder with `detailmodules.txt` (in this folder) listing only `OS_Ats1.dll`, so
SelTrac does not hold the train in mode 5 (Off).

**Drive**: close the doors that are open, notch up. Expected (logcat, `x86 DetailManager.dll`):
beacon 44003 with signal 0 → `panel 51=1` and the emergency brake; at a stand, **S** (tpwsresetkey
0) → `panel 51=0`, brake released; **B1** (tpwsoverridekey 3) → `panel 52=1`.

The same behaviour is checked on the desktop with
`OpenBve.X86.Tests <train> <OS_Ats1.dll> tpws 44003 0 0 60 0 3` (trip, reset, override) and
`tpws !44003` (override pressed first: no trip, override consumed).
