﻿/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class AppProcessor : BaseProcessor
    {
        public const string HistoryQuery = "INSERT INTO `AppsHistory` (`ChangeID`, `AppID`, `Action`, `Key`, `OldValue`, `NewValue`, `Diff`) VALUES (@ChangeID, @ID, @Action, @Key, @OldValue, @NewValue, @Diff)";

        private static readonly string[] Triggers =
        {
            "Valve",
            "Steam",
            "Half-Life",
            "Left 4 Dead",
            "Counter-Strike",
            "Dota",
            "Day of Defeat",
            "Team Fortress"
        };

        private Dictionary<string, PICSInfo> CurrentData;
        private uint ChangeNumber;
        private readonly uint AppID;

        public AppProcessor(uint appID, SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo)
        {
            Id = appID;
            AppID = appID;
            ProductInfo = productInfo;
        }

        protected override AsyncJob RefreshSteam()
        {
            return Steam.Instance.Apps.PICSGetAccessTokens(AppID, null);
        }

        protected override async Task LoadData()
        {
            CurrentData = (await DbConnection.QueryAsync<PICSInfo>("SELECT `Name` as `KeyName`, `Value`, `Key` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new { AppID })).ToDictionary(x => x.KeyName, x => x);
        }

        protected override async Task ProcessData()
        {
            ChangeNumber = ProductInfo.ChangeNumber;

            if (Settings.IsFullRun)
            {
                await DbConnection.ExecuteAsync("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeNumber) ON DUPLICATE KEY UPDATE `Date` = `Date`", new { ProductInfo.ChangeNumber });
                await DbConnection.ExecuteAsync("INSERT INTO `ChangelistsApps` (`ChangeID`, `AppID`) VALUES (@ChangeNumber, @AppID) ON DUPLICATE KEY UPDATE `AppID` = `AppID`", new { AppID, ProductInfo.ChangeNumber });
            }

            await ProcessKey("root_changenumber", "changenumber", ChangeNumber.ToString());

            var app = (await DbConnection.QueryAsync<App>("SELECT `Name`, `AppType` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new { AppID })).SingleOrDefault();

            var isPublicOnly = false;
            var newAppName = ProductInfo.KeyValues["common"]["name"].AsString();
            var newAppType = EAppType.Invalid;

            if (newAppName != null)
            {
                var currentType = ProductInfo.KeyValues["common"]["type"].AsString().ToLowerInvariant();

                newAppType = Utils.GetAppType(currentType);
                var modifiedNameOrType = false;

                if (string.IsNullOrEmpty(app.Name) || app.Name.StartsWith(SteamDB.UnknownAppName, StringComparison.Ordinal))
                {
                    await DbConnection.ExecuteAsync("INSERT INTO `Apps` (`AppID`, `AppType`, `Name`, `LastKnownName`) VALUES (@AppID, @Type, @AppName, @AppName) ON DUPLICATE KEY UPDATE `Name` = VALUES(`Name`), `LastKnownName` = VALUES(`LastKnownName`), `AppType` = VALUES(`AppType`)",
                        new
                        {
                            AppID,
                            Type = (int)newAppType,
                            AppName = newAppName
                        }
                    );

                    await MakeHistory("created_app");
                    await MakeHistory("created_info", SteamDB.DatabaseNameType, string.Empty, newAppName);

                    modifiedNameOrType = true;
                }
                else if (app.Name != newAppName)
                {
                    await DbConnection.ExecuteAsync("UPDATE `Apps` SET `Name` = @AppName, `LastKnownName` = @AppName WHERE `AppID` = @AppID", new { AppID, AppName = newAppName });
                    await MakeHistory("modified_info", SteamDB.DatabaseNameType, app.Name, newAppName);

                    modifiedNameOrType = true;
                }

                if (app.AppType != newAppType)
                {
                    await DbConnection.ExecuteAsync("UPDATE `Apps` SET `AppType` = @Type WHERE `AppID` = @AppID", new { AppID, Type = (int)newAppType });

                    if (app.AppType == EAppType.Invalid)
                    {
                        await MakeHistory("created_info", SteamDB.DatabaseAppType, string.Empty, newAppType.ToString("d"));
                    }
                    else
                    {
                        await MakeHistory("modified_info", SteamDB.DatabaseAppType, app.AppType.ToString(), newAppType.ToString("d"));
                    }

                    modifiedNameOrType = true;
                }

                if (modifiedNameOrType && Triggers.Any(newAppName.Contains))
                {
                    IRC.Instance.SendOps($"New {newAppType}: {Colors.BLUE}{Utils.LimitStringLength(newAppName)}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetAppUrl(AppID, "history")}");
                }
            }

            foreach (var section in ProductInfo.KeyValues.Children)
            {
                var sectionName = section.Name.ToLowerInvariant();

                if (sectionName == "appid" || sectionName == "change_number")
                {
                    continue;
                }

                if (sectionName == "common" || sectionName == "extended")
                {
                    foreach (var keyvalue in section.Children)
                    {
                        var keyName = $"{sectionName}_{keyvalue.Name}";

                        if (keyName == "common_type" || keyName == "common_gameid" || keyName == "common_name" || keyName == "extended_order")
                        {
                            // Ignore common keys that are either duplicated or serve no real purpose
                            continue;
                        }

                        if (keyvalue.Value != null)
                        {
                            await ProcessKey(keyName, keyvalue.Name, keyvalue.Value);
                            
                        }
                        else
                        {
                            await ProcessKey(keyName, keyvalue.Name, Utils.JsonifyKeyValue(keyvalue), keyvalue);
                        }
                    }
                }
                else if (sectionName == "public_only")
                {
                    isPublicOnly = section.Value == "1";

                    await ProcessKey($"root_{sectionName}", section.Name, section.Value);
                }
                else
                {
                    sectionName = $"root_{sectionName}";

                    if (await ProcessKey(sectionName, sectionName, Utils.JsonifyKeyValue(section), section) && sectionName == "root_depots")
                    {
                        await DbConnection.ExecuteAsync("UPDATE `Apps` SET `LastDepotUpdate` = CURRENT_TIMESTAMP() WHERE `AppID` = @AppID", new { AppID });
                    }
                }
            }

            // If app gets hidden but we already have data, do not delete the already existing app info
            if (newAppName != null)
            {
                foreach (var data in CurrentData.Values)
                {
                    // This key still exists in appinfo and was correctly processed above
                    if (data.Processed)
                    {
                        continue;
                    }

                    // This is a key that is created and handled by steamdb.info; not in appinfo
                    if (data.KeyName.StartsWith("website", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // If this app requires a token, but previously was public and we had stored data, keep it around
                    if (isPublicOnly && !data.KeyName.StartsWith("common", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await DbConnection.ExecuteAsync("DELETE FROM `AppsInfo` WHERE `AppID` = @AppID AND `Key` = @Key", new { AppID, data.Key });
                    await MakeHistory("removed_key", data.Key, data.Value);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(app.Name)) // We don't have the app in our database yet
                {
                    await DbConnection.ExecuteAsync("INSERT INTO `Apps` (`AppID`, `Name`) VALUES (@AppID, @AppName) ON DUPLICATE KEY UPDATE `AppType` = `AppType`", new {
                        AppID,
                        AppName = $"{SteamDB.UnknownAppName} {AppID}"
                    });
                }
                else if (!app.Name.StartsWith(SteamDB.UnknownAppName, StringComparison.Ordinal)) // We do have the app, replace it with default name
                {
                    await DbConnection.ExecuteAsync("UPDATE `Apps` SET `Name` = @AppName, `AppType` = @AppType WHERE `AppID` = @AppID", new {
                        AppID,
                        AppType = (int)EAppType.Invalid,
                        AppName = $"{SteamDB.UnknownAppName} {AppID}"
                    });
                    await MakeHistory("deleted_app", 0, app.Name);
                }
            }
            
            if (ProductInfo.KeyValues["depots"].Children.Any())
            {
                await Steam.Instance.DepotProcessor.Process(DbConnection, AppID, ChangeNumber, ProductInfo.KeyValues["depots"]);
            }

            if (ProductInfo.MissingToken && PICSTokens.HasAppToken(AppID))
            {
                Log.WriteError(nameof(PICSTokens), $"Overridden token for appid {AppID} is invalid?");
                IRC.Instance.SendOps($"[Tokens] Looks like the overridden token for appid {AppID} ({newAppName}) is invalid");
            }

            if (Settings.IsMillhaven && newAppType == EAppType.Beta && !LicenseList.OwnedApps.ContainsKey(AppID))
            {
                var betaAppId = ProductInfo.KeyValues["extended"]["betaforappid"].AsUnsignedInteger();

                if (betaAppId == 0)
                {
                    betaAppId = ProductInfo.KeyValues["common"]["parent"].AsUnsignedInteger();
                }

                if (betaAppId > 0)
                {
                    Steam.Instance.FreeLicense.AddBeta(AppID, betaAppId);
                }
            }
        }

        protected override async Task ProcessUnknown()
        {
            var name = await DbConnection.ExecuteScalarAsync<string>("SELECT `Name` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new { AppID });

            var data = CurrentData.Values.Where(x => !x.KeyName.StartsWith("website", StringComparison.Ordinal)).ToList();

            if (data.Count > 0)
            {
                await DbConnection.ExecuteAsync(HistoryQuery, data.Select(x => new PICSHistory
                {
                    ID = AppID,
                    ChangeID = ChangeNumber,
                    Key = x.Key,
                    OldValue = x.Value,
                    Action = "removed_key"
                }));
            }

            if (!string.IsNullOrEmpty(name) && !name.StartsWith(SteamDB.UnknownAppName, StringComparison.Ordinal))
            {
                await DbConnection.ExecuteAsync(HistoryQuery, new PICSHistory
                {
                    ID = AppID,
                    ChangeID = ChangeNumber,
                    OldValue = name,
                    Action = "deleted_app"
                });
            }

            await DbConnection.ExecuteAsync("DELETE FROM `Apps` WHERE `AppID` = @AppID", new { AppID });
            await DbConnection.ExecuteAsync("DELETE FROM `AppsInfo` WHERE `AppID` = @AppID", new { AppID });

            if (Settings.IsMillhaven)
            {
                await DbConnection.ExecuteAsync("DELETE FROM `Store` WHERE `AppID` = @AppID", new { AppID });
            }
        }

        private async Task<bool> ProcessKey(string keyName, string displayName, string value, KeyValue newKv = null)
        {
            if (keyName.Length > 90)
            {
                Log.WriteError(nameof(AppProcessor), $"Key {keyName} for AppID {AppID} is too long, not inserting info.");

                return false;
            }

            // All keys in PICS are supposed to be lower case
            keyName = keyName.ToLowerInvariant().Trim();

            if (!CurrentData.ContainsKey(keyName))
            {
                CurrentData[keyName] = new PICSInfo
                {
                    Processed = true,
                };

                var key = KeyNameCache.GetAppKeyID(keyName);

                if (key == 0)
                {
                    var type = newKv != null ? 86 : 0; // 86 is a hardcoded const for the website

                    key = await KeyNameCache.CreateAppKey(keyName, displayName, type);

                    if (key == 0)
                    {
                        // We can't insert anything because key wasn't created
                        Log.WriteError(nameof(AppProcessor), $"Failed to create key {keyName} for AppID {AppID}, not inserting info.");

                        return false;
                    }

                    IRC.Instance.SendOps($"New app keyname: {Colors.BLUE}{keyName} {Colors.LIGHTGRAY}(ID: {key}) ({displayName}) - {SteamDB.GetAppUrl(AppID, "history")}");
                }

                await DbConnection.ExecuteAsync("INSERT INTO `AppsInfo` (`AppID`, `Key`, `Value`) VALUES (@AppID, @Key, @Value)", new { AppID, Key = key, Value = value });
                await MakeHistory("created_key", key, string.Empty, value);

                if ((keyName == "extended_developer" || keyName == "extended_publisher") && value == "Valve")
                {
                    IRC.Instance.SendOps($"New {displayName}=Valve app: {Colors.BLUE}{Steam.GetAppName(AppID)}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetAppUrl(AppID, "history")}");
                }

                if (keyName == "common_oslist" && value.Contains("linux"))
                {
                    PrintLinux();
                }

                return true;
            }

            var data = CurrentData[keyName];

            if (data.Processed)
            {
                Log.WriteWarn(nameof(AppProcessor), $"Duplicate key {keyName} in AppID {AppID}");

                return false;
            }

            data.Processed = true;

            CurrentData[keyName] = data;

            if (data.Value == value)
            {
                return false;
            }

            await DbConnection.ExecuteAsync("UPDATE `AppsInfo` SET `Value` = @Value WHERE `AppID` = @AppID AND `Key` = @Key", new { AppID, data.Key, Value = value });

            if (newKv != null)
            {
                await MakeHistoryForJson(data.Key, data.Value, newKv);
            }
            else
            {
                await MakeHistory("modified_key", data.Key, data.Value, value);
            }

            if (keyName == "common_oslist" && value.Contains("linux") && !data.Value.Contains("linux"))
            {
                PrintLinux();
            }

            return true;
        }

        private Task MakeHistoryForJson(uint keyNameId, string oldValue, KeyValue newKv)
        {
            var diff = JsonConvert.SerializeObject(DiffKeyValues.Diff(oldValue, newKv));

            return DbConnection.ExecuteAsync(HistoryQuery,
                new PICSHistory
                {
                    ID = AppID,
                    ChangeID = ChangeNumber,
                    Key = keyNameId,
                    Diff = diff,
                    Action = "modified_key"
                }
            );
        }

        private Task MakeHistory(string action, uint keyNameID = 0, string oldValue = "", string newValue = "")
        {
            return DbConnection.ExecuteAsync(HistoryQuery,
                new PICSHistory
                {
                    ID = AppID,
                    ChangeID = ChangeNumber,
                    Key = keyNameID,
                    OldValue = oldValue,
                    NewValue = newValue,
                    Action = action
                }
            );
        }

        private void PrintLinux()
        {
            var name = Steam.GetAppName(AppID, out var appType);

            if (appType != EAppType.Game && appType != EAppType.Application)
            {
                return;
            }

            if (!Settings.IsMillhaven)
            {
                return;
            }

            IRC.Instance.SendLinuxAnnouncement($"\U0001F427 {name} now lists Linux - {SteamDB.GetAppUrl(AppID, "history")}");
        }

        public override string ToString()
        {
            return $"{(ProductInfo == null ? "Unknown " : "")}App {AppID}";
        }
    }
}
