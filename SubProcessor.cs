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
    class SubProcessor
    {
        private const string DATABASE_NAME_TYPE = "10";

        public void Process(uint SubID, SteamApps.PICSProductInfoCallback.PICSProductInfo ProductInfo)
        {
#if DEBUG
            if (true)
#else
            if (Program.fullRunOption > 0)
#endif
            {
                Log.WriteInfo("Sub Processor", "SubID: {0}", SubID);
            }

            if (ProductInfo.KeyValues == null || ProductInfo.KeyValues.Children.Count == 0)
            {
                Log.WriteWarn("Sub Processor", "SubID {0} is empty, wot do I do?", SubID);
                return;
            }

            string packageName = "";
            List<KeyValuePair<string, string>> apps = new List<KeyValuePair<string, string>>();
            Dictionary<string, string> subdata = new Dictionary<string, string>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `Value` FROM SubsInfo INNER JOIN KeyNamesSubs ON SubsInfo.Key=KeyNamesSubs.ID WHERE SubID = @SubID", new MySqlParameter("@SubID", SubID)))
            {
                while (Reader.Read())
                {
                    subdata.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
                }
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID", new MySqlParameter("@SubID", SubID)))
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
                if (packageName.Equals(""))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO Subs (SubID, Name) VALUES (@SubID, @Name) ON DUPLICATE KEY UPDATE `Name` = @Name",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@Name", kv["name"].Value)
                    );

                    MakeHistory(SubID, ProductInfo.ChangeNumber, "created_sub");
                    MakeHistory(SubID, ProductInfo.ChangeNumber, "created_info", DATABASE_NAME_TYPE, "", kv["name"].Value, true);
                }
                else if (!packageName.Equals(kv["name"].Value))
                {
                    DbWorker.ExecuteNonQuery("UPDATE Subs SET Name = @Name WHERE SubID = @SubID",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@Name", kv["name"].Value)
                    );

                    MakeHistory(SubID, ProductInfo.ChangeNumber, "modified_info", DATABASE_NAME_TYPE, packageName, kv["name"].Value, true);
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
                    string type = sectionName.Replace("ids", ""); // Remove "ids", so we get app from appids and depot from depotids

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
                            DbWorker.ExecuteNonQuery("INSERT INTO SubsApps(SubID, AppID, Type) VALUES(@SubID, @AppID, @Type) ON DUPLICATE KEY UPDATE Type=@Type",
                                                     new MySqlParameter("@SubID", SubID),
                                                     new MySqlParameter("@AppID", childrenApp.Value),
                                                     new MySqlParameter("@Type", type)
                            );

                            MakeHistory(SubID, ProductInfo.ChangeNumber, "added_to_sub", type.Equals("app") ? "0" : "1", "", childrenApp.Value, true); // TODO: Remove legacy 0/1 and replace with type
                        }
                    }
                }
                else if (sectionName.Equals("extended"))
                {
                    string keyName = "";

                    foreach (KeyValue children in section.Children)
                    {
                        keyName = string.Format("{0}_{1}", sectionName, children.Name);

                        ProcessKey(SubID, ProductInfo.ChangeNumber, subdata, keyName, children.Name, children.Value);

                        subdata.Remove(keyName);
                    }
                }
                else if(section.Children.Count > 0)
                {
                    sectionName = string.Format("root_{0}", sectionName);

                    ProcessKey(SubID, ProductInfo.ChangeNumber, subdata, sectionName, "jsonHack", DbWorker.JsonifyKeyValue(section));

                    subdata.Remove(sectionName);
                }
                else if(!string.IsNullOrEmpty(section.Value))
                {
                    string keyName = string.Format("root_{0}", sectionName);

                    ProcessKey(SubID, ProductInfo.ChangeNumber, subdata, keyName, sectionName, section.Value);

                    subdata.Remove(keyName);
                }
            }

            foreach (string key in subdata.Keys)
            {
                if (!key.StartsWith("website"))
                {
                    DbWorker.ExecuteNonQuery("DELETE FROM SubsInfo WHERE `SubID` = @SubID AND `Key` = (SELECT ID from KeyNamesSubs WHERE Name = @KeyName LIMIT 1)",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@KeyName", key)
                    );

                    MakeHistory(SubID, ProductInfo.ChangeNumber, "removed_key", key, subdata[key], "");
                }

            }

            foreach (var app in apps)
            {
                DbWorker.ExecuteNonQuery("DELETE FROM `SubsApps` WHERE `SubID` = @SubID AND `AppID` = @AppID AND `Type` = @Type",
                                         new MySqlParameter("@SubID", SubID),
                                         new MySqlParameter("@AppID", app.Value),
                                         new MySqlParameter("@Type", app.Key)
                );

                MakeHistory(SubID, ProductInfo.ChangeNumber, "removed_from_sub", app.Key.Equals("app") ? "0" : "1", app.Value, "", true); // TODO: Remove legacy 0/1 and replace with type
            }

            // I believe this can't happen with packages, but let's just be sure
            if (kv["name"].Value == null)
            {
                if (packageName.Equals("")) // We don't have the app in our database yet
                {
                    // Don't do anything then
                    Log.WriteError("Sub Processor", "Got a package without a name, and we don't have it in our database: {0}", SubID);
                }
                else
                {
                    //MakeHistory(SubID, ProductInfo.ChangeNumber, "deleted_sub", "0", packageName, "", true);

                    Log.WriteError("Sub Processor", "Got a package without a name, but we have it in our database: {0}", SubID);
                }
            }
        }

        private static void ProcessKey(uint SubID, uint ChangeNumber, Dictionary<string, string> subData, string keyName, string displayName, string value)
        {
            if (!subData.ContainsKey(keyName))
            {
                if (displayName.Equals("jsonHack"))
                {
                    const uint DB_TYPE_JSON = 86; // TODO: Verify this

                    DbWorker.ExecuteNonQuery("INSERT INTO KeyNamesSubs(`Name`, `Type`) VALUES(@Name, @Type) ON DUPLICATE KEY UPDATE `ID` = `ID`",
                                             new MySqlParameter("@Name", keyName),
                                             new MySqlParameter("@Type", DB_TYPE_JSON)
                    );
                }
                else
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO KeyNamesSubs(`Name`, `DisplayName`) VALUES(@Name, @DisplayName) ON DUPLICATE KEY UPDATE `ID` = `ID`",
                                             new MySqlParameter("@Name", keyName),
                                             new MySqlParameter("@DisplayName", displayName)
                    );
                }

                MakeSubsInfo(SubID, keyName, value);
                MakeHistory(SubID, ChangeNumber, "created_key", keyName, "", value);
            }
            else if (!subData[keyName].Equals(value))
            {
                MakeSubsInfo(SubID, keyName, value);
                MakeHistory(SubID, ChangeNumber, "modified_key", keyName, subData[keyName], value);
            }
        }

        private static void MakeSubsInfo(uint SubID, string KeyName = "", string Value = "")
        {
            DbWorker.ExecuteNonQuery("INSERT INTO SubsInfo VALUES (@SubID, (SELECT ID from KeyNamesSubs WHERE Name = @KeyName LIMIT 1), @Value) ON DUPLICATE KEY UPDATE Value=@Value",
                                     new MySqlParameter("@SubID", SubID),
                                     new MySqlParameter("@KeyName", KeyName),
                                     new MySqlParameter("@Value", Value)
            );
        }

        private static void MakeHistory(uint SubID, uint ChangeNumber, string Action, string KeyName = "", string OldValue = "", string NewValue = "", bool keyoverride = false)
        {
            string query = "INSERT INTO `SubsHistory` (`ChangeID`, `SubID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES ";

            if (keyoverride == true || KeyName.Equals(""))
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
