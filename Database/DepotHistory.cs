/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
namespace SteamDatabaseBackend
{
    internal class DepotHistory
    {
        public uint ID { get; set; }
        public uint DepotID { get; set; }
        public uint ChangeID { get; set; }
        public ulong ManifestID { get; set; }
        public string Action { get; set; }
        public ulong OldValue { get; set; }
        public ulong NewValue { get; set; }
        public string File { get; set; } = "";
    }
}
