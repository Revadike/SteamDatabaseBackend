/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class AppCommand : Command
    {
        public AppCommand()
        {
            Trigger = "app";
            IsSteamCommand = true;

            Steam.Instance.CallbackManager.Subscribe<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo);
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} app <appid or partial game name>", Colors.OLIVE);

                return;
            }

            var count = PICSProductInfo.ProcessedApps.Count;

            if (count > 100)
            {
                CommandHandler.ReplyToCommand(command, "There are currently {0} apps awaiting to be processed, try again later.", count);

                return;
            }

            uint appID;

            if (!uint.TryParse(command.Message, out appID))
            {
                string name = command.Message;

                if (!Utils.ConvertUserInputToSQLSearch(ref name))
                {
                    CommandHandler.ReplyToCommand(command, "Your request is invalid or too short.");

                    return;
                }

                using (var db = Database.GetConnection())
                {
                    appID = db.ExecuteScalar<uint>("SELECT `AppID` FROM `Apps` WHERE `Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name OR `Apps`.`LastKnownName` LIKE @Name ORDER BY `LastUpdated` DESC LIMIT 1", new { Name = name });
                }

                if (appID == 0)
                {
                    CommandHandler.ReplyToCommand(command, "Nothing was found matching your request.");

                    return;
                }
            }

            var apps = new List<uint>();

            apps.Add(appID);

            JobManager.AddJob(
                () => Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>()), 
                new JobManager.IRCRequest
                {
                    Target = appID,
                    Type = JobManager.IRCRequestType.TYPE_APP,
                    Command = command
                }
            );
        }

        private static void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job) || !job.IsCommand)
            {
                return;
            }

            var request = job.CommandRequest;

            if (request.Type == JobManager.IRCRequestType.TYPE_SUB)
            {
                if (!callback.Packages.ContainsKey(request.Target))
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unknown SubID: {0}{1}{2}", Colors.BLUE, request.Target, LicenseList.OwnedSubs.ContainsKey(request.Target) ? SteamDB.StringCheckmark : string.Empty);

                    return;
                }

                var info = callback.Packages[request.Target];
                string name;

                if (info.KeyValues["name"].Value != null)
                {
                    name = Utils.RemoveControlCharacters(info.KeyValues["name"].AsString());
                }
                else
                {
                    name = Steam.GetPackageName(info.ID);
                }

                try
                {
                    info.KeyValues.SaveToFile(Path.Combine(Application.Path, "sub", string.Format("{0}.vdf", info.ID)), false);
                }
                catch (Exception e)
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unable to save file for {0}: {1}", name, e.Message);

                    return;
                }

                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2} -{3} {4}{5} - Dump:{6} {7}{8}{9}{10}",
                    Colors.BLUE, name, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetPackageURL(info.ID), Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetRawPackageURL(info.ID), Colors.NORMAL,
                    info.MissingToken ? SteamDB.StringNeedToken : string.Empty,
                    LicenseList.OwnedSubs.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty
                );
            }
            else if (request.Type == JobManager.IRCRequestType.TYPE_APP)
            {
                if (!callback.Apps.ContainsKey(request.Target))
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unknown AppID: {0}{1}{2}", Colors.BLUE, request.Target, LicenseList.OwnedApps.ContainsKey(request.Target) ? SteamDB.StringCheckmark : string.Empty);

                    return;
                }

                var info = callback.Apps[request.Target];
                string name;

                if (info.KeyValues["common"]["name"].Value != null)
                {
                    name = Utils.RemoveControlCharacters(info.KeyValues["common"]["name"].AsString());
                }
                else
                {
                    name = Steam.GetAppName(info.ID);
                }

                try
                {
                    info.KeyValues.SaveToFile(Path.Combine(Application.Path, "app", string.Format("{0}.vdf", info.ID)), false);
                }
                catch (Exception e)
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unable to save file for {0}: {1}", name, e.Message);

                    return;
                }

                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2} -{3} {4}{5} - Dump:{6} {7}{8}{9}{10}",
                    Colors.BLUE, name, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetAppURL(info.ID), Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetRawAppURL(info.ID), Colors.NORMAL,
                    info.MissingToken ? SteamDB.StringNeedToken : string.Empty,
                    LicenseList.OwnedApps.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty
                );
            }
            else
            {
                CommandHandler.ReplyToCommand(request.Command, "I have no idea what happened here!");
            }
        }
    }
}
