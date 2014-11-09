/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using MySql.Data.MySqlClient;

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
            using (var reader = GetBlogQuery(command.Message))
            {
                if (reader.Read())
                {
                    var slug = reader.GetString("Slug");

                    if (slug.Length == 0)
                    {
                        slug = reader.GetString("ID");
                    }

                    CommandHandler.ReplyToCommand(
                        command,

                        command.Message.Length > 0 ?
                            "Blog post:{0} {1}{2} -{3} {4}" :
                            "Latest blog post:{0} {1}{2} -{3} {4}",

                        Colors.BLUE, reader.GetString("Title"), Colors.NORMAL,
                        Colors.DARKBLUE, SteamDB.GetBlogURL(slug)
                    );
                }
                else
                {
                    CommandHandler.ReplyToCommand(command, "No blog post found.");
                }
            }
        }

        private static MySqlDataReader GetBlogQuery(string input)
        {
            if (input.Length > 0)
            {
                return DbWorker.ExecuteReader("SELECT `ID`, `Slug`, `Title` FROM `Blog` WHERE `IsHidden` = 0 AND (`Slug` = @Slug OR `ID` = @Slug) LIMIT 1",
                    new MySqlParameter("@Slug", input)
                );
            }

            return DbWorker.ExecuteReader("SELECT `ID`, `Slug`, `Title` FROM `Blog` WHERE `IsHidden` = 0 ORDER BY `ID` DESC LIMIT 1");
        }
    }
}
