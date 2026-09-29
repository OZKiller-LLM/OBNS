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
using System.Text;

namespace OpenBve.GLES
{
	/// <summary>
	/// Rewrites OpenBVE's desktop GLSL into OpenGL ES 3 dialect as it is handed to the driver.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The shaders themselves are already modern: they use in/out blocks, texture(), explicit
	/// attribute locations and std140 uniform blocks, all of which exist in GLSL ES 3.00. What
	/// desktop GLSL does not need, and ES does, is the version token and — in fragment shaders —
	/// an explicit default precision, without which compilation fails outright.
	/// </para>
	/// <para>
	/// Translating here rather than forking the .vert/.frag files keeps the upstream assets as
	/// the single source of truth, so shader changes in future OpenBVE releases carry over
	/// without a merge.
	/// </para>
	/// </remarks>
	public static class ShaderTranslator
	{
		/// <summary>The version directive every shader is given.</summary>
		private const string EsVersion = "#version 300 es";

		/// <summary>
		/// Default precisions injected after the version directive. A fragment shader with no
		/// declared float precision is a compile error in GLSL ES; samplers otherwise default to
		/// lowp, which is not enough for depth comparisons in the shadow cascades.
		/// </summary>
		private static readonly string[] DefaultPrecision =
		{
			"precision highp float;",
			"precision highp int;",
			"precision highp sampler2D;",
			"precision highp sampler2DShadow;"
		};

		/// <summary>Translates a desktop GLSL shader into its GLSL ES 3.00 equivalent.</summary>
		/// <param name="source">The shader source as shipped with OpenBVE.</param>
		/// <returns>The translated source.</returns>
		public static string ToOpenGLES(string source)
		{
			if (string.IsNullOrEmpty(source))
			{
				return source;
			}

			if (source.IndexOf("#version 300 es", StringComparison.Ordinal) >= 0)
			{
				// Already ES: a shader the Android front end supplied itself.
				return source;
			}

			/*
			 * The version directive must be on the very first line for an ES driver — desktop GL
			 * tolerates comments ahead of it, and every OpenBVE shader opens with a 24 line
			 * licence header. An ES driver that does not see the directive first silently
			 * compiles as GLSL ES 1.00, where there are no implicit int-to-float conversions and
			 * sampler2DShadow is a reserved word, so the failures surface far from the cause.
			 *
			 * So the directive and the default precisions are emitted first, and the original
			 * directive is dropped from the body. The licence header stays, just below them.
			 */
			StringBuilder translated = new StringBuilder(source.Length + 256);
			translated.AppendLine(EsVersion);
			foreach (string precision in DefaultPrecision)
			{
				translated.AppendLine(precision);
			}

			string[] lines = source.Replace("\r\n", "\n").Split('\n');
			bool versionDropped = false;
			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i];
				if (!versionDropped && line.TrimStart().StartsWith("#version", StringComparison.Ordinal))
				{
					versionDropped = true;
					continue;
				}

				translated.AppendLine(line);
			}

			return translated.ToString();
		}
	}
}
