/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
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
    class SubProcessor
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

            string packageName = string.Empty;
            string packageNameCurrent = string.Empty;
            var apps = new Dictionary<uint, string>();

            using (var reader = DbWorker.ExecuteReader("SELECT `Name`, `Value` FROM `SubsInfo` INNER JOIN `KeyNamesSubs` ON `SubsInfo`.`Key` = `KeyNamesSubs`.`ID` WHERE `SubID` = @SubID", new MySqlParameter("@SubID", SubID)))
            {
                while (reader.Read())
                {
                    CurrentData.Add(reader.GetString("Name"), reader.GetString("Value"));
                }
            }

            using (var reader = DbWorker.ExecuteReader("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID LIMIT 1", new MySqlParameter("@SubID", SubID)))
            {
                if (reader.Read())
                {
                    packageNameCurrent = reader.GetString("Name");
                }
            }

            using (var reader = DbWorker.ExecuteReader("SELECT `AppID`, `Type` FROM `SubsApps` WHERE `SubID` = @SubID", new MySqlParameter("@SubID", SubID)))
            {
                while (reader.Read())
                {
                    apps.Add(reader.GetUInt32("AppID"), reader.GetString("Type"));
                }
            }

            var kv = productInfo.KeyValues.Children.FirstOrDefault();

            packageName = kv["name"].Value;

            if (packageName != null)
            {
                if (string.IsNullOrEmpty(packageNameCurrent))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Subs` (`SubID`, `Name`) VALUES (@SubID, @Name) ON DUPLICATE KEY UPDATE `Name` = @Name",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@Name", packageName)
                    );

                    MakeHistory("created_sub");
                    MakeHistory("created_info", DATABASE_NAME_TYPE, string.Empty, packageName);
                }
                else if (!packageNameCurrent.Equals(packageName))
                {
                    DbWorker.ExecuteNonQuery("UPDATE `Subs` SET `Name` = @Name WHERE `SubID` = @SubID",
                                             new MySqlParameter("@SubID", SubID),
                                             new MySqlParameter("@Name", packageName)
                    );

                    MakeHistory("modified_info", DATABASE_NAME_TYPE, packageNameCurrent, packageName);
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

                                // TODO: Log relevant add/remove history for depot/app?
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

                            if (typeID == 0)
                            {
                                AppProcessor.MakeHistory(appID, ChangeNumber, "added_to_sub", 0, string.Empty, SubID.ToString());
                            }
                            else
                            {
                                DepotProcessor.MakeHistory(new DepotProcessor.ManifestJob
                                {
                                    DepotID = appID,
                                    ChangeNumber = ChangeNumber
                                }, string.Empty, "added_to_sub", 0, SubID);
                            }
                        }
                    }

                    // TODO: Probably should check if apps are owned
                    if (typeID == 0 && kv["billingtype"].AsInteger() == 12 && !Application.OwnedSubs.ContainsKey(SubID)) // 12 == free on demand
                    {
                        Log.WriteDebug("Sub Processor", "Requesting apps in SubID {0} as a free license", SubID);

                        JobManager.AddJob(() => SteamDB.RequestFreeLicense(section.Children.Select(appid => (uint)appid.AsInteger()).ToList()));
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

                if (typeID == 0)
                {
                    AppProcessor.MakeHistory(app.Key, ChangeNumber, "removed_from_sub", 0, SubID.ToString());
                }
                else
                {
                    DepotProcessor.MakeHistory(new DepotProcessor.ManifestJob
                    {
                        DepotID = app.Key,
                        ChangeNumber = ChangeNumber
                    }, string.Empty, "removed_from_sub", SubID);
                }
            }

#if DEBUG
            if (packageName == null)
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

            using (var reader = DbWorker.ExecuteReader("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID LIMIT 1", new MySqlParameter("SubID", SubID)))
            {
                if (!reader.Read())
                {
                    return;
                }

                name = reader.GetString("Name");
            }

            using (var reader = DbWorker.ExecuteReader("SELECT `Name`, `Key`, `Value` FROM `SubsInfo` INNER JOIN `KeyNamesSubs` ON `SubsInfo`.`Key` = `KeyNamesSubs`.`ID` WHERE `SubID` = @SubID", new MySqlParameter("SubID", SubID)))
            {
                while (reader.Read())
                {
                    if (!reader.GetString("Name").StartsWith("website", StringComparison.Ordinal))
                    {
                        MakeHistory("removed_key", reader.GetUInt32("Key"), reader.GetString("Value"));
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

                    IRC.Instance.SendOps("New package keyname: {0}{1} {2}(ID: {3})", Colors.BLUE, displayName, Colors.LIGHTGRAY, ID);

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
            using (var reader = DbWorker.ExecuteReader("SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = @KeyName LIMIT 1", new MySqlParameter("KeyName", keyName)))
            {
                if (reader.Read())
                {
                    return reader.GetUInt32("ID");
                }
            }

            return 0;
        }
    }
}
