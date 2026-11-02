// OBNS: a copy of upstream's source/OpenBveApi/Train/Axle.cs (OpenBVE 1.14.1.2), laid over the desktop
// build's mirror (OBNS/build-desktop.ps1) and compiled by Android: a BVE5 route's JointNoise.Play(index) picks the point sound (OBNS/BVE5.md).
// On an upstream update: copy the file again and reapply the parts marked OBNS.
using OpenBveApi.Routes;

namespace OpenBveApi.Trains
{
	/// <summary>An axle of a car or bogie</summary>
	public abstract class AbstractAxle
	{
		/// <summary>The track follower</summary>
		public TrackFollower Follower;
		/// <summary>Whether this axle is currently experiencing wheelslip</summary>
		public bool CurrentWheelSlip;
		/// <summary>The relative position of the axle</summary>
		public double Position;
		/// <summary>The point sounds array</summary>
		public object[] PointSounds;
		/// <summary>The current flange sound index at this axle's location</summary>
		public int FlangeIndex;
		/// <summary>The current run sound index at this axle's location</summary>
		public int RunIndex;
		/// <summary>Stores whether the point sound has been triggered</summary>
		public bool PointSoundTriggered;
		/// <summary>OBNS: the point sound the event asks for, or -1 for the one matching the current run sound</summary>
		public int PointSoundIndex = -1;
		/// <summary>A reference to the base car</summary>
		protected readonly AbstractCar baseCar;
		
		/// <summary>Creates a new axle</summary>
		protected AbstractAxle(Hosts.HostInterface currentHost, AbstractTrain Train, AbstractCar Car)
		{
			Follower = new TrackFollower(currentHost, Train, Car);
			baseCar = Car;
			PointSounds = new object[] { };
		}

		/// <summary>Gets the resistance value for this axle in the current direction</summary>
		public abstract double GetResistance(double Speed, double FrontalArea, double AirDensity, double AccelerationDueToGravity);

		/// <summary>Gets the critical value at which wheelslip occurs from motor drive</summary>
		public abstract double CriticalWheelSlipAccelerationForElectricMotor(double AccelerationDueToGravity);


		/// <summary>Gets the critical value at which wheelslip occurs from brake application</summary>
		public abstract double CriticalWheelSlipAccelerationForFrictionBrake(double AccelerationDueToGravity);
	}
}
