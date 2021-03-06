/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class DepotFile
    {
        public uint DepotID { get; set; }
        public string File { get; set; }
        public byte[] Hash { get; set; }
        public ulong Size { get; set; }
        public EDepotFileFlag Flags { get; set; }
    }
}
