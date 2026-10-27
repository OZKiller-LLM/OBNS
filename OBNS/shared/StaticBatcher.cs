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
using System.Diagnostics;
using LibRender2;
using LibRender2.Objects;
using LibRender2.Textures;
using OpenBveApi.Colors;
using OpenBveApi.Graphics;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Hosts;
using OpenBveApi.Textures;

// OBNS: shared by the Android port (AndroidRenderer) and the desktop build (OBNS's copy of upstream's NewRenderer), each
// giving it what differs between them through IStaticBatchHost.

namespace OpenBve
{
	/// <summary>What the static batcher needs from the renderer drawing it, beyond upstream's BaseRenderer.</summary>
	internal interface IStaticBatchHost
	{
		/// <summary>The renderer: its object states, visible faces, shaders and debug options.</summary>
		BaseRenderer Renderer { get; }

		/// <summary>The program's host.</summary>
		HostInterface Host { get; }

		/// <summary>Whether transparency is in quality mode (colour-keyed faces in the transparent list).</summary>
		bool QualityTransparency { get; }

		/// <summary>The trains whose cars' still parts are batched; null until the game is loaded.</summary>
		IEnumerable<global::TrainManager.Trains.TrainBase> Trains();

		/// <summary>A face vertex array of a length, for drawing a merged face (only its length is read).</summary>
		MeshFaceVertex[] VerticesOfLength(int length);

		/// <summary>Draws one face, as upstream's RenderFace.</summary>
		void DrawFace(LibRender2.Shaders.Shader shader, ObjectState state, MeshFace face);

		/// <summary>A texture's transparency type, if it is known yet (a texture not classified is never batched).</summary>
		bool TryGetTransparencyType(Texture texture, out TextureTransparencyType type);

		/// <summary>Lets go of the CPU copy of a mesh's vertices once the GPU has them (where the platform keeps one).</summary>
		void ReleaseVertexCopy(Mesh mesh);

		/// <summary>Writes to the log.</summary>
		void Log(string message, bool warning);
	}

	/// <summary>
	/// Draws the route's static scenery in batches: the objects sharing a stretch of track merged,
	/// per material, into one mesh drawn with one call, instead of one call per object and
	/// material.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Dense scenery is thousands of small placed objects - posts, fence panels, sleepers,
	/// platform fittings, trees - and upstream draws each with its own calls. On a phone the cost
	/// of a draw call, not the drawing, sets the frame rate: 8,000 calls took ~150 ms a frame on
	/// the MTR route. OpenBVE's accurate object disposal gives every static object a visible range
	/// snapped to whole track blocks, so all the objects of a stretch share one range and are shown
	/// and hidden together; a group of them is merged into a batch, which is drawn exactly when
	/// its members would be.
	/// </para>
	/// <para>
	/// A batch is an ordinary upstream StaticObject in the group's own space: each member's
	/// vertices and normals baked through the member's model matrix, in the Z-flipped space the
	/// shader works in, relative to the first member's position so the floats stay precise. It is
	/// drawn through upstream's RenderFace, so lighting, fog, day/night and textures are exactly
	/// upstream's. Only faces upstream draws as opaque or colour-keyed are batched; anything
	/// truly translucent, additive, glowing or animated keeps its own sorted draw.
	/// </para>
	/// <para>
	/// Batches are built when their group first comes into view (a few milliseconds a frame at
	/// most; until then the members draw as before) and freed when out of view for a while, so
	/// memory follows what is near the camera, not the whole route.
	/// </para>
	/// </remarks>
	internal sealed class StaticBatcher
	{
		/// <summary>Objects with more face vertices than this are drawn as they are: they are few calls, and costly to copy.</summary>
		private const int MaxPrototypeFaceVertices = 4096;

		/*
		 * A train part is different: a car body is one large object the car shows at every moment
		 * it is in view, not one of thousands of scenery objects, and upstream leaves objects of
		 * 1,000 to 20,000 faces unfolded, so each of its faces is a draw call of its own (17,900 on
		 * the Tuen Ma Line's cars). Baking it once into the car's batch is worth the copy.
		 */
		private const int MaxTrainPartFaceVertices = 65536;

		/// <summary>Batched face vertices kept at once, across all batches (GPU and build memory).</summary>
		private const long MaxBatchedVertices = 3_000_000;

		/// <summary>How long a batch may go unseen before it is freed.</summary>
		private const double EvictAfterSeconds = 30.0;

		/// <summary>Time spent building batches per frame.</summary>
		private const double BuildBudgetMilliseconds = 6.0;

		private readonly IStaticBatchHost host;

		private sealed class Group
		{
			/// <summary>For a train group: the part the batch moves with (its members never move relative to it); null for scenery.</summary>
			public ObjectState Reference;
			public float Start, End;
			public readonly List<ObjectState> Members = new List<ObjectState>();
			public Batch Batch;
			public bool Queued, Failed;
			public double LastSeen;
			public int SeenFrame = -1;
			/// <summary>Whether members were taken out of their car's update (see Detach).</summary>
			public bool Detached;
		}

		private sealed class Batch
		{
			public ObjectState State;
			public int[] OpaqueFaces, AlphaFaces;
			/// <summary>Each merged face's index count (its vertex array is dropped once uploaded).</summary>
			public int[] FaceLengths;
			public HashSet<MeshFaceVertex[]> Covered;
			public long Vertices;
		}

