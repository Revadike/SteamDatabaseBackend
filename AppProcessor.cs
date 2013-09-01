/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using SteamKit2;
using System.Linq;

namespace PICSUpdater
{
    class AppProcessor
    {
        public void Process(uint AppID, SteamApps.PICSProductInfoCallback.PICSProductInfo ProductInfo)
        {
#if DEBUG
            if (true)
#else
            if (Program.fullRunOption > 0)
#endif
            {
                Log.WriteInfo("App Processor", "AppID: {0}", AppID);
            }

            if (ProductInfo.KeyValues == null)
            {
                Log.WriteWarn("App Processor", "AppID {0} is empty, wot do I do?", AppID);
                return;
            }

            Dictionary<string, string> appdata = new Dictionary<string, string>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `Name`, `Value` FROM AppsInfo INNER JOIN KeyNames ON AppsInfo.Key=KeyNames.ID WHERE AppID = @AppID", new MySqlParameter("AppID", AppID)))
            {
                while (Reader.Read())
                {
                    appdata.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
                }
            }

            string AppName = "";
            string AppType = "";

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `Name`, `AppType` FROM Apps WHERE AppID = @AppID", new MySqlParameter("AppID", AppID)))
            {
                if (Reader.Read())
                {
                    AppName = DbWorker.GetString("Name", Reader);
                    AppType = DbWorker.GetString("AppType", Reader);
                }
            }

            if (ProductInfo.KeyValues["common"]["name"].Value != null)
            {
                string newAppType = "0";

                if (!ProductInfo.KeyValues["common"]["type"].Value.Equals(""))
                {
                    using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT AppType FROM AppsTypes WHERE Name = @type LIMIT 1", new MySqlParameter("type", ProductInfo.KeyValues["common"]["type"].Value)))
                    {
                        if (Reader.Read())
                        {
                            newAppType = DbWorker.GetString("AppType", Reader);
                        }
                    }
                }

                if (AppName.Equals("") || AppName.StartsWith("SteamDB Unknown App"))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO Apps (AppID, AppType, Name) VALUES (@AppID, @Type, @AppName) ON DUPLICATE KEY UPDATE `Name` = @AppName, `AppType` = @Type",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Type", newAppType),
                                             new MySqlParameter("@AppName", ProductInfo.KeyValues["common"]["name"].Value)
                    );

