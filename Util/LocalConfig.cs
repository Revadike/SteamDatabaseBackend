/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    internal static class LocalConfig
    {
        public static async Task Update(string key, string value)
        {
            Log.WriteDebug(nameof(LocalConfig), $"Saving {key}");

            await using var db = await Database.GetConnectionAsync();
            await db.ExecuteAsync("INSERT INTO `LocalConfig` (`ConfigKey`, `Value`) VALUES (@ConfigKey, @Value) ON DUPLICATE KEY UPDATE `Value` = VALUES(`Value`)",
                new
                {
                    ConfigKey = key,
                    Value = value,
                }
            );
        }
    }
}
