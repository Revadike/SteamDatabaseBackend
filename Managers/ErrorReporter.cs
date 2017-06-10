/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;

namespace SteamDatabaseBackend
{
    static class ErrorReporter
    {
        public static void Notify(Exception e)
        {
            Log.WriteError("Caught Exception", "{0}", e);

            IRC.Instance.SendOps("Backend Exception: {0}", e.Message);
        }
    }
}
