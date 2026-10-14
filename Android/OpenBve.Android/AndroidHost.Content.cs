//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2026, The OpenBVE Android port contributors
//
//Redistribution and use in source and binary forms, with or without
//modification, are permitted provided that the following conditions are met:
//
//1. Redistributions of source code must retain the above copyright notice, this
//   list of conditions and the following disclaimer.
//2. Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
//THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
//ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
//WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
//DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
//ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
//(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
//LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
//ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
//(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
//SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenBveApi;
using OpenBveApi.Hosts;
using OpenBveApi.Interface;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Routes;
using OpenBveApi.Textures;
using OpenBveApi.World;
using RouteManager2;
using ApiTexture = OpenBveApi.Textures.Texture;
using Path = System.IO.Path;

namespace OpenBve.Android
{
	/// <summary>
	/// Content loading for the Android host: plugin dispatch for textures and objects, and object
	/// registration with the renderer.
	/// </summary>
	/// <remarks>
	/// These mirror the desktop host (OpenBVE/System/Host.cs) closely, since the behaviour — try
	/// each plugin that claims the file, cache the result, report failures once — is part of what
	/// makes routes load the same way on both platforms. The differences are only in where the
	/// route, renderer and options come from: properties here rather than Program statics.
	/// </remarks>
	public partial class AndroidHost
	{
		/// <summary>The route being built. Objects are created against its disposal mode and block length.</summary>
		public CurrentRoute Route { get; set; }

		/// <summary>
		/// The simulation clock, as upstream's host reports it. Handle changes, spring returns and
		/// plugin timing are all scheduled against this; the base class's constant zero left every
		/// delayed power notch change waiting forever, so the power never reached the motors.
		/// </summary>
		public override double InGameTime => Route?.SecondsSinceMidnight ?? 0.0;

		/// <inheritdoc />
		public override Dictionary<int, Track> Tracks => Route?.Tracks ?? base.Tracks;

		/// <summary>The registered plugins, or none if registration has not happened yet.</summary>
		private ContentLoadingPlugin[] LoadedPlugins => Plugins ?? Array.Empty<ContentLoadingPlugin>();

		// --- animated world objects (signals, animated scenery) ---

		/*
		 * Upstream keeps these in OpenBVE's ObjectManager statics. The object code appends by
		 * reading the count, writing the slot, and incrementing the count through this property,
		 * so the array must grow in the setter, before the next slot is written.
		 */
		private WorldObject[] animatedWorldObjects = new WorldObject[16];
		private int animatedWorldObjectsUsed;

		/// <inheritdoc />
		public override int AnimatedWorldObjectsUsed
		{
			get => animatedWorldObjectsUsed;
			set
			{
				if (animatedWorldObjects.Length - 1 == animatedWorldObjectsUsed)
				{
					Array.Resize(ref animatedWorldObjects, animatedWorldObjects.Length << 1);
				}

				animatedWorldObjectsUsed = value;
			}
		}

		/// <inheritdoc />
		public override WorldObject[] AnimatedWorldObjects
		{
			get => animatedWorldObjects;
			set => animatedWorldObjects = value;
		}

		// --- textures ---

		/// <inheritdoc />
		public override bool QueryTextureDimensions(string path, out int width, out int height)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("texture query");
			if (File.Exists(path) || Directory.Exists(path))
			{
				foreach (ContentLoadingPlugin plugin in LoadedPlugins)
				{
					if (plugin.Texture == null)
					{
						continue;
					}

					try
					{
						if (plugin.Texture.CanLoadTexture(path) && plugin.Texture.QueryTextureDimensions(path, out width, out height))
						{
							return true;
						}
					}
					catch (Exception ex)
					{
						AddMessage(MessageType.Error, false, "Plugin " + plugin.Title + " raised the following exception at QueryTextureDimensions:" + ex.Message);
					}
				}
			}
			else
			{
				ReportProblem(ProblemType.PathNotFound, path);
			}

			width = 0;
			height = 0;
			return false;
		}


