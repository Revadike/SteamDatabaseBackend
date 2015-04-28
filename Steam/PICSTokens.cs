/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSTokens : SteamHandler
    {
        private class SillyToken
        {
            public uint AppID { get; set; }
            public ulong Token { get; set; }
        }

        private static Dictionary<uint, ulong> SecretTokens;

        public PICSTokens(CallbackManager manager)
            : base(manager)
        {
            manager.Register(new Callback<SteamApps.PICSTokensCallback>(OnPICSTokens));

            Reload();
        }

        public static void Reload(CommandArguments command)
        {
            Reload();

            command.ReplyAsNotice = true;
            CommandHandler.ReplyToCommand(command, "Reloaded {0} token overrides", SecretTokens.Count);
        }

        private static void Reload()
        {
            using (var db = Database.GetConnection())
            {
                SecretTokens = db.Query<SillyToken>("SELECT `AppID`, `Token` FROM `PICSTokens`").ToDictionary(x => x.AppID, x => x.Token);
            }
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

        public static ulong GetToken(uint id)
        {
            if (SecretTokens.ContainsKey(id))
            {
                Log.WriteInfo("PICSTokens", "Using an overriden token for appid {0}", id);

                return SecretTokens[id];
            }

            return 0;
        }

        public static void HandleToken(uint id, ulong accessToken)
        {
            if (!SecretTokens.ContainsKey(id))
            {
                SecretTokens.Add(id, accessToken);

                IRC.Instance.SendOps("[TOKENS] Added a new token that the bot got itself: {0} ({1})", id, Steam.GetAppName(id));

                Log.WriteInfo("PICSTokens", "New token for appid {0}", id);

                using (var db = Database.GetConnection())
                {
                    db.Execute("INSERT INTO `PICSTokens` (`AppID`, `Token`, `CommunityID`) VALUES(@AppID, @Token, @CommunityID)",
                        new { AppID = id, Token = accessToken, CommunityID = Steam.Instance.Client.SteamID.ConvertToUInt64() }
                    );
                }
            }
        }
    }
}
