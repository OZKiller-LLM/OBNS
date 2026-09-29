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

using System.Text;
using Android.Content;
using OpenBveApi.Routes;
using OpenBveApi.Trains;

namespace OpenBve.Android
{
	/// <summary>
	/// What the menu asks the game to load: upstream's formMain.LaunchParameters, carried from the
	/// menu to the game activity as intent extras.
	/// </summary>
	public class LaunchParameters
	{
		private const string RouteFileExtra = "route_file";
		private const string RouteEncodingExtra = "route_encoding";
		private const string TrainFolderExtra = "train_folder";
		private const string TrainEncodingExtra = "train_encoding";
		private const string ImageFileExtra = "image_file";

		/// <summary>The route file, or null for the first route under the content folder.</summary>
		public string RouteFile;

		/// <summary>The route's encoding, or null to detect it.</summary>
		public Encoding RouteEncoding;

		/// <summary>The train folder, or null for the first train under the content folder.</summary>
		public string TrainFolder;

		/// <summary>The train's encoding, or null for UTF-8.</summary>
		public Encoding TrainEncoding;

		/// <summary>The route's picture, for the loading screen, or null.</summary>
		public string ImageFile;

		/// <summary>Writes the parameters into an intent.</summary>
		public void WriteTo(Intent intent)
		{
			intent.PutExtra(RouteFileExtra, RouteFile);
			intent.PutExtra(TrainFolderExtra, TrainFolder);
			intent.PutExtra(ImageFileExtra, ImageFile);
			if (RouteEncoding != null)
			{
				intent.PutExtra(RouteEncodingExtra, RouteEncoding.CodePage);
			}

			if (TrainEncoding != null)
			{
				intent.PutExtra(TrainEncodingExtra, TrainEncoding.CodePage);
			}
		}

		/// <summary>Reads the parameters from an intent; missing extras stay null.</summary>
		public static LaunchParameters ReadFrom(Intent intent)
		{
			LaunchParameters result = new LaunchParameters();
			if (intent == null)
			{
				return result;
			}

			result.RouteFile = intent.GetStringExtra(RouteFileExtra);
			result.TrainFolder = intent.GetStringExtra(TrainFolderExtra);
			result.ImageFile = intent.GetStringExtra(ImageFileExtra);
			result.RouteEncoding = GetEncoding(intent.GetIntExtra(RouteEncodingExtra, 0));
			result.TrainEncoding = GetEncoding(intent.GetIntExtra(TrainEncodingExtra, 0));
			return result;
		}

		private static Encoding GetEncoding(int codePage)
		{
			if (codePage == 0)
			{
				return null;
			}

			try
			{
				return Encoding.GetEncoding(codePage);
			}
			catch
			{
				return null;
			}
		}
	}

	/// <summary>
	/// The plugins loading content right now, so the loading screen can show their progress and
	/// ask them to stop. Read from the UI thread while the render thread loads.
	/// </summary>
	public static class LoadProgress
	{
		/// <summary>The route plugin while a route loads.</summary>
		public static volatile RouteInterface Route;

		/// <summary>The train plugin while a train loads.</summary>
		public static volatile TrainInterface Train;

		/// <summary>
		/// Overall progress, 0 to 1. As upstream's loading screen, the route is the first part of
		/// the bar and the train the rest.
		/// </summary>
		public static double Value
		{
			get
			{
				TrainInterface train = Train;
				if (train != null)
				{
					return 0.8 + 0.2 * Clamp(train.CurrentProgress);
				}

				RouteInterface route = Route;
				return route != null ? 0.8 * Clamp(route.CurrentProgress) : 0.0;
			}
		}

		/// <summary>Asks whichever plugin is loading to stop.</summary>
		public static void Cancel()
		{
			RouteInterface route = Route;
			if (route != null)
			{
				route.Cancel = true;
			}

			TrainInterface train = Train;
			if (train != null)
			{
				train.Cancel = true;
			}
		}

		private static double Clamp(double value)
		{
			return value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;
		}
	}
}