		/// <inheritdoc />
		public override bool LoadTexture(string path, TextureParameters parameters, out ApiTexture texture)
		{
			// A texture decoded in the background, being uploaded now (see StreamTexture).
			if (handoff != null)
			{
				texture = handoff;
				handoff = null;
				return true;
			}

			if (TryAnswerFormatProbe(path, out texture))
			{
				return true;
			}

			using LoadProfile.Scope profile = LoadProfile.Enter("texture decode " + Path.GetExtension(path).ToLowerInvariant());
			if (!File.Exists(path) && !Directory.Exists(path))
			{
				ReportProblem(ProblemType.PathNotFound, path);
				texture = null;
				return false;
			}

			foreach (ContentLoadingPlugin plugin in LoadedPlugins)
			{
				if (plugin.Texture == null)
				{
					continue;
				}

				try
				{
					if (!plugin.Texture.CanLoadTexture(path))
					{
						continue;
					}

					// Large images halved while still native (System.Drawing.Bitmap.DecodeMaxSize).
					System.Drawing.Bitmap.DecodeMaxSize = parameters?.ClipRegion is null ? TextureSizeLimit.MaxSize : 0;
					System.Drawing.Bitmap.DecodeNearest = parameters?.TransparentColor != null;
					bool decoded;
					try
					{
						decoded = plugin.Texture.LoadTexture(path, out texture);
					}
					finally
					{
						System.Drawing.Bitmap.DecodeMaxSize = 0;
					}

					if (decoded)
					{
						texture.CompatibleTransparencyMode = Options.OldTransparencyMode;
						texture = TextureSizeLimit.Apply(texture.ApplyParameters(parameters));
						return true;
					}

					ReportOnce(FailedTextures, path, "Plugin " + plugin.Title + " returned unsuccessfully at LoadTexture for file " + path);
				}
				catch (Exception ex)
				{
					AddMessage(MessageType.Error, false, "Plugin " + plugin.Title + " raised the following exception at LoadTexture:" + ex.Message);
				}
			}

			ReportOnce(FailedTextures, path, new FileInfo(path).Length == 0
				? "Zero-byte texture file encountered at " + path
				: "No plugin found that is capable of loading texture " + path);
			texture = null;
			return false;
		}

		// --- objects ---

		/*
		 * Upstream's object caches (plain dictionaries on HostInterface), behind a lock: scripted
		 * trains load on several threads (ScriptedTrainLoader), and each asks for its objects.
		 * Only the lookups and insertions are locked; parsing and copying happen outside.
		 */
		private readonly object objectCacheLock = new object();

		private bool TryCachedStatic(ValueTuple<string, bool, DateTime> key, out StaticObject cached)
		{
			lock (objectCacheLock)
			{
				return StaticObjectCache.TryGetValue(key, out cached);
			}
		}

		private void CacheStatic(ValueTuple<string, bool, DateTime> key, StaticObject value)
		{
			lock (objectCacheLock)
			{
				StaticObjectCache[key] = value;
			}
		}

		private bool TryCachedCollection(string key, out AnimatedObjectCollection cached)
		{
			lock (objectCacheLock)
			{
				return AnimatedObjectCollectionCache.TryGetValue(key, out cached);
			}
		}

		private void CacheCollection(string key, AnimatedObjectCollection value)
		{
			lock (objectCacheLock)
			{
				AnimatedObjectCollectionCache[key] = value;
			}
		}

		/// <inheritdoc />
		public override bool LoadStaticObject(string path, Encoding encoding, bool preserveVertices, out StaticObject staticObject)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("object load", path);
			// Upstream's cache lookup (HostInterface.LoadStaticObject), with the file date remembered while loading.
			if (TryCachedStatic(ValueTuple.Create(path.ToLowerInvariant(), preserveVertices, OpenBveApi.Path.LastWriteTime(path)), out StaticObject cached))
			{
				using (LoadProfile.Enter("object load: cached copy"))
				{
					staticObject = (StaticObject)cached.Clone();
				}

				return true;
			}

			if (!File.Exists(path) && !Directory.Exists(path))
			{
				ReportProblem(ProblemType.PathNotFound, path);
				staticObject = null;
				return false;
			}

			// Parsed ahead on another core (ObjectPrefetch): carry on from the plugin's result.
			if (ObjectPrefetch.Take(path, encoding, ObjectPrefetch.Culls(preserveVertices, Options.ObjectOptimizationVertexCulling)) is StaticObject prefetched)
			{
				prefetched.OptimizeObject(preserveVertices, Options.ObjectOptimizationBasicThreshold, Options.ObjectOptimizationVertexCulling); // already done: returns at once
				staticObject = prefetched;
				CacheStatic(ValueTuple.Create(path.ToLowerInvariant(), preserveVertices, OpenBveApi.Path.LastWriteTime(path)), prefetched);
				return true;
			}

