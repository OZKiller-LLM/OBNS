//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2020, S520, The OpenBVE Project
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
using System.IO;
using System.Linq;
using System.Text;
using Bve5_Parsing;
using Bve5_Parsing.MapGrammar;
using Bve5_Parsing.MapGrammar.AstNodes;
using Bve5_Parsing.MapGrammar.EvaluateData;
using OpenBveApi.Colors;
using OpenBveApi.Interface;
using RouteManager2.Climate;
using static Bve5_Parsing.MapGrammar.MapGrammarParser;

namespace Route.Bve5
{
	internal static partial class Bve5ScenarioParser
	{
		private const int InterpolateInterval = 5;
		private const double StationNoticeDistance = -200.0;

		internal class MapParser
		{
			private readonly string FileName;
			private readonly bool IsDisplayErrors;
			private MapGrammarParser Parser;

			internal MapParser(string fileName, bool isDisplayErrors)
			{
				FileName = fileName;
				IsDisplayErrors = isDisplayErrors;
			}


			internal MapData Parse()
			{
				MapData Data = new MapData();
				Parser = new MapGrammarParser();

				if (Parser != null)
				{
					Data = ParseFromFile();

					if (IsDisplayErrors)
					{
						DisplayErrors();
					}
				}

				return Data;
			}

			/// <summary>OBNS: what the parser's ParseFromFile does, with OBNS's visitor (Bve5MapVisitor).</summary>
			private MapData ParseFromFile()
			{
				System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
				MapGrammarAstNodes root = Parser.ParseToAst(Bve5MapVisitor.Read(FileName), FileName, MapGrammarParserOption.ParseIncludeSyntaxRecursively);
				if (root == null)
				{
					return null;
				}

				MapData read = (MapData)new Bve5MapVisitor(Parser.Store, Path.GetDirectoryName(FileName), Parser.ErrorListener, 0).Visit(root);
				MapData sorted = new MapData(read.Version, read.Encoding);
				sorted.StructureListPaths.UnionWith(read.StructureListPaths);
				sorted.StationListPaths.UnionWith(read.StationListPaths);
				sorted.SignalListPaths.UnionWith(read.SignalListPaths);
				sorted.SoundListPaths.UnionWith(read.SoundListPaths);
				sorted.Sound3DListPaths.UnionWith(read.Sound3DListPaths);
				sorted.TrackKeys.UnionWith(read.TrackKeys);
				foreach (Statement statement in read.Statements.OrderBy(s => s.Distance))
				{
					sorted.AddStatement(statement);
				}

				return sorted;
			}

			private void DisplayErrors()
			{
				foreach (ParseError error in Parser.ParserErrors.OrderBy(e => e.Line).ThenBy(e => e.Column))
				{
					// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
					switch (error.ErrorLevel)
					{
						case ParseErrorLevel.Error:
							Plugin.CurrentHost.AddMessage(MessageType.Error, false, $"[{error.Line}:{error.Column}] {error.ErrorLevel}: {error.Message} in {FileName}");
							break;
						case ParseErrorLevel.Warning:
							Plugin.CurrentHost.AddMessage(MessageType.Warning, false, $"[{error.Line}:{error.Column}] {error.ErrorLevel}: {error.Message} in {FileName}");
							break;
					}
				}
			}
		}

		/// <summary>
		/// OBNS: the parser's evaluation, with BVE5's rand(x) for a fractional x. The parser rounds x to a whole number, so
		/// Ceil(Rand(1.05)), which picks a file named 1 or 2 in the Uchibo line's map, was always 0 and named no file. A whole x
		/// stays as the parser has it: that map's Ceil(Rand(2)) has to give 0 or 1. The parser's includes make their own
		/// visitors, so they are read here to go on using this one; anything wrong with one is left for the parser to report.
		/// </summary>
		private sealed class Bve5MapVisitor : EvaluateMapGrammarVisitorWithInclude
		{
			private readonly string directory;
			private readonly Random random = new Random();

			internal Bve5MapVisitor(VariableStore store, string directory, ParseErrorListener errorListener, double nowDistance) : base(store, directory, errorListener, nowDistance)
			{
				this.directory = directory;
			}

			public override object Visit(RandNode node)
			{
				if (node.Value != null && Visit(node.Value) is double limit && limit > 0 && limit != Math.Floor(limit))
				{
					return random.NextDouble() * limit;
				}

				return base.Visit(node);
			}

