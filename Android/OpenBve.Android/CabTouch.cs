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
using OpenBveApi.Runtime;
using LibRender2.Trains;
using OpenBveApi.Interface;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using TrainManager.Car;

namespace OpenBve.Android
{
	/// <summary>
	/// The cab's touch areas (panel2.cfg [Touch], panel.xml and panel.animated touch elements):
	/// a tap or mouse click on one works its commands, as upstream's Renderers.Touch.
	/// </summary>
	/// <remarks>
	/// Upstream finds the area under the pointer by drawing the areas into a float texture and
	/// reading the pixel back; GLES 3 does not promise either. The areas are a few flat shapes,
	/// so instead each frame they are projected with the matrices the cab was drawn with, and the
	/// UI thread tests a touch against those screen triangles at once - so it can tell straight
	/// away whether the touch is on a control or should turn the camera.
	/// </remarks>
	public sealed class CabTouch
	{
		private sealed class Area
		{
			public int Element;
			public float[] Triangles; // x0, y0, x1, y1, x2, y2 per triangle, in view pixels
		}

		private volatile Area[] areas = Array.Empty<Area>();
		private int group = -1;
		private TouchElement pressed;

		/// <summary>
		/// Render thread, once a frame after the cab is drawn: projects the touch areas of the panel
		/// screen on show, as upstream's Touch.PreRender chooses them.
		/// </summary>
		public void Update(AndroidTrainSession session, AndroidRenderer renderer, int width, int height)
		{
			TouchElement[] elements = Elements(session, renderer, out int add);
			if (elements == null || !renderer.CabMatricesValid)
			{
				areas = Array.Empty<Area>();
				return;
			}

			group = add;
			List<Area> projected = new List<Area>();
			for (int i = 0; i < elements.Length; i++)
			{
				ObjectState state = elements[i].Element.internalObject;
				if (state?.Prototype?.Mesh?.Faces == null)
				{
					continue;
				}

				// As LibRender2's RenderFace for the cab: model, camera translation, view, projection.
				Matrix4D transform = state.ModelMatrix * renderer.CabCameraTranslation * renderer.CabView * renderer.CabProjection;
				List<float> triangles = new List<float>();
				foreach (MeshFace face in state.Prototype.Mesh.Faces)
				{
					bool list = (face.Flags & FaceFlags.FaceTypeMask) == FaceFlags.Triangles;
					int n = face.Vertices.Length;
					for (int k = 0; k + 2 < n; k += list ? 3 : 1)
					{
						// A triangle list as given, anything else as a fan from its first vertex.
						int a = list ? k : 0, b = k + 1, c = k + 2;
						if (Project(state, face, a, transform, width, height, out float ax, out float ay) &&
						    Project(state, face, b, transform, width, height, out float bx, out float by) &&
						    Project(state, face, c, transform, width, height, out float cx, out float cy))
						{
							triangles.AddRange(new[] { ax, ay, bx, by, cx, cy });
						}
					}
				}

				if (triangles.Count > 0)
				{
					projected.Add(new Area { Element = i, Triangles = triangles.ToArray() });
				}
			}

			areas = projected.ToArray();
			// Where the areas are, once per change of panel screen or of their number: the first question when a touch does nothing.
			string key = add + ":" + projected.Count + ":" + width + "x" + height;
			if (key != logged)
			{
				logged = key;
				foreach (Area area in projected)
				{
					float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
					for (int t = 0; t + 1 < area.Triangles.Length; t += 2)
					{
						x0 = Math.Min(x0, area.Triangles[t]);
						x1 = Math.Max(x1, area.Triangles[t]);
						y0 = Math.Min(y0, area.Triangles[t + 1]);
						y1 = Math.Max(y1, area.Triangles[t + 1]);
					}

					string commands = string.Join(",", Array.ConvertAll(elements[area.Element].ControlIndices,
						i => i >= 0 && i < session.TouchControls.Length ? session.TouchControls[i].Command.ToString() : "?"));
					global::Android.Util.Log.Debug("OpenBVE", "cab touch area " + area.Element + " (" + commands + "): " +
					                                         (int)x0 + "," + (int)y0 + " - " + (int)x1 + "," + (int)y1 + " of " + width + "x" + height);
				}
			}
		}

		private string logged;

		/// <summary>UI thread: the touch area under a point (view pixels), or -1.</summary>
		/// <param name="slop">How near a miss still counts, in pixels: a fingertip is wider than a mouse pointer.</param>
		public int HitTest(float x, float y, float slop)
		{
			Area[] current = areas;
			int nearest = -1;
			float best = slop;
			foreach (Area area in current)
			{
				for (int t = 0; t + 5 < area.Triangles.Length; t += 6)
				{
					float d = Distance(x, y, area.Triangles, t);
					if (d <= 0.0f)
					{
						return area.Element;
					}

					if (d < best)
					{
						best = d;
						nearest = area.Element;
					}
				}
			}

			return nearest;
		}

