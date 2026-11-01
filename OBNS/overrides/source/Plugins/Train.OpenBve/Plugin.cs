// OBNS: a copy of upstream's source/Plugins/Train.OpenBve/Plugin.cs (OpenBVE 1.14.1.2), laid over the desktop
// build's mirror (OBNS/build-desktop.ps1) and compiled by Android (Train.OpenBve.Android.csproj):
// - BVE Trainsim 5 trains (OBNS/BVE5.md): CanLoadTrain claims them, as upstream's comments say it should once
//   they are supported, and LoadTrain builds them (Train/BVE5);
// - the static fields a train load fills in are [ThreadStatic], so that trains can load on several threads at
//   once, each with its own plugin instance (Android's scripted trains); each is set and read within one load.
// On an upstream update: copy the file again and reapply the parts marked OBNS.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using LibRender2;
using LibRender2.Trains;
using OpenBveApi;
using OpenBveApi.FileSystem;
using OpenBveApi.Graphics;
using OpenBveApi.Hosts;
using OpenBveApi.Interface;
using OpenBveApi.Objects;
using OpenBveApi.Trains;
using TrainManager.Motor;
using TrainManager.Power;
using TrainManager.Trains;
using Path = OpenBveApi.Path;

namespace Train.OpenBve
{
    public class Plugin : TrainInterface
    {
	    internal static HostInterface CurrentHost;

	    internal static FileSystem FileSystem;

	    internal static BaseOptions CurrentOptions;

	    internal static BaseRenderer Renderer;

	    internal TrainDatParser TrainDatParser;

	    internal ExtensionsCfgParser ExtensionsCfgParser;

	    internal SoundCfgParser SoundCfgParser;

		// ReSharper disable InconsistentNaming
		internal BVE2SoundParser BVE2SoundParser;
		
	    internal BVE4SoundParser BVE4SoundParser;
	    // ReSharper restore InconsistentNaming

	    internal SoundXmlParser SoundXmlParser;

	    internal PanelCfgParser PanelCfgParser;

	    internal Panel2CfgParser Panel2CfgParser;

	    internal PanelXmlParser PanelXmlParser;

	    internal PanelAnimatedXmlParser PanelAnimatedXmlParser;

	    internal TrainXmlParser TrainXmlParser;

	    internal Control[] CurrentControls;

	    internal double LastProgress;

	    // OBNS: [ThreadStatic] (see the header).
	    [ThreadStatic] internal static BVEMotorSoundTable[] MotorSoundTables;
	    [ThreadStatic] internal static BveAccelerationCurve[] AccelerationCurves;
	    [ThreadStatic] internal static double MaximumAcceleration;

		public Plugin()
	    {
		    TrainDatParser = new TrainDatParser(this);

		    if (ExtensionsCfgParser == null)
		    {
			    ExtensionsCfgParser = new ExtensionsCfgParser(this);
		    }

		    if (SoundCfgParser == null)
		    {
			    SoundCfgParser = new SoundCfgParser(this);
			    BVE2SoundParser = new BVE2SoundParser(this);
			    BVE4SoundParser = new BVE4SoundParser(this);
			    SoundXmlParser = new SoundXmlParser(this);
		    }

		    if (PanelCfgParser == null)
		    {
			    PanelCfgParser = new PanelCfgParser(this);
			    Panel2CfgParser = new Panel2CfgParser(this);
			    PanelXmlParser = new PanelXmlParser(this);
			    PanelAnimatedXmlParser = new PanelAnimatedXmlParser(this);
		    }

		    if (TrainXmlParser == null)
		    {
			    TrainXmlParser = new TrainXmlParser(this);
		    }
	    }

		public override void Load(HostInterface host, FileSystem fileSystem, BaseOptions options, object rendererReference)
		{
			CurrentHost = host;
			FileSystem = fileSystem;
			CurrentOptions = options;
			// ReSharper disable once MergeCastWithTypeCheck
			if (rendererReference is BaseRenderer)
			{
				Renderer = (BaseRenderer)rendererReference;
			}
		}

		private static readonly string[] BlockedDirectories = new[]
		{
			"Application Data",
			"Documents and Settings",
			"Default User",
			"Cookies",
			"Local Settings",
			"My Documents",
			"MSOCache",
			"NetHood",
			"PrintHood",
			"Recent",
			"Recovery",
			"SendTo",
			"Start Menu",
			"System Volume Information",
			"Templates"
		};

