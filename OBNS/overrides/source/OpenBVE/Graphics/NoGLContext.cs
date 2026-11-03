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

// OBNS: not an upstream file. A game window with no OpenGL context behind it (OBNS/RENDERERS.md).

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using OpenTK;
using OpenTK.Graphics;

namespace OpenBve.Graphics
{
	/// <summary>
	/// OpenTK's GameWindow always makes a context, which loads the OpenGL driver and gives the window a pixel format. The game's
	/// window class is upstream's, so OpenTK's platform factory (a static it reads, its type internal) is wrapped instead: the
	/// wrapper passes everything on, but a context asked for is OpenTK's own dummy.
	/// </summary>
	internal sealed class NoGLContext : RealProxy
	{
		private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
		private static readonly ContextHandle dummyHandle = new ContextHandle((IntPtr)1);
		private readonly object factory;

		private NoGLContext(Type factoryInterface, object factory) : base(factoryInterface)
		{
			this.factory = factory;
		}

		[DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
		[DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
		[DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
		[DllImport("gdi32.dll")] private static extern int GetPixelFormat(IntPtr dc);

		/// <summary>What the system says of OpenGL in this window, for the log: no current context, and no pixel format.</summary>
		internal static string Describe(IntPtr window)
		{
			if (GLStub.AvailableOnMac)
			{
				return "OpenGL context current: " + (Metal.Mtl.CGLGetCurrentContext() != IntPtr.Zero ? "yes" : "none");
			}

			IntPtr dc = GetDC(window);
			int pixelFormat = GetPixelFormat(dc);
			ReleaseDC(window, dc);
			return "OpenGL context current: " + (wglGetCurrentContext() != IntPtr.Zero ? "yes" : "none") + ", the window's pixel format: " + (pixelFormat != 0 ? pixelFormat.ToString() : "none");
		}

		private static IntPtr NoAddress(string function) => IntPtr.Zero;

		private static ContextHandle Current() => dummyHandle;

		/// <summary>Windows made from now have no OpenGL context.</summary>
		internal static void Install()
		{
			Assembly toolkit = typeof(GameWindow).Assembly;
			Type factoryType = toolkit.GetType("OpenTK.Platform.Factory", true);
			RuntimeHelpers.RunClassConstructor(factoryType.TypeHandle);
			FieldInfo defaultFactory = factoryType.GetField("<Default>k__BackingField", Any);
			object factory = defaultFactory.GetValue(null);
			NoGLContext proxy = new NoGLContext(toolkit.GetType("OpenTK.Platform.IPlatformFactory", true), factory);
			defaultFactory.SetValue(null, proxy.GetTransparentProxy());

			// Which context is current is asked of the system otherwise: the dummy is, on the thread that made it current.
			FieldInfo current = typeof(GraphicsContext).GetField("GetCurrentContext", Any);
			current.SetValue(null, Delegate.CreateDelegate(current.FieldType, typeof(NoGLContext).GetMethod(nameof(Current), Any)));
		}

		public override IMessage Invoke(IMessage message)
		{
			IMethodCallMessage call = (IMethodCallMessage)message;
			try
			{
				object result;
				switch (call.MethodName)
				{
					case "CreateGLContext":
						Type dummy = typeof(GameWindow).Assembly.GetType("OpenTK.Platform.Dummy.DummyGLContext", true);
						Delegate loader = Delegate.CreateDelegate(typeof(GraphicsContext).GetNestedType("GetAddressDelegate", Any), typeof(NoGLContext).GetMethod(nameof(NoAddress), Any));
						result = Activator.CreateInstance(dummy, Any, null, new object[] { dummyHandle, loader }, null);
						break;
					case "CreateGetCurrentGraphicsContext":
						result = Delegate.CreateDelegate(typeof(GraphicsContext).GetField("GetCurrentContext", Any).FieldType, typeof(NoGLContext).GetMethod(nameof(Current), Any));
						break;
					default:
						result = call.MethodBase.Invoke(factory, call.Args);
						break;
				}

				return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
			}
			catch (TargetInvocationException ex)
			{
				return new ReturnMessage(ex.InnerException, call);
			}
			catch (Exception ex)
			{
				return new ReturnMessage(ex, call);
			}
		}
	}
}
