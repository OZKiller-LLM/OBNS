// OBNS: a copy of upstream's source/OpenBVE/Graphics/Renderers/Touch.cs (OpenBVE 1.14.1.2), laid over the desktop
// build's mirror (OBNS/build-desktop.ps1): touch areas picked without OpenGL when Vulkan draws the whole game (OBNS/RENDERERS.md).
// On an upstream update: copy the file again and reapply the parts marked OBNS.
using System.Collections.Generic;
using System.Linq;
using LibRender2;
using LibRender2.Trains;
using OpenBveApi.Interface;
using OpenBveApi.Objects;
using OpenBveApi.Runtime;
using OpenTK.Graphics.OpenGL;
using TrainManager.Car;
using Vector2 = OpenTK.Vector2;

namespace OpenBve.Graphics.Renderers
{
    internal class Touch
    {
        private readonly NewRenderer renderer;
        private readonly List<ObjectState> touchableObject;
        private readonly FrameBufferObject fbo;
        private ObjectState prePickedObject;

        internal Touch(NewRenderer renderer)
        {
            this.renderer = renderer;
            touchableObject = new List<ObjectState>();
            fbo = new FrameBufferObject();
            fbo.Bind();
            fbo.SetTextureBuffer(FrameBufferObject.TargetBuffer.Color, PixelInternalFormat.R32f, PixelFormat.Red, PixelType.Float, renderer.Screen.Width, renderer.Screen.Height);
            fbo.DrawBuffers(new[] { DrawBuffersEnum.ColorAttachment0 });
            fbo.UnBind();
        }

        internal void UpdateViewport()
        {
            fbo.Bind();
            fbo.SetTextureBuffer(FrameBufferObject.TargetBuffer.Color, PixelInternalFormat.R32f, PixelFormat.Red, PixelType.Float, renderer.Screen.Width, renderer.Screen.Height);
            fbo.DrawBuffers(new[] { DrawBuffersEnum.ColorAttachment0 });
            fbo.UnBind();
        }

        private void ShowObject(ObjectState state)
        {
            touchableObject.Add(state);
        }

        private void PreRender()
        {
            touchableObject.Clear();

            if (!Loading.SimulationSetup || TrainManager.PlayerTrain == null)
            {
                return;
            }

            CarBase Car = TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.DriverCar];

            if (renderer.Camera.CurrentMode != CameraViewMode.Interior && renderer.Camera.CurrentMode != CameraViewMode.InteriorLookAhead || !Car.CarSections.ContainsKey(CarSectionType.Interior))
            {
                return;
            }

            
            int add = Car.CarSections[CarSectionType.Interior].CurrentAdditionalGroup + 1;

            if (add >= Car.CarSections[CarSectionType.Interior].Groups.Length)
            {
                return;
            }

            TouchElement[] TouchElements = Car.CarSections[CarSectionType.Interior].Groups[add].TouchElements;

            if (TouchElements == null)
            {
                return;
            }

            foreach (TouchElement element in TouchElements)
            {
                ShowObject(element.Element.internalObject);
            }
        }

        internal void RenderScene()
        {
            PreRender();

            if (!touchableObject.Any())
            {
                //Drop out early if the pre-render process reveals no available touch faces for a minor boost
                return;
            }

            // OBNS: what the faces are drawn with, for picking them without drawing (Pick).
            pickProjection = renderer.CurrentProjectionMatrix;
            pickView = renderer.CurrentViewMatrix;
            pickCamera = renderer.Camera.TranslationMatrix;
            if (OpenBve.Graphics.Vulkan.VulkanDevice.Full)
            {
                return;
            }

            renderer.ResetOpenGlState();

            fbo.Bind();
            GL.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.pickingShader.Activate();
            renderer.pickingShader.SetCurrentProjectionMatrix(renderer.CurrentProjectionMatrix);

            for (int i = 0; i < touchableObject.Count; i++)
            {
                renderer.pickingShader.SetObjectIndex(i + 1);

                foreach (MeshFace face in touchableObject[i].Prototype.Mesh.Faces)
                {
                    renderer.RenderFace(renderer.pickingShader, touchableObject[i], face);
                }
            }

            //Must deactivate and unbind here
            renderer.pickingShader.Deactivate();
            fbo.UnBind();
            ComparePicks();

            // for debug
            if (renderer.DebugTouchMode)
            {
                GL.DepthMask(false);
                GL.Disable(EnableCap.DepthTest);

                renderer.DefaultShader.Activate();
                renderer.ResetShader(renderer.DefaultShader);
                renderer.DefaultShader.SetCurrentProjectionMatrix(renderer.CurrentProjectionMatrix);

                foreach (ObjectState objectState in touchableObject)
                {
                    foreach (MeshFace face in objectState.Prototype.Mesh.Faces)
                    {
                        renderer.RenderFace(renderer.DefaultShader, objectState, face, true);
                    }
                }
                renderer.DefaultShader.Deactivate();
            }
        }

