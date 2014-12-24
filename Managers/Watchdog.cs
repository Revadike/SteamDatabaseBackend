/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Threading;

namespace SteamDatabaseBackend
{
    public class Watchdog
    {
        public Watchdog()
        {
            var t = new Timer(OnTimer, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        }

        private void OnTimer(object state)
        {
            if (Steam.Instance.Client.IsConnected)
            {
                AccountInfo.Sync();
            }
            else if (DateTime.Now.Subtract(Connection.LastSuccessfulLogin).TotalMinutes >= 5.0)
            {
                Connection.Reconnect(null, null);
            }
        }
    }
}