		private readonly Dictionary<ObjectState, Group> groupOf = new Dictionary<ObjectState, Group>();

		/*
		 * Taking train parts out of upstream's per-frame update. Upstream's CarBase.UpdateObjects
		 * updates every part of every car of every train on every frame - functions, three axis
		 * rotations, a chain of double-precision matrix products - including cars far out of view
		 * (only to hide them again) and still parts the car batches draw (whose own matrices nothing
		 * reads once their batch exists: it is drawn with its reference part's matrix). The loop
		 * reads ElementsGroup.Elements afresh each time, so the port gives it a shorter array:
		 *  - a car beyond viewing range (CarBase.CurrentlyVisible false: upstream has just hidden
		 *    its parts) gets none, and all of them back the frame it comes within range;
		 *  - a built train batch's members other than the reference, whose faces it covers
		 *    entirely, are left out and hidden (such batches are never evicted).
		 * Each group's array is always recomputed from its original, and every original is put
		 * back before the trains are planned again or the batches freed.
		 * Turned off for comparisons with --ez detach_parts false on Android, OBNS_DETACH_PARTS=0 on the desktop.
		 */
		public static bool DetachParts = true;
		private readonly Dictionary<ObjectState, (LibRender2.Trains.ElementsGroup Owner, AnimatedObject Element)> partSource =
			new Dictionary<ObjectState, (LibRender2.Trains.ElementsGroup, AnimatedObject)>();
		private readonly Dictionary<LibRender2.Trains.ElementsGroup, AnimatedObject[]> originalElements =
			new Dictionary<LibRender2.Trains.ElementsGroup, AnimatedObject[]>();
		private readonly HashSet<AnimatedObject> batchDetached = new HashSet<AnimatedObject>();
		private readonly Dictionary<global::TrainManager.Car.CarBase, List<LibRender2.Trains.ElementsGroup>> carGroups =
			new Dictionary<global::TrainManager.Car.CarBase, List<LibRender2.Trains.ElementsGroup>>();
		private readonly Dictionary<LibRender2.Trains.ElementsGroup, global::TrainManager.Car.CarBase> groupCar =
			new Dictionary<LibRender2.Trains.ElementsGroup, global::TrainManager.Car.CarBase>();
		private readonly HashSet<global::TrainManager.Car.CarBase> farCars = new HashSet<global::TrainManager.Car.CarBase>();
		private readonly List<Group> built = new List<Group>();
		private readonly Queue<Group> toBuild = new Queue<Group>();
		private readonly List<Group> visible = new List<Group>();
		private int plannedCount = -1;
		private long batchedVertices;
		private int frame;
		private double lastEvictionCheck;
		private readonly Stopwatch clock = Stopwatch.StartNew();

		/// <summary>Batches built so far, and faces they replaced, for the performance log.</summary>
		public int BatchesBuilt, BatchesFreed, GroupsFailed;

		/// <summary>Why faces were left to draw on their own, by reason, for the performance log.</summary>
		private readonly Dictionary<string, int> leftAloneReasons = new Dictionary<string, int>();

		private void CountReason(string reason)
		{
			leftAloneReasons[reason] = leftAloneReasons.TryGetValue(reason, out int n) ? n + 1 : 1;
		}

		/// <summary>Switch for testing: batching on (default) or off (Android: --ez static_batching false; desktop: OBNS_STATIC_BATCHES=0).</summary>
		public static bool Enabled = true;

		public StaticBatcher(IStaticBatchHost host)
		{
			this.host = host;
		}

		/// <summary>A summary for the log: groups, batches in memory, their size.</summary>
		public string Describe()
		{
			return (groupOf.Count == 0 ? "no groups" : CountGroups() + " groups of " + groupOf.Count + " objects") + ", " + built.Count + " batches (" +
			       (batchedVertices / 1000) + "k vertices), " + visible.Count + " drawn; built " + BatchesBuilt + ", freed " + BatchesFreed +
			       ", failed " + GroupsFailed + ", queued " + toBuild.Count + ", train parts detached: " + batchDetached.Count + " batched, " + farCars.Count + " cars out of range; faces still drawn singly: scenery " + keptStatic + ", other " + keptOther + " (" +
			       string.Join(", ", System.Linq.Enumerable.Select(otherReasons, p => p.Key + " " + p.Value)) + ")" +
			       "; faces left alone: " +
			       string.Join(", ", System.Linq.Enumerable.Select(leftAloneReasons, p => p.Key + " " + p.Value));
		}

		private int CountGroups()
		{
			HashSet<Group> groups = new HashSet<Group>(groupOf.Values);
			return groups.Count;
		}

		// --- planning ---