		/// <summary>Render thread: a touch area pressed, as upstream's Touch.TouchCheck.</summary>
		public void Press(AndroidTrainSession session, AndroidControls controls, int element)
		{
			TouchElement[] elements = Elements(session, Program.Renderer as AndroidRenderer, out int add);
			if (elements == null || element < 0 || element >= elements.Length || add != group)
			{
				return;
			}

			pressed = elements[element];
			foreach (int index in pressed.ControlIndices)
			{
				if (index < 0 || index >= session.TouchControls.Length)
				{
					continue;
				}

				session.Train.Plugin?.TouchEvent(add, index);
				controls.Press(session.TouchControls[index].Command, exact: true);
			}
		}

		/// <summary>Render thread: the touch lifted, as upstream's Touch.LeaveCheck.</summary>
		public void Release(AndroidTrainSession session, AndroidControls controls)
		{
			TouchElement element = pressed;
			pressed = null;
			if (element == null)
			{
				return;
			}

			foreach (int index in element.ControlIndices)
			{
				if (index >= 0 && index < session.TouchControls.Length)
				{
					controls.Release(session.TouchControls[index].Command, exact: true);
				}
			}

			CarBase car = session.Train.Cars[session.Train.DriverCar];
			if (!car.CarSections.ContainsKey(CarSectionType.Interior))
			{
				return;
			}

			// Another panel screen, then the touch sounds, as upstream.
			car.CarSections[CarSectionType.Interior].CurrentAdditionalGroup = element.JumpScreenIndex;
			car.ChangeCarSection(CarSectionType.Interior, false, true);
			foreach (int index in element.SoundIndices)
			{
				if (car.Sounds.Touch.ContainsKey(index))
				{
					car.Sounds.Touch[index].Play(car, false);
				}
			}
		}

		/// <summary>The touch elements of the panel screen on show, or null (not in the cab, or none).</summary>
		private static TouchElement[] Elements(AndroidTrainSession session, AndroidRenderer renderer, out int add)
		{
			add = -1;
			if (session?.Train == null || renderer == null ||
			    renderer.Camera.CurrentMode != CameraViewMode.Interior && renderer.Camera.CurrentMode != CameraViewMode.InteriorLookAhead)
			{
				return null;
			}

			CarBase car = session.Train.Cars[session.Train.DriverCar];
			if (!car.CarSections.ContainsKey(CarSectionType.Interior))
			{
				return null;
			}

			CarSection section = car.CarSections[CarSectionType.Interior];
			add = section.CurrentAdditionalGroup + 1;
			return add < section.Groups.Length ? section.Groups[add].TouchElements : null;
		}

		private static bool Project(ObjectState state, MeshFace face, int vertex, Matrix4D transform, int width, int height, out float x, out float y)
		{
			Vector3 p = state.Prototype.Mesh.Vertices[face.Vertices[vertex].Index].Coordinates;
			Vector4 clip = Vector4.Transform(new Vector4(p.X, p.Y, p.Z, 1.0), transform);
			if (clip.W <= 1e-6)
			{
				x = y = 0.0f;
				return false;
			}

			x = (float)((clip.X / clip.W * 0.5 + 0.5) * width);
			y = (float)((0.5 - clip.Y / clip.W * 0.5) * height);
			return true;
		}

		/// <summary>0 inside the triangle, else the distance to its nearest edge.</summary>
		private static float Distance(float px, float py, float[] t, int o)
		{
			float ax = t[o], ay = t[o + 1], bx = t[o + 2], by = t[o + 3], cx = t[o + 4], cy = t[o + 5];
			float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
			float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
			float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
			bool negative = d1 < 0 || d2 < 0 || d3 < 0;
			bool positive = d1 > 0 || d2 > 0 || d3 > 0;
			if (!(negative && positive))
			{
				return 0.0f;
			}

			return Math.Min(Segment(px, py, ax, ay, bx, by), Math.Min(Segment(px, py, bx, by, cx, cy), Segment(px, py, cx, cy, ax, ay)));
		}

		private static float Segment(float px, float py, float ax, float ay, float bx, float by)
		{
			float dx = bx - ax, dy = by - ay;
			float length = dx * dx + dy * dy;
			float t = length <= 0 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / length));
			float ex = ax + t * dx - px, ey = ay + t * dy - py;
			return (float)Math.Sqrt(ex * ex + ey * ey);
		}
	}
}
