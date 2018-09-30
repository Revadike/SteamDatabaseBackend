/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

namespace SteamDatabaseBackend
{
    enum FullRunState
    {
        None = 0,
        Normal = 1,
        WithForcedDepots = 2,
        ImportantOnly = 3,
        Enumerate = 4,
        PackagesNormal = 5,
    }
}