		/*
		 * Groups the eligible static objects by their visible range (and day/night blend, which
		 * is per object). A range with a single object is not worth a batch.
		 */
		private void Plan()
		{
			List<ObjectState> states = host.Renderer.StaticObjectStates;
			if (states == null || states.Count == plannedCount)
			{
				return;
			}

			FreeAll();
			groupOf.Clear();
			trainSignature = -1;
			plannedCount = states.Count;
			staticStates = new HashSet<ObjectState>(states);
			Dictionary<(float, float, byte), Group> groups = new Dictionary<(float, float, byte), Group>();
			Dictionary<string, int> ineligible = new Dictionary<string, int>();
			foreach (ObjectState state in states)
			{
				string reason = Ineligible(state);
				if (reason != null)
				{
					ineligible[reason] = ineligible.TryGetValue(reason, out int n) ? n + 1 : 1;
					continue;
				}

				(float, float, byte) key = (state.StartingDistance, state.EndingDistance, state.DaytimeNighttimeBlend);
				if (!groups.TryGetValue(key, out Group group))
				{
					group = new Group { Start = state.StartingDistance, End = state.EndingDistance };
					groups[key] = group;
				}

				group.Members.Add(state);
			}

			int singletons = 0;
			foreach (Group group in groups.Values)
			{
				if (group.Members.Count < 2)
				{
					singletons++;
					continue;
				}

				foreach (ObjectState member in group.Members)
				{
					groupOf[member] = group;
				}
			}

			host.Log("static batches planned: " + states.Count + " static objects, " + groupOf.Count + " in " +
			                                         (groups.Count - singletons) + " groups, " + singletons + " alone in their range; not batched: " +
			         string.Join(", ", System.Linq.Enumerable.Select(ineligible, p => p.Key + " " + p.Value)), false);
		}

		/*
		 * Trains: each car's parts that never move relative to the car - body panels, windows,
		 * seats, handrails, under-floor equipment - an AnimatedObject with a single state and no
		 * animation of any kind. Upstream still places each of them every frame from the car's
		 * position, so their relative placement is fixed: merged per car section group, relative to
		 * one of them, and drawn each frame with that part's current matrix, the batch follows the
		 * car exactly - curves, cant, sway. Moving parts (doors, wheels, pantographs, displays)
		 * and the cab (drawn in its own pass) keep their own draws.
		 */
		private int trainSignature = -1;
		private readonly List<Group> trainGroups = new List<Group>();

		private void PlanTrains()
		{
			IEnumerable<global::TrainManager.Trains.TrainBase> source = host.Trains();
			if (source == null)
			{
				return;
			}

			List<global::TrainManager.Trains.TrainBase> trains = new List<global::TrainManager.Trains.TrainBase>(source);
			int signature = trains.Count;
			foreach (global::TrainManager.Trains.TrainBase train in trains)
			{
				signature = signature * 31 + train.Cars.Length;
			}

			if (signature == trainSignature)
			{
				return;
			}

			trainSignature = signature;
			ReattachAll();
			partSource.Clear();
			carGroups.Clear();
			groupCar.Clear();
			foreach (Group group in trainGroups)
			{
				Free(group);
				built.Remove(group);
				foreach (ObjectState member in group.Members)
				{
					groupOf.Remove(member);
				}
			}

			trainGroups.Clear();
			trainPartReason.Clear();
			int parts = 0, elementsSeen = 0;
			foreach (global::TrainManager.Trains.TrainBase train in trains)
			{
				foreach (global::TrainManager.Car.CarBase car in train.Cars)
				{
					foreach (LibRender2.Trains.CarSection section in car.CarSections.Values)
					{
						if (section.Type != ObjectType.Dynamic || section.Groups == null)
						{
							continue;
						}

						foreach (LibRender2.Trains.ElementsGroup elements in section.Groups)
						{
							if (elements?.Elements == null)
							{
								continue;
							}

							if (!carGroups.TryGetValue(car, out List<LibRender2.Trains.ElementsGroup> owned))
							{
								carGroups[car] = owned = new List<LibRender2.Trains.ElementsGroup>();
							}

							owned.Add(elements);
							groupCar[elements] = car;

							Group group = new Group();
							foreach (AnimatedObject element in elements.Elements)
							{
								if (element?.internalObject == null)
								{
									continue;
								}

								elementsSeen++;

								string why = NotStill(element) ?? Ineligible(element.internalObject, true);
								if (why == null && !groupOf.ContainsKey(element.internalObject))
								{
									group.Members.Add(element.internalObject);
									partSource[element.internalObject] = (elements, element);
								}
								else if (why != null)
								{
									trainPartReason[element.internalObject] = why;
								}
							}

							if (group.Members.Count < 2)
							{
								continue;
							}

							group.Reference = group.Members[0];
							foreach (ObjectState member in group.Members)
							{
								groupOf[member] = group;
							}

							parts += group.Members.Count;
							trainGroups.Add(group);
						}
					}
				}
			}

			host.Log("train batches planned: " + trains.Count + " trains, " + parts + " still parts in " + trainGroups.Count + " groups, of " + elementsSeen + " parts in all", false);
		}

		/// <summary>Why a train part is not batched, for the log.</summary>
		private readonly Dictionary<ObjectState, string> trainPartReason = new Dictionary<ObjectState, string>();

		/*
		 * A function that always gives the same value is as good as none: "RotateYFunction = 3.14"
		 * turns a part round once and for all (the Tuen Ma Line's trains are full of them), and a
		 * constant state function always shows the same state. Upstream's FunctionScript says
		 * which functions are constant (no variables, only constants and arithmetic).
		 */
		private static bool Constant(OpenBveApi.FunctionScripting.AnimationScript script) =>
			script == null || (script is OpenBveApi.FunctionScripting.FunctionScript function && function.ConstantResult());

