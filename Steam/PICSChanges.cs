/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSChanges : SteamHandler
    {
        public uint PreviousChangeNumber { get; private set; }

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

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ChangeID` FROM `Changelists` ORDER BY `ChangeID` DESC LIMIT 1"))
            {
                if (Reader.Read())
                {
                    PreviousChangeNumber = Reader.GetUInt32("ChangeID");

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

            Log.WriteInfo("Steam", "Requesting info for {0} apps and {1} packages", callback.AppChanges.Count, callback.PackageChanges.Count);

            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), callback.PackageChanges.Keys.Select(package => Utils.NewPICSRequest(package))));
            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(callback.AppChanges.Keys, Enumerable.Empty<uint>()));
        }

        private void OnPICSChanges(SteamApps.PICSChangesCallback callback)
        {
            if (PreviousChangeNumber == callback.CurrentChangeNumber)
            {
                return;
            }

            if (Application.Instance.ProcessorPool.IsIdle)
            {
                Log.WriteDebug("Steam", "Cleaning processed {0} apps and {1} subs", Application.Instance.ProcessedApps.Count, Application.Instance.ProcessedSubs.Count);

                // TODO: Do we really need to clear? Find a better solution for this
                Application.Instance.ProcessedApps.Clear();
                Application.Instance.ProcessedSubs.Clear();
            }

            var packageChangesCount = callback.PackageChanges.Count;
            var appChangesCount = callback.AppChanges.Count;

            Log.WriteInfo("Steam", "Changelist {0} -> {1} ({2} apps, {3} packages)", PreviousChangeNumber, callback.CurrentChangeNumber, appChangesCount, packageChangesCount);

            PreviousChangeNumber = callback.CurrentChangeNumber;

            DbWorker.ExecuteNonQuery("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = CURRENT_TIMESTAMP()", new MySqlParameter("@ChangeID", callback.CurrentChangeNumber));

            if (appChangesCount == 0 && packageChangesCount == 0)
            {
                IRC.Instance.SendAnnounce("{0}»{1} Changelist {2}{3}{4} (empty)", Colors.RED, Colors.NORMAL, Colors.OLIVE, PreviousChangeNumber, Colors.DARKGRAY);

                return;
            }

            Application.Instance.SecondaryPool.QueueWorkItem(SendChangelistsToIRC, callback);

            if (appChangesCount > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(callback.AppChanges.Keys, Enumerable.Empty<uint>()));

                Application.Instance.SecondaryPool.QueueWorkItem(HandleApps, callback);
            }

            if (packageChangesCount > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), callback.PackageChanges.Keys.Select(package => Utils.NewPICSRequest(package))));

                Application.Instance.SecondaryPool.QueueWorkItem(HandlePackages, callback);
            }
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

                DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `AppID` = @AppID", new MySqlParameter("@AppID", app.ID));

                changes += string.Format("({0}, {1}),", app.ChangeNumber, app.ID);
            }

            if (!changes.Equals(string.Empty))
            {
                changes = string.Format("INSERT INTO `ChangelistsApps` (`ChangeID`, `AppID`) VALUES {0} ON DUPLICATE KEY UPDATE `AppID` = `AppID`", changes.Remove(changes.Length - 1));

                DbWorker.ExecuteNonQuery(changes);
            }
        }

        private static void HandlePackages(SteamApps.PICSChangesCallback callback)
        {
            string changes = string.Empty;

            foreach (var package in callback.PackageChanges.Values)
            {
                if (callback.CurrentChangeNumber != package.ChangeNumber)
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = `Date`", new MySqlParameter("@ChangeID", package.ChangeNumber));
                }

                DbWorker.ExecuteNonQuery("UPDATE `Subs` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `SubID` = @SubID", new MySqlParameter("@SubID", package.ID));

                changes += string.Format("({0}, {1}),", package.ChangeNumber, package.ID);
            }

            if (!changes.Equals(string.Empty))
            {
                changes = string.Format("INSERT INTO `ChangelistsSubs` (`ChangeID`, `SubID`) VALUES {0} ON DUPLICATE KEY UPDATE `SubID` = `SubID`", changes.Remove(changes.Length - 1));

                DbWorker.ExecuteNonQuery(changes);
            }
        }

        private static void SendChangelistsToIRC(SteamApps.PICSChangesCallback callback)
        {
            // Print any apps importants first
            var important = callback.AppChanges.Keys.Intersect(Application.Instance.ImportantApps.Keys);

            if (important.Count() > 5)
            {
                IRC.Instance.SendMain("{0}{1}{2} important apps updated -{3} {4}", Colors.OLIVE, important.Count(), Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetChangelistURL(callback.CurrentChangeNumber));
            }
            else
            {
                foreach (var app in important)
                {
                    IRC.Instance.SendMain("Important app update: {0}{1}{2} -{3} {4}", Colors.OLIVE, Steam.GetAppName(app), Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetAppURL(app, "history"));
                }
            }

            // And then important packages
            important = callback.PackageChanges.Keys.Intersect(Application.Instance.ImportantSubs.Keys);

            if (important.Count() > 5)
            {
                IRC.Instance.SendMain("{0}{1}{2} important packages updated -{3} {4}", Colors.OLIVE, important.Count(), Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetChangelistURL(callback.CurrentChangeNumber));
            }
            else
            {
                foreach (var package in important)
                {
                    IRC.Instance.SendMain("Important package update: {0}{1}{2} -{3} {4}", Colors.OLIVE, Steam.GetPackageName(package), Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetPackageURL(package, "history"));
                }
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

                string Message = string.Format("Changelist {0}{1}{2} {3}({4:N0} apps and {5:N0} packages){6} -{7} {8}",
                                     Colors.OLIVE, changeList.ChangeNumber, Colors.NORMAL,
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

                if (appCount > 0)
                {
                    foreach (var app in changeList.Apps)
                    {
                        name = Steam.GetAppName(app.ID, true);

                        if (string.IsNullOrEmpty(name))
                        {
                            name = string.Format("{0}{1}{2}", Colors.GREEN, app.ID, Colors.NORMAL);
                        }
                        else
                        {
                            name = string.Format("{0}{1}{2} - {3}", Colors.LIGHTGRAY, app.ID, Colors.NORMAL, name);
                        }

                        IRC.Instance.SendAnnounce("  App: {0}{1}{2}",
                            name,
                            app.NeedsToken ? SteamDB.StringNeedToken : string.Empty,
                            Application.Instance.OwnedApps.ContainsKey(app.ID) ? SteamDB.StringCheckmark : string.Empty
                        );
                    }
                }

                if (packageCount > 0)
                {
                    foreach (var package in changeList.Packages)
                    {
                        name = Steam.GetPackageName(package.ID, true);

                        if (string.IsNullOrEmpty(name))
                        {
                            name = string.Format("{0}{1}{2}", Colors.GREEN, package.ID, Colors.NORMAL);
                        }
                        else
                        {
                            name = string.Format("{0}{1}{2} - {3}", Colors.LIGHTGRAY, package.ID, Colors.NORMAL, name);
                        }

                        IRC.Instance.SendAnnounce("  Package: {0}{1}{2}",
                            name,
                            package.NeedsToken ? SteamDB.StringNeedToken : string.Empty,
                            Application.Instance.OwnedPackages.ContainsKey(package.ID) ? SteamDB.StringCheckmark : string.Empty
                        );
                    }
                }
            }
        }
    }
}
