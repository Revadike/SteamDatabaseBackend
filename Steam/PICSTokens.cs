/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Linq;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSTokens : SteamHandler
    {
        public PICSTokens(CallbackManager manager)
            : base(manager)
        {
            manager.Register(new Callback<SteamApps.PICSTokensCallback>(OnPICSTokens));
        }

        private static void OnPICSTokens(SteamApps.PICSTokensCallback callback)
        {
            Log.WriteDebug("Steam", "Tokens granted: {0} - Tokens denied: {1}", callback.AppTokens.Count, callback.AppTokensDenied.Count);

            var apps = callback.AppTokensDenied
                .Select(app => Utils.NewPICSRequest(app))
                .Concat(callback.AppTokens.Select(app => Utils.NewPICSRequest(app.Key, app.Value)));

            Func<JobID> func = () => Steam.Instance.Apps.PICSGetProductInfo(apps, Enumerable.Empty<SteamApps.PICSRequest>());

            JobAction job;

            // We have to preserve CommandRequest between jobs
            if (JobManager.TryRemoveJob(callback.JobID, out job) && job.IsCommand)
            {
                JobManager.AddJob(func, job.CommandRequest);

                return;
            }

            JobManager.AddJob(func);
        }
    }
}
