/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    internal static class KeyNameCache
    {
        private static Dictionary<string, uint> App;
        private static Dictionary<string, uint> Sub;

        public static async Task Init()
        {
            await using var db = await Database.GetConnectionAsync();
            App = (await db.QueryAsync<KeyName>("SELECT `Name`, `ID` FROM `KeyNames`")).ToDictionary(x => x.Name, x => x.ID);
            Sub = (await db.QueryAsync<KeyName>("SELECT `Name`, `ID` FROM `KeyNamesSubs`")).ToDictionary(x => x.Name, x => x.ID);
        }

        public static uint GetAppKeyID(string name)
        {
            App.TryGetValue(name, out var id);

            return id;
        }

        public static uint GetSubKeyID(string name)
        {
            Sub.TryGetValue(name, out var id);

            return id;
        }

        public static async Task<uint> CreateAppKey(string name, string displayName, int type)
        {
            uint newKey;

            await using (var db = await Database.GetConnectionAsync())
            {
                await db.ExecuteAsync("INSERT INTO `KeyNames` (`Name`, `Type`, `DisplayName`) VALUES(@Name, @Type, @DisplayName)", new
                {
                    Name = name,
                    DisplayName = displayName,
                    Type = type
                });

                newKey = await db.ExecuteScalarAsync<uint>("SELECT `ID` FROM `KeyNames` WHERE `Name` = @name", new { name });
            }

            if (newKey > 0)
            {
                App.Add(name, newKey);
            }

            return newKey;
        }

        public static async Task<uint> CreateSubKey(string name, string displayName, int type)
        {
            uint newKey;

            await using (var db = await Database.GetConnectionAsync())
            {
                await db.ExecuteAsync("INSERT INTO `KeyNamesSubs` (`Name`, `Type`, `DisplayName`) VALUES(@Name, @Type, @DisplayName)", new
                {
                    Name = name,
                    DisplayName = displayName,
                    Type = type
                });

                newKey = await db.ExecuteScalarAsync<uint>("SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = @name", new { name });
            }

            if (newKey > 0)
            {
                Sub.Add(name, newKey);
            }

            return newKey;
        }
    }
}
