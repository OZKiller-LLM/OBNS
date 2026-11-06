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
using System.Text;
using System.Threading.Tasks;
using OpenBve.Android;

namespace OpenBve.Switch
{
	/// <summary>What the Start page hands the game: the route and train chosen, with their encodings.</summary>
	public sealed class SwitchLaunch
	{
		public string RouteFile;
		public Encoding RouteEncoding;
		public string TrainFolder;
		public Encoding TrainEncoding;
		public string ImageFile;
	}

	/// <summary>
	/// Start new game: Android's Start page (StartPage.cs) for the console's buttons - choose a route, then a train,
	/// then start, each browsed for on the SD card or picked from the recently used list; a route's preview
	/// (description, image, suggested train) and "Use the train suggested by the route" as on Windows and Android.
	/// </summary>
	/// <remarks>
	/// Three parts take the focus in turn: the steps along the top (① Route, ② Train, Start: left and right), the
	/// list on the left (the folder's entries: A opens, Y or B goes up a folder, X switches between browsing and the
	/// recent list) and the choices on the right (the game mode, the suggested train). + starts the game from anywhere
	/// once a route and a train are chosen.
	/// </remarks>
	public sealed class StartPage
	{
		private enum Step { Route, Train }

		private enum Part { Steps, List, Choices }

		private Step step = Step.Route;
		private Part part = Part.List;
		private int stepFocus;
		private bool showRecent;
		private readonly MenuList list = new MenuList();
		private readonly MenuList choices = new MenuList();
		private string routeFolder, trainFolder;
		private ChoiceItem defaultTrainChoice;

		private volatile RoutePreview route;
		private volatile TrainPreview train;
		private int previewToken;

		/// <summary>Set when Start is pressed with a route and a train chosen; the game takes it.</summary>
		public SwitchLaunch Launch { get; private set; }

		private static string T(string group, string key, string fallback) => Menu.T(group, key, fallback);

		public StartPage()
		{
			routeFolder = Existing(SwitchSettings.GetRouteFolder()) ?? Menu.FileSystem?.InitialRouteFolder;
			trainFolder = Existing(SwitchSettings.GetTrainFolder()) ?? Menu.FileSystem?.InitialTrainFolder;
			BuildChoices();
			Fill();
		}

		private static string Existing(string folder) => !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;

		/// <summary>What X and Y do here, for the hints along the bottom (empty when the list isn't in use).</summary>
		public string Hints
		{
			get
			{
				if (part != Part.List)
				{
					return string.Empty;
				}

				string x = "    Ⓧ " + (showRecent
					? T("start", step == Step.Route ? "route_browse" : "train_browse", "Browse manually")
					: T("start", step == Step.Route ? "route_recently" : "train_recently", "Recently used"));
				return showRecent ? x : x + "    Ⓨ " + T("switch", "hint_up", "Up a folder");
			}
		}

		/// <summary>Whether the page has a route and a train to start with.</summary>
		public bool Ready => route != null && route.Error == null && route.Description != Processing && train != null && train.Error == null;

		private static string Processing => T("start", "route_processing", "Processing route, please wait.....");

		/// <summary>Rebuilds the labels (after the language changed) and the lists (after content changed).</summary>
		public void Refresh()
		{
			BuildChoices();
			Fill();
		}

		private void BuildChoices()
		{
			choices.Clear();
			OpenBveApi.GameMode[] modes = { OpenBveApi.GameMode.Arcade, OpenBveApi.GameMode.Normal, OpenBveApi.GameMode.Expert };
			choices.Add(new ChoiceItem(T("start", "start_mode", "Mode:"),
				new[] { T("mode", "arcade", "Arcade"), T("mode", "normal", "Normal"), T("mode", "expert", "Expert") },
				Math.Max(0, Array.IndexOf(modes, SwitchSettings.GetGameMode())), i => SwitchSettings.SetGameMode(modes[i])));
			string[] onOff = { T("android", "on", "On"), T("android", "off", "Off") };
			defaultTrainChoice = new ChoiceItem(T("start", "train_usedefault", "Use the train suggested by the route"), onOff,
				SwitchSettings.GetUseDefaultTrain() ? 0 : 1, i =>
				{
					SwitchSettings.SetUseDefaultTrain(i == 0);
					if (i == 0)
					{
						ApplyDefaultTrain();
					}
				});
			choices.Add(defaultTrainChoice);
			choices.FocusFirst();
		}

