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

namespace PICSUpdater
{
    class SubProcessor
    {
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

            if (ProductInfo.KeyValues == null)
            {
                Log.WriteWarn("Sub Processor", "SubID {0} is empty, wot do I do?", SubID);
                return;
            }

            Dictionary<string, string> subdata = new Dictionary<string, string>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `Name`, `Value` FROM SubsInfo INNER JOIN KeyNamesSubs ON SubsInfo.Key=KeyNamesSubs.ID WHERE SubID = @SubID", new MySqlParameter("@SubID", SubID)))
            {
                while (Reader.Read())
                {
                    subdata.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
                }
            }

            string PackageName = "";
            List<int> subapps = new List<int>();
            List<int> subdepots = new List<int>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `Name` FROM Subs WHERE SubID = @SubID", new MySqlParameter("@SubID", SubID)))
            {
                if (Reader.Read())
                {
                    PackageName = DbWorker.GetString("Name", Reader);
                }
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT AppID FROM SubsApps WHERE SubID = @SubID AND Type = 'app'", new MySqlParameter("@SubID", SubID)))
            {
                while (Reader.Read())
                {
                    subapps.Add(Reader.GetInt32("AppID"));
                }
            }

            // TODO: Combine into a single query/list
            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT AppID FROM SubsApps WHERE SubID = @SubID AND Type = 'depot'", new MySqlParameter("@SubID", SubID)))
            {
                while (Reader.Read())
                {
                    subdepots.Add(Reader.GetInt32("AppID"));
                }
            }

            foreach (KeyValue kv in ProductInfo.KeyValues.Children)
            {
                if (kv.Children.Count == 0)
                {
                    Log.WriteDebug("Sub Processor", "SubID {0}: Empty children? {1} - {2}", SubID, kv.Name, kv.Value);
                    continue;
                }

                foreach (KeyValue kv2 in kv.Children)
                {
                    if (kv2.Children.Count == 0)
                    {
                        if (kv2.Name != null && !kv2.Name.Equals("") && !kv2.Name.Equals("extended") && !kv2.Name.Equals("depotids") &&  !kv2.Name.Equals("appids") && !kv2.Name.Equals("AppItems") && !kv2.Name.Equals("name") && !kv2.Name.Equals("packageid"))
                        {
                            DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNamesSubs(Name, DisplayName) VALUES(@KeyValueNameSection, @KeyValueName)",
                                new MySqlParameter[]
                                {
                                    new MySqlParameter("@KeyValueNameSection", "root_" + kv2.Name.ToString()),
                                    new MySqlParameter("@KeyValueName", kv2.Name.ToString())
                                });
                            if (kv2.Value != null)
                            {
                                MakeSubsInfo(SubID, "root_" + kv2.Name.ToString(), kv2.Value.ToString());

                                if (subdata.ContainsKey("root_" + kv2.Name.ToString()))
                                {
                                    if (!subdata["root_" + kv2.Name.ToString()].Equals(kv2.Value.ToString()))
                                    {
                                        MakeHistory(SubID, ProductInfo.ChangeNumber, "modified_key", "root_" + kv2.Name.ToString(), subdata["root_" + kv2.Name.ToString()].ToString(), kv2.Value.ToString());
                                    }
                                    subdata.Remove("root_" + kv2.Name.ToString());
                                }
                                else
                                {
                                    MakeHistory(SubID, ProductInfo.ChangeNumber, "created_key", "root_" + kv2.Name.ToString(), "", kv2.Value.ToString());
                                }
                            }

                        }
                        else if (kv2.Name.Equals("name"))
                        {
                            DbWorker.ExecuteNonQuery("INSERT IGNORE INTO Subs(SubID, Name) VALUES(@SubID, @Name) ON DUPLICATE KEY UPDATE Name=@Name",
                            new MySqlParameter[]
                                {
                                    new MySqlParameter("@SubID", SubID),
                                    new MySqlParameter("@Name", kv2.Value)
                                });
                            if (PackageName.Equals(""))
                            {
                                MakeHistory(SubID, ProductInfo.ChangeNumber, "created_sub");
                            }else if(!PackageName.Equals(kv2.Value.ToString())){
                                MakeHistory(SubID, ProductInfo.ChangeNumber, "modified_info", "10", PackageName, kv2.Value.ToString(), true);
                            }
                        }
                    }
                    else
                    {
                        if (kv2.Name == "extended")
                        {
                            foreach (KeyValue kv3 in kv2.Children)
                            {
                                DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNamesSubs(Name, DisplayName) VALUES(@KeyValueNameSection, @KeyValueName)",
                                new MySqlParameter[]
                                {
                                    new MySqlParameter("@KeyValueNameSection", "extended_" + kv3.Name.ToString()),
                                    new MySqlParameter("@KeyValueName", kv3.Name.ToString())
                                });

                                MakeSubsInfo(SubID, "extended_" + kv3.Name.ToString(), kv3.Value.ToString());

                                if (subdata.ContainsKey("extended_" + kv3.Name.ToString()))
                                {
                                    if (!subdata["extended_" + kv3.Name.ToString()].Equals(kv3.Value.ToString()))
                                    {
                                        MakeHistory(SubID, ProductInfo.ChangeNumber, "modified_key", "extended_" + kv3.Name.ToString(), subdata["extended_" + kv3.Name.ToString()].ToString(), kv3.Value.ToString());
                                    }
                                    subdata.Remove("extended_" + kv3.Name.ToString());
                                }
                                else
                                {
                                    MakeHistory(SubID, ProductInfo.ChangeNumber, "created_key", "extended_" + kv3.Name.ToString(), "", kv3.Value.ToString());
                                }
                            }
                        }
                        else if (kv2.Name == "appids" || kv2.Name == "depotids")
                        {
                            String type;
                            if (kv2.Name == "appids") { type = "app"; } else { type = "depot"; }
                            foreach (KeyValue kv3 in kv2.Children)
                            {
                                DbWorker.ExecuteNonQuery("INSERT INTO SubsApps(SubID, AppID, Type) VALUES(@SubID, @AppID, @Type) ON DUPLICATE KEY UPDATE Type=@Type",
                                new MySqlParameter[]
                                {
                                    new MySqlParameter("@SubID", SubID),
                                    new MySqlParameter("@AppID", kv3.Value),
                                    new MySqlParameter("@Type", type)
                                });

                                if (type == "app")
                                {
                                    if (!subapps.Contains(kv3.AsInteger()))
                                    {
                                        MakeHistory(SubID, ProductInfo.ChangeNumber, "added_to_sub", "0", "", kv3.Value, true);
                                    }
                                    else
                                    {
                                        subapps.Remove(kv3.AsInteger());
                                    }

                                }
                                else if (type == "depot")
                                {
                                    if (!subdepots.Contains(kv3.AsInteger()))
                                    {
                                        MakeHistory(SubID, ProductInfo.ChangeNumber, "added_to_sub", "1", "", kv3.Value, true);
                                    }
                                    else
                                    {
                                        subdepots.Remove(kv3.AsInteger());
                                    }
                                }
                            }
                        }
                        else
                        {
                            String json = DbWorker.JsonifyKeyValue(kv2);

                            List<MySqlParameter> parameters = new List<MySqlParameter>();

                            DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNamesSubs (Type, Name, DisplayName) VALUES (99, @KeyName, @KeyName)",
                                new MySqlParameter[]
                                {
                                    new MySqlParameter("@KeyName", "marlamin" + "_" + kv2.Name)
                                });

                            MakeSubsInfo(SubID, "marlamin_" + kv2.Name, json);

                            if (subdata.ContainsKey("marlamin_" + kv2.Name.ToString()))
                            {
                                if (!subdata["marlamin_" + kv2.Name.ToString()].Equals(json))
                                {
                                    MakeHistory(SubID, ProductInfo.ChangeNumber, "modified_key", "marlamin_" + kv2.Name.ToString(), subdata["marlamin_" + kv2.Name.ToString()].ToString(), json);
                                }
                            }
                            else
                            {
                                MakeHistory(SubID, ProductInfo.ChangeNumber, "created_key", "marlamin_" + kv2.Name.ToString(), "", json);
                            }
                            subdata.Remove("marlamin" + "_" + kv2.Name);
                        }
                    }
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

            foreach (int AppID in subapps)
            {
                DbWorker.ExecuteNonQuery("DELETE FROM SubsApps WHERE SubID = @SubID AND AppID = @AppID AND `Type` = 'app'",
                                         new MySqlParameter("@SubID", SubID),
                                         new MySqlParameter("@AppID", AppID)
                );

                MakeHistory(SubID, ProductInfo.ChangeNumber, "removed_from_sub", "0", AppID.ToString(), "", true);
            }

            foreach (int AppID in subdepots)
            {
                DbWorker.ExecuteNonQuery("DELETE FROM SubsApps WHERE SubID = @SubID AND AppID = @AppID AND `Type` = 'depot'",
                                         new MySqlParameter("@SubID", SubID),
                                         new MySqlParameter("@AppID", AppID)
                );

                MakeHistory(SubID, ProductInfo.ChangeNumber, "removed_from_sub", "1", AppID.ToString(), "", true);
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
