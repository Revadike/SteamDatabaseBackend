/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class AppProcessor : IDisposable
    {
        private const uint DATABASE_APPTYPE   = 9;
        private const uint DATABASE_NAME_TYPE = 10;

        private IDbConnection DbConnection;

        private Dictionary<string, PICSInfo> CurrentData;
        private uint ChangeNumber;
        private readonly uint AppID;

        public AppProcessor(uint appID)
        {
            AppID = appID;

            DbConnection = Database.GetConnection();

            CurrentData = DbConnection.Query<PICSInfo>("SELECT `Name` as `KeyName`, `Value`, `Key` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new { AppID }).ToDictionary(x => x.KeyName, x => x);
        }

        public void Dispose()
        {
            if (DbConnection != null)
            {
                DbConnection.Dispose();
                DbConnection = null;
            }
        }

        public void Process(SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo)
        {
            ChangeNumber = productInfo.ChangeNumber;

#if !DEBUG
            if (Settings.IsFullRun)
#endif
            {
                Log.WriteDebug("App Processor", "AppID: {0}", AppID);
            }

            var app = DbConnection.Query<App>("SELECT `Name`, `AppType` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new { AppID }).SingleOrDefault();

            if (productInfo.KeyValues["common"]["name"].Value != null)
            {
                uint newAppType = 0;
                string currentType = productInfo.KeyValues["common"]["type"].AsString().ToLower();

                using (var reader = DbConnection.ExecuteReader("SELECT `AppType` FROM `AppsTypes` WHERE `Name` = @Type LIMIT 1", new { Type = currentType }))
                {
                    if (reader.Read())
                    {
                        newAppType = (uint)reader.GetInt32(reader.GetOrdinal("AppType"));
                    }
                    else
                    {
                        // TODO: Create it?
                        Log.WriteError("App Processor", "AppID {0} - unknown app type: {1}", AppID, currentType);

                        IRC.Instance.SendOps("New app type: {0}{1}{2} for app {3}{4}{5}", Colors.BLUE, currentType, Colors.NORMAL, Colors.BLUE, AppID, Colors.NORMAL);

                        ErrorReporter.Notify(new NotImplementedException(string.Format("Unknown apptype {0} (appid {1})", currentType, AppID)));
                    }
                }

                if (string.IsNullOrEmpty(app.Name) || app.Name.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal))
                {
                    DbConnection.Execute("INSERT INTO `Apps` (`AppID`, `AppType`, `Name`, `LastKnownName`) VALUES (@AppID, @Type, @AppName, @AppName) ON DUPLICATE KEY UPDATE `Name` = @AppName, `LastKnownName` = @AppName, `AppType` = @Type",
                        new { AppID, Type = newAppType, AppName = productInfo.KeyValues["common"]["name"].Value }
                    );

                    MakeHistory("created_app");
                    MakeHistory("created_info", DATABASE_NAME_TYPE, string.Empty, productInfo.KeyValues["common"]["name"].Value);

                    // TODO: Testy testy
                    if (!Settings.IsFullRun
                    &&  Settings.Current.ChatRooms.Count > 0
                    &&  !app.Name.StartsWith("SteamApp", StringComparison.Ordinal)
                    &&  !app.Name.StartsWith("ValveTest", StringComparison.Ordinal))
                    {
                        Steam.Instance.Friends.SendChatRoomMessage(Settings.Current.ChatRooms[0], EChatEntryType.ChatMsg,
                            string.Format(
                                "New {0} was published: {1}\nSteamDB: {2}\nSteam: http://store.steampowered.com/app/{3}/",
                                currentType,
                                productInfo.KeyValues["common"]["name"].AsString(),
                                SteamDB.GetAppURL(AppID),
                                AppID
                            )
                        );
                    }
                }
                else if (!app.Name.Equals(productInfo.KeyValues["common"]["name"].Value))
                {
                    string newAppName = productInfo.KeyValues["common"]["name"].AsString();

                    DbConnection.Execute("UPDATE `Apps` SET `Name` = @AppName, `LastKnownName` = @AppName WHERE `AppID` = @AppID", new { AppID, AppName = newAppName });

                    MakeHistory("modified_info", DATABASE_NAME_TYPE, app.Name, newAppName);
                }

                if (app.AppType == 0 || app.AppType != newAppType)
                {
                    DbConnection.Execute("UPDATE `Apps` SET `AppType` = @Type WHERE `AppID` = @AppID", new { AppID, Type = newAppType });

                    if (app.AppType == 0)
                    {
                        MakeHistory("created_info", DATABASE_APPTYPE, string.Empty, newAppType.ToString());
                    }
                    else
                    {
                        MakeHistory("modified_info", DATABASE_APPTYPE, app.AppType.ToString(), newAppType.ToString());
                    }
                }
            }

            foreach (var section in productInfo.KeyValues.Children)
            {
                string sectionName = section.Name.ToLower();

                if (sectionName == "appid" || sectionName == "public_only")
                {
                    continue;
                }

                if (sectionName == "change_number") // Carefully handle change_number
                {
                    sectionName = "root_change_number";

                    // TODO: Remove this key, move it to Apps table itself
                    ProcessKey(sectionName, "change_number", productInfo.ChangeNumber.ToString()); //section.AsString());
                }
                else if (sectionName == "common" || sectionName == "extended")
                {
                    string keyName;

                    foreach (KeyValue keyvalue in section.Children)
                    {
                        keyName = string.Format("{0}_{1}", sectionName, keyvalue.Name);

                        if (keyName.Equals("common_type") || keyName.Equals("common_gameid") || keyName.Equals("common_name") || keyName.Equals("extended_order"))
                        {
                            // Ignore common keys that are either duplicated or serve no real purpose
                            continue;
                        }

                        if (keyvalue.Children.Count > 0)
                        {
                            ProcessKey(keyName, keyvalue.Name, Utils.JsonifyKeyValue(keyvalue), true);
                        }
                        else if (!string.IsNullOrEmpty(keyvalue.Value))
                        {
                            ProcessKey(keyName, keyvalue.Name, keyvalue.Value);
                        }
                    }
                }
                else
                {
                    sectionName = string.Format("root_{0}", sectionName);

                    if (ProcessKey(sectionName, sectionName, Utils.JsonifyKeyValue(section), true) && sectionName.Equals("root_depots"))
                    {
                        DbConnection.Execute("UPDATE `Apps` SET `LastDepotUpdate` = CURRENT_TIMESTAMP() WHERE `AppID` = @AppID", new { AppID });
                    }
                }
            }
           
            foreach (var data in CurrentData)
            {
                if (!data.Key.StartsWith("website", StringComparison.Ordinal))
                {
                    DbConnection.Execute("DELETE FROM `AppsInfo` WHERE `AppID` = @AppID AND `Key` = @Key", new { AppID, data.Value.Key });

                    MakeHistory("removed_key", data.Value.Key, data.Value.Value);
                }
            }

            if (productInfo.KeyValues["common"]["name"].Value == null)
            {
                if (string.IsNullOrEmpty(app.Name)) // We don't have the app in our database yet
                {
                    DbConnection.Execute("INSERT INTO `Apps` (`AppID`, `Name`) VALUES (@AppID, @AppName) ON DUPLICATE KEY UPDATE `AppType` = `AppType`", new { AppID, AppName = string.Format("{0} {1}", SteamDB.UNKNOWN_APP, AppID) });
                }
                else if (!app.Name.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal)) // We do have the app, replace it with default name
                {
                    IRC.Instance.SendMain("App deleted: {0}{1}{2} -{3} {4}", Colors.BLUE, app.Name, Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetAppURL(AppID, "history"));

                    DbConnection.Execute("UPDATE `Apps` SET `Name` = @AppName, `AppType` = 0 WHERE `AppID` = @AppID", new { AppID, AppName = string.Format("{0} {1}", SteamDB.UNKNOWN_APP, AppID) });

                    MakeHistory("deleted_app", 0, app.Name);
                }
            }

            if (productInfo.KeyValues["depots"] != null)
            {
                Steam.Instance.DepotProcessor.Process(AppID, ChangeNumber, productInfo.KeyValues["depots"]);
            }
        }

        public void ProcessUnknown()
        {
            Log.WriteInfo("App Processor", "Unknown AppID: {0}", AppID);

            var name = DbConnection.ExecuteScalar<string>("SELECT `Name` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new { AppID });

            var data = CurrentData.Values.Where(x => !x.KeyName.StartsWith("website", StringComparison.Ordinal)).ToList();

            if (data.Any())
            {
                DbConnection.Execute(GetHistoryQuery(), data.Select(x => new PICSHistory
                {
                    ID = AppID,
                    ChangeID = ChangeNumber,
                    Key = x.Key,
                    OldValue = x.Value,
                    Action = "removed_key"
                }));
            }

            if (!string.IsNullOrEmpty(name) && !name.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal))
            {
                DbConnection.Execute(GetHistoryQuery(), new PICSHistory
                {
                    ID       = AppID,
                    ChangeID = ChangeNumber,
                    OldValue = name,
                    Action   = "deleted_app"
                });
            }

            DbConnection.Execute("DELETE FROM `Apps` WHERE `AppID` = @AppID", new { AppID });
            DbConnection.Execute("DELETE FROM `AppsInfo` WHERE `AppID` = @AppID", new { AppID });
            DbConnection.Execute("DELETE FROM `Store` WHERE `AppID` = @AppID", new { AppID });
        }
            
        private bool ProcessKey(string keyName, string displayName, string value, bool isJSON = false)
        {
            // All keys in PICS are supposed to be lower case
            keyName = keyName.ToLower().Trim();

            if (!CurrentData.ContainsKey(keyName))
            {
                uint key = GetKeyNameID(keyName);

                if (key == 0)
                {
                    var type = isJSON ? 86 : 0; // 86 is a hardcoded const for the website

                    DbConnection.Execute("INSERT INTO `KeyNames` (`Name`, `Type`, `DisplayName`) VALUES(@Name, @Type, @DisplayName) ON DUPLICATE KEY UPDATE `Type` = `Type`", new { Name = keyName, DisplayName = displayName, Type = type });

                    key = GetKeyNameID(keyName);

                    IRC.Instance.SendOps("New app keyname: {0}{1} {2}(ID: {3})", Colors.BLUE, displayName, Colors.LIGHTGRAY, key);

                    if (key == 0)
                    {
                        // We can't insert anything because key wasn't created
                        Log.WriteError("App Processor", "Failed to create key {0} for AppID {1}, not inserting info.", keyName, AppID);

                        return false;
                    }
                }

                InsertInfo(key, value);
                MakeHistory("created_key", key, string.Empty, value);

                return true;
            }

            var data = CurrentData[keyName];

            CurrentData.Remove(keyName);

            if (!data.Value.Equals(value))
            {
                InsertInfo(data.Key, value);
                MakeHistory("modified_key", data.Key, data.Value, value);

                return true;
            }

            return false;
        }

        private void InsertInfo(uint id, string value)
        {
            DbConnection.Execute("INSERT INTO `AppsInfo` VALUES (@AppID, @Key, @Value) ON DUPLICATE KEY UPDATE `Value` = @Value", new { AppID, Key = id, Value = value });
        }

        public static string GetHistoryQuery()
        {
            return "INSERT INTO `AppsHistory` (`ChangeID`, `AppID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES (@ChangeID, @ID, @Action, @Key, @OldValue, @NewValue)";
        }

        private void MakeHistory(string action, uint keyNameID = 0, string oldValue = "", string newValue = "")
        {
            DbConnection.Execute(GetHistoryQuery(),
                new PICSHistory
                {
                    ID       = AppID,
                    ChangeID = ChangeNumber,
                    Key      = keyNameID,
                    OldValue = oldValue,
                    NewValue = newValue,
                    Action   = action
                }
            );
        }

        private uint GetKeyNameID(string keyName)
        {
            return DbConnection.ExecuteScalar<uint>("SELECT `ID` FROM `KeyNames` WHERE `Name` = @keyName", new { keyName });
        }
    }
}
