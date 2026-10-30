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

// OBNS: not an upstream file. The in-window main menu (OBNS/GLFORMS.md): the window the forms are drawn in, with
// OpenGL, and the loop that runs them. The main form (formMain's copy) is the window's content; a dialog it opens
// (a message, a file dialog, the About box) is drawn over it, modal, as Windows would show it - in the same window.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;
using GLPixelFormat = OpenTK.Graphics.OpenGL.PixelFormat;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	/// <summary>A control's bitmap and the OpenGL texture made from it.</summary>
	internal sealed class Surface : IDisposable
	{
		internal readonly Bitmap Bitmap;
		internal readonly int Width, Height;
		internal bool Changed = true;
		internal int Texture;

		internal Surface(int width, int height)
		{
			Width = width;
			Height = height;
			Bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
		}

		public void Dispose()
		{
			Bitmap.Dispose();
			if (Texture != 0)
			{
				Host.DeleteTexture(Texture);
				Texture = 0;
			}
		}
	}

	internal static partial class Host
	{
		/// <summary>The menu's window; null when no form is showing.</summary>
		internal static GameWindow Window;

		/// <summary>Physical pixels per form unit.</summary>
		internal static float Scale = 1.0f;

		private static float baseScale = 1.0f;

		/// <summary>The open forms, the main form first; the last is the active one.</summary>
		internal static readonly List<Form> Forms = new List<Form>();

		internal static Form MainForm => Forms.Count > 0 ? Forms[0] : null;

		internal static Control FocusedControl;
		internal static Control HoveredControl;
		internal static Control CapturedControl;
		internal static bool CaptureInside;
		internal static Popup ActivePopup;

		private static Thread uiThread;
		private static readonly Queue<Action> posted = new Queue<Action>();
		private static readonly bool debug = Environment.GetEnvironmentVariable("OBNS_MENU_DEBUG") == "1";
		private static bool renderRequested = true;
		private static string lastRenderError;
		private static readonly string snapshotFolder = Environment.GetEnvironmentVariable("OBNS_MENU_SNAPSHOT");
		private static long lastSnapshot;

		/// <summary>
		/// With OBNS_MENU_SNAPSHOT=folder: the frame just drawn, read back and saved as menu.png there twice a second, for
		/// testing (a capture of an OpenGL window from outside can catch it between frames).
		/// </summary>
		private static void SaveSnapshot()
		{
			if (string.IsNullOrEmpty(snapshotFolder) || Now - lastSnapshot < 500)
			{
				return;
			}

			lastSnapshot = Now;
			Size fb = FramebufferSize;
			try
			{
				using (Bitmap bitmap = new Bitmap(fb.Width, fb.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
				{
					BitmapData data = bitmap.LockBits(new Rectangle(0, 0, fb.Width, fb.Height), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
					GL.ReadPixels(0, 0, fb.Width, fb.Height, GLPixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
					bitmap.UnlockBits(data);
					bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
					string file = System.IO.Path.Combine(snapshotFolder, "menu.png");
					string temporary = file + ".tmp";
					bitmap.Save(temporary, ImageFormat.Png);
					if (System.IO.File.Exists(file))
					{
						System.IO.File.Delete(file);
					}

					System.IO.File.Move(temporary, file);
				}
			}
			catch (Exception ex)
			{
				Log("snapshot failed: " + ex.Message);
			}
		}
		private static long framesDrawn, lastDebugLine;
		private static long lastPump;

		/// <summary>With OBNS_MENU_DEBUG=1: where the menu's thread is, logged when the loop has not turned for 4 s.</summary>
		private static void StartWatchdog()
		{
			if (Environment.GetEnvironmentVariable("OBNS_MENU_DEBUG") != "1")
			{
				return;
			}

			Thread ui = uiThread;
			Thread watchdog = new Thread(() =>
			{
				bool reported = false;
				while (Window != null)
				{
					Thread.Sleep(1000);
					if (Now - lastPump > 4000 && !reported)
					{
						reported = true;
						try
						{
#pragma warning disable 618
							ui.Suspend();
							StackTrace trace;
							try
							{
								trace = new StackTrace(ui, false);
							}
							finally
							{
								ui.Resume();
							}
#pragma warning restore 618
							Log("the loop has not turned for " + (Now - lastPump) + " ms:" + Environment.NewLine + trace);
						}
						catch (Exception ex)
						{
							Log("the loop has not turned for " + (Now - lastPump) + " ms (" + ex.Message + ")");
						}
					}
					else if (Now - lastPump < 1000)
					{
						reported = false;
					}
				}
			}) { IsBackground = true, Name = "OBNS menu watchdog" };
			watchdog.Start();
		}
		private static long lastRender;
		private static readonly List<int> deadTextures = new List<int>();

		internal static void Log(string message)
		{
			try
			{
				Program.FileSystem?.AppendToLogFile("OBNS menu: " + message);
			}
			catch
			{
				// the log is a convenience
			}
		}

		internal static void RequestRender()
		{
			renderRequested = true;
		}

		// --- threads ---

		internal static bool InvokeRequired => uiThread != null && Thread.CurrentThread != uiThread;

		internal static void Post(Action action)
		{
			lock (posted)
			{
				posted.Enqueue(action);
			}
		}

		internal static object Invoke(Func<object> function)
		{
			if (!InvokeRequired)
			{
				return function();
			}

			object result = null;
			Exception error = null;
			using (ManualResetEventSlim done = new ManualResetEventSlim(false))
			{
				Post(() =>
				{
					try
					{
						result = function();
					}
					catch (Exception ex)
					{
						error = ex;
					}
					finally
					{
						done.Set();
					}
				});
				done.Wait();
			}

			if (error != null)
			{
				throw error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
			}

			return result;
		}

		private static void RunPosted()
		{
			while (true)
			{
				Action action;
				lock (posted)
				{
					if (posted.Count == 0)
					{
						return;
					}

					action = posted.Dequeue();
				}

				try
				{
					action();
				}
				catch (TargetInvocationException ex) when (ex.InnerException != null)
				{
					Log("a posted call failed: " + ex.InnerException);
				}
			}
		}

		/// <summary>Brings BackgroundWorker's completion events to the menu's thread, as Windows Forms' context does.</summary>
		private sealed class MenuSynchronizationContext : SynchronizationContext
		{
			public override void Post(SendOrPostCallback d, object state)
			{
				Host.Post(() => d(state));
			}

			public override void Send(SendOrPostCallback d, object state)
			{
				Host.Invoke(() =>
				{
					d(state);
					return null;
				});
			}

			public override SynchronizationContext CreateCopy() => this;
		}

		// --- timers ---

		internal static readonly List<Timer> Timers = new List<Timer>();

		private static readonly Stopwatch clock = Stopwatch.StartNew();

		internal static long Now => clock.ElapsedMilliseconds;

		private static void RunTimers()
		{
			foreach (Timer timer in Timers.ToArray())
			{
				if (timer.Enabled && Now >= timer.NextTick)
				{
					timer.NextTick = Now + Math.Max(1, timer.Interval);
					timer.RaiseTick();
				}
			}
		}

		// --- the window ---

		private static int program, vao, whiteTexture;
		private static int uRect, uScreen, uTex, uColor;
		private static SynchronizationContext previousContext;

		private static void CreateWindow(Form form)
		{
			uiThread = Thread.CurrentThread;
			previousContext = SynchronizationContext.Current;
			SynchronizationContext.SetSynchronizationContext(new MenuSynchronizationContext());
			string scale = Environment.GetEnvironmentVariable("OBNS_MENU_SCALE");
			if (!string.IsNullOrEmpty(scale) && float.TryParse(scale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) && s >= 0.5f && s <= 4.0f)
			{
				baseScale = s;
			}

			Size size = form.Size;
			Window = new GameWindow(Math.Max(320, (int)(size.Width * baseScale)), Math.Max(240, (int)(size.Height * baseScale)), GraphicsMode.Default, form.Text,
				GameWindowFlags.Default, DisplayDevice.Default, 3, 3, GraphicsContextFlags.ForwardCompatible);
			Window.Visible = false;
			Window.VSync = VSyncMode.On;
			Window.MakeCurrent();
			CreateGraphics();
			Window.Resize += (sender, e) => UpdateMainFormSize();
			Window.Closing += (sender, e) =>
			{
				Form main = MainForm;
				if (main != null && main.IsOpen)
				{
					main.CloseFromWindow(e);
				}
			};
			Window.MouseDown += (sender, e) => OnMouseDown(e.Button, e.X, e.Y);
			Window.MouseUp += (sender, e) => OnMouseUp(e.Button, e.X, e.Y);
			Window.MouseMove += (sender, e) => OnMouseMove(e.X, e.Y);
			Window.MouseWheel += (sender, e) => OnMouseWheel(e.Delta);
			Window.KeyDown += (sender, e) => OnKeyDown(e.Key, e.Modifiers);
			Window.KeyUp += (sender, e) => OnKeyUp(e.Key, e.Modifiers);
			Window.KeyPress += (sender, e) => OnKeyPress(e.KeyChar);
			Window.FileDrop += (sender, e) => OnFileDrop(e.FileName);
			Log("window " + Window.ClientSize.Width + "x" + Window.ClientSize.Height + ", OpenGL " + GL.GetString(StringName.Version));
		}

		private static void DestroyWindow()
		{
			foreach (Form form in Forms)
			{
				ReleaseSurfaces(form);
			}

			Window.Visible = false;
			Window.Dispose();
			Window = null;
			program = vao = whiteTexture = 0;
			deadTextures.Clear();
			SynchronizationContext.SetSynchronizationContext(previousContext);
		}

		private static void ReleaseSurfaces(Control control)
		{
			control.surface?.Dispose();
			control.surface = null;
			control.overSurface?.Dispose();
			control.overSurface = null;
			foreach (Control child in control.Controls)
			{
				ReleaseSurfaces(child);
			}
		}

		private const string VertexShader = @"#version 150 core
uniform vec4 uRect;
uniform vec2 uScreen;
uniform vec4 uTex;
out vec2 textureCoord;
void main()
{
	vec2 corner = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
	vec2 position = uRect.xy + corner * uRect.zw;
	gl_Position = vec4(position.x / uScreen.x * 2.0 - 1.0, 1.0 - position.y / uScreen.y * 2.0, 0.0, 1.0);
	textureCoord = mix(uTex.xy, uTex.zw, corner);
}";

		private const string FragmentShader = @"#version 150 core
uniform sampler2D uTexture;
uniform vec4 uColor;
in vec2 textureCoord;
out vec4 fragColor;
void main()
{
	fragColor = texture(uTexture, textureCoord) * uColor;
}";

		private static void CreateGraphics()
		{
			int vs = Compile(ShaderType.VertexShader, VertexShader);
			int fs = Compile(ShaderType.FragmentShader, FragmentShader);
			program = GL.CreateProgram();
			GL.AttachShader(program, vs);
			GL.AttachShader(program, fs);
			GL.BindFragDataLocation(program, 0, "fragColor");
			GL.LinkProgram(program);
			GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
			if (linked == 0)
			{
				throw new InvalidOperationException("The menu's shader did not link: " + GL.GetProgramInfoLog(program));
			}

			GL.DeleteShader(vs);
			GL.DeleteShader(fs);
			uRect = GL.GetUniformLocation(program, "uRect");
			uScreen = GL.GetUniformLocation(program, "uScreen");
			uTex = GL.GetUniformLocation(program, "uTex");
			uColor = GL.GetUniformLocation(program, "uColor");
			vao = GL.GenVertexArray();
			whiteTexture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, whiteTexture);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0, GLPixelFormat.Bgra, PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 });
			SetFilter();
		}

		private static int Compile(ShaderType type, string source)
		{
			int shader = GL.CreateShader(type);
			GL.ShaderSource(shader, source);
			GL.CompileShader(shader);
			GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
			if (compiled == 0)
			{
				throw new InvalidOperationException("The menu's shader did not compile: " + GL.GetShaderInfoLog(shader));
			}

			return shader;
		}

		private static void SetFilter()
		{
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
		}

		internal static void DeleteTexture(int texture)
		{
			lock (deadTextures)
			{
				deadTextures.Add(texture);
			}
		}

		private static void Upload(Surface surface)
		{
			if (surface.Texture == 0)
			{
				surface.Texture = GL.GenTexture();
			}

			GL.BindTexture(TextureTarget.Texture2D, surface.Texture);
			BitmapData data = surface.Bitmap.LockBits(new Rectangle(0, 0, surface.Width, surface.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
			try
			{
				GL.PixelStore(PixelStoreParameter.UnpackRowLength, data.Stride / 4);
				GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, surface.Width, surface.Height, 0, GLPixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
				GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
			}
			finally
			{
				surface.Bitmap.UnlockBits(data);
			}

			SetFilter();
			surface.Changed = false;
		}

		/// <summary>The size of the window's drawing area, in pixels.</summary>
		private static Size FramebufferSize => Window.ClientSize;

		/// <summary>Fits the main form to the window: the window is the form's client area.</summary>
		private static void UpdateMainFormSize()
		{
			Form main = MainForm;
			if (Window == null || main == null)
			{
				return;
			}

			Size window = FramebufferSize;
			if (window.Width <= 0 || window.Height <= 0)
			{
				return;
			}

			// A window smaller than the form's minimum shows the form smaller, rather than cut off.
			Scale = baseScale;
			Size minimum = main.MinimumSize;
			if (minimum.Width > 0 && window.Width < minimum.Width * Scale)
			{
				Scale = window.Width / (float)minimum.Width;
			}

			if (minimum.Height > 0 && window.Height < minimum.Height * Scale)
			{
				Scale = Math.Min(Scale, window.Height / (float)minimum.Height);
			}

			main.SetSizeFromWindow((int)Math.Floor(window.Width / Scale), (int)Math.Floor(window.Height / Scale));
			RequestRender();
		}

		/// <summary>Where a form's client area is, in the window's form units.</summary>
		internal static Point FormOrigin(Form form)
		{
			if (form == null || form == MainForm)
			{
				return Point.Empty;
			}

			return new Point(form.Left + Form.FrameBorder, form.Top + Form.TitleHeight);
		}

		// --- running ---

		/// <summary>Shows a form and runs the menu until it closes: the main form opens the window, a dialog is drawn over it.</summary>
		internal static void RunModal(Form form)
		{
			// The Mac's own controls where their library is there (Host.Native.cs).
			if (NativeActive || (Window == null && UseNative()))
			{
				RunModalNative(form);
				return;
			}

			bool main = Window == null;
			if (main)
			{
				CreateWindow(form);
			}

			Forms.Add(form);
			Control previousFocus = FocusedControl;
			ClosePopup();
			try
			{
				if (main)
				{
					lastPump = Now;
					StartWatchdog();
					Control.AnchorsLive = true;
					form.RecordAnchorsRecursive();
					UpdateMainFormSize();
				}
				else
				{
					form.RecordAnchorsRecursive();
					form.PlaceDialog(MainForm);
				}

				form.RaiseLoad();
				if (main)
				{
					Window.Visible = true;
					UpdateMainFormSize();
				}

				form.SelectFirstControl();
				form.RaiseShown();
				RequestRender();
				while (form.IsOpen && Window != null && Window.Exists)
				{
					PumpOnce(true);
				}
			}
			finally
			{
				Forms.Remove(form);
				ReleaseSurfaces(form);
				FocusedControl = previousFocus != null && previousFocus.FindForm() != null && Forms.Contains(previousFocus.FindForm()) ? previousFocus : null;
				HoveredControl = null;
				CapturedControl = null;
				ClosePopup();
				if (main && Window != null)
				{
					DestroyWindow();
					Forms.Clear();
					Control.AnchorsLive = false;
				}

				RequestRender();
			}
		}

		/// <summary>One turn of the loop: the window's events, posted calls, timers, and a frame if anything changed.</summary>
		internal static void PumpOnce(bool wait)
		{
			if (NativeActive)
			{
				PumpNative(wait);
				return;
			}

			if (Window == null)
			{
				return;
			}

			lastPump = Now;
			Window.ProcessEvents();
			if (Window == null || !Window.Exists)
			{
				return;
			}

			RunPosted();
			RunTimers();
			UpdateToolTip();
			// Drawn when something changed, and a few times a second regardless: a window uncovered, restored or
			// captured shows the menu, not whatever its buffers held.
			if (renderRequested || Now - lastRender > 250)
			{
				renderRequested = false;
				lastRender = Now;
				try
				{
					Render();
				}
				catch (Exception ex)
				{
					// Logged once; the frame is shown as far as it was drawn.
					string message = ex.GetType().Name + ": " + ex.Message;
					if (message != lastRenderError)
					{
						lastRenderError = message;
						Log("drawing failed: " + ex);
					}
				}

				SaveSnapshot();
				if (debug)
				{
					framesDrawn++;
					if (Now - lastDebugLine > 2000)
					{
						lastDebugLine = Now;
						Form top = Forms.Count > 0 ? Forms[Forms.Count - 1] : null;
						Log("frames " + framesDrawn + ", window " + FramebufferSize.Width + "x" + FramebufferSize.Height + ", scale " + Scale + ", forms " + Forms.Count +
						    ", main " + MainForm?.Width + "x" + MainForm?.Height + ", top " + top?.GetType().Name + " at " + top?.Bounds + ", popup " + ActivePopup?.GetType().Name);
					}

					ErrorCode error = GL.GetError();
					if (error != ErrorCode.NoError)
					{
						Log("OpenGL error " + error + " drawing at " + FramebufferSize.Width + "x" + FramebufferSize.Height + " (scale " + Scale + ")");
					}
				}

				Window.SwapBuffers();
			}
			else if (wait)
			{
				Thread.Sleep(10);
			}
		}

		// --- drawing ---

		private static void Render()
		{
			Window.MakeCurrent();
			lock (deadTextures)
			{
				if (deadTextures.Count > 0)
				{
					GL.DeleteTextures(deadTextures.Count, deadTextures.ToArray());
					deadTextures.Clear();
				}
			}

			Size fb = FramebufferSize;
			GL.Viewport(0, 0, fb.Width, fb.Height);
			Form main = MainForm;
			Color back = main?.BackColor ?? Color.White;
			GL.ClearColor(back.R / 255f, back.G / 255f, back.B / 255f, 1f);
			GL.Disable(EnableCap.ScissorTest);
			GL.Clear(ClearBufferMask.ColorBufferBit);
			GL.Disable(EnableCap.DepthTest);
			GL.Disable(EnableCap.CullFace);
			GL.Enable(EnableCap.Blend);
			GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
			GL.UseProgram(program);
			GL.BindVertexArray(vao);
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.Uniform1(GL.GetUniformLocation(program, "uTexture"), 0);
			GL.Uniform2(uScreen, (float)fb.Width, fb.Height);
			GL.Enable(EnableCap.ScissorTest);
			Rectangle all = new Rectangle(0, 0, (int)Math.Ceiling(fb.Width / Scale), (int)Math.Ceiling(fb.Height / Scale));
			foreach (Form form in Forms.ToArray())
			{
				if (form != main)
				{
					// The window behind a dialog is dimmed, and the dialog drawn with a frame and title.
					FillRect(all, Color.FromArgb(70, 0, 0, 0), all);
					DrawFrame(form, all);
				}

				Point origin = FormOrigin(form);
				DrawTree(form, origin.X, origin.Y, all);
			}

			if (ActivePopup != null)
			{
				DrawTree(ActivePopup, ActivePopup.Left, ActivePopup.Top, all);
			}

			GL.Disable(EnableCap.ScissorTest);
			GL.BindVertexArray(0);
			GL.UseProgram(0);
		}

		private static void DrawFrame(Form form, Rectangle clip)
		{
			Surface frame = form.GetFrameSurface(Scale);
			if (frame.Changed)
			{
				Upload(frame);
			}

			Rectangle outer = form.FrameBounds;
			Draw(frame.Texture, outer.X, outer.Y, frame.Width / Scale, frame.Height / Scale, clip, Color.White);
		}

		/// <summary>Draws a control and its children; x, y are where the control's own top-left corner is, in form units.</summary>
		private static void DrawTree(Control control, int x, int y, Rectangle clip)
		{
			if (!control.OwnVisible)
			{
				return;
			}

			Rectangle bounds = new Rectangle(x, y, control.Width, control.Height);
			Rectangle visible = Rectangle.Intersect(bounds, clip);
			if (visible.Width <= 0 || visible.Height <= 0)
			{
				return;
			}

			if (control.DrawsItself)
			{
				Surface surface = control.GetSurface(Scale);
				if (surface.Changed)
				{
					Upload(surface);
				}

				Draw(surface.Texture, x, y, surface.Width / Scale, surface.Height / Scale, visible, Color.White);
			}

			Rectangle area = control.DisplayRectangle;
			Rectangle childClip = Rectangle.Intersect(visible, new Rectangle(x + area.X, y + area.Y, area.Width, area.Height));
			if (childClip.Width > 0 && childClip.Height > 0)
			{
				Point offset = control.ChildOffset;
				for (int i = control.Controls.Count - 1; i >= 0; i--)
				{
					Control child = control.Controls[i];
					DrawTree(child, x + offset.X + child.Left, y + offset.Y + child.Top, childClip);
				}
			}

			if (control.PaintsOverChildren)
			{
				Surface over = control.GetOverSurface(Scale);
				if (over.Changed)
				{
					Upload(over);
				}

				Draw(over.Texture, x, y, over.Width / Scale, over.Height / Scale, visible, Color.White);
			}
		}

		internal static void FillRect(Rectangle r, Color color, Rectangle clip)
		{
			Draw(whiteTexture, r.X, r.Y, r.Width, r.Height, clip, color);
		}

		/// <summary>Draws a texture at a place given in form units, clipped.</summary>
		private static void Draw(int texture, float x, float y, float w, float h, Rectangle clip, Color color)
		{
			Size fb = FramebufferSize;
			int cx = (int)Math.Floor(clip.X * Scale), cy = (int)Math.Floor(clip.Y * Scale);
			int cw = (int)Math.Ceiling(clip.Right * Scale) - cx, ch = (int)Math.Ceiling(clip.Bottom * Scale) - cy;
			GL.Scissor(cx, fb.Height - cy - ch, Math.Max(0, cw), Math.Max(0, ch));
			GL.BindTexture(TextureTarget.Texture2D, texture);
			GL.Uniform4(uRect, (float)Math.Round(x * Scale), (float)Math.Round(y * Scale), w * Scale, h * Scale);
			GL.Uniform4(uTex, 0f, 0f, 1f, 1f);
			float a = color.A / 255f;
			GL.Uniform4(uColor, color.R / 255f * a, color.G / 255f * a, color.B / 255f * a, a);
			GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
		}

		// --- input ---

		private static Point mouse;
		private static long lastClickTime;
		private static Control lastClickControl;
		private static Point lastClickPoint;
		private static Point dragStart;
		private static Form draggingForm;

		/// <summary>The window point in form units.</summary>
		private static Point ToForm(int x, int y) => new Point((int)Math.Floor(x / Scale), (int)Math.Floor(y / Scale));

		/// <summary>The control under a point (window, form units) and the point in its coordinates, or null.</summary>
		private static Control Find(Point p, out Point local)
		{
			local = Point.Empty;
			if (ActivePopup != null && ActivePopup.Bounds.Contains(p))
			{
				Control hit = ActivePopup.HitTest(new Point(p.X - ActivePopup.Left, p.Y - ActivePopup.Top));
				if (hit != null)
				{
					local = ToLocal(hit, p);
				}

				return hit;
			}

			Form top = Forms.Count > 0 ? Forms[Forms.Count - 1] : null;
			if (top == null)
			{
				return null;
			}

			Point origin = FormOrigin(top);
			Control found = top.HitTest(new Point(p.X - origin.X, p.Y - origin.Y));
			if (found != null)
			{
				local = ToLocal(found, p);
			}

			return found;
		}

		private static Point ToLocal(Control control, Point p)
		{
			if (control is Popup || control.FindForm() == null)
			{
				Control root = control;
				int ox = 0, oy = 0;
				while (root.Parent != null)
				{
					ox += root.Left + root.Parent.ChildOffset.X;
					oy += root.Top + root.Parent.ChildOffset.Y;
					root = root.Parent;
				}

				return new Point(p.X - root.Left - ox, p.Y - root.Top - oy);
			}

			Point origin = FormOrigin(control.FindForm());
			Point own = control.PositionInForm();
			return new Point(p.X - origin.X - own.X, p.Y - origin.Y - own.Y);
		}

		private static MouseButtons Map(MouseButton button)
		{
			switch (button)
			{
				case MouseButton.Left: return MouseButtons.Left;
				case MouseButton.Right: return MouseButtons.Right;
				case MouseButton.Middle: return MouseButtons.Middle;
				default: return MouseButtons.None;
			}
		}

		internal static MouseButtons MouseButtonsDown;

		private static void OnMouseDown(MouseButton button, int x, int y)
		{
			Point p = ToForm(x, y);
			mouse = p;
			MouseButtonsDown |= Map(button);
			HideToolTip();
			if (ActivePopup != null && !ActivePopup.Bounds.Contains(p))
			{
				// A click beside an open list or menu only closes it.
				ClosePopup();
				return;
			}

			Form top = Forms.Count > 0 ? Forms[Forms.Count - 1] : null;
			if (top != null && top != MainForm && ActivePopup == null)
			{
				Rectangle frame = top.FrameBounds;
				if (!frame.Contains(p))
				{
					return;
				}

				if (p.Y < frame.Top + Form.TitleHeight)
				{
					if (top.CloseBoxBounds.Contains(p))
					{
						top.DialogResult = DialogResult.Cancel;
					}
					else
					{
						draggingForm = top;
						dragStart = new Point(p.X - top.Left, p.Y - top.Top);
					}

					return;
				}
			}

			Control target = Find(p, out Point local);
			if (target == null)
			{
				return;
			}

			if (!target.Enabled)
			{
				return;
			}

			Control focusable = target;
			while (focusable != null && !focusable.CanTakeFocus)
			{
				focusable = focusable.Parent;
			}

			if (focusable != null && button == MouseButton.Left)
			{
				SetFocus(focusable);
			}

			CapturedControl = target;
			CaptureInside = true;
			MouseButtons mapped = Map(button);
			target.RaiseMouseDown(new MouseEventArgs(mapped, 1, local.X, local.Y, 0));
			target.Invalidate();
		}

		private static void OnMouseUp(MouseButton button, int x, int y)
		{
			Point p = ToForm(x, y);
			mouse = p;
			MouseButtonsDown &= ~Map(button);
			if (draggingForm != null)
			{
				draggingForm = null;
				return;
			}

			Control target = CapturedControl;
			CapturedControl = null;
			if (target == null)
			{
				return;
			}

			Point local = ToLocal(target, p);
			bool inside = local.X >= 0 && local.Y >= 0 && local.X < target.Width && local.Y < target.Height;
			MouseButtons mapped = Map(button);
			target.RaiseMouseUp(new MouseEventArgs(mapped, 1, local.X, local.Y, 0));
			target.Invalidate();
			if (!inside || !target.Enabled)
			{
				return;
			}

			if (mapped == MouseButtons.Right)
			{
				Control owner = target;
				while (owner != null && owner.ContextMenuStrip == null)
				{
					owner = owner.Parent;
				}

				target.RaiseMouseClick(new MouseEventArgs(mapped, 1, local.X, local.Y, 0));
				if (owner != null)
				{
					owner.ContextMenuStrip.Show(owner, owner.PointToClient(ToScreen(p)));
				}

				return;
			}

			if (mapped != MouseButtons.Left)
			{
				return;
			}

			bool doubleClick = target == lastClickControl && Now - lastClickTime < 500 && Math.Abs(p.X - lastClickPoint.X) < 5 && Math.Abs(p.Y - lastClickPoint.Y) < 5;
			if (doubleClick && target.StandardDoubleClick)
			{
				lastClickControl = null;
				target.RaiseMouseDoubleClick(new MouseEventArgs(mapped, 2, local.X, local.Y, 0));
				target.RaiseDoubleClick();
				return;
			}

			lastClickControl = target;
			lastClickTime = Now;
			lastClickPoint = p;
			target.RaiseMouseClick(new MouseEventArgs(mapped, 1, local.X, local.Y, 0));
			target.RaiseClick();
		}

		/// <summary>A point in the window as "screen" coordinates: the window is the screen the forms know.</summary>
		private static Point ToScreen(Point p) => p;

		private static void OnMouseMove(int x, int y)
		{
			Point p = ToForm(x, y);
			if (p == mouse)
			{
				return;
			}

			mouse = p;
			HideToolTip();
			toolTipSince = Now;
			if (draggingForm != null)
			{
				draggingForm.Location = new Point(p.X - dragStart.X, p.Y - dragStart.Y);
				RequestRender();
				return;
			}

			if (CapturedControl != null)
			{
				Point local = ToLocal(CapturedControl, p);
				bool inside = local.X >= 0 && local.Y >= 0 && local.X < CapturedControl.Width && local.Y < CapturedControl.Height;
				if (inside != CaptureInside)
				{
					CaptureInside = inside;
					CapturedControl.Invalidate();
				}

				CapturedControl.RaiseMouseMove(new MouseEventArgs(MouseButtonsDown, 0, local.X, local.Y, 0));
				return;
			}

			Control target = Find(p, out Point at);
			SetHovered(target);
			target?.RaiseMouseMove(new MouseEventArgs(MouseButtons.None, 0, at.X, at.Y, 0));
		}

		private static void SetHovered(Control target)
		{
			if (target == HoveredControl)
			{
				return;
			}

			Control old = HoveredControl;
			HoveredControl = target;
			if (old != null)
			{
				old.RaiseMouseLeave();
				old.Invalidate();
			}

			if (target != null)
			{
				target.RaiseMouseEnter();
				target.Invalidate();
			}
		}

		private static void OnMouseWheel(int delta)
		{
			HideToolTip();
			Control target = Find(mouse, out Point local);
			int windowsDelta = delta * 120;
			for (Control c = target; c != null; c = c.Parent)
			{
				if (!c.Enabled)
				{
					continue;
				}

				c.RaiseMouseWheel(new MouseEventArgs(MouseButtons.None, 0, local.X, local.Y, windowsDelta));
				if (c.HandleWheel(windowsDelta, ToLocal(c, mouse)))
				{
					RequestRender();
					return;
				}
			}
		}

		// --- keyboard ---

		private static bool suppressKeyPress;

		internal static Keys ModifierKeys;

		private static Keys MapKey(Key key)
		{
			if (key >= Key.A && key <= Key.Z) return Keys.A + (key - Key.A);
			if (key >= Key.Number0 && key <= Key.Number9) return Keys.D0 + (key - Key.Number0);
			if (key >= Key.Keypad0 && key <= Key.Keypad9) return Keys.NumPad0 + (key - Key.Keypad0);
			if (key >= Key.F1 && key <= Key.F12) return Keys.F1 + (key - Key.F1);
			switch (key)
			{
				case Key.BackSpace: return Keys.Back;
				case Key.Tab: return Keys.Tab;
				case Key.Enter: case Key.KeypadEnter: return Keys.Return;
				case Key.Escape: return Keys.Escape;
				case Key.Space: return Keys.Space;
				case Key.PageUp: return Keys.PageUp;
				case Key.PageDown: return Keys.PageDown;
				case Key.End: return Keys.End;
				case Key.Home: return Keys.Home;
				case Key.Left: return Keys.Left;
				case Key.Up: return Keys.Up;
				case Key.Right: return Keys.Right;
				case Key.Down: return Keys.Down;
				case Key.Insert: return Keys.Insert;
				case Key.Delete: return Keys.Delete;
				case Key.ShiftLeft: case Key.ShiftRight: return Keys.ShiftKey;
				case Key.ControlLeft: case Key.ControlRight: return Keys.ControlKey;
				case Key.AltLeft: case Key.AltRight: return Keys.Menu;
				case Key.KeypadAdd: return Keys.Add;
				case Key.KeypadSubtract: return Keys.Subtract;
				case Key.KeypadMultiply: return Keys.Multiply;
				case Key.KeypadDivide: return Keys.Divide;
				case Key.KeypadDecimal: return Keys.Decimal;
				default: return Keys.None;
			}
		}

		private static Keys MapModifiers(KeyModifiers modifiers)
		{
			Keys result = Keys.None;
			if ((modifiers & KeyModifiers.Shift) != 0) result |= Keys.Shift;
			if ((modifiers & KeyModifiers.Control) != 0) result |= Keys.Control;
			if ((modifiers & KeyModifiers.Alt) != 0) result |= Keys.Alt;
			// The Mac's Command key works as Control does in a Windows form (copy, paste, select all).
			if ((modifiers & KeyModifiers.Command) != 0) result |= Keys.Control;
			return result;
		}

		/// <summary>The modifier keys held, as their own key events say (the event's modifiers miss keys sent by software).</summary>
		private static Keys heldModifiers;

		private static Keys ModifierOf(Key key)
		{
			switch (key)
			{
				case Key.ShiftLeft: case Key.ShiftRight: return Keys.Shift;
				case Key.ControlLeft: case Key.ControlRight: return Keys.Control;
				case Key.AltLeft: case Key.AltRight: return Keys.Alt;
				case Key.WinLeft: case Key.WinRight: return OpenTK.Configuration.RunningOnMacOS ? Keys.Control : Keys.None;
				default: return Keys.None;
			}
		}

		private static void OnKeyDown(Key key, KeyModifiers modifiers)
		{
			HideToolTip();
			heldModifiers |= ModifierOf(key);
			ModifierKeys = MapModifiers(modifiers) | heldModifiers;
			if (debug)
			{
				Log("key down " + key + " (" + modifiers + "), modifiers " + ModifierKeys + ", focus " + FocusedControl?.Name);
			}

			Keys code = MapKey(key);
			if (code == Keys.None)
			{
				return;
			}

			KeyEventArgs e = new KeyEventArgs(code | ModifierKeys);
			suppressKeyPress = false;
			if (ActivePopup != null)
			{
				if (ActivePopup.HandleKey(e))
				{
					suppressKeyPress = true;
					RequestRender();
					return;
				}
			}

			Form form = Forms.Count > 0 ? Forms[Forms.Count - 1] : null;
			if (form == null)
			{
				return;
			}

			DispatchKey(form, e, code);
			RequestRender();
		}

		/// <summary>A key on the top form, as Windows Forms has it (shared with the native host); whether it was dealt with.</summary>
		private static bool DispatchKey(Form form, KeyEventArgs e, Keys code)
		{
			if (form.KeyPreview)
			{
				form.RaiseKeyDown(e);
			}

			Control target = FocusedControl != null && FocusedControl.FindForm() == form ? FocusedControl : null;
			if (!e.Handled && target != null)
			{
				target.RaiseKeyDown(e);
			}

			suppressKeyPress = e.SuppressKeyPress;
			if (e.Handled)
			{
				return true;
			}

			bool inputKey = target != null && target.IsInputKey(e.KeyData);
			switch (code)
			{
				case Keys.Tab:
					if (!inputKey)
					{
						form.SelectNextControl(target, (e.KeyData & Keys.Shift) == 0);
						suppressKeyPress = true;
					}

					break;
				case Keys.Return:
					if (!inputKey && form.AcceptButton != null && !(target is ButtonBase))
					{
						form.AcceptButton.PerformClick();
						suppressKeyPress = true;
					}
					else if (target is ButtonBase button && !inputKey)
					{
						button.PerformClick();
						suppressKeyPress = true;
					}

					break;
				case Keys.Space:
					if (target is ButtonBase spaceButton && !inputKey)
					{
						spaceButton.PerformClick();
						suppressKeyPress = true;
					}

					break;
				case Keys.Escape:
					if (!inputKey)
					{
						if (form.CancelButton != null)
						{
							form.CancelButton.PerformClick();
						}
						else if (form != MainForm)
						{
							form.DialogResult = DialogResult.Cancel;
						}

						suppressKeyPress = true;
					}

					break;
			}

			return suppressKeyPress;
		}

		private static void OnKeyUp(Key key, KeyModifiers modifiers)
		{
			heldModifiers &= ~ModifierOf(key);
			ModifierKeys = MapModifiers(modifiers) | heldModifiers;
			Keys code = MapKey(key);
			Form form = Forms.Count > 0 ? Forms[Forms.Count - 1] : null;
			if (code == Keys.None || form == null)
			{
				return;
			}

			KeyEventArgs e = new KeyEventArgs(code | ModifierKeys);
			if (form.KeyPreview)
			{
				form.RaiseKeyUp(e);
			}

			if (!e.Handled && FocusedControl != null && FocusedControl.FindForm() == form)
			{
				FocusedControl.RaiseKeyUp(e);
			}
		}

		private static void OnKeyPress(char c)
		{
			if (suppressKeyPress || char.IsControl(c))
			{
				return;
			}

			if (ActivePopup != null)
			{
				return;
			}

			Form form = Forms.Count > 0 ? Forms[Forms.Count - 1] : null;
			KeyPressEventArgs e = new KeyPressEventArgs(c);
			if (form != null && form.KeyPreview)
			{
				form.RaiseKeyPress(e);
			}

			if (!e.Handled && FocusedControl != null && FocusedControl.FindForm() == form)
			{
				FocusedControl.RaiseKeyPress(e);
			}

			RequestRender();
		}

		private static void OnFileDrop(string file)
		{
			Control target = Find(mouse, out Point local);
			while (target != null && !target.AllowDrop)
			{
				target = target.Parent;
			}

			if (target == null)
			{
				return;
			}

			DragEventArgs e = new DragEventArgs(new FileDropData(new[] { file }), 0, mouse.X, mouse.Y, DragDropEffects.All, DragDropEffects.None);
			target.RaiseDragEnter(e);
			if (e.Effect != DragDropEffects.None)
			{
				target.RaiseDragDrop(e);
			}
		}

		// --- focus ---

		internal static bool ContainsFocus(Control control)
		{
			for (Control c = FocusedControl; c != null; c = c.Parent)
			{
				if (c == control)
				{
					return true;
				}
			}

			return false;
		}

		internal static void SetFocus(Control control)
		{
			if (control == FocusedControl)
			{
				return;
			}

			FocusNative(control);

			Control old = FocusedControl;
			FocusedControl = control;
			if (old != null)
			{
				old.RaiseLeave();
				old.RaiseLostFocus();
				old.Invalidate();
			}

			if (control != null)
			{
				control.RaiseEnter();
				control.RaiseGotFocus();
				control.Invalidate();
			}

			RequestRender();
		}

		// --- pop-ups: a combo box's list, a context menu, a tool tip ---

		internal static void ShowPopup(Popup popup)
		{
			ClosePopup();
			// Kept inside the window.
			Rectangle all = new Rectangle(0, 0, (int)(FramebufferSize.Width / Scale), (int)(FramebufferSize.Height / Scale));
			int x = Math.Max(0, Math.Min(popup.Left, all.Right - popup.Width));
			int y = popup.Top;
			if (y + popup.Height > all.Bottom)
			{
				y = Math.Max(0, popup.FlipTop - popup.Height);
			}

			popup.Location = new Point(x, y);
			ActivePopup = popup;
			RequestRender();
		}

		internal static void ClosePopup()
		{
			if (ActivePopup == null)
			{
				return;
			}

			Popup popup = ActivePopup;
			ActivePopup = null;
			if (popup.surface != null)
			{
				popup.surface.Dispose();
				popup.surface = null;
			}

			popup.RaiseClosed();
			RequestRender();
		}

		private static long toolTipSince;
		private static ToolTipPopup toolTip;

		private static void UpdateToolTip()
		{
			if (toolTip != null || ActivePopup != null || CapturedControl != null || Now - toolTipSince < 600 || HoveredControl == null)
			{
				return;
			}

			string text = ToolTip.TextFor(HoveredControl);
			if (string.IsNullOrEmpty(text))
			{
				toolTipSince = long.MaxValue / 2;
				return;
			}

			toolTip = new ToolTipPopup(text);
			toolTip.Location = new Point(mouse.X + 12, mouse.Y + 18);
			toolTip.FlipTop = mouse.Y - 4;
			ShowPopup(toolTip);
		}

		private static void HideToolTip()
		{
			if (toolTip != null)
			{
				if (ActivePopup == toolTip)
				{
					ClosePopup();
				}

				toolTip = null;
			}
		}

		internal static bool IsToolTip(Popup popup) => popup == toolTip;
	}

	/// <summary>A control drawn above the forms, at a place in the window (a list, a menu, a tip).</summary>
	internal abstract class Popup : Control
	{
		/// <summary>Where the popup goes above its anchor if it does not fit below.</summary>
		internal int FlipTop;

		internal override bool DrawsItself => true;

		internal virtual bool HandleKey(KeyEventArgs e) => false;

		internal event EventHandler Closed;

		internal void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
	}

	internal sealed class ToolTipPopup : Popup
	{
		internal ToolTipPopup(string text)
		{
			Text = text;
			SizeF size = MeasureText(text, DefaultFont, 400);
			Size = new Size((int)Math.Ceiling(size.Width) + 10, (int)Math.Ceiling(size.Height) + 6);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			e.Graphics.FillRectangle(Brushes.White, 0, 0, Width, Height);
			DrawBorder(e.Graphics, ClientRectangle, Color.FromArgb(118, 118, 118));
			DrawText(e.Graphics, Text, DefaultFont, Color.Black, new RectangleF(5, 3, Width - 10, Height - 6), ContentAlignment.TopLeft, true);
		}
	}

	public sealed class Cursor
	{
		internal Cursor(string name)
		{
			Name = name;
		}

		internal string Name { get; }

		public static Cursor Current { get; set; }

		public static Point Position => Point.Empty;

		public override string ToString() => "[Cursor: " + Name + "]";
	}

	public static class Cursors
	{
		public static readonly Cursor Default = new Cursor("Default");
		public static readonly Cursor Arrow = new Cursor("Arrow");
		public static readonly Cursor Hand = new Cursor("Hand");
		public static readonly Cursor WaitCursor = new Cursor("WaitCursor");
		public static readonly Cursor AppStarting = new Cursor("AppStarting");
		public static readonly Cursor IBeam = new Cursor("IBeam");
		public static readonly Cursor No = new Cursor("No");
		public static readonly Cursor SizeAll = new Cursor("SizeAll");
	}

	public static class Application
	{
		private static Assembly Program => Assembly.GetEntryAssembly() ?? typeof(Application).Assembly;

		public static string ProductName
		{
			get
			{
				object[] attributes = Program.GetCustomAttributes(typeof(AssemblyProductAttribute), false);
				return attributes.Length > 0 ? ((AssemblyProductAttribute)attributes[0]).Product : Program.GetName().Name;
			}
		}

		public static string ProductVersion
		{
			get
			{
				object[] attributes = Program.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
				return attributes.Length > 0 ? ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion : Program.GetName().Version.ToString();
			}
		}

		public static string CompanyName
		{
			get
			{
				object[] attributes = Program.GetCustomAttributes(typeof(AssemblyCompanyAttribute), false);
				return attributes.Length > 0 ? ((AssemblyCompanyAttribute)attributes[0]).Company : string.Empty;
			}
		}

		public static string StartupPath => System.IO.Path.GetDirectoryName(Program.Location);

		public static string ExecutablePath => Program.Location;

		public static void DoEvents()
		{
			Host.PumpOnce(false);
		}

		public static void EnableVisualStyles()
		{
		}

		public static void SetCompatibleTextRenderingDefault(bool value)
		{
		}

		public static void Exit()
		{
			foreach (Form form in Host.Forms.ToArray())
			{
				form.Close();
			}
		}
	}

	public sealed class Screen
	{
		private Screen()
		{
		}

		public static Screen PrimaryScreen => new Screen();

		public static Screen FromControl(Control control) => new Screen();

		public static Screen[] AllScreens => new[] { new Screen() };

		public Rectangle Bounds => new Rectangle(0, 0, DisplayDevice.Default.Width, DisplayDevice.Default.Height);

		public Rectangle WorkingArea => Bounds;

		public bool Primary => true;
	}
}