                    MakeHistory(AppID, ProductInfo.ChangeNumber, "created_app");
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "created_info", "10", "", ProductInfo.KeyValues["common"]["name"].Value, true);
                }
                else if (!AppName.Equals(ProductInfo.KeyValues["common"]["name"].Value))
                {
                    DbWorker.ExecuteNonQuery("UPDATE Apps SET Name = @AppName WHERE AppID = @AppID", new MySqlParameter("@AppID", AppID), new MySqlParameter("@AppName", ProductInfo.KeyValues["common"]["name"].Value));
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "modified_info", "10", AppName, ProductInfo.KeyValues["common"]["name"].Value, true);
                }

                if (AppType.Equals("") || AppType.Equals("0"))
                {
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "created_info", "9", "", newAppType, true);
                    DbWorker.ExecuteNonQuery("UPDATE Apps SET AppType = @type WHERE AppID = @AppID", new MySqlParameter("@AppID", AppID), new MySqlParameter("@type", newAppType));
                }
                else if (!AppType.Equals(newAppType))
                {
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "modified_info", "9", AppType, newAppType, true);
                    DbWorker.ExecuteNonQuery("UPDATE Apps SET AppType = @type WHERE AppID = @AppID", new MySqlParameter("@AppID", AppID), new MySqlParameter("@type", newAppType));
                }
            }

            #region HugeQuery
            foreach (KeyValue section in ProductInfo.KeyValues.Children)
            {
                string sectionName = section.Name.ToLower();

                if (sectionName == "appid" || sectionName == "public_only")
                {
                    continue;
                }

                if (sectionName == "change_number") // Carefully handle change_number
                {
                    sectionName = "marlamin_change_number";

                    ProcessKey(AppID, ProductInfo.ChangeNumber, appdata, "marlamin_change_number", "change_number", section.AsString());

                    appdata.Remove(sectionName);
                }
                else if (sectionName == "common" || sectionName == "extended")
                {
                    string keyName = "";

                    foreach (KeyValue keyvalue in section.Children)
                    {
                        keyName = string.Format("{0}_{1}", sectionName, keyvalue.Name);

                        if (keyName.Equals("common_type") || keyName.Equals("common_gameid") || keyName.Equals("common_name") || keyName.Equals("extended_order"))
                        {
                            // Ignore common keys that are either duplicated or serve no real purpose
                            continue;
                        }
                        // TODO: This is godlike hackiness
                        else if
                        (
                            keyName.StartsWith("extended_us ") ||
                            keyName.StartsWith("extended_im ") ||
                            keyName.StartsWith("extended_af ax al dz as ad ao ai aq ag ") ||
                            keyName.Equals("extended_de") ||
                            keyName.Equals("extended_jp") ||
                            keyName.Equals("extended_cn") ||
                            keyName.Equals("extended_us")
                        )
                        {
                            Log.WriteWarn("App Processor", "Dammit Valve, why these long keynames: {0} - {1} ", AppID, keyName);

                            continue;
                        }

                        string value = "";

                        if(keyvalue.Children.Count > 0)
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
                        else if (!keyvalue.Value.Equals(""))
                        {
                            value = keyvalue.Value;
                        }

                        if(!value.Equals(""))
                        {
                            ProcessKey(AppID, ProductInfo.ChangeNumber, appdata, keyName, keyvalue.Name, value);

                            appdata.Remove(keyName);
                        }
                    }
                }
                else
                {
                    sectionName = string.Format("marlamin_{0}", sectionName);

                    ProcessKey(AppID, ProductInfo.ChangeNumber, appdata, sectionName, "jsonHack", DbWorker.JsonifyKeyValue(section));

                    appdata.Remove(sectionName);
                }
            }
            #endregion

            foreach (string key in appdata.Keys)
            {
                if (!key.StartsWith("website"))
                {
                    DbWorker.ExecuteNonQuery("DELETE FROM AppsInfo WHERE `AppID` = @AppID AND `Key` = (SELECT ID from KeyNames WHERE Name = @KeyName LIMIT 1)",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@KeyName", key)
                    );

                    MakeHistory(AppID, ProductInfo.ChangeNumber, "removed_key", key, appdata[key], "");
                }
            }

            if (ProductInfo.KeyValues["common"]["name"].Value == null)
            {
                if (AppName.Equals("")) // We never knew it
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO Apps (AppID, Name) VALUES (@AppID, @AppName)",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", "SteamDB Unknown App " + AppID)
                    );
                }
                else if (!AppName.StartsWith("SteamDB Unknown App")) // App name is not empty in db
                {
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "deleted_app", "0", AppName, "", true);

                    DbWorker.ExecuteNonQuery("UPDATE Apps SET Name = @AppName, AppType = 0 WHERE AppID = @AppID",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@AppName", "SteamDB Unknown App " + AppID)
                    );
                }
            }
        }

        private static void ProcessKey(uint AppID, uint ChangeNumber, Dictionary<string, string> appData, string keyName, string displayName, string value)
        {
            if (!appData.ContainsKey(keyName))
            {
                if (displayName.Equals("jsonHack"))
                {
                    const uint DB_TYPE_JSON = 86; // TODO: Verify this

                    DbWorker.ExecuteNonQuery("INSERT INTO KeyNames(`Name`, `Type`) VALUES(@Name, @Type) ON DUPLICATE KEY UPDATE `ID` = `ID`",
                                             new MySqlParameter("@Name", keyName),
                                             new MySqlParameter("@Type", DB_TYPE_JSON)
                    );
                }
                else
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO KeyNames(`Name`, `DisplayName`) VALUES(@Name, @DisplayName) ON DUPLICATE KEY UPDATE `ID` = `ID`",
                                             new MySqlParameter("@Name", keyName),
                                             new MySqlParameter("@DisplayName", displayName)
                    );
                }

                MakeAppsInfo(AppID, keyName, value);
                MakeHistory(AppID, ChangeNumber, "created_key", keyName, "", value);
            }
            else if (!appData[keyName].Equals(value))
            {
                MakeAppsInfo(AppID, keyName, value);
                MakeHistory(AppID, ChangeNumber, "modified_key", keyName, appData[keyName], value);
            }
        }

        private static void MakeAppsInfo(uint AppID, string KeyName = "", string Value = "")
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `AppsInfo` VALUES (@AppID, (SELECT `ID` FROM `KeyNames` WHERE `Name` = @KeyName LIMIT 1), @Value) ON DUPLICATE KEY UPDATE `Value` = @Value",
                                     new MySqlParameter("@AppID", AppID),
                                     new MySqlParameter("@KeyName", KeyName),
                                     new MySqlParameter("@Value", Value)
            );
        }

        private static void MakeHistory(uint AppID, uint ChangeNumber, string Action, string KeyName = "", string OldValue = "", string NewValue = "", bool keyoverride = false)
        {
            string query = "INSERT INTO `AppsHistory` (`ChangeID`, `AppID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES ";

            if (keyoverride == true || KeyName.Equals(""))
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

        public void ProcessUnknown(uint AppID)
        {
            Log.WriteInfo("App Processor", "Unknown AppID: {0}", AppID);

            string AppName = "";

            using (MySqlDataReader MainReader = DbWorker.ExecuteReader(@"SELECT `Name` FROM Apps WHERE AppID = @AppID", new MySqlParameter("AppID", AppID)))
            {
                if (!MainReader.Read())
                {
                    return;
                }

                AppName = DbWorker.GetString("Name", MainReader);
            }

            Dictionary<string, string> appdata = new Dictionary<string, string>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `Name`, `Value` FROM AppsInfo INNER JOIN KeyNames ON AppsInfo.Key=KeyNames.ID WHERE AppID = @AppID", new MySqlParameter("AppID", AppID)))
            {
                while (Reader.Read())
                {
                    appdata.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
                }
            }

            DbWorker.ExecuteNonQuery("DELETE FROM Apps WHERE AppID = @AppID", new MySqlParameter("@AppID", AppID));
            DbWorker.ExecuteNonQuery("DELETE FROM AppsInfo WHERE AppID = @AppID", new MySqlParameter("@AppID", AppID));
            DbWorker.ExecuteNonQuery("DELETE FROM Store WHERE AppID = @AppID", new MySqlParameter("@AppID", AppID));

            foreach (string key in appdata.Keys)
            {
                if (!key.StartsWith("website"))
                {
                    MakeHistory(AppID, 0, "removed_key", key, appdata[key], "");
                }
            }

            if (!AppName.StartsWith("SteamDB Unknown App"))
            {
                MakeHistory(AppID, 0, "deleted_app", "0", AppName, "", true);
            }
        }
    }
}
