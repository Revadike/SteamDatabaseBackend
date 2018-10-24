/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Threading;

namespace SteamDatabaseBackend
{
    class Watchdog
    {
        private readonly Timer Timer;

        public Watchdog()
        {
            Timer = new Timer(OnTimer, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        }

        private void OnTimer(object state)
        {
            if (Settings.Current.IRC.Enabled && !IRC.Instance.IsConnected)
            {
                Log.WriteWarn("Watchdog", "Forcing IRC reconnect.");

                IRC.Instance.Connect();
            }

            if (Steam.Instance.Client.IsConnected && Application.ChangelistTimer.Enabled)
            {
                AccountInfo.Sync();

                if (WebAuth.IsAuthorized)
                {
                    TaskManager.RunAsync(async () => await AccountInfo.RefreshAppsToIdle());
                }
                else
                {
                    WebAuth.AuthenticateUser();
                }
            }
            else if (DateTime.Now.Subtract(Connection.LastSuccessfulLogin).TotalMinutes >= 5.0)
            {
                Log.WriteWarn("Watchdog", "Forcing a Steam reconnect.");

                Connection.Reconnect(null, null);
            }
        }
    }
}
