/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
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
        public class IRCRequest
        {
            public CommandArguments Command { get; set; }
            public IRCRequestType Type { get; set; }
            public uint Target { get; set; }
        }

        public enum IRCRequestType
        {
            TYPE_APP,
            TYPE_SUB
        }

        private static readonly Dictionary<JobID, JobAction> Jobs = new Dictionary<JobID, JobAction>();

        public static void AddJob(Func<JobID> action)
        {
            var jobID = action();

            var job = new JobAction
            {
                Action = action
            };

            Log.WriteDebug("Job Manager", "New job: {0}", jobID);

            Jobs.Add(jobID, job);
        }

        public static void AddJob(Func<JobID> action, IRCRequest request)
        {
            var jobID = action();

            var job = new JobAction
            {
                Action = action,
                CommandRequest = request
            };

            // Chat rooms don't have full message saved
            Log.WriteDebug("Job Manager", "New chat job: {0} ({1})", jobID, request.Command.IsChatRoomCommand ? request.Command.Message : request.Command.MessageData.Message);

            Jobs.Add(jobID, job);
        }

        public static void AddJob(Func<JobID> action, DepotProcessor.ManifestJob manifestJob)
        {
            var jobID = action();

            var job = new JobAction
            {
                Action = action,
                ManifestJob = manifestJob
            };

            Log.WriteDebug("Job Manager", "New depot job: {0} ({1} - {2})", jobID, manifestJob.DepotID, manifestJob.ManifestID);

            Jobs.Add(jobID, job);
        }

        public static bool TryRemoveJob(JobID jobID, out JobAction job)
        {
            if (Jobs.TryGetValue(jobID, out job))
            {
                Jobs.Remove(jobID);

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
                Jobs.Add(job.Value.Action(), job.Value);
            }
        }

        public static void CancelChatJobsIfAny()
        {
            var jobs = Jobs.Where(job => job.Value.IsCommand);

            foreach (var job in jobs)
            {
                CommandHandler.ReplyToCommand(job.Value.CommandRequest.Command, "Your request failed.");

                Jobs.Remove(job.Key);
            }
        }
    }
}