		/// <summary>The list for the step and source shown: a folder's entries, or the recently used.</summary>
		private void Fill()
		{
			string focusedPath = (list.Focused as EntryItem)?.Path;
			list.Clear();
			if (showRecent)
			{
				List<string> items = step == Step.Route ? SwitchSettings.GetRecentRoutes() : SwitchSettings.GetRecentTrains();
				items.RemoveAll(p => !(File.Exists(p) || Directory.Exists(p)));
				foreach (string item in items)
				{
					string path = item;
					// The folder it's in as the second line: recent entries come from anywhere.
					list.Add(new EntryItem(step == Step.Route ? "◆" : "■", Path.GetFileName(path.TrimEnd('/')), Shown(Path.GetDirectoryName(path.TrimEnd('/'))), () => Choose(path), path));
				}

				if (items.Count == 0)
				{
					list.Add(new InfoItem(T("switch", "recent_empty", "Nothing has been used yet.")));
				}
			}
			else
			{
				string folder = step == Step.Route ? routeFolder : trainFolder;
				foreach (MenuItem item in Entries(folder))
				{
					list.Add(item);
				}
			}

			MarkChosen();
			list.FocusFirst();
			if (focusedPath != null)
			{
				int index = list.Items.FindIndex(i => i is EntryItem e && e.Path == focusedPath);
				if (index >= 0)
				{
					list.Focus = index;
				}
			}
		}

		/// <summary>A folder's entries as Android's file browser lists them: up, the sub-folders (trains among them), then
		/// the files a plugin loads.</summary>
		private List<MenuItem> Entries(string folder)
		{
			List<MenuItem> entries = new List<MenuItem>();
			if (string.IsNullOrEmpty(folder))
			{
				return entries;
			}

			string parent = Path.GetDirectoryName(folder.TrimEnd('/'));
			if (!string.IsNullOrEmpty(parent))
			{
				entries.Add(new EntryItem("↑", "..", null, () => Open(parent), parent));
			}

			string[] folders, files;
			try
			{
				folders = Directory.GetDirectories(folder);
				files = Directory.GetFiles(folder);
			}
			catch (Exception)
			{
				entries.Add(new InfoItem(T("errors", "security_checkaccess", "Please check that this path is accessible.")));
				return entries;
			}

			Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
			Array.Sort(files, StringComparer.OrdinalIgnoreCase);
			int count = entries.Count;
			foreach (string sub in folders)
			{
				string name = Path.GetFileName(sub), path = sub;
				if (string.IsNullOrEmpty(name) || name[0] == '.')
				{
					continue;
				}

				if (step == Step.Train && RouteInfo.IsTrainFolder(sub))
				{
					entries.Add(new EntryItem("■", name, null, () => Choose(path), path));
				}
				else
				{
					entries.Add(new EntryItem("▸", name, null, () => Open(path), path));
				}
			}

			foreach (string file in files)
			{
				string path = file;
				if (step == Step.Route ? RouteInfo.IsRouteFile(file) : RouteInfo.IsTrainFile(file))
				{
					entries.Add(new EntryItem(step == Step.Route ? "◆" : "■", Path.GetFileName(file), null, () => Choose(path), path));
				}
			}

			if (entries.Count == count)
			{
				entries.Add(new InfoItem(T("android", "folder_empty", "Nothing to show in this folder.")));
			}

			return entries;
		}

		private void Open(string folder)
		{
			if (step == Step.Route)
			{
				routeFolder = folder;
				SwitchSettings.SetRouteFolder(folder);
			}
			else
			{
				trainFolder = folder;
				SwitchSettings.SetTrainFolder(folder);
			}

			Fill();
		}

		private void GoUp()
		{
			string folder = step == Step.Route ? routeFolder : trainFolder;
			string parent = folder == null ? null : Path.GetDirectoryName(folder.TrimEnd('/'));
			if (!string.IsNullOrEmpty(parent))
			{
				Open(parent);
			}
		}

		private void Choose(string path)
		{
			if (step == Step.Route)
			{
				SelectRoute(path);
			}
			else
			{
				SelectTrain(path, manual: true);
			}
		}

		private void MarkChosen()
		{
			string chosen = step == Step.Route ? route?.File : train?.Folder;
			foreach (MenuItem item in list.Items)
			{
				if (item is EntryItem entry)
				{
					entry.Chosen = chosen != null && entry.Path == chosen;
				}
			}
		}

