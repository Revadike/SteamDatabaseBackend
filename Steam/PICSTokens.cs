/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class PICSTokens : SteamHandler
    {
        public class RequestedTokens
        {
            public List<uint> Apps;
            public List<uint> Packages;
        }

        private class PICSToken
        {
            public uint AppID { get; set; }
            public uint SubID { get; set; }
            public ulong Token { get; set; }
        }

        public static Dictionary<uint, ulong> AppTokens { get; private set; }
        public static Dictionary<uint, ulong> PackageTokens { get; private set; }

        public PICSTokens(CallbackManager manager)
        {
            manager.Subscribe<SteamApps.PICSTokensCallback>(OnPICSTokens);
        }

        public static async Task Reload()
        {
            var oldAppTokens = AppTokens;
            var oldSubTokens = PackageTokens;

            await using var db = await Database.GetConnectionAsync();
            AppTokens = (await db.QueryAsync<PICSToken>("SELECT `AppID`, `Token` FROM `PICSTokens`")).ToDictionary(x => x.AppID, x => x.Token);
            PackageTokens = (await db.QueryAsync<PICSToken>("SELECT `SubID`, `Token` FROM `PICSTokensSubs`")).ToDictionary(x => x.SubID, x => x.Token);

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
                subs = PackageTokens
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
            JobManager.TryRemoveJob(callback.JobID, out var job);

            if (callback.AppTokens.Count > 0 || callback.AppTokensDenied.Count > 0)
            {
                Log.WriteInfo(nameof(PICSTokens), $"App tokens: {callback.AppTokens.Count} received, {callback.AppTokensDenied.Count} denied");
            }
            else
            {
                Log.WriteInfo(nameof(PICSTokens), $"Package tokens: {callback.PackageTokens.Count} received, {callback.PackageTokensDenied.Count} denied");
            }

            var apps = callback.AppTokensDenied
                .Select(NewAppRequest)
                .Concat(callback.AppTokens.Select(app => NewAppRequest(app.Key, app.Value)))
                .ToList();

            var subs = callback.PackageTokensDenied
                .Select(NewPackageRequest)
                .Concat(callback.PackageTokens.Select(sub => NewPackageRequest(sub.Key, sub.Value)))
                .ToList();

            if (job?.Metadata != default)
            {
                var requested = (RequestedTokens)job.Metadata;

                if (requested.Apps != null)
                {
                    foreach (var appid in requested.Apps.Where(app =>
                        !callback.AppTokens.ContainsKey(app) && !callback.AppTokensDenied.Contains(app)))
                    {
                        Log.WriteError(nameof(PICSTokens), $"Requested token for app {appid} but Steam did not return it");
                        IRC.Instance.SendOps($"[TOKENS] Requested token for app {appid} but Steam did not return it");

                        apps.Add(NewAppRequest(appid));
                    }
                }

                if (requested.Packages != null)
                {
                    foreach (var subid in requested.Packages.Where(sub =>
                        !callback.PackageTokens.ContainsKey(sub) && !callback.PackageTokensDenied.Contains(sub)))
                    {
                        Log.WriteError(nameof(PICSTokens), $"Requested token for package {subid} but Steam did not return it");

                        subs.Add(NewPackageRequest(subid));
                    }
                }
            }

            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(apps, subs));
        }

        public static bool HasAppToken(uint id) => AppTokens.ContainsKey(id);

        public static bool HasPackageToken(uint id) => PackageTokens.ContainsKey(id);

        private static void HandleAppToken(uint id, ulong accessToken)
        {
            if (!AppTokens.TryGetValue(id, out var existingToken))
            {
                AppTokens.Add(id, accessToken);

                Log.WriteInfo(nameof(PICSTokens), $"New token for appid {id}");

                using var db = Database.Get();
                db.Execute("INSERT INTO `PICSTokens` (`AppID`, `Token`) VALUES(@AppID, @Token)",
                    new PICSToken { AppID = id, Token = accessToken }
                );
            }
            else if (existingToken != accessToken)
            {
                Log.WriteWarn(nameof(PICSTokens), $"New token for appid {id} that mismatches the existing one ({existingToken} != {accessToken})");

                IRC.Instance.SendOps($"{Colors.RED}[Tokens] Bot got an app token that mismatches the one in database:{Colors.BLUE} {id} {Colors.NORMAL}({existingToken} != {accessToken})");

                AppTokens[id] = accessToken;

                using var db = Database.Get();
                db.Execute("UPDATE `PICSTokens` SET `Token` = @Token, `Date` = NOW() WHERE `AppID` = @AppID",
                    new PICSToken { AppID = id, Token = accessToken }
                );
            }
        }

        private static void HandlePackageToken(uint id, ulong accessToken)
        {
            if (!PackageTokens.TryGetValue(id, out var existingToken))
            {
                PackageTokens.Add(id, accessToken);

                Log.WriteInfo(nameof(PICSTokens), $"New token for subid {id}");

                using var db = Database.Get();
                db.Execute("INSERT INTO `PICSTokensSubs` (`SubID`, `Token`) VALUES(@SubID, @Token)",
                    new PICSToken { SubID = id, Token = accessToken }
                );
            }
            else if (existingToken != accessToken)
            {
                Log.WriteWarn(nameof(PICSTokens), $"New token for subid {id} that mismatches the existing one ({existingToken} != {accessToken})");

                IRC.Instance.SendOps($"{Colors.RED}[Tokens] Bot got a package token that mismatches the one in database:{Colors.BLUE} {id} {Colors.NORMAL}({existingToken} != {accessToken})");

                PackageTokens[id] = accessToken;

                using var db = Database.Get();
                db.Execute("UPDATE `PICSTokensSubs` SET `Token` = @Token, `Date` = NOW() WHERE `SubID` = @SubID",
                    new PICSToken { SubID = id, Token = accessToken }
                );
            }
        }

        public static SteamApps.PICSRequest NewAppRequest(uint id)
        {
            AppTokens.TryGetValue(id, out var token);
            return new SteamApps.PICSRequest(id, token, false);
        }

        public static SteamApps.PICSRequest NewPackageRequest(uint id)
        {
            PackageTokens.TryGetValue(id, out var token);
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
