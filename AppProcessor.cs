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
using System.IO;
using Jayrock.Json;

namespace PICSUpdater
{
    class AppProcessor
    {
        public void ProcessApp(object appp)
        {
            Dictionary<string, string> appdata = new Dictionary<string, string>();
            KeyValuePair<UInt32, SteamApps.PICSProductInfoCallback.PICSProductInfo> app = (KeyValuePair<UInt32, SteamApps.PICSProductInfoCallback.PICSProductInfo>)appp;
            MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT * FROM AppsInfo INNER JOIN KeyNames ON AppsInfo.Key=KeyNames.ID WHERE AppID = @AppId", new MySqlParameter[]
                {
                    new MySqlParameter("AppID", app.Value.ID)
                });
            while (Reader.Read())
            {
                appdata.Add(GetDBString("Name", Reader), GetDBString("Value", Reader));
            }
            Reader.Close();
            Reader.Dispose();

            String AppName = "";
            String AppType = "";

            MySqlDataReader MainReader = DbWorker.ExecuteReader(@"SELECT `Name`, `AppType` FROM Apps WHERE AppID = @AppID", new MySqlParameter[]
                {
                    new MySqlParameter("AppID", app.Value.ID)
                });

            if (MainReader.Read())
            {
                AppName = GetDBString("Name", MainReader);
                AppType = GetDBString("AppType", MainReader);
            }
            MainReader.Close();
            MainReader.Dispose();

            if (app.Value.KeyValues["common"]["name"].Value != null)
            {
                string newAppType = "0";
                if (!app.Value.KeyValues["common"]["type"].Equals(""))
                {
                    newAppType = getType(app.Value.KeyValues["common"]["type"].Value); // Value.ToString() ?? other part of code had this // also this is the only time getType() is used, you can get rid of it, if you want
                }

                if (AppName.Equals("") || AppName.StartsWith("SteamDB Unknown App"))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO Apps (AppID, AppType, Name) VALUES (@AppId, @Type, @AppName) ON DUPLICATE KEY UPDATE `Name` = @AppName, `AppType` = @Type", new MySqlParameter[] { new MySqlParameter("@AppId", app.Value.ID), new MySqlParameter("@Type", newAppType), new MySqlParameter("@AppName", app.Value.KeyValues["common"]["name"].Value.ToString()) });
                    MakeHistory(app.Value.ID, app.Value.ChangeNumber, "created_app");
                    MakeHistory(app.Value.ID, app.Value.ChangeNumber, "created_info", "10", "", app.Value.KeyValues["common"]["name"].Value.ToString(), true);
                }
                else if (!AppName.Equals(app.Value.KeyValues["common"]["name"].Value.ToString()))
                {
                    DbWorker.ExecuteNonQuery("UPDATE Apps SET Name = @AppName WHERE AppID = @AppId", new MySqlParameter[] { new MySqlParameter("@AppId", app.Value.ID), new MySqlParameter("@AppName", app.Value.KeyValues["common"]["name"].Value.ToString()) });
                    MakeHistory(app.Value.ID, app.Value.ChangeNumber, "modified_info", "10", AppName, app.Value.KeyValues["common"]["name"].Value.ToString(), true);
                }

                if (AppType.Equals("") || AppType.Equals("0"))
                {
                    MakeHistory(app.Value.ID, app.Value.ChangeNumber, "created_info", "9", "", newAppType, true);
                    DbWorker.ExecuteNonQuery("UPDATE Apps SET AppType = @type WHERE AppID = @AppId", new MySqlParameter[] { new MySqlParameter("@AppId", app.Value.ID), new MySqlParameter("@type", newAppType) });
                }

                else if (!AppType.Equals(newAppType))
                {
                    MakeHistory(app.Value.ID, app.Value.ChangeNumber, "modified_info", "9", AppType, newAppType, true);
                    DbWorker.ExecuteNonQuery("UPDATE Apps SET AppType = @type WHERE AppID = @AppId", new MySqlParameter[] { new MySqlParameter("@AppId", app.Value.ID), new MySqlParameter("@type", newAppType) });
                }
            }