		/// <summary>OBNS: whether a train's train.xml gave any motor car its motor sound (none has one before the sound files are read).</summary>
		private static bool HasXmlMotorSound(TrainBase train)
		{
			for (int i = 0; i < train.Cars.Length; i++)
			{
				if (train.Cars[i].TractionModel.ProvidesPower && train.Cars[i].TractionModel.MotorSounds != null)
				{
					return true;
				}
			}

			return false;
		}

		public override bool CanLoadTrain(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}
			string directoryName = new DirectoryInfo(path).Name;
			if (!Directory.Exists(Path.GetDirectoryName(path)) || BlockedDirectories.Contains(directoryName))
			{
				return false;
			}

			// OBNS: a BVE5 vehicle (a folder with one, or the vehicle file itself), before any train.dat beside it.
			if (Bve5Vehicle.Find(path) != null)
			{
				return true;
			}

			

			if (Directory.Exists(path))
			{
				string vehicleTxt;
				try {
					vehicleTxt = Path.CombineFile(path, "vehicle.txt");
				} catch {
					return false;
				}
				if (File.Exists(vehicleTxt))
				{
					string[] lines = File.ReadAllLines(vehicleTxt);
					for (int i = 10; i < lines.Length; i++)
					{
						if (lines[i].StartsWith(@"bvets vehicle ", StringComparison.InvariantCultureIgnoreCase))
						{
							/*
							 * BVE5 format train
							 * When the BVE5 plugin is implemented, this should return false, as BVE5 trains
							 * often seem to keep the train.dat lying around and we need to use the right plugin
							 *
							 * For the moment however, this is ignored....
							 */
						}
					}
				}

				string trainDat = Path.CombineFile(path, "train.dat");
				if (File.Exists(trainDat))
				{
					if (TrainDatParser.CanLoad(trainDat))
					{
						return true;
					}
				}

				string trainXML = Path.CombineFile(path, "train.xml");
				if (File.Exists(trainXML))
				{
					/*
					 * XML format train
					 * At present, XML is used only as an extension, but acceleration etc. will be implemented
					 * When this is done, return true here
					 */
				}

				return false;
			}

			if (File.Exists(path))
			{
				if (path.EndsWith("vehicle.txt", StringComparison.InvariantCultureIgnoreCase))
				{
					/*
					 * BVE5 format train
					 * When the BVE5 plugin is implemented, this should return false, as BVE5 trains
					 * often seem to keep the train.dat lying around and we need to use the right plugin
					 *
					 * For the moment however, this is ignored....
					 */
				}

				if (path.EndsWith("train.dat", StringComparison.InvariantCultureIgnoreCase) || path.EndsWith("train.ai", StringComparison.InvariantCultureIgnoreCase))
				{
					if (TrainDatParser.CanLoad(path))
					{
						return true;
					}
				}

				if (path.EndsWith("train.xml", StringComparison.InvariantCultureIgnoreCase))
				{
					/*
					 * XML format train
					 * At present, XML is used only as an extension, but acceleration etc. will be implemented
					 * When this is done, return true here
					 */
				}
			}
			return false;
		}

