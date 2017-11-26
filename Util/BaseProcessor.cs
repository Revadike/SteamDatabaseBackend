/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    abstract class BaseProcessor : IDisposable
    {
        protected SteamApps.PICSProductInfoCallback.PICSProductInfo ProductInfo;
        public uint Id;

        protected abstract Task LoadData();
        protected abstract Task ProcessData();
        protected abstract Task ProcessUnknown();
        protected abstract AsyncJob RefreshSteam();
        public abstract override string ToString();
        public abstract void Dispose();

        public async Task Process()
        {
#if DEBUG
            Log.WriteDebug(ToString(), "Begin");
#endif

            try
            {
                await LoadData().ConfigureAwait(false);

                if (ProductInfo == null)
                {
                    await ProcessUnknown().ConfigureAwait(false);
                }
                else
                {
                    await ProcessData().ConfigureAwait(false);
                }
            }
            catch (MySqlException e)
            {
                ErrorReporter.Notify(ToString(), e);

                JobManager.AddJob(() => RefreshSteam());
            }
            catch (Exception e)
            {
                ErrorReporter.Notify(ToString(), e);
            }

#if DEBUG
            Log.WriteDebug(ToString(), "Done");
#endif
        }
    }
}
