/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
                @"(?:^|/|\.)steamcommunity\.com/sharedfiles/filedetails/(?:\?id=|comments/|changelog/|discussions/|)(?<pubfileid>[0-9]{1,20})",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture
            );

            PublishedFiles = Steam.Instance.Client.GetHandler<SteamUnifiedMessages>().CreateService<IPublishedFile>();
        }

        public async void OnMessage(CommandArguments command)
        {
            var matches = SharedFileMatch.Matches(command.Message);

            foreach (Match match in matches)
            {
                if (!ulong.TryParse(match.Groups["pubfileid"].Value, out var pubFileId))
                {
                    continue;
                }

                Log.WriteInfo("Link Expander", "Will look up pubfile {0} for {1}", pubFileId, command);

                var pubFileRequest = new CPublishedFile_GetDetails_Request
                {
                    includevotes = true,
                };

                pubFileRequest.publishedfileids.Add(pubFileId);

                PublishedFileDetails details;

                try
                {
                    var callback = await PublishedFiles.SendMessage(api => api.GetDetails(pubFileRequest));
                    var response = callback.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
                    details = response.publishedfiledetails.FirstOrDefault();
                }
                catch (Exception e)
                {
                    Log.WriteError("Link Expander", "Failed to get pubfile details: {0}", e.Message);

                    continue;
                }

                if (details == null || (EResult)details.result != EResult.OK)
                {
                    continue;
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

                var votesUp = details.vote_data?.votes_up ?? 0;
                var votesDown = details.vote_data?.votes_down ?? 0;

                if (command.CommandType == ECommandType.SteamChatRoom)
                {
                    Steam.Instance.Friends.SendChatRoomMessage(command.ChatRoomID, EChatEntryType.ChatMsg,
                        string.Format("» {0}: {1} for {2} ({3:N0} views, {4:N0} \ud83d\udc4d, {5:N0} \ud83d\udc4e){6}",
                            (EWorkshopFileType)details.file_type,
                            title,
                            details.app_name,
                            details.views,
                            votesUp,
                            votesDown,
                            details.spoiler_tag ? " :retreat: SPOILER" : ""
                        )
                    );
                }
                else
                {
                    IRC.Instance.SendReply(command.Recipient,
                        string.Format("{0}» {1}{2} {3}{4}{5} for {6}{7}{8} ({9:N0} views, {10:N0} \ud83d\udc4d, {11:N0} \ud83d\udc4e)",
                            Colors.OLIVE,
                            Colors.NORMAL,
                            (EWorkshopFileType)details.file_type,
                            Colors.BLUE,
                            title,
                            Colors.NORMAL,
                            Colors.BLUE,
                            details.app_name,
                            Colors.LIGHTGRAY,
                            details.views,
                            votesUp,
                            votesDown
                        ),
                        false
                    );
                }
            }
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                command.Reply("Usage:{0} pubfile <pubfileid>", Colors.OLIVE);

                return;
            }

            if (!ulong.TryParse(command.Message, out var pubFileId))
            {
                command.Reply("Invalid Published File ID");

                return;
            }

            var pubFileRequest = new CPublishedFile_GetDetails_Request
            {
                includeadditionalpreviews = true,
                includechildren = true,
                includetags = true,
                includekvtags = true,
                includevotes = true,
                includeforsaledata = true,
                includemetadata = true,
            };

            pubFileRequest.publishedfileids.Add(pubFileId);

            var callback = await PublishedFiles.SendMessage(api => api.GetDetails(pubFileRequest));
            var response = callback.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
            var details = response.publishedfiledetails.FirstOrDefault();

            if (details == null)
            {
                command.Reply("Unable to make service request for published file info: the server returned no info");

                return;
            }

            var result = (EResult)details.result;

            if (result != EResult.OK)
            {
                command.Reply("Unable to get published file info: {0}{1}", Colors.RED, result);

                return;
            }

            var json = JsonConvert.SerializeObject(details, Formatting.Indented);

            File.WriteAllText(Path.Combine(Application.Path, "ugc", string.Format("{0}.json", details.publishedfileid)), json, Encoding.UTF8);

            command.Reply("{0}, Title: {1}{2}{3}, Creator: {4}{5}{6}, App: {7}{8}{9}{10}, File UGC: {11}{12}{13}, Preview UGC: {14}{15}{16} -{17} {18}",
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

            command.Notice("{0} - https://steamcommunity.com/sharedfiles/filedetails/?id={1}", details.file_url, details.publishedfileid);
        }
    }
}