        // OBNS: picking without OpenGL's framebuffer, with Vulkan drawing the whole game (OBNS/RENDERERS.md).
        private OpenBveApi.Math.Matrix4D pickProjection, pickView, pickCamera;

        private static readonly bool comparePicks = System.Environment.GetEnvironmentVariable("OBNS_TOUCH_CHECK") == "1";
        private int comparedFrames;

        /// <summary>
        /// OBNS: with OBNS_TOUCH_CHECK=1, a grid of the screen picked both ways now and then, for the log: <see cref="Pick"/> is right if it
        /// finds what OpenGL's framebuffer holds.
        /// </summary>
        private void ComparePicks()
        {
            if (!comparePicks || ++comparedFrames % 300 != 1)
            {
                return;
            }

            int same = 0, different = 0, hits = 0;
            for (int y = 3; y < renderer.Screen.Height - 3; y += 3)
            {
                for (int x = 3; x < renderer.Screen.Width - 3; x += 3)
                {
                    Vector2 point = new Vector2(x, y);
                    ObjectState drawn = ParseFBO(point, 5, 5), picked = Pick(point, 5, 5);
                    if (drawn != null)
                    {
                        hits++;
                    }

                    if (drawn == picked)
                    {
                        same++;
                    }
                    else
                    {
                        different++;
                    }
                }
            }

            Program.FileSystem.AppendToLogFile("OBNS touch check: " + touchableObject.Count + " objects, " + hits + " points on them; picked as OpenGL's framebuffer has it: " + same + ", differently: " + different);
        }

        /// <summary>OBNS: where a vertex comes out on the screen (OpenGL's window coordinates), for a matrix taking it to clip space.</summary>
        private bool ToWindow(OpenBveApi.Math.Vector3 p, ref OpenBveApi.Math.Matrix4D m, out double x, out double y)
        {
            // Upstream's shader negates Z; the matrices are row-major with row vectors.
            double vx = p.X, vy = p.Y, vz = -p.Z;
            double cx = vx * m.Row0.X + vy * m.Row1.X + vz * m.Row2.X + m.Row3.X;
            double cy = vx * m.Row0.Y + vy * m.Row1.Y + vz * m.Row2.Y + m.Row3.Y;
            double cw = vx * m.Row0.W + vy * m.Row1.W + vz * m.Row2.W + m.Row3.W;
            x = y = 0.0;
            if (cw <= 0.0)
            {
                return false;
            }

            x = (cx / cw + 1.0) * 0.5 * renderer.Screen.Width;
            y = (cy / cw + 1.0) * 0.5 * renderer.Screen.Height;
            return true;
        }