			public override object Visit(IncludeNode node)
			{
				string file = node.FilePath == null ? null : Visit(node.FilePath)?.ToString();
				if (string.IsNullOrEmpty(file) || file.IndexOf("atsex::", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return base.Visit(node);
				}

				string path = CombineFile(directory, file);
				if (!File.Exists(path))
				{
					return base.Visit(node);
				}

				string text = Read(path);
				MapGrammarAstNodes root = string.IsNullOrEmpty(text) ? null : new MapGrammarParser(ErrorListener).ParseToAst(text, path, MapGrammarParserOption.ParseIncludeSyntaxRecursively | MapGrammarParserOption.NoClearErrors);
				if (root == null)
				{
					return base.Visit(node);
				}

				evaluateData.AddIncludeData((MapData)new Bve5MapVisitor(Store, directory, ErrorListener, NowDistance).Visit(root));
				return null;
			}

			/// <summary>A map file's text, in the encoding of its byte order mark, or that its header names, or UTF-8.</summary>
			internal static string Read(string fileName)
			{
				byte[] bytes = File.ReadAllBytes(fileName);
				using (StreamReader bom = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), true))
				{
					bom.Peek();
					if (!(bom.CurrentEncoding is UTF8Encoding))
					{
						return bom.ReadToEnd();
					}
				}

				Encoding encoding = new UTF8Encoding(false);
				int end = Array.IndexOf(bytes, (byte)'\n');
				string header = Encoding.ASCII.GetString(bytes, 0, end < 0 ? Math.Min(bytes.Length, 64) : Math.Min(end, 64));
				int colon = header.IndexOf(':');
				if (colon >= 0)
				{
					try
					{
						encoding = Encoding.GetEncoding(header.Substring(colon + 1).Trim());
					}
					catch (ArgumentException)
					{
						// Not an encoding that can be named here: UTF-8.
					}
				}

				string text = encoding.GetString(bytes);
				return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
			}
		}

		private static void ParseMap(string FileName, bool PreviewOnly)
		{
			if (string.IsNullOrEmpty(FileName))
			{
				throw new Exception("The BVE5 scenario did not define a route map");
			}
			if (!File.Exists(FileName))
			{
				throw new Exception("The BVE5 route map file: " + FileName + " was not found");
			}

			MapParser Parser = new MapParser(FileName, true);
			MapData RootData = Parser.Parse();

			System.Threading.Thread.Sleep(1);
			if (plugin.Cancel) return;


			System.Threading.Thread.Sleep(1);
			if (plugin.Cancel) return;

			ConvertToBlock(FileName, PreviewOnly, RootData, out RouteData RouteData);

			System.Threading.Thread.Sleep(1);
			if (plugin.Cancel) return;

			ApplyRouteData(FileName, PreviewOnly, RouteData);
		}

		private static void ConvertToBlock(string FileName, bool PreviewOnly, MapData ParseData, out RouteData RouteData)
		{
			RouteData = new RouteData(ParseData.TrackKeys);
			RouteData.TryAddBlock(0);
			RouteData.Blocks[0].Fog = new Fog(0, 1, Color24.Grey, 0, false);
			
			if (ParseData.StationListPaths.Count == 0)
			{
				Plugin.CurrentHost.AddMessage(MessageType.Error, true, "BVE5: No Station List file was specified.");
				return;
			}

			foreach (string path in ParseData.StationListPaths)
			{
				LoadStationList(FileName, path, RouteData);
			}

			foreach (string path in ParseData.StructureListPaths)
			{
				LoadStructureList(FileName, PreviewOnly, path, RouteData);
			}
			
			foreach (string path in ParseData.SignalListPaths)
			{
				LoadSignalList(FileName, PreviewOnly, path, RouteData);
			}

			foreach (string path in ParseData.SoundListPaths)
			{
				LoadSoundList(FileName, PreviewOnly, path, RouteData);
			}

			foreach (string path in ParseData.Sound3DListPaths)
			{
				LoadSound3DList(FileName, PreviewOnly, path, RouteData);
			}
			
			if (Plugin.CurrentOptions.EnableBve5ScriptedTrain)
			{
				LoadScriptedTrain(FileName, PreviewOnly, ParseData, RouteData);
			}

			System.Threading.Thread.Sleep(1);
			if (plugin.Cancel) return;
			RouteData.Backgrounds = new ObjectDictionary();

			/*
			 * NOTE:
			 * Looping through the statement list multiple times is horrifically slow
			 * The slowness comes from somewhere within the BVE5 parsing library (to investigate)
			 *
			 * Track code currently requires a complete re-work to sort out properly so that only one loop
			 * is needed
			 *
			 */

			ConvertData(ParseData, RouteData, PreviewOnly);
			ConvertTrack(ParseData, RouteData);
			
			System.Threading.Thread.Sleep(1);
			if (plugin.Cancel) return;

			ConfirmCurve(RouteData.Blocks);
			ConfirmGradient(RouteData.Blocks);
			ConfirmTrack(RouteData);
			ConfirmStructure(PreviewOnly, ParseData, RouteData);
			ConfirmRepeater(PreviewOnly, ParseData, RouteData);
			ConfirmSection(PreviewOnly, ParseData, RouteData);
			ConfirmSignal(PreviewOnly, ParseData, RouteData);
			ConfirmBeacon(PreviewOnly, ParseData, RouteData);
			// these require looping through existing blocks, so need to be here at the minute
			ConfirmIrregularity(PreviewOnly, RouteData);
			ConfirmAdhesion(PreviewOnly, RouteData);
			ConfirmFlangeNoise(PreviewOnly, ParseData, RouteData);
		}

