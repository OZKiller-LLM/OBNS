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
using OpenBveApi.Hosts;
using OpenBveApi.Math;
using OpenBveApi.Trains;

namespace OpenBveApi.FunctionScripting
{
	/// <summary>
	/// Stands in for the CS-Script backed animation script of the desktop build.
	/// </summary>
	/// <remarks>
	/// Upstream compiles C# animation scripts at runtime with CS-Script, which needs Roslyn to
	/// emit and load an assembly — unavailable on Android under AOT. Objects that use a `.cs`
	/// animation script therefore cannot animate; `.animated` files, which are the common case,
	/// are unaffected.
	///
	/// The constructor throws so that upstream's existing try/catch around script loading
	/// reports the object as unsupported and carries on loading the route.
	/// </remarks>
	public class CSAnimationScript : AnimationScript
	{
		/// <summary>The result of the last invocation.</summary>
		public double LastResult { get; set; }

		/// <summary>The maximum value this script may return.</summary>
		public double Maximum { get; set; } = double.NaN;

		/// <summary>The minimum value this script may return.</summary>
		public double Minimum { get; set; } = double.NaN;

		/// <summary>Not supported on Android.</summary>
		public CSAnimationScript(HostInterface host, string path)
		{
			throw new NotSupportedException("C# animation scripts require runtime compilation, which is not available on Android: " + path);
		}

		/// <inheritdoc />
		public double ExecuteScript(AbstractTrain train, int carIndex, Vector3 position, double trackPosition, int sectionIndex, bool isPartOfTrain, double timeElapsed, int currentState)
		{
			return 0.0;
		}

		/// <inheritdoc />
		public AnimationScript Clone()
		{
			return this;
		}
	}
}
