/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class SubProcessor : BaseProcessor
    {
        public const string HistoryQuery = "INSERT INTO `SubsHistory` (`ChangeID`, `SubID`, `Action`, `Key`, `OldValue`, `NewValue`) VALUES (@ChangeID, @ID, @Action, @Key, @OldValue, @NewValue)";

        private string PackageName;
        private Dictionary<string, PICSInfo> CurrentData;
        private uint ChangeNumber;
        private readonly uint SubID;

        public SubProcessor(uint subID, SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo)
        {
            SubID = subID;
            ProductInfo = productInfo;

            // Even though there won't be any problems with appids and subids colliding, they'll just wait for each other
            // We just add one billion to prevent unnecessary waiting
            Id = subID + 1000000000;
        }

        protected override AsyncJob RefreshSteam()
        {
            return Steam.Instance.Apps.PICSGetProductInfo(null, SubID, false, false);
        }

        protected override async Task LoadData()
        {
            PackageName = await DbConnection.ExecuteScalarAsync<string>("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID LIMIT 1", new { SubID });
            CurrentData = (await DbConnection.QueryAsync<PICSInfo>("SELECT `Name` as `KeyName`, `Value`, `Key` FROM `SubsInfo` INNER JOIN `KeyNamesSubs` ON `SubsInfo`.`Key` = `KeyNamesSubs`.`ID` WHERE `SubID` = @SubID", new { SubID })).ToDictionary(x => x.KeyName, x => x);
        }

        protected override async Task ProcessData()
        {
            await LoadData();

            ChangeNumber = ProductInfo.ChangeNumber;

            if (Settings.IsFullRun)
            {
                await DbConnection.ExecuteAsync("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeNumber) ON DUPLICATE KEY UPDATE `Date` = `Date`", new { ProductInfo.ChangeNumber });
                await DbConnection.ExecuteAsync("INSERT INTO `ChangelistsSubs` (`ChangeID`, `SubID`) VALUES (@ChangeNumber, @SubID) ON DUPLICATE KEY UPDATE `SubID` = `SubID`", new { SubID, ProductInfo.ChangeNumber });
            }

            await ProcessKey("root_changenumber", "changenumber", ChangeNumber.ToString());

            var appAddedToThisPackage = false;
            var packageOwned = LicenseList.OwnedSubs.ContainsKey(SubID);
            var newPackageName = ProductInfo.KeyValues["name"].AsString();
            var apps = (await DbConnection.QueryAsync<PackageApp>("SELECT `AppID`, `Type` FROM `SubsApps` WHERE `SubID` = @SubID", new { SubID })).ToDictionary(x => x.AppID, x => x.Type);

            // TODO: Ideally this should be SteamDB Unknown Package and proper checks like app processor does
            if (newPackageName == null)
            {
                newPackageName = string.Concat("Steam Sub ", SubID);
            }

            if (newPackageName != null)
            {
                if (string.IsNullOrEmpty(PackageName))
                {
                    await DbConnection.ExecuteAsync("INSERT INTO `Subs` (`SubID`, `Name`, `LastKnownName`) VALUES (@SubID, @Name, @Name)", new { SubID, Name = newPackageName });

                    await MakeHistory("created_sub");
                    await MakeHistory("created_info", SteamDB.DATABASE_NAME_TYPE, string.Empty, newPackageName);
                }
                else if (!PackageName.Equals(newPackageName))
                {
                    if (newPackageName.StartsWith("Steam Sub ", StringComparison.Ordinal))
                    {
                        await DbConnection.ExecuteAsync("UPDATE `Subs` SET `Name` = @Name WHERE `SubID` = @SubID", new { SubID, Name = newPackageName });
                    }
                    else
                    {
                        await DbConnection.ExecuteAsync("UPDATE `Subs` SET `Name` = @Name, `LastKnownName` = @Name WHERE `SubID` = @SubID", new { SubID, Name = newPackageName });
                    }

                    await MakeHistory("modified_info", SteamDB.DATABASE_NAME_TYPE, PackageName, newPackageName);
                }
            }

            foreach (var section in ProductInfo.KeyValues.Children)
            {
                var sectionName = section.Name.ToLower();

                if (string.IsNullOrEmpty(sectionName) || sectionName.Equals("packageid") || sectionName.Equals("changenumber") || sectionName.Equals("name"))
                {
                    // Ignore common keys
                    continue;
                }

                if (sectionName.Equals("appids") || sectionName.Equals("depotids"))
                {
                    // Remove "ids", so we get "app" from appids and "depot" from depotids
                    var type = sectionName.Replace("ids", string.Empty);
                    var isAppSection = type.Equals("app");
                    var typeID = (uint)(isAppSection ? 0 : 1); // 0 = app, 1 = depot; can't store as string because it's in the `key` field

                    foreach (var childrenApp in section.Children)
                    {
                        var appID = uint.Parse(childrenApp.Value);

                        // Is this appid already in this package?
                        if (apps.ContainsKey(appID))
                        {
                            // Is this appid's type the same?
                            if (apps[appID] != type)
                            {
                                await DbConnection.ExecuteAsync("UPDATE `SubsApps` SET `Type` = @Type WHERE `SubID` = @SubID AND `AppID` = @AppID", new {SubID, AppID = appID, Type = type});
                                await MakeHistory("added_to_sub", typeID, apps[appID].Equals("app") ? "0" : "1", childrenApp.Value);

                                appAddedToThisPackage = true;

                                // TODO: Log relevant add/remove history for depot/app?
                            }

                            apps.Remove(appID);
                        }
                        else
                        {
                            await DbConnection.ExecuteAsync("INSERT INTO `SubsApps` (`SubID`, `AppID`, `Type`) VALUES(@SubID, @AppID, @Type)", new { SubID, AppID = appID, Type = type });
                            await MakeHistory("added_to_sub", typeID, string.Empty, childrenApp.Value);

                            if (isAppSection)
                            {
                                await DbConnection.ExecuteAsync(AppProcessor.HistoryQuery,
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
                                await DbConnection.ExecuteAsync(DepotProcessor.HistoryQuery,
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
                    foreach (var children in section.Children)
                    {
                        var keyName = string.Format("{0}_{1}", sectionName, children.Name);

                        if (children.Children.Count > 0)
                        {
                            await ProcessKey(keyName, children.Name, Utils.JsonifyKeyValue(children), true);
                        }
                        else
                        {
                            await ProcessKey(keyName, children.Name, children.Value);
                        }
                    }
                }
                else if (section.Children.Any())
                {
                    sectionName = string.Format("root_{0}", sectionName);

                    await ProcessKey(sectionName, sectionName, Utils.JsonifyKeyValue(section), true);
                }
                else if (!string.IsNullOrEmpty(section.Value))
                {
                    var keyName = string.Format("root_{0}", sectionName);

                    await ProcessKey(keyName, sectionName, section.Value);
                }
            }

            foreach (var data in CurrentData.Values.Where(data => !data.Processed && !data.KeyName.StartsWith("website", StringComparison.Ordinal)))
            {
                await DbConnection.ExecuteAsync("DELETE FROM `SubsInfo` WHERE `SubID` = @SubID AND `Key` = @Key", new { SubID, data.Key });
                await MakeHistory("removed_key", data.Key, data.Value);
            }

            var appsRemoved = apps.Any();

            foreach (var app in apps)
            {
                await DbConnection.ExecuteAsync("DELETE FROM `SubsApps` WHERE `SubID` = @SubID AND `AppID` = @AppID AND `Type` = @Type", new { SubID, AppID = app.Key, Type = app.Value });

                var isAppSection = app.Value.Equals("app");

                var typeID = (uint)(isAppSection ? 0 : 1); // 0 = app, 1 = depot; can't store as string because it's in the `key` field

                await MakeHistory("removed_from_sub", typeID, app.Key.ToString());

                if (isAppSection)
                {
                    await DbConnection.ExecuteAsync(AppProcessor.HistoryQuery,
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
                    await DbConnection.ExecuteAsync(DepotProcessor.HistoryQuery,
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

            if (!packageOwned && SubID != 17906)
            {
                FreeLicense.RequestFromPackage(SubID, ProductInfo.KeyValues);
            }

            // Re-queue apps in this package so we can update depots and whatnot
            if (appAddedToThisPackage && !Settings.IsFullRun && !string.IsNullOrEmpty(PackageName))
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(ProductInfo.KeyValues["appids"].Children.Select(x => (uint)x.AsInteger()), Enumerable.Empty<uint>()));
            }

            // Maintain a list of anonymous content
            if (appAddedToThisPackage && SubID == 17906)
            {
                LicenseList.RefreshAnonymous();
            }
        }

        protected override async Task ProcessUnknown()
        {
            await LoadData();

            var data = CurrentData.Values.Where(x => !x.KeyName.StartsWith("website", StringComparison.Ordinal)).ToList();

            if (data.Any())
            {
                await DbConnection.ExecuteAsync(HistoryQuery, data.Select(x => new PICSHistory
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
                await MakeHistory("deleted_sub", 0, PackageName);
            }

            await DbConnection.ExecuteAsync("DELETE FROM `Subs` WHERE `SubID` = @SubID", new { SubID });
            await DbConnection.ExecuteAsync("DELETE FROM `SubsInfo` WHERE `SubID` = @SubID", new { SubID });
            await DbConnection.ExecuteAsync("DELETE FROM `SubsApps` WHERE `SubID` = @SubID", new { SubID });

            if (Settings.Current.CanQueryStore)
            {
                await DbConnection.ExecuteAsync("DELETE FROM `StoreSubs` WHERE `SubID` = @SubID", new { SubID });
            }
        }

        private async Task ProcessKey(string keyName, string displayName, string value, bool isJSON = false)
        {
            if (keyName.Length > 90)
            {
                Log.WriteError("Sub Processor", "Key {0} for SubID {1} is too long, not inserting info.", keyName, SubID);

                return;
            }

            // All keys in PICS are supposed to be lower case.
            // But currently some keys in packages are not lowercased,
            // this lowercases everything to make sure nothing breaks in future
            keyName = keyName.ToLower().Trim();

            if (!CurrentData.ContainsKey(keyName))
            {
                var key = KeyNameCache.GetSubKeyID(keyName);

                if (key == 0)
                {
                    var type = isJSON ? 86 : 0; // 86 is a hardcoded const for the website

                    key = await KeyNameCache.CreateSubKey(keyName, displayName, type);

                    if (key == 0)
                    {
                        // We can't insert anything because key wasn't created
                        Log.WriteError("Sub Processor", "Failed to create key {0} for SubID {1}, not inserting info.", keyName, SubID);

                        return;
                    }

                    IRC.Instance.SendOps("New package keyname: {0}{1} {2}(ID: {3}) ({4}) - {5}", Colors.BLUE, keyName, Colors.LIGHTGRAY, key, displayName, SteamDB.GetPackageURL(SubID, "history"));
                }

                await DbConnection.ExecuteAsync("INSERT INTO `SubsInfo` (`SubID`, `Key`, `Value`) VALUES (@SubID, @Key, @Value)", new { SubID, Key = key, Value = value });
                await MakeHistory("created_key", key, string.Empty, value);

                return;
            }

            var data = CurrentData[keyName];

            if (data.Processed)
            {
                Log.WriteWarn("Sub Processor", "Duplicate key {0} in SubID {1}", keyName, SubID);

                return;
            }

            data.Processed = true;

            CurrentData[keyName] = data;

            if (data.Value.Equals(value))
            {
                return;
            }

            await DbConnection.ExecuteAsync("UPDATE `SubsInfo` SET `Value` = @Value WHERE `SubID` = @SubID AND `Key` = @Key", new { SubID, data.Key, Value = value });
            await MakeHistory("modified_key", data.Key, data.Value, value);
        }

        private Task MakeHistory(string action, uint keyNameID = 0, string oldValue = "", string newValue = "")
        {
            return DbConnection.ExecuteAsync(HistoryQuery,
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
        
        public override string ToString()
        {
            return $"Package {SubID}";
        }
    }
}
