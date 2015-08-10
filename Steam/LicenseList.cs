/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
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

        static LicenseList()
        {
            OwnedApps = new Dictionary<uint, byte>();
            OwnedSubs = new Dictionary<uint, byte>();
        }

        public LicenseList(CallbackManager manager)
            : base(manager)
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

            if (!licenseList.LicenseList.Any())
            {
                OwnedSubs.Clear();
                OwnedApps.Clear();

                return;
            }

            var ownedSubs = new Dictionary<uint, byte>();

            foreach (var license in licenseList.LicenseList)
            {
                // For some obscure reason license list can contain duplicates
                if (ownedSubs.ContainsKey(license.PackageID))
                {
                    Log.WriteWarn("LicenseList", "Already contains {0} ({1})", license.PackageID, license.PaymentMethod);

                    continue;
                }

                ownedSubs.Add(license.PackageID, (byte)license.PaymentMethod);
            }


            OwnedSubs = ownedSubs;

            RefreshApps();
        }

        public static void RefreshApps()
        {
            if (!OwnedSubs.Any())
            {
                return;
            }

            using (var db = Database.GetConnection())
            {
                OwnedApps = db.Query<App>("SELECT DISTINCT `AppID` FROM `SubsApps` WHERE `SubID` IN @Ids", new { Ids = OwnedSubs.Keys }).ToDictionary(x => x.AppID, x => (byte)1);
            }
        }
    }
}
