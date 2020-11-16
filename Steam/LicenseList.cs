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
    internal class LicenseList : SteamHandler
    {
        public static Dictionary<uint, byte> OwnedApps { get; private set; }
        public static Dictionary<uint, byte> OwnedSubs { get; private set; }

        static LicenseList()
        {
            OwnedApps = new Dictionary<uint, byte>();
            OwnedSubs = new Dictionary<uint, byte>();
        }

        public LicenseList(CallbackManager manager)
        {
            manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseListCallback);
        }

        private static void OnLicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                Log.WriteError(nameof(LicenseList), $"Failed: {licenseList.Result}");

                return;
            }

            Log.WriteInfo(nameof(LicenseList), $"Received {licenseList.LicenseList.Count} licenses from Steam");

            if (licenseList.LicenseList.Count == 0)
            {
                return;
            }

            var ownedSubs = new Dictionary<uint, byte>();
            var newSubs = new List<uint>();
            var hasAnyLicense = OwnedSubs.Count > 0;

            foreach (var license in licenseList.LicenseList)
            {
                if (license.AccessToken > 0)
                {
                    PICSTokens.NewPackageRequest(license.PackageID, license.AccessToken);
                }

                // Expired licenses block access to depots, so we have no use in these
                if ((license.LicenseFlags & ELicenseFlags.Expired) != 0)
                {
                    continue;
                }

                // For some obscure reason license list can contain duplicates
                if (ownedSubs.ContainsKey(license.PackageID))
                {
#if DEBUG
                    Log.WriteWarn(nameof(LicenseList), $"Already contains {license.PackageID} ({license.PaymentMethod})");
#endif

                    continue;
                }

                if (hasAnyLicense && !OwnedSubs.ContainsKey(license.PackageID))
                {
                    Log.WriteInfo(nameof(LicenseList), $"New license granted: {license.PackageID} ({license.PaymentMethod}, {license.LicenseFlags})");

                    newSubs.Add(license.PackageID);
                }

                if (Steam.Instance.FreeLicense.FreeLicensesToRequest.TryRemove(license.PackageID, out _))
                {
                    Log.WriteInfo(nameof(FreeLicense), $"Package {license.PackageID} was granted, removed from free request");
                }

                ownedSubs.Add(license.PackageID, (byte)license.PaymentMethod);
            }

            OwnedSubs = ownedSubs;

            RefreshApps();

            if (newSubs.Count <= 0)
            {
                return;
            }

            using var db = Database.Get();
            var apps = db.Query<uint>("SELECT `AppID` FROM `SubsApps` WHERE `Type` = \"app\" AND `SubID` IN @Ids", new { Ids = newSubs }).ToList();

            JobManager.AddJob(
                () => Steam.Instance.Apps.PICSGetAccessTokens(apps, newSubs),
                new PICSTokens.RequestedTokens
                {
                    Apps = apps,
                    Packages = newSubs,
                });
        }

        public static void RefreshApps()
        {
            if (OwnedSubs.Count == 0)
            {
                return;
            }

            using var db = Database.Get();
            OwnedApps = db.Query<App>("SELECT DISTINCT `AppID` FROM `SubsApps` WHERE `SubID` IN @Ids", new { Ids = OwnedSubs.Keys }).ToDictionary(x => x.AppID, _ => (byte)1);
        }
    }
}
