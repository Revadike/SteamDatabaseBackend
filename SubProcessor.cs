/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MySql.Data.MySqlClient;
using SteamKit2;
using Newtonsoft.Json;
using System.IO;

namespace PICSUpdater
{
    class SubProcessor
    {
        public void ProcessSub(object sub)
        {
            if (Steam.fullRunOption > 0)
            {
                Console.WriteLine("Processing Sub: {0}", sub);
            }

            Dictionary<string, string> subdata = new Dictionary<string, string>();
            KeyValuePair<UInt32, SteamApps.PICSProductInfoCallback.PICSProductInfo> subb = (KeyValuePair<UInt32, SteamApps.PICSProductInfoCallback.PICSProductInfo>)sub;
            MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `Name`, `Value` FROM SubsInfo INNER JOIN KeyNamesSubs ON SubsInfo.Key=KeyNamesSubs.ID WHERE SubID = @SubID", new MySqlParameter[]
                {
                    new MySqlParameter("@SubID", subb.Key)
                });
            while (Reader.Read())
            {
                subdata.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
            }
            Reader.Close();
            Reader.Dispose();

            String PackageName = "";

            MySqlDataReader mainsubReader = DbWorker.ExecuteReader(@"SELECT `Name` FROM Subs WHERE SubID = @SubID", new MySqlParameter[]
                {
                    new MySqlParameter("@SubID", subb.Key)
                });
            if(mainsubReader.Read())
            {
                PackageName = DbWorker.GetString("Name", mainsubReader);
            }
            mainsubReader.Close();
            mainsubReader.Dispose();

            List<string> subapps = new List<string>();
            MySqlDataReader SubAppsReader = DbWorker.ExecuteReader(@"SELECT AppID FROM SubsApps WHERE SubID = @SubID AND Type = 'app'", new MySqlParameter[]
                {
                    new MySqlParameter("@SubID", subb.Key)
                });
            while (SubAppsReader.Read())
            {
                subapps.Add(DbWorker.GetString("AppID", SubAppsReader));
            }
            SubAppsReader.Close();
            SubAppsReader.Dispose();

            List<string> subdepots = new List<string>();
            MySqlDataReader SubDepotsReader = DbWorker.ExecuteReader(@"SELECT AppID FROM SubsApps WHERE SubID = @SubID AND Type = 'depot'", new MySqlParameter[]
                {
                    new MySqlParameter("@SubID", subb.Key)
                });
            while (SubDepotsReader.Read())
            {
                subdepots.Add(DbWorker.GetString("AppID", SubDepotsReader));
            }
            SubDepotsReader.Close();
            SubDepotsReader.Dispose();

