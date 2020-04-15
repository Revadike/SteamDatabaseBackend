/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class PICSTokens : SteamHandler
    {
        private class PICSToken
        {
            public uint AppID { get; set; }
            public uint SubID { get; set; }
            public ulong Token { get; set; }
        }

        private static Dictionary<uint, ulong> AppTokens;
        private static Dictionary<uint, ulong> PackageTokens;

        public PICSTokens(CallbackManager manager)
        {
            manager.Subscribe<SteamApps.PICSTokensCallback>(OnPICSTokens);

            Reload();
        }

        public static void Reload(CommandArguments command)
        {
            Reload();

            command.Notice($"Reloaded {AppTokens.Count} app tokens and {PackageTokens.Count} package tokens");
        }

        private static void Reload()
        {
            var oldAppTokens = AppTokens;
            var oldSubTokens = PackageTokens;

            using (var db = Database.Get())
            {
                AppTokens = db.Query<PICSToken>("SELECT `AppID`, `Token` FROM `PICSTokens`").ToDictionary(x => x.AppID, x => x.Token);
                PackageTokens = db.Query<PICSToken>("SELECT `SubID`, `Token` FROM `PICSTokensSubs`").ToDictionary(x => x.SubID, x => x.Token);
            }

            var apps = Enumerable.Empty<SteamApps.PICSRequest>();
            var subs = Enumerable.Empty<SteamApps.PICSRequest>();

            if (oldAppTokens != null)
            {
                apps = AppTokens
                    .Where(x => !oldAppTokens.ContainsKey(x.Key))
                    .Select(app => NewAppRequest(app.Key, app.Value));
            }

            if (oldSubTokens != null)
            {
                subs = AppTokens
                    .Where(x => !oldSubTokens.ContainsKey(x.Key))
                    .Select(sub => NewPackageRequest(sub.Key, sub.Value));
            }

            if (apps.Any() || subs.Any())
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(apps, subs));
            }
        }

        private static void OnPICSTokens(SteamApps.PICSTokensCallback callback)
        {
            JobManager.TryRemoveJob(callback.JobID);

            Log.WriteDebug(nameof(PICSTokens), $"App tokens: {callback.AppTokens.Count} ({callback.AppTokensDenied.Count} denied) - Package tokens: {callback.PackageTokens.Count} ({callback.PackageTokensDenied.Count} denied)" );

            var apps = callback.AppTokensDenied
                .Select(NewAppRequest)
                .Concat(callback.AppTokens.Select(app => NewAppRequest(app.Key, app.Value)));

            var subs = callback.PackageTokensDenied
                .Select(NewPackageRequest)
                .Concat(callback.PackageTokens.Select(sub => NewPackageRequest(sub.Key, sub.Value)));

            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(apps, subs));
        }

        public static bool HasAppToken(uint id) => AppTokens.ContainsKey(id);

        public static bool HasPackageToken(uint id) => PackageTokens.ContainsKey(id);

        private static void HandleAppToken(uint id, ulong accessToken)
        {
            if (!AppTokens.ContainsKey(id))
            {
                AppTokens.Add(id, accessToken);

                IRC.Instance.SendOps($"{Colors.GREEN}[Tokens]{Colors.NORMAL} Added a new app token that the bot got itself:{Colors.BLUE} {id} {Colors.NORMAL}({Steam.GetAppName(id)})");

                Log.WriteInfo(nameof(PICSTokens), "New token for appid {0}", id);

                using var db = Database.Get();
                db.Execute("INSERT INTO `PICSTokens` (`AppID`, `Token`) VALUES(@AppID, @Token)",
                    new PICSToken { AppID = id, Token = accessToken }
                );
            }
            else if (AppTokens[id] != accessToken)
            {
                IRC.Instance.SendOps($"{Colors.GREEN}[Tokens]{Colors.NORMAL} Bot got an app token that mismatches the one in database: {AppTokens[id]} != {accessToken}");
            }
        }

        private static void HandlePackageToken(uint id, ulong accessToken)
        {
            if (!PackageTokens.ContainsKey(id))
            {
                PackageTokens.Add(id, accessToken);

                IRC.Instance.SendOps($"{Colors.GREEN}[Tokens]{Colors.NORMAL} Added a new package token that the bot got itself:{Colors.BLUE} {id} {Colors.NORMAL}({Steam.GetPackageName(id)})");

                Log.WriteInfo(nameof(PICSTokens), "New token for subid {0}", id);

                using var db = Database.Get();
                db.Execute("INSERT INTO `PICSTokensSubs` (`SubID`, `Token`) VALUES(@SubID, @Token)",
                    new PICSToken { SubID = id, Token = accessToken }
                );
            }
            else if (PackageTokens[id] != accessToken)
            {
                IRC.Instance.SendOps($"{Colors.GREEN}[Tokens]{Colors.NORMAL} Bot got a package token that mismatches the one in database: {PackageTokens[id]} != {accessToken}");
            }
        }

        public static SteamApps.PICSRequest NewAppRequest(uint id)
        {
            if (AppTokens.TryGetValue(id, out var token))
            {
                Log.WriteInfo(nameof(PICSTokens), "Using an overriden token for appid {0}", id);
            }

            return new SteamApps.PICSRequest(id, token, false);
        }

        public static SteamApps.PICSRequest NewPackageRequest(uint id)
        {
            if (PackageTokens.TryGetValue(id, out var token))
            {
                Log.WriteInfo(nameof(PICSTokens), "Using an overriden token for subid {0}", id);
            }

            return new SteamApps.PICSRequest(id, token, false);
        }

        public static SteamApps.PICSRequest NewAppRequest(uint id, ulong accessToken)
        {
            if (accessToken > 0)
            {
                HandleAppToken(id, accessToken);
            }

            return new SteamApps.PICSRequest(id, accessToken, false);
        }

        public static SteamApps.PICSRequest NewPackageRequest(uint id, ulong accessToken)
        {
            if (accessToken > 0)
            {
                HandlePackageToken(id, accessToken);
            }

            return new SteamApps.PICSRequest(id, accessToken, false);
        }
    }
}
