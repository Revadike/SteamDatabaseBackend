/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Xml;
using Dapper;

namespace SteamDatabaseBackend
{
    class RSS
    {
        private class Build
        {
            public uint BuildID { get; set; }
            public uint ChangeID { get; set; }
            public uint AppID { get; set; }
            public uint Official { get; set; }
            public DateTime Date { get; set; }
        }

        public class GenericFeedItem
        {
            public string Title { get; set; }
            public string Link { get; set; }
            public string Content { get; set; }
        }

        public Timer Timer { get; }

        public RSS()
        {
            Timer = new Timer();
            Timer.Elapsed += Tick;
            Timer.Interval = TimeSpan.FromSeconds(60).TotalMilliseconds;
            Timer.Start();

            Tick(null, null);
        }

        private static async void Tick(object sender, ElapsedEventArgs e)
        {
            var tasks = Settings.Current.RssFeeds.Select(ProcessFeed);

            await Task.WhenAll(tasks);
        }

        private static async Task ProcessFeed(Uri feed)
        {
            var rssItems = LoadRSS(feed, out var feedTitle);

            if (rssItems == null)
            {
                return;
            }

            if (rssItems.Count == 0)
            {
                Log.WriteError("RSS", "Did not find any items in {0}", feed);
                return;
            }

            using (var db = await Database.GetConnectionAsync())
            {
                var items = (await db.QueryAsync<GenericFeedItem>("SELECT `Link` FROM `RSS` WHERE `Link` IN @Ids", new { Ids = rssItems.Select(x => x.Link) })).ToDictionary(x => x.Link, x => (byte)1);

                var newItems = rssItems.Where(item => !items.ContainsKey(item.Link));

                foreach (var item in newItems)
                {
                    Log.WriteInfo("RSS", "[{0}] {1}: {2}", feedTitle, item.Title, item.Link);

                    IRC.Instance.SendMain("{0}{1}{2}: {3} -{4} {5}", Colors.BLUE, feedTitle, Colors.NORMAL, item.Title, Colors.DARKBLUE, item.Link);

                    await db.ExecuteAsync("INSERT INTO `RSS` (`Link`, `Title`) VALUES(@Link, @Title)", new { item.Link, item.Title });

                    if (!Settings.Current.CanQueryStore)
                    {
                        continue;
                    }

                    uint appID = 0;

                    if (feedTitle == "Steam RSS News Feed")
                    {
                        if (item.Title.StartsWith("Dota 2 Update", StringComparison.Ordinal))
                        {
                            appID = 570;
                        }
                        else if (item.Title == "Team Fortress 2 Update Released")
                        {
                            appID = 440;
                        }
                        else if (item.Title == "Left 4 Dead 2 - Update")
                        {
                            appID = 550;
                        }
                        else if (item.Title == "Left 4 Dead - Update")
                        {
                            appID = 500;
                        }
                        else if (item.Title == "Portal 2 - Update")
                        {
                            appID = 620;
                        }
                    }
                    else if (feedTitle.Contains("Counter-Strike: Global Offensive") && item.Title.StartsWith("Release Notes", StringComparison.Ordinal))
                    {
                        appID = 730;

                        // csgo changelog cleanup
                        item.Content = item.Content.Replace("</p>", "\n");
                        item.Content = new Regex("<p>\\[\\s*(.+)\\s*\\]", RegexOptions.Multiline | RegexOptions.CultureInvariant).Replace(item.Content, "## $1");
                        item.Content = item.Content.Replace("<p>", "");
                    }

                    if (appID > 0)
                    {
                        var build = (await db.QueryAsync<Build>(
                            "SELECT `Builds`.`BuildID`, `Builds`.`ChangeID`, `Builds`.`AppID`, `Changelists`.`Date`, LENGTH(`Official`) as `Official` FROM `Builds` " +
                            "LEFT JOIN `Patchnotes` ON `Patchnotes`.`BuildID` = `Builds`.`BuildID` " +
                            "JOIN `Apps` ON `Apps`.`AppID` = `Builds`.`AppID` " +
                            "JOIN `Changelists` ON `Builds`.`ChangeID` = `Changelists`.`ChangeID` " +
                            "WHERE `Builds`.`AppID` = @AppID ORDER BY `Builds`.`BuildID` DESC LIMIT 1",
                            new { appID }
                        )).SingleOrDefault();

                        if (build == null)
                        {
                            continue;
                        }

                        if (DateTime.UtcNow > build.Date.AddMinutes(60))
                        {
                            Log.WriteDebug("RSS", "Got {0} update patch notes, but there is no build within last 10 minutes. {1}", appID, item.Link);
                            IRC.Instance.SendOps($"{Colors.GREEN}[Patch notes]{Colors.NORMAL} Got {appID} update patch notes, but there is no build within last 10 minutes. {item.Link}");
                            continue;
                        }

                        if (build.Official > 0)
                        {
                            Log.WriteDebug("RSS", "Got {0} update patch notes, but official patch notes is already filled. {1}", appID, item.Link);
                            IRC.Instance.SendOps($"{Colors.GREEN}[Patch notes]{Colors.NORMAL} Got {appID} update patch notes, but official patch notes is already filled. {item.Link}");
                            continue;
                        }

                        // breaks
                        item.Content = new Regex(@"<br( \/)?>\r?\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant).Replace(item.Content, "\n");

                        // dashes (CS:GO mainly)
                        item.Content = new Regex("^&#(8208|8209|8210|8211|8212|8213); ?", RegexOptions.Multiline | RegexOptions.CultureInvariant).Replace(item.Content, "* ");

                        // add source
                        item.Content = string.Format("Via [{0}]({1}):\n\n{2}", appID == 730 ? "CS:GO Blog" : "the Steam Store", item.Link, item.Content);

                        Log.WriteDebug("RSS", "Inserting {0} patchnotes for build {1}:\n{2}", build.AppID, build.BuildID, item.Content);

                        await db.ExecuteAsync(
                            "INSERT INTO `Patchnotes` (`BuildID`, `AppID`, `ChangeID`, `Date`, `Official`) " +
                            "VALUES (@BuildID, @AppID, @ChangeID, @Date, @Content) ON DUPLICATE KEY UPDATE `Official` = VALUES(`Official`), `LastEditor` = @AccountID",
                            new
                            {
                                build.BuildID,
                                build.AppID,
                                build.ChangeID,
                                Date = build.Date.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss"),
                                item.Content,
                                Steam.Instance.Client.SteamID.AccountID
                            }
                        );

                        IRC.Instance.SendMain($"\u2699 Official patch notes:{Colors.BLUE} {Steam.GetAppName(build.AppID)}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetPatchnotesURL(build.BuildID)}");
                    }
                }
            }
        }

        private static List<GenericFeedItem> LoadRSS(Uri url, out string feedTitle)
        {
            try
            {
                var webReq = WebRequest.Create(url) as HttpWebRequest;
                webReq.UserAgent = "RSS2IRC";
                webReq.Timeout = (int)TimeSpan.FromSeconds(15).TotalMilliseconds;
                webReq.ReadWriteTimeout = (int)TimeSpan.FromSeconds(15).TotalMilliseconds;

                using (var response = webReq.GetResponse())
                {
                    using (var reader = new XmlTextReader(response.GetResponseStream()))
                    {
                        return ReadFeedItems(reader, out feedTitle);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteError("RSS", "Unable to load RSS feed {0}: {1}", url, ex.Message);

                feedTitle = null;

                return null;
            }
        }

        // http://www.nullskull.com/a/1177/everything-rss--atom-feed-parser.aspx
        private static List<GenericFeedItem> ReadFeedItems(XmlTextReader reader, out string feedTitle)
        {
            feedTitle = string.Empty;

            var itemList = new List<GenericFeedItem>();
            GenericFeedItem currentItem = null;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    string name = reader.Name.ToLowerInvariant();

                    if (name == "item" || name == "entry")
                    {
                        if (currentItem != null)
                        {
                            itemList.Add(currentItem);
                        }

                        currentItem = new GenericFeedItem();
                    }
                    else if (currentItem != null)
                    {
                        reader.Read();

                        switch (name)
                        {
                            case "title":
                                currentItem.Title = reader.Value.Trim();
                                break;
                            case "link":
                                currentItem.Link = reader.Value;
                                break;
                            case "description":
                            case "content":
                            case "content:encoded":
                                currentItem.Content = reader.Value;
                                break;
                        }
                    }
                    else if (name == "title")
                    {
                        reader.Read();

                        feedTitle = reader.Value;
                    }
                }
            }

            return itemList;
        }
    }
}
