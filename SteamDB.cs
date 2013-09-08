/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;

namespace SteamDatabaseBackend
{
    public static class SteamDB
    {
        public static string GetChangelistURL(uint changeNumber)
        {
            return string.Format("{0}/changelist/{1}/", Settings.Current.BaseURL, changeNumber);
        }

        public static string GetRawAppURL(uint appID)
        {
            return string.Format("{0}/app/{1}.vdf", Settings.Current.RawBaseURL, appID);
        }

        public static string GetRawPackageURL(uint subID)
        {
            return string.Format("{0}/sub/{1}.vdf", Settings.Current.RawBaseURL, subID);
        }

        public static string GetGraphURL(uint appID)
        {
            return string.Format("{0}/graph/{1}/", Settings.Current.BaseURL, appID);
        }

        public static string GetAppURL(uint appID)
        {
            return string.Format("{0}/app/{1}/", Settings.Current.BaseURL, appID);
        }

        public static string GetAppURL(uint appID, string section)
        {
            return string.Format("{0}/app/{1}/#section_{2}", Settings.Current.BaseURL, appID, section);
        }

        public static string GetPackageURL(uint subID)
        {
            return string.Format("{0}/sub/{1}/", Settings.Current.BaseURL, subID);
        }

        public static string GetPackageURL(uint subID, string section)
        {
            return string.Format("{0}/sub/{1}/#section_{2}", Settings.Current.BaseURL, subID, section);
        }
    }
}
