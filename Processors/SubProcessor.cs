/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class SubProcessor : IDisposable
    {
        private IDbConnection DbConnection;

        private readonly string PackageName;
        private readonly Dictionary<string, PICSInfo> CurrentData;
        private uint ChangeNumber;
        private readonly uint SubID;

        public SubProcessor(uint subID)
        {
            SubID = subID;

            DbConnection = Database.GetConnection();

            PackageName = DbConnection.ExecuteScalar<string>("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID LIMIT 1", new { SubID });
            CurrentData = DbConnection.Query<PICSInfo>("SELECT `Name` as `KeyName`, `Value`, `Key` FROM `SubsInfo` INNER JOIN `KeyNamesSubs` ON `SubsInfo`.`Key` = `KeyNamesSubs`.`ID` WHERE `SubID` = @SubID", new { SubID }).ToDictionary(x => x.KeyName, x => x);
        }

        public void Dispose()
        {
            if (DbConnection != null)
            {
                DbConnection.Dispose();
                DbConnection = null;
            }
        }

        public void Process(SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo)
        {
            ChangeNumber = productInfo.ChangeNumber;

            if (Settings.IsFullRun)
            {
#if DEBUG
                Log.WriteDebug("Sub Processor", "SubID: {0}", SubID);
#endif

                DbConnection.Execute("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeNumber) ON DUPLICATE KEY UPDATE `Date` = `Date`", new { productInfo.ChangeNumber });
                DbConnection.Execute("INSERT INTO `ChangelistsSubs` (`ChangeID`, `SubID`) VALUES (@ChangeNumber, @ID) ON DUPLICATE KEY UPDATE `SubID` = `SubID`", new { SubID, productInfo.ChangeNumber });
            }

            var appAddedToThisPackage = false;
            var packageOwned = LicenseList.OwnedSubs.ContainsKey(SubID);
            var newPackageName = productInfo.KeyValues["name"].AsString();
            var apps = DbConnection.Query<PackageApp>("SELECT `AppID`, `Type` FROM `SubsApps` WHERE `SubID` = @SubID", new { SubID }).ToDictionary(x => x.AppID, x => x.Type);

            // TODO: Ideally this should be SteamDB Unknown Package and proper checks like app processor does
            if (newPackageName == null)
            {
                newPackageName = string.Concat("Steam Sub ", SubID);
            }

            if (newPackageName != null)
            {
                if (string.IsNullOrEmpty(PackageName))
                {
                    DbConnection.Execute("INSERT INTO `Subs` (`SubID`, `Name`, `LastKnownName`) VALUES (@SubID, @Name, @Name)", new { SubID, Name = newPackageName });

                    MakeHistory("created_sub");
                    MakeHistory("created_info", SteamDB.DATABASE_NAME_TYPE, string.Empty, newPackageName);
                }
                else if (!PackageName.Equals(newPackageName))
                {
                    if (newPackageName.StartsWith("Steam Sub ", StringComparison.Ordinal))
                    {
                        DbConnection.Execute("UPDATE `Subs` SET `Name` = @Name WHERE `SubID` = @SubID", new { SubID, Name = newPackageName });
                    }
                    else
                    {
                        DbConnection.Execute("UPDATE `Subs` SET `Name` = @Name, `LastKnownName` = @Name WHERE `SubID` = @SubID", new { SubID, Name = newPackageName });
                    }

                    MakeHistory("modified_info", SteamDB.DATABASE_NAME_TYPE, PackageName, newPackageName);
                }
            }

            foreach (var section in productInfo.KeyValues.Children)
            {
                string sectionName = section.Name.ToLower();

                if (string.IsNullOrEmpty(sectionName) || sectionName.Equals("packageid") || sectionName.Equals("name"))
                {
                    // Ignore common keys
                    continue;
                }

                if (sectionName.Equals("appids") || sectionName.Equals("depotids"))
                {
                    // Remove "ids", so we get "app" from appids and "depot" from depotids
                    string type = sectionName.Replace("ids", string.Empty);

                    var isAppSection = type.Equals("app");

                    var typeID = (uint)(isAppSection ? 0 : 1); // 0 = app, 1 = depot; can't store as string because it's in the `key` field

                    foreach (var childrenApp in section.Children)
                    {
                        uint appID = uint.Parse(childrenApp.Value);

                        // Is this appid already in this package?
                        if (apps.ContainsKey(appID))
                        {
                            // Is this appid's type the same?
                            if (apps[appID] != type)
                            {
                                DbConnection.Execute("UPDATE `SubsApps` SET `Type` = @Type WHERE `SubID` = @SubID AND `AppID` = @AppID", new { SubID, AppID = appID, Type = type });

                                MakeHistory("added_to_sub", typeID, apps[appID].Equals("app") ? "0" : "1", childrenApp.Value);

                                appAddedToThisPackage = true;

                                // TODO: Log relevant add/remove history for depot/app?
                            }

                            apps.Remove(appID);
                        }
                        else
                        {
                            DbConnection.Execute("INSERT INTO `SubsApps` (`SubID`, `AppID`, `Type`) VALUES(@SubID, @AppID, @Type)", new { SubID, AppID = appID, Type = type });

                            MakeHistory("added_to_sub", typeID, string.Empty, childrenApp.Value);

                            if (isAppSection)
                            {
                                DbConnection.Execute(AppProcessor.GetHistoryQuery(),
                                    new PICSHistory
                                    {
                                        ID       = appID,
                                        ChangeID = ChangeNumber,
                                        NewValue = SubID.ToString(),
                                        Action   = "added_to_sub"
                                    }
                                );
                            }
                            else
                            {
                                DbConnection.Execute(DepotProcessor.GetHistoryQuery(),
                                    new DepotHistory
                                    {
                                        DepotID  = appID,
                                        ChangeID = ChangeNumber,
                                        NewValue = SubID,
                                        Action   = "added_to_sub"
                                    }
                                );
                            }

                            appAddedToThisPackage = true;

                            if (packageOwned && !LicenseList.OwnedApps.ContainsKey(appID))
                            {
                                LicenseList.OwnedApps.Add(appID, 1);
                            }
                        }
                    }
                }
                else if (sectionName.Equals("extended"))
                {
                    string keyName;

                    foreach (var children in section.Children)
                    {
                        keyName = string.Format("{0}_{1}", sectionName, children.Name);

                        if (children.Children.Count > 0)
                        {
                            ProcessKey(keyName, children.Name, Utils.JsonifyKeyValue(children), true);
                        }
                        else
                        {
                            ProcessKey(keyName, children.Name, children.Value);
                        }
                    }
                }
                else if (section.Children.Any())
                {
                    sectionName = string.Format("root_{0}", sectionName);

                    ProcessKey(sectionName, sectionName, Utils.JsonifyKeyValue(section), true);
                }
                else if (!string.IsNullOrEmpty(section.Value))
                {
                    string keyName = string.Format("root_{0}", sectionName);

                    ProcessKey(keyName, sectionName, section.Value);
                }
            }

            foreach (var data in CurrentData.Values)
            {
                if (!data.Processed && !data.KeyName.StartsWith("website", StringComparison.Ordinal))
                {
                    DbConnection.Execute("DELETE FROM `SubsInfo` WHERE `SubID` = @SubID AND `Key` = @Key", new { SubID, data.Key });

                    MakeHistory("removed_key", data.Key, data.Value);
                }
            }

            var appsRemoved = apps.Any();

            foreach (var app in apps)
            {
                DbConnection.Execute("DELETE FROM `SubsApps` WHERE `SubID` = @SubID AND `AppID` = @AppID AND `Type` = @Type", new { SubID, AppID = app.Key, Type = app.Value });

                var isAppSection = app.Value.Equals("app");

                var typeID = (uint)(isAppSection ? 0 : 1); // 0 = app, 1 = depot; can't store as string because it's in the `key` field

                MakeHistory("removed_from_sub", typeID, app.Key.ToString());

                if (isAppSection)
                {
                    DbConnection.Execute(AppProcessor.GetHistoryQuery(),
                        new PICSHistory
                        {
                            ID       = app.Key,
                            ChangeID = ChangeNumber,
                            OldValue = SubID.ToString(),
                            Action   = "removed_from_sub"
                        }
                    );
                }
                else
                {
                    DbConnection.Execute(DepotProcessor.GetHistoryQuery(),
                        new DepotHistory
                        {
                            DepotID  = app.Key,
                            ChangeID = ChangeNumber,
                            OldValue = SubID,
                            Action   = "removed_from_sub"
                        }
                    );
                }
            }

            if (appsRemoved)
            {
                LicenseList.RefreshApps();
            }

            if (productInfo.KeyValues["billingtype"].AsInteger() == 12 && !packageOwned) // 12 == free on demand
            {
                Log.WriteDebug("Sub Processor", "Requesting apps in SubID {0} as a free license", SubID);

                JobManager.AddJob(() => Steam.Instance.Apps.RequestFreeLicense(productInfo.KeyValues["appids"].Children.Select(appid => (uint)appid.AsInteger()).ToList()));
            }

            // Re-queue apps in this package so we can update depots and whatnot
            if (appAddedToThisPackage && !Settings.IsFullRun && !string.IsNullOrEmpty(PackageName))
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(productInfo.KeyValues["appids"].Children.Select(x => (uint)x.AsInteger()), Enumerable.Empty<uint>()));
            }
        }

        public void ProcessUnknown()
        {
            Log.WriteInfo("Sub Processor", "Unknown SubID: {0}", SubID);

            var data = CurrentData.Values.Where(x => !x.KeyName.StartsWith("website", StringComparison.Ordinal)).ToList();

            if (data.Any())
            {
                DbConnection.Execute(GetHistoryQuery(), data.Select(x => new PICSHistory
                {
                    ID       = SubID,
                    ChangeID = ChangeNumber,
                    Key      = x.Key,
                    OldValue = x.Value,
                    Action   = "removed_key"
                }));
            }

            if (!string.IsNullOrEmpty(PackageName))
            {
                MakeHistory("deleted_sub", 0, PackageName);
            }

            DbConnection.Execute("DELETE FROM `Subs` WHERE `SubID` = @SubID", new { SubID });
            DbConnection.Execute("DELETE FROM `SubsInfo` WHERE `SubID` = @SubID", new { SubID });
            DbConnection.Execute("DELETE FROM `SubsApps` WHERE `SubID` = @SubID", new { SubID });
            DbConnection.Execute("DELETE FROM `StoreSubs` WHERE `SubID` = @SubID", new { SubID });
        }

        private bool ProcessKey(string keyName, string displayName, string value, bool isJSON = false)
        {
            if (keyName.Length > 90)
            {
                Log.WriteError("Sub Processor", "Key {0} for SubID {1} is too long, not inserting info.", keyName, SubID);

                return false;
            }

            // All keys in PICS are supposed to be lower case.
            // But currently some keys in packages are not lowercased,
            // this lowercases everything to make sure nothing breaks in future
            keyName = keyName.ToLower().Trim();

            if (!CurrentData.ContainsKey(keyName))
            {
                uint key = GetKeyNameID(keyName);

                if (key == 0)
                {
                    var type = isJSON ? 86 : 0; // 86 is a hardcoded const for the website

                    DbConnection.Execute("INSERT INTO `KeyNamesSubs` (`Name`, `Type`, `DisplayName`) VALUES(@Name, @Type, @DisplayName)", new { Name = keyName, DisplayName = displayName, Type = type });

                    key = GetKeyNameID(keyName);

                    if (key == 0)
                    {
                        // We can't insert anything because key wasn't created
                        Log.WriteError("Sub Processor", "Failed to create key {0} for SubID {1}, not inserting info.", keyName, SubID);

                        return false;
                    }

                    IRC.Instance.SendOps("New package keyname: {0}{1} {2}(ID: {3}) ({4})", Colors.BLUE, keyName, Colors.LIGHTGRAY, key, displayName);
                }

                DbConnection.Execute("INSERT INTO `SubsInfo` (`SubID`, `Key`, `Value`) VALUES (@SubID, @Key, @Value)", new { SubID, Key = key, Value = value });
                MakeHistory("created_key", key, string.Empty, value);

                return true;
            }

            var data = CurrentData[keyName];

            if (data.Processed)
            {
                Log.WriteWarn("Sub Processor", "Duplicate key {0} in SubID {1}", keyName, SubID);

                return false;
            }

            data.Processed = true;

            CurrentData[keyName] = data;

            if (!data.Value.Equals(value))
            {
                DbConnection.Execute("UPDATE `SubsInfo` SET `Value` = @Value WHERE `SubID` = @SubID AND `Key` = @Key", new { SubID, Key = data.Key, Value = value });
                MakeHistory("modified_key", data.Key, data.Value, value);

                return true;
            }

            return false;
        }

        private void MakeHistory(string action, uint keyNameID = 0, string oldValue = "", string newValue = "")
        {
            DbConnection.Execute(GetHistoryQuery(),
                new PICSHistory
                {
                    ID       = SubID,
                    ChangeID = ChangeNumber,
                    Key      = keyNameID,
                    OldValue = oldValue,
                    NewValue = newValue,
                    Action   = action
                }
            );
        }

        public static string GetHistoryQuery()
        {
            return "INSERT INTO `SubsHistory` (`ChangeID`, `SubID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES (@ChangeID, @ID, @Action, @Key, @OldValue, @NewValue)";
        }

        private uint GetKeyNameID(string keyName)
        {
            return DbConnection.ExecuteScalar<uint>("SELECT `ID` FROM `KeyNamesSubs` WHERE `Name` = @keyName", new { keyName });
        }
    }
}
