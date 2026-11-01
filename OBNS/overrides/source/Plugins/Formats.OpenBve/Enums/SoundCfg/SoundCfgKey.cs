// OBNS: a copy of upstream's source/Plugins/Formats.OpenBve/Enums/SoundCfg/SoundCfgKey.cs (OpenBVE 1.14.1.2), laid over the desktop
// build's mirror (OBNS/build-desktop.ps1) and compiled by Android (Formats.OpenBve.Android.csproj): [AirSpring]'s keys. OpenBVE's suspension has one sound a side,
// which is the air spring's release; the Apply sounds are read and not played.
// On an upstream update: copy the file again and reapply the parts marked OBNS.
namespace Formats.OpenBve
{
	/// <summary>The keys in a sound.cfg file</summary>
	public enum SoundCfgKey
	{
		Unknown = 0,
		// brake
		BcReleaseHigh,
		BcRelease,
		BcReleaseFull,
		Emergency,
		EmergencyRelease,
		BpDecomp,
		// compressor
		Attack,
		Loop,
		Release,
		// suspension
		Left,
		// OBNS: [AirSpring]
		LeftRelease = Left,
		LeftApply,
		Right,
		// OBNS: [AirSpring]
		RightRelease = Right,
		RightApply,
		// horn
		PrimaryStart,
		// duplicated indicies used for enum parsing
		PrimaryEnd,
		PrimaryRelease = PrimaryEnd,
		PrimaryLoop,
		Primary,
		SecondaryStart,
		SecondaryEnd,
		SecondaryRelease = SecondaryEnd,
		SecondaryLoop,
		Secondary,
		MusicStart,
		MusicEnd,
		MusicRelease = MusicEnd,
		MusicLoop,
		Music,
		// door
		OpenLeft,
		OpenRight,
		CloseLeft,
		CloseRight,
		// buzzer
		Correct,
		// pilot lamp
		On,
		Off,
		// brake handle
		Apply,
		ApplyFast,
		// **release** duplicated above
		ReleaseFast,
		Min,
		Max,
		// master controller
		Up,
		UpFast,
		Down,
		DownFast,
		// **min** and **max** duplicated above
		// reverser + breaker **on** and **off** duplicated above
		//others
		Noise,
		Shoe,
		Halt,
		// windscreen
		RainDrop,
		WetWipe,
		DryWipe,
		Switch,
		// request stop
		Stop,
		Pass,
		Ignored
	}
}
