/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    class BlogCommand : Command
    {
        public BlogCommand()
        {
            Trigger = "blog";
        }

        public override async Task OnCommand(CommandArguments command)
        {
            BlogPost post;

            using (var db = Database.Get())
            {
                post = (await db.QueryAsync<BlogPost>("SELECT `ID`, `Slug`, `Title` FROM `Blog` WHERE `IsHidden` = 0 ORDER BY `Time` DESC LIMIT 1")).SingleOrDefault();
            }

            if (post.ID == 0)
            {
                command.Reply("No blog post found.");

                return;
            }

            command.Reply(
                command.Message.Length > 0 ?
                    "Blog post:{0} {1}{2} -{3} {4}" :
                    "Latest blog post:{0} {1}{2} -{3} {4}",

                Colors.BLUE, post.Title, Colors.NORMAL,
                Colors.DARKBLUE, SteamDB.GetBlogURL(post.Slug.Length > 0 ? post.Slug : post.ID.ToString())
            );
        }
    }
}