		/// <summary>Why a train part may move relative to its car (so is not batched), or null if it never does.</summary>
		private static string NotStill(AnimatedObject element)
		{
			if (element.internalObject?.Prototype == null || element.States == null)
			{
				return "no object";
			}

			if (!Constant(element.StateFunction))
			{
				return element.States.Length != 1 ? "several states" : "state function";
			}

			/*
			 * Several states and no function (or a constant one): the state is fixed once the part
			 * initialises (ElementsGroup.Initialize leaves the last), and the batch copies whichever
			 * state the part shows.
			 */

			if (!Constant(element.TranslateXFunction) || !Constant(element.TranslateYFunction) || !Constant(element.TranslateZFunction))
			{
				return "moves";
			}

			if (!Constant(element.RotateXFunction) || !Constant(element.RotateYFunction) || !Constant(element.RotateZFunction))
			{
				return "rotates";
			}

			if (!Constant(element.ScaleXFunction) || !Constant(element.ScaleYFunction) || !Constant(element.ScaleZFunction))
			{
				return "scales";
			}

			if (element.TextureShiftXFunction != null || element.TextureShiftYFunction != null)
			{
				return "texture shift";
			}

			if (element.LEDFunction != null)
			{
				return "LED";
			}

			if (element.TrackFollowerFunction != null)
			{
				return "track follower";
			}

			if (element.ColorFunction != null)
			{
				return "colour function";
			}

			return element.isTimeTableObject ? "timetable" : null;
		}

		/// <summary>Why an object cannot be batched, or null if it can.</summary>
		private static string Ineligible(ObjectState state, bool moving = false)
		{
			StaticObject prototype = state.Prototype;
			if (prototype?.Mesh?.Faces == null || prototype.Mesh.Vertices == null || prototype.Mesh.Vertices.Length == 0)
			{
				return "empty";
			}

			/*
			 * Not excluded: prototype.Dynamic. An .animated file's [Include] marks the shared,
			 * cached object Dynamic - the same instance the route places as plain scenery - so the
			 * flag says nothing about a placement's mesh changing (only LED objects rewrite theirs,
			 * and they are never static placements); on the Tuen Ma Line it kept 4,632 objects out.
			 */

			if (state.Matricies != null && state.Matricies.Length > 0)
			{
				return "animated";
			}

			if (state.TextureTranslation != Matrix4D.Identity)
			{
				return "texture shift";
			}

			int faceVertices = 0;
			foreach (MeshFace face in prototype.Mesh.Faces)
			{
				faceVertices += face.Vertices.Length;
			}

			if (faceVertices > (moving ? MaxTrainPartFaceVertices : MaxPrototypeFaceVertices))
			{
				return "large";
			}

			foreach (VertexTemplate vertex in prototype.Mesh.Vertices)
			{
				if (vertex is AnimatedVertex)
				{
					return "animated vertices";
				}
			}

			return null;
		}

		// --- each frame ---

		/// <summary>
		/// Takes the faces batches will draw out of this frame's lists, and notes which batches are
		/// in view; asks for batches of groups coming into view. Call on the render thread with the
		/// lists about to be drawn.
		/// </summary>
		public void Filter(List<FaceState> opaque, List<FaceState> alphaUnsorted, List<FaceState> alphaSorted)
		{
			visible.Clear();
			frame++;
			// The debug views draw face by face, through upstream's RenderFace.
			if (!Enabled || host.Renderer.OptionWireFrame || host.Renderer.OptionNormals)
			{
				return;
			}

			Plan();
			if (groupOf.Count == 0)
			{
				return;
			}

			PlanTrains();
			if (DetachParts)
			{
				CullFarCars();
			}

			double now = clock.Elapsed.TotalSeconds;
			keptStatic = keptOther = 0;
			otherReasons.Clear();
			FilterList(opaque, now);
			FilterList(alphaUnsorted, now);
			FilterList(alphaSorted, now);
			foreach (Group group in visible)
			{
				if (group.Reference != null)
				{
					Follow(group);
				}
			}
			BuildSome();
			if (now - lastEvictionCheck > 2.0)
			{
				lastEvictionCheck = now;
				Evict(now);
			}
		}

		/// <summary>Faces left in this frame's lists: of static scenery (batched or not) and of everything else (trains, animated objects).</summary>
		private int keptStatic, keptOther;
		private readonly Dictionary<string, int> otherReasons = new Dictionary<string, int>();
		private HashSet<ObjectState> staticStates = new HashSet<ObjectState>();

