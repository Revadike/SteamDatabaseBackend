/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System.Linq;
using Dapper;

namespace SteamDatabaseBackend
{
    class BlogCommand : Command
    {
        public BlogCommand()
        {
            Trigger = "!blog";
        }

        public override void OnCommand(CommandArguments command)
        {
            BlogPost post;

            using (var db = Database.GetConnection())
            {
                if (command.Message.Length > 0)
                {
                    post = db.Query<BlogPost>("SELECT `ID`, `Slug`, `Title` FROM `Blog` WHERE `IsHidden` = 0 AND (`Slug` = @Slug OR `ID` = @Slug) LIMIT 1", new { Slug = command.Message }).SingleOrDefault();
                }
                else
                {
                    post = db.Query<BlogPost>("SELECT `ID`, `Slug`, `Title` FROM `Blog` WHERE `IsHidden` = 0 ORDER BY `Time` DESC LIMIT 1").SingleOrDefault();
                }
            }

            if (post.ID == 0)
            {
                CommandHandler.ReplyToCommand(command, "No blog post found.");

                return;
            }

            CommandHandler.ReplyToCommand(
                command,

                command.Message.Length > 0 ?
                    "Blog post:{0} {1}{2} -{3} {4}" :
                    "Latest blog post:{0} {1}{2} -{3} {4}",

                Colors.BLUE, post.Title, Colors.NORMAL,
                Colors.DARKBLUE, SteamDB.GetBlogURL(post.Slug.Length > 0 ? post.Slug : post.ID.ToString())
            );
        }
    }
}
