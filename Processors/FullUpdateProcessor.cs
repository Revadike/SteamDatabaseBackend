﻿/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class FullUpdateProcessor
    {
        private const int IdsPerMetadataRequest = 5000;

        public static async Task PerformSync()
        {
            Log.WriteInfo(nameof(Settings), $"Running full update with option \"{Settings.FullRun}\"");

            if (Settings.FullRun == FullRunState.NormalUsingMetadata)
            {
                await FullUpdateAppsMetadata();
                await FullUpdatePackagesMetadata();

                return;
            }
            else if (Settings.FullRun == FullRunState.Enumerate)
            {
                await FullUpdateEnumeration();

                return;
            }
            else if (Settings.FullRun == FullRunState.ImportantOnly)
            {
                await RequestUpdateForList(Application.ImportantApps.ToList(), Application.ImportantSubs.ToList(), true);

                return;
            }

            List<uint> apps;
            List<uint> packages;

            await using (var db = await Database.GetConnectionAsync())
            {
                if (Settings.FullRun == FullRunState.TokensOnly)
                {
                    apps = PICSTokens.AppTokens.Keys.ToList();
                    packages = PICSTokens.PackageTokens.Keys.ToList();
                }
                else
                {
                    if (Settings.FullRun == FullRunState.PackagesNormal)
                    {
                        apps = new List<uint>();
                    }
                    else
                    {
                        apps = (await db.QueryAsync<uint>("(SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC) UNION DISTINCT (SELECT `AppID` FROM `SubsApps` WHERE `Type` = 'app') ORDER BY `AppID` DESC")).ToList();

                        try
                        {
                            using var steamApps = Steam.Configuration.GetAsyncWebAPIInterface("ISteamApps");
                            var response = await steamApps.CallAsync(HttpMethod.Get, "GetAppList", 2);
                            var apiApps = response["apps"].Children.Select(app => app["appid"].AsUnsignedInteger()).ToList();

                            using var steamStore = Steam.Configuration.GetAsyncWebAPIInterface("IStoreService");
                            var lastAppId = 0u;
                            var storeApiApps = new List<uint>();

                            do
                            {
                                response = await steamStore.CallAsync(HttpMethod.Get, "GetAppList", 1, new Dictionary<string, object>
                                {
                                    { "last_appid", lastAppId },
                                    { "max_results", 50000 },
                                });

                                storeApiApps.AddRange(response["apps"].Children.Select(app => app["appid"].AsUnsignedInteger()).ToList());
                                lastAppId = response["last_appid"].AsUnsignedInteger();
                            }
                            while (response["have_more_results"].AsBoolean());

                            apps = apps
                                .Union(apiApps)
                                .Union(storeApiApps)
                                .OrderByDescending(x => x)
                                .ToList();
                        }
                        catch (Exception)
                        {
                            //
                        }
                    }

                    if (Settings.FullRun != FullRunState.WithForcedDepots)
                    {
                        packages = (await db.QueryAsync<uint>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC"))
                            .Union(LicenseList.OwnedSubs.Keys)
                            .OrderByDescending(x => x)
                            .ToList();
                    }
                    else
                    {
                        packages = new List<uint>();
                    }
                }
            }

            await RequestUpdateForList(apps, packages, true);
        }

        public static async Task FullUpdateAppsMetadata()
        {
            await using var db = await Database.GetConnectionAsync();
            var apps = db.Query<uint>("(SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC) UNION DISTINCT (SELECT `AppID` FROM `SubsApps` WHERE `Type` = 'app') ORDER BY `AppID` DESC").ToList();

            await RequestUpdateForList(apps, Enumerable.Empty<uint>().ToList());
        }

        public static async Task FullUpdatePackagesMetadata()
        {
            await using var db = await Database.GetConnectionAsync();
            var subs = db.Query<uint>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC").ToList();

            await RequestUpdateForList(Enumerable.Empty<uint>().ToList(), subs);
        }

        public static async Task HandleMetadataInfo(SteamApps.PICSProductInfoCallback callback)
        {
            var apps = new List<uint>();
            var subs = new List<uint>();
            await using var db = await Database.GetConnectionAsync();

            if (callback.Apps.Any())
            {
                Log.WriteDebug(nameof(FullUpdateProcessor), $"Received metadata only product info for {callback.Apps.Count} apps ({callback.Apps.First().Key}...{callback.Apps.Last().Key}), job: {callback.JobID}");

                var currentChangeNumbers = (await db.QueryAsync<(uint, uint)>(
                    "SELECT `AppID`, `Value` FROM `AppsInfo` WHERE `Key` = @ChangeNumberKey AND `AppID` IN @Apps",
                    new
                    {
                        ChangeNumberKey = KeyNameCache.GetAppKeyID("root_changenumber"),
                        Apps = callback.Apps.Keys
                    }
                )).ToDictionary(x => x.Item1, x => x.Item2);

                foreach (var app in callback.Apps.Values)
                {
                    currentChangeNumbers.TryGetValue(app.ID, out var currentChangeNumber);

                    if (currentChangeNumber == app.ChangeNumber)
                    {
                        continue;
                    }

                    Log.WriteInfo(nameof(FullUpdateProcessor), $"App {app.ID} - Change: {currentChangeNumber} -> {app.ChangeNumber}");
                    apps.Add(app.ID);

                    if (!Settings.IsFullRun)
                    {
                        await db.ExecuteAsync("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeNumber) ON DUPLICATE KEY UPDATE `Date` = `Date`", new { app.ChangeNumber });
                        await db.ExecuteAsync("INSERT INTO `ChangelistsApps` (`ChangeID`, `AppID`) VALUES (@ChangeNumber, @AppID) ON DUPLICATE KEY UPDATE `AppID` = `AppID`", new { AppID = app.ID, app.ChangeNumber });
                    }
                }
            }
            else if (callback.UnknownApps.Any())
            {
                Log.WriteDebug(nameof(FullUpdateProcessor), $"Received metadata only product info only for {callback.UnknownApps.Count} unknown apps ({callback.UnknownApps.First()}...{callback.UnknownApps.Last()}), job: {callback.JobID}");
            }

            if (callback.Packages.Any())
            {
                Log.WriteDebug(nameof(FullUpdateProcessor), $"Received metadata only product info for {callback.Packages.Count} packages ({callback.Packages.First().Key}...{callback.Packages.Last().Key}), job: {callback.JobID}");

                var currentChangeNumbers = (await db.QueryAsync<(uint, uint)>(
                    "SELECT `SubID`, `Value` FROM `SubsInfo` WHERE `Key` = @ChangeNumberKey AND `SubID` IN @Subs",
                    new
                    {
                        ChangeNumberKey = KeyNameCache.GetSubKeyID("root_changenumber"),
                        Subs = callback.Packages.Keys
                    }
                )).ToDictionary(x => x.Item1, x => x.Item2);

                foreach (var sub in callback.Packages.Values)
                {
                    currentChangeNumbers.TryGetValue(sub.ID, out var currentChangeNumber);

                    if (currentChangeNumber == sub.ChangeNumber)
                    {
                        continue;
                    }

                    Log.WriteInfo(nameof(FullUpdateProcessor), $"Package {sub.ID} - Change: {currentChangeNumber} -> {sub.ChangeNumber}");
                    subs.Add(sub.ID);

                    if (!Settings.IsFullRun)
                    {
                        await db.ExecuteAsync("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeNumber) ON DUPLICATE KEY UPDATE `Date` = `Date`", new { sub.ChangeNumber });
                        await db.ExecuteAsync("INSERT INTO `ChangelistsSubs` (`ChangeID`, `SubID`) VALUES (@ChangeNumber, @SubID) ON DUPLICATE KEY UPDATE `SubID` = `SubID`", new { SubID = sub.ID, sub.ChangeNumber });
                    }
                }
            }
            else if (callback.UnknownPackages.Any())
            {
                Log.WriteDebug(nameof(FullUpdateProcessor), $"Received metadata only product info only for {callback.UnknownPackages.Count} unknown packages ({callback.UnknownPackages.First()}...{callback.UnknownPackages.Last()}), job: {callback.JobID}");
            }

            if (apps.Any() || subs.Any())
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(apps, subs),
                    new PICSTokens.RequestedTokens
                    {
                        Apps = apps,
                        Packages = subs,
                    });
            }
        }

        public static bool IsBusy()
        {
            var jobs = JobManager.JobsCount;
            var tasks = TaskManager.TasksCount;
            var processes = PICSProductInfo.CurrentlyProcessingCount;
            var depots = Steam.Instance.DepotProcessor.DepotLocksCount;

            Console.Error.WriteLine($"[{nameof(FullUpdateProcessor)}] Jobs: {jobs} - Tasks: {tasks} - Processing: {processes} - Depot locks: {depots}");

            // 2 tasks when not full running = PICS ticker and full update task
            return tasks > 3 || jobs > 0 || processes > 50 || depots > 4;
        }

        private static async Task FullUpdateEnumeration()
        {
            await using var db = await Database.GetConnectionAsync();
            var lastAppId = 50000 + db.ExecuteScalar<int>("SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC LIMIT 1");
            var lastSubId = 10000 + db.ExecuteScalar<int>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC LIMIT 1");

            // greatest code you've ever seen
            var apps = Enumerable.Range(0, lastAppId).Reverse().Select(i => (uint)i).ToList();
            var subs = Enumerable.Range(0, lastSubId).Reverse().Select(i => (uint)i).ToList();

            await RequestUpdateForList(apps, subs);
        }

        private static async Task RequestUpdateForList(List<uint> apps, List<uint> packages, bool requestTokens = false)
        {
            Log.WriteInfo(nameof(FullUpdateProcessor), $"Requesting info for {apps.Count} apps and {packages.Count} packages");

            foreach (var list in apps.Split(requestTokens ? 100 : IdsPerMetadataRequest))
            {
                do
                {
                    try
                    {
                        if (requestTokens)
                        {
                            var job = Steam.Instance.Apps.PICSGetAccessTokens(list, Enumerable.Empty<uint>());
                            job.Timeout = TimeSpan.FromMinutes(2);
                            await job;
                        }
                        else
                        {
                            var job = Steam.Instance.Apps.PICSGetProductInfo(list.Select(PICSTokens.NewAppRequest), Enumerable.Empty<SteamApps.PICSRequest>(), true);
                            job.Timeout = TimeSpan.FromMinutes(2);
                            await job;
                        }

                        break;
                    }
                    catch (TaskCanceledException)
                    {
                        Log.WriteWarn(nameof(FullUpdateProcessor), $"Apps metadata request timed out");
                    }
                } while (true);

                do
                {
                    await Task.Delay(500);
                }
                while (IsBusy());
            }

            foreach (var list in packages.Split(requestTokens ? 200 : IdsPerMetadataRequest))
            {
                do
                {
                    try
                    {
                        if (requestTokens)
                        {
                            var job = Steam.Instance.Apps.PICSGetAccessTokens(Enumerable.Empty<uint>(), list);
                            job.Timeout = TimeSpan.FromMinutes(2);
                            await job;
                        }
                        else
                        {
                            var job = Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), list.Select(PICSTokens.NewPackageRequest), true);
                            job.Timeout = TimeSpan.FromMinutes(2);
                            await job;
                        }
                        
                        break;
                    }
                    catch (TaskCanceledException)
                    {
                        Log.WriteWarn(nameof(FullUpdateProcessor), $"Package metadata request timed out");
                    }
                } while (true);

                do
                {
                    await Task.Delay(500);
                }
                while (IsBusy());
            }
        }
    }
}