	    public override bool LoadTrain(Encoding encoding, string trainPath, ref AbstractTrain train, ref Control[] currentControls)
	    {
		    // OBNS: the BVE5 vehicle being loaded, if it is one.
		    Bve5Vehicle bve5Vehicle = null;
		    CurrentProgress = 0.0;
		    LastProgress = 0.0;
		    IsLoading = true;
		    CurrentControls = currentControls;
		    TrainBase currentTrain = train as TrainBase;
		    if (currentTrain == null)
		    {
				CurrentHost.ReportProblem(ProblemType.InvalidData, "Train was not valid");
				IsLoading = false;
				return false;
		    }

		    if (currentTrain.State == TrainState.Bogus)
		    {
			    // bogus train
			    string trainData = Path.CombineFile(FileSystem.GetDataFolder("Compatibility", "PreTrain"), "train.dat");
			    TrainDatParser.Parse(trainData, Encoding.UTF8, currentTrain);
			    Thread.Sleep(1);

			    if (Cancel)
			    {
				    IsLoading = false;
				    return false;
			    }
		    }
		    else if (Bve5Vehicle.Find(trainPath) is string bve5File)
		    {
			    // OBNS: a BVE5 train, from its vehicle file.
			    bve5Vehicle = Bve5Vehicle.Load(bve5File);
			    currentTrain.TrainFolder = bve5Vehicle.Folder;
			    FileSystem.AppendToLogFile("Loading " + (currentTrain.IsPlayerTrain ? "player" : "AI") + " BVE5 train: " + bve5File);
			    Bve5Parameters parameters = Bve5Parameters.Load(bve5Vehicle.Parameters);
			    Bve5PerformanceSet.Load(bve5Vehicle.PerformanceCurve, out Bve5PerformanceSet power, out Bve5PerformanceSet brake);
			    Bve5TrainBuilder.Build(currentTrain, bve5Vehicle, parameters, power, brake);
			    LastProgress = 0.1;
		    }
		    else
		    {
				currentTrain.TrainFolder = trainPath;
			    // real train
			    if (currentTrain.IsPlayerTrain)
			    {
				    FileSystem.AppendToLogFile("Loading player train: " + currentTrain.TrainFolder);
			    }
			    else
			    {
				    FileSystem.AppendToLogFile("Loading AI train: " + currentTrain.TrainFolder);
			    }

				string trainData = null;
				if (!currentTrain.IsPlayerTrain)
				{
					trainData = Path.CombineFile(currentTrain.TrainFolder, "train.ai");
				}
				if (currentTrain.IsPlayerTrain || !File.Exists(trainData))
				{
					trainData = Path.CombineFile(currentTrain.TrainFolder, "train.dat");
				}
				TrainDatParser.Parse(trainData, encoding, currentTrain);
			    LastProgress = 0.1;
			    Thread.Sleep(1);
			    if (Cancel)
			    {
				    IsLoading = false;
				    return false;
			    }
		    }
		    // add panel section
		    if (currentTrain.IsPlayerTrain && bve5Vehicle != null && bve5Vehicle.Panel != null && File.Exists(bve5Vehicle.Panel))
		    {
			    // OBNS: the BVE5 vehicle's panel - BVE5's instrument panel keeps panel2.cfg's sections and subjects.
			    currentTrain.Cars[currentTrain.DriverCar].CarSections = new Dictionary<CarSectionType, CarSection>();
			    currentTrain.Cars[currentTrain.DriverCar].CarSections.Add(CarSectionType.Interior, new CarSection(CurrentHost, ObjectType.Overlay, true));
			    FileSystem.AppendToLogFile("Loading train panel: " + bve5Vehicle.Panel);
			    // Read as BVE5 reads it, whichever format the panel is in.
			    Panel2CfgParser.Bve5Train = true;
			    Panel2CfgParser.ParsePanel2Config(Path.GetFileName(bve5Vehicle.Panel), Path.GetDirectoryName(bve5Vehicle.Panel), currentTrain.Cars[currentTrain.DriverCar]);
			    Panel2CfgParser.Bve5Train = false;
			    currentTrain.Cars[currentTrain.DriverCar].CameraRestrictionMode = CameraRestrictionMode.On;
			    Renderer.Camera.CurrentRestriction = CameraRestrictionMode.On;
			    FileSystem.AppendToLogFile("Train panel loaded successfully.");
		    }
		    else if (currentTrain.IsPlayerTrain) {	
			    ParsePanelConfig(currentTrain, encoding);
			    Thread.Sleep(1);
			    if (Cancel)
			    {
				    IsLoading = false;
				    return false;
			    }
			    FileSystem.AppendToLogFile("Train panel loaded successfully.");
		    }

		    CurrentProgress = 0.5;
		    LastProgress = 0.5;
		    
			// add exterior section
			if (currentTrain.State != TrainState.Bogus)
			{
				bool[] visibleFromInterior = null;
				UnifiedObject[] carObjects = new UnifiedObject[currentTrain.Cars.Length];
				UnifiedObject[] bogieObjects = new UnifiedObject[currentTrain.Cars.Length * 2];
				UnifiedObject[] couplerObjects = new UnifiedObject[currentTrain.Cars.Length];

				string tXml = Path.CombineFile(currentTrain.TrainFolder, "train.xml");

				bool parseExtensionsCfg = true;
				if (File.Exists(tXml))
				{
					try
					{
						TrainXmlParser.Parse(tXml, currentTrain, ref carObjects, ref bogieObjects, ref couplerObjects, out visibleFromInterior);
						parseExtensionsCfg = false;
					}
					catch(Exception e)
					{
						CurrentHost.ReportProblem(ProblemType.UnexpectedException, "Whilst processing XML file " + tXml + " encountered the following exeception:" + Environment.NewLine + e);
					}
					
				}

				if(parseExtensionsCfg)
				{
					// train.xml is either missing or broken
					ExtensionsCfgParser.ParseExtensionsConfig(currentTrain.TrainFolder, encoding, ref carObjects, ref bogieObjects, ref couplerObjects, out visibleFromInterior, currentTrain);
				}

				// Ensure visibleFromInterior is valid even if both parsers fail
				if (visibleFromInterior == null || visibleFromInterior.Length != currentTrain.Cars.Length)
				{
					visibleFromInterior = new bool[currentTrain.Cars.Length];
				}
				currentTrain.CameraCar = currentTrain.DriverCar;
				Thread.Sleep(1);
				if (Cancel)
				{
					IsLoading = false;
					return false;
				}
				
				CurrentProgress = 0.75;
				LastProgress = 0.75;
				
				//Stores the current array index of the bogie object to add
				//Required as there are two bogies per car, and we're using a simple linear array....
				int currentBogieObject = 0;
				for (int i = 0; i < currentTrain.Cars.Length; i++)
				{
					if (carObjects[i] == null)
					{
						// load default exterior object
						string file = Path.CombineFile(FileSystem.GetDataFolder("Compatibility"), "exterior.csv");
						CurrentHost.LoadStaticObject(file, Encoding.UTF8, false, out var so);
						if (so == null)
						{
							carObjects[i] = null;
						}
						else
						{
							StaticObject c = (StaticObject) so.Clone(); //Clone as otherwise the cached object doesn't scale right
							c.ApplyScale(currentTrain.Cars[i].Width, currentTrain.Cars[i].Height, currentTrain.Cars[i].Length);
							carObjects[i] = c;
						}
					}

					if (carObjects[i] != null)
					{
						// add object
						currentTrain.Cars[i].CarSections.Add(CarSectionType.Exterior, new CarSection(CurrentHost, ObjectType.Dynamic, visibleFromInterior[i], currentTrain.Cars[i], carObjects[i]));
					}

					if (couplerObjects[i] != null)
					{
						currentTrain.Cars[i].Coupler.LoadCarSections(couplerObjects[i], visibleFromInterior[i]);
					}

					//Load bogie objects
					if (bogieObjects[currentBogieObject] != null)
					{
						currentTrain.Cars[i].FrontBogie.LoadCarSections(bogieObjects[currentBogieObject], visibleFromInterior[i]);
					}

					currentBogieObject++;
					if (bogieObjects[currentBogieObject] != null)
					{
						currentTrain.Cars[i].RearBogie.LoadCarSections(bogieObjects[currentBogieObject], visibleFromInterior[i]);
					}

					currentBogieObject++;
				}
			}

			if (currentTrain.State != TrainState.Bogus)
			{
				if (Cancel) return false;
				if (bve5Vehicle != null)
				{
					// OBNS: the BVE5 vehicle's sound file (BVE4's sound.cfg sections), motor noise and safety plugin.
					if (bve5Vehicle.Sound != null && File.Exists(bve5Vehicle.Sound))
					{
						BVE4SoundParser.Parse(bve5Vehicle.Sound, Path.GetDirectoryName(bve5Vehicle.Sound), currentTrain);
					}

					Bve5MotorNoise.Apply(currentTrain, bve5Vehicle);
					// OBNS_ATS64=1: the 64-bit plugin where a train has both, for testing it.
					string atsPlugin = IntPtr.Size == 8 && (bve5Vehicle.Ats32 == null || bve5Vehicle.Ats64 != null && Environment.GetEnvironmentVariable("OBNS_ATS64") == "1") ? bve5Vehicle.Ats64 : bve5Vehicle.Ats32;
					if (currentTrain.IsPlayerTrain && atsPlugin != null)
					{
						FileSystem.AppendToLogFile("Loading train plugin: " + atsPlugin);
						FileSystem.AppendToLogFile(currentTrain.LoadPlugin(atsPlugin, currentTrain.TrainFolder) ? "Train plugin loaded successfully." : "The train plugin " + atsPlugin + " failed to load.");
					}
				}
				else if (!HasXmlMotorSound(currentTrain) && Bve5SoundAddOn.Find(currentTrain.TrainFolder, out string addOnNoise, out string addOnSound))
				{
					// OBNS: a train.dat train with BVE5 motor noise (Bve5SoundAddOn): that noise, and the BVE5 sound file in
					// place of sound.cfg if there is one; its motor sounds are the [Motor] of whichever sound file it uses.
					// A train.xml that gives its cars their motor sound is the author's choice, and is kept.
					string soundFile = addOnSound;
					if (soundFile != null)
					{
						FileSystem.AppendToLogFile("Loading BVE5 sound for this train: " + soundFile);
						BVE4SoundParser.Parse(soundFile, Path.GetDirectoryName(soundFile), currentTrain);
					}
					else
					{
						SoundCfgParser.ParseSoundConfig(currentTrain);
						soundFile = Path.CombineFile(currentTrain.TrainFolder, "sound.cfg");
					}

					FileSystem.AppendToLogFile("Loading BVE5 motor noise for this train: " + addOnNoise);
					Bve5MotorNoise.Apply(currentTrain, addOnNoise, soundFile, addOnNoise, currentTrain.TrainFolder);
				}
				else
				{
					SoundCfgParser.ParseSoundConfig(currentTrain);
				}
				/*
				 * Determine door opening / closing speed & copy in any missing bits (e.g. motor sound tables)
				 * This *must* be done after the sound configuration has loaded
				 * (As the original BVE / OpenBVE implementation calculates this from
				 * the length of the sound buffer)
				 *
				 * However, as motor performance / sound tables may also be loaded via car XML files,
				 * this also needs to be done dead last
				 */

				int numMotorCars = 0;
				for (int i = 0; i < currentTrain.Cars.Length; i++)
				{
					currentTrain.Cars[i].DetermineDoorClosingSpeed();
					if (currentTrain.Cars[i].TractionModel.ProvidesPower)
					{
						numMotorCars++;
						if (currentTrain.Cars[i].TractionModel.MotorSounds == null && TrainXmlParser.MotorSoundXMLParsed != null)
						{
							if(i < TrainXmlParser.MotorSoundXMLParsed.Length && !TrainXmlParser.MotorSoundXMLParsed[i])
							{
								currentTrain.Cars[i].TractionModel.MotorSounds = new BVEMotorSound(currentTrain.Cars[i], 18.0, MotorSoundTables);
							}
						}
						
					}
				}

				if (currentTrain.IsPlayerTrain && numMotorCars == 0)
				{
					/*
					 * https://bveworldwide.forumotion.com/t2389-engine-sound-in-correct-car-wagon#21789
					 *
					 * Workaround for the suspected cause of this one- Malformed XML where all cars are set as trailer
					 * Speed / physics are likely to be off, but let's at least do something
					 */
					CurrentHost.AddMessage(MessageType.Error, false, "Player train appears to have no motor cars, assigning Car 0 as a motor car.");
					currentTrain.Cars[0].TractionModel = new BVEMotorCar(currentTrain.Cars[0], null);
					currentTrain.Cars[0].TractionModel.MotorSounds = new BVEMotorSound(currentTrain.Cars[0], 18.0, MotorSoundTables);
				}
			}
			CurrentProgress = 1;
			// place cars
			currentTrain.PlaceCars(0.0);
			currentControls = CurrentControls;
			IsLoading = false;
			return true;
	    }

