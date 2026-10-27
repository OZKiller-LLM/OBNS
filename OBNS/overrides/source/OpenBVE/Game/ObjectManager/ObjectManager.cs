// OBNS: a copy of upstream's source/OpenBVE/Game/ObjectManager/ObjectManager.cs (OpenBVE 1.14.1.2), laid over the
// desktop build's mirror (OBNS/build-desktop.ps1): animated world objects updated on several cores
// (UpdateAnimatedWorldObjectsParallel).
// On an upstream update: copy the file again and reapply the parts marked OBNS.
using System;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Trains;
using TrackFollowingObject = OpenBveApi.Objects.TrackFollowingObject;

namespace OpenBve
{
	/// <summary>The ObjectManager is the root class containing functions to load and manage objects within the simulation world</summary>
	public static class ObjectManager
	{
		/// <summary>Holds all animated objects currently in use within the game world</summary>
		internal static WorldObject[] AnimatedWorldObjects = new WorldObject[4];
		/// <summary>The total number of animated objects used</summary>
		internal static int AnimatedWorldObjectsUsed = 0;

		/// <summary>Is called once a frame to update all animated objects</summary>
		/// <param name="timeElapsed">The total frame time elapsed</param>
		/// <param name="forceUpdate">Whether this is a forced update (e.g. camera change etc)</param>
		internal static void UpdateAnimatedWorldObjects(double timeElapsed, bool forceUpdate)
		{
			// OBNS: on several cores, when there are enough to share out (see UpdateAnimatedWorldObjectsParallel).
			if (ParallelUpdates.Enabled && !forceUpdate && AnimatedWorldObjectsUsed >= 64)
			{
				UpdateAnimatedWorldObjectsParallel(timeElapsed);
				return;
			}

			for (int i = 0; i < AnimatedWorldObjectsUsed; i++)
			{
				AbstractTrain train = null;
				Vector3 cameraPos = Program.Renderer.Camera.Alignment.Position;
				cameraPos.Z += Program.Renderer.CameraTrackFollower.TrackPosition;
				bool visible = AnimatedWorldObjects[i].IsVisible(cameraPos, Program.CurrentRoute.CurrentBackground.BackgroundImageDistance, Program.Renderer.Camera.ExtraViewingDistance);
				if (visible | forceUpdate)
				{
					//Find the closest train
					train = Program.CurrentHost.ClosestTrain(AnimatedWorldObjects[i].Position);
				}

				if (forceUpdate)
				{
					if (Interface.CurrentOptions.DelayedAnimatedUpdates == false || AnimatedWorldObjects[i].TrackPosition - Math.Abs(Program.Renderer.CameraTrackFollower.TrackPosition) <= 5000 || AnimatedWorldObjects[i].Object.TrackFollowerFunction != null)
					{
						AnimatedWorldObjects[i].Update(train, timeElapsed, true, visible);
					}
				}
				else
				{
					AnimatedWorldObjects[i].Update(train, timeElapsed, false, visible);
				}
			}
		}

		/// <summary>
		/// OBNS: UpdateAnimatedWorldObjects (not forced) with the plain animated and keyframe objects updated on several
		/// cores (OBNS/shared/ParallelUpdates.cs, as the Android port does): each only moves and shows its own objects.
		/// Those that play sounds (WorldSound, state sounds) or follow the track stay on this thread, after the
		/// others, in their usual order.
		/// </summary>
		private static void UpdateAnimatedWorldObjectsParallel(double timeElapsed)
		{
			Vector3 cameraPos = Program.Renderer.Camera.Alignment.Position;
			cameraPos.Z += Program.Renderer.CameraTrackFollower.TrackPosition;
			double backgroundDistance = Program.CurrentRoute.CurrentBackground.BackgroundImageDistance;
			double extraViewingDistance = Program.Renderer.Camera.ExtraViewingDistance;
			int count = AnimatedWorldObjectsUsed;
			WorldObject[] objects = AnimatedWorldObjects;
			ParallelUpdates.Active = true;
			try
			{
				FrameWorkers.Shared.Run(count, (start, end) =>
				{
					for (int i = start; i < end; i++)
					{
						WorldObject worldObject = objects[i];
						if (worldObject is AnimatedWorldObject || worldObject is KeyframeWorldObject)
						{
							UpdateAnimatedWorldObject(worldObject, cameraPos, backgroundDistance, extraViewingDistance, timeElapsed);
						}
					}
				}, 32);
			}
			finally
			{
				ParallelUpdates.End(Program.CurrentHost);
			}

			for (int i = 0; i < count; i++)
			{
				WorldObject worldObject = objects[i];
				if (!(worldObject is AnimatedWorldObject) && !(worldObject is KeyframeWorldObject))
				{
					UpdateAnimatedWorldObject(worldObject, cameraPos, backgroundDistance, extraViewingDistance, timeElapsed);
				}
			}
		}

		/// <summary>OBNS: one object's part of UpdateAnimatedWorldObjects (not forced), as upstream does it.</summary>
		private static void UpdateAnimatedWorldObject(WorldObject worldObject, Vector3 cameraPos, double backgroundDistance, double extraViewingDistance, double timeElapsed)
		{
			bool visible = worldObject.IsVisible(cameraPos, backgroundDistance, extraViewingDistance);
			AbstractTrain train = visible ? Program.CurrentHost.ClosestTrain(worldObject.Position) : null;
			worldObject.Update(train, timeElapsed, false, visible);
		}

		/// <summary>Updates any TFOs within the world after a jump</summary>
		/// <param name="Train">The train which has jumped</param>
		public static void ProcessJump(AbstractTrain Train)
		{
			if (Train.IsPlayerTrain)
			{
				for (int i = 0; i < AnimatedWorldObjects.Length; i++)
				{
					if (AnimatedWorldObjects[i] is TrackFollowingObject obj)
					{
						//Track followers should be reset if we jump between stations
						obj.FrontAxleFollower.TrackPosition = AnimatedWorldObjects[i].TrackPosition + obj.FrontAxlePosition;
						obj.FrontAxleFollower.TrackPosition = AnimatedWorldObjects[i].TrackPosition + obj.RearAxlePosition;
						obj.FrontAxleFollower.UpdateWorldCoordinates(false);
						obj.RearAxleFollower.UpdateWorldCoordinates(false);
					}

				}
			}
			UpdateAnimatedWorldObjects(0.0,true);
		}
	}
}