		private void FilterList(List<FaceState> faces, double now)
		{
			int kept = 0;
			for (int i = 0; i < faces.Count; i++)
			{
				FaceState face = faces[i];
				if (groupOf.TryGetValue(face.Object, out Group group))
				{
					if (group.Batch != null)
					{
						if (group.SeenFrame != frame)
						{
							group.SeenFrame = frame;
							group.LastSeen = now;
							visible.Add(group);
						}

						if (group.Batch.Covered.Contains(face.Face.Vertices))
						{
							continue;
						}
					}
					else if (!group.Queued && !group.Failed)
					{
						group.Queued = true;
						toBuild.Enqueue(group);
					}
				}

				if (staticStates.Contains(face.Object))
				{
					keptStatic++;
				}
				else
				{
					keptOther++;
					string why = trainPartReason.TryGetValue(face.Object, out string reason) ? "train part: " + reason :
						!groupOf.TryGetValue(face.Object, out Group owner) ? "not a train part" :
						owner.Failed ? "train batch failed" :
						owner.Batch == null ? "train batch not built" : "train batch: face left alone";
					otherReasons[why] = otherReasons.TryGetValue(why, out int n) ? n + 1 : 1;
				}

				faces[kept++] = face;
			}

			faces.RemoveRange(kept, faces.Count - kept);
		}

		/// <summary>Draws the batches' opaque faces in view. Call in the opaque pass.</summary>
		public int DrawOpaque()
		{
			int calls = 0;
			foreach (Group group in visible)
			{
				calls += Draw(group.Batch, group.Batch.OpaqueFaces);
			}

			return calls;
		}

		/// <summary>Draws the batches' colour-keyed faces in view. Call in each transparent pass, with its state set.</summary>
		public int DrawAlpha()
		{
			int calls = 0;
			foreach (Group group in visible)
			{
				calls += Draw(group.Batch, group.Batch.AlphaFaces);
			}

			return calls;
		}

		private int Draw(Batch batch, int[] faces)
		{
			MeshFace[] meshFaces = batch.State.Prototype.Mesh.Faces;
			LibRender2.Shaders.Shader shader = host.Renderer.CurrentShader as LibRender2.Shaders.Shader;
			foreach (int index in faces)
			{
				// RenderFace draws as many indices as the face has vertices: a shared array of that length.
				MeshFace face = meshFaces[index];
				face.Vertices = host.VerticesOfLength(batch.FaceLengths[index]);
				host.DrawFace(shader, batch.State, face);
			}

			return faces.Length;
		}

		/// <summary>Places a train batch where its reference part is this frame.</summary>
		private static void Follow(Group group)
		{
			ObjectState batch = group.Batch.State, reference = group.Reference;
			batch.Scale = reference.Scale;
			batch.Rotate = reference.Rotate;
			batch.Translation = reference.Translation;
			batch.DaytimeNighttimeBlend = reference.DaytimeNighttimeBlend;
		}

		// --- building ---

		private void BuildSome()
		{
			if (toBuild.Count == 0)
			{
				return;
			}

			long start = clock.ElapsedTicks;
			long budget = (long)(BuildBudgetMilliseconds * Stopwatch.Frequency / 1000.0);
			while (toBuild.Count > 0 && clock.ElapsedTicks - start < budget && batchedVertices < MaxBatchedVertices)
			{
				Group group = toBuild.Dequeue();
				group.Queued = false;
				try
				{
					group.Batch = Build(group);
				}
				catch (Exception ex)
				{
					host.Log("static batch failed: " + ex.Message, true);
					group.Batch = null;
				}

				if (group.Batch == null)
				{
					group.Failed = true;
					GroupsFailed++;
					continue;
				}

				group.LastSeen = clock.Elapsed.TotalSeconds;
				batchedVertices += group.Batch.Vertices;
				built.Add(group);
				BatchesBuilt++;
				if (group.Reference != null && DetachParts)
				{
					Detach(group);
				}
			}
		}

		/// <summary>What a batched face is drawn with: everything RenderFace reads from its material and face.</summary>
		private readonly struct MaterialKey : IEquatable<MaterialKey>
		{
			public readonly bool Alpha;
			public readonly Texture Daytime, Nighttime;
			public readonly Color32 Color, Emissive;
			public readonly Color24 Specular;
			public readonly MaterialFlags Flags;
			public readonly OpenGlTextureWrapMode Wrap;
			public readonly bool DoubleSided;

			public MaterialKey(bool alpha, MeshMaterial material, bool doubleSided)
			{
				Alpha = alpha;
				Daytime = material.DaytimeTexture;
				Nighttime = material.NighttimeTexture;
				Color = material.Color;
				Emissive = material.EmissiveColor;
				Specular = material.SpecularColor;
				Flags = material.Flags;
				Wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;
				DoubleSided = doubleSided;
			}

			public bool Equals(MaterialKey other) =>
				Alpha == other.Alpha && ReferenceEquals(Daytime, other.Daytime) && ReferenceEquals(Nighttime, other.Nighttime) &&
				Color == other.Color && Emissive == other.Emissive && Specular == other.Specular && Flags == other.Flags && Wrap == other.Wrap &&
				DoubleSided == other.DoubleSided;

			public override bool Equals(object obj) => obj is MaterialKey other && Equals(other);

			public override int GetHashCode()
			{
				// By hand: .NET Framework (the desktop build) has no HashCode.Combine.
				unchecked
				{
					int hash = Alpha ? 1 : 0;
					hash = hash * 31 + (Daytime?.GetHashCode() ?? 0);
					hash = hash * 31 + (Nighttime?.GetHashCode() ?? 0);
					hash = hash * 31 + Color.GetHashCode();
					hash = hash * 31 + Emissive.GetHashCode();
					hash = hash * 31 + (int)Flags;
					hash = hash * 31 + (int)Wrap;
					return hash * 31 + (DoubleSided ? 1 : 0);
				}
			}
		}

