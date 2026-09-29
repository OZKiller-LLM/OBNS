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
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace OpenBve.Android
{
	/// <summary>
	/// Start new game: choose a route, then a train, then start - the Windows main form's Start
	/// tab laid out for a phone held sideways.
	/// </summary>
	/// <remarks>
	/// As on Windows, each of route and train can be browsed for manually or picked from the
	/// recently used list; selecting a route previews it (description, image, suggested train),
	/// and "Use the train suggested by the route" picks that train when it is installed.
	/// The browser reaches any folder the app may read, so content need not be imported first.
	/// </remarks>
	public class StartPage : LinearLayout
	{
		private enum Step
		{
			Route,
			Train
		}

		private readonly Activity activity;
		private readonly Button routeStep;
		private readonly Button trainStep;
		private readonly Button start;
		private readonly FrameLayout sourceHost;
		private readonly Button browseTab;
		private readonly Button recentTab;
		private readonly FileBrowser routeBrowser;
		private readonly FileBrowser trainBrowser;
		private readonly ListView recentList;
		private readonly CheckBox useDefaultTrain;
		private readonly ImageView image;
		private readonly TextView description;
		private readonly TextView previewTitle;

		private Step step = Step.Route;
		private bool showRecent;
		private RoutePreview route;
		private TrainPreview train;
		private int previewToken;

		public StartPage(Activity activity) : base(activity)
		{
			this.activity = activity;
			Orientation = Orientation.Vertical;

			// Steps and the Start button.
			LinearLayout steps = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
			steps.SetGravity(GravityFlags.CenterVertical);
			routeStep = Ui.Button(activity, string.Empty);
			routeStep.Click += (s, e) => ShowStep(Step.Route);
			trainStep = Ui.Button(activity, string.Empty);
			trainStep.Click += (s, e) => ShowStep(Step.Train);
			start = Ui.Button(activity, Menu.T("start", "start_start", "Start"), prominent: true);
			start.Click += (s, e) => Launch();
			steps.AddView(routeStep, Ui.Weighted(1, activity, 8));
			steps.AddView(trainStep, Ui.Weighted(1, activity, 8));
			steps.AddView(start, new LayoutParams(Ui.Dp(activity, 140), ViewGroup.LayoutParams.MatchParent));
			AddView(steps, new LayoutParams(ViewGroup.LayoutParams.MatchParent, Ui.Dp(activity, 48)));

			LinearLayout content = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
			content.SetPadding(0, Ui.Dp(activity, 8), 0, 0);

			// Left: where to choose from.
			LinearLayout left = new LinearLayout(activity) { Orientation = Orientation.Vertical };
			left.Background = Ui.Panel(activity, Ui.Surface);
			int pad = Ui.Dp(activity, 8);
			left.SetPadding(pad, pad, pad, pad);
			LinearLayout sources = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
			browseTab = Ui.SmallButton(activity, string.Empty);
			browseTab.Click += (s, e) => ShowSource(false);
			recentTab = Ui.SmallButton(activity, string.Empty);
			recentTab.Click += (s, e) => ShowSource(true);
			sources.AddView(browseTab);
			sources.AddView(recentTab);
			left.AddView(sources);

			sourceHost = new FrameLayout(activity);
			routeBrowser = new FileBrowser(activity, FileBrowser.Mode.Route);
			routeBrowser.Selected += SelectRoute;
			routeBrowser.FolderChanged += folder => AndroidSettings.SetRouteFolder(activity, folder);
			trainBrowser = new FileBrowser(activity, FileBrowser.Mode.Train);
			trainBrowser.Selected += folder => SelectTrain(folder, manual: true);
			trainBrowser.FolderChanged += folder => AndroidSettings.SetTrainFolder(activity, folder);
			recentList = new ListView(activity);
			recentList.ItemClick += (s, e) => OnRecentClick(e.Position);
			sourceHost.AddView(routeBrowser);
			sourceHost.AddView(trainBrowser);
			sourceHost.AddView(recentList);
			left.AddView(sourceHost, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
			content.AddView(left, Ui.Weighted(1.15f, activity, 8));

			// Right: the preview.
			LinearLayout right = new LinearLayout(activity) { Orientation = Orientation.Vertical };
			right.Background = Ui.Panel(activity, Ui.Surface);
			right.SetPadding(pad, pad, pad, pad);
			useDefaultTrain = new CheckBox(activity)
			{
				Checked = AndroidSettings.GetUseDefaultTrain(activity),
				Text = Menu.T("start", "train_usedefault", "Use the train suggested by the route")
			};
			useDefaultTrain.SetTextColor(Ui.Primary);
			useDefaultTrain.CheckedChange += (s, e) => OnUseDefaultTrainChanged(e.IsChecked);
			right.AddView(useDefaultTrain);
			previewTitle = Ui.Heading(activity, string.Empty);
			right.AddView(previewTitle);
			image = new ImageView(activity);
			image.SetScaleType(ImageView.ScaleType.FitCenter);
			image.SetAdjustViewBounds(true);
			right.AddView(image, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
			ScrollView scroll = new ScrollView(activity);
			description = Ui.Body(activity, string.Empty);
			scroll.AddView(description);
			right.AddView(scroll, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));
			content.AddView(right, Ui.Weighted(1.0f, activity));

			AddView(content, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f));

			routeBrowser.Open(AndroidSettings.GetRouteFolder(activity));
			trainBrowser.Open(AndroidSettings.GetTrainFolder(activity));
			ShowStep(Step.Route);
		}

		/// <summary>Refreshes the lists, e.g. after a package was installed or storage access granted.</summary>
		public void Refresh()
		{
			routeBrowser.Open(routeBrowser.CurrentFolder);
			trainBrowser.Open(trainBrowser.CurrentFolder);
		}

		private void ShowStep(Step newStep)
		{
			step = newStep;
			routeStep.Background = Ui.Panel(activity, step == Step.Route ? Ui.Accent : Ui.SurfaceRaised, 8);
			trainStep.Background = Ui.Panel(activity, step == Step.Train ? Ui.Accent : Ui.SurfaceRaised, 8);
			browseTab.Text = Menu.T("start", step == Step.Route ? "route_browse" : "train_browse", "Browse manually");
			recentTab.Text = Menu.T("start", step == Step.Route ? "route_recently" : "train_recently", "Recently used");
			useDefaultTrain.Visibility = step == Step.Train ? ViewStates.Visible : ViewStates.Gone;
			ShowSource(showRecent);
			UpdateSummary();
			ShowPreview();
		}

		private void ShowSource(bool recent)
		{
			showRecent = recent;
			browseTab.Background = Ui.Panel(activity, recent ? Ui.SurfaceRaised : Ui.Selection, 8);
			recentTab.Background = Ui.Panel(activity, recent ? Ui.Selection : Ui.SurfaceRaised, 8);
			routeBrowser.Visibility = !recent && step == Step.Route ? ViewStates.Visible : ViewStates.Gone;
			trainBrowser.Visibility = !recent && step == Step.Train ? ViewStates.Visible : ViewStates.Gone;
			recentList.Visibility = recent ? ViewStates.Visible : ViewStates.Gone;
			if (recent)
			{
				List<string> items = step == Step.Route ? AndroidSettings.GetRecentRoutes(activity) : AndroidSettings.GetRecentTrains(activity);
				items.RemoveAll(p => !(File.Exists(p) || Directory.Exists(p)));
				recentList.Tag = new Java.Lang.String(string.Join("\n", items));
				List<string> names = items.ConvertAll(p => System.IO.Path.GetFileName(p.TrimEnd('/')) + "\n" + p);
				recentList.Adapter = new ArrayAdapter<string>(activity, global::Android.Resource.Layout.SimpleListItem1, names);
			}
		}

		private void OnRecentClick(int position)
		{
			string[] items = recentList.Tag?.ToString().Split('\n');
			if (items == null || position >= items.Length)
			{
				return;
			}

			if (step == Step.Route)
			{
				routeBrowser.Reveal(items[position]);
				SelectRoute(items[position]);
			}
			else
			{
				trainBrowser.Reveal(items[position]);
				SelectTrain(items[position], manual: true);
			}
		}

		private void SelectRoute(string file)
		{
			route = new RoutePreview { File = file, Description = Menu.T("start", "route_processing", "Processing route, please wait.....") };
			ShowPreview();
			UpdateSummary();
			int token = ++previewToken;
			Task.Run(() => RouteInfo.PreviewRoute(file)).ContinueWith(t => activity.RunOnUiThread(() =>
			{
				if (token != previewToken || t.IsFaulted)
				{
					return;
				}

				route = t.Result;
				ApplyDefaultTrain();
				ShowPreview();
				UpdateSummary();
			}));
		}

		private void SelectTrain(string folder, bool manual)
		{
			if (manual && useDefaultTrain.Checked)
			{
				// Choosing a train by hand, as on Windows, means not using the route's.
				useDefaultTrain.Checked = false;
			}

			train = new TrainPreview { Folder = folder };
			ShowPreview();
			UpdateSummary();
			Task.Run(() => RouteInfo.PreviewTrain(folder)).ContinueWith(t => activity.RunOnUiThread(() =>
			{
				if (train?.Folder != folder || t.IsFaulted)
				{
					return;
				}

				train = t.Result;
				ShowPreview();
			}));
		}

		private void OnUseDefaultTrainChanged(bool isChecked)
		{
			AndroidSettings.SetUseDefaultTrain(activity, isChecked);
			if (isChecked)
			{
				trainBrowser.ClearSelection();
				ApplyDefaultTrain();
			}
		}

		/// <summary>Uses the route's suggested train if that option is on and the train is installed.</summary>
		private void ApplyDefaultTrain()
		{
			string name = route?.DefaultTrainName;
			useDefaultTrain.Text = Menu.T("start", "train_usedefault", "Use the train suggested by the route") + (name != null ? " (" + name + ")" : string.Empty);
			if (!useDefaultTrain.Checked)
			{
				return;
			}

			if (route?.DefaultTrainFolder != null)
			{
				SelectTrain(route.DefaultTrainFolder, manual: false);
			}
			else
			{
				train = null;
				UpdateSummary();
			}
		}

		private void ShowPreview()
		{
			if (step == Step.Route)
			{
				previewTitle.Text = Menu.T("start", "route_details", "Details");
				ShowImage(route?.ImageFile);
				description.Text = route == null
					? Menu.T("errors", "route_please_select", "Please first select a route file to begin.")
					: route.Error ?? route.Description ?? string.Empty;
				description.SetTextColor(route?.Error != null ? Ui.Danger : Ui.Primary);
			}
			else
			{
				previewTitle.Text = Menu.T("start", "train_details", "Details");
				ShowImage(train?.ImageFile);
				string text;
				if (train != null)
				{
					text = train.Error ?? train.Description ?? string.Empty;
				}
				else if (useDefaultTrain.Checked && route?.DefaultTrainName != null)
				{
					// The route names a train that is not installed.
					text = Menu.T("start", "train_notfound", "The default train could not be found:\n\n") + route.DefaultTrainName;
				}
				else
				{
					text = Menu.T("start", "train_choose", "Choose Train...");
				}

				description.Text = text;
				description.SetTextColor(train?.Error != null ? Ui.Danger : Ui.Primary);
			}
		}

		private void ShowImage(string file)
		{
			Bitmap bitmap = null;
			if (!string.IsNullOrEmpty(file))
			{
				try
				{
					// Route and train pictures are small, but decode at a bounded size regardless.
					BitmapFactory.Options bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
					BitmapFactory.DecodeFile(file, bounds);
					int sample = 1;
					while (bounds.OutWidth / (sample * 2) >= 800)
					{
						sample *= 2;
					}

					bitmap = BitmapFactory.DecodeFile(file, new BitmapFactory.Options { InSampleSize = sample });
				}
				catch (Exception)
				{
					bitmap = null;
				}
			}

			image.SetImageBitmap(bitmap);
			image.Visibility = bitmap != null ? ViewStates.Visible : ViewStates.Gone;
		}

		private void UpdateSummary()
		{
			routeStep.Text = "① " + Menu.T("start", "route", "Route") + ": " + (route != null ? System.IO.Path.GetFileNameWithoutExtension(route.File) : "—");
			trainStep.Text = "② " + Menu.T("start", "train", "Train") + ": " + (train != null ? System.IO.Path.GetFileName(train.Folder.TrimEnd('/')) : "—");
			bool ready = route != null && route.Error == null && train != null;
			start.Enabled = ready;
			start.Alpha = ready ? 1.0f : 0.45f;
		}

		/// <summary>Starts the game with the chosen route and train, in the game process.</summary>
		private void Launch()
		{
			if (route == null || train == null)
			{
				return;
			}

			AndroidSettings.AddRecentRoute(activity, route.File);
			AndroidSettings.AddRecentTrain(activity, train.Folder);

			LaunchParameters launch = new LaunchParameters
			{
				RouteFile = route.File,
				RouteEncoding = route.Encoding,
				TrainFolder = train.Folder,
				TrainEncoding = train.Encoding,
				ImageFile = route.ImageFile
			};
			Intent intent = new Intent(activity, typeof(GameActivity));
			launch.WriteTo(intent);
			activity.StartActivity(intent);
		}
	}
}
