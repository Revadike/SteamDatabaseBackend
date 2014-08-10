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
        private readonly uint SubID;

        public SubProcessor(uint subID)
        {
            this.SubID = subID;
        }

        public void Process(SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo)
        {
            ChangeNumber = productInfo.ChangeNumber;

#if !DEBUG
            if (Settings.IsFullRun)
#endif
            {
                Log.WriteDebug("Sub Processor", "SubID: {0}", SubID);
            }

            try
            {
                TryProcess(productInfo);
            }
            catch (Exception e)
            {
                Log.WriteError("Sub Processor", "Caught exception while processing sub {0}: {1}\n{2}", SubID, e.Message, e.StackTrace);
            }
        }

        private void TryProcess(SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo)
        {
            string packageName = string.Empty;
            var apps = new Dictionary<uint, string>();

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
                while (Reader.Read())
                {
                    apps.Add(Reader.GetUInt32("AppID"), Reader.GetString("Type"));
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

                    uint typeID = (uint)(type.Equals("app") ? 0 : 1); // TODO: Remove legacy 0/1 and replace with type

                    foreach (KeyValue childrenApp in section.Children)
                    {
                        uint appID = uint.Parse(childrenApp.Value);

                        // Is this appid already in this package?
                        if (apps.ContainsKey(appID))
                        {
                            // Is this appid's type the same?
                            if (apps[appID] != type)
                            {
                                DbWorker.ExecuteNonQuery("UPDATE `SubsApps` SET `Type` = @Type WHERE `SubID` = @SubID AND `AppID` = @AppID",
                                                         new MySqlParameter("@SubID", SubID),
                                                         new MySqlParameter("@AppID", appID),
                                                         new MySqlParameter("@Type", type)
                                );

                                MakeHistory("added_to_sub", typeID, apps[appID].Equals("app") ? "0" : "1", childrenApp.Value);
                            }

                            apps.Remove(appID);
                        }
                        else
                        {
                            DbWorker.ExecuteNonQuery("INSERT INTO `SubsApps` (`SubID`, `AppID`, `Type`) VALUES(@SubID, @AppID, @Type) ON DUPLICATE KEY UPDATE `Type` = @Type",
                                                     new MySqlParameter("@SubID", SubID),
                                                     new MySqlParameter("@AppID", appID),
                                                     new MySqlParameter("@Type", type)
                            );

                            MakeHistory("added_to_sub", typeID, string.Empty, childrenApp.Value);
                            AppProcessor.MakeHistory(appID, ChangeNumber, "added_to_sub", typeID, string.Empty, SubID.ToString());

                            if (SteamProxy.Instance.ImportantApps.Contains(appID))
                            {
                                IRC.SendMain("Important app {0}{1}{2} was added to package {3}{4}{5} -{6} {7}",
                                             Colors.OLIVE, SteamProxy.GetAppName(appID), Colors.NORMAL,
                                             Colors.OLIVE, packageName, Colors.NORMAL,
                                             Colors.DARK_BLUE, SteamDB.GetPackageURL(SubID, "history")
                                );
                            }
                        }
                    }
                }
                else if (sectionName.Equals("extended"))
                {
                    string keyName;

                    foreach (KeyValue children in section.Children)
                    {
                        if (children.Children.Count > 0)
                        {
                            Log.WriteError("Sub Processor", "SubID {0} has childen in extended section", SubID);
                        }

                        keyName = string.Format("{0}_{1}", sectionName, children.Name);

                        ProcessKey(keyName, children.Name, children.Value);
                    }
                }
                else if (section.Children.Count > 0)
                {
                    sectionName = string.Format("root_{0}", sectionName);

                    ProcessKey(sectionName, sectionName, DbWorker.JsonifyKeyValue(section), true);
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

                MakeHistory("removed_from_sub", typeID, app.Key.ToString());
                AppProcessor.MakeHistory(app.Key, ChangeNumber, "removed_from_sub", typeID, SubID.ToString());

                if (SteamProxy.Instance.ImportantApps.Contains(app.Key))
                {
                    IRC.SendMain("Important app {0}{1}{2} was removed from package {3}{4}{5} -{6} {7}",
                        Colors.OLIVE, SteamProxy.GetAppName(app.Key), Colors.NORMAL,
                        Colors.OLIVE, packageName, Colors.NORMAL,
                        Colors.DARK_BLUE, SteamDB.GetPackageURL(SubID, "history")
                    );
                }
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

        public void ProcessUnknown()
        {
            Log.WriteInfo("Sub Processor", "Unknown SubID: {0}", SubID);

            try
            {
                TryProcessUnknown();
            }
            catch (Exception e)
            {
                Log.WriteError("Sub Processor", "Caught exception while processing unknown sub {0}: {1}\n{2}", SubID, e.Message, e.StackTrace);
            }
        }

        private void TryProcessUnknown()
        {
            string name;

            using (MySqlDataReader MainReader = DbWorker.ExecuteReader("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID LIMIT 1", new MySqlParameter("SubID", SubID)))
            {
                if (!MainReader.Read())
                {
                    return;
                }

                name = DbWorker.GetString("Name", MainReader);
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `Key`, `Value` FROM `SubsInfo` INNER JOIN `KeyNamesSubs` ON `SubsInfo`.`Key` = `KeyNamesSubs`.`ID` WHERE `SubID` = @SubID", new MySqlParameter("SubID", SubID)))
            {
                while (Reader.Read())
                {
                    if (!DbWorker.GetString("Name", Reader).StartsWith("website", StringComparison.Ordinal))
                    {
                        MakeHistory("removed_key", Reader.GetUInt32("Key"), DbWorker.GetString("Value", Reader));
                    }
                }
            }

            DbWorker.ExecuteNonQuery("DELETE FROM `Subs` WHERE `SubID` = @SubID", new MySqlParameter("@SubID", SubID));
            DbWorker.ExecuteNonQuery("DELETE FROM `SubsInfo` WHERE `SubID` = @SubID", new MySqlParameter("@SubID", SubID));
            DbWorker.ExecuteNonQuery("DELETE FROM `StoreSubs` WHERE `SubID` = @SubID", new MySqlParameter("@SubID", SubID));

            // TODO
            MakeHistory("deleted_sub", 0, name);
        }

        private bool ProcessKey(string keyName, string displayName, string value, bool isJSON = false)
        {
            // All keys in PICS are supposed to be lower case.
            // But currently some keys in packages are not lowercased,
            // this lowercases everything to make sure nothing breaks in future
            keyName = keyName.ToLower().Trim();

            if (!CurrentData.ContainsKey(keyName))
            {
                uint ID = GetKeyNameID(keyName);

                if (ID == 0)
                {
                    if (isJSON)
                    {
                        const uint DB_TYPE_JSON = 86;

                        DbWorker.ExecuteNonQuery("INSERT INTO `KeyNamesSubs` (`Name`, `Type`, `DisplayName`) VALUES(@Name, @Type, @DisplayName) ON DUPLICATE KEY UPDATE `Type` = `Type`",
                                                 new MySqlParameter("@Name", keyName),
                                                 new MySqlParameter("@DisplayName", displayName),
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

                    if (ID == 0)
                    {
                        // We can't insert anything because key wasn't created
                        Log.WriteError("Sub Processor", "Failed to create key {0} for SubID {1}, not inserting info.", keyName, SubID);

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