		private sealed class Bucket
		{
			public MeshMaterial Material;
			public bool Alpha;
			public bool DoubleSided;
			public readonly List<VertexTemplate> Vertices = new List<VertexTemplate>();
			public readonly List<Vector3> Normals = new List<Vector3>();
		}

		/*
		 * How upstream's ObjectLibrary.ShowObject classifies a face: null if it must keep its own
		 * draw, false for the opaque list, true for the transparent list (colour-keyed textures in
		 * quality mode). Glow, additive and translucent-colour faces, alpha textures and faces whose
		 * texture has not been classified yet are left alone.
		 */
		private bool? Classify(MeshMaterial material)
		{
			if (material.GlowAttenuationData != 0)
			{
				CountReason("glow");
				return null;
			}

			if (material.BlendMode != MeshMaterialBlendMode.Normal)
			{
				CountReason("additive");
				return null;
			}

			if (material.Color.A != 255)
			{
				CountReason("translucent colour");
				return null;
			}

			// Upstream settles a wrap mode only for textured faces, and reads it only to bind a texture.
			if (!material.WrapMode.HasValue && (material.DaytimeTexture != null || material.NighttimeTexture != null))
			{
				CountReason("wrap mode unset");
				return null;
			}

			bool alpha = false;
			foreach (Texture texture in new[] { material.DaytimeTexture, material.NighttimeTexture })
			{
				if (texture == null)
				{
					continue;
				}

				if (!host.TryGetTransparencyType(texture, out TextureTransparencyType type))
				{
					CountReason("texture not classified");
					return null;
				}

				if (type == TextureTransparencyType.Alpha)
				{
					CountReason("alpha texture");
					return null;
				}

				if (type == TextureTransparencyType.Partial && host.QualityTransparency)
				{
					alpha = true;
				}
			}

			return alpha;
		}

