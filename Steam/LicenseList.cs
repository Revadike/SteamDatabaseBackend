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
    class LicenseList : SteamHandler
    {
        public static Dictionary<uint, byte> OwnedApps { get; private set; }
        public static Dictionary<uint, byte> OwnedSubs { get; private set; }
        public static Dictionary<uint, byte> AnonymousApps { get; private set; }

        static LicenseList()
        {
            OwnedApps = new Dictionary<uint, byte>();
            OwnedSubs = new Dictionary<uint, byte>();

            RefreshAnonymous();
        }

        public static void RefreshAnonymous()
        {
            using (var db = Database.Get())
            {
                AnonymousApps = db
                    .Query("SELECT `AppID` FROM `SubsApps` WHERE `SubID` = 17906")
                    .ToDictionary(x => (uint)x.AppID, _ => (byte)1);
            }

            Log.WriteDebug("LicenseList", "Loaded {0} anonymous apps", AnonymousApps.Count);
        }

        public LicenseList(CallbackManager manager)
        {
            manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseListCallback);
        }

        private static void OnLicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                Log.WriteError("LicenseList", "Failed: {0}", licenseList.Result);

                return;
            }

            Log.WriteInfo("LicenseList", "Received {0} licenses from Steam", licenseList.LicenseList.Count);

            if (licenseList.LicenseList.Count == 0)
            {
                return;
            }

            var ownedSubs = new Dictionary<uint, byte>();
            var newSubs = new List<uint>();
            var hasAnyLicense = OwnedSubs.Count > 0;

            foreach (var license in licenseList.LicenseList)
            {
                // Expired licenses block access to depots, so we have no use in these
                if ((license.LicenseFlags & ELicenseFlags.Expired) != 0)
                {
                    continue;
                }

                // For some obscure reason license list can contain duplicates
                if (ownedSubs.ContainsKey(license.PackageID))
                {
                    Log.WriteWarn("LicenseList", "Already contains {0} ({1})", license.PackageID, license.PaymentMethod);

                    continue;
                }

                if (hasAnyLicense && !OwnedSubs.ContainsKey(license.PackageID))
                {
                    Log.WriteInfo("LicenseList", $"New license granted: {license.PackageID} ({license.PaymentMethod}, {license.LicenseFlags})");

                    newSubs.Add(license.PackageID);
                }

                ownedSubs.Add(license.PackageID, (byte)license.PaymentMethod);
            }

            OwnedSubs = ownedSubs;

            RefreshApps();

            if (newSubs.Count <= 0)
            {
                return;
            }

            using (var db = Database.Get())
            {
                var apps = db.Query<uint>("SELECT `AppID` FROM `SubsApps` WHERE `Type` = \"app\" AND `SubID` IN @Ids", new { Ids = newSubs });

                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>()));
            }
        }

        public static void RefreshApps()
        {
            if (OwnedSubs.Count == 0)
            {
                return;
            }

            using (var db = Database.Get())
            {
                OwnedApps = db.Query<App>("SELECT DISTINCT `AppID` FROM `SubsApps` WHERE `SubID` IN @Ids", new { Ids = OwnedSubs.Keys }).ToDictionary(x => x.AppID, _ => (byte)1);
            }
        }
    }
}
