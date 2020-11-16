/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;

namespace SteamDatabaseBackend
{
    internal static class SteamDB
    {
        public const uint DatabaseAppType   = 9;
        public const uint DatabaseNameType = 10;

        public const string UserAgent = "Steam Database (https://github.com/SteamDatabase/SteamDatabaseBackend)";

        public const string UnknownAppName = "SteamDB Unknown App";

        public static readonly string StringNeedToken = $" {Colors.DARKGRAY}(needs token){Colors.NORMAL}";
        public static readonly string StringCheckmark = $" {Colors.DARKGRAY}✓{Colors.NORMAL}";

        public static string GetBlogUrl(string postId) => new Uri(Settings.Current.BaseURL, $"/blog/{postId}/").AbsoluteUri;

        public static string GetRawAppUrl(uint appId) => new Uri(Settings.Current.RawBaseURL, $"/app/{appId}.vdf").AbsoluteUri;

        public static string GetRawPackageUrl(uint subId) => new Uri(Settings.Current.RawBaseURL, $"/sub/{subId}.vdf").AbsoluteUri;

        public static string GetPublishedFileRawUrl(ulong id) => new Uri(Settings.Current.RawBaseURL, $"/ugc/{id}.json").AbsoluteUri;

        public static string GetAppUrl(uint appId) => new Uri(Settings.Current.BaseURL, $"/app/{appId}/").AbsoluteUri;

        public static string GetAppUrl(uint appId, string section) => new Uri(Settings.Current.BaseURL, $"/app/{appId}/{section}/").AbsoluteUri;

        public static string GetPackageUrl(uint subId) => new Uri(Settings.Current.BaseURL, $"/sub/{subId}/").AbsoluteUri;

        public static string GetPackageUrl(uint subId, string section) => new Uri(Settings.Current.BaseURL, $"/sub/{subId}/{section}/").AbsoluteUri;

        public static string GetDepotUrl(uint depotId) => new Uri(Settings.Current.BaseURL, $"/depot/{depotId}/").AbsoluteUri;

        public static string GetPatchnotesUrl(uint buildId) => new Uri(Settings.Current.BaseURL, $"/patchnotes/{buildId}/").AbsoluteUri;
    }
}