		private static void ConvertData(MapData parseData, RouteData routeData, bool previewOnly)
		{
			for (int i = 0; i < parseData.Statements.Count; i++)
			{
				switch(parseData.Statements[i].ElementName)
				{
					case MapElementName.Curve:
						ConvertCurve(parseData.Statements[i], routeData);
						break;
					case MapElementName.Gradient:
						ConvertGradient(parseData.Statements[i], routeData);
						break;
					case MapElementName.Legacy:
						switch (parseData.Statements[i].FunctionName)
						{
							case MapFunctionName.Curve:
							case MapFunctionName.Turn:
								ConvertCurve(parseData.Statements[i], routeData);
								break;
							case MapFunctionName.Pitch:
								ConvertGradient(parseData.Statements[i], routeData);
								break;
							case MapFunctionName.Fog:
								if (!previewOnly)
								{
									ConvertFog(parseData.Statements[i], routeData);
								}
								break;
						}
						break;
					case MapElementName.Station:
						ConvertStation(parseData.Statements[i], routeData);
						break;
					case MapElementName.Background:
						if (!previewOnly)
						{
							ConvertBackground(parseData.Statements[i], routeData);
						}
						break;
					case MapElementName.Fog:
						if (!previewOnly)
						{
							ConvertFog(parseData.Statements[i], routeData);
						}
						break;
					case MapElementName.Irregularity:
						if (!previewOnly)
						{
							ConvertIrregularity(parseData.Statements[i], routeData);
						}
						break;
					case MapElementName.Adhesion:
						if (!previewOnly)
						{
							ConvertAdhesion(parseData.Statements[i], routeData);
						}
						break;
					case MapElementName.JointNoise:
						if (!previewOnly)
						{
							ConvertJointNoise(parseData.Statements[i], routeData);
						}
						break;
					case MapElementName.Pretrain:
						if (!previewOnly)
						{
							ConfirmPreTrain(parseData.Statements[i]);
						}
						break;
					case MapElementName.Light:
						if (!previewOnly)
						{
							ConfirmLight(parseData.Statements[i]);
						}
						break;
					case MapElementName.Sound:
						if (!previewOnly)
						{
							ConfirmSound(parseData.Statements[i], routeData);
						}
						break;
					case MapElementName.Sound3d:
						if (!previewOnly)
						{
							ConfirmSound3D(parseData.Statements[i], routeData);
						}
						break;
					case MapElementName.SpeedLimit:
						if (!previewOnly)
						{
							ConfirmSpeedLimit(parseData.Statements[i], routeData);
						}
						break;
					case MapElementName.CabIlluminance:
						if (!previewOnly)
						{
							ConfirmCabIlluminance(parseData.Statements[i], routeData);
						}
						break;
					case MapElementName.RollingNoise:
						if (!previewOnly)
						{
							ConfirmRollingNoise(parseData.Statements[i], routeData);
						}
						break;
				}
			}
		}
	}
}
