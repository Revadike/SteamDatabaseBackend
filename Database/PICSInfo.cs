/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
namespace SteamDatabaseBackend
{
    struct PICSInfo
    {
        public uint Key { get; set; }
        public string KeyName { get; set; }
        public string Value { get; set; }
        public bool Processed { get; set; }
    }
}
