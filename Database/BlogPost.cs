/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
namespace SteamDatabaseBackend
{
    internal struct BlogPost
    {
        public uint ID { get; set; }
        public string Title { get; set; }
        public string Slug { get; set; }
    }
}
