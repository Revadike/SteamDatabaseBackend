/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class JobManager
    {
        public class JobAction
        {
            public JobID JobID;
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

        private static List<JobAction> Jobs = new List<JobAction>();

        public static void AddJob(Func<JobID> action)
        {
            var jobID = action();

            var job = new JobAction
            {
                JobID = jobID,
                Action = action
            };

            Log.WriteDebug("Job Manager", "New job: {0}", jobID);

            Jobs.Add(job);
        }

        public static void AddJob(Func<JobID> action, IRCRequest request)
        {
            var jobID = action();

            var job = new JobAction
            {
                JobID = jobID,
                Action = action,
                CommandRequest = request
            };

            Log.WriteDebug("Job Manager", "New IRC job: {0} ({1})", jobID, request.Command.MessageData.Message);

            Jobs.Add(job);
        }

        public static JobAction RemoveJob(JobID jobID)
        {
            var job = Jobs.Find(r => r.JobID == jobID);

            if (job != null)
            {
                Jobs.Remove(job);
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

            foreach (var job in Jobs)
            {
                job.JobID = job.Action();
            }
        }

        public static void CancelChatJobsIfAny()
        {
            foreach (var job in Jobs)
            {
                if (job.IsCommand)
                {
                    CommandHandler.ReplyToCommand(job.CommandRequest.Command, "Your request failed.");

                    Jobs.Remove(job);
                }
            }
        }
    }
}
