/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using Bugsnag;
using Bugsnag.Clients;

namespace SteamDatabaseBackend
{
    static class ErrorReporter
    {
        private static BaseClient Client;

        public static void Init(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                Log.WriteWarn("Error Reporter", "API key is empty, errors will not be sent to Bugsnag.");

                return;
            }

            Client = new BaseClient(apiKey);
        }

        public static void Notify(Exception e)
        {
            if (Client != null)
            {
                Client.Notify(e, Severity.Error);
            }
        }
    }
}
