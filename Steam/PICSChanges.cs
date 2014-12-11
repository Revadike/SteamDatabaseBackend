/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSChanges : SteamHandler
    {
        public uint PreviousChangeNumber { get; private set; }

        private static readonly string IgnorableBillingTypes;

        static PICSChanges()
        {
            var ignorableBillingTypes = new List<EBillingType>()
            {
                EBillingType.ProofOfPrepurchaseOnly, // CDKey
                EBillingType.GuestPass,
                EBillingType.HardwarePromo,
                EBillingType.Gift,
                EBillingType.AutoGrant,
                EBillingType.OEMTicket,
                EBillingType.RecurringOption, // Not sure if should be ignored
            };

            IgnorableBillingTypes = string.Join(",", ignorableBillingTypes.Select(@enum => (int)@enum));
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

            using (var reader = DbWorker.ExecuteReader("SELECT `ChangeID` FROM `Changelists` ORDER BY `ChangeID` DESC LIMIT 1"))
            {
                if (reader.Read())
                {
                    PreviousChangeNumber = reader.GetUInt32("ChangeID");

                    Log.WriteInfo("PICSChanges", "Previous changelist was {0}", PreviousChangeNumber);
                }
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

            DbWorker.ExecuteNonQuery("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = CURRENT_TIMESTAMP()", new MySqlParameter("@ChangeID", callback.CurrentChangeNumber));

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
            }

            TaskManager.Run(() => SendChangelistsToIRC(callback));

            PrintImportants(callback);
        }

        private static void HandleApps(SteamApps.PICSChangesCallback callback)
        {
            string changes = string.Empty;

            foreach (var app in callback.AppChanges.Values)
            {
                if (callback.CurrentChangeNumber != app.ChangeNumber)
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = `Date`", new MySqlParameter("@ChangeID", app.ChangeNumber));
                }

                changes += string.Format("({0}, {1}),", app.ChangeNumber, app.ID);
            }

            if (!changes.Equals(string.Empty))
            {
                changes = string.Format("INSERT INTO `ChangelistsApps` (`ChangeID`, `AppID`) VALUES {0} ON DUPLICATE KEY UPDATE `AppID` = `AppID`", changes.Remove(changes.Length - 1));

                DbWorker.ExecuteNonQuery(changes);
            }

            StoreQueue.AddAppToQueue(callback.AppChanges.Values.Select(x => x.ID));

            Parallel.ForEach(callback.AppChanges.Values, app =>
            {
                DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `AppID` = @AppID", new MySqlParameter("@AppID", app.ID));
            });
        }

        private static void HandlePackages(SteamApps.PICSChangesCallback callback)
        {
            string changes = string.Empty;
            uint count = 0;

            foreach (var package in callback.PackageChanges.Values)
            {
                if (callback.CurrentChangeNumber != package.ChangeNumber)
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = `Date`", new MySqlParameter("@ChangeID", package.ChangeNumber));
                }

                changes += string.Format("({0}, {1}),", package.ChangeNumber, package.ID);

                if (++count > 500)
                {
                    InsertPackagesToChangelist(changes);

                    changes = string.Empty;
                    count = 0;
                }
            }

            if (!changes.Equals(string.Empty))
            {
                InsertPackagesToChangelist(changes);
            }

            var ignoredPackages = new Dictionary<uint, byte>();

            using (var reader = DbWorker.ExecuteReader(string.Format("SELECT `SubID`, `SubID` FROM `SubsInfo` WHERE `SubID` IN({0}) AND `Key` = (SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = 'root_billingtype') AND `Value` IN ({1})",
                string.Join(",", callback.PackageChanges.Values.Select(x => x.ID)),
                IgnorableBillingTypes)))
            {
                while (reader.Read())
                {
                    ignoredPackages.Add(reader.GetUInt32("SubID"), (byte)1);
                }
            }

            var subids = new List<uint>();
            var appids = new List<uint>();

            Parallel.ForEach(callback.PackageChanges.Values, package =>
            {
                DbWorker.ExecuteNonQuery("UPDATE `Subs` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `SubID` = @SubID", new MySqlParameter("@SubID", package.ID));

                if (package.ID == 0 || ignoredPackages.ContainsKey(package.ID))
                {
                    Log.WriteDebug("PICSChanges Store Queue", "Ignoring sub {0}", package.ID);

                    return;
                }

                subids.Add(package.ID);

                if (subids.Count() > 500)
                {
                    StoreQueue.AddPackageToQueue(subids);

                    subids = new List<uint>();
                }

                // Queue all the apps in the package as well
                using (var reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `SubsApps` WHERE `SubID` = @SubID AND `Type` = 'app'", new MySqlParameter("@SubID", package.ID)))
                {
                    while (reader.Read())
                    {
                        appids.Add(reader.GetUInt32("AppID"));
                    }
                }

                if (appids.Count() > 500)
                {
                    StoreQueue.AddAppToQueue(appids);

                    appids = new List<uint>();
                }
            });

            if (appids.Any())
            {
                StoreQueue.AddAppToQueue(appids);
            }

            if (subids.Any())
            {
                StoreQueue.AddPackageToQueue(subids);
            }
        }

        private static void PrintImportants(SteamApps.PICSChangesCallback callback)
        {
            // Apps
            var important = callback.AppChanges.Keys.Intersect(Application.ImportantApps.Keys);

            foreach (var app in important)
            {
                IRC.Instance.AnnounceImportantAppUpdate(app, "Important app update: {0}{1}{2} -{3} {4}", Colors.BLUE, Steam.GetAppName(app), Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetAppURL(app, "history"));
            }

            // Packages
            important = callback.PackageChanges.Keys.Intersect(Application.ImportantSubs.Keys);

            foreach (var package in important)
            {
                IRC.Instance.SendMain("Important package update: {0}{1}{2} -{3} {4}", Colors.BLUE, Steam.GetPackageName(package), Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetPackageURL(package, "history"));
            }
        }

        private static void SendChangelistsToIRC(SteamApps.PICSChangesCallback callback)
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

                string name;
                string nameOther;
                var names = new Dictionary<uint, string>();

                if (appCount > 0)
                {
                    using (var reader = DbWorker.ExecuteReader(string.Format("SELECT `AppID`, `Name`, `LastKnownName` FROM `Apps` WHERE `AppID` IN ({0})", string.Join(", ", changeList.Apps.Select(x => x.ID)))))
                    {
                        while (reader.Read())
                        {
                            name = Utils.RemoveControlCharacters(reader.GetString("Name"));
                            nameOther = Utils.RemoveControlCharacters(reader.GetString("LastKnownName"));

                            if (!string.IsNullOrEmpty(nameOther) && !name.Equals(nameOther))
                            {
                                name = string.Format("{0} {1}({2}){3}", name, Colors.DARKGRAY, nameOther, Colors.NORMAL);
                            }

                            names.Add(reader.GetUInt32("AppID"), name);
                        }
                    }

                    foreach (var app in changeList.Apps)
                    {
                        if (!names.TryGetValue(app.ID, out name))
                        {
                            name = string.Format("{0}{1}", Colors.RED, SteamDB.UNKNOWN_APP);
                        }

                        IRC.Instance.SendAnnounce("  App: {0}{1}{2} - {3}{4}",
                            Colors.BLUE, app.ID, Colors.NORMAL,
                            name,
                            app.NeedsToken ? SteamDB.StringNeedToken : string.Empty
                        );
                    }
                }

                if (packageCount > 0)
                {
                    names.Clear();

                    using (var reader = DbWorker.ExecuteReader(string.Format("SELECT `SubID`, `Name`, `LastKnownName` FROM `Subs` WHERE `SubID` IN ({0})", string.Join(", ", changeList.Packages.Select(x => x.ID)))))
                    {
                        while (reader.Read())
                        {
                            name = Utils.RemoveControlCharacters(reader.GetString("Name"));
                            nameOther = Utils.RemoveControlCharacters(reader.GetString("LastKnownName"));

                            if (!string.IsNullOrEmpty(nameOther) && !name.Equals(nameOther))  // TODO: Only do it for 'Steam Sub' names?
                            {
                                name = string.Format("{0} {1}({2}){3}", name, Colors.DARKGRAY, nameOther, Colors.NORMAL);
                            }

                            names.Add(reader.GetUInt32("SubID"), name);
                        }
                    }

                    foreach (var package in changeList.Packages)
                    {
                        if (!names.TryGetValue(package.ID, out name))
                        {
                            name = string.Format("{0}SteamDB Unknown Package", Colors.RED);
                        }

                        IRC.Instance.SendAnnounce("  Package: {0}{1}{2} - {3}{4}",
                            Colors.BLUE, package.ID, Colors.NORMAL,
                            name,
                            package.NeedsToken ? SteamDB.StringNeedToken : string.Empty
                        );
                    }
                }
            }
        }

        private static void InsertPackagesToChangelist(string changes)
        {
            changes = string.Format("INSERT INTO `ChangelistsSubs` (`ChangeID`, `SubID`) VALUES {0} ON DUPLICATE KEY UPDATE `SubID` = `SubID`", changes.Remove(changes.Length - 1));

            DbWorker.ExecuteNonQuery(changes);
        }
    }
}