		private Batch Build(Group group)
		{
			Dictionary<MaterialKey, Bucket> buckets = new Dictionary<MaterialKey, Bucket>();
			HashSet<MeshFaceVertex[]> covered = new HashSet<MeshFaceVertex[]>();
			HashSet<MeshFaceVertex[]> leftAlone = new HashSet<MeshFaceVertex[]>();

			// The batch's origin: the first member's position, in the shader's (Z-flipped) space.
			Matrix4D firstModel = group.Members[0].ModelMatrix;
			Vector3 origin = new Vector3(firstModel.Row3.X, firstModel.Row3.Y, firstModel.Row3.Z);
			// A train batch lives in its reference part's space, and is drawn with that part's matrix.
			bool moving = group.Reference != null;
			Matrix4D referenceInverse = moving ? Matrix4D.Invert(group.Reference.ModelMatrix) : Matrix4D.Identity;
			if (moving)
			{
				origin = Vector3.Zero;
			}

			int[] fan = new int[3];
			foreach (ObjectState member in group.Members)
			{
				Mesh mesh = member.Prototype.Mesh;
				Matrix4D model = moving ? member.ModelMatrix * referenceInverse : member.ModelMatrix;
				double[,] normalMatrix = InverseTranspose(model);
				foreach (MeshFace face in mesh.Faces)
				{
					if (leftAlone.Contains(face.Vertices))
					{
						continue;
					}

					MeshMaterial material = mesh.Materials[face.Material];
					bool? alpha = Classify(material);
					FaceFlags type = face.Flags & FaceFlags.FaceTypeMask;
					if (alpha == null || type == FaceFlags.QuadStrip || type == FaceFlags.NotSet || face.Vertices.Length < 3)
					{
						leftAlone.Add(face.Vertices);
						covered.Remove(face.Vertices);
						continue;
					}

					bool doubleSided = (face.Flags & FaceFlags.Face2Mask) != 0;
					MaterialKey key = new MaterialKey(alpha.Value, material, doubleSided);
					if (!buckets.TryGetValue(key, out Bucket bucket))
					{
						bucket = new Bucket { Material = material, Alpha = alpha.Value, DoubleSided = doubleSided };
						buckets[key] = bucket;
					}

					covered.Add(face.Vertices);
					MeshFaceVertex[] corners = face.Vertices;
					int triangles = type == FaceFlags.Triangles ? corners.Length / 3 : type == FaceFlags.Quads ? corners.Length / 4 * 2 : corners.Length - 2;
					for (int t = 0; t < triangles; t++)
					{
						switch (type)
						{
							case FaceFlags.Triangles:
								fan[0] = 3 * t;
								fan[1] = 3 * t + 1;
								fan[2] = 3 * t + 2;
								break;
							case FaceFlags.Quads:
								int q = 4 * (t / 2);
								fan[0] = q;
								fan[1] = (t & 1) == 0 ? q + 1 : q + 2;
								fan[2] = (t & 1) == 0 ? q + 2 : q + 3;
								break;
							case FaceFlags.TriangleStrip:
								fan[0] = (t & 1) == 0 ? t : t + 1;
								fan[1] = (t & 1) == 0 ? t + 1 : t;
								fan[2] = t + 2;
								break;
							default: // polygon: a fan
								fan[0] = 0;
								fan[1] = t + 1;
								fan[2] = t + 2;
								break;
						}

						for (int c = 0; c < 3; c++)
						{
							MeshFaceVertex corner = corners[fan[c]];
							VertexTemplate source = mesh.Vertices[corner.Index];
							VertexTemplate vertex = source.Clone();
							vertex.Coordinates = BakePosition(source.Coordinates, model, origin);
							bucket.Vertices.Add(vertex);
							bucket.Normals.Add(BakeNormal(corner.Normal, normalMatrix));
						}
					}
				}
			}

			if (buckets.Count == 0)
			{
				return null;
			}

			// One face per material: the merged mesh, as an ordinary upstream static object.
			List<VertexTemplate> vertices = new List<VertexTemplate>();
			List<MeshMaterial> materials = new List<MeshMaterial>();
			List<MeshFace> faces = new List<MeshFace>();
			List<int> opaqueFaces = new List<int>(), alphaFaces = new List<int>();
			foreach (Bucket bucket in buckets.Values)
			{
				MeshFaceVertex[] faceVertices = new MeshFaceVertex[bucket.Vertices.Count];
				for (int i = 0; i < faceVertices.Length; i++)
				{
					faceVertices[i] = new MeshFaceVertex(vertices.Count, bucket.Normals[i]);
					vertices.Add(bucket.Vertices[i]);
				}

				MeshFace merged = new MeshFace(faceVertices, (ushort)materials.Count)
				{
					Flags = FaceFlags.Triangles | (bucket.DoubleSided ? FaceFlags.Face2Mask : 0)
				};
				materials.Add(bucket.Material);
				(bucket.Alpha ? alphaFaces : opaqueFaces).Add(faces.Count);
				faces.Add(merged);
			}

			StaticObject prototype = new StaticObject(host.Host)
			{
				Dynamic = false
			};
			prototype.Mesh.Vertices = vertices.ToArray();
			prototype.Mesh.Materials = materials.ToArray();
			prototype.Mesh.Faces = faces.ToArray();
			VAOExtensions.CreateOrUpdateVAO(prototype.Mesh, false, host.Renderer.DefaultShader.VertexLayout, host.Renderer);
			if (!(prototype.Mesh.VAO is VertexArrayObject))
			{
				return null;
			}

			/*
			 * The GPU has the vertices now. RenderFace reads only the face's vertex count (glowing
			 * materials, which read positions, are never batched) and that the mesh has vertices,
			 * so the copies are dropped to keep the build memory from staying resident.
			 */
			prototype.Mesh.Vertices = new[] { vertices[0] };
			host.ReleaseVertexCopy(prototype.Mesh);
			int[] lengths = new int[prototype.Mesh.Faces.Length];
			for (int i = 0; i < prototype.Mesh.Faces.Length; i++)
			{
				lengths[i] = prototype.Mesh.Faces[i].Vertices.Length;
				prototype.Mesh.Faces[i].Vertices = Array.Empty<MeshFaceVertex>();
			}

			ObjectState state = new ObjectState(prototype)
			{
				Translation = Matrix4D.CreateTranslation(origin),
				DaytimeNighttimeBlend = group.Members[0].DaytimeNighttimeBlend,
				StartingDistance = group.Start,
				EndingDistance = group.End
			};
			if (moving)
			{
				state.Scale = group.Reference.Scale;
				state.Rotate = group.Reference.Rotate;
				state.Translation = group.Reference.Translation;
			}

			return new Batch
			{
				State = state,
				OpaqueFaces = opaqueFaces.ToArray(),
				AlphaFaces = alphaFaces.ToArray(),
				FaceLengths = lengths,
				Covered = covered,
				Vertices = vertices.Count
			};
		}

		/// <summary>A member's vertex in the batch's space: through its model matrix, in the shader's Z-flipped space, less the origin.</summary>
		private static Vector3 BakePosition(Vector3 p, Matrix4D m, Vector3 origin)
		{
			double x = p.X, y = p.Y, z = -p.Z;
			double wx = x * m.Row0.X + y * m.Row1.X + z * m.Row2.X + m.Row3.X - origin.X;
			double wy = x * m.Row0.Y + y * m.Row1.Y + z * m.Row2.Y + m.Row3.Y - origin.Y;
			double wz = x * m.Row0.Z + y * m.Row1.Z + z * m.Row2.Z + m.Row3.Z - origin.Z;
			return new Vector3(wx, wy, -wz);
		}

		/// <summary>A member's normal in the batch's space: through the inverse transpose of its model matrix, as the shader treats it.</summary>
		private static Vector3 BakeNormal(Vector3 n, double[,] inverseTranspose)
		{
			double x = n.X, y = n.Y, z = -n.Z;
			double nx = x * inverseTranspose[0, 0] + y * inverseTranspose[1, 0] + z * inverseTranspose[2, 0];
			double ny = x * inverseTranspose[0, 1] + y * inverseTranspose[1, 1] + z * inverseTranspose[2, 1];
			double nz = x * inverseTranspose[0, 2] + y * inverseTranspose[1, 2] + z * inverseTranspose[2, 2];
			double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
			if (length > 1e-12)
			{
				nx /= length;
				ny /= length;
				nz /= length;
			}

			return new Vector3(nx, ny, -nz);
		}

