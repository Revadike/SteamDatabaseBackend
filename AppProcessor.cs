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
        private const string DATABASE_NAME_TYPE = "10";
        private const string STEAMDB_UNKNOWN = "SteamDB Unknown App ";

        private Dictionary<string, string> appData = new Dictionary<string, string>();
        private uint ChangeNumber;
        private uint AppID;

        public AppProcessor(uint AppID)
        {
            this.AppID = AppID;
        }

        public void Process(SteamApps.PICSProductInfoCallback.PICSProductInfo ProductInfo)
        {
            ChangeNumber = ProductInfo.ChangeNumber;

#if DEBUG
            if (true)
#else
            if (Settings.Current.FullRun > 0)
#endif
            {
                Log.WriteDebug("App Processor", "AppID: {0}", AppID);
            }

            if (ProductInfo.KeyValues == null)
            {
                Log.WriteWarn("App Processor", "AppID {0} is empty, wot do I do?", AppID);
                return;
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `Value` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new MySqlParameter("AppID", AppID)))
            {
                while (Reader.Read())
                {
                    appData.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
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

            if (ProductInfo.KeyValues["common"]["name"].Value != null)
            {
                string newAppType = "0";
                string currentType = ProductInfo.KeyValues["common"]["type"].AsString().ToLower();

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

                if (appName.Equals(string.Empty) || appName.StartsWith(STEAMDB_UNKNOWN, StringComparison.Ordinal))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Apps` (`AppID`, `AppType`, `Name`) VALUES (@AppID, @Type, @AppName) ON DUPLICATE KEY UPDATE `Name` = @AppName, `AppType` = @Type",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Type", newAppType),
                                             new MySqlParameter("@AppName", ProductInfo.KeyValues["common"]["name"].Value)
                    );

                    MakeHistory("created_app");
                    MakeHistory("created_info", DATABASE_NAME_TYPE, string.Empty, ProductInfo.KeyValues["common"]["name"].Value, true);
                }
                else if (!appName.Equals(ProductInfo.KeyValues["common"]["name"].Value))
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `Name` = @AppName WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", ProductInfo.KeyValues["common"]["name"].Value)
                    );

                    MakeHistory("modified_info", DATABASE_NAME_TYPE, appName, ProductInfo.KeyValues["common"]["name"].Value, true);
                }

                if (appType.Equals("0"))
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `AppType` = @Type WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Type", newAppType)
                    );

                    MakeHistory("created_info", "9", string.Empty, newAppType, true);
                }
                else if (!appType.Equals(newAppType))
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `AppType` = @Type WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Type", newAppType)
                    );

                    MakeHistory("modified_info", "9", appType, newAppType, true);
                }
            }

            foreach (KeyValue section in ProductInfo.KeyValues.Children)
            {
                string sectionName = section.Name.ToLower();

                if (sectionName == "appid" || sectionName == "public_only")
                {
                    continue;
                }

                if (sectionName == "change_number") // Carefully handle change_number
                {
                    sectionName = "root_change_number";

                    ProcessKey(sectionName, "change_number", section.AsString());
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
                        // TODO: This is godlike hackiness
                        else if (keyName.StartsWith("extended_us ", StringComparison.Ordinal) ||
                                 keyName.StartsWith("extended_im ", StringComparison.Ordinal) ||
                                 keyName.StartsWith("extended_af ax al dz as ad ao ai aq ag ", StringComparison.Ordinal) ||
                                 keyName.Equals("extended_de") ||
                                 keyName.Equals("extended_jp") ||
                                 keyName.Equals("extended_cn") ||
                                 keyName.Equals("extended_us")
                        )
                        {
                            Log.WriteWarn("App Processor", "Dammit Valve, why these long keynames: {0} - {1} ", AppID, keyName);

                            continue;
                        }

                        string value = string.Empty;

                        if (keyvalue.Children.Count > 0)
                        {
                            if (keyName.Equals("common_languages"))
                            {
                                value = string.Join(",", keyvalue.Children.Select(x => x.Name));
                            }
                            else
                            {
                                value = DbWorker.JsonifyKeyValue(keyvalue);
                            }
                        }
                        else if (!string.IsNullOrEmpty(keyvalue.Value))
                        {
                            value = keyvalue.Value;
                        }

                        if (!value.Equals(string.Empty))
                        {
                            ProcessKey(keyName, keyvalue.Name, value);
                        }
                    }
                }
                else
                {
                    sectionName = string.Format("root_{0}", sectionName);

                    ProcessKey(sectionName, "jsonHack", DbWorker.JsonifyKeyValue(section));
                }
            }
           
            foreach (string key in appData.Keys)
            {
                if (!key.StartsWith("website", StringComparison.Ordinal))
                {
                    DbWorker.ExecuteNonQuery("DELETE FROM AppsInfo WHERE `AppID` = @AppID AND `Key` = (SELECT ID from KeyNames WHERE Name = @KeyName LIMIT 1)",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@KeyName", key)
                    );

                    MakeHistory("removed_key", key, appData[key], string.Empty);
                }
            }

            if (ProductInfo.KeyValues["common"]["name"].Value == null)
            {
                if (appName.Equals(string.Empty)) // We don't have the app in our database yet
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Apps` (`AppID`, `Name`) VALUES (@AppID, @AppName)",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", STEAMDB_UNKNOWN + AppID)
                    );
                }
                else if (!appName.StartsWith(STEAMDB_UNKNOWN, StringComparison.Ordinal)) // We do have the app, but it has a default name
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `Name` = @AppName, `AppType` = 0 WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", STEAMDB_UNKNOWN + AppID)
                    );

                    MakeHistory("deleted_app", "0", appName, string.Empty, true);
                }
            }
        }

        public void ProcessUnknown()
        {
            Log.WriteInfo("App Processor", "Unknown AppID: {0}", AppID);

            string AppName;

            using (MySqlDataReader MainReader = DbWorker.ExecuteReader("SELECT `Name` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new MySqlParameter("AppID", AppID)))
            {
                if (!MainReader.Read())
                {
                    return;
                }

                AppName = DbWorker.GetString("Name", MainReader);
            }

            string key;

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `Value` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new MySqlParameter("AppID", AppID)))
            {
                while (Reader.Read())
                {
                    key = DbWorker.GetString("Name", Reader);

                    if (!key.StartsWith("website", StringComparison.Ordinal))
                    {
                        MakeHistory("removed_key", key, DbWorker.GetString("Value", Reader), string.Empty);
                    }
                }
            }

            DbWorker.ExecuteNonQuery("DELETE FROM `Apps` WHERE `AppID` = @AppID", new MySqlParameter("@AppID", AppID));
            DbWorker.ExecuteNonQuery("DELETE FROM `AppsInfo` WHERE `AppID` = @AppID", new MySqlParameter("@AppID", AppID));
            DbWorker.ExecuteNonQuery("DELETE FROM `Store` WHERE `AppID` = @AppID", new MySqlParameter("@AppID", AppID));

            if (!AppName.StartsWith(STEAMDB_UNKNOWN, StringComparison.Ordinal))
            {
                MakeHistory("deleted_app", "0", AppName, string.Empty, true);
            }
        }

        private void ProcessKey(string keyName, string displayName, string value)
        {
            // All keys in PICS are supposed to be lower case
            keyName = keyName.ToLower();

            if (!appData.ContainsKey(keyName))
            {
                string ID = string.Empty;

                // Try to get ID from database to prevent autoindex bugginess and for faster performance (select > insert)
                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ID` FROM `KeyNames` WHERE `Name` = @KeyName LIMIT 1", new MySqlParameter("KeyName", keyName)))
                {
                    if (Reader.Read())
                    {
                        ID = DbWorker.GetString("ID", Reader);
                    }
                }

                if (ID.Equals(string.Empty))
                {
                    if (displayName.Equals("jsonHack"))
                    {
                        const uint DB_TYPE_JSON = 86;

                        DbWorker.ExecuteNonQuery("INSERT INTO `KeyNames` (`Name`, `Type`) VALUES(@Name, @Type) ON DUPLICATE KEY UPDATE `Type` = `Type`",
                                                 new MySqlParameter("@Name", keyName),
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
                }

                MakeAppsInfo(keyName, value, ID);
                MakeHistory("created_key", keyName, string.Empty, value);
            }
            else if (!appData[keyName].Equals(value))
            {
                MakeAppsInfo(keyName, value);
                MakeHistory("modified_key", keyName, appData[keyName], value);
            }

            appData.Remove(keyName);
        }

        private void MakeAppsInfo(string KeyName = "", string Value = "", string ID = "")
        {
            // If ID is passed, we don't have to make a subquery
            if (ID.Equals(string.Empty))
            {
                DbWorker.ExecuteNonQuery("INSERT INTO `AppsInfo` VALUES (@AppID, (SELECT `ID` FROM `KeyNames` WHERE `Name` = @KeyName LIMIT 1), @Value) ON DUPLICATE KEY UPDATE `Value` = @Value",
                                         new MySqlParameter("@AppID", AppID),
                                         new MySqlParameter("@KeyName", KeyName),
                                         new MySqlParameter("@Value", Value)
                );
            }
            else
            {
                DbWorker.ExecuteNonQuery("INSERT INTO `AppsInfo` VALUES (@AppID, @ID, @Value) ON DUPLICATE KEY UPDATE `Value` = @Value",
                                         new MySqlParameter("@AppID", AppID),
                                         new MySqlParameter("@ID", ID),
                                         new MySqlParameter("@Value", Value)
                );
            }
        }

        private void MakeHistory(string Action, string KeyName = "", string OldValue = "", string NewValue = "", bool keyoverride = false)
        {
            string query = "INSERT INTO `AppsHistory` (`ChangeID`, `AppID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES ";

            if (keyoverride || KeyName.Equals(string.Empty))
            {
                query += "(@ChangeID, @AppID, @Action, @KeyName, @OldValue, @NewValue)";
            }
            else
            {
                query += "(@ChangeID, @AppID, @Action, (SELECT `ID` FROM `KeyNames` WHERE `Name` = @KeyName LIMIT 1), @OldValue, @NewValue)";
            }

            DbWorker.ExecuteNonQuery(query,
                                     new MySqlParameter("@AppID", AppID),
                                     new MySqlParameter("@ChangeID", ChangeNumber),
                                     new MySqlParameter("@Action", Action),
                                     new MySqlParameter("@KeyName", KeyName),
                                     new MySqlParameter("@OldValue", OldValue),
                                     new MySqlParameter("@NewValue", NewValue)
            );
        }
    }
}
