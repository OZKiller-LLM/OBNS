// OBNS: a copy of upstream's source/Plugins/Train.OpenBve/Train/BVE5/MotorSoundTable.cs (OpenBVE 1.14.1.2), laid over
// the desktop build's mirror (OBNS/build-desktop.ps1) and compiled by Android. The parser is rewritten so that BVE5
// motor noise tables are read as BVE5 reads them; it uses nothing beyond upstream's own types, for upstream to take:
// - comments ('#', '//') are removed from each line (upstream's string[] TrimBVE5Comments drops its results, so
//   the column heading row and notes were read as rows and reported as invalid speeds);
// - a blank cell is the value interpolated from its column's rows either side (upstream read it as 1, a full
//   volume where the table meant to fade between its rows);
// - the frequency and volume tables are each read on their own, then both are taken at every speed either names
//   (upstream merged their rows by speed, so a speed only one table named had no volume, or pitch 0, in the other);
// - a speed given twice takes its later row (upstream's Add threw, and the train lost its motor noise).
// On an upstream update: copy the file again and reapply this parser, or drop this copy if upstream has fixed these.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenBveApi;
using OpenBveApi.Interface;
using TrainManager.Car;
using TrainManager.Motor;

namespace Train.OpenBve
{
	class Bve5MotorSoundTableParser
	{
		/// <summary>Parses a set of BVE5 motor sound tables</summary>
		/// <param name="car">The car</param>
		/// <param name="motorSoundPitch">The motor pitch table</param>
		/// <param name="motorSoundGain">The motor gain table</param>
		/// <param name="brakeSoundPitch">The brake pitch table</param>
		/// <param name="brakeSoundGain">The brake gain table</param>
		internal static BVE5MotorSound Parse(CarBase car, string motorSoundPitch, string motorSoundGain, string brakeSoundPitch, string brakeSoundGain)
		{
			BVE5MotorSound motorSound = new BVE5MotorSound(car);
			motorSound.MotorSoundTable = BuildTable(motorSoundPitch, motorSoundGain, "MotorSound");
			motorSound.BrakeSoundTable = BuildTable(brakeSoundPitch, brakeSoundGain, "BrakeSound");
			return motorSound;
		}

		/// <summary>The motor sound table from a frequency (pitch) table and a volume (gain) table.</summary>
		private static BVE5MotorSoundTableEntry[] BuildTable(string pitchFile, string gainFile, string name)
		{
			if (!File.Exists(pitchFile) || !File.Exists(gainFile))
			{
				Plugin.CurrentHost.AddMessage("Missing BVE5 " + name + " table file.");
				return new BVE5MotorSoundTableEntry[0];
			}

			// A sound the frequency table leaves out plays at its own pitch; one the volume table leaves out is silent.
			NoiseTable pitch = NoiseTable.Read(pitchFile, 1.0);
			NoiseTable gain = NoiseTable.Read(gainFile, 0.0);
			double[] speeds = pitch.Speeds.Union(gain.Speeds).OrderBy(speed => speed).ToArray();
			int sounds = Math.Max(pitch.Columns, gain.Columns);
			BVE5MotorSoundTableEntry[] table = new BVE5MotorSoundTableEntry[speeds.Length];
			for (int i = 0; i < speeds.Length; i++)
			{
				table[i] = new BVE5MotorSoundTableEntry(speeds[i]) { Sounds = new SoundState[sounds] };
				for (int j = 0; j < sounds; j++)
				{
					table[i].Sounds[j].Pitch = pitch.ValueAt(j, speeds[i]);
					table[i].Sounds[j].Gain = gain.ValueAt(j, speeds[i]);
				}
			}

			return table;
		}

		/// <summary>One BVE5 motor noise table: a column of values per sound, against speed.</summary>
		private class NoiseTable
		{
			/// <summary>The rows' speeds, in km/h, ascending.</summary>
			internal double[] Speeds;

			/// <summary>The values: Values[sound][row].</summary>
			private double[][] values;

			/// <summary>The value of a sound with no column in the table.</summary>
			private double missing;

			internal int Columns => values.Length;

