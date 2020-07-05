/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
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
            if (Settings.Current.FullRun == FullRunState.NormalUsingMetadata)
            {
                await FullUpdateAppsMetadata();
                await FullUpdatePackagesMetadata();

                return;
            }
            else if (Settings.Current.FullRun == FullRunState.Enumerate)
            {
                await FullUpdateEnumeration();

                return;
            }

            List<uint> apps;
            List<uint> packages;

            await using (var db = await Database.GetConnectionAsync())
            {
                if (Settings.Current.FullRun == FullRunState.TokensOnly)
                {
                    Log.WriteInfo(nameof(FullUpdateProcessor), $"Enumerating {PICSTokens.AppTokens.Count} apps and {PICSTokens.PackageTokens.Count} packages that have a token.");

                    apps = PICSTokens.AppTokens.Keys.ToList();
                    packages = PICSTokens.PackageTokens.Keys.ToList();
                }
                else
                {
                    Log.WriteInfo(nameof(FullUpdateProcessor), "Doing a full update on all apps and packages in the database.");

                    if (Settings.Current.FullRun == FullRunState.PackagesNormal)
                    {
                        apps = new List<uint>();
                    }
                    else
                    {
                        apps = (await db.QueryAsync<uint>("(SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC) UNION DISTINCT (SELECT `AppID` FROM `SubsApps` WHERE `Type` = 'app') ORDER BY `AppID` DESC")).ToList();
                    }

                    packages = (await db.QueryAsync<uint>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC")).ToList();
                }
            }

            await RequestUpdateForList(apps, packages);
        }

        private static async Task RequestUpdateForList(List<uint> appIDs, List<uint> packageIDs)
        {
            Log.WriteInfo(nameof(FullUpdateProcessor), "Requesting info for {0} apps and {1} packages", appIDs.Count, packageIDs.Count);

            foreach (var list in appIDs.Split(200))
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(list, Enumerable.Empty<uint>()),
                    new PICSTokens.RequestedTokens
                    {
                        Apps = list.ToList()
                    });

                do
                {
                    await Task.Delay(100);
                }
                while (IsBusy());
            }

            if (Settings.Current.FullRun == FullRunState.WithForcedDepots)
            {
                return;
            }

            foreach (var list in packageIDs.Split(1000))
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(Enumerable.Empty<uint>(), list),
                    new PICSTokens.RequestedTokens
                    {
                        Packages = list.ToList()
                    });

                do
                {
                    await Task.Delay(100);
                }
                while (IsBusy());
            }

            LocalConfig.Save();
        }

        public static async Task FullUpdateAppsMetadata()
        {
            Log.WriteInfo(nameof(FullUpdateProcessor), "Doing a full update for apps using metadata requests");

            var db = await Database.GetConnectionAsync();
            var apps = db.Query<uint>("(SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC) UNION DISTINCT (SELECT `AppID` FROM `SubsApps` WHERE `Type` = 'app') ORDER BY `AppID` DESC").ToList();

            foreach (var list in apps.Split(IdsPerMetadataRequest))
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(list.Select(PICSTokens.NewAppRequest), Enumerable.Empty<SteamApps.PICSRequest>(), true));

                do
                {
                    await Task.Delay(500);
                }
                while (IsBusy());
            }
        }

        public static async Task FullUpdatePackagesMetadata()
        {
            Log.WriteInfo(nameof(FullUpdateProcessor), "Doing a full update for packages using metadata requests");

            var db = await Database.GetConnectionAsync();
            var subs = db.Query<uint>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC").ToList();

            foreach (var list in subs.Split(IdsPerMetadataRequest))
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), list.Select(PICSTokens.NewPackageRequest), true));

                do
                {
                    await Task.Delay(500);
                }
                while (IsBusy());
            }
        }

        public static async Task HandleMetadataInfo(SteamApps.PICSProductInfoCallback callback)
        {
            var apps = new List<uint>();
            var subs = new List<uint>();
            var db = await Database.GetConnectionAsync();

            if (callback.Apps.Any())
            {
                Log.WriteDebug(nameof(FullUpdateProcessor), $"Received metadata only product info for {callback.Apps.Count} apps ({callback.Apps.First().Key}...{callback.Apps.Last().Key})");

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

            if (callback.Packages.Any())
            {
                Log.WriteDebug(nameof(FullUpdateProcessor), $"Received metadata only product info for {callback.Packages.Count} packages ({callback.Packages.First().Key}...{callback.Packages.Last().Key})");

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

            return tasks > 1 || jobs > 0 || processes > 50 || depots > 4;
        }

        private static async Task FullUpdateEnumeration()
        {
            var db = await Database.GetConnectionAsync();
            var lastAppId = 50000 + db.ExecuteScalar<int>("SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC LIMIT 1");
            var lastSubId = 10000 + db.ExecuteScalar<int>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC LIMIT 1");

            Log.WriteInfo(nameof(FullUpdateProcessor), "Will enumerate {0} apps and {1} packages", lastAppId, lastSubId);

            // greatest code you've ever seen
            var apps = Enumerable.Range(0, lastAppId).Reverse().Select(i => (uint)i);
            var subs = Enumerable.Range(0, lastSubId).Reverse().Select(i => (uint)i);

            foreach (var list in apps.Split(IdsPerMetadataRequest))
            {
                Log.WriteDebug(nameof(FullUpdateProcessor), $"Requesting app range: {list.First()}-{list.Last()}");

                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(list.Select(PICSTokens.NewAppRequest), Enumerable.Empty<SteamApps.PICSRequest>(), true));

                do
                {
                    await Task.Delay(500);
                }
                while (IsBusy());
            }

            foreach (var list in subs.Split(IdsPerMetadataRequest))
            {
                Log.WriteDebug(nameof(FullUpdateProcessor), $"Requesting package range: {list.First()}-{list.Last()}");

                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), list.Select(PICSTokens.NewPackageRequest), true));

                do
                {
                    await Task.Delay(500);
                }
                while (IsBusy());
            }
        }
    }
}
