/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
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
    class PICSChanges : SteamHandler
    {
        public uint PreviousChangeNumber { get; private set; }

        private readonly uint BillingTypeKey;

        private static readonly List<EBillingType> IgnorableBillingTypes;

        private const uint CHANGELIST_BURST_MIN = 50;
        private uint ChangelistBurstCount;
        private DateTime ChangelistBurstTime;

        static PICSChanges()
        {
            IgnorableBillingTypes = new List<EBillingType>
            {
                EBillingType.ProofOfPrepurchaseOnly, // CDKey
                EBillingType.GuestPass,
                EBillingType.HardwarePromo,
                EBillingType.Gift,
                EBillingType.AutoGrant,
                EBillingType.OEMTicket,
                EBillingType.RecurringOption, // Not sure if should be ignored
            };
        }

        public PICSChanges(CallbackManager manager)
            : base(manager)
        {
            if (Settings.IsFullRun)
            {
                var timer = new Timer
                {
                    Interval = TimeSpan.FromSeconds(10).TotalMilliseconds
                };
                timer.Elapsed += (sender, args) => IsBusy();
                timer.Start();

                return;
            }
                
            manager.Subscribe<SteamApps.PICSChangesCallback>(OnPICSChanges);

            using (var db = Database.Get())
            {
                BillingTypeKey = db.ExecuteScalar<uint>("SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = 'root_billingtype'");

                if (LocalConfig.Current.ChangeNumber == 0)
                {
                    PreviousChangeNumber = db.ExecuteScalar<uint>("SELECT `ChangeID` FROM `Changelists` ORDER BY `ChangeID` DESC LIMIT 1");
                    LocalConfig.Current.ChangeNumber = PreviousChangeNumber;
                }
                else
                {
                    PreviousChangeNumber = LocalConfig.Current.ChangeNumber;
                }
                
                Log.WriteInfo("PICSChanges", "Previous changelist was {0}", PreviousChangeNumber);
            }

            if (PreviousChangeNumber == 0)
            {
                Log.WriteWarn("PICSChanges", "Looks like there are no changelists in the database.");
                Log.WriteWarn("PICSChanges", "If you want to fill up your database first, restart with \"FullRun\" setting set to {0}.", (int)FullRunState.Enumerate);
            }
        }

        public void PerformSync()
        {
            PreviousChangeNumber = 2;

            List<uint> apps;
            List<uint> packages;

            using (var db = Database.Get())
            {
                if (Settings.Current.FullRun == FullRunState.Enumerate)
                {
                    var lastAppID = 50000 + db.ExecuteScalar<int>("SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC LIMIT 1");
                    var lastSubID = 10000 + db.ExecuteScalar<int>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC LIMIT 1");

                    // TODO: Tempo hack due to appid 2032727
                    if (lastAppID > 1000000)
                    {
                        lastAppID = 1000000;
                    }

                    Log.WriteInfo("Full Run", "Will enumerate {0} apps and {1} packages", lastAppID, lastSubID);

                    // greatest code you've ever seen
                    apps = Enumerable.Range(0, lastAppID).Select(i => (uint)i).ToList();
                    packages = Enumerable.Range(0, lastSubID).Select(i => (uint)i).ToList();
                }
                else
                {
                    Log.WriteInfo("Full Run", "Doing a full run on all apps and packages in the database.");

                    apps = db.Query<uint>("SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC").ToList();
                    packages = db.Query<uint>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC").ToList();
                }
            }

            TaskManager.RunAsync(() => RequestUpdateForList(apps, packages));
        }

        private bool IsBusy()
        {
            Log.WriteInfo("Full Run", "Jobs: {0} - Tasks: {1} - Processing: {2} - Depot locks: {3}",
                              JobManager.JobsCount,
                              TaskManager.TasksCount,
                              PICSProductInfo.CurrentlyProcessingCount,
                              Steam.Instance.DepotProcessor.DepotLocksCount);

            return TaskManager.TasksCount > 0
                   || JobManager.JobsCount > 0
                   || PICSProductInfo.CurrentlyProcessingCount > 50
                   || Steam.Instance.DepotProcessor.DepotLocksCount > 4;
        }

        private async void RequestUpdateForList(List<uint> appIDs, List<uint> packageIDs)
        {
            Log.WriteInfo("Full Run", "Requesting info for {0} apps and {1} packages", appIDs.Count, packageIDs.Count);

            const int size = 100;

            for (var i = 0; i < appIDs.Count; i += size)
            {
                var list = appIDs.GetRange(i, Math.Min(size, appIDs.Count - i));

                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(list, Enumerable.Empty<uint>()));

                do
                {
                    await Task.Delay(1000);
                }
                while (IsBusy());
            }

            if (Settings.Current.FullRun == FullRunState.WithForcedDepots)
            {
                return;
            }

            var packages = packageIDs.Select(Utils.NewPICSRequest).ToList();

            for (var i = 0; i < packages.Count; i += size)
            {
                var list = packages.GetRange(i, Math.Min(size, packages.Count - i));

                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), list));

                do
                {
                    await Task.Delay(1000);
                }
                while (IsBusy());
            }
        }

        private async void OnPICSChanges(SteamApps.PICSChangesCallback callback)
        {
            if (PreviousChangeNumber == callback.CurrentChangeNumber)
            {
                return;
            }

            var packageChangesCount = callback.PackageChanges.Count;
            var appChangesCount = callback.AppChanges.Count;

            Log.WriteInfo("PICSChanges", "Changelist {0} -> {1} ({2} apps, {3} packages)", PreviousChangeNumber, callback.CurrentChangeNumber, appChangesCount, packageChangesCount);

            LocalConfig.Current.ChangeNumber = PreviousChangeNumber = callback.CurrentChangeNumber;

            await HandleChangeNumbers(callback);

            if (appChangesCount == 0 && packageChangesCount == 0)
            {
                IRC.Instance.SendAnnounce("{0}»{1} Changelist {2}{3}{4} (empty)", Colors.RED, Colors.NORMAL, Colors.BLUE, PreviousChangeNumber, Colors.DARKGRAY);

                return;
            }

// We don't care about waiting for separate database queries to finish, so don't await them
#pragma warning disable 4014
            if (appChangesCount > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(callback.AppChanges.Keys, Enumerable.Empty<uint>()));

                TaskManager.RunAsync(async () => await HandleApps(callback));
            }

            if (packageChangesCount > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), callback.PackageChanges.Keys.Select(Utils.NewPICSRequest)));

                TaskManager.RunAsync(async () => await HandlePackages(callback));
                TaskManager.RunAsync(async () => await HandlePackagesChangelists(callback));
            }

            TaskManager.RunAsync(async () => await SendChangelistsToIRC(callback));