            #region HugeQuery
            if (app.Value.KeyValues == null)
            {
                return;
            }

            foreach (KeyValue section in app.Value.KeyValues.Children)
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
                        MakeAppsInfo(app.Value.ID, "marlamin_change_number", section.Value);
                        MakeHistory(app.Value.ID, app.Value.ChangeNumber, "created_key", "marlamin_change_number", "", section.Value);
                    }
                    else
                    {
                        if (!appdata["marlamin_change_number"].Equals(section.Value))
                        {
                            MakeAppsInfo(app.Value.ID, "marlamin_change_number", section.Value);
                            MakeHistory(app.Value.ID, app.Value.ChangeNumber, "modified_key", "marlamin_change_number", appdata["marlamin_change_number"], section.Value);
                        }
                    }
                    appdata.Remove("marlamin_change_number");
                }else if (sectionName == "common" || sectionName == "extended"){
                    foreach (KeyValue keyvalue in section.Children)
                    {
                        String keynamecheck = sectionName.ToLower() + "_" + keyvalue.Name.ToString();
                        if (keynamecheck.Equals("common_type") || keynamecheck.Equals("common_gameid") || keynamecheck.Equals("common_name") || keynamecheck.Equals("extended_order"))
                        {
                            continue;
                        }
                        else if 
                        (
                        keynamecheck.StartsWith("extended_us ") ||
                        keynamecheck.StartsWith("extended_im ") ||
                        keynamecheck.Equals("extended_de") ||
                        keynamecheck.Equals("extended_jp") ||
                        keynamecheck.Equals("extended_af ax al dz as ad ao ai aq ag am aw at az au nz bh bd bb by be bj bm bt ba bw bv io bn bg bf bi kh cm cv ky cf td cn cx cc km cg cd ck ci hr cy cz dk dj dm do eg gq er ee et fk fo fj fi fr pf tf ga gm ge de gh gi gr gl gd gp gu gn gw gg ht hm va hk hu is in id ie im il it jm jp je jo kz ke ki kr kw kg la lv lb ls lr li lt lu mo mk mg mw my mv ml mt mh mq mr mu yt fm md mc mn me ms ma mz na nr np nl an nc nz ne ng nu nf mp no om pk pw ps pg ph pn pl pt qa re ro ru rw sh kn lc pm vc ws sm st sa sn rs sc sl sg sk si sb so za gs es lk sj sz se ch tw tj tz th tl tg tk to tt tn tr tm tc tv ug ua ae gb um uz vu vn vg vi wf eh ye zm zw") ||
                        keynamecheck.Equals("extended_cn") ||
                        keynamecheck.Equals("extended_us")
                        )
                        {
                            Console.WriteLine("Dammit Valve, why these long keynames? " + app.Value.ID + " " + keynamecheck);
                            continue;
                        }

                        List<KeyValue> subvalues = keyvalue.Children;
                        if (subvalues.Count == 0)
                        {
                            if (!keyvalue.Value.ToString().Equals(""))
                            {
                                if (appdata.ContainsKey(keynamecheck))
                                {
                                    if (!appdata[keynamecheck].Equals(keyvalue.Value.ToString()))
                                    {
                                        MakeAppsInfo(app.Value.ID, keynamecheck, keyvalue.Value.ToString());
                                        MakeHistory(app.Value.ID, app.Value.ChangeNumber, "modified_key", keynamecheck, appdata[keynamecheck].ToString(), keyvalue.Value.ToString());
                                    }
                                }
                                else
                                {
                                    DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNames(Name, DisplayName) VALUES(@KeyValueNameSection, @KeyValueName)",
                                    new MySqlParameter[]
                                    {
                                        new MySqlParameter("@KeyValueNameSection", keynamecheck),
                                        new MySqlParameter("@KeyValueName", keyvalue.Name.ToString())
                                    });
                                    MakeAppsInfo(app.Value.ID, keynamecheck, keyvalue.Value.ToString());
                                    MakeHistory(app.Value.ID, app.Value.ChangeNumber, "created_key", keynamecheck, "", keyvalue.Value.ToString());
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
                                StringBuilder sb = new StringBuilder();
                                StringWriter sw = new StringWriter(sb);
                                using (JsonWriter w = new JsonTextWriter(sw))
                                {
                                    List<KeyValue> fullarray = keyvalue.Children;
                                    WriteKey(w, fullarray);
                                }
                                generated_value = sw.ToString();
                            }

                            if (appdata.ContainsKey(keynamecheck))
                            {
                                if (!appdata[keynamecheck].Equals(generated_value))
                                {
                                    MakeAppsInfo(app.Value.ID, keynamecheck, generated_value);
                                    MakeHistory(app.Value.ID, app.Value.ChangeNumber, "modified_key", keynamecheck, appdata[keynamecheck].ToString(), generated_value);
                                }
                            }
                            else
                            {
                                DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNames(Name, DisplayName) VALUES(@KeyValueNameSection, @KeyValueName)",
                                new MySqlParameter[]
                                {
                                    new MySqlParameter("@KeyValueNameSection", keynamecheck),
                                    new MySqlParameter("@KeyValueName", keyvalue.Name.ToString())
                                });
                                MakeAppsInfo(app.Value.ID, keynamecheck, generated_value);
                                MakeHistory(app.Value.ID, app.Value.ChangeNumber, "created_key", keynamecheck, "", generated_value);
                            }
                            appdata.Remove(keynamecheck);
                        }
                        // more stuff in sub arrays?
                    }
                }
                else
                {
                    sectionName = "marlamin" + "_" + section.Name.ToString().ToLower();
                    StringBuilder sb = new StringBuilder();
                    StringWriter sw = new StringWriter(sb);
                    String json = "";
                    using (JsonWriter w = new JsonTextWriter(sw))
                    {
                        List<KeyValue> fullarray = section.Children;
                        WriteKey(w, fullarray);
                    }
                    json = sw.ToString();

                    List<MySqlParameter> parameters = new List<MySqlParameter>();

                    if (appdata.ContainsKey(sectionName))
                    {
                        if (!appdata[sectionName].Equals(json))
                        {
                            MakeAppsInfo(app.Value.ID, sectionName, json);
                            MakeHistory(app.Value.ID, app.Value.ChangeNumber, "modified_key", sectionName, appdata[sectionName], json);
                        }
                    }
                    else
                    {
                        DbWorker.ExecuteNonQuery("INSERT IGNORE INTO KeyNames (Type, Name, DisplayName) VALUES (99, @KeyName, @KeyName)",
                            new MySqlParameter[]
                        {
                            new MySqlParameter("@KeyName", sectionName)
                        });
                        MakeAppsInfo(app.Value.ID, sectionName, json);
                        MakeHistory(app.Value.ID, app.Value.ChangeNumber, "created_key", sectionName, "", json);
                    }
                    appdata.Remove(sectionName);
                }
            }

            foreach (String key in appdata.Keys)
            {
                if (!key.StartsWith("website"))
                {
                    DbWorker.ExecuteNonQuery("DELETE FROM AppsInfo WHERE `AppID` = @AppId AND `Key` = (SELECT ID from KeyNames WHERE Name = @KeyName LIMIT 1)",
                    new MySqlParameter[]
                    {
                        new MySqlParameter("@AppId", app.Value.ID),
                        new MySqlParameter("@KeyName", key)
                    });
                    MakeHistory(app.Value.ID, app.Value.ChangeNumber, "removed_key", key, appdata[key].ToString(), "");
                }
            }

            if (app.Value.KeyValues["common"]["name"].Value == null)
            {
                if (AppName.Equals(""))
                {
                    //we never knew it
                    DbWorker.ExecuteNonQuery("INSERT INTO Apps (AppID, Name) VALUES (@AppId, @AppName)", new MySqlParameter[] { 
                        new MySqlParameter("@AppId", app.Value.ID), 
                        new MySqlParameter("@AppName", "SteamDB Unknown App " + app.Value.ID) 
                    });
                }
                else
                {
                    //app name is not empty in db
                    if (!AppName.StartsWith("SteamDB Unknown App"))
                    {
                        MakeHistory(app.Value.ID, app.Value.ChangeNumber, "deleted_app", "0", AppName, "", true);
                        DbWorker.ExecuteNonQuery("UPDATE Apps SET Name = @AppName, AppType = 0 WHERE AppID = @AppId",
                            new MySqlParameter[] { 
                                new MySqlParameter("@AppId", app.Value.ID), 
                                new MySqlParameter("@AppName", "SteamDB Unknown App " + app.Value.ID) 
                            });
                    }
                }
            }
            #endregion
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
                    w.WriteMember(keyval.Name.ToString());
                    WriteKey(w, subkeys);
                }
            }
            w.WriteEndObject();
        }

        private static void WriteSubkey(JsonWriter w, string name, string value)
        {
            w.WriteMember(name);
            w.WriteString(value);
        }
        private string GetDBString(string SqlFieldName, MySqlDataReader Reader)
        {
            return Reader[SqlFieldName].Equals(DBNull.Value) ? String.Empty : Reader.GetString(SqlFieldName);
        }
        private string getType(string type)
        {
            string newtype = "0";
            MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT AppType FROM AppsTypes WHERE Name = @type LIMIT 1", new MySqlParameter[]
                {
                    new MySqlParameter("type", type)
                });
            while (Reader.Read())
            {
                newtype = GetDBString("AppType", Reader);
            }
            Reader.Close();
            Reader.Dispose();
            return newtype;
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
            parameters.Clear();
        }
        public void ProcessUnknownApp(object app)
        {
            uint appid = (uint)app;
            Console.WriteLine("Unknown app " + appid);

            MySqlDataReader MainReader = DbWorker.ExecuteReader(@"SELECT `Name` FROM Apps WHERE AppID = @AppID", new MySqlParameter[]
                {
                    new MySqlParameter("AppID", appid)
                });

            if (!MainReader.Read())
            {
                MainReader.Close();
                MainReader.Dispose();
                return;
            }
            
            String AppName = GetDBString("Name", MainReader);
            MainReader.Close();
            MainReader.Dispose();

            Dictionary<string, string> appdata = new Dictionary<string, string>();
            MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT * FROM AppsInfo INNER JOIN KeyNames ON AppsInfo.Key=KeyNames.ID WHERE AppID = @AppId", new MySqlParameter[]
                {
                    new MySqlParameter("AppID", appid)
                });
            while (Reader.Read())
            {
                appdata.Add(GetDBString("Name", Reader), GetDBString("Value", Reader));
            }
            Reader.Close();
            Reader.Dispose();

            DbWorker.ExecuteNonQuery("DELETE FROM Apps WHERE AppID = @AppId",
                new MySqlParameter[] { 
                    new MySqlParameter("@AppId", appid)
                });

            DbWorker.ExecuteNonQuery("DELETE FROM AppsInfo WHERE AppID = @AppId",
                new MySqlParameter[] { 
                    new MySqlParameter("@AppId", appid)
                });

            DbWorker.ExecuteNonQuery("DELETE FROM Store WHERE AppID = @AppId",
                new MySqlParameter[] { 
                    new MySqlParameter("@AppId", appid)
                });

            foreach (String key in appdata.Keys)
            {
                if (!key.StartsWith("website"))
                {
                    MakeHistory(appid, 0, "removed_key", key, appdata[key].ToString(), "");
                }
            }

            if (!AppName.StartsWith("SteamDB Unknown App"))
            {
                MakeHistory(appid, 0, "deleted_app", "0", AppName, "", true);
            }
        }
    }
}
