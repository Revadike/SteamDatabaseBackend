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
        private const uint DATABASE_NAME_TYPE = 10;

        private Dictionary<string, string> CurrentData = new Dictionary<string, string>();
        private uint ChangeNumber;
        private uint SubID;

        public SubProcessor(uint subID)
        {
            this.SubID = subID;
        }

        public void Process(SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo)
        {
            ChangeNumber = productInfo.ChangeNumber;

#if DEBUG
            if (true)
#else
            if (Settings.Current.FullRun > 0)
#endif
            {
                Log.WriteDebug("Sub Processor", "SubID: {0}", SubID);
            }

            if (productInfo.KeyValues == null || productInfo.KeyValues.Children.Count == 0)
            {
                Log.WriteWarn("Sub Processor", "SubID {0} is empty, wot do I do?", SubID);
                return;
            }

            string packageName = string.Empty;
            Dictionary<string, string> apps = new Dictionary<string, string>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `Value` FROM `SubsInfo` INNER JOIN `KeyNamesSubs` ON `SubsInfo`.`Key` = `KeyNamesSubs`.`ID` WHERE `SubID` = @SubID", new MySqlParameter("@SubID", SubID)))
            {
                while (Reader.Read())
                {
                    CurrentData.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
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
                string appID;

                while (Reader.Read())
                {
                    appID = Reader.GetString("AppID");

                    if (!apps.ContainsKey(appID))
                    {
                        apps.Add(appID, Reader.GetString("Type"));
                    }
                }
            }

            var kv = productInfo.KeyValues.Children.FirstOrDefault();

            if (kv["name"].Value != null)
            {
                if (string.IsNullOrEmpty(packageName))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Subs` (`SubID`, `Name`) VALUES (@SubID, @Name) ON DUPLICATE KEY UPDATE `Name` = @Name",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@Name", kv["name"].Value)
                    );

                    MakeHistory("created_sub");
                    MakeHistory("created_info", DATABASE_NAME_TYPE, string.Empty, kv["name"].Value);
                }
                else if (!packageName.Equals(kv["name"].Value))
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Subs` SET `Name` = @Name WHERE `SubID` = @SubID",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@Name", kv["name"].Value)
                    );

                    MakeHistory("modified_info", DATABASE_NAME_TYPE, packageName, kv["name"].Value);
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
                        if (apps.ContainsKey(childrenApp.Value) && apps[childrenApp.Value] == type)
                        {
                            // This combination of appid+type already exists, don't do anything
                            apps.Remove(childrenApp.Value);
                        }
                        else
                        {
                            DbWorker.ExecuteNonQuery("INSERT INTO `SubsApps` (`SubID`, `AppID`, `Type`) VALUES(@SubID, @AppID, @Type) ON DUPLICATE KEY UPDATE `Type` = @Type",
                                                     new MySqlParameter("@SubID", SubID),
                                                     new MySqlParameter("@AppID", childrenApp.Value),
                                                     new MySqlParameter("@Type", type)
                            );

                            uint typeID = (uint)(type.Equals("app") ? 0 : 1); // TODO: Remove legacy 0/1 and replace with type

                            MakeHistory("added_to_sub", typeID, string.Empty, childrenApp.Value);
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

            foreach (string keyName in CurrentData.Keys)
            {
                if (!keyName.StartsWith("website", StringComparison.Ordinal))
                {
                    uint ID = GetKeyNameID(keyName);

                    DbWorker.ExecuteNonQuery("DELETE FROM `SubsInfo` WHERE `SubID` = @SubID AND `Key` = @KeyNameID",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@KeyNameID", ID)
                    );

                    MakeHistory("removed_key", ID, CurrentData[keyName]);
                }
            }

            foreach (var app in apps)
            {
                DbWorker.ExecuteNonQuery("DELETE FROM `SubsApps` WHERE `SubID` = @SubID AND `AppID` = @AppID AND `Type` = @Type",
                                         new MySqlParameter("@SubID", SubID),
                                         new MySqlParameter("@AppID", app.Key),
                                         new MySqlParameter("@Type", app.Value)
                );

                uint typeID = (uint)(app.Value.Equals("app") ? 0 : 1); // TODO: Remove legacy 0/1 and replace with type

                MakeHistory("removed_from_sub", typeID, app.Key);
            }

#if DEBUG
            if (kv["name"].Value == null)
            {
                if (string.IsNullOrEmpty(packageName)) // We don't have the package in our database yet
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

        private bool ProcessKey(string keyName, string displayName, string value)
        {
            // All keys in PICS are supposed to be lower case.
            // But currently some keys in packages are not lowercased,
            // this lowercases everything to make sure nothing breaks in future
            keyName = keyName.ToLower();

            if (!CurrentData.ContainsKey(keyName))
            {
                uint ID = GetKeyNameID(keyName);

                if (ID == 0)
                {
                    if (displayName.Equals("jsonHack"))
                    {
                        const uint DB_TYPE_JSON = 86;

                        DbWorker.ExecuteNonQuery("INSERT INTO `KeyNamesSubs` (`Name`, `Type`, `DisplayName`) VALUES(@Name, @Type, @DisplayName) ON DUPLICATE KEY UPDATE `Type` = `Type`",
                                                 new MySqlParameter("@Name", keyName),
                                                 new MySqlParameter("@DisplayName", keyName),
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

                    ID = GetKeyNameID(keyName);
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
            DbWorker.ExecuteNonQuery("INSERT INTO `SubsInfo` VALUES (@SubID, @KeyNameID, @Value) ON DUPLICATE KEY UPDATE `Value` = @Value",
                                     new MySqlParameter("@SubID", SubID),
                                     new MySqlParameter("@KeyNameID", id),
                                     new MySqlParameter("@Value", value)
            );
        }

        private void MakeHistory(string action, uint keyNameID = 0, string oldValue = "", string newValue = "")
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `SubsHistory` (`ChangeID`, `SubID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES (@ChangeID, @SubID, @Action, @KeyNameID, @OldValue, @NewValue)",
                                     new MySqlParameter("@SubID", SubID),
                                     new MySqlParameter("@ChangeID", ChangeNumber),
                                     new MySqlParameter("@Action", action),
                                     new MySqlParameter("@KeyNameID", keyNameID),
                                     new MySqlParameter("@OldValue", oldValue),
                                     new MySqlParameter("@NewValue", newValue)
            );
        }

        private static uint GetKeyNameID(string keyName)
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = @KeyName LIMIT 1", new MySqlParameter("KeyName", keyName)))
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
