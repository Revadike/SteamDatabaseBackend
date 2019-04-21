/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
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
        private class PICSToken
        {
            public uint AppID { get; set; }
            public ulong Token { get; set; }
        }

        private static Dictionary<uint, ulong> SecretTokens;

        public PICSTokens(CallbackManager manager)
        {
            manager.Subscribe<SteamApps.PICSTokensCallback>(OnPICSTokens);

            Reload();
        }

        public static void Reload(CommandArguments command)
        {
            Reload();

            command.Notice("Reloaded {0} token overrides", SecretTokens.Count);
        }

        private static void Reload()
        {
            var oldTokens = SecretTokens;

            using (var db = Database.Get())
            {
                SecretTokens = db.Query<PICSToken>("SELECT `AppID`, `Token` FROM `PICSTokens`").ToDictionary(x => x.AppID, x => x.Token);
            }

            if (oldTokens == null)
            {
                return;
            }

            var apps = SecretTokens
                .Where(x => !oldTokens.ContainsKey(x.Key))
                .Select(app => Utils.NewPICSRequest(app.Key, app.Value));

            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(apps, Enumerable.Empty<SteamApps.PICSRequest>()));
        }

        private static void OnPICSTokens(SteamApps.PICSTokensCallback callback)
        {
            JobManager.TryRemoveJob(callback.JobID);

            Log.WriteDebug("PICSTokens", "Tokens granted: {0} - Tokens denied: {1}", callback.AppTokens.Count, callback.AppTokensDenied.Count);

            var apps = callback.AppTokensDenied
                .Select(Utils.NewPICSRequest)
                .Concat(callback.AppTokens.Select(app => Utils.NewPICSRequest(app.Key, app.Value)));

            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(apps, Enumerable.Empty<SteamApps.PICSRequest>()));
        }

        public static bool HasToken(uint id)
        {
            return SecretTokens.ContainsKey(id);
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

        public static bool HandleToken(uint id, ulong accessToken)
        {
            if (!SecretTokens.ContainsKey(id))
            {
                SecretTokens.Add(id, accessToken);

                IRC.Instance.SendOps($"{Colors.GREEN}[Tokens]{Colors.NORMAL} Added a new token that the bot got itself:{Colors.BLUE} {id} {Colors.NORMAL}({Steam.GetAppName(id)})");

                Log.WriteInfo("PICSTokens", "New token for appid {0}", id);

                using (var db = Database.Get())
                {
                    db.Execute("INSERT INTO `PICSTokens` (`AppID`, `Token`) VALUES(@AppID, @Token)",
                        new { AppID = id, Token = accessToken }
                    );
                }

                return true;
            }
            else if (SecretTokens[id] != accessToken)
            {
                IRC.Instance.SendOps($"{Colors.GREEN}[Tokens]{Colors.NORMAL} Bot got a token that mismatches the one in database: {SecretTokens[id]} != {accessToken}");
            }

            return false;
        }
    }
}
