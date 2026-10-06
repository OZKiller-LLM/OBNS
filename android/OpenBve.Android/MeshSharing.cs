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
using System.Runtime.CompilerServices;
using LibRender2.Trains;
using OpenBveApi.Objects;
using OpenBveApi.Trains;
using TrainManager.Car;
using TrainManager.Trains;

namespace OpenBve.Android
{
	/*
	 * Identical train meshes, shared. Every object the host hands out from its cache is a deep copy
	 * (upstream's HostInterface.LoadObject), so each car of each train carries its own copy of every
	 * part it shows: on the Tuen Ma Line, with 36 scripted trains, 11,197 of the 15,745 meshes were
	 * exact copies of another - ~160 MB of the ~240 MB of geometry, on phones where 4 GB is not
	 * enough. Here each train part's StaticObject keeps its identity (and each car its own states,
	 * positions and animation), but objects whose meshes are identical down to every vertex, face
	 * and material point at one Mesh, and the copies are left to the collector.
	 *
	 * Left alone: the cab (overlay sections, whose 2D panel elements rewrite their meshes), elements
	 * with a colour function (which write their material colour every frame), LED elements
	 * (UpdateVAO: rebuilt every frame), and any mesh already on the GPU (its buffers and face
	 * offsets are its own). Runs on the scripted trains and the route's animated objects (whose
	 * every placement is a copy too) before the scene is prepared, and on the player's and
	 * preceding trains as soon as they load - all before their first upload.
	 */
	internal static class MeshSharing
	{
		public static bool Enabled = true;

		private static readonly Dictionary<ulong, List<Mesh>> canonical = new Dictionary<ulong, List<Mesh>>();
		private static int shared, onGpu;
		private static long sharedBytes;

		public static string Describe() => !Enabled ? "mesh sharing off" :
			"mesh sharing: " + shared + " meshes shared, ~" + (sharedBytes >> 20) + " MB" + (onGpu > 0 ? ", " + onGpu + " left (already on the GPU)" : string.Empty);

		/// <summary>Forgets the meshes of a previous game.</summary>
		public static void Reset()
		{
			canonical.Clear();
			shared = 0;
			onGpu = 0;
			sharedBytes = 0;
		}

		/// <summary>Shares identical meshes among the given trains' parts.</summary>
		public static void Share(IEnumerable<AbstractTrain> trains)
		{
			if (!Enabled)
			{
				return;
			}

			foreach (AbstractTrain abstractTrain in trains)
			{
				if (!(abstractTrain is TrainBase train) || train.Cars == null)
				{
					continue;
				}

				foreach (CarBase car in train.Cars)
				{
					if (car?.CarSections != null)
					{
						foreach (CarSection section in car.CarSections.Values)
						{
							Share(section);
						}
					}

					ShareAll(car?.FrontBogie?.CarSections);
					ShareAll(car?.RearBogie?.CarSections);
				}
			}
		}

		/// <summary>Shares identical meshes among the route's animated objects (signals, doors, scripted trains' track followers...).</summary>
		public static void Share(WorldObject[] objects, int count)
		{
			if (!Enabled || objects == null)
			{
				return;
			}

			for (int i = 0; i < Math.Min(count, objects.Length); i++)
			{
				Share(objects[i]?.Object);
			}
		}

		private static void ShareAll(CarSection[] sections)
		{
			if (sections == null)
			{
				return;
			}

			foreach (CarSection section in sections)
			{
				Share(section);
			}
		}

		private static void Share(CarSection section)
		{
			if (section?.Groups == null || section.Type == ObjectType.Overlay)
			{
				return;
			}

			foreach (ElementsGroup group in section.Groups)
			{
				if (group?.Elements == null)
				{
					continue;
				}

				foreach (AnimatedObject element in group.Elements)
				{
					Share(element);
				}
			}
		}

		private static void Share(AnimatedObject element)
		{
			if (element?.States == null || element.ColorFunction != null || element.UpdateVAO)
			{
				return;
			}

			foreach (ObjectState state in element.States)
			{
				Share(state?.Prototype);
			}
		}

		private static void Share(StaticObject prototype)
		{
			Mesh mesh = prototype?.Mesh;
			if (mesh?.Vertices == null || mesh.Faces == null || mesh.Materials == null)
			{
				return;
			}

			if (mesh.VAO != null || mesh.NormalsVAO != null)
			{
				onGpu++;
				return;
			}

			ulong hash = Hash(mesh);
			if (!canonical.TryGetValue(hash, out List<Mesh> candidates))
			{
				canonical[hash] = new List<Mesh> { mesh };
				return;
			}

			foreach (Mesh candidate in candidates)
			{
				if (ReferenceEquals(candidate, mesh))
				{
					return;
				}

				if (Same(candidate, mesh))
				{
					prototype.Mesh = candidate;
					shared++;
					sharedBytes += Bytes(mesh);
					return;
				}
			}

			candidates.Add(mesh);
		}