        /// <summary>
        /// OBNS: what <see cref="ParseFBO"/> would find, worked out from the faces: the same pixels in the same order, and in each the last
        /// face covering it (upstream's framebuffer has no depth, so the last drawn is what it holds), culled as upstream culls them.
        /// </summary>
        private ObjectState Pick(Vector2 point, int deltaX, int deltaY)
        {
            Vector2 topLeft = point - new Vector2(deltaX, deltaY) / 2.0f;
            int left = (int)topLeft.X, bottom = renderer.Screen.Height - (int)topLeft.Y;
            System.Collections.Generic.List<double[]> triangles = new System.Collections.Generic.List<double[]>();
            for (int i = 0; i < touchableObject.Count; i++)
            {
                ObjectState state = touchableObject[i];
                OpenBveApi.Math.Matrix4D matrix = state.ModelMatrix * pickCamera * pickView * pickProjection;
                Mesh mesh = state.Prototype.Mesh;
                foreach (MeshFace face in mesh.Faces)
                {
                    bool cull = renderer.OptionBackFaceCulling && (face.Flags & FaceFlags.Face2Mask) == 0;
                    FaceFlags type = face.Flags & FaceFlags.FaceTypeMask;
                    int count = face.Vertices.Length;
                    double[] xs = new double[count], ys = new double[count];
                    bool visible = true;
                    for (int k = 0; k < count; k++)
                    {
                        visible &= ToWindow(mesh.Vertices[face.Vertices[k].Index].Coordinates, ref matrix, out xs[k], out ys[k]);
                    }

                    if (!visible)
                    {
                        continue;
                    }

                    void Add(int a, int b, int c)
                    {
                        double area = (xs[b] - xs[a]) * (ys[c] - ys[a]) - (xs[c] - xs[a]) * (ys[b] - ys[a]);
                        // OpenGL's front faces (counter-clockwise on its window) are culled.
                        if (!cull || area < 0.0)
                        {
                            triangles.Add(new[] { i, xs[a], ys[a], xs[b], ys[b], xs[c], ys[c] });
                        }
                    }

                    switch (type)
                    {
                        case FaceFlags.Triangles:
                            for (int k = 0; k + 2 < count; k += 3) Add(k, k + 1, k + 2);
                            break;
                        case FaceFlags.TriangleStrip:
                            for (int k = 0; k + 2 < count; k++)
                            {
                                if ((k & 1) == 0) Add(k, k + 1, k + 2);
                                else Add(k + 1, k, k + 2);
                            }

                            break;
                        case FaceFlags.Quads:
                            for (int k = 0; k + 3 < count; k += 4)
                            {
                                Add(k, k + 1, k + 2);
                                Add(k, k + 2, k + 3);
                            }

                            break;
                        case FaceFlags.QuadStrip:
                            for (int k = 0; k + 3 < count; k += 2)
                            {
                                Add(k, k + 1, k + 3);
                                Add(k, k + 3, k + 2);
                            }

                            break;
                        default:
                            for (int k = 1; k + 1 < count; k++) Add(0, k, k + 1);
                            break;
                    }
                }
            }

            for (int row = 0; row < deltaY; row++)
            {
                for (int column = 0; column < deltaX; column++)
                {
                    double px = left + column + 0.5, py = bottom + row + 0.5;
                    int found = -1;
                    foreach (double[] t in triangles)
                    {
                        double d0 = (t[3] - t[1]) * (py - t[2]) - (t[4] - t[2]) * (px - t[1]);
                        double d1 = (t[5] - t[3]) * (py - t[4]) - (t[6] - t[4]) * (px - t[3]);
                        double d2 = (t[1] - t[5]) * (py - t[6]) - (t[2] - t[6]) * (px - t[5]);
                        if ((d0 >= 0.0 && d1 >= 0.0 && d2 >= 0.0) || (d0 <= 0.0 && d1 <= 0.0 && d2 <= 0.0))
                        {
                            found = (int)t[0];
                        }
                    }

                    if (found >= 0)
                    {
                        return touchableObject[found];
                    }
                }
            }

            return null;
        }

        private ObjectState ParseFBO(Vector2 point, int deltaX, int deltaY)
        {
            if (OpenBve.Graphics.Vulkan.VulkanDevice.Full)
            {
                return Pick(point, deltaX, deltaY);
            }

            Vector2 topLeft = point - new Vector2(deltaX, deltaY) / 2.0f;
            float[,] objectIndices = new float[deltaX, deltaY];

            fbo.Bind();
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.ReadPixels((int)topLeft.X, renderer.Screen.Height - (int)topLeft.Y, deltaX, deltaY, PixelFormat.Red, PixelType.Float, objectIndices);
            GL.ReadBuffer(ReadBufferMode.None);
            fbo.UnBind();

            foreach (float objectIndex in objectIndices)
            {
                int index = (int)objectIndex - 1;

                if (index >= 0 && index < touchableObject.Count)
                {
                    return touchableObject[index];
                }
            }

            return null;
        }



        internal bool MoveCheck(Vector2 Point, out MouseCursor.Status Status, out MouseCursor NewCursor)
        {
            NewCursor = null;

            if (!Loading.SimulationSetup)
            {
                Status = MouseCursor.Status.Default;
                return false;
            }

            CarBase Car = TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.DriverCar];

			if (renderer.Camera.CurrentMode != CameraViewMode.Interior && renderer.Camera.CurrentMode != CameraViewMode.InteriorLookAhead || !Car.CarSections.ContainsKey(CarSectionType.Interior))
            {
                Status = MouseCursor.Status.Default;
                return false;
            }

            Status = MouseCursor.Status.Default;

            
            int add = Car.CarSections[CarSectionType.Interior].CurrentAdditionalGroup + 1;

            if (add >= Car.CarSections[CarSectionType.Interior].Groups.Length)
            {
                return false;
            }

            TouchElement[] TouchElements = Car.CarSections[CarSectionType.Interior].Groups[add].TouchElements;

            if (TouchElements == null)
            {
                return false;
            }

            ObjectState pickedObject = ParseFBO(Point, 5, 5);

