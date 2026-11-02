// OBNS: a copy of upstream's source/RouteManager2/Events/PointSound.cs (OpenBVE 1.14.1.2), laid over the desktop
// build's mirror (OBNS/build-desktop.ps1) and compiled by Android: a BVE5 route's JointNoise.Play(index) picks the point sound (OBNS/BVE5.md).
// On an upstream update: copy the file again and reapply the parts marked OBNS.
using OpenBveApi.Routes;
using OpenBveApi.Trains;
using SoundManager;

namespace RouteManager2.Events
{
	public class PointSoundEvent : GeneralEvent
	{
		public PointSoundEvent(int index = -1)
		{
			DontTriggerAnymore = false;
			Index = index;
		}

		/// <summary>OBNS: the point sound to play, or -1 for the one matching the current run sound</summary>
		private readonly int Index;

		/// <summary>Triggers the playback of a sound</summary>
		/// <param name="direction">The direction of travel- 1 for forwards, and -1 for backwards</param>
		/// <param name="trackFollower">The TrackFollower</param>
		public override void Trigger(int direction, TrackFollower trackFollower)
		{
			if (SoundsBase.SuppressSoundEvents) return;
			AbstractCar car = trackFollower.Car;
			switch (trackFollower.TriggerType)
			{
				case EventTriggerType.FrontCarFrontAxle:
				case EventTriggerType.OtherCarFrontAxle:
					car.FrontAxle.PointSoundTriggered = true;
					car.FrontAxle.PointSoundIndex = Index;
					DontTriggerAnymore = false;
					break;
				case EventTriggerType.OtherCarRearAxle:
				case EventTriggerType.RearCarRearAxle:
					car.RearAxle.PointSoundTriggered = true;
					car.RearAxle.PointSoundIndex = Index;
					DontTriggerAnymore = false;
					break;
			}
		}
	}
}
