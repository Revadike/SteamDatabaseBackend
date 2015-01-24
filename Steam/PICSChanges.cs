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
    class PICSChanges : SteamHandler
    {
        public uint PreviousChangeNumber { get; private set; }

        private readonly uint BillingTypeKey;

        private static readonly List<EBillingType> IgnorableBillingTypes;

        static PICSChanges()
        {
            IgnorableBillingTypes = new List<EBillingType>()
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
                PreviousChangeNumber = 1; // Request everything

                manager.Register(new Callback<SteamApps.PICSChangesCallback>(OnPICSChangesFullRun));

                return;
            }
                
            manager.Register(new Callback<SteamApps.PICSChangesCallback>(OnPICSChanges));

            BillingTypeKey = SubProcessor.GetKeyNameID("root_billingtype");

            using (var db = Database.GetConnection())
            {
                PreviousChangeNumber = db.ExecuteScalar<uint>("SELECT `ChangeID` FROM `Changelists` ORDER BY `ChangeID` DESC LIMIT 1");

                Log.WriteInfo("PICSChanges", "Previous changelist was {0}", PreviousChangeNumber);
            }

            if (PreviousChangeNumber == 0)
            {
                Log.WriteWarn("PICSChanges", "Looks like there are no changelists in the database.");
                Log.WriteWarn("PICSChanges", "If you want to fill up your database first, restart with \"FullRun\" setting set to 1.");
            }
        }

        private void OnPICSChangesFullRun(SteamApps.PICSChangesCallback callback)
        {
            PreviousChangeNumber = 2;

            Log.WriteInfo("PICSChanges", "Requesting info for {0} apps and {1} packages", callback.AppChanges.Count, callback.PackageChanges.Count);

            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), callback.PackageChanges.Keys.Select(package => Utils.NewPICSRequest(package))));
            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(callback.AppChanges.Keys, Enumerable.Empty<uint>()));
        }

        private void OnPICSChanges(SteamApps.PICSChangesCallback callback)
        {
            if (PreviousChangeNumber == callback.CurrentChangeNumber)
            {
                return;
            }

            var packageChangesCount = callback.PackageChanges.Count;
            var appChangesCount = callback.AppChanges.Count;

            Log.WriteInfo("PICSChanges", "Changelist {0} -> {1} ({2} apps, {3} packages)", PreviousChangeNumber, callback.CurrentChangeNumber, appChangesCount, packageChangesCount);

            PreviousChangeNumber = callback.CurrentChangeNumber;

            HandleChangeNumbers(callback);

            if (appChangesCount == 0 && packageChangesCount == 0)
            {
                IRC.Instance.SendAnnounce("{0}»{1} Changelist {2}{3}{4} (empty)", Colors.RED, Colors.NORMAL, Colors.BLUE, PreviousChangeNumber, Colors.DARKGRAY);

                return;
            }

            if (appChangesCount > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(callback.AppChanges.Keys, Enumerable.Empty<uint>()));

                TaskManager.Run(() => HandleApps(callback));
            }

            if (packageChangesCount > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), callback.PackageChanges.Keys.Select(package => Utils.NewPICSRequest(package))));

                TaskManager.Run(() => HandlePackages(callback));
                TaskManager.Run(() => HandlePackagesChangelists(callback));
            }

            TaskManager.Run(() => SendChangelistsToIRC(callback));

            PrintImportants(callback);
        }

        private void HandleApps(SteamApps.PICSChangesCallback callback)
        {
            StoreQueue.AddAppToQueue(callback.AppChanges.Values.Select(x => x.ID));

            using (var db = Database.GetConnection())
            {
                db.Execute("INSERT INTO `ChangelistsApps` (`ChangeID`, `AppID`) VALUES (@ChangeNumber, @ID) ON DUPLICATE KEY UPDATE `AppID` = `AppID`", callback.AppChanges.Values);

                db.Execute("UPDATE `Apps` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `AppID` IN @Ids", new { Ids = callback.AppChanges.Values.Select(x => x.ID) });
            }
        }

        private void HandlePackagesChangelists(SteamApps.PICSChangesCallback callback)
        {
            using (var db = Database.GetConnection())
            {
                db.Execute("INSERT INTO `ChangelistsSubs` (`ChangeID`, `SubID`) VALUES (@ChangeNumber, @ID) ON DUPLICATE KEY UPDATE `SubID` = `SubID`", callback.PackageChanges.Values);

                db.Execute("UPDATE `Subs` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `SubID` IN @Ids", new { Ids = callback.PackageChanges.Values.Select(x => x.ID) });
            }
        }

        private void HandlePackages(SteamApps.PICSChangesCallback callback)
        {
            var ignoredPackages = new Dictionary<uint, byte>();

            using (var db = Database.GetConnection())
            {
                ignoredPackages = db.Query("SELECT `SubID`, `SubID` FROM `SubsInfo` WHERE `SubID` IN @Subs AND `Key` = @Key AND `Value` IN @Types",
                    new
                    {
                        Key = BillingTypeKey,
                        Subs = callback.PackageChanges.Values.Select(x => x.ID),
                        Types = IgnorableBillingTypes
                    }
                ).ToDictionary(x => (uint)x.SubID, x => (byte)1);
            }

            // Steam comp
            if (!ignoredPackages.ContainsKey(0))
            {
                ignoredPackages.Add(0, (byte)1);
            }

            // Anon dedi comp
            if (!ignoredPackages.ContainsKey(17906))
            {
                ignoredPackages.Add(17906, (byte)1);
            }

            var subids = callback.PackageChanges.Values.Select(x => x.ID).Where(x => !ignoredPackages.ContainsKey(x));

            if (!subids.Any())
            {
                return;
            }

            List<uint> appids;

            // Queue all the apps in the package as well
            using (var db = Database.GetConnection())
            {
                appids = db.Query<uint>("SELECT `AppID` FROM `SubsApps` WHERE `SubID` IN @Ids AND `Type` = 'app'", new { Ids = subids }).ToList();
            }

            if (appids.Any())
            {
                StoreQueue.AddAppToQueue(appids);
            }

            StoreQueue.AddPackageToQueue(subids);
        }

        private void HandleChangeNumbers(SteamApps.PICSChangesCallback callback)
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

            using (var db = Database.GetConnection())
            {
                db.Execute("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = `Date`", changeLists);
            }
        }

        private void PrintImportants(SteamApps.PICSChangesCallback callback)
        {
            // Apps
            var important = callback.AppChanges.Keys.Intersect(Application.ImportantApps.Keys);
            string appType;
            string appName;

            foreach (var app in important)
            {
                appName = Steam.GetAppName(app, out appType);

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

        private void SendChangelistsToIRC(SteamApps.PICSChangesCallback callback)
        {
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

                string Message = string.Format("Changelist {0}{1}{2} {3}({4:N0} apps and {5:N0} packages){6} -{7} {8}",
                                     Colors.BLUE, changeList.ChangeNumber, Colors.NORMAL,
                                     Colors.DARKGRAY, appCount, packageCount, Colors.NORMAL,
                                     Colors.DARKBLUE, SteamDB.GetChangelistURL(changeList.ChangeNumber)
                                 );

                var changesCount = appCount + packageCount;

                if (changesCount >= 50)
                {
                    IRC.Instance.SendMain(Message);
                }

                IRC.Instance.SendAnnounce("{0}»{1} {2}", Colors.RED, Colors.NORMAL, Message);

                // If this changelist is very big, freenode will hate us forever if we decide to print all that stuff
                if (changesCount > 300)
                {
                    IRC.Instance.SendAnnounce("{0}  This changelist is too big to be printed in IRC, please view it online", Colors.RED);

                    continue;
                }
                    
                if (appCount > 0)
                {
                    Dictionary<uint, App> apps;
                    App data;

                    using (var db = Database.GetConnection())
                    {
                        apps = db.Query<App>("SELECT `AppID`, `Name`, `LastKnownName` FROM `Apps` WHERE `AppID` IN @Ids", new { Ids = changeList.Apps.Select(x => x.ID) }).ToDictionary(x => x.AppID, x => x);
                    }

                    foreach (var app in changeList.Apps)
                    {
                        apps.TryGetValue(app.ID, out data);

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
                    Package data;

                    using (var db = Database.GetConnection())
                    {
                        packages = db.Query<Package>("SELECT `SubID`, `Name`, `LastKnownName` FROM `Subs` WHERE `SubID` IN @Ids", new { Ids = changeList.Packages.Select(x => x.ID) }).ToDictionary(x => x.SubID, x => x);
                    }

                    foreach (var package in changeList.Packages)
                    {
                        packages.TryGetValue(package.ID, out data);

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
