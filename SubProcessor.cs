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
    public class SubProcessor
    {
        private const string DATABASE_NAME_TYPE = "10";

        private Dictionary<string, string> subData = new Dictionary<string, string>();
        private uint ChangeNumber;
        private uint SubID;

        public SubProcessor(uint SubID)
        {
            this.SubID = SubID;
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
                Log.WriteDebug("Sub Processor", "SubID: {0}", SubID);
            }

            if (ProductInfo.KeyValues == null || ProductInfo.KeyValues.Children.Count == 0)
            {
                Log.WriteWarn("Sub Processor", "SubID {0} is empty, wot do I do?", SubID);
                return;
            }
            
            string packageName = string.Empty;
            List<KeyValuePair<string, string>> apps = new List<KeyValuePair<string, string>>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `Value` FROM `SubsInfo` INNER JOIN `KeyNamesSubs` ON `SubsInfo`.`Key` = `KeyNamesSubs`.`ID` WHERE `SubID` = @SubID", new MySqlParameter("@SubID", SubID)))
            {
                while (Reader.Read())
                {
                    subData.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
                }
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID LIMIT 1", new MySqlParameter("@SubID", SubID)))
            {
                if (Reader.Read())
                {
                    packageName = DbWorker.GetString("Name", Reader);
                }
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID`, `Type` FROM `SubsApps` WHERE `SubID` = @SubID", new MySqlParameter("@SubID", SubID)))
            {
                while (Reader.Read())
                {
                    apps.Add(new KeyValuePair<string, string>(Reader.GetString("Type"), Reader.GetString("AppID")));
                }
            }

            var kv = ProductInfo.KeyValues.Children.FirstOrDefault();

            if (kv["name"].Value != null)
            {
                if (packageName.Equals(string.Empty))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Subs` (`SubID`, `Name`) VALUES (@SubID, @Name) ON DUPLICATE KEY UPDATE `Name` = @Name",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@Name", kv["name"].Value)
                    );

                    MakeHistory("created_sub");
                    MakeHistory("created_info", DATABASE_NAME_TYPE, string.Empty, kv["name"].Value, true);
                }
                else if (!packageName.Equals(kv["name"].Value))
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Subs` SET `Name` = @Name WHERE `SubID` = @SubID",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@Name", kv["name"].Value)
                    );

                    MakeHistory("modified_info", DATABASE_NAME_TYPE, packageName, kv["name"].Value, true);
                }
            }

            foreach (KeyValue section in kv.Children)
            {
                string sectionName = section.Name.ToLower();

                if (string.IsNullOrEmpty(sectionName) || sectionName.Equals("packageid") || sectionName.Equals("name"))
                {
                    // Ignore common keys
                    continue;
                }

                if (sectionName.Equals("appids") || sectionName.Equals("depotids"))
                {
                    string type = sectionName.Replace("ids", string.Empty); // Remove "ids", so we get app from appids and depot from depotids

                    foreach (KeyValue childrenApp in section.Children)
                    {
                        var app = apps.Where(x => x.Key == type && x.Value == childrenApp.Value);

                        if (app.Any())
                        {
                            // This combination of appid+type already exists, don't do anything
                            apps.Remove(app.First());
                        }
                        else
                        {
                            DbWorker.ExecuteNonQuery("INSERT INTO `SubsApps` (`SubID`, `AppID`, `Type`) VALUES(@SubID, @AppID, @Type) ON DUPLICATE KEY UPDATE `Type` = @Type",
                                                     new MySqlParameter("@SubID", SubID),
                                                     new MySqlParameter("@AppID", childrenApp.Value),
                                                     new MySqlParameter("@Type", type)
                            );

                            MakeHistory("added_to_sub", type.Equals("app") ? "0" : "1", string.Empty, childrenApp.Value, true); // TODO: Remove legacy 0/1 and replace with type
                        }
                    }
                }
                else if (sectionName.Equals("extended"))
                {
                    string keyName;

                    foreach (KeyValue children in section.Children)
                    {
                        keyName = string.Format("{0}_{1}", sectionName, children.Name);

                        ProcessKey(keyName, children.Name, children.Value);
                    }
                }
                else if (section.Children.Count > 0)
                {
                    sectionName = string.Format("root_{0}", sectionName);

                    ProcessKey(sectionName, "jsonHack", DbWorker.JsonifyKeyValue(section));
                }
                else if (!string.IsNullOrEmpty(section.Value))
                {
                    string keyName = string.Format("root_{0}", sectionName);

                    ProcessKey(keyName, sectionName, section.Value);
                }
            }

            foreach (string key in subData.Keys)
            {
                if (!key.StartsWith("website", StringComparison.Ordinal))
                {
                    DbWorker.ExecuteNonQuery("DELETE FROM `SubsInfo` WHERE `SubID` = @SubID AND `Key` = (SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = @KeyName LIMIT 1)",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@KeyName", key)
                    );

                    MakeHistory("removed_key", key, subData[key], string.Empty);
                }
            }

            foreach (var app in apps)
            {
                DbWorker.ExecuteNonQuery("DELETE FROM `SubsApps` WHERE `SubID` = @SubID AND `AppID` = @AppID AND `Type` = @Type",
                                         new MySqlParameter("@SubID", SubID),
                                         new MySqlParameter("@AppID", app.Value),
                                         new MySqlParameter("@Type", app.Key)
                );

                MakeHistory("removed_from_sub", app.Key.Equals("app") ? "0" : "1", app.Value, string.Empty, true); // TODO: Remove legacy 0/1 and replace with type
            }

#if DEBUG
            // I believe this can't happen with packages, but let's just be sure
            if (kv["name"].Value == null)
            {
                if (packageName.Equals(string.Empty)) // We don't have the app in our database yet
                {
                    // Don't do anything then
                    Log.WriteError("Sub Processor", "Got a package without a name, and we don't have it in our database: {0}", SubID);
                }
                else
                {
                    ////MakeHistory("deleted_sub", "0", packageName, "", true);

                    Log.WriteError("Sub Processor", "Got a package without a name, but we have it in our database: {0}", SubID);
                }
            }
#endif
        }

        private void ProcessKey(string keyName, string displayName, string value)
        {
            // All keys in PICS are supposed to be lower case.
            // But currently some keys in packages are not lowercased,
            // this lowercases everything to make sure nothing breaks in future
            keyName = keyName.ToLower();

            if (!subData.ContainsKey(keyName))
            {
                string ID = string.Empty;

                // Try to get ID from database to prevent autoindex bugginess and for faster performance (select > insert)
                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = @KeyName LIMIT 1", new MySqlParameter("KeyName", keyName)))
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

                        DbWorker.ExecuteNonQuery("INSERT INTO `KeyNamesSubs` (`Name`, `Type`) VALUES(@Name, @Type) ON DUPLICATE KEY UPDATE `Type` = `Type`",
                                                 new MySqlParameter("@Name", keyName),
                                                 new MySqlParameter("@Type", DB_TYPE_JSON)
                        );
                    }
                    else
                    {
                        DbWorker.ExecuteNonQuery("INSERT INTO `KeyNamesSubs` (`Name`, `DisplayName`) VALUES(@Name, @DisplayName) ON DUPLICATE KEY UPDATE `Type` = `Type`",
                                                 new MySqlParameter("@Name", keyName),
                                                 new MySqlParameter("@DisplayName", displayName)
                        );
                    }
                }

                MakeSubsInfo(keyName, value, ID);
                MakeHistory("created_key", keyName, string.Empty, value);
            }
            else if (!subData[keyName].Equals(value))
            {
                MakeSubsInfo(keyName, value);
                MakeHistory("modified_key", keyName, subData[keyName], value);
            }

            subData.Remove(keyName);
        }

        private void MakeSubsInfo(string KeyName = "", string Value = "", string ID = "")
        {
            // If ID is passed, we don't have to make a subquery
            if (ID.Equals(string.Empty))
            {
                DbWorker.ExecuteNonQuery("INSERT INTO `SubsInfo` VALUES (@SubID, (SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = @KeyName LIMIT 1), @Value) ON DUPLICATE KEY UPDATE `Value` = @Value",
                                         new MySqlParameter("@SubID", SubID),
                                         new MySqlParameter("@KeyName", KeyName),
                                         new MySqlParameter("@Value", Value)
                );
            }
            else
            {
                DbWorker.ExecuteNonQuery("INSERT INTO `SubsInfo` VALUES (@SubID, @ID, @Value) ON DUPLICATE KEY UPDATE `Value` = @Value",
                                         new MySqlParameter("@SubID", SubID),
                                         new MySqlParameter("@ID", ID),
                                         new MySqlParameter("@Value", Value)
                );
            }
        }

        private void MakeHistory(string Action, string KeyName = "", string OldValue = "", string NewValue = "", bool keyoverride = false)
        {
            string query = "INSERT INTO `SubsHistory` (`ChangeID`, `SubID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES ";

            if (keyoverride || KeyName.Equals(string.Empty))
            {
                query += "(@ChangeID, @SubID, @Action, @KeyName, @OldValue, @NewValue)";
            }
            else
            {
                query += "(@ChangeID, @SubID, @Action, (SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = @KeyName LIMIT 1), @OldValue, @NewValue)";
            }

            DbWorker.ExecuteNonQuery(query,
                                     new MySqlParameter("@SubID", SubID),
                                     new MySqlParameter("@ChangeID", ChangeNumber),
                                     new MySqlParameter("@Action", Action),
                                     new MySqlParameter("@KeyName", KeyName),
                                     new MySqlParameter("@OldValue", OldValue),
                                     new MySqlParameter("@NewValue", NewValue)
            );
        }
    }
}
