// OBNS: not an upstream file. Direct3D 11's drawing calls through their COM tables (OBNS/DIRECT3D.md): shaders, states,
// buffers' views, and the immediate context's binding and drawing; and the HLSL compiler (d3dcompiler_47.dll, part of
// Windows since 8.1).

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBve.Graphics.Direct3D11
{
	internal static unsafe partial class Dx
	{
		// D3D11_BLEND, D3D11_BLEND_OP, D3D11_COMPARISON_FUNC, D3D11_STENCIL_OP, D3D11_CULL_MODE, D3D11_FILL_MODE
		internal const uint BlendZero = 1, BlendOne = 2, BlendSrcAlpha = 5, BlendInvSrcAlpha = 6, BlendOpAdd = 1;
		internal const uint ComparisonLess = 2, ComparisonLessEqual = 4, ComparisonAlways = 8, StencilOpKeep = 1;
		internal const uint CullNone = 1, CullFront = 2, FillSolid = 3;

		// D3D11_FILTER, D3D11_TEXTURE_ADDRESS_MODE
		internal const uint FilterPoint = 0x0, FilterMinPointMagMipLinear = 0x5, FilterLinear = 0x15, FilterAnisotropic = 0x55;
		internal const uint AddressWrap = 1, AddressClamp = 3;

		// DXGI_FORMAT (the others in Dx.cs)
		internal const uint FormatR32G32B32A32Float = 2;

		// D3D11_INPUT_CLASSIFICATION, D3D11_PRIMITIVE_TOPOLOGY, D3D11_MAP
		internal const uint InputPerVertex = 0, InputPerInstance = 1, TopologyTriangleList = 4, MapWriteDiscard = 4;

		[StructLayout(LayoutKind.Sequential)]
		internal struct RenderTargetBlendDesc
		{
			public int BlendEnable;
			public uint SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha;
			public byte RenderTargetWriteMask;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct BlendDesc
		{
			public int AlphaToCoverageEnable, IndependentBlendEnable;
			public RenderTargetBlendDesc Target0, Target1, Target2, Target3, Target4, Target5, Target6, Target7;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct StencilOpDesc
		{
			public uint StencilFailOp, StencilDepthFailOp, StencilPassOp, StencilFunc;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DepthStencilDesc
		{
			public int DepthEnable;
			public uint DepthWriteMask, DepthFunc;
			public int StencilEnable;
			public byte StencilReadMask, StencilWriteMask;
			public StencilOpDesc FrontFace, BackFace;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct RasterizerDesc
		{
			public uint FillMode, CullMode;
			public int FrontCounterClockwise, DepthBias;
			public float DepthBiasClamp, SlopeScaledDepthBias;
			public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SamplerDesc
		{
			public uint Filter, AddressU, AddressV, AddressW;
			public float MipLodBias;
			public uint MaxAnisotropy, ComparisonFunc;
			public float BorderR, BorderG, BorderB, BorderA;
			public float MinLod, MaxLod;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct InputElementDesc
		{
			public IntPtr SemanticName;
			public uint SemanticIndex, Format, InputSlot, AlignedByteOffset, InputSlotClass, InstanceDataStepRate;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Viewport
		{
			public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MappedSubresource
		{
			public void* Data;
			public uint RowPitch, DepthPitch;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct ShaderMacro
		{
			public IntPtr Name, Definition;
		}

		[DllImport("d3dcompiler_47.dll")]
		private static extern int D3DCompile(byte* source, UIntPtr size, IntPtr sourceName, ShaderMacro* defines, IntPtr include, IntPtr entryPoint, IntPtr target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

		private const uint CompileOptimizationLevel3 = 1 << 15;

		/// <summary>HLSL compiled (D3DCompile): the bytecode, or an exception with the compiler's messages.</summary>
		internal static byte[] Compile(string source, string name, string entryPoint, string target, params string[] defines)
		{
			byte[] text = Encoding.UTF8.GetBytes(source);
			IntPtr nameText = Marshal.StringToHGlobalAnsi(name), entryText = Marshal.StringToHGlobalAnsi(entryPoint), targetText = Marshal.StringToHGlobalAnsi(target);
			int count = defines.Length / 2;
			ShaderMacro* macros = stackalloc ShaderMacro[count + 1];
			IntPtr[] strings = new IntPtr[count * 2];
			try
			{
				for (int i = 0; i < count * 2; i++)
				{
					strings[i] = Marshal.StringToHGlobalAnsi(defines[i]);
				}

				for (int i = 0; i < count; i++)
				{
					macros[i] = new ShaderMacro { Name = strings[2 * i], Definition = strings[2 * i + 1] };
				}

				macros[count] = default(ShaderMacro);
				int hr;
				IntPtr code, errors;
				fixed (byte* data = text)
				{
					hr = D3DCompile(data, (UIntPtr)text.Length, nameText, macros, IntPtr.Zero, entryText, targetText, CompileOptimizationLevel3, 0, out code, out errors);
				}

				string messages = errors == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(BlobPointer(errors), (int)BlobSize(errors));
				Release(errors);
				if (hr < 0 || code == IntPtr.Zero)
				{
					Release(code);
					throw new InvalidOperationException("compiling " + name + " (" + entryPoint + ", " + target + ") failed (0x" + hr.ToString("X8") + "): " + messages);
				}

				byte[] bytecode = new byte[(int)BlobSize(code)];
				Marshal.Copy(BlobPointer(code), bytecode, 0, bytecode.Length);
				Release(code);
				return bytecode;
			}
			finally
			{
				foreach (IntPtr text2 in strings)
				{
					Marshal.FreeHGlobal(text2);
				}

				Marshal.FreeHGlobal(nameText);
				Marshal.FreeHGlobal(entryText);
				Marshal.FreeHGlobal(targetText);
			}
		}

		/// <summary>ID3DBlob::GetBufferPointer (3).</summary>
		internal static IntPtr BlobPointer(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Table(blob)[3])(blob);

		/// <summary>ID3DBlob::GetBufferSize (4).</summary>
		internal static ulong BlobSize(IntPtr blob) => (ulong)((delegate* unmanaged[Stdcall]<IntPtr, UIntPtr>)Table(blob)[4])(blob);

		// --- the device ---

		// DXGI_FORMAT and D3D11_FILTER for the shadow maps: the depth stored typeless, written as depth, read as a colour
		internal const uint FormatR32Typeless = 39, FilterComparisonLinear = 0x94;

		[StructLayout(LayoutKind.Sequential)]
		internal struct DepthStencilViewDesc
		{
			public uint Format, ViewDimension, Flags;
			public uint MipSlice, FirstArraySlice, ArraySize;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ShaderResourceViewDesc
		{
			public uint Format, ViewDimension;
			public uint MostDetailedMip, MipLevels, FirstArraySlice, ArraySize;
		}

		internal const uint DsvDimensionTexture2DArray = 4, SrvDimensionTexture2DArray = 5;

		/// <summary>ID3D11Device::CreateShaderResourceView (7), described.</summary>
		internal static IntPtr CreateShaderResourceView(IntPtr device, IntPtr resource, ShaderResourceViewDesc desc)
		{
			IntPtr view;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ShaderResourceViewDesc*, IntPtr*, int>)Table(device)[7])(device, resource, &desc, &view), "ID3D11Device::CreateShaderResourceView");
			return view;
		}

		/// <summary>ID3D11Device::CreateDepthStencilView (10), described.</summary>
		internal static IntPtr CreateDepthStencilView(IntPtr device, IntPtr resource, DepthStencilViewDesc desc)
		{
			IntPtr view;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DepthStencilViewDesc*, IntPtr*, int>)Table(device)[10])(device, resource, &desc, &view), "ID3D11Device::CreateDepthStencilView");
			return view;
		}

		/// <summary>ID3D11Device::CreateInputLayout (11).</summary>
		internal static IntPtr CreateInputLayout(IntPtr device, InputElementDesc* elements, uint count, byte[] vertexShader)
		{
			IntPtr layout;
			fixed (byte* code = vertexShader)
			{
				Check(((delegate* unmanaged[Stdcall]<IntPtr, InputElementDesc*, uint, byte*, UIntPtr, IntPtr*, int>)Table(device)[11])(device, elements, count, code, (UIntPtr)vertexShader.Length, &layout), "ID3D11Device::CreateInputLayout");
			}

			return layout;
		}

		/// <summary>ID3D11Device::CreateVertexShader (12).</summary>
		internal static IntPtr CreateVertexShader(IntPtr device, byte[] bytecode)
		{
			IntPtr shader;
			fixed (byte* code = bytecode)
			{
				Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, UIntPtr, IntPtr, IntPtr*, int>)Table(device)[12])(device, code, (UIntPtr)bytecode.Length, IntPtr.Zero, &shader), "ID3D11Device::CreateVertexShader");
			}

			return shader;
		}

		/// <summary>ID3D11Device::CreatePixelShader (15).</summary>
		internal static IntPtr CreatePixelShader(IntPtr device, byte[] bytecode)
		{
			IntPtr shader;
			fixed (byte* code = bytecode)
			{
				Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, UIntPtr, IntPtr, IntPtr*, int>)Table(device)[15])(device, code, (UIntPtr)bytecode.Length, IntPtr.Zero, &shader), "ID3D11Device::CreatePixelShader");
			}

			return shader;
		}

		/// <summary>ID3D11Device::CreateBlendState (20).</summary>
		internal static IntPtr CreateBlendState(IntPtr device, BlendDesc desc)
		{
			IntPtr state;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, BlendDesc*, IntPtr*, int>)Table(device)[20])(device, &desc, &state), "ID3D11Device::CreateBlendState");
			return state;
		}

		/// <summary>ID3D11Device::CreateDepthStencilState (21).</summary>
		internal static IntPtr CreateDepthStencilState(IntPtr device, DepthStencilDesc desc)
		{
			IntPtr state;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, DepthStencilDesc*, IntPtr*, int>)Table(device)[21])(device, &desc, &state), "ID3D11Device::CreateDepthStencilState");
			return state;
		}

		/// <summary>ID3D11Device::CreateRasterizerState (22).</summary>
		internal static IntPtr CreateRasterizerState(IntPtr device, RasterizerDesc desc)
		{
			IntPtr state;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, RasterizerDesc*, IntPtr*, int>)Table(device)[22])(device, &desc, &state), "ID3D11Device::CreateRasterizerState");
			return state;
		}

		/// <summary>ID3D11Device::CreateSamplerState (23).</summary>
		internal static IntPtr CreateSamplerState(IntPtr device, SamplerDesc desc)
		{
			IntPtr state;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, SamplerDesc*, IntPtr*, int>)Table(device)[23])(device, &desc, &state), "ID3D11Device::CreateSamplerState");
			return state;
		}

		// --- the immediate context ---

		/// <summary>ID3D11DeviceContext::VSSetConstantBuffers (7), one at a slot.</summary>
		internal static void VSSetConstantBuffer(IntPtr context, uint slot, IntPtr buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Table(context)[7])(context, slot, 1, &buffer);

		/// <summary>ID3D11DeviceContext::PSSetShaderResources (8), one at a slot.</summary>
		internal static void PSSetShaderResource(IntPtr context, uint slot, IntPtr view) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Table(context)[8])(context, slot, 1, &view);

		/// <summary>ID3D11DeviceContext::PSSetShader (9).</summary>
		internal static void PSSetShader(IntPtr context, IntPtr shader) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)Table(context)[9])(context, shader, null, 0);

		/// <summary>ID3D11DeviceContext::PSSetSamplers (10), one at a slot.</summary>
		internal static void PSSetSampler(IntPtr context, uint slot, IntPtr sampler) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Table(context)[10])(context, slot, 1, &sampler);

		/// <summary>ID3D11DeviceContext::VSSetShader (11).</summary>
		internal static void VSSetShader(IntPtr context, IntPtr shader) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)Table(context)[11])(context, shader, null, 0);

		/// <summary>ID3D11DeviceContext::Map (14).</summary>
		internal static void* Map(IntPtr context, IntPtr resource, uint mapType)
		{
			MappedSubresource mapped;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)Table(context)[14])(context, resource, 0, mapType, 0, &mapped), "ID3D11DeviceContext::Map");
			return mapped.Data;
		}

		/// <summary>ID3D11DeviceContext::Unmap (15).</summary>
		internal static void Unmap(IntPtr context, IntPtr resource) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Table(context)[15])(context, resource, 0);

		/// <summary>ID3D11DeviceContext::PSSetConstantBuffers (16), one at a slot.</summary>
		internal static void PSSetConstantBuffer(IntPtr context, uint slot, IntPtr buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Table(context)[16])(context, slot, 1, &buffer);

		/// <summary>ID3D11DeviceContext::IASetInputLayout (17).</summary>
		internal static void IASetInputLayout(IntPtr context, IntPtr layout) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Table(context)[17])(context, layout);

		/// <summary>ID3D11DeviceContext::IASetVertexBuffers (18), one at a slot.</summary>
		internal static void IASetVertexBuffer(IntPtr context, uint slot, IntPtr buffer, uint stride, uint offset) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Table(context)[18])(context, slot, 1, &buffer, &stride, &offset);

		/// <summary>ID3D11DeviceContext::IASetIndexBuffer (19).</summary>
		internal static void IASetIndexBuffer(IntPtr context, IntPtr buffer, uint format, uint offset) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, void>)Table(context)[19])(context, buffer, format, offset);

		/// <summary>ID3D11DeviceContext::DrawIndexedInstanced (20).</summary>
		internal static void DrawIndexedInstanced(IntPtr context, uint indexCount, uint instanceCount, uint startIndex, int baseVertex, uint startInstance) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int, uint, void>)Table(context)[20])(context, indexCount, instanceCount, startIndex, baseVertex, startInstance);

		/// <summary>ID3D11DeviceContext::IASetPrimitiveTopology (24).</summary>
		internal static void IASetPrimitiveTopology(IntPtr context, uint topology) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)Table(context)[24])(context, topology);

		/// <summary>ID3D11DeviceContext::VSSetShaderResources (25), one at a slot.</summary>
		internal static void VSSetShaderResource(IntPtr context, uint slot, IntPtr view) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Table(context)[25])(context, slot, 1, &view);

		/// <summary>ID3D11DeviceContext::OMSetBlendState (35), with no blend factor and every sample.</summary>
		internal static void OMSetBlendState(IntPtr context, IntPtr state) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, uint, void>)Table(context)[35])(context, state, null, 0xFFFFFFFF);

		/// <summary>ID3D11DeviceContext::OMSetDepthStencilState (36).</summary>
		internal static void OMSetDepthStencilState(IntPtr context, IntPtr state) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Table(context)[36])(context, state, 0);

		/// <summary>ID3D11DeviceContext::RSSetState (43).</summary>
		internal static void RSSetState(IntPtr context, IntPtr state) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Table(context)[43])(context, state);

		/// <summary>ID3D11DeviceContext::RSSetViewports (44), one.</summary>
		internal static void RSSetViewport(IntPtr context, Viewport viewport) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, Viewport*, void>)Table(context)[44])(context, 1, &viewport);
	}
}
