# BVE5 trains in OBNS

BVE Trainsim 5 trains, and their safety plugins, on the desktop build and on Android. A BVE5 train is a
vehicle file (`BveTs Vehicle 2.00`) naming its other files. OBNS reads it in upstream's own train plugin
(`Train.OpenBve`): its OBNS copy of `Plugin.cs` claims BVE5 vehicles in `CanLoadTrain`, as upstream's
comments there say it should once BVE5 is supported, and builds them in `LoadTrain` (`Train/BVE5`). The
same files are compiled by the desktop build and by Android.

Checked against real trains: JR E233-7000 (cocoa; Kazuma's, on the BVE5 TWR Rinkai Line and the Saikyo
Line), Eidan 02, Metro 02B, Keio 8000, JR 103 and H102 (RockOn's JR pack), JRW 103.

## Files

Every file: first line `BveTs <kind> <version>[:encoding]` (UTF-8 if no encoding, else Shift_JIS where
not valid UTF-8; older trains write `Bvets`), `#` or `;` comments, INI sections and `Key = Value`, paths
relative to the file. The vehicle performance file and the motor noise file have no header line.

| File | Header | Contents |
|---|---|---|
| Vehicle | `BveTs Vehicle 0.03`-`2.00` | `PerformanceCurve`, `Parameters`, `Panel`, `Sound`, `MotorNoise`, `Ats` (old) / `Ats32`, `Ats64` |
| Parameters | `BveTs Vehicle Parameters 0.04`-`2.01` | `Cab`/`OneLeverCab`, `Ecb`/`Smee`/`Cl` with `Sap`, `Er`, `Bp`, `AirSupplement`/`LockoutValve`, `Bc`, `Brake`, `Compressor`, `Dynamics`, ... |
| Performance | (none) | `[Power]` and `[Brake]`: `Force`, `MaxForce`, `Current`, `MaxCurrent`, `NoLoadCurrent`, `Params` tables |
| Performance table | `BveTs Vehicle Performance Table 0.01`-`2.01` | speed (km/h) then a column per step; trailing commas and `#` rows |
| Sound | `BveTs Vehicle Sound 3.01` | BVE4's sound.cfg sections (`[Motor]` indexed sounds and the rest) |
| Motor noise | (none) | `[Power]` and `[Brake]`, each `Volume` and `Frequency`: motor noise tables |
| Motor noise table | `BveTs Motor Noise Table 0.01` | speed (km/h) then a column per `[Motor]` sound; blank cells interpolated |
| Panel | `BveTs Instrument Panel 2.01`, or panel2.cfg `Version 1.0` | panel2.cfg's sections and subjects |
| Safety plugin | Win32 DLL, the BVE4 ATS interface | `Ats32` (x86), `Ats64` (x64) |

## How OBNS reads them

- **Choosing the train**: a folder with a BVE5 vehicle file (its `vehicle.txt`, else the first by name),
  or a vehicle file itself - a formation of several in one folder is chosen by its file, which the train
  lists show as upstream's lists show MSTS consists (desktop and Android) and the command line accepts.
  A BVE5 scenario's `Vehicle` (chosen by weight, as its route is) is the route's suggested train
  (OBNS's copies of `Route.Bve5/ScenarioParser.cs`, `OpenBVE/System/Loading.cs` and `Program.cs`).
- **Cars, handles, brakes, traction**: `Dynamics` counts and masses; `Cab` notch counts; `Ecb`/`Smee`/
  `Cl` as OpenBVE's electric command, electromagnetic straight air and automatic air brakes; a BVE5
  traction model per motor car with BVE5's step logic (a step advances while the handle is at
  `RequiredNotch_Up` or above and the current at or under `CurrentLimitingValue`).
  `[AirSupplement]` with an `InitialPressure` is delay-filling control (the electric brake stands in
  for the air); without one, the air keeps the full command and the electric brake adds to it
  (37 of the 57 such files met), as BVE does by Rinkin's logs (`D:\dev\Rinkin\docs\sim.md`).
- **Checked against Rinkin's simulator** (`rinkin-sim-run`, its model compared with BVE 5/6 runs):
  peak acceleration E233 cocoa 2.57 / 2.53 km/h/s (OBNS / Rinkin), TWR E233 2.54 / 2.50, E235
  3.06 / 3.02, JR 103 HK609 about 3.3 / 3.13; full service brake E233 cocoa 5.45 / 5.50, TWR E233
  4.51 / 4.65, E235 3.62 + 3.59 electric (both act) / 6.98.
- **Passengers** (`Bve5Load`, by Rinkin's runs of BVE5): the load factor of the stop just served (a
  station list's `passengers`, a factor: 1 is a full train; OBNS's copy of `Route.Bve5`'s
  `StationList.cs` reads it so, where upstream divided it by 100), from the first stop's at the start:
  factor x `[Passengers] Capacity` (150) people of `BodyWeight` (65 kg) a car. With `LoadCompensating`
  the current limits lie between `CurrentLimitingValue_Empty` and `_Full` by the load (at most 1),
  steps with jerk regulation raise their force by the loaded mass over the empty mass (up to
  `MaxForce`, the current towards `MaxCurrent` as much), and the brake cylinders' pressure rises by
  it, so that a loaded train accelerates and brakes about as an empty one; without, the same forces
  move more mass. Upstream's own passenger mass is never added to a car's (`CargoMass`).
- **Panel**: both formats through upstream's panel2.cfg parser (OBNS copy), read as BVE5 reads them: laid
  out for a 4:3 view with the default view kept within the panel's limits; a needle with
  `NaturalFreq = 0` undamped (upstream holds it at its first angle); a pilot lamp lit by any value but 0
  (upstream: 1 only; KO-ATC's speed-code lamps take 2, 4, 8 ...); `#` comments; a colour without its `#`
  (`TransparentColor = 0000ff`). A BVE5 cab ported to a BVE4 train's panel2.cfg asks for the same with
  `Bve5 = true` in `[This]` (an OBNS key; upstream reports it as unknown and reads the panel as before).
- **Ammeter**: `Subject = am`, through upstream's `ampsindex`, is one motor car's current (`Bve5MotorCar`):
  the `[Power]` `Current` table's for the step the controller settles on, following the force as it builds up,
  and while the motors brake the `[Brake]` table's as a negative current, by the share of the electric
  brake in use. It reads 0 on a ported cab on a BVE4 train, which has no such tables.
- **Sound**: the vehicle sound file through BVE4's sound.cfg parser; the motor noise as BVE5 plays it,
  through OBNS copies of upstream's BVE5 motor sound (`TrainManager/Motor/BVE5/MotorSound.cs`) and table
  parser (`Train/BVE5/MotorSoundTable.cs`), which fix upstream's: comments read as rows, blank cells read
  as full volume, pitch and volume rows at different speeds, interpolation running backwards, the brake
  table silent and the last sound left playing when coasting. Both copies use only upstream's types.
- **A train.dat train with BVE5 motor noise**: a train.dat train whose folder (or two levels below) holds
  a BVE5 motor noise file plays that noise in place of its own motor sound, and a BVE5 vehicle sound file
  there, if any, in place of its sound.cfg; its physics stay train.dat's (`Bve5SoundAddOn`).
- **Exterior**: as any train's - the vehicle file's folder's `train.xml` or `extensions.cfg`, else white
  boxes. Only that folder is looked in, not the folders above it: a formation whose vehicle file is in a
  subfolder (`formation\hae\116_TWR.txt`) has its own `extensions.cfg` in that subfolder, written for
  its own cars, as each formation's car count and order may differ.
- **Safety plugin**: `Ats32` through upstream's Win32PluginProxy on the desktop, or `Ats64` if it has no
  `Ats32`, loaded directly into the 64-bit program as BVE6 loads it (`Win64Plugin`; also any train.dat
  train's 64-bit plugin); on Android, Linux and macOS through the plugin layer (`NativePluginBridge`, which reads the
  vehicle file as an ats.cfg would be read), `Ats32` in its x86 processor and `Ats64`, if the train has no `Ats32`, in its
  x64 one. `OBNS_ATS64=1` takes `Ats64` where a train has both, for testing. A plugin under a path the system's ANSI code page cannot name (a Japanese
  train on a Chinese Windows) is loaded through an ASCII-named junction (`%LOCALAPPDATA%\OBNS\PluginPaths`),
  so that it can open its own files (OBNS copy of `TrainManager/SafetySystems/Plugin/Plugin.Functions.cs`).
  Each plugin proxy is started with a name of its own (OBNS copies of `ProxyPlugin.cs` and
  `Win32PluginProxy.cs`), so two games open at once each reach their own plugins; upstream's proxies
  all used one pipe and one ready event, and the second game waited forever.

## Not yet

- Upstream's BVE5 map parser (the `Bve5_Parsing` package) rejects some map 2.02 statements, such as
  `Track[...].Position(x, y, radius)`; such routes load without them.
- BVE5-only plugins that read BVE5's memory (AtsEx and the like) cannot work outside BVE5.