			encoding = TextEncoding.GetSystemEncodingFromFile(path, encoding);
			foreach (ContentLoadingPlugin plugin in LoadedPlugins)
			{
				if (plugin.Object == null)
				{
					continue;
				}

				try
				{
					if (!plugin.Object.CanLoadObject(path))
					{
						continue;
					}

					if (plugin.Object.LoadObject(path, encoding, out UnifiedObject unified))
					{
						if (unified is StaticObject loaded)
						{
							using (LoadProfile.Enter("object optimise"))
							{
								loaded.OptimizeObject(preserveVertices, Options.ObjectOptimizationBasicThreshold, Options.ObjectOptimizationVertexCulling);
							}

							staticObject = loaded;
							CacheStatic(ValueTuple.Create(path.ToLowerInvariant(), preserveVertices, OpenBveApi.Path.LastWriteTime(path)), loaded);
							return true;
						}

						AddMessage(MessageType.Error, false, "Attempted to load " + path + " which is an animated object where only static objects are allowed.");
					}

					ReportOnce(FailedObjects, path, "Plugin " + plugin.Title + " returned unsuccessfully at LoadObject for file " + path);
				}
				catch (Exception ex)
				{
					AddMessage(MessageType.Error, false, "Plugin " + plugin.Title + " raised the following exception at LoadObject:" + ex.Message);
				}
			}

