/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
            if (Program.steam.fullRunOption > 0)
            {
                Console.WriteLine("Processing AppID: {0}", AppID);
            }

            Dictionary<string, string> appdata = new Dictionary<string, string>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `Name`, `Value` FROM AppsInfo INNER JOIN KeyNames ON AppsInfo.Key=KeyNames.ID WHERE AppID = @AppId", new MySqlParameter[]
                {
                new MySqlParameter("AppID", AppID)
                }))
            {
                while (Reader.Read())
                {
                    appdata.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
                }
            }

            String AppName = "";
            String AppType = "";

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `Name`, `AppType` FROM Apps WHERE AppID = @AppID", new MySqlParameter[]
                {
                new MySqlParameter("AppID", AppID)
                }))
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
                if (!ProductInfo.KeyValues["common"]["type"].Equals(""))
                {
                    using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT AppType FROM AppsTypes WHERE Name = @type LIMIT 1", new MySqlParameter[]
                    {
                        new MySqlParameter("type", ProductInfo.KeyValues["common"]["type"].Value)
                    }))
                    {
                        if (Reader.Read())
                        {
                            newAppType = DbWorker.GetString("AppType", Reader);
                        }
                    }
                }

                if (AppName.Equals("") || AppName.StartsWith("SteamDB Unknown App"))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO Apps (AppID, AppType, Name) VALUES (@AppId, @Type, @AppName) ON DUPLICATE KEY UPDATE `Name` = @AppName, `AppType` = @Type",
                        new MySqlParameter[]
                        {
                            new MySqlParameter("@AppId", AppID),
                            new MySqlParameter("@Type", newAppType),
                            new MySqlParameter("@AppName", ProductInfo.KeyValues["common"]["name"].Value)
                        });
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "created_app");
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "created_info", "10", "", ProductInfo.KeyValues["common"]["name"].Value, true);
                }
                else if (!AppName.Equals(ProductInfo.KeyValues["common"]["name"].Value))
                {
                    DbWorker.ExecuteNonQuery("UPDATE Apps SET Name = @AppName WHERE AppID = @AppId", new MySqlParameter[] { new MySqlParameter("@AppId", AppID), new MySqlParameter("@AppName", ProductInfo.KeyValues["common"]["name"].Value) });
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "modified_info", "10", AppName, ProductInfo.KeyValues["common"]["name"].Value, true);
                }

                if (AppType.Equals("") || AppType.Equals("0"))
                {
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "created_info", "9", "", newAppType, true);
                    DbWorker.ExecuteNonQuery("UPDATE Apps SET AppType = @type WHERE AppID = @AppId", new MySqlParameter[] { new MySqlParameter("@AppId", AppID), new MySqlParameter("@type", newAppType) });
                }

                else if (!AppType.Equals(newAppType))
                {
                    MakeHistory(AppID, ProductInfo.ChangeNumber, "modified_info", "9", AppType, newAppType, true);
                    DbWorker.ExecuteNonQuery("UPDATE Apps SET AppType = @type WHERE AppID = @AppId", new MySqlParameter[] { new MySqlParameter("@AppId", AppID), new MySqlParameter("@type", newAppType) });
                }
            }

            if (ProductInfo.KeyValues == null)
            {
                return;
            }

            #region HugeQuery
            foreach (KeyValue section in ProductInfo.KeyValues.Children)
            {
                String sectionName = section.Name;

                if (sectionName == "appid" || sectionName == "public_only")
                {
                    continue;
                }

                if (sectionName == "change_number")
                {
                    if (!appdata.ContainsKey("marlamin_change_number"))
                    {
                        DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNames(Name, DisplayName) VALUES('marlamin_change_number', 'change_number')");
                        MakeAppsInfo(AppID, "marlamin_change_number", section.Value);
                        MakeHistory(AppID, ProductInfo.ChangeNumber, "created_key", "marlamin_change_number", "", section.Value);
                    }
                    else
                    {
                        if (!appdata["marlamin_change_number"].Equals(section.Value))
                        {
                            MakeAppsInfo(AppID, "marlamin_change_number", section.Value);
                            MakeHistory(AppID, ProductInfo.ChangeNumber, "modified_key", "marlamin_change_number", appdata["marlamin_change_number"], section.Value);
                        }
                    }

                    appdata.Remove("marlamin_change_number");
                }
                else if (sectionName == "common" || sectionName == "extended")
                {
                    foreach (KeyValue keyvalue in section.Children)
                    {
                        String keynamecheck = sectionName.ToLower() + "_" + keyvalue.Name;

                        if (keynamecheck.Equals("common_type") || keynamecheck.Equals("common_gameid") || keynamecheck.Equals("common_name") || keynamecheck.Equals("extended_order"))
                        {
                            continue;
                        }
                        // TODO: This is godlike hackiness
                        else if
                        (
                        keynamecheck.StartsWith("extended_us ") ||
                        keynamecheck.StartsWith("extended_im ") ||
                        keynamecheck.StartsWith("extended_af ax al dz as ad ao ai aq ag ") ||
                        keynamecheck.Equals("extended_de") ||
                        keynamecheck.Equals("extended_jp") ||
                        keynamecheck.Equals("extended_cn") ||
                        keynamecheck.Equals("extended_us")
                        )
                        {
                            Console.WriteLine("Dammit Valve, why these long keynames: {0} - {1} ", AppID, keynamecheck);
                            continue;
                        }

                        List<KeyValue> subvalues = keyvalue.Children;
                        if (subvalues.Count == 0)
                        {
                            if (!keyvalue.Value.Equals(""))
                            {
                                if (appdata.ContainsKey(keynamecheck))
                                {
                                    if (!appdata[keynamecheck].Equals(keyvalue.Value))
                                    {
                                        MakeAppsInfo(AppID, keynamecheck, keyvalue.Value);
                                        MakeHistory(AppID, ProductInfo.ChangeNumber, "modified_key", keynamecheck, appdata[keynamecheck], keyvalue.Value);
                                    }
                                }
                                else
                                {
                                    DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNames(Name, DisplayName) VALUES(@KeyValueNameSection, @KeyValueName)",
                                    new MySqlParameter[]
                                    {
                                        new MySqlParameter("@KeyValueNameSection", keynamecheck),
                                        new MySqlParameter("@KeyValueName", keyvalue.Name)
                                    });
                                    MakeAppsInfo(AppID, keynamecheck, keyvalue.Value);
                                    MakeHistory(AppID, ProductInfo.ChangeNumber, "created_key", keynamecheck, "", keyvalue.Value);
                                }
                                appdata.Remove(keynamecheck);
                            }
                        }
                        else
                        {
                            string generated_value = "";
                            if (keynamecheck.Equals("common_languages"))
                            {
                                int language_count = 1;
                                foreach (KeyValue subvalue in subvalues)
                                {
                                    generated_value += subvalue.Name;
                                    if (language_count != subvalues.Count)
                                    {
                                        generated_value += ",";
                                    }
                                    language_count++;
                                }
                            }
                            else
                            {
                                using(StringWriter sw = new StringWriter(new StringBuilder()))
                                {
                                    using (JsonWriter w = new JsonTextWriter(sw))
                                    {
                                        DbWorker.JsonifyKeyValue(w, keyvalue.Children);
                                    }

                                    generated_value = sw.ToString();
                                }
                            }

                            if (appdata.ContainsKey(keynamecheck))
                            {
                                if (!appdata[keynamecheck].Equals(generated_value))
                                {
                                    MakeAppsInfo(AppID, keynamecheck, generated_value);
                                    MakeHistory(AppID, ProductInfo.ChangeNumber, "modified_key", keynamecheck, appdata[keynamecheck], generated_value);
                                }
                            }
                            else
                            {
                                DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNames(Name, DisplayName) VALUES(@KeyValueNameSection, @KeyValueName)",
                                    new MySqlParameter[]
                                    {
                                        new MySqlParameter("@KeyValueNameSection", keynamecheck),
                                        new MySqlParameter("@KeyValueName", keyvalue.Name)
                                    });

                                MakeAppsInfo(AppID, keynamecheck, generated_value);
                                MakeHistory(AppID, ProductInfo.ChangeNumber, "created_key", keynamecheck, "", generated_value);
                            }

                            appdata.Remove(keynamecheck);
                        }
                        // more stuff in sub arrays?
                    }
                }
                else
                {
                    sectionName = "marlamin" + "_" + section.Name.ToLower();

                    string json = "";

                    using(StringWriter sw = new StringWriter(new StringBuilder()))
                    {
                        using (JsonWriter w = new JsonTextWriter(sw))
                        {
                            DbWorker.JsonifyKeyValue(w, section.Children);
                        }

                        json = sw.ToString();
                    }

                    if (appdata.ContainsKey(sectionName))
                    {
                        if (!appdata[sectionName].Equals(json))
                        {
                            MakeAppsInfo(AppID, sectionName, json);
                            MakeHistory(AppID, ProductInfo.ChangeNumber, "modified_key", sectionName, appdata[sectionName], json);
                        }
                    }
                    else
                    {
                        DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNames (Type, Name, DisplayName) VALUES (99, @KeyName, @KeyName)",
                            new MySqlParameter[]
                            {
                                new MySqlParameter("@KeyName", sectionName)
                            });

                        MakeAppsInfo(AppID, sectionName, json);
                        MakeHistory(AppID, ProductInfo.ChangeNumber, "created_key", sectionName, "", json);
                    }

                    appdata.Remove(sectionName);
                }
            }
            #endregion

            foreach (String key in appdata.Keys)
            {
                if (!key.StartsWith("website"))
                {
                    DbWorker.ExecuteNonQuery("DELETE FROM AppsInfo WHERE `AppID` = @AppId AND `Key` = (SELECT ID from KeyNames WHERE Name = @KeyName LIMIT 1)",
                        new MySqlParameter[]
                        {
                            new MySqlParameter("@AppId", AppID),
                            new MySqlParameter("@KeyName", key)
                        });

                    MakeHistory(AppID, ProductInfo.ChangeNumber, "removed_key", key, appdata[key], "");
                }
            }

            if (ProductInfo.KeyValues["common"]["name"].Value == null)
            {
                if (AppName.Equals(""))
                {
                    //we never knew it
                    DbWorker.ExecuteNonQuery("INSERT INTO Apps (AppID, Name) VALUES (@AppId, @AppName)",
                        new MySqlParameter[]
                        {
                            new MySqlParameter("@AppId", AppID),
                            new MySqlParameter("@AppName", "SteamDB Unknown App " + AppID)
                        });
                }
                else
                {
                    //app name is not empty in db
                    if (!AppName.StartsWith("SteamDB Unknown App"))
                    {
                        MakeHistory(AppID, ProductInfo.ChangeNumber, "deleted_app", "0", AppName, "", true);

                        DbWorker.ExecuteNonQuery("UPDATE Apps SET Name = @AppName, AppType = 0 WHERE AppID = @AppId",
                            new MySqlParameter[] {
                                new MySqlParameter("@AppId", AppID),
                                new MySqlParameter("@AppName", "SteamDB Unknown App " + AppID)
                            });
                    }
                }
            }
        }

        private static void MakeAppsInfo(uint AppID, string KeyName = "", string Value = "")
        {
            DbWorker.ExecuteNonQuery("INSERT INTO AppsInfo VALUES (@AppId, (SELECT ID from KeyNames WHERE Name = @KeyName LIMIT 1), @Value) ON DUPLICATE KEY UPDATE Value=@Value",
                new MySqlParameter[]
                {
                    new MySqlParameter("@AppId", AppID),
                    new MySqlParameter("@KeyName", KeyName),
                    new MySqlParameter("@Value", Value)
                });
        }

        private static void MakeHistory(uint AppID, uint ChangeNumber, string Action, string KeyName = "", string OldValue = "", string NewValue = "", bool keyoverride = false)
        {
            List<MySqlParameter> parameters = new List<MySqlParameter>();
            parameters.Add(new MySqlParameter("@AppID", AppID));
            parameters.Add(new MySqlParameter("@ChangeID", ChangeNumber));
            parameters.Add(new MySqlParameter("@Action", Action));
            parameters.Add(new MySqlParameter("@KeyName", KeyName));
            parameters.Add(new MySqlParameter("@OldValue", OldValue));
            parameters.Add(new MySqlParameter("@NewValue", NewValue));

            if (keyoverride == true || KeyName.Equals(""))
            {
                DbWorker.ExecuteNonQuery("INSERT INTO AppsHistory (ChangeID, AppID, `Action`, `Key`, OldValue, NewValue) VALUES (@ChangeID, @AppID, @Action, @KeyName, @OldValue, @NewValue)",
                    parameters.ToArray());
            }
            else
            {
                DbWorker.ExecuteNonQuery("INSERT INTO AppsHistory (ChangeID, AppID, `Action`, `Key`, OldValue, NewValue) VALUES (@ChangeID, @AppID, @Action, (SELECT ID from KeyNames WHERE Name = @KeyName LIMIT 1), @OldValue, @NewValue)",
                    parameters.ToArray());
            }
        }

        public void ProcessUnknown(uint AppID)
        {
            Console.WriteLine("Unknown AppID: {0}", AppID);

            String AppName = "";

            using (MySqlDataReader MainReader = DbWorker.ExecuteReader(@"SELECT `Name` FROM Apps WHERE AppID = @AppID",
                    new MySqlParameter[]
                    {
                        new MySqlParameter("AppID", AppID)
                    }))
            {
                if (!MainReader.Read())
                {
                    return;
                }

                AppName = DbWorker.GetString("Name", MainReader);
            }

            Dictionary<string, string> appdata = new Dictionary<string, string>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `Name`, `Value` FROM AppsInfo INNER JOIN KeyNames ON AppsInfo.Key=KeyNames.ID WHERE AppID = @AppId",
                    new MySqlParameter[]
                    {
                        new MySqlParameter("AppID", AppID)
                    }))
            {
                while (Reader.Read())
                {
                    appdata.Add(DbWorker.GetString("Name", Reader), DbWorker.GetString("Value", Reader));
                }
            }

            DbWorker.ExecuteNonQuery("DELETE FROM Apps WHERE AppID = @AppId",
                new MySqlParameter[] {
                    new MySqlParameter("@AppId", AppID)
                });

            DbWorker.ExecuteNonQuery("DELETE FROM AppsInfo WHERE AppID = @AppId",
                new MySqlParameter[] {
                    new MySqlParameter("@AppId", AppID)
                });

            DbWorker.ExecuteNonQuery("DELETE FROM Store WHERE AppID = @AppId",
                new MySqlParameter[] {
                    new MySqlParameter("@AppId", AppID)
                });

            foreach (String key in appdata.Keys)
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
