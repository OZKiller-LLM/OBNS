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

// OBNS: BVE Trainsim 5 trains (OBNS/BVE5.md), part of upstream's train plugin in the OBNS build.

using System;
using System.Collections.Generic;
using TrainManager.Power;

namespace Train.OpenBve
{
	/// <summary>
	/// A BVE5 performance table ("BveTs Vehicle Performance Table", 0.01 to 2.01): the first column
	/// the speed in km/h, ascending; each further column one curve - a step of the controller.
	/// Between speeds the values are linear; beyond the table they keep its first or last row.
	/// </summary>
	internal sealed class Bve5Table
	{
		internal double[] Speeds = new double[0];
		/// <summary>Values[curve][row].</summary>
		internal double[][] Values = new double[0][];

		internal int Curves => Values.Length;

		internal static Bve5Table Load(string fileName)
		{
			Bve5Table table = new Bve5Table();
			if (string.IsNullOrEmpty(fileName) || !System.IO.File.Exists(fileName))
			{
				return table;
			}

			List<double> speeds = new List<double>();
			List<List<double>> columns = new List<List<double>>();
			foreach (string[] row in Bve5Text.Read(fileName).Rows())
			{
				double speed = Bve5Text.Number(row[0], double.NaN);
				if (double.IsNaN(speed) || speeds.Count > 0 && speed <= speeds[speeds.Count - 1])
				{
					continue;
				}

				speeds.Add(speed);
				for (int c = 1; c < row.Length; c++)
				{
					if (columns.Count < c)
					{
						// A curve that starts late is zero before it.
						columns.Add(new List<double>(new double[speeds.Count - 1]));
					}

					columns[c - 1].Add(Bve5Text.Number(row[c], 0));
				}

				// A short row leaves its missing cells at zero.
				foreach (List<double> column in columns)
				{
					while (column.Count < speeds.Count)
					{
						column.Add(0);
					}
				}
			}

			table.Speeds = speeds.ToArray();
			table.Values = new double[columns.Count][];
			for (int c = 0; c < columns.Count; c++)
			{
				table.Values[c] = columns[c].ToArray();
			}

			return table;
		}

		/// <summary>A curve's value at a speed (km/h); 0 for a curve the table does not have.</summary>
		internal double Get(int curve, double kmh)
		{
			if (curve < 0 || curve >= Values.Length || Speeds.Length == 0)
			{
				return 0;
			}

			double[] values = Values[curve];
			if (kmh <= Speeds[0])
			{
				return values[0];
			}

			int last = Speeds.Length - 1;
			if (kmh >= Speeds[last])
			{
				return values[last];
			}

			int i = Array.BinarySearch(Speeds, kmh);
			if (i >= 0)
			{
				return values[i];
			}

			i = ~i;
			double t = (kmh - Speeds[i - 1]) / (Speeds[i] - Speeds[i - 1]);
			return values[i - 1] + (values[i] - values[i - 1]) * t;
		}

		/// <summary>The largest value of a curve over the table.</summary>
		internal double Max(int curve)
		{
			double max = 0;
			if (curve >= 0 && curve < Values.Length)
			{
				foreach (double v in Values[curve])
				{
					max = Math.Max(max, v);
				}
			}

			return max;
		}
	}

	/// <summary>A BVE5 notch parameter table: named rows, a value per curve.</summary>
	internal sealed class Bve5StepParameters
	{
		private readonly Dictionary<string, double[]> rows = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

		internal static Bve5StepParameters Load(string fileName)
		{
			Bve5StepParameters parameters = new Bve5StepParameters();
			if (string.IsNullOrEmpty(fileName) || !System.IO.File.Exists(fileName))
			{
				return parameters;
			}

			foreach (string[] row in Bve5Text.Read(fileName).Rows())
			{
				if (row[0].Length == 0 || char.IsDigit(row[0][0]))
				{
					continue;
				}

				double[] values = new double[row.Length - 1];
				for (int c = 1; c < row.Length; c++)
				{
					values[c - 1] = Bve5Text.Number(row[c], double.NaN);
				}

				parameters.rows[row[0]] = values;
			}

			return parameters;
		}

		/// <summary>A row's value for a curve, or the fallback where the row or its cell is missing.</summary>
		internal double Get(string row, int curve, double fallback)
		{
			return rows.TryGetValue(row, out double[] values) && curve >= 0 && curve < values.Length && !double.IsNaN(values[curve]) ? values[curve] : fallback;
		}
	}

