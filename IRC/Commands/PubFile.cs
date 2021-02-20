/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    internal class PubFileCommand : Command
    {
        private readonly SteamUnifiedMessages.UnifiedService<IPublishedFile> PublishedFiles;

        public PubFileCommand()
        {
            Trigger = "pubfile";
            IsSteamCommand = true;

            PublishedFiles = Steam.Instance.UnifiedMessages.CreateService<IPublishedFile>();
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                command.Reply($"Usage:{Colors.OLIVE} pubfile <pubfileid>");

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

            var task = PublishedFiles.SendMessage(api => api.GetDetails(pubFileRequest));
            task.Timeout = TimeSpan.FromSeconds(10);
            var callback = await task;
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
                command.Reply($"Unable to get published file info: {Colors.RED}{result}");

                return;
            }

            var json = JsonConvert.SerializeObject(details, Formatting.Indented);

            await File.WriteAllTextAsync(Path.Combine(Application.Path, "ugc", $"{details.publishedfileid}.json"), json, Encoding.UTF8);

            command.Reply($"{(EWorkshopFileType)details.file_type}, Title: {Colors.BLUE}{(string.IsNullOrWhiteSpace(details.title) ? "[no title]" : details.title)}{Colors.NORMAL}, Creator: {Colors.BLUE}{new SteamID(details.creator).Render()}{Colors.NORMAL}, App: {Colors.BLUE}{details.creator_appid}{(details.creator_appid == details.consumer_appid ? "" : $" (consumer {details.consumer_appid})")}{Colors.NORMAL}, File UGC: {Colors.BLUE}{details.hcontent_file}{Colors.NORMAL}, Preview UGC: {Colors.BLUE}{details.hcontent_preview}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetPublishedFileRawUrl(details.publishedfileid)}");

            command.Reply($"<{details.file_url}> - <https://steamcommunity.com/sharedfiles/filedetails/?id={details.publishedfileid}>");
        }
    }
}