	    public override string GetDescription(string trainPath, Encoding encoding = null)
	    {
		    try
		    {
			    string xmlFile = Path.CombineFile(trainPath, "train.xml");
			    if (File.Exists(xmlFile))
			    {
				    XmlDocument currentXML = new XmlDocument();
				    //Load the marker's XML file 
				    currentXML.Load(xmlFile);
				    XmlNodeList documentNodes = currentXML.DocumentElement?.SelectNodes("/openBVE/Train/Description");
				    if (documentNodes != null && documentNodes.Count > 0)
				    {
					    for (int i = 0; i < documentNodes.Count; i++)
					    {
						    if (!string.IsNullOrEmpty(documentNodes[i].InnerText))
						    {
							    return documentNodes[i].InnerText;
						    }
					    }
				    }
			    }
				string descriptionFile = Path.CombineFile(trainPath, "train.txt");
			    if (!File.Exists(descriptionFile))
			    {
					// No description, but a readme- Let's try that instead to at least give something
					descriptionFile = Path.CombineFile(trainPath, "readme.txt");
			    }
			    if (!File.Exists(descriptionFile))
			    {
				    // another variant on readme
				    descriptionFile = Path.CombineFile(trainPath, "read me.txt");
			    }
			    if (File.Exists(descriptionFile))
			    {
				    if (encoding == null)
				    {
					    return File.ReadAllText(descriptionFile, TextEncoding.GetSystemEncodingFromFile(descriptionFile));
				    }
				    return File.ReadAllText(descriptionFile, encoding);

			    }
		    }
		    catch(Exception ex)
		    {
				CurrentHost.ReportProblem(ProblemType.UnexpectedException, "Unable to get the description for train " + trainPath + " due to the exception: " + ex.Message);
		    }
		    return string.Empty;
	    }