#pragma warning restore 4014

            PrintImportants(callback);
        }

        private async Task HandleApps(SteamApps.PICSChangesCallback callback)
        {
            await StoreQueue.AddAppToQueue(callback.AppChanges.Values.Select(x => x.ID));

            using (var db = await Database.GetConnectionAsync())
            {
                await db.ExecuteAsync("INSERT INTO `ChangelistsApps` (`ChangeID`, `AppID`) VALUES (@ChangeNumber, @ID) ON DUPLICATE KEY UPDATE `AppID` = `AppID`", callback.AppChanges.Values);
                await db.ExecuteAsync("UPDATE `Apps` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `AppID` IN @Ids", new { Ids = callback.AppChanges.Values.Select(x => x.ID) });
            }
        }

        private async Task HandlePackagesChangelists(SteamApps.PICSChangesCallback callback)
        {
            using (var db = await Database.GetConnectionAsync())
            {
                await db.ExecuteAsync("INSERT INTO `ChangelistsSubs` (`ChangeID`, `SubID`) VALUES (@ChangeNumber, @ID) ON DUPLICATE KEY UPDATE `SubID` = `SubID`", callback.PackageChanges.Values);
                await db.ExecuteAsync("UPDATE `Subs` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `SubID` IN @Ids", new { Ids = callback.PackageChanges.Values.Select(x => x.ID) });
            }
        }

        private async Task HandlePackages(SteamApps.PICSChangesCallback callback)
        {
            Dictionary<uint, byte> ignoredPackages;

            using (var db = await Database.GetConnectionAsync())
            {
                ignoredPackages = (await db.QueryAsync("SELECT `SubID`, `SubID` FROM `SubsInfo` WHERE `SubID` IN @Subs AND `Key` = @Key AND `Value` IN @Types",
                    new
                    {
                        Key = BillingTypeKey,
                        Subs = callback.PackageChanges.Values.Select(x => x.ID),
                        Types = IgnorableBillingTypes
                    }
                )).ToDictionary(x => (uint)x.SubID, x => (byte)1);
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

            if (!subids.Any())
            {
                return;
            }

            List<uint> appids;

            // Queue all the apps in the package as well
            using (var db = Database.Get())
            {
                appids = (await db.QueryAsync<uint>("SELECT `AppID` FROM `SubsApps` WHERE `SubID` IN @Ids AND `Type` = 'app'", new { Ids = subids })).ToList();
            }

            if (appids.Any())
            {
                await StoreQueue.AddAppToQueue(appids);
            }

            await StoreQueue.AddPackageToQueue(subids);
        }

        private async Task HandleChangeNumbers(SteamApps.PICSChangesCallback callback)
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

            using (var db = Database.Get())
            {
                await db.ExecuteAsync("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = `Date`", changeLists);
            }
        }

        private void PrintImportants(SteamApps.PICSChangesCallback callback)
        {
            // Apps
            var important = callback.AppChanges.Keys.Intersect(Application.ImportantApps.Keys);

            foreach (var app in important)
            {
                var appName = Steam.GetAppName(app, out var appType);

                IRC.Instance.AnnounceImportantAppUpdate(app, "{0} update: {1}{2}{3} -{4} {5}",
                    appType,
                    Colors.BLUE, appName, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetAppURL(app, "history"));
            }

            // Packages
            important = callback.PackageChanges.Keys.Intersect(Application.ImportantSubs.Keys);

            foreach (var package in important)
            {
                IRC.Instance.AnnounceImportantPackageUpdate(package, "Package update: {0}{1}{2} -{3} {4}", Colors.BLUE, Steam.GetPackageName(package), Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetPackageURL(package, "history"));
            }
        }

        private async Task SendChangelistsToIRC(SteamApps.PICSChangesCallback callback)
        {
            if (DateTime.Now > ChangelistBurstTime)
            {
                ChangelistBurstTime = DateTime.Now.AddMinutes(5);
                ChangelistBurstCount = 0;
            }

            // Group apps and package changes by changelist, this will seperate into individual changelists
            var appGrouping = callback.AppChanges.Values.GroupBy(a => a.ChangeNumber);
            var packageGrouping = callback.PackageChanges.Values.GroupBy(p => p.ChangeNumber);

            // Join apps and packages back together based on changelist number
            var changeLists = Utils.FullOuterJoin(appGrouping, packageGrouping, a => a.Key, p => p.Key, (a, p, key) => new
            {
                ChangeNumber = key,

                Apps = a.ToList(),
                Packages = p.ToList(),
            },
                                  new EmptyGrouping<uint, SteamApps.PICSChangesCallback.PICSChangeData>(),
                                  new EmptyGrouping<uint, SteamApps.PICSChangesCallback.PICSChangeData>())
                .OrderBy(c => c.ChangeNumber);

            foreach (var changeList in changeLists)
            {
                var appCount = changeList.Apps.Count;
                var packageCount = changeList.Packages.Count;

                var message = $"Changelist {Colors.BLUE}{changeList.ChangeNumber}{Colors.NORMAL} {Colors.DARKGRAY}({appCount:N0} apps and {packageCount:N0} packages)";

                var changesCount = appCount + packageCount;

                if (changesCount >= 50)
                {
                    IRC.Instance.SendMain($"Big {message}{Colors.DARKBLUE} {SteamDB.GetChangelistURL(changeList.ChangeNumber)}");
                }

                if (ChangelistBurstCount++ >= CHANGELIST_BURST_MIN || changesCount > 300)
                {
                    if (appCount > 0)
                    {
                        message += $" (Apps: {string.Join(", ", changeList.Apps.Select(x => x.ID))})";
                    }

                    if (packageCount > 0)
                    {
                        message += $" (Packages: {string.Join(", ", changeList.Packages.Select(x => x.ID))})";
                    }

                    IRC.Instance.SendAnnounce($"{Colors.RED}»{Colors.NORMAL} {message}");

                    continue;
                }

                IRC.Instance.SendAnnounce("{0}»{1} {2}", Colors.RED, Colors.NORMAL, message);

                if (appCount > 0)
                {
                    Dictionary<uint, App> apps;

                    using (var db = Database.Get())
                    {
                        apps = (await db.QueryAsync<App>("SELECT `AppID`, `Name`, `LastKnownName` FROM `Apps` WHERE `AppID` IN @Ids", new { Ids = changeList.Apps.Select(x => x.ID) })).ToDictionary(x => x.AppID, x => x);
                    }

                    foreach (var app in changeList.Apps)
                    {
                        apps.TryGetValue(app.ID, out var data);

                        IRC.Instance.SendAnnounce("  App: {0}{1}{2} - {3}{4}",
                            Colors.BLUE, app.ID, Colors.NORMAL,
                            Steam.FormatAppName(app.ID, data),
                            app.NeedsToken ? SteamDB.StringNeedToken : string.Empty
                        );
                    }
                }

                if (packageCount > 0)
                {
                    Dictionary<uint, Package> packages;
                    
                    using (var db = Database.Get())
                    {
                        packages = (await db.QueryAsync<Package>("SELECT `SubID`, `Name`, `LastKnownName` FROM `Subs` WHERE `SubID` IN @Ids", new { Ids = changeList.Packages.Select(x => x.ID) })).ToDictionary(x => x.SubID, x => x);
                    }

                    foreach (var package in changeList.Packages)
                    {
                        packages.TryGetValue(package.ID, out var data);

                        IRC.Instance.SendAnnounce("  Package: {0}{1}{2} - {3}{4}",
                            Colors.BLUE, package.ID, Colors.NORMAL,
                            Steam.FormatPackageName(package.ID, data),
                            package.NeedsToken ? SteamDB.StringNeedToken : string.Empty
                        );
                    }
                }
            }
        }
    }
}