	/// <summary>A BVE5 vehicle performance file's [Power] or [Brake]: its tables.</summary>
	internal sealed class Bve5PerformanceSet
	{
		internal Bve5Table Force = new Bve5Table();
		internal Bve5Table MaxForce = new Bve5Table();
		internal Bve5Table Current = new Bve5Table();
		internal Bve5Table MaxCurrent = new Bve5Table();
		internal Bve5Table NoLoadCurrent = new Bve5Table();
		internal Bve5StepParameters Params = new Bve5StepParameters();

		/// <summary>
		/// The controller step a handle notch settles on at a speed (km/h): starting from the first, a
		/// step advances to the next while the handle is at that step's RequiredNotch_Up or above and
		/// the current is within its CurrentLimitingValue_Empty, as BVE5's controller does once its
		/// step delays have passed. With no parameters, notch n is step n.
		/// </summary>
		internal int StepFor(int notch, double kmh)
		{
			int steps = Force.Curves;
			if (steps == 0 || notch <= 0)
			{
				return -1;
			}

			int step = 0;
			while (step + 1 < steps)
			{
				double required = Params.Get("RequiredNotch_Up", step, step + 2);
				if (notch < required)
				{
					break;
				}

				double limit = Params.Get("CurrentLimitingValue_Empty", step, 0);
				if (limit > 0 && Current.Get(step, kmh) > limit)
				{
					break;
				}

				step++;
			}

			return step;
		}

		internal static void Load(string performanceFile, out Bve5PerformanceSet power, out Bve5PerformanceSet brake)
		{
			power = new Bve5PerformanceSet();
			brake = new Bve5PerformanceSet();
			if (string.IsNullOrEmpty(performanceFile) || !System.IO.File.Exists(performanceFile))
			{
				return;
			}

			Dictionary<string, Dictionary<string, string>> sections = Bve5Text.Read(performanceFile).Sections();
			power = LoadSet(performanceFile, sections, "Power");
			brake = LoadSet(performanceFile, sections, "Brake");
		}

		private static Bve5PerformanceSet LoadSet(string performanceFile, Dictionary<string, Dictionary<string, string>> sections, string name)
		{
			Bve5PerformanceSet set = new Bve5PerformanceSet();
			if (!sections.TryGetValue(name, out Dictionary<string, string> keys))
			{
				return set;
			}

			string Path(string key) => keys.TryGetValue(key, out string value) ? Bve5Text.ResolvePath(performanceFile, value) : null;
			set.Force = Bve5Table.Load(Path("Force"));
			set.MaxForce = Bve5Table.Load(Path("MaxForce"));
			set.Current = Bve5Table.Load(Path("Current"));
			set.MaxCurrent = Bve5Table.Load(Path("MaxCurrent"));
			set.NoLoadCurrent = Bve5Table.Load(Path("NoLoadCurrent"));
			set.Params = Bve5StepParameters.Load(Path("Params"));
			return set;
		}
	}

	/// <summary>
	/// A power notch of a BVE5 train, as OpenBVE's motor cars take it: the acceleration (m/s²) a
	/// motor car gives itself at a speed (m/s) - the force of the step the controller settles on,
	/// per motor car, over the motor car's mass - scaled so that the whole train, its trailers and
	/// the cars' rotating parts included, accelerates as the force over its effective mass.
	/// </summary>
	internal sealed class Bve5AccelerationCurve : AccelerationCurve
	{
		private readonly Bve5PerformanceSet power;
		private readonly int notch;
		private readonly double scale;
		private readonly double maximum;

		/// <param name="scale">Per newton of one motor car's force, the acceleration (m/s²) it is to give itself.</param>
		internal Bve5AccelerationCurve(Bve5PerformanceSet power, int notch, double scale)
		{
			this.power = power;
			this.notch = notch;
			this.scale = scale;
			for (double kmh = 0; kmh <= 200; kmh += 1)
			{
				maximum = Math.Max(maximum, GetAccelerationOutput(kmh / 3.6));
			}
		}

		public override double GetAccelerationOutput(double Speed)
		{
			double kmh = Math.Abs(Speed) * 3.6;
			int step = power.StepFor(notch, kmh);
			return step < 0 ? 0 : Math.Max(0, power.Force.Get(step, kmh)) * scale;
		}

		public override double MaximumAcceleration => maximum;
	}
}