	    public override string GetImage(string trainPath)
	    {
		    try
		    {
			    string imageFile = Path.CombineFile(trainPath, "train.png");
			    if (File.Exists(imageFile) && new FileInfo(imageFile).Length != 0)
			    {
				    return imageFile;
			    }
			    imageFile  = Path.CombineFile(trainPath, "train.gif");
			    if (File.Exists(imageFile) && new FileInfo(imageFile).Length != 0)
			    {
				    return imageFile;
			    }
			    imageFile  = Path.CombineFile(trainPath, "train.bmp");
			    if (File.Exists(imageFile) && new FileInfo(imageFile).Length != 0)
			    {
				    return imageFile;
			    }
		    }
		    catch (Exception ex)
		    {
			    CurrentHost.ReportProblem(ProblemType.UnexpectedException, "Unable to get the image for train " + trainPath + " due to the exception: " + ex.Message);
		    }
		    return null;
	    }


	    /// <summary>Attempts to load and parse the current train's panel configuration file.</summary>
	    /// <param name="train">The train</param>
	    /// <param name="encoding">The selected train encoding</param>
	    internal void ParsePanelConfig(TrainBase train, Encoding encoding)
	    {
		    train.Cars[train.DriverCar].CarSections = new Dictionary<CarSectionType, CarSection>();
		    train.Cars[train.DriverCar].CarSections.Add(CarSectionType.Interior, new CarSection(CurrentHost, ObjectType.Overlay, true));
		    string panelFile = Path.CombineFile(train.TrainFolder, "panel.xml");
		    if (!File.Exists(panelFile))
		    {
			    //Try animated variant too
			    panelFile = Path.CombineFile(train.TrainFolder, "panel.animated.xml");
		    }

		    if (File.Exists(panelFile))
		    {
			    FileSystem.AppendToLogFile("Loading train panel: " + panelFile);
			    try
			    {
				    /*
				     * First load the XML. We use this to determine
				     * whether this is a 2D or a 3D animated panel
				     */
				    XDocument currentXML = XDocument.Load(panelFile, LoadOptions.SetLineInfo);

				    // Check for null
				    if (currentXML.Root != null)
				    {
						List<XElement> documentElements = currentXML.Root.Elements("PanelAnimated").ToList();
					    if (documentElements.Count != 0)
					    {
						    PanelAnimatedXmlParser.ParsePanelAnimatedXml(System.IO.Path.GetFileName(panelFile), train, train.DriverCar);
						    if (train.Cars[train.DriverCar].CameraRestrictionMode != CameraRestrictionMode.Restricted3D)
						    {
							    train.Cars[train.DriverCar].CameraRestrictionMode = CameraRestrictionMode.NotAvailable;
							    Renderer.Camera.CurrentRestriction = CameraRestrictionMode.NotAvailable;
						    }
							return;
					    }

					    documentElements = currentXML.Root.Elements("Panel").ToList();
					    if (documentElements.Count != 0)
					    {
						    PanelXmlParser.ParsePanelXml(System.IO.Path.GetFileName(panelFile), train, train.DriverCar);
						    train.Cars[train.DriverCar].CameraRestrictionMode = CameraRestrictionMode.On;
						    Renderer.Camera.CurrentRestriction = CameraRestrictionMode.On;
						    return;
					    }
				    }
			    }
			    catch
			    {
				    var currentError = Translations.GetInterfaceString(HostApplication.OpenBve, new[] {"errors","critical_file"});
				    currentError = currentError.Replace("[file]", "panel.xml");
				    CurrentHost.ReportProblem(ProblemType.InvalidData, currentError);
				    Cancel = true;
				    return;
			    }

			    CurrentHost.AddMessage(MessageType.Error, false, "The panel.xml file " + panelFile + " failed to load. Falling back to legacy panel.");
		    }
		    else
		    {
			    panelFile = Path.CombineFile(train.TrainFolder, "panel.animated");
			    if (File.Exists(panelFile))
			    {
				    FileSystem.AppendToLogFile("Loading train panel: " + panelFile);
				    if (File.Exists(Path.CombineFile(train.TrainFolder, "panel2.cfg")) || File.Exists(Path.CombineFile(train.TrainFolder, "panel.cfg")))
				    {
					    FileSystem.AppendToLogFile("INFO: This train contains both a 2D and a 3D panel. The 3D panel will always take precedence");
				    }

				    CurrentHost.LoadObject(panelFile, encoding, out var currentObject);
				    var a = (AnimatedObjectCollection)currentObject;
				    if (a != null)
				    {
					    //HACK: If a == null , loading our animated object completely failed (Missing objects?). Fallback to trying the panel2.cfg
					    try
					    {
						    for (int i = 0; i < a.Objects.Length; i++)
						    {
							    CurrentHost.CreateDynamicObject(ref a.Objects[i].internalObject);
						    }

						    train.Cars[train.DriverCar].CarSections[CarSectionType.Interior].Groups[0].Elements = a.Objects;
						    if (train.Cars[train.DriverCar].CameraRestrictionMode != CameraRestrictionMode.Restricted3D)
						    {
							    train.Cars[train.DriverCar].CameraRestrictionMode = CameraRestrictionMode.NotAvailable;
							    Renderer.Camera.CurrentRestriction = CameraRestrictionMode.NotAvailable;
						    }

						    return;
					    }
					    catch
					    {
						    var currentError = Translations.GetInterfaceString(HostApplication.OpenBve, new[] {"errors","critical_file"});
						    currentError = currentError.Replace("[file]", "panel.animated");
						    CurrentHost.ReportProblem(ProblemType.InvalidData, currentError);
						    Cancel = true;
						    return;
					    }
				    }

				    CurrentHost.AddMessage(MessageType.Error, false, "The panel.animated file " + panelFile + " failed to load. Falling back to 2D panel.");
			    }
		    }

		    bool panel2 = false;
		    try
		    {
			    panelFile = Path.CombineFile(train.TrainFolder, "panel2.cfg");
			    if (File.Exists(panelFile))
			    {
				    FileSystem.AppendToLogFile("Loading train panel: " + panelFile);
				    panel2 = true;
				    Panel2CfgParser.ParsePanel2Config("panel2.cfg", train.TrainFolder, train.Cars[train.DriverCar]);
				    train.Cars[train.DriverCar].CameraRestrictionMode = CameraRestrictionMode.On;
				    Renderer.Camera.CurrentRestriction = CameraRestrictionMode.On;
			    }
			    else
			    {
				    panelFile = Path.CombineFile(train.TrainFolder, "panel.cfg");
				    if (File.Exists(panelFile))
				    {
					    FileSystem.AppendToLogFile("Loading train panel: " + panelFile);
					    PanelCfgParser.ParsePanelConfig(train.TrainFolder, train.Cars[train.DriverCar]);
					    train.Cars[train.DriverCar].CameraRestrictionMode = CameraRestrictionMode.On;
					    Renderer.Camera.CurrentRestriction = CameraRestrictionMode.On;
				    }
				    else
				    {
					    Renderer.Camera.CurrentRestriction = CameraRestrictionMode.NotAvailable;
				    }
			    }
		    }
		    catch
		    {
			    var currentError = Translations.GetInterfaceString(HostApplication.OpenBve, new[] {"errors","critical_file"});
			    currentError = currentError.Replace("[file]", panel2 ? "panel2.cfg" : "panel.cfg");
			    CurrentHost.ReportProblem(ProblemType.InvalidData, currentError);
			    Cancel = true;
		    }
	    }
    }
}
