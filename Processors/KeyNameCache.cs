/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    static class KeyNameCache
    {
        private static Dictionary<string, uint> App;
        private static Dictionary<string, uint> Sub;

        public static async Task Init()
        {
            using (var db = Database.Get())
            {
                App = (await db.QueryAsync<KeyName>("SELECT `Name`, `ID` FROM `KeyNames`")).ToDictionary(x => x.Name, x => x.ID);
                Sub = (await db.QueryAsync<KeyName>("SELECT `Name`, `ID` FROM `KeyNamesSubs`")).ToDictionary(x => x.Name, x => x.ID);
            }
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

            using (var db = Database.Get())
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

            using (var db = Database.Get())
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
