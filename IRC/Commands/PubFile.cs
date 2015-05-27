/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Unified.Internal;

namespace SteamDatabaseBackend
{
    class PubFileCommand : Command
    {
        private readonly SteamUnifiedMessages.UnifiedService<IPublishedFile> PublishedFiles;
        private readonly Regex SharedFileMatch;

        public PubFileCommand()
        {
            Trigger = "pubfile";
            IsSteamCommand = true;

            SharedFileMatch = new Regex(
                @"(?:^|/|\.)steamcommunity\.com/sharedfiles/filedetails/(?:\?id=|comments/|changelog/|discussions/|)(?<pubfileid>[0-9]+)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture
            );

            PublishedFiles = Steam.Instance.Client.GetHandler<SteamUnifiedMessages>().CreateService<IPublishedFile>();
        }

        public void OnMessage(CommandArguments command)
        {
            var matches = SharedFileMatch.Matches(command.Message);

            foreach (Match match in matches)
            {
                var pubFileId = ulong.Parse(match.Groups["pubfileid"].Value);
                var pubFileRequest = new CPublishedFile_GetDetails_Request();

                pubFileRequest.publishedfileids.Add(pubFileId);

                JobManager.AddJob(
                    () => PublishedFiles.SendMessage(api => api.GetDetails(pubFileRequest)), 
                    new JobManager.IRCRequest
                    {
                        Type = JobManager.IRCRequestType.TYPE_PUBFILE_SILENT,
                        Command = command
                    }
                );
            }
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} pubfile <pubfileid>", Colors.OLIVE);

                return;
            }

            ulong pubFileId;

            if (!ulong.TryParse(command.Message, out pubFileId))
            {
                CommandHandler.ReplyToCommand(command, "Invalid Published File ID");

                return;
            }

            var pubFileRequest = new CPublishedFile_GetDetails_Request
            {
                includeadditionalpreviews = true,
                includetags = true,
                includekvtags = true,
                includevotes = true,
                //includeforsaledata = true, // TODO: Needs updated steamkit
            };

            pubFileRequest.publishedfileids.Add(pubFileId);

            JobManager.AddJob(
                () => PublishedFiles.SendMessage(api => api.GetDetails(pubFileRequest)), 
                new JobManager.IRCRequest
                {
                    Type = JobManager.IRCRequestType.TYPE_PUBFILE,
                    Command = command
                }
            );
        }

        public static void OnServiceMethod(SteamUnifiedMessages.ServiceMethodResponse callback, JobManager.IRCRequest request)
        {
            var response = callback.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
            var details = response.publishedfiledetails.FirstOrDefault();

            if (request.Type == JobManager.IRCRequestType.TYPE_PUBFILE_SILENT)
            {
                if (details == null || (EResult)details.result != EResult.OK)
                {
                    return;
                }

                string title;

                if (!string.IsNullOrWhiteSpace(details.title))
                {
                    title = details.title;
                }
                else if (!string.IsNullOrEmpty(details.file_description))
                {
                    title = details.file_description;
                }
                else
                {
                    title = details.filename;
                }

                if (title.Length > 49)
                {
                    title = title.Substring(0, 49) + "…";
                }

                if (request.Command.CommandType == ECommandType.SteamChatRoom)
                {
                    Steam.Instance.Friends.SendChatRoomMessage(request.Command.ChatRoomID, EChatEntryType.ChatMsg,
                        string.Format("» {0}: {1} for {2} ({3} views){4}", ((EWorkshopFileType)details.file_type), title, details.app_name, details.views, details.spoiler_tag ? " :retreat: SPOILER" : "")
                    );
                }
                else
                {
                    IRC.Instance.SendReply(request.Command.Recipient,
                        string.Format("{0}» {1}{2} {3}{4}{5} for {6}{7}{8} ({9} views)",
                            Colors.OLIVE,
                            Colors.NORMAL,
                            ((EWorkshopFileType)details.file_type),
                            Colors.BLUE,
                            title,
                            Colors.NORMAL,
                            Colors.BLUE,
                            details.app_name,
                            Colors.LIGHTGRAY,
                            details.views
                        ),
                        false
                    );
                }

                return;
            }

            if (details == null)
            {
                CommandHandler.ReplyToCommand(request.Command, "Unable to make service request for published file info: the server returned no info");

                return;
            }

            var result = (EResult)details.result;

            if (result != EResult.OK)
            {
                CommandHandler.ReplyToCommand(request.Command, "Unable to get published file info: {0}", result);

                return;
            }

            try
            {
                var json = JsonConvert.SerializeObject(details, Formatting.Indented);

                File.WriteAllText(Path.Combine(Application.Path, "ugc", string.Format("{0}.json", details.publishedfileid)), json, Encoding.UTF8);
            }
            catch (Exception e)
            {
                CommandHandler.ReplyToCommand(request.Command, "Unable to save file: {0}", e.Message);

                return;
            }

            CommandHandler.ReplyToCommand(request.Command, "{0}, Title: {1}{2}{3}, Creator: {4}{5}{6}, App: {7}{8}{9}{10}, File UGC: {11}{12}{13}, Preview UGC: {14}{15}{16} -{17} {18}",
                (EWorkshopFileType)details.file_type,
                Colors.BLUE, string.IsNullOrWhiteSpace(details.title) ? "[no title]" : details.title, Colors.NORMAL,
                Colors.BLUE, new SteamID(details.creator).Render(true), Colors.NORMAL,
                Colors.BLUE, details.creator_appid,
                details.creator_appid == details.consumer_appid ? "" : string.Format(" (consumer {0})", details.consumer_appid),
                Colors.NORMAL,
                Colors.BLUE, details.hcontent_file, Colors.NORMAL,
                Colors.BLUE, details.hcontent_preview, Colors.NORMAL,
                Colors.DARKBLUE, SteamDB.GetUGCURL(details.publishedfileid)
            );

            CommandHandler.ReplyToCommand(request.Command, true, "{0} - https://steamcommunity.com/sharedfiles/filedetails/?id={1}", details.file_url, details.publishedfileid);
        }
    }
}