		private static long Bytes(Mesh mesh)
		{
			long corners = 0;
			foreach (MeshFace face in mesh.Faces)
			{
				corners += face.Vertices?.Length ?? 0;
			}

			return mesh.Vertices.Length * 56L + corners * 32L + mesh.Faces.Length * 48L;
		}

		private static ulong Hash(Mesh mesh)
		{
			ulong hash = 14695981039346656037UL;
			void Mix(ulong value)
			{
				hash = (hash ^ value) * 1099511628211UL;
				hash ^= hash >> 29;
			}

			Mix((ulong)mesh.Vertices.Length);
			foreach (VertexTemplate vertex in mesh.Vertices)
			{
				if (vertex == null)
				{
					Mix(0);
					continue;
				}

				Mix((ulong)BitConverter.DoubleToInt64Bits(vertex.Coordinates.X));
				Mix((ulong)BitConverter.DoubleToInt64Bits(vertex.Coordinates.Y));
				Mix((ulong)BitConverter.DoubleToInt64Bits(vertex.Coordinates.Z));
				Mix((ulong)BitConverter.DoubleToInt64Bits(vertex.TextureCoordinates.X));
				Mix((ulong)BitConverter.DoubleToInt64Bits(vertex.TextureCoordinates.Y));
			}

			Mix((ulong)mesh.Faces.Length);
			foreach (MeshFace face in mesh.Faces)
			{
				Mix((ulong)face.Flags);
				Mix(face.Material);
				Mix((ulong)(face.Vertices?.Length ?? 0));
			}

			Mix((ulong)mesh.Materials.Length);
			return hash;
		}

		/// <summary>Whether two meshes are identical in everything that is drawn.</summary>
		private static bool Same(Mesh a, Mesh b)
		{
			if (a.Vertices.Length != b.Vertices.Length || a.Faces.Length != b.Faces.Length || a.Materials.Length != b.Materials.Length ||
			    a.VAO != null && b.VAO != null)
			{
				return false;
			}

			for (int i = 0; i < a.Vertices.Length; i++)
			{
				if (!Same(a.Vertices[i], b.Vertices[i]))
				{
					return false;
				}
			}

			for (int i = 0; i < a.Faces.Length; i++)
			{
				MeshFace fa = a.Faces[i], fb = b.Faces[i];
				if (fa.Flags != fb.Flags || fa.Material != fb.Material || (fa.Vertices == null) != (fb.Vertices == null))
				{
					return false;
				}

				if (fa.Vertices == null)
				{
					continue;
				}

				if (fa.Vertices.Length != fb.Vertices.Length)
				{
					return false;
				}

				for (int j = 0; j < fa.Vertices.Length; j++)
				{
					if (fa.Vertices[j].Index != fb.Vertices[j].Index || fa.Vertices[j].Normal != fb.Vertices[j].Normal)
					{
						return false;
					}
				}
			}

			for (int i = 0; i < a.Materials.Length; i++)
			{
				MeshMaterial ma = a.Materials[i], mb = b.Materials[i];
				if (ma.Flags != mb.Flags || ma.Color != mb.Color || ma.TransparentColor != mb.TransparentColor ||
				    ma.EmissiveColor != mb.EmissiveColor || ma.SpecularColor != mb.SpecularColor ||
				    !ReferenceEquals(ma.DaytimeTexture, mb.DaytimeTexture) || !ReferenceEquals(ma.NighttimeTexture, mb.NighttimeTexture) ||
				    !ReferenceEquals(ma.LightMapTexture, mb.LightMapTexture) || ma.BlendMode != mb.BlendMode ||
				    ma.GlowAttenuationData != mb.GlowAttenuationData || ma.WrapMode != mb.WrapMode)
				{
					return false;
				}
			}

			return BoundingBoxesEqual(a.BoundingBox, b.BoundingBox);
		}

		private static bool BoundingBoxesEqual(OpenBveApi.Math.Vector3[] a, OpenBveApi.Math.Vector3[] b)
		{
			if (a == null || b == null)
			{
				return a == b;
			}

			if (a.Length != b.Length)
			{
				return false;
			}

			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i])
				{
					return false;
				}
			}

			return true;
		}

		private static bool Same(VertexTemplate a, VertexTemplate b)
		{
			if (a == null || b == null)
			{
				return a == b;
			}

			if (a.GetType() != b.GetType() || a.Coordinates != b.Coordinates || a.TextureCoordinates != b.TextureCoordinates)
			{
				return false;
			}

			switch (a)
			{
				case ColoredVertex ca:
					return ca.Color == ((ColoredVertex)b).Color;
				case LightMappedVertex la:
					return la.LightMapCoordinates == ((LightMappedVertex)b).LightMapCoordinates;
				case AnimatedVertex aa:
					int[] chainA = aa.MatrixChain, chainB = ((AnimatedVertex)b).MatrixChain;
					if (chainA == null || chainB == null)
					{
						return chainA == chainB;
					}

					if (chainA.Length != chainB.Length)
					{
						return false;
					}

					for (int i = 0; i < chainA.Length; i++)
					{
						if (chainA[i] != chainB[i])
						{
							return false;
						}
					}

					return true;
				default:
					return a is Vertex;
			}
		}
	}
}
