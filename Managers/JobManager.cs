/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class JobAction
    {
        public Func<JobID> Action;
        public CommandArguments Command;
    }

    static class JobManager
    {
        private static readonly ConcurrentDictionary<JobID, JobAction> Jobs = new ConcurrentDictionary<JobID, JobAction>();

        public static int JobsCount => Jobs.Count;

        public static void AddJob(Func<JobID> action)
        {
            var jobID = action();

            var job = new JobAction
            {
                Action = action
            };

            Log.WriteDebug("Job Manager", "New job: {0}", jobID);

            Jobs.TryAdd(jobID, job);
        }

        public static void AddJob(Func<JobID> action, CommandArguments command)
        {
            var jobID = action();

            var job = new JobAction
            {
                Action = action,
                Command = command
            };

            // Chat rooms don't have full message saved
            Log.WriteDebug("Job Manager", "New chat job: {0} ({1})", jobID, command.Message);

            Jobs.TryAdd(jobID, job);
        }

        public static bool TryRemoveJob(JobID jobID)
        {
            return TryRemoveJob(jobID, out _);
        }

        public static bool TryRemoveJob(JobID jobID, out JobAction job)
        {
            if (Jobs.TryRemove(jobID, out job))
            {
                Log.WriteDebug("Job Manager", "Removed job: {0} ({1} jobs left)", jobID, Jobs.Count);

                return true;
            }

            return false;
        }

        public static void RestartJobsIfAny()
        {
            if (Jobs.Count == 0)
            {
                return;
            }

            Log.WriteInfo("Job Manager", "Restarting {0} jobs", Jobs.Count);

            var jobs = Jobs;

            Jobs.Clear();

            foreach (var job in jobs)
            {
                Jobs.TryAdd(job.Value.Action(), job.Value);
            }
        }
    }
}
