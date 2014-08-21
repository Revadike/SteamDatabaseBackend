/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Linq;
using MySql.Data.MySqlClient;
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
                Log.WriteError("Steam", "Unable to get license list: {0}", licenseList.Result);

                return;
            }

            Log.WriteInfo("Steam", "{0} licenses received", licenseList.LicenseList.Count);

            Application.Instance.OwnedPackages = licenseList.LicenseList.ToDictionary(lic => lic.PackageID, lic => (byte)1);

            Application.Instance.OwnedApps.Clear();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(string.Format("SELECT DISTINCT `AppID` FROM `SubsApps` WHERE `SubID` IN ({0})", string.Join(", ", Application.Instance.OwnedPackages.Keys))))
            {
                while (Reader.Read())
                {
                    Application.Instance.OwnedApps.Add(Reader.GetUInt32("AppID"), 1);
                }
            }
        }
    }
}