			/// <summary>Reads a table: after the header, rows of a speed and a value per sound, blank cells interpolated.</summary>
			internal static NoiseTable Read(string fileName, double missing)
			{
				Encoding encoding = Text.DetermineBVE5FileEncoding(fileName);
				string[] lines = File.ReadAllLines(fileName, encoding);
				if (lines.Length == 0 || !lines[0].TrimStart().StartsWith("BveTs Motor Noise Table", StringComparison.InvariantCultureIgnoreCase))
				{
					throw new Exception("Expected a BveTs Motor Noise Table, got: " + (lines.Length > 0 ? lines[0].Split(':')[0] : "an empty file"));
				}

				SortedDictionary<double, double[]> rows = new SortedDictionary<double, double[]>();
				int columns = 0;
				for (int i = 1; i < lines.Length; i++)
				{
					string line = lines[i].TrimBVE5Comments().Trim();
					if (line.Length == 0)
					{
						continue;
					}

					string[] cells = line.Split(',');
					if (!double.TryParse(cells[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double speed))
					{
						Plugin.CurrentHost.AddMessage(MessageType.Error, false, "Invalid speed encountered at Line " + (i + 1) + " in BveTs Motor Noise table " + fileName);
						continue;
					}

					double[] row = new double[cells.Length - 1];
					for (int j = 1; j < cells.Length; j++)
					{
						string cell = cells[j].Trim();
						row[j - 1] = double.NaN;
						if (cell.Length != 0 && !double.TryParse(cell, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out row[j - 1]))
						{
							row[j - 1] = double.NaN;
							Plugin.CurrentHost.AddMessage(MessageType.Error, false, "Invalid value encountered at Line " + (i + 1) + " Index " + (j - 1) + " in BveTs Motor Noise table " + fileName);
						}
					}

					columns = Math.Max(columns, row.Length);
					// A speed given twice takes its later row.
					rows[speed] = row;
				}

				NoiseTable table = new NoiseTable { Speeds = rows.Keys.ToArray(), values = new double[columns][], missing = missing };
				double[][] byRow = rows.Values.ToArray();
				for (int j = 0; j < columns; j++)
				{
					table.values[j] = new double[byRow.Length];
					for (int r = 0; r < byRow.Length; r++)
					{
						table.values[j][r] = j < byRow[r].Length ? byRow[r][j] : double.NaN;
					}

					FillBlanks(table.Speeds, table.values[j]);
				}

				return table;
			}

			/// <summary>
			/// Fills a column's blank cells: between two values, linearly by speed; before the first value or after
			/// the last, with that value; a column with no values at all is silent (0).
			/// </summary>
			private static void FillBlanks(double[] speeds, double[] column)
			{
				int previous = -1;
				for (int r = 0; r <= column.Length; r++)
				{
					if (r < column.Length && double.IsNaN(column[r]))
					{
						continue;
					}

					// Rows previous+1 .. r-1 are blank, between the values at previous and r (either may be missing).
					for (int b = previous + 1; b < r; b++)
					{
						if (previous < 0 && r >= column.Length)
						{
							column[b] = 0.0;
						}
						else if (previous < 0)
						{
							column[b] = column[r];
						}
						else if (r >= column.Length || speeds[r] <= speeds[previous])
						{
							column[b] = column[previous];
						}
						else
						{
							column[b] = column[previous] + (column[r] - column[previous]) * (speeds[b] - speeds[previous]) / (speeds[r] - speeds[previous]);
						}
					}

					previous = r;
				}
			}

			/// <summary>A sound's value at a speed: interpolated between the rows either side, the end rows' beyond them.</summary>
			internal double ValueAt(int column, double speed)
			{
				if (column >= values.Length || Speeds.Length == 0)
				{
					return missing;
				}

				double[] value = values[column];
				if (speed <= Speeds[0])
				{
					return value[0];
				}

				for (int r = 1; r < Speeds.Length; r++)
				{
					if (speed <= Speeds[r])
					{
						return value[r - 1] + (value[r] - value[r - 1]) * (speed - Speeds[r - 1]) / (Speeds[r] - Speeds[r - 1]);
					}
				}

				return value[value.Length - 1];
			}
		}
	}
}
