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

// OBNS: not an upstream file. The world layer drawn by an API of its own (OBNS/VULKAN.md, OBNS/DIRECT3D.md): what the
// renderer calls, whichever API draws it (Vulkan or Direct3D 11).

using System;
using LibRender2;
using LibRender2.ShadowMapping;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using TrainManager.Trains;

namespace OpenBve.Graphics
{
	/// <summary>The world layer's blend modes: none, upstream's normal blending, additive.</summary>
	internal enum WorldBlend { None = 0, Normal = 1, Additive = 2 }

	/// <summary>A frame's faces, in upstream's passes and order, for the API drawing the world layer.</summary>
	internal interface IWorldScene
	{
		/// <summary>Starts a frame's records (the world layer).</summary>
		void Begin(BaseRenderer renderer);

		/// <summary>Starts the cab layer's records: upstream's cab projection and view, lit as a 3D cab or not (a 2D panel).</summary>
		void BeginCab(Matrix4D projection, Matrix4D view, bool lit, Vector3 lightPosition);

		/// <summary>A pass of upstream's: its blending, alpha test, depth writes, whether its faces keep their order, and depth testing.</summary>
		void Pass(WorldBlend blend, int alphaFunction, float alphaReference, bool depthWrite, bool ordered, bool depthTest = true);

		/// <summary>The alpha test for the faces after, within the pass.</summary>
		void SetAlphaFunction(int alphaFunction, float alphaReference);

		/// <summary>A face drawn in the current pass.</summary>
		void Face(ObjectState state, MeshFace face);

		/// <summary>The shadow maps' size and cascades (made again when they change; 1 and 1 with shadows off).</summary>
		void EnsureShadowMaps(int resolution, int cascades);

		/// <summary>The frame's shadows as upstream's shadow pass makes them (no caster: none).</summary>
		void SetShadows(CascadedShadowCaster caster, float strength, bool smooth, float filterRadius, float userBias, float normalBiasTexels);
	}

	/// <summary>The API drawing the world layer: a frame's start and finish, the cab layer's composite, preloads.</summary>
	internal interface IWorldLayer
	{
		IWorldScene Scene { get; }

		/// <summary>Whether this frame has a cab layer to lay over the frame (CompositeCab).</summary>
		bool DrawsCab { get; }

		/// <summary>The main thread's last wait for the frame's thread (ticks), for the frame statistics.</summary>
		long LastJoinTicks { get; }

		/// <summary>The frame's records are complete: the API starts drawing them (over OpenGL's viewport).</summary>
		void Start(int x, int y, int width, int height);

		/// <summary>Lays the world layer over what OpenGL has drawn, writing its depth.</summary>
		void Finish();

		/// <summary>The frame's time gathering faces and finishing, for the log.</summary>
		void Timed(long gather, long draw);

		/// <summary>Lays the cab layer over the frame.</summary>
		void CompositeCab();

		/// <summary>Makes every texture the route has registered (upstream's "load textures in advance").</summary>
		void LoadAllTextures();

		/// <summary>Makes a train's cab textures (its exterior's are made as they come into view).</summary>
		void PreloadTrain(TrainBase train);
	}

	/// <summary>Which API draws the world layer, if any (null: OpenGL, as upstream).</summary>
	internal static class WorldLayer
	{
		/// <summary>OpenGL's alpha functions, as the faces' alpha tests take them.</summary>
		internal const int AlphaNever = 512, AlphaLess = 513, AlphaEqual = 514, AlphaGreater = 516;

		internal static IWorldLayer Instance => Vulkan.VulkanWorld.Enabled ? Vulkan.VulkanWorld.Instance : Direct3D11.D3D11World.Enabled ? (IWorldLayer)Direct3D11.D3D11World.Instance : Direct3D12.D3D12World.Enabled ? (IWorldLayer)Direct3D12.D3D12World.Instance : Direct3D9.D3D9World.Enabled ? (IWorldLayer)Direct3D9.D3D9World.Instance : Metal.MetalWorld.Enabled ? Metal.MetalWorld.Instance : null;

		internal static bool Enabled => Instance != null;

