/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
namespace SteamDatabaseBackend
{
    internal class PICSHistory
    {
        public uint ID { get; set; } // AppID or SubID 
        public uint ChangeID { get; set; }
        public uint Key { get; set; }
        public string Action { get; set; }
        public string OldValue { get; set; } = "";
        public string NewValue { get; set; } = "";
        public string Diff { get; set; }
    }
}