		private void SelectRoute(string file)
		{
			route = new RoutePreview { File = file, Description = Processing };
			MarkChosen();
			int token = ++previewToken;
			Task.Run(() =>
			{
				RoutePreview result;
				try
				{
					result = RouteInfo.PreviewRoute(file);
				}
				catch (Exception e)
				{
					result = new RoutePreview { File = file, Error = e.Message };
				}

				if (token == previewToken)
				{
					route = result;
					ApplyDefaultTrain();
				}
			});
		}

		private void SelectTrain(string folder, bool manual)
		{
			if (manual && defaultTrainChoice.Selected == 0)
			{
				// Choosing a train by hand, as on Windows, means not using the route's.
				SwitchSettings.SetUseDefaultTrain(false);
				BuildChoices();
			}

			train = new TrainPreview { Folder = folder };
			MarkChosen();
			Task.Run(() =>
			{
				TrainPreview result;
				try
				{
					result = RouteInfo.PreviewTrain(folder);
				}
				catch (Exception e)
				{
					result = new TrainPreview { Folder = folder, Error = e.Message };
				}

				if (train?.Folder == folder)
				{
					train = result;
				}
			});
		}

		/// <summary>Uses the route's suggested train if that option is on and the train is installed.</summary>
		private void ApplyDefaultTrain()
		{
			if (!SwitchSettings.GetUseDefaultTrain())
			{
				return;
			}

			RoutePreview preview = route;
			if (preview?.DefaultTrainFolder != null)
			{
				SelectTrain(preview.DefaultTrainFolder, manual: false);
			}
			else
			{
				train = null;
			}
		}

		private void ShowStep(Step newStep)
		{
			if (step != newStep)
			{
				step = newStep;
				Fill();
			}
		}

		private void Start()
		{
			if (!Ready)
			{
				return;
			}

			SwitchSettings.AddRecentRoute(route.File);
			SwitchSettings.AddRecentTrain(train.Folder);
			Launch = new SwitchLaunch
			{
				RouteFile = route.File,
				RouteEncoding = route.Encoding,
				TrainFolder = train.Folder,
				TrainEncoding = train.Encoding,
				ImageFile = route.ImageFile
			};
		}

		/// <summary>The frame's input, while the page is in use; false when B or left at the edge hands it back to the rail.</summary>
		public bool Update(MenuInput input, IMenuHost host)
		{
			if (input.Is(MenuButtons.Plus))
			{
				Start();
				return true;
			}

			if (input.Is(MenuButtons.X) && part == Part.List)
			{
				showRecent = !showRecent;
				Fill();
				return true;
			}

			if (TapSteps(input))
			{
				return true;
			}

			switch (part)
			{
				case Part.Steps:
					if (input.Is(MenuButtons.Left))
					{
						if (stepFocus == 0)
						{
							return false;
						}

						stepFocus--;
					}
					else if (input.Is(MenuButtons.Right))
					{
						stepFocus = Math.Min(2, stepFocus + 1);
					}
					else if (input.Is(MenuButtons.Down))
					{
						part = Part.List;
					}
					else if (input.Is(MenuButtons.A))
					{
						PressStep(stepFocus);
					}
					else if (input.Is(MenuButtons.B))
					{
						return false;
					}

					return true;
				case Part.List:
					if (input.Is(MenuButtons.Y) || (input.Is(MenuButtons.B) && !showRecent && CanGoUp()))
					{
						GoUp();
						return true;
					}

					if (input.Is(MenuButtons.Right))
					{
						part = Part.Choices;
						return true;
					}

					if (list.Update(input, host))
					{
						return true;
					}

					if (input.Is(MenuButtons.Up))
					{
						part = Part.Steps;
						stepFocus = step == Step.Route ? 0 : 1;
						return true;
					}

					// Left or B at the edge: back to the rail.
					return !(input.Is(MenuButtons.Left) || input.Is(MenuButtons.B));
				default:
					if (input.Is(MenuButtons.B))
					{
						part = Part.List;
						return true;
					}

					// On the route step only the mode is shown: the suggested train belongs to the train step, as Android's.
					if (step == Step.Route ? choices.Items[0].Handle(input, host) || input.Is(MenuButtons.Down) : choices.Update(input, host))
					{
						return true;
					}

					if (input.Is(MenuButtons.Up))
					{
						part = Part.Steps;
						stepFocus = 2;
						return true;
					}

					if (input.Is(MenuButtons.Left))
					{
						part = Part.List;
					}

					return true;
			}
		}

		private bool CanGoUp()
		{
			string folder = step == Step.Route ? routeFolder : trainFolder;
			return !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(Path.GetDirectoryName(folder.TrimEnd('/')));
		}