		/// <summary>Makes the layer the options (or the switches) ask for, where it can share images with OpenGL; logs which, and why.</summary>
		internal static void Initialize()
		{
			// The renderer asked for, turned off by its switch: said here, as the layer itself says nothing and what is tried after has another reason.
			ObnsOptions.RendererKind asked = ObnsOptions.Renderer;
			string askedName = asked == ObnsOptions.RendererKind.Metal ? "METAL" : asked == ObnsOptions.RendererKind.Direct3D9 ? "D3D9" : asked == ObnsOptions.RendererKind.Direct3D11 ? "D3D11" : asked == ObnsOptions.RendererKind.Direct3D12 ? "D3D12" : null;
			if (askedName != null && Environment.GetEnvironmentVariable("OBNS_" + askedName) == "0")
			{
				Log("OBNS " + asked + " world layer off (OBNS_" + askedName + "=0)");
			}

			// Direct3D 12, 11 or 9.0c when the options choose it (OBNS/DIRECT3D.md), Direct3D 11 where 12 cannot be used; Vulkan
			// when the options choose it, or where Direct3D cannot be used.
			Direct3D12.D3D12World.Initialize();
			if (!Direct3D12.D3D12World.Enabled)
			{
				Direct3D11.D3D11World.Initialize();
			}

			Direct3D9.D3D9World.Initialize();
			// Metal on macOS when the options choose it (OBNS/METAL.md).
			Metal.MetalWorld.Initialize();
			if (!Direct3D12.D3D12World.Enabled && !Direct3D11.D3D11World.Enabled && !Direct3D9.D3D9World.Enabled && !Metal.MetalWorld.Enabled)
			{
				Vulkan.VulkanWorld.Initialize();
			}

			// The options show what drew the game, and why if it isn't what was asked for.
			ObnsOptions.RendererKind drawn = Metal.MetalWorld.Enabled ? ObnsOptions.RendererKind.Metal : Direct3D9.D3D9World.Enabled ? ObnsOptions.RendererKind.Direct3D9 : Direct3D12.D3D12World.Enabled ? ObnsOptions.RendererKind.Direct3D12 : Direct3D11.D3D11World.Enabled ? ObnsOptions.RendererKind.Direct3D11 : Vulkan.VulkanWorld.Enabled ? ObnsOptions.RendererKind.Vulkan : ObnsOptions.RendererKind.OpenGL;
			ObnsOptions.SetLastDrawn(drawn, drawn != ObnsOptions.Renderer ? firstReason : null);
		}

		/// <summary>The first reason given this game for a layer not being used: the one asked for comes first, and what is tried after has less to say.</summary>
		private static string firstReason;

		/// <summary>Logs a line of a layer's, keeping what it says of a layer that doesn't draw for the options.</summary>
		internal static void Log(string text)
		{
			Program.FileSystem.AppendToLogFile(text);
			int layer = text.IndexOf(" world layer ", StringComparison.Ordinal);
			if (layer < 0 || text.IndexOf(" world layer on", StringComparison.Ordinal) == layer)
			{
				return;
			}

			// The first line, in fewer words: a failed frame's line goes on with the exception.
			int end = text.IndexOfAny(new[] { '\r', '\n' });
			string line = (end < 0 ? text : text.Substring(0, end)).Replace("OBNS ", "").Replace(" world layer", "").Replace("; OpenGL draws it", "");
			line = line.Length > 300 ? line.Substring(0, 300) : line;
			if (text.IndexOf(" world layer stopped", StringComparison.Ordinal) == layer)
			{
				ObnsOptions.SetLastDrawn(ObnsOptions.RendererKind.OpenGL, line);
			}
			else if (firstReason == null)
			{
				firstReason = line;
			}
		}

		/// <summary>Which renderer draws the world, for the debug overlay.</summary>
		internal static string RendererName => Metal.MetalWorld.Enabled ? Metal.MetalWorld.RendererName : Direct3D9.D3D9World.Enabled ? Direct3D9.D3D9World.RendererName : Direct3D12.D3D12World.Enabled ? Direct3D12.D3D12World.RendererName : Direct3D11.D3D11World.Enabled ? Direct3D11.D3D11World.RendererName : Vulkan.VulkanWorld.RendererName;

		/// <summary>The layer's line in the frame statistics.</summary>
		internal static string Summary => Metal.MetalWorld.Enabled ? Metal.MetalWorld.Summary : Direct3D9.D3D9World.Enabled ? Direct3D9.D3D9World.Summary : Direct3D12.D3D12World.Enabled ? Direct3D12.D3D12World.Summary : Direct3D11.D3D11World.Enabled ? Direct3D11.D3D11World.Summary : Vulkan.VulkanWorld.Summary;
	}
}
