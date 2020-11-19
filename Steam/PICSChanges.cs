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
using Timer = System.Timers.Timer;

namespace SteamDatabaseBackend
{
    internal class PICSChanges : SteamHandler
    {
        public uint PreviousChangeNumber { get; set; }
        private uint LastStoredChangeNumber;
        private uint TickerHash;
        private readonly uint BillingTypeKey;

        private static readonly List<EBillingType> IgnorableBillingTypes = new List<EBillingType>
        {
            EBillingType.ProofOfPrepurchaseOnly, // CDKey
            EBillingType.GuestPass,
            EBillingType.HardwarePromo,
            EBillingType.Gift,
            EBillingType.AutoGrant,
            EBillingType.OEMTicket,
            EBillingType.RecurringOption, // Not sure if should be ignored
        };

        public PICSChanges(CallbackManager manager)
        {
            if (Settings.IsFullRun)
            {
                var timer = new Timer
                {
                    Interval = TimeSpan.FromSeconds(10).TotalMilliseconds
                };
                timer.Elapsed += (sender, args) => FullUpdateProcessor.IsBusy();
                timer.Start();

                return;
            }

            manager.Subscribe<SteamApps.PICSChangesCallback>(OnPICSChanges);

            using (var db = Database.Get())
            {
                BillingTypeKey = KeyNameCache.GetSubKeyID("root_billingtype");

                PreviousChangeNumber = db.ExecuteScalar<uint>("SELECT `Value` FROM `LocalConfig` WHERE `ConfigKey` = 'backend.changenumber'");

                if (PreviousChangeNumber == 0)
                {
                    PreviousChangeNumber = db.ExecuteScalar<uint>("SELECT `ChangeID` FROM `Changelists` ORDER BY `ChangeID` DESC LIMIT 1");
                }

                Log.WriteInfo(nameof(PICSChanges), $"Previous changelist was {PreviousChangeNumber}");
            }

            if (PreviousChangeNumber == 0)
            {
                Log.WriteWarn(nameof(PICSChanges), "Looks like there are no changelists in the database.");
                Log.WriteWarn(nameof(PICSChanges), $"If you want to fill up your database first, restart with \"FullRun\" setting set to {(int)FullRunState.Normal}.");
            }

            LastStoredChangeNumber = PreviousChangeNumber;
        }

        public void StartTick()
        {
            TickerHash++;
            TaskManager.Run(Tick);
        }

        public void StopTick()
        {
            TickerHash++;
        }

        private async Task Tick()
        {
            var currentHash = TickerHash;

            Log.WriteDebug(nameof(PICSChanges), $"Thread started #{currentHash}");

            while (currentHash == TickerHash)
            {
                try
                {
                    await Steam.Instance.Apps.PICSGetChangesSince(PreviousChangeNumber, true, true);
                }
                catch (OperationCanceledException)
                {
                    Log.WriteError(nameof(PICSChanges), "PICSGetChangesSince task was cancelled");
                }
                catch (AsyncJobFailedException)
                {
                    Log.WriteError(nameof(PICSChanges), "PICSGetChangesSince async job failed");
                }

#if !DEBUG
                if (!Settings.IsMillhaven)
                {
#endif
                    await Task.Delay(10000);
#if !DEBUG
                }
#endif
            }

            Log.WriteDebug(nameof(PICSChanges), $"Thread stopped #{currentHash}");
        }