			ReportOnce(FailedObjects, path, "No plugin found that is capable of loading object " + path);
			staticObject = null;
			return false;
		}

		/// <summary>
		/// For ObjectPrefetch, on a worker thread: the object plugin's parse of a file, optimised as
		/// LoadObject optimises it (no caching - LoadObject does that when the file is asked for).
		/// Null if no plugin loads it; the loading thread then tries again, and reports.
		/// </summary>
		internal UnifiedObject ParseObjectForPrefetch(string path, Encoding encoding)
		{
			foreach (ContentLoadingPlugin plugin in LoadedPlugins)
			{
				try
				{
					if (plugin.Object != null && plugin.Object.CanLoadObject(path) && plugin.Object.LoadObject(path, encoding, out UnifiedObject loaded) && loaded != null)
					{
						loaded.OptimizeObject(false, Options.ObjectOptimizationBasicThreshold, true);
						return loaded;
					}
				}
				catch (Exception)
				{
					return null;
				}
			}

			return null;
		}

		/// <inheritdoc />
		public override bool LoadObject(string path, Encoding encoding, out UnifiedObject unifiedObject)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("object load", path);
			// Upstream's cache lookup (HostInterface.LoadObject), with the file date remembered while loading.
			if (TryCachedStatic(ValueTuple.Create(path.ToLowerInvariant(), false, OpenBveApi.Path.LastWriteTime(path)), out StaticObject cachedStatic))
			{
				using (LoadProfile.Enter("object load: cached copy"))
				{
					unifiedObject = cachedStatic.Clone();
				}

				return true;
			}

			if (TryCachedCollection(path.ToLowerInvariant(), out AnimatedObjectCollection cachedCollection))
			{
				using (LoadProfile.Enter("object load: cached copy"))
				{
					unifiedObject = cachedCollection.Clone();
				}

				return true;
			}

			if (!File.Exists(path) && !Directory.Exists(path))
			{
				ReportProblem(ProblemType.PathNotFound, path);
				unifiedObject = null;
				return false;
			}

			// Parsed ahead on another core (ObjectPrefetch): carry on from the plugin's result.
			if (ObjectPrefetch.Take(path, encoding, ObjectPrefetch.Culls(false, true)) is UnifiedObject prefetched)
			{
				prefetched.OptimizeObject(false, Options.ObjectOptimizationBasicThreshold, true); // already done: returns at once
				unifiedObject = prefetched;
				if (prefetched is StaticObject prefetchedStatic)
				{
					CacheStatic(ValueTuple.Create(path.ToLowerInvariant(), false, OpenBveApi.Path.LastWriteTime(path)), prefetchedStatic);
				}
				else if (prefetched is AnimatedObjectCollection prefetchedCollection)
				{
					CacheCollection(path.ToLowerInvariant(), prefetchedCollection);
				}

				return true;
			}

			encoding = TextEncoding.GetSystemEncodingFromFile(path, encoding);
			foreach (ContentLoadingPlugin plugin in LoadedPlugins)
			{
				if (plugin.Object == null)
				{
					continue;
				}

				try
				{
					if (!plugin.Object.CanLoadObject(path))
					{
						continue;
					}

					if (plugin.Object.LoadObject(path, encoding, out UnifiedObject loaded) && loaded != null)
					{
						using (LoadProfile.Enter("object optimise"))
						{
							loaded.OptimizeObject(false, Options.ObjectOptimizationBasicThreshold, true);
						}

						unifiedObject = loaded;

						if (loaded is StaticObject staticObject)
						{
							CacheStatic(ValueTuple.Create(path.ToLowerInvariant(), false, OpenBveApi.Path.LastWriteTime(path)), staticObject);
						}
						else if (loaded is AnimatedObjectCollection collection)
						{
							CacheCollection(path.ToLowerInvariant(), collection);
						}

						return true;
					}

					ReportOnce(FailedObjects, path, "Plugin " + plugin.Title + " returned unsuccessfully at LoadObject for file " + path);
				}
				catch (Exception ex)
				{
					AddMessage(MessageType.Error, false, "Plugin " + plugin.Title + " raised the following exception at LoadObject:" + ex.Message);
				}
			}

			string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
			if (!NullFiles.Contains(name))
			{
				ReportOnce(FailedObjects, path, new FileInfo(path).Length == 0
					? "Zero-byte object file encountered at " + path
					: "No plugin found that is capable of loading object " + path);
			}

			unifiedObject = null;
			return false;
		}

		/// <inheritdoc />
		public override int CreateStaticObject(StaticObject prototype, Vector3 position, ObjectCreationParameters parameters,
			Transformation worldTransformation, Transformation localTransformation = null)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("object create");
			return Renderer.CreateStaticObject(prototype, position, worldTransformation, localTransformation,
				Route.AccurateObjectDisposal, parameters, Route.BlockLength);
		}

		/// <inheritdoc />
		public override int CreateStaticObject(StaticObject prototype, Vector3 position, Transformation localTransformation,
			Matrix4D rotate, Matrix4D translate, ObjectCreationParameters parameters)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("object create");
			return Renderer.CreateStaticObject(position, prototype, localTransformation, rotate, translate,
				Route.AccurateObjectDisposal, parameters, Route.BlockLength);
		}

		/// <inheritdoc />
		public override void CreateDynamicObject(ref ObjectState internalObject)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("object create");
			// Scripted trains create theirs on several threads (ScriptedTrainLoader).
			lock (Renderer.DynamicObjectStates)
			{
				Renderer.CreateDynamicObject(ref internalObject);
			}
		}

		/// <inheritdoc />
		public override void ShowObject(ObjectState objectToShow, ObjectType objectType)
		{
			/*
			 * Upstream shows every visible animated object again every frame (~4,300 calls a frame on
			 * the Tuen Ma Line from outside), and the renderer ignores all but the first. Already
			 * shown, and not about to be hidden by a queued hide: nothing to do, on any thread.
			 */
			if (objectToShow != null && Renderer.VisibleObjects.Objects.ContainsKey(objectToShow) && !ParallelUpdates.HidePending(objectToShow))
			{
				return;
			}

			// During a parallel update, applied by the render thread afterwards (ParallelUpdates).
			if (ParallelUpdates.Active)
			{
				ParallelUpdates.Defer(objectToShow, objectType, true);
				return;
			}

			// Locked: scripted trains' cars are set up on several threads (ScriptedTrainLoader).
			lock (visibilityLock)
			{
				Renderer.VisibleObjects.ShowObject(objectToShow, objectType);
			}
		}

		/// <inheritdoc />
		public override void HideObject(ObjectState objectToHide)
		{
			if (ParallelUpdates.Active)
			{
				ParallelUpdates.Defer(objectToHide, ObjectType.Dynamic, false);
				return;
			}

			lock (visibilityLock)
			{
				Renderer.VisibleObjects.HideObject(objectToHide);
			}
		}

		private readonly object visibilityLock = new object();

		/// <summary>Reports a failure for a file only the first time it happens, as upstream does.</summary>
		private void ReportOnce(HashSet<string> failures, string path, string message)
		{
			// Streamed textures are decoded on worker threads, so failures can arrive concurrently.
			lock (failures)
			{
				if (!failures.Add(path))
				{
					return;
				}

				AddMessage(MessageType.Error, false, message);
			}
		}
	}
}
