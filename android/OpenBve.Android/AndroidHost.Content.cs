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

					if (plugin.Texture.LoadTexture(path, out texture))
					{
						texture.CompatibleTransparencyMode = Options.OldTransparencyMode;
						texture = texture.ApplyParameters(parameters);
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

		/// <inheritdoc />
		public override bool LoadStaticObject(string path, Encoding encoding, bool preserveVertices, out StaticObject staticObject)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("object load");
			if (base.LoadStaticObject(path, encoding, preserveVertices, out staticObject))
			{
				return true;
			}

			if (!File.Exists(path) && !Directory.Exists(path))
			{
				ReportProblem(ProblemType.PathNotFound, path);
				staticObject = null;
				return false;
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
							loaded.OptimizeObject(preserveVertices, Options.ObjectOptimizationBasicThreshold, Options.ObjectOptimizationVertexCulling);
							staticObject = loaded;
							StaticObjectCache[ValueTuple.Create(path.ToLowerInvariant(), preserveVertices, File.GetLastWriteTime(path))] = loaded;
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

		/// <inheritdoc />
		public override bool LoadObject(string path, Encoding encoding, out UnifiedObject unifiedObject)
		{
			using LoadProfile.Scope profile = LoadProfile.Enter("object load");
			if (base.LoadObject(path, encoding, out unifiedObject))
			{
				return true;
			}

			if (!File.Exists(path) && !Directory.Exists(path))
			{
				ReportProblem(ProblemType.PathNotFound, path);
				unifiedObject = null;
				return false;
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
						loaded.OptimizeObject(false, Options.ObjectOptimizationBasicThreshold, true);
						unifiedObject = loaded;

						if (loaded is StaticObject staticObject)
						{
							StaticObjectCache[ValueTuple.Create(path.ToLowerInvariant(), false, File.GetLastWriteTime(path))] = staticObject;
						}
						else if (loaded is AnimatedObjectCollection collection)
						{
							AnimatedObjectCollectionCache[path.ToLowerInvariant()] = collection;
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
			if (Array.IndexOf(NullFiles, name) < 0)
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
			Renderer.CreateDynamicObject(ref internalObject);
		}

		/// <inheritdoc />
		public override void ShowObject(ObjectState objectToShow, ObjectType objectType)
		{
			Renderer.VisibleObjects.ShowObject(objectToShow, objectType);
		}

		/// <inheritdoc />
		public override void HideObject(ObjectState objectToHide)
		{
			Renderer.VisibleObjects.HideObject(objectToHide);
		}

		/// <summary>Reports a failure for a file only the first time it happens, as upstream does.</summary>
		private void ReportOnce(List<string> failures, string path, string message)
		{
			// Streamed textures are decoded on worker threads, so failures can arrive concurrently.
			lock (failures)
			{
				if (failures.Contains(path))
				{
					return;
				}

				failures.Add(path);
				AddMessage(MessageType.Error, false, message);
			}
		}
	}
}