        private void OnPICSChanges(SteamApps.PICSChangesCallback callback)
        {
            if (PreviousChangeNumber == callback.CurrentChangeNumber)
            {
                return;
            }

            Log.WriteInfo(nameof(PICSChanges), $"Changelist {callback.LastChangeNumber} -> {callback.CurrentChangeNumber} ({callback.AppChanges.Count} apps, {callback.PackageChanges.Count} packages)");

            PreviousChangeNumber = callback.CurrentChangeNumber;

            TaskManager.Run(async () => await HandleChangeNumbers(callback));

            if (callback.RequiresFullAppUpdate || callback.RequiresFullPackageUpdate)
            {
                TaskManager.Run(async () =>
                {
                    if (callback.RequiresFullAppUpdate)
                    {
                        IRC.Instance.SendOps($"Changelist {callback.CurrentChangeNumber} has forced a full app update");

                        // When full update flag is set, presumably Steam client start hammering the servers
                        // and the PICS service just does not return any data for a while until it clears up
                        await FullUpdateProcessor.FullUpdateAppsMetadata(true);
                    }

                    if (callback.RequiresFullPackageUpdate)
                    {
                        IRC.Instance.SendOps($"Changelist {callback.CurrentChangeNumber} has forced a full package update");

                        await FullUpdateProcessor.FullUpdatePackagesMetadata();
                    }

                    IRC.Instance.SendOps($"Changelist {callback.CurrentChangeNumber} full update has finished");
                });
            }

            if (callback.AppChanges.Count == 0 && callback.PackageChanges.Count == 0)
            {
                return;
            }

            const int appsPerJob = 50;

            if (callback.AppChanges.Count > appsPerJob)
            {
                foreach (var list in callback.AppChanges.Keys.Split(appsPerJob))
                {
                    JobManager.AddJob(
                        () => Steam.Instance.Apps.PICSGetAccessTokens(list, Enumerable.Empty<uint>()),
                        new PICSTokens.RequestedTokens
                        {
                            Apps = list.ToList()
                        });
                }
            }
            else if (callback.AppChanges.Count > 0)
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(callback.AppChanges.Keys, Enumerable.Empty<uint>()),
                    new PICSTokens.RequestedTokens
                    {
                        Apps = callback.AppChanges.Keys.ToList()
                    });
            }