            foreach (KeyValue kv in subb.Value.KeyValues.Children)
            {
                if (kv.Children.Count != 0)
                {
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
                                    MakeSubsInfo(subb.Key, "root_" + kv2.Name.ToString(), kv2.Value.ToString());

                                    if (subdata.ContainsKey("root_" + kv2.Name.ToString()))
                                    {
                                        if (!subdata["root_" + kv2.Name.ToString()].Equals(kv2.Value.ToString()))
                                        {
                                            MakeHistory(subb.Key, subb.Value.ChangeNumber, "modified_key", "root_" + kv2.Name.ToString(), subdata["root_" + kv2.Name.ToString()].ToString(), kv2.Value.ToString());
                                        }
                                        subdata.Remove("root_" + kv2.Name.ToString());
                                    }
                                    else
                                    {
                                        MakeHistory(subb.Key, subb.Value.ChangeNumber, "created_key", "root_" + kv2.Name.ToString(), "", kv2.Value.ToString());
                                    }
                                }

                            }
                            else if (kv2.Name.Equals("name"))
                            {
                                DbWorker.ExecuteNonQuery("INSERT IGNORE INTO Subs(SubID, Name) VALUES(@SubID, @Name) ON DUPLICATE KEY UPDATE Name=@Name",
                                new MySqlParameter[]
                                    {
                                        new MySqlParameter("@SubID", subb.Key),
                                        new MySqlParameter("@Name", kv2.Value)
                                    });
                                if (PackageName.Equals(""))
                                {
                                    MakeHistory(subb.Key, subb.Value.ChangeNumber, "created_sub");
                                }else if(!PackageName.Equals(kv2.Value.ToString())){
                                    MakeHistory(subb.Key, subb.Value.ChangeNumber, "modified_info", "10", PackageName, kv2.Value.ToString(), true);
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

                                    MakeSubsInfo(subb.Key, "extended_" + kv3.Name.ToString(), kv3.Value.ToString());

                                    if (subdata.ContainsKey("extended_" + kv3.Name.ToString()))
                                    {
                                        if (!subdata["extended_" + kv3.Name.ToString()].Equals(kv3.Value.ToString()))
                                        {
                                            MakeHistory(subb.Key, subb.Value.ChangeNumber, "modified_key", "extended_" + kv3.Name.ToString(), subdata["extended_" + kv3.Name.ToString()].ToString(), kv3.Value.ToString());
                                        }
                                        subdata.Remove("extended_" + kv3.Name.ToString());
                                    }
                                    else
                                    {
                                        MakeHistory(subb.Key, subb.Value.ChangeNumber, "created_key", "extended_" + kv3.Name.ToString(), "", kv3.Value.ToString());
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
                                        new MySqlParameter("@SubID", subb.Key),
                                        new MySqlParameter("@AppID", kv3.Value),
                                        new MySqlParameter("@Type", type)
                                    });

                                    if (type == "app")
                                    {
                                        if (!subapps.Contains(kv3.Value))
                                        {
                                            MakeHistory(subb.Key, subb.Value.ChangeNumber, "added_to_sub", "0", "", kv3.Value, true);
                                        }
                                        else
                                        {
                                            subapps.Remove(kv3.Value);
                                        }
                                        
                                    }
                                    else if (type == "depot")
                                    {
                                        if (!subdepots.Contains(kv3.Value))
                                        {
                                            MakeHistory(subb.Key, subb.Value.ChangeNumber, "added_to_sub", "1", "", kv3.Value, true);
                                        }
                                        else
                                        {
                                            subdepots.Remove(kv3.Value);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                StringBuilder sb = new StringBuilder();
                                StringWriter sw = new StringWriter(sb);
                                String json = "";
                                using (JsonWriter w = new JsonTextWriter(sw))
                                {
                                    List<KeyValue> fullarray = kv2.Children;
                                    WriteKey(w, fullarray);
                                }
                                json = sw.ToString();

                                List<MySqlParameter> parameters = new List<MySqlParameter>();

                                DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNamesSubs (Type, Name, DisplayName) VALUES (99, @KeyName, @KeyName)",
                                    new MySqlParameter[]
                                    {
                                        new MySqlParameter("@KeyName", "marlamin" + "_" + kv2.Name)
                                    });

                                MakeSubsInfo(subb.Key, "marlamin_" + kv2.Name, json);

                                if (subdata.ContainsKey("marlamin_" + kv2.Name.ToString()))
                                {
                                    if (!subdata["marlamin_" + kv2.Name.ToString()].Equals(json))
                                    {
                                        MakeHistory(subb.Key, subb.Value.ChangeNumber, "modified_key", "marlamin_" + kv2.Name.ToString(), subdata["marlamin_" + kv2.Name.ToString()].ToString(), json); 
                                    }
                                }
                                else
                                {
                                    MakeHistory(subb.Key, subb.Value.ChangeNumber, "created_key", "marlamin_" + kv2.Name.ToString(), "", json);
                                }
                                subdata.Remove("marlamin" + "_" + kv2.Name);
                            }
                        }
                    }
                }

            }
            foreach (String key in subdata.Keys)
            {
                if (!key.StartsWith("website"))
                {
                    DbWorker.ExecuteNonQuery("DELETE FROM SubsInfo WHERE `SubID` = @SubID AND `Key` = (SELECT ID from KeyNamesSubs WHERE Name = @KeyName LIMIT 1)",
                    new MySqlParameter[]
                    {
                        new MySqlParameter("@SubID", subb.Key),
                        new MySqlParameter("@KeyName", key)
                    });
                    MakeHistory(subb.Key, subb.Value.ChangeNumber, "removed_key", key, subdata[key].ToString(), "");
                }
               
            }
            foreach (String key in subapps)
            {
                DbWorker.ExecuteNonQuery("DELETE FROM SubsApps WHERE SubID = @SubID AND AppID = @Key AND `Type` = 'app'", 
                new MySqlParameter[] { 
                    new MySqlParameter("@SubID", subb.Key),
                    new MySqlParameter("@Key", key)
                });
                MakeHistory(subb.Key, subb.Value.ChangeNumber, "removed_from_sub", "0", key, "", true);
            }
            foreach (String key in subdepots)
            {
                DbWorker.ExecuteNonQuery("DELETE FROM SubsApps WHERE SubID = @SubID AND AppID = @Key AND `Type` = 'depot'",
                new MySqlParameter[] { 
                    new MySqlParameter("@SubID", subb.Key),
                    new MySqlParameter("@Key", key)
                });
                MakeHistory(subb.Key, subb.Value.ChangeNumber, "removed_from_sub", "1", key, "", true);
            }
        }

        private static void MakeSubsInfo(uint SubID, string KeyName = "", string Value = "")
        {
            DbWorker.ExecuteNonQuery("INSERT INTO SubsInfo VALUES (@SubID, (SELECT ID from KeyNamesSubs WHERE Name = @KeyName LIMIT 1), @Value) ON DUPLICATE KEY UPDATE Value=@Value",
            new MySqlParameter[]
                    {
                        new MySqlParameter("@SubID", SubID),
                        new MySqlParameter("@KeyName", KeyName),
                        new MySqlParameter("@Value", Value)
                    });
        }
        private static void MakeHistory(uint SubID, uint ChangeNumber, string Action, string KeyName = "", string OldValue = "", string NewValue = "", bool keyoverride = false)
        {
            List<MySqlParameter> parameters = new List<MySqlParameter>();
            parameters.Add(new MySqlParameter("@SubID", SubID));
            parameters.Add(new MySqlParameter("@ChangeID", ChangeNumber));
            parameters.Add(new MySqlParameter("@Action", Action));
            parameters.Add(new MySqlParameter("@KeyName", KeyName));
            parameters.Add(new MySqlParameter("@OldValue", OldValue));
            parameters.Add(new MySqlParameter("@NewValue", NewValue));
            if (keyoverride == true || KeyName.Equals(""))
            {
                DbWorker.ExecuteNonQuery("INSERT INTO SubsHistory (ChangeID, SubID, `Action`, `Key`, OldValue, NewValue) VALUES (@ChangeID, @SubID, @Action, @KeyName, @OldValue, @NewValue)",
                parameters.ToArray());
            }
            else
            {
                DbWorker.ExecuteNonQuery("INSERT INTO SubsHistory (ChangeID, SubID, `Action`, `Key`, OldValue, NewValue) VALUES (@ChangeID, @SubID, @Action, (SELECT ID from KeyNamesSubs WHERE Name = @KeyName LIMIT 1), @OldValue, @NewValue)",
                parameters.ToArray());
            }
            parameters.Clear();
        }

        private static void WriteKey(JsonWriter w, List<KeyValue> keys)
        {
            w.WriteStartObject();
            foreach (KeyValue keyval in keys)
            {
                if (keyval.Children.Count == 0)
                {
                    if (keyval.Value != null)
                    {
                        WriteSubkey(w, keyval.Name.ToString(), keyval.Value.ToString());
                    }
                }
                else
                {
                    List<KeyValue> subkeys = keyval.Children;
                    w.WriteValue(keyval.Name.ToString());
                    WriteKey(w, subkeys);
                }
            }
            w.WriteEndObject();
        }

        private static void WriteSubkey(JsonWriter w, string name, string value)
        {
            w.WritePropertyName(name);
            w.WriteValue(value);
        }
    }
}