		private void PressStep(int index)
		{
			switch (index)
			{
				case 0:
					ShowStep(Step.Route);
					part = Part.List;
					break;
				case 1:
					ShowStep(Step.Train);
					part = Part.List;
					break;
				default:
					Start();
					break;
			}
		}

		// The layout, in the content area the menu gives the page.
		private float areaX, areaY, areaWidth;
		private const float StepsHeight = 64;

		private bool TapSteps(MenuInput input)
		{
			if (!input.Tapped || input.TouchY < areaY || input.TouchY > areaY + StepsHeight)
			{
				return false;
			}

			for (int i = 0; i < 3; i++)
			{
				StepRect(i, out float x, out float width);
				if (input.TouchX >= x && input.TouchX < x + width)
				{
					stepFocus = i;
					PressStep(i);
					return true;
				}
			}

			return false;
		}

		private void StepRect(int index, out float x, out float width)
		{
			float startWidth = 170, stepWidth = (areaWidth - startWidth - 2 * MenuStyle.Gap) / 2;
			x = areaX + index * (stepWidth + MenuStyle.Gap);
			width = index == 2 ? startWidth : stepWidth;
		}

		public void Draw(IMenuCanvas canvas, float x, float y, float width, float height, bool active, double seconds)
		{
			areaX = x;
			areaY = y;
			areaWidth = width;
			RoutePreview routeNow = route;
			TrainPreview trainNow = train;

			// The steps and Start.
			string[] labels =
			{
				"① " + T("start", "route", "Route") + ": " + (routeNow != null ? Path.GetFileNameWithoutExtension(routeNow.File) : "—"),
				"② " + T("start", "train", "Train") + ": " + (trainNow != null ? Path.GetFileName(trainNow.Folder.TrimEnd('/')) : "—"),
				T("start", "start_start", "Start")
			};
			for (int i = 0; i < 3; i++)
			{
				StepRect(i, out float stepX, out float stepWidth);
				bool current = i == 0 ? step == Step.Route : i == 1 && step == Step.Train;
				uint fill = i == 2 ? (Ready ? MenuStyle.Accent : MenuStyle.Surface) : current ? MenuStyle.Accent : MenuStyle.SurfaceRaised;
				canvas.Fill(stepX, y, stepWidth, StepsHeight - 8, fill, 8);
				if (active && part == Part.Steps && stepFocus == i)
				{
					canvas.Outline(stepX, y, stepWidth, StepsHeight - 8, MenuStyle.FocusRing, 3, 8);
				}

				string label = MenuStyle.Fit(canvas, labels[i], MenuStyle.BodySize, stepWidth - 24, i == 2);
				uint color = i == 2 && !Ready ? MenuStyle.Secondary : current || i == 2 ? 0xFFFFFFFF : MenuStyle.Primary;
				canvas.Text(label, stepX + (stepWidth - canvas.TextWidth(label, MenuStyle.BodySize, i == 2)) / 2, y + (StepsHeight - 8 - MenuStyle.BodySize) / 2,
					MenuStyle.BodySize, color, i == 2);
			}

			float top = y + StepsHeight + 4, bodyHeight = height - StepsHeight - 4;
			float leftWidth = (width - MenuStyle.Gap) * 0.53f, rightX = x + leftWidth + MenuStyle.Gap, rightWidth = width - leftWidth - MenuStyle.Gap;

			// Left: where to choose from, browsing or the recently used.
			canvas.Fill(x, top, leftWidth, bodyHeight, MenuStyle.Surface, MenuStyle.Radius);
			string source = showRecent
				? T("start", step == Step.Route ? "route_recently" : "train_recently", "Recently used")
				: FitTail(canvas, Shown(step == Step.Route ? routeFolder : trainFolder), MenuStyle.SmallSize, leftWidth - 190);
			canvas.Text(source, x + 14, top + 12, MenuStyle.SmallSize, MenuStyle.Secondary);
			string toggle = "X  " + (showRecent ? T("start", step == Step.Route ? "route_browse" : "train_browse", "Browse manually") : T("start", "route_recently", "Recently used"));
			toggle = MenuStyle.Fit(canvas, toggle, MenuStyle.SmallSize, 140);
			canvas.Text(toggle, x + leftWidth - 14 - canvas.TextWidth(toggle, MenuStyle.SmallSize), top + 12, MenuStyle.SmallSize, MenuStyle.Accent);
			list.Draw(canvas, x + 8, top + 44, leftWidth - 26, bodyHeight - 52, active && part == Part.List, seconds);

			// Right: the choices and the preview.
			canvas.Fill(rightX, top, rightWidth, bodyHeight, MenuStyle.Surface, MenuStyle.Radius);
			float choicesHeight = choices.Items[0].Measure(canvas, rightWidth - 16) + 4;
			if (step == Step.Train)
			{
				choicesHeight += choices.Items[1].Measure(canvas, rightWidth - 16) + 4;
			}
			DrawChoices(canvas, rightX + 8, top + 8, rightWidth - 16, choicesHeight, active && part == Part.Choices, seconds);
			float previewY = top + 8 + choicesHeight + 4;
			canvas.Text(T("start", step == Step.Route ? "route_details" : "train_details", "Details"), rightX + 16, previewY, MenuStyle.HeadingSize, MenuStyle.Primary, true);
			previewY += MenuStyle.HeadingSize + 12;
			string image = step == Step.Route ? routeNow?.ImageFile : trainNow?.ImageFile;
			float imageHeight = 0;
			if (!string.IsNullOrEmpty(image))
			{
				imageHeight = Math.Min(220, (top + bodyHeight - previewY) * 0.5f);
				if (!canvas.Image(image, rightX + 16, previewY, rightWidth - 32, imageHeight))
				{
					canvas.Fill(rightX + 16, previewY, rightWidth - 32, imageHeight, MenuStyle.SurfaceRaised, 6);
				}

				previewY += imageHeight + 10;
			}

			string text;
			bool error;
			if (step == Step.Route)
			{
				text = routeNow == null ? T("errors", "route_please_select", "Please first select a route file to begin.") : routeNow.Error ?? routeNow.Description ?? string.Empty;
				error = routeNow?.Error != null;
			}
			else if (trainNow != null)
			{
				text = trainNow.Error ?? trainNow.Description ?? string.Empty;
				error = trainNow.Error != null;
			}
			else if (SwitchSettings.GetUseDefaultTrain() && routeNow?.DefaultTrainName != null)
			{
				// The route names a train that is not installed.
				text = T("start", "train_notfound", "The default train could not be found:\n\n") + routeNow.DefaultTrainName;
				error = false;
			}
			else
			{
				text = T("start", "train_choose", "Choose Train...");
				error = false;
			}

			canvas.Clip(rightX + 16, previewY, rightWidth - 32, top + bodyHeight - previewY - 10);
			foreach (string line in MenuStyle.Wrap(canvas, text, MenuStyle.SmallSize, rightWidth - 32))
			{
				if (previewY > top + bodyHeight)
				{
					break;
				}

				canvas.Text(line, rightX + 16, previewY, MenuStyle.SmallSize, error ? MenuStyle.Danger : MenuStyle.Primary);
				previewY += MenuStyle.SmallSize + 6;
			}

			canvas.Unclip();
		}

