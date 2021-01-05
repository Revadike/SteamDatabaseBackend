/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
namespace SteamDatabaseBackend
{
    internal struct App
    {
        public uint AppID { get; set; }
        public EAppType AppType { get; set; }
        public string Name { get; set; }
        public string LastKnownName { get; set; }
    }
}
