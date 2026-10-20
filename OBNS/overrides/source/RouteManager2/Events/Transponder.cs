// OBNS: a copy of upstream's RouteManager2/Events/Transponder.cs (OpenBVE 1.14.1.2), used in its place by the
// desktop build (laid over the mirror by OBNS/build-desktop.ps1) and by Android (RouteManager2.Android.csproj):
// a section reference past the last section counts as none (see Trigger).
// On an upstream update: copy the file again and reapply the change.
using System;
using OpenBveApi.Routes;

namespace RouteManager2.Events
{
	/// <summary>Called when a train passes over a transponder attached to the signalling system</summary>
	public class TransponderEvent : GeneralEvent
	{
		private readonly CurrentRoute currentRoute;

		/// <summary>The type of transponder</summary>
		public readonly int Type;

		/// <summary>An optional data parameter passed to plugins receiving this event</summary>
		public readonly int Data;

		/// <summary>The index of the section this is attached to</summary>
		private readonly int SectionIndex;

		/// <summary>Whether the section index this transponder returns is that of the first red section ahead of the train</summary>
		private readonly bool ClipToFirstRedSection;

		public TransponderEvent(CurrentRoute CurrentRoute, double TrackPositionDelta, TransponderTypes Type, int Data, int SectionIndex, bool ClipToFirstRedSection)
			: this(CurrentRoute, TrackPositionDelta, (int)Type, Data, SectionIndex, ClipToFirstRedSection)
		{
		}

		public TransponderEvent(CurrentRoute CurrentRoute, double TrackPositionDelta, int Type, int Data, int SectionIndex, bool ClipToFirstRedSection) : base(TrackPositionDelta)
		{
			currentRoute = CurrentRoute;

			DontTriggerAnymore = false;
			this.Type = Type;
			this.Data = Data;
			this.SectionIndex = SectionIndex;
			this.ClipToFirstRedSection = ClipToFirstRedSection;
		}

		public override void Trigger(int direction, TrackFollower trackFollower)
		{
			if (trackFollower.TriggerType == EventTriggerType.TrainFront || trackFollower.TriggerType == EventTriggerType.SingleCarTrain)
			{
				int s = SectionIndex;
				if (s >= currentRoute.Sections.Length)
				{
					// OBNS: upstream 1.14.1.x keeps an orphaned transponder's section reference
					// (CsvRwRouteParser.ApplyRouteData), which can lie past the last section that exists;
					// such a reference counts as none, as 1.14.0.3 had it (-1), instead of throwing.
					s = -1;
				}

				if (ClipToFirstRedSection)
				{
					if (s >= 0)
					{
						while (true)
						{
							if (currentRoute.Sections[s].Exists(trackFollower.Train))
							{
								s = SectionIndex;
								break;
							}

							int a = currentRoute.Sections[s].CurrentAspect;

							if (a >= 0)
							{
								if (currentRoute.Sections[s].Aspects[a].Number == 0)
								{
									break;
								}
							}

							s = Array.IndexOf(currentRoute.Sections, currentRoute.Sections[s].PreviousSection);

							if (s >= 0)
							{
								s = SectionIndex;
								break;
							}
						}
					}
				}

				trackFollower.Train.UpdateBeacon(Type, s, Data);
			}
		}
	}
}
