# BVE5 trains in OpenBVE (plan)

BVE Trainsim 5 trains, and their safety plugins, for the desktop build and Android. A BVE5 train is a
folder with a vehicle file (`BveTs Vehicle 2.00`) naming its other files. Upstream's train plugin
recognises the header (`Plugin.CanLoadTrain`) and notes that BVE5 is not implemented; upstream already
has the BVE5 motor noise tables (`Train/BVE5/MotorSoundTable.cs`, used from train.xml).

Status: formats gathered (below); loader not written yet. **Needs real BVE5 trains to check against**
- the motor noise file's header and keys, and the step logic of the performance tables, are not
fully documented in what could be read.

## Files

Every file: first line `BveTs <kind> <version>[:encoding]` (UTF-8 if no encoding), `#` or `;` comments,
INI sections and `Key = Value`, paths relative to the file.

| File | Header | Contents |
|---|---|---|
| Vehicle | `BveTs Vehicle 2.00` | `PerformanceCurve`, `Parameters`, `Panel`, `Sound`, `MotorNoise`, `Ats32`, `Ats64` |
| Parameters | `BveTs Vehicle Parameters 2.01` | general (`FirstCar`, `LoadCompensating`); `Cab`/`OneLeverCab` (notch counts, holding speed, ATS cancel notch, texts); `ConstantSpeedControl`; `MainCircuit`; `PowerReAdhesion`; brake system `Ecb`/`Smee`/`Cl` with `Sap`, `Er`, `Bp`, `AirSupplement`/`LockoutValve`, `Bc`/`BcServo`, `Brake`, `BrakeReAdhesion`; `Compressor`; `Dynamics` (masses, counts, inertia, car length, resistance); `Passengers`; `Door`; `ViewPoint` |
| Performance | (vehicle performance file; header not confirmed) | `[Power]` and `[Brake]`, each: `Force`, `MaxForce`, `Current`, `MaxCurrent`, `NoLoadCurrent`, `Params` - paths to performance tables |
| Performance table | `BveTs Vehicle Performance Table 2.01` | a grid: column 1 the speed (km/h, ascending), each further column one curve (a "notch" or "step"); force in N per motor car, current in A; linear between speeds. The `Params` table has rows `BreakerDelay_On/Off`, `ResetTime`, `CurrentReducingTime`, `JerkRegulation_Up/Down`, `RequiredNotch_Up/Down`, `StepDelay_Up/Down`, `CurrentLimitingValue_Empty/Full` |
| Sound | `BveTs Vehicle Sound 3.01` | `[Run]`, `[Flange]`, `[Motor]`, `[Joint]` (indexed), `[Brake]`, `[Compressor]`, `[AirSpring]`, `[Breaker]`, `[Horn]`, `[Door]`, `[Ats]` (0-255), `[Buzzer]`, `[PilotLamp]`, `[BrakeHandle]`, `[MasterController]`, `[Reverser]`, `[Others]` |
| Motor noise | `BveTs Motor Noise ...` (not confirmed) | the power and brake pitch and volume tables (`BveTs Motor Noise Table`), as upstream's XML `PowerFreq`, `PowerVol`, `BrakeFreq`, `BrakeVol` |
| Panel | `BveTs Instrument Panel 2.01` | `[This]`, `[PilotLamp]`, `[Needle]`, `[DigitalGauge]`, `[DigitalNumber]`; subjects `kmph`, `bc`, `mr`, `sap`, `bp`, `er`, `am`, `door`, `csc`, `ats0`-`ats255`, `power`, `brake`, `hour`/`min`/`sec` and digits |
| Safety plugin | Win32 DLL, the BVE4 ATS interface (16 functions) | `Ats32` (x86), `Ats64` (x64) |

## Mapping onto OpenBVE

- **Cars**: `Dynamics` counts and masses, `FirstCar`; motor cars get the traction model.
- **Handles**: `Cab` notch counts, holding-speed brake as hold brake, `OneLeverCab` as a single handle.
- **Brakes**: `Ecb` → electric command brake, `Smee` → electromagnetic straight air brake,
  `Cl` → automatic air brake - OpenBVE's own three, which follow BVE4's, with the pressures,
  rates and volume ratios from `Bc`, `Bp`, `Sap`, `Er`, `Compressor`, `Brake`.
- **Traction**: a BVE5 acceleration model per motor car - force from the active curve at the
  speed, scaled by load (`MaxForce`) - with BVE5's step state (advance while the handle is at
  `RequiredNotch_Up` or above and the current under `CurrentLimitingValue`, after `StepDelay`;
  return likewise), jerk regulation, the regeneration limits and the breaker delays. The current
  tables feed the `am` panel subject and motor sound.
- **Panel**: a BVE5 panel parser onto OpenBVE's 2D panel elements (the same element kinds as
  panel2.cfg), its subjects mapped to OpenBVE's. A BVE5 train's panel, in either format, is read
  as BVE5 reads it: laid out for a 4:3 view, with the default view kept within the panel's
  limits; and a needle with `NaturalFreq = 0` follows its value undamped (upstream holds such a
  needle at its first angle; most BVE5 needles set 0, the E233's speedometer among them).
- **Sound**: the vehicle sound file onto OpenBVE's car sounds; motor noise through upstream's
  `Bve5MotorSoundTableParser`.
- **Safety plugin**: `Ats64` on the desktop (a 64-bit process loads it directly); `Ats32` through
  upstream's Win32PluginProxy, and on Android through the x86 plugin layer. An `Ats64`-only train
  on Android needs an x64 layer, which does not exist yet.

## Where it goes

A BVE5 train plugin in OBNS (a new upstream-style train plugin project, `Train.Bve5`), built into
the desktop build and compiled for Android like the other plugins. Upstream's `Train.OpenBve`
claims BVE5 files in `CanLoadTrain`; an OBNS copy of its `Plugin.cs` gives them up, as the comment
there says it should once a BVE5 plugin exists.
