/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class JobAction
    {
        public Func<JobID> Action;
        public JobManager.IRCRequest CommandRequest;
        public DepotProcessor.ManifestJob ManifestJob;

        public bool IsManifestJob
        {
            get
            {
                return ManifestJob != null;
            }
        }

        public bool IsCommand
        {
            get
            {
                return CommandRequest != null;
            }
        }
    }

    static class JobManager
    {
        private const uint CHAT_COMMAND_TIMEOUT = 5;

        public class IRCRequest
        {
            public CommandArguments Command { get; set; }
            public IRCRequestType Type { get; set; }
            public uint Target { get; set; }
            public DateTime ExpireTime { get; set; }
        }

        public enum IRCRequestType
        {
            TYPE_APP,
            TYPE_SUB
        }

        private static readonly ConcurrentDictionary<JobID, JobAction> Jobs = new ConcurrentDictionary<JobID, JobAction>();

        public static void AddJob(Func<JobID> action)
        {
            RemoveStaleJobs();

            var jobID = action();

            var job = new JobAction
            {
                Action = action
            };

            Log.WriteDebug("Job Manager", "New job: {0}", jobID);

            Jobs.TryAdd(jobID, job);
        }

        public static void AddJob(Func<JobID> action, IRCRequest request)
        {
            RemoveStaleJobs();

            var jobID = action();

            request.ExpireTime = DateTime.Now + TimeSpan.FromSeconds(CHAT_COMMAND_TIMEOUT);

            var job = new JobAction
            {
                Action = action,
                CommandRequest = request
            };

            // Chat rooms don't have full message saved
            Log.WriteDebug("Job Manager", "New chat job: {0} ({1})", jobID, request.Command.Message);

            Jobs.TryAdd(jobID, job);
        }

        public static void AddJob(Func<JobID> action, DepotProcessor.ManifestJob manifestJob)
        {
            RemoveStaleJobs();

            var jobID = action();

            var job = new JobAction
            {
                Action = action,
                ManifestJob = manifestJob
            };

            Log.WriteDebug("Job Manager", "New depot job: {0} ({1} - {2})", jobID, manifestJob.DepotID, manifestJob.ManifestID);

            Jobs.TryAdd(jobID, job);
        }

        public static bool TryRemoveJob(JobID jobID, out JobAction job)
        {
            RemoveStaleJobs();

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

        public static void CancelChatJobsIfAny()
        {
            var jobs = Jobs.Where(job => job.Value.IsCommand).ToList();

            JobAction dummy;

            foreach (var job in jobs)
            {
                CommandHandler.ReplyToCommand(job.Value.CommandRequest.Command, true, "Your request failed.");

                Jobs.TryRemove(job.Key, out dummy);
            }
        }

        private static void RemoveStaleJobs()
        {
            var jobs = Jobs.Where(job => job.Value.IsCommand && DateTime.Now >= job.Value.CommandRequest.ExpireTime).ToList();

            JobAction dummy;

            foreach (var job in jobs)
            {
                Log.WriteDebug("Job Manager", "Timed out job: {0} ({1} jobs left)", job.Key, Jobs.Count);

                CommandHandler.ReplyToCommand(job.Value.CommandRequest.Command, true, "Your request timed out.");

                Jobs.TryRemove(job.Key, out dummy);
            }
        }
    }
}