		/// <summary>A folder as the list's heading shows it: from the content folder (LegacyContent) when it's inside it.</summary>
		private static string Shown(string folder)
		{
			if (string.IsNullOrEmpty(folder))
			{
				return string.Empty;
			}

			string railway = Menu.FileSystem?.RouteInstallationDirectory;
			string content = string.IsNullOrEmpty(railway) ? null : Path.GetDirectoryName(railway.TrimEnd('/', '\\'));
			if (!string.IsNullOrEmpty(content) && folder.StartsWith(content, StringComparison.OrdinalIgnoreCase) && folder.Length > content.Length)
			{
				folder = folder.Substring(content.Length).TrimStart('/', '\\');
			}

			return folder.Replace('\\', '/');
		}

		/// <summary>Text cut from the start to fit, so a path's last folders stay in view.</summary>
		private static string FitTail(IMenuCanvas canvas, string text, float size, float width)
		{
			if (canvas.TextWidth(text, size) <= width)
			{
				return text;
			}

			int start = 0;
			while (start < text.Length - 1 && canvas.TextWidth("…" + text.Substring(start), size) > width)
			{
				start++;
			}

			return "…" + text.Substring(start);
		}

		/// <summary>The right side's choices: the game mode, and on the train step the suggested train.</summary>
		private void DrawChoices(IMenuCanvas canvas, float x, float y, float width, float height, bool active, double seconds)
		{
			if (step == Step.Route)
			{
				choices.Items[0].Draw(canvas, x, y, width, active);
				return;
			}

			choices.Draw(canvas, x, y, width, height, active, seconds);
		}
	}
}
