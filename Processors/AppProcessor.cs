/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class AppProcessor
    {
        private const uint DATABASE_APPTYPE   = 9;
        private const uint DATABASE_NAME_TYPE = 10;

        private Dictionary<string, string> CurrentData = new Dictionary<string, string>();
        private uint ChangeNumber;
        private readonly uint AppID;

        public AppProcessor(uint appID)
        {
            this.AppID = appID;
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

            bool depotsSectionModified = false;

            using (var reader = DbWorker.ExecuteReader("SELECT `Name`, `Value` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new MySqlParameter("AppID", AppID)))
            {
                while (reader.Read())
                {
                    CurrentData.Add(reader.GetString("Name"), reader.GetString("Value"));
                }
            }

            string appName = string.Empty;
            uint appType = 0;

            using (var reader = DbWorker.ExecuteReader("SELECT `Name`, `AppType` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new MySqlParameter("AppID", AppID)))
            {
                if (reader.Read())
                {
                    appName = reader.GetString("Name");
                    appType = reader.GetUInt32("AppType");
                }
            }

            if (productInfo.KeyValues["common"]["name"].Value != null)
            {
                uint newAppType = 0;
                string currentType = productInfo.KeyValues["common"]["type"].AsString().ToLower();

                using (var reader = DbWorker.ExecuteReader("SELECT `AppType` FROM `AppsTypes` WHERE `Name` = @Type LIMIT 1", new MySqlParameter("Type", currentType)))
                {
                    if (reader.Read())
                    {
                        newAppType = reader.GetUInt32("AppType");
                    }
                    else
                    {
                        // TODO: Create it?
                        Log.WriteError("App Processor", "AppID {0} - unknown app type: {1}", AppID, currentType);

                        IRC.Instance.SendOps("New app type: {0}{1}{2} for app {3}{4}{5}", Colors.BLUE, currentType, Colors.NORMAL, Colors.BLUE, AppID, Colors.NORMAL);
                    }
                }

                if (string.IsNullOrEmpty(appName) || appName.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Apps` (`AppID`, `AppType`, `Name`, `LastKnownName`) VALUES (@AppID, @Type, @AppName, @AppName) ON DUPLICATE KEY UPDATE `Name` = @AppName, `AppType` = @Type",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Type", newAppType),
                                             new MySqlParameter("@AppName", productInfo.KeyValues["common"]["name"].Value)
                    );

                    MakeHistory("created_app");
                    MakeHistory("created_info", DATABASE_NAME_TYPE, string.Empty, productInfo.KeyValues["common"]["name"].Value);

                    // TODO: Testy testy
                    if (!Settings.IsFullRun
                    &&  Settings.Current.ChatRooms.Count > 0
                    &&  !appName.StartsWith("SteamApp", StringComparison.Ordinal)
                    &&  !appName.StartsWith("ValveTest", StringComparison.Ordinal))
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
                else if (!appName.Equals(productInfo.KeyValues["common"]["name"].Value))
                {
                    string newAppName = productInfo.KeyValues["common"]["name"].AsString();

                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `Name` = @AppName WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", newAppName)
                    );

                    MakeHistory("modified_info", DATABASE_NAME_TYPE, appName, newAppName);
                }

                if (appType == 0 || appType != newAppType)
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `AppType` = @Type WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Type", newAppType)
                    );

                    if (appType == 0)
                    {
                        MakeHistory("created_info", DATABASE_APPTYPE, string.Empty, newAppType.ToString());
                    }
                    else
                    {
                        MakeHistory("modified_info", DATABASE_APPTYPE, appType.ToString(), newAppType.ToString());
                    }
                }
            }

            foreach (KeyValue section in productInfo.KeyValues.Children)
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
                            if (keyName.Equals("common_languages"))
                            {
                                ProcessKey(keyName, keyvalue.Name, string.Join(",", keyvalue.Children.Select(x => x.Name)));
                            }
                            else
                            {
                                ProcessKey(keyName, keyvalue.Name, Utils.JsonifyKeyValue(keyvalue), true);
                            }
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
                        depotsSectionModified = true;
                    }
                }
            }
           
            foreach (string keyName in CurrentData.Keys)
            {
                if (!keyName.StartsWith("website", StringComparison.Ordinal))
                {
                    uint ID = GetKeyNameID(keyName);

                    DbWorker.ExecuteNonQuery("DELETE FROM AppsInfo WHERE `AppID` = @AppID AND `Key` = @KeyNameID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@KeyNameID", ID)
                    );

                    MakeHistory("removed_key", ID, CurrentData[keyName]);
                }
            }

            if (productInfo.KeyValues["common"]["name"].Value == null)
            {
                if (string.IsNullOrEmpty(appName)) // We don't have the app in our database yet
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Apps` (`AppID`, `Name`) VALUES (@AppID, @AppName) ON DUPLICATE KEY UPDATE `AppType` = `AppType`",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", string.Format("{0} {1}", SteamDB.UNKNOWN_APP, AppID))
                    );
                }
                else if (!appName.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal)) // We do have the app, replace it with default name
                {
                    IRC.Instance.SendMain("App deleted: {0}{1}{2} -{3} {4}", Colors.BLUE, Steam.GetAppName(AppID), Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetAppURL(AppID, "history"));

                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `Name` = @AppName, `AppType` = 0 WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", string.Format("{0} {1}", SteamDB.UNKNOWN_APP, AppID))
                    );

                    MakeHistory("deleted_app", 0, appName);
                }
            }

            if (depotsSectionModified || (Settings.IsFullRun && productInfo.KeyValues["depots"].Children.Count > 0))
            {
                if (depotsSectionModified)
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `LastDepotUpdate` = CURRENT_TIMESTAMP() WHERE `AppID` = @AppID",
                        new MySqlParameter("@AppID", AppID)
                    );
                }

                Steam.Instance.DepotProcessor.Process(AppID, ChangeNumber, productInfo.KeyValues["depots"]);
            }
        }

        public void ProcessUnknown()
        {
            Log.WriteInfo("App Processor", "Unknown AppID: {0}", AppID);

            try
            {
                TryProcessUnknown();
            }
            catch (Exception e)
            {
                Log.WriteError("App Processor", "Caught exception while processing unknown app {0}: {1}\n{2}", AppID, e.Message, e.StackTrace);
            }
        }

        private void TryProcessUnknown()
        {
            string AppName;

            using (var reader = DbWorker.ExecuteReader("SELECT `Name` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new MySqlParameter("AppID", AppID)))
            {
                if (!reader.Read())
                {
                    return;
                }

                AppName = reader.GetString("Name");
            }

            bool historyChanged = false;

            using (var reader = DbWorker.ExecuteReader("SELECT `Name`, `Key`, `Value` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new MySqlParameter("AppID", AppID)))
            {
                while (reader.Read())
                {
                    if (!reader.GetString("Name").StartsWith("website", StringComparison.Ordinal))
                    {
                        MakeHistory("removed_key", reader.GetUInt32("Key"), reader.GetString("Value"));

                        historyChanged = true;
                    }
                }
            }

            DbWorker.ExecuteNonQuery("DELETE FROM `Apps` WHERE `AppID` = @AppID", new MySqlParameter("@AppID", AppID));
            DbWorker.ExecuteNonQuery("DELETE FROM `AppsInfo` WHERE `AppID` = @AppID", new MySqlParameter("@AppID", AppID));
            DbWorker.ExecuteNonQuery("DELETE FROM `Store` WHERE `AppID` = @AppID", new MySqlParameter("@AppID", AppID));

            if (!AppName.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal))
            {
                MakeHistory("deleted_app", 0, AppName);

                historyChanged = true;
            }

            // TODO: This is a dirty hack so we somehow track these app changes
            if (!historyChanged && !Settings.IsFullRun)
            {
                MakeHistory("removed_key", GetKeyNameID("root_change_number"), "0", "0");
            }
        }

        private bool ProcessKey(string keyName, string displayName, string value, bool isJSON = false)
        {
            // All keys in PICS are supposed to be lower case
            keyName = keyName.ToLower().Trim();

            if (!CurrentData.ContainsKey(keyName))
            {
                uint ID = GetKeyNameID(keyName);

                if (ID == 0)
                {
                    if (isJSON)
                    {
                        const uint DB_TYPE_JSON = 86;

                        DbWorker.ExecuteNonQuery("INSERT INTO `KeyNames` (`Name`, `Type`, `DisplayName`) VALUES(@Name, @Type, @DisplayName) ON DUPLICATE KEY UPDATE `Type` = `Type`",
                                                 new MySqlParameter("@Name", keyName),
                                                 new MySqlParameter("@DisplayName", displayName),
                                                 new MySqlParameter("@Type", DB_TYPE_JSON)
                        );
                    }
                    else
                    {
                        DbWorker.ExecuteNonQuery("INSERT INTO `KeyNames` (`Name`, `DisplayName`) VALUES(@Name, @DisplayName) ON DUPLICATE KEY UPDATE `Type` = `Type`",
                                                 new MySqlParameter("@Name", keyName),
                                                 new MySqlParameter("@DisplayName", displayName)
                        );
                    }

                    ID = GetKeyNameID(keyName);

                    IRC.Instance.SendOps("New app keyname: {0}{1} {2}(ID: {3})", Colors.BLUE, displayName, Colors.LIGHTGRAY, ID);

                    if (ID == 0)
                    {
                        // We can't insert anything because key wasn't created
                        Log.WriteError("App Processor", "Failed to create key {0} for AppID {1}, not inserting info.", keyName, AppID);

                        return false;
                    }
                }

                InsertInfo(ID, value);
                MakeHistory("created_key", ID, string.Empty, value);

                return true;
            }

            string currentValue = CurrentData[keyName];

            CurrentData.Remove(keyName);

            if (!currentValue.Equals(value))
            {
                uint ID = GetKeyNameID(keyName);

                InsertInfo(ID, value);
                MakeHistory("modified_key", ID, currentValue, value);

                return true;
            }

            return false;
        }

        private void InsertInfo(uint id, string value)
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `AppsInfo` VALUES (@AppID, @KeyNameID, @Value) ON DUPLICATE KEY UPDATE `Value` = @Value",
                                     new MySqlParameter("@AppID", AppID),
                                     new MySqlParameter("@KeyNameID", id),
                                     new MySqlParameter("@Value", value)
            );
        }

        private void MakeHistory(string action, uint keyNameID = 0, string oldValue = "", string newValue = "")
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `AppsHistory` (`ChangeID`, `AppID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES (@ChangeID, @AppID, @Action, @KeyNameID, @OldValue, @NewValue)",
                                     new MySqlParameter("@AppID", AppID),
                                     new MySqlParameter("@ChangeID", ChangeNumber),
                                     new MySqlParameter("@Action", action),
                                     new MySqlParameter("@KeyNameID", keyNameID),
                                     new MySqlParameter("@OldValue", oldValue),
                                     new MySqlParameter("@NewValue", newValue)
            );
        }

        public static void MakeHistory(uint appID, uint changeNumber, string action, uint keyNameID = 0, string oldValue = "", string newValue = "")
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `AppsHistory` (`ChangeID`, `AppID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES (@ChangeID, @AppID, @Action, @KeyNameID, @OldValue, @NewValue)",
                new MySqlParameter("@AppID", appID),
                new MySqlParameter("@ChangeID", changeNumber),
                new MySqlParameter("@Action", action),
                new MySqlParameter("@KeyNameID", keyNameID),
                new MySqlParameter("@OldValue", oldValue),
                new MySqlParameter("@NewValue", newValue)
            );
        }

        private static uint GetKeyNameID(string keyName)
        {
            using (var db = Database.GetConnection())
            {
                return db.ExecuteScalar<uint>("SELECT `ID` FROM `KeyNames` WHERE `Name` = @keyName", new { keyName });
            }
        }
    }
}
