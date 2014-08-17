/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class JobManager
    {
        public class JobAction
        {
            public Func<JobID> Action;
            public IRCRequest CommandRequest;

            public bool IsCommand
            {
                get
                {
                    return CommandRequest != null;
                }
            }
        }

        public class IRCRequest
        {
            public CommandHandler.CommandArguments Command { get; set; }
            public IRCRequestType Type { get; set; }
            public uint Target { get; set; }
        }

        public enum IRCRequestType
        {
            TYPE_APP,
            TYPE_SUB
        }

        private static Dictionary<JobID, JobAction> Jobs = new Dictionary<JobID, JobAction>();

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

            Log.WriteDebug("Job Manager", "New IRC job: {0} ({1})", jobID, request.Command.MessageData.Message);

            Jobs.Add(jobID, job);
        }

        public static JobAction RemoveJob(JobID jobID)
        {
            JobAction job;

            if (Jobs.TryGetValue(jobID, out job))
            {
                Jobs.Remove(jobID);
            }

            Log.WriteDebug("Job Manager", "Removed job {0}. {1} jobs left", jobID, Jobs.Count);

            return job;
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
