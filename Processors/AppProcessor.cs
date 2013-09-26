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
        private const string STEAMDB_UNKNOWN  = "SteamDB Unknown App ";

        private Dictionary<string, string> CurrentData = new Dictionary<string, string>();
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
                    MakeHistory("created_info", DATABASE_NAME_TYPE, string.Empty, ProductInfo.KeyValues["common"]["name"].Value);
                }
                else if (!appName.Equals(ProductInfo.KeyValues["common"]["name"].Value))
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `Name` = @AppName WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", ProductInfo.KeyValues["common"]["name"].Value)
                    );

                    MakeHistory("modified_info", DATABASE_NAME_TYPE, appName, ProductInfo.KeyValues["common"]["name"].Value);
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
            bool depotsSectionModified = Settings.Current.FullRun == 2;

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

                    if (ProcessKey(sectionName, "jsonHack", DbWorker.JsonifyKeyValue(section)) && sectionName.Equals("root_depots"))
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

            if (ProductInfo.KeyValues["common"]["name"].Value == null)
            {
                if (appName.Equals(string.Empty)) // We don't have the app in our database yet
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Apps` (`AppID`, `Name`) VALUES (@AppID, @AppName)",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", STEAMDB_UNKNOWN + AppID)
                    );
                }
                else if (!appName.StartsWith(STEAMDB_UNKNOWN, StringComparison.Ordinal)) // We do have the app, replace it with default name
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `Name` = @AppName, `AppType` = 0 WHERE `AppID` = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", STEAMDB_UNKNOWN + AppID)
                    );

                    MakeHistory("deleted_app", 0, appName);
                }
            }

#if DEBUG
            if (depotsSectionModified)
            {
                DepotProcessor.Process(AppID, ChangeNumber, ProductInfo.KeyValues["depots"]);
            }
#endif
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

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `Key`, `Value` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new MySqlParameter("AppID", AppID)))
            {
                while (Reader.Read())
                {
                    if (!DbWorker.GetString("Name", Reader).StartsWith("website", StringComparison.Ordinal))
                    {
                        MakeHistory("removed_key", Reader.GetUInt32("ID"), DbWorker.GetString("Value", Reader));
                    }
                }
            }

            DbWorker.ExecuteNonQuery("DELETE FROM `Apps` WHERE `AppID` = @AppID", new MySqlParameter("@AppID", AppID));
            DbWorker.ExecuteNonQuery("DELETE FROM `AppsInfo` WHERE `AppID` = @AppID", new MySqlParameter("@AppID", AppID));
            DbWorker.ExecuteNonQuery("DELETE FROM `Store` WHERE `AppID` = @AppID", new MySqlParameter("@AppID", AppID));

            if (!AppName.StartsWith(STEAMDB_UNKNOWN, StringComparison.Ordinal))
            {
                MakeHistory("deleted_app", 0, AppName);
            }
        }

        private bool ProcessKey(string keyName, string displayName, string value)
        {
            // All keys in PICS are supposed to be lower case
            keyName = keyName.ToLower();

            if (!CurrentData.ContainsKey(keyName))
            {
                uint ID = GetKeyNameID(keyName);

                if (ID == 0)
                {
                    if (displayName.Equals("jsonHack"))
                    {
                        const uint DB_TYPE_JSON = 86;

                        DbWorker.ExecuteNonQuery("INSERT INTO `KeyNames` (`Name`, `Type`, `DisplayName`) VALUES(@Name, @Type, @DisplayName) ON DUPLICATE KEY UPDATE `Type` = `Type`",
                                                 new MySqlParameter("@Name", keyName),
                                                 new MySqlParameter("@DisplayName", keyName),
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
                }

                InsertInfo(ID, value);
                MakeHistory("created_key", ID, string.Empty, value);

                return true;
            }
            else if (!CurrentData[keyName].Equals(value))
            {
                uint ID = GetKeyNameID(keyName);

                InsertInfo(ID, value);
                MakeHistory("modified_key", ID, CurrentData[keyName], value);

                return true;
            }

            CurrentData.Remove(keyName);

            return false;
        }

        private void InsertInfo(uint ID, string Value)
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `AppsInfo` VALUES (@AppID, @KeyNameID, @Value) ON DUPLICATE KEY UPDATE `Value` = @Value",
                                     new MySqlParameter("@AppID", AppID),
                                     new MySqlParameter("@KeyNameID", ID),
                                     new MySqlParameter("@Value", Value)
            );
        }

        private void MakeHistory(string Action, uint KeyNameID = 0, string OldValue = "", string NewValue = "")
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `AppsHistory` (`ChangeID`, `AppID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES (@ChangeID, @AppID, @Action, @KeyNameID, @OldValue, @NewValue)",
                                     new MySqlParameter("@AppID", AppID),
                                     new MySqlParameter("@ChangeID", ChangeNumber),
                                     new MySqlParameter("@Action", Action),
                                     new MySqlParameter("@KeyNameID", KeyNameID),
                                     new MySqlParameter("@OldValue", OldValue),
                                     new MySqlParameter("@NewValue", NewValue)
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