            foreach (TouchElement TouchElement in TouchElements.Where(x => x.Element.internalObject == pickedObject))
            {
                if (TouchElement.MouseCursor != null)
                {
                    Status = MouseCursor.Status.Default;
                    NewCursor = TouchElement.MouseCursor;
                    return true;
                }
                foreach (int index in TouchElement.ControlIndices)
                {
                    switch (Interface.CurrentControls[index].Command)
                    {
                        case Translations.Command.PowerIncrease:
                        case Translations.Command.BrakeIncrease:
                        case Translations.Command.ReverserForward:
                            Status = MouseCursor.Status.Plus;
                            break;
                        case Translations.Command.PowerDecrease:
                        case Translations.Command.BrakeDecrease:
                        case Translations.Command.ReverserBackward:
                            Status = MouseCursor.Status.Minus;
                            break;
                    }
                }
            }
            return pickedObject != null;
        }

        internal void TouchCheck(Vector2 Point)
        {
            if (!Loading.SimulationSetup)
            {
                return;
            }

            CarBase Car = TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.DriverCar];

			if (renderer.Camera.CurrentMode != CameraViewMode.Interior && renderer.Camera.CurrentMode != CameraViewMode.InteriorLookAhead || !Car.CarSections.ContainsKey(CarSectionType.Interior))
            {
                return;
            }

            int add = Car.CarSections[CarSectionType.Interior].CurrentAdditionalGroup + 1;

            if (add >= Car.CarSections[CarSectionType.Interior].Groups.Length)
            {
                return;
            }

            TouchElement[] TouchElements = Car.CarSections[0].Groups[add].TouchElements;

            if (TouchElements == null)
            {
                return;
            }

            ObjectState pickedObject = ParseFBO(Point, 5, 5);

            foreach (TouchElement TouchElement in TouchElements.Where(x => x.Element.internalObject == pickedObject))
            {
                foreach (int index in TouchElement.ControlIndices)
                {
                    if (Interface.CurrentControls[index].DigitalState != DigitalControlState.Pressed && TrainManager.PlayerTrain.Plugin != null)
                    {
                        TrainManager.PlayerTrain.Plugin.TouchEvent(add, index);
                    }
                    Interface.CurrentControls[index].AnalogState = 1.0;
                    Interface.CurrentControls[index].DigitalState = DigitalControlState.Pressed;
                    MainLoop.AddControlRepeat(index);
                }
            }

            prePickedObject = pickedObject;
        }

        internal void LeaveCheck(Vector2 Point)
        {
            if (!Loading.SimulationSetup)
            {
                return;
            }

            CarBase Car = TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.DriverCar];

			if (renderer.Camera.CurrentMode != CameraViewMode.Interior && renderer.Camera.CurrentMode != CameraViewMode.InteriorLookAhead || !Car.CarSections.ContainsKey(CarSectionType.Interior))
            {
                return;
            }

            int add = Car.CarSections[CarSectionType.Interior].CurrentAdditionalGroup + 1;
            if (add >= Car.CarSections[CarSectionType.Interior].Groups.Length)
            {
                return;
            }

            TouchElement[] TouchElements = Car.CarSections[CarSectionType.Interior].Groups[add].TouchElements;

            if (TouchElements == null)
            {
                return;
            }

            ObjectState pickedObject = ParseFBO(Point, 5, 5);

            foreach (TouchElement TouchElement in TouchElements)
            {
                if (TouchElement.Element.internalObject == pickedObject)
                {
                    Car.CarSections[CarSectionType.Interior].CurrentAdditionalGroup = TouchElement.JumpScreenIndex;
                    // Force a show / hide of the car sections to ensure that the touch stack is correctly updated
                    Car.ChangeCarSection(CarSectionType.Interior, false, true);

                    foreach (var index in TouchElement.SoundIndices)
                    {
                        if (Car.Sounds.Touch.ContainsKey(index))
                        {
                            Car.Sounds.Touch[index].Play(TrainManager.PlayerTrain.Cars[TrainManager.PlayerTrain.DriverCar], false);
                        }
                    }
                }

                // HACK: Normally terminate the command issued once.
                if (TouchElement.Element.internalObject == pickedObject || (pickedObject != prePickedObject && TouchElement.Element.internalObject == prePickedObject))
                {
                    foreach (int index in TouchElement.ControlIndices)
                    {
                        Interface.CurrentControls[index].AnalogState = 0.0;
                        Interface.CurrentControls[index].DigitalState = DigitalControlState.Released;
                        MainLoop.RemoveControlRepeat(index);
                    }
                }
            }
        }
    }
}

