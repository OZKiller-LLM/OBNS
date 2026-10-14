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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;
using OpenBveApi;
using OpenBveApi.Interface;
using OpenBveApi.Trains;
using TrainManager.Trains;

namespace OpenBve.Android
{
	/*
	 * Scripted trains (Route.TfoXML) loaded on several cores. Upstream's route parser loads them
	 * one after another at the end of the route (ApplyRouteData), each through the host's
	 * ParseTrackFollowingObject, and only adds what that returns to its list: on the Tuen Ma Line
	 * 36 trains, 13 s on a Galaxy A73 and 20-55 s on an A05s, nearly all inside the train plugin.
	 * So each request reads its XML file on the parser's thread (quick) and returns the train
	 * object at once, while the train plugin loads the train into that object on a worker thread;
	 * when the route has loaded (AndroidRouteLoader), Finish waits for every worker and takes out
	 * of the list any train that failed - exactly the trains upstream would have kept. Which
	 * trains are loaded is the parser's own choice (routes pick some by $Include at random).
	 *
	 * Each worker has its own instance of every train plugin (a plugin's parsers keep per-train
	 * state), and the plugin's static per-train fields are [ThreadStatic] in the port's copies
	 * (Train.OpenBve.Android). The host's object cache, dynamic object list, object visibility and
	 * error count are locked or atomic for this; texture and sound registration already were.
	 * --ez parallel_scripted_trains false loads them one at a time, as upstream.
	 */
	internal static class ScriptedTrainLoader
	{
		public static bool Enabled = true;

		private sealed class Job
		{
			public TrackFollowingObjectParser.Prepared Prepared;
			public TaskCompletionSource<bool> Done;
		}

		private static BlockingCollection<Job> queue;
		private static readonly List<Job> jobs = new List<Job>();
		private static int completed, failed;
		private static readonly object SharedPlugins = new object();

		/// <summary>For the load log.</summary>
		public static string Describe() => !Enabled ? "scripted trains loaded one at a time" :
			"scripted trains: " + completed + " of " + (completed + failed) + " loaded in parallel" + (failed > 0 ? ", " + failed + " failed" : string.Empty);

		/// <summary>Loads a scripted train: the file read now, the train loaded on a worker (or now, when off).</summary>
		public static TrainBase Load(AndroidHost host, string objectPath, string file)
		{
			if (!Enabled)
			{
				return TrackFollowingObjectParser.Parse(host, objectPath, file);
			}

			TrackFollowingObjectParser.Prepared prepared = TrackFollowingObjectParser.Prepare(host, objectPath, file);
			if (prepared == null)
			{
				return null;
			}

			if (queue == null)
			{
				Start(host);
			}

			Job job = new Job { Prepared = prepared, Done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
			jobs.Add(job);
			queue.Add(job);
			return prepared.Train;
		}

		/// <summary>
		/// Waits for every train still loading, and takes those that failed out of the route's list
		/// of track following objects. Call when the route has loaded (or failed to).
		/// </summary>
		public static void Finish(List<AbstractTrain> trackFollowingObjects)
		{
			if (queue == null)
			{
				return;
			}

			queue.CompleteAdding();
			foreach (Job job in jobs)
			{
				bool loaded;
				try
				{
					loaded = job.Done.Task.GetAwaiter().GetResult();
				}
				catch (Exception)
				{
					loaded = false;
				}

				if (loaded)
				{
					completed++;
				}
				else
				{
					failed++;
					trackFollowingObjects?.Remove(job.Prepared.Train);
				}
			}

			jobs.Clear();
			queue = null;
		}

		/// <summary>Forgets a previous route's counts.</summary>
		public static void Reset()
		{
			completed = failed = 0;
		}

		private static void Start(AndroidHost host)
		{
			BlockingCollection<Job> work = new BlockingCollection<Job>();
			queue = work;
			int workers = Math.Max(1, Environment.ProcessorCount - 1);
			for (int w = 0; w < workers; w++)
			{
				Task.Factory.StartNew(() =>
				{
					List<TrainInterface> plugins = null;
					foreach (Job job in work.GetConsumingEnumerable())
					{
						try
						{
							plugins ??= PluginsForThisWorker(host);
							bool loaded;
							if (plugins.Count > 0)
							{
								loaded = TrackFollowingObjectParser.Complete(host, job.Prepared, plugins);
							}
							else
							{
								// No instance of its own: the host's plugins, one train at a time.
								lock (SharedPlugins)
								{
									loaded = TrackFollowingObjectParser.Complete(host, job.Prepared, null);
								}
							}

							job.Done.SetResult(loaded);
						}
						catch (Exception ex)
						{
							host.AddMessage(MessageType.Error, false, "Could not load the track following object " + job.Prepared.FileName + ": " + ex.Message);
							job.Done.SetResult(false);
						}
					}
				}, TaskCreationOptions.LongRunning);
			}
		}

		/// <summary>A fresh instance of every train plugin, set up as the host set up its own.</summary>
		private static List<TrainInterface> PluginsForThisWorker(AndroidHost host)
		{
			List<TrainInterface> plugins = new List<TrainInterface>();
			foreach (ContentLoadingPlugin plugin in host.Plugins)
			{
				if (plugin.Train == null)
				{
					continue;
				}

				try
				{
					TrainInterface instance = (TrainInterface)Activator.CreateInstance(plugin.Train.GetType());
					instance.Load(host, host.FileSystem, host.Options, host.Renderer);
					plugins.Add(instance);
				}
				catch (Exception ex)
				{
					Log.Warn("OpenBVE", "scripted trains: no worker instance of " + plugin.Train.GetType().Name + ": " + ex.Message);
				}
			}

			return plugins;
		}
	}
}
