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
        public LicenseList(CallbackManager manager)
            : base(manager)
        {
            manager.Register(new Callback<SteamApps.LicenseListCallback>(OnLicenseListCallback));
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
                Application.OwnedSubs.Clear();
                Application.OwnedApps.Clear();

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

            if (ownedSubs.Any())
            {
                using (var db = Database.GetConnection())
                {
                    Application.OwnedApps = db.Query<App>("SELECT DISTINCT `AppID` FROM `SubsApps` WHERE `SubID` IN @Ids", new { Ids = ownedSubs.Keys }).ToDictionary(x => x.AppID, x => (byte)1);
                }
            }

            Application.OwnedSubs = ownedSubs;
        }
    }
}
