/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public class AppProcessor
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
            if (Settings.Current.FullRun > 0)
#endif
            {
                Log.WriteDebug("App Processor", "AppID: {0}", AppID);
            }

            try
            {
                TryProcess(productInfo);
            }
            catch (Exception e)
            {
                Log.WriteError("App Processor", "Caught exception while processing app {0}: {1}\n{2}", AppID, e.Message, e.StackTrace);
            }
        }

        private void TryProcess(SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo)
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `Value` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new MySqlParameter("AppID", AppID)))
            {
                while (Reader.Read())
                {
                    CurrentData.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
                }
            }

            string appName = string.Empty;
            string appType = "0";

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `AppType` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new MySqlParameter("AppID", AppID)))
            {
                if (Reader.Read())
                {
                    appName = DbWorker.GetString("Name", Reader);
                    appType = DbWorker.GetString("AppType", Reader);
                }
            }

            if (productInfo.KeyValues["common"]["name"].Value != null)
            {
                string newAppType = "0";
                string currentType = productInfo.KeyValues["common"]["type"].AsString().ToLower();

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppType` FROM `AppsTypes` WHERE `Name` = @Type LIMIT 1", new MySqlParameter("Type", currentType)))
                {
                    if (Reader.Read())
                    {
                        newAppType = DbWorker.GetString("AppType", Reader);
                    }
                    else
                    {
                        // TODO: Create it?
                        Log.WriteError("App Processor", "AppID {0} - unknown app type: {1}", AppID, currentType);

                        // TODO: This is debuggy just so we are aware of new app types
                        IRC.SendAnnounce("Unknown app type \"{0}\" for appid {1}, cc Alram and xPaw", currentType, AppID);
                    }
                }

                if (string.IsNullOrEmpty(appName) || appName.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Apps` (`AppID`, `AppType`, `Name`) VALUES (@AppID, @Type, @AppName) ON DUPLICATE KEY UPDATE `Name` = @AppName, `AppType` = @Type",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Type", newAppType),
                                             new MySqlParameter("@AppName", productInfo.KeyValues["common"]["name"].Value)
                    );

                    MakeHistory("created_app");
                    MakeHistory("created_info", DATABASE_NAME_TYPE, string.Empty, productInfo.KeyValues["common"]["name"].Value);

                    // TODO: Testy testy
                    if (Settings.Current.FullRun == 0
                    &&  Settings.Current.ChatRooms.Count > 0
                    &&  !appName.StartsWith("SteamApp", StringComparison.Ordinal)
                    &&  !appName.StartsWith("ValveTest", StringComparison.Ordinal))
                    {
                        Steam.Instance.Friends.SendChatRoomMessage(Settings.Current.ChatRooms[0], EChatEntryType.ChatMsg, string.Format(":retreat: New {0} was published: {1} - {2}", currentType, productInfo.KeyValues["common"]["name"].AsString(), SteamDB.GetAppURL(AppID)));
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

                    // TODO: Testy testy
                    if (Settings.Current.FullRun == 0
                    &&  Settings.Current.ChatRooms.Count > 0
                    &&  !string.Equals(appName, newAppName, StringComparison.OrdinalIgnoreCase)
                    &&  !newAppName.StartsWith("SteamApp", StringComparison.Ordinal)
                    &&  !newAppName.StartsWith("ValveTest", StringComparison.Ordinal))
                    {
                        Steam.Instance.Friends.SendChatRoomMessage(Settings.Current.ChatRooms[0], EChatEntryType.ChatMsg, string.Format(":retreat: {0} name was changed - {1}\n« {2}\n» {3}", currentType, SteamDB.GetAppURL(AppID, "history"), appName, newAppName));
                    }
                }

                if (appType.Equals("0"))
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `AppType` = @Type WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Type", newAppType)
                    );

                    MakeHistory("created_info", DATABASE_APPTYPE, string.Empty, newAppType);
                }
                else if (!appType.Equals(newAppType))
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `AppType` = @Type WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Type", newAppType)
                    );

                    MakeHistory("modified_info", DATABASE_APPTYPE, appType, newAppType);
                }
            }

            // If we are full running with unknowns, process depots too
            bool depotsSectionModified = Settings.Current.FullRun >= 2 && productInfo.KeyValues["depots"].Children.Count > 0;

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
                                ProcessKey(keyName, keyvalue.Name, DbWorker.JsonifyKeyValue(keyvalue), true);
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

                    if (ProcessKey(sectionName, sectionName, DbWorker.JsonifyKeyValue(section), true) && sectionName.Equals("root_depots"))
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
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `Name` = @AppName, `AppType` = 0 WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", string.Format("{0} {1}", SteamDB.UNKNOWN_APP, AppID))
                    );

                    MakeHistory("deleted_app", 0, appName);
                }
            }

            if (depotsSectionModified)
            {
                DepotProcessor.Process(AppID, ChangeNumber, productInfo.KeyValues["depots"]);
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

            using (MySqlDataReader MainReader = DbWorker.ExecuteReader("SELECT `Name` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new MySqlParameter("AppID", AppID)))
            {
                if (!MainReader.Read())
                {
                    return;
                }

                AppName = DbWorker.GetString("Name", MainReader);
            }

            bool historyChanged = false;

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `Key`, `Value` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new MySqlParameter("AppID", AppID)))
            {
                while (Reader.Read())
                {
                    if (!DbWorker.GetString("Name", Reader).StartsWith("website", StringComparison.Ordinal))
                    {
                        MakeHistory("removed_key", Reader.GetUInt32("Key"), DbWorker.GetString("Value", Reader));

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
            if (!historyChanged && Settings.Current.FullRun == 0)
            {
                MakeHistory("removed_key", GetKeyNameID("root_change_number"), "0", "0");
            }
        }

        private bool ProcessKey(string keyName, string displayName, string value, bool isJSON = false)
        {
            // All keys in PICS are supposed to be lower case
            keyName = keyName.ToLower();

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
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ID` FROM `KeyNames` WHERE `Name` = @KeyName LIMIT 1", new MySqlParameter("KeyName", keyName)))
            {
                if (Reader.Read())
                {
                    return Reader.GetUInt32("ID");
                }
            }

            return 0;
        }
    }
}