		/*
		 * For row vectors (v' = v M), normals transform by n' = n (M^-1)^T; this returns that
		 * matrix's 3x3 part, indexed [row, column].
		 */
		private static double[,] InverseTranspose(Matrix4D m)
		{
			double a = m.Row0.X, b = m.Row0.Y, c = m.Row0.Z;
			double d = m.Row1.X, e = m.Row1.Y, f = m.Row1.Z;
			double g = m.Row2.X, h = m.Row2.Y, i = m.Row2.Z;
			double co00 = e * i - f * h, co01 = -(d * i - f * g), co02 = d * h - e * g;
			double co10 = -(b * i - c * h), co11 = a * i - c * g, co12 = -(a * h - b * g);
			double co20 = b * f - c * e, co21 = -(a * f - c * d), co22 = a * e - b * d;
			double det = a * co00 + b * co01 + c * co02;
			if (Math.Abs(det) < 1e-20)
			{
				return new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
			}

			// inverse = adjugate / det, adjugate = cofactor^T; so (inverse)^T = cofactor / det.
			return new double[,]
			{
				{ co00 / det, co01 / det, co02 / det },
				{ co10 / det, co11 / det, co12 / det },
				{ co20 / det, co21 / det, co22 / det }
			};
		}

		// --- detaching ---

		/// <summary>Sets a group's parts to its original ones, less those detached (all of them if its car is out of range).</summary>
		private void Apply(LibRender2.Trains.ElementsGroup owner)
		{
			if (!originalElements.TryGetValue(owner, out AnimatedObject[] original))
			{
				originalElements[owner] = original = owner.Elements;
			}

			if (groupCar.TryGetValue(owner, out global::TrainManager.Car.CarBase car) && farCars.Contains(car))
			{
				owner.Elements = System.Array.Empty<AnimatedObject>();
				return;
			}

			List<AnimatedObject> kept = new List<AnimatedObject>(original.Length);
			foreach (AnimatedObject element in original)
			{
				if (!batchDetached.Contains(element))
				{
					kept.Add(element);
				}
			}

			owner.Elements = kept.Count == original.Length ? original : kept.ToArray();
		}

		/// <summary>Once a train batch is built: its members other than the reference that it covers entirely leave the update.</summary>
		private void Detach(Group group)
		{
			HashSet<LibRender2.Trains.ElementsGroup> owners = new HashSet<LibRender2.Trains.ElementsGroup>();
			foreach (ObjectState member in group.Members)
			{
				if (member == group.Reference || !partSource.TryGetValue(member, out var source) || !FullyCovered(member, group.Batch.Covered))
				{
					continue;
				}

				if (batchDetached.Add(source.Element))
				{
					host.Renderer.VisibleObjects.HideObject(member);
					owners.Add(source.Owner);
				}
			}

			foreach (LibRender2.Trains.ElementsGroup owner in owners)
			{
				Apply(owner);
			}

			group.Detached = owners.Count > 0;
		}

		/// <summary>Each frame, after upstream's update: cars out of range leave it, cars back in range rejoin it.</summary>
		private void CullFarCars()
		{
			foreach (KeyValuePair<global::TrainManager.Car.CarBase, List<LibRender2.Trains.ElementsGroup>> entry in carGroups)
			{
				bool far = !entry.Key.CurrentlyVisible;
				if (far == farCars.Contains(entry.Key))
				{
					continue;
				}

				if (far)
				{
					farCars.Add(entry.Key);
				}
				else
				{
					farCars.Remove(entry.Key);
				}

				foreach (LibRender2.Trains.ElementsGroup owner in entry.Value)
				{
					Apply(owner);
				}
			}
		}

		private static bool FullyCovered(ObjectState member, HashSet<MeshFaceVertex[]> covered)
		{
			foreach (MeshFace face in member.Prototype.Mesh.Faces)
			{
				if (!covered.Contains(face.Vertices))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Puts every part back into its car's update (hidden ones show again on the next update).</summary>
		private void ReattachAll()
		{
			foreach (KeyValuePair<LibRender2.Trains.ElementsGroup, AnimatedObject[]> entry in originalElements)
			{
				entry.Key.Elements = entry.Value;
			}

			originalElements.Clear();
			batchDetached.Clear();
			farCars.Clear();
			foreach (Group group in trainGroups)
			{
				group.Detached = false;
			}
		}

		// --- freeing ---

		private void Evict(double now)
		{
			for (int i = built.Count - 1; i >= 0; i--)
			{
				Group group = built[i];
				if (now - group.LastSeen > EvictAfterSeconds && !group.Detached)
				{
					Free(group);
					built.RemoveAt(i);
				}
			}
		}

		private void Free(Group group)
		{
			if (group.Batch == null)
			{
				return;
			}

			(group.Batch.State.Prototype.Mesh.VAO as VertexArrayObject)?.Dispose();
			batchedVertices -= group.Batch.Vertices;
			group.Batch = null;
			group.Failed = false;
			BatchesFreed++;
		}

		/// <summary>Frees every batch (the route changed, or the renderer is going).</summary>
		public void FreeAll()
		{
			ReattachAll();
			foreach (Group group in built)
			{
				Free(group);
			}

			built.Clear();
			toBuild.Clear();
			visible.Clear();
		}
	}
}