            if (callback.PackageChanges.Count > appsPerJob)
            {
                foreach (var list in callback.PackageChanges.Keys.Split(appsPerJob))
                {
                    JobManager.AddJob(
                        () => Steam.Instance.Apps.PICSGetAccessTokens(Enumerable.Empty<uint>(), list),
                        new PICSTokens.RequestedTokens
                        {
                            Packages = list.ToList()
                        });
                }
            }
            else if (callback.PackageChanges.Count > 0)
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(Enumerable.Empty<uint>(), callback.PackageChanges.Keys),
                    new PICSTokens.RequestedTokens
                    {
                        Packages = callback.PackageChanges.Keys.ToList()
                    });
            }

            if (callback.AppChanges.Count > 0)
            {
                _ = TaskManager.Run(async () => await HandleApps(callback));
            }

            if (callback.PackageChanges.Count > 0)
            {
                _ = TaskManager.Run(async () => await HandlePackages(callback));
                _ = TaskManager.Run(async () => await HandlePackagesChangelists(callback));
            }

            if (PreviousChangeNumber - LastStoredChangeNumber >= 1000)
            {
                LastStoredChangeNumber = PreviousChangeNumber;

                _ = TaskManager.Run(async () => await LocalConfig.Update("backend.changenumber", LastStoredChangeNumber.ToString()));
            }

            PrintImportants(callback);
        }

        private static async Task HandleApps(SteamApps.PICSChangesCallback callback)
        {
            await StoreQueue.AddAppToQueue(callback.AppChanges.Values.Select(x => x.ID));

            await using var db = await Database.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `ChangelistsApps` (`ChangeID`, `AppID`) VALUES (@ChangeNumber, @ID) ON DUPLICATE KEY UPDATE `AppID` = `AppID`", callback.AppChanges.Values);
            await db.ExecuteAsync("UPDATE `Apps` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `AppID` IN @Ids", new { Ids = callback.AppChanges.Values.Select(x => x.ID) });
        }

        private static async Task HandlePackagesChangelists(SteamApps.PICSChangesCallback callback)
        {
            await using var db = await Database.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `ChangelistsSubs` (`ChangeID`, `SubID`) VALUES (@ChangeNumber, @ID) ON DUPLICATE KEY UPDATE `SubID` = `SubID`", callback.PackageChanges.Values);
            await db.ExecuteAsync("UPDATE `Subs` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `SubID` IN @Ids", new { Ids = callback.PackageChanges.Values.Select(x => x.ID) });
        }

        private async Task HandlePackages(SteamApps.PICSChangesCallback callback)
        {
            Dictionary<uint, byte> ignoredPackages;

            await using (var db = await Database.GetConnectionAsync())
            {
                ignoredPackages = (await db.QueryAsync("SELECT `SubID`, `SubID` FROM `SubsInfo` WHERE `SubID` IN @Subs AND `Key` = @Key AND `Value` IN @Types",
                    new
                    {
                        Key = BillingTypeKey,
                        Subs = callback.PackageChanges.Values.Select(x => x.ID),
                        Types = IgnorableBillingTypes
                    }
                )).ToDictionary(x => (uint)x.SubID, _ => (byte)1);
            }

            // Steam comp
            if (!ignoredPackages.ContainsKey(0))
            {
                ignoredPackages.Add(0, 1);
            }

            // Anon dedi comp
            if (!ignoredPackages.ContainsKey(17906))
            {
                ignoredPackages.Add(17906, 1);
            }

            var subids = callback.PackageChanges.Values
                .Select(x => x.ID).Where(x => !ignoredPackages.ContainsKey(x))
                .ToList();

            if (subids.Count == 0)
            {
                return;
            }

            List<uint> appids;

            // Queue all the apps in the package as well
            await using (var db = await Database.GetConnectionAsync())
            {
                appids = (await db.QueryAsync<uint>("SELECT `AppID` FROM `SubsApps` WHERE `SubID` IN @Ids AND `Type` = 'app'", new { Ids = subids })).ToList();
            }

            if (appids.Count > 0)
            {
                await StoreQueue.AddAppToQueue(appids);
            }

            await StoreQueue.AddPackageToQueue(subids);
        }

        private static async Task HandleChangeNumbers(SteamApps.PICSChangesCallback callback)
        {
            var changeNumbers = callback.AppChanges.Values
                .Select(x => x.ChangeNumber)
                .Union(callback.PackageChanges.Values.Select(x => x.ChangeNumber))
                .Distinct()
                .Where(x => x != callback.CurrentChangeNumber)
                .ToList();

            changeNumbers.Add(callback.CurrentChangeNumber);

            // Silly thing
            var changeLists = changeNumbers.Select(x => new Changelist { ChangeID = x });

            await using var db = await Database.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = `Date`", changeLists);
        }

        private static void PrintImportants(SteamApps.PICSChangesCallback callback)
        {
            // Apps
            var important = callback.AppChanges.Keys.Intersect(Application.ImportantApps);

            foreach (var app in important)
            {
                var changeNumber = callback.AppChanges[app].ChangeNumber;

                TaskManager.Run(async () => await Utils.SendWebhook(new
                {
                    Type = "ImportantAppUpdate",
                    AppID = app,
                    ChangeNumber = changeNumber,
                    Url = $"{SteamDB.GetAppUrl(app, "history")}?changeid={changeNumber}",
                }));

                var appName = Steam.GetAppName(app, out var appType);
                IRC.Instance.SendAnnounce($"{appType} update: {Colors.BLUE}{appName}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetAppUrl(app, "history")}");
            }

            // Packages
            important = callback.PackageChanges.Keys.Intersect(Application.ImportantSubs);

            foreach (var package in important)
            {
                var changeNumber = callback.PackageChanges[package].ChangeNumber;

                TaskManager.Run(async () => await Utils.SendWebhook(new
                {
                    Type = "ImportantSubUpdate",
                    SubID = package,
                    ChangeNumber = changeNumber,
                    Url = $"{SteamDB.GetPackageUrl(package, "history")}?changeid={changeNumber}",
                }));

                var subName = Steam.GetPackageName(package);
                IRC.Instance.SendAnnounce($"Package update: {Colors.BLUE}{subName}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetPackageUrl(package, "history")}");
            }
        }
    }
}
