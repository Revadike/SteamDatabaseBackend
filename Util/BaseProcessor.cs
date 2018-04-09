/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Data;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    abstract class BaseProcessor : IDisposable
    {
        protected IDbConnection DbConnection;
        protected SteamApps.PICSProductInfoCallback.PICSProductInfo ProductInfo;
        public uint Id;

        protected abstract Task LoadData();
        protected abstract Task ProcessData();
        protected abstract Task ProcessUnknown();
        protected abstract AsyncJob RefreshSteam();
        public abstract override string ToString();

        public void Dispose()
        {
            if (DbConnection != null)
            {
                DbConnection.Dispose();
                DbConnection = null;
            }
        }

        public async Task Process()
        {
            Log.WriteInfo(ToString(), "Process");

            try
            {
                DbConnection = await Database.GetConnectionAsync().ConfigureAwait(false);

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

#if DEBUG
            Log.WriteDebug(ToString(), "Processed");
#endif
        }
    }
}
