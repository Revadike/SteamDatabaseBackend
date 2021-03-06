/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
namespace SteamDatabaseBackend
{
    internal class Depot
    {
        public uint DepotID { get; set; }
        public string Name { get; set; }
        public uint BuildID { get; set; }
        public ulong ManifestID { get; set; }
        public ulong LastManifestID { get; set; }
        public bool FilenamesEncrypted { get; set; }
    }
}
