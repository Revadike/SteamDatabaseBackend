/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class JobAction
    {
        public Func<JobID> Action;
        public CommandArguments Command;
        public object Metadata;
    }

    internal static class JobManager
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

#if DEBUG
            Log.WriteDebug(nameof(JobManager), $"New job: {jobID}");
#endif

            Jobs.TryAdd(jobID, job);
        }

        public static void AddJob(Func<JobID> action, object metadata)
        {
            var jobID = action();

            var job = new JobAction
            {
                Action = action,
                Metadata = metadata,
            };

#if DEBUG
            Log.WriteDebug(nameof(JobManager), $"New job: {jobID} (with data)");
#endif

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

#if DEBUG
            // Chat rooms don't have full message saved
            Log.WriteDebug(nameof(JobManager), $"New chat job: {jobID} ({command.Message})");
#endif

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
#if DEBUG
                Log.WriteDebug(nameof(JobManager), $"Removed job: {jobID} ({Jobs.Count} jobs left)");
#endif

                return true;
            }

            return false;
        }

        public static void RestartJobsIfAny()
        {
            if (Jobs.IsEmpty)
            {
                return;
            }

            Log.WriteInfo(nameof(JobManager), $"Restarting {Jobs.Count} jobs");

            foreach (var (jobId, job) in Jobs)
            {
                TryRemoveJob(jobId);

                if (job.Command == null)
                {
                    AddJob(job.Action);
                }
                else
                {
                    AddJob(job.Action, job.Command);
                }
            }
        }
    }
}
