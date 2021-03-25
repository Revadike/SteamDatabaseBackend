/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Xml;
using Dapper;

namespace SteamDatabaseBackend
{
    internal class RSS : IDisposable
    {
        private class Build
        {
            public uint BuildID { get; set; }
            public uint ChangeID { get; set; }
            public uint AppID { get; set; }
            public uint Official { get; set; }
            public DateTime Date { get; set; }
        }

        public class GenericFeed
        {
            public string Title { get; set; } = string.Empty;
            public List<GenericFeedItem> Items { get; } = new();
        }

        public class GenericFeedItem
        {
            public string Title { get; set; }
            public string Link { get; set; }
            public string Content { get; set; }
            public DateTime PubDate { get; set; }
        }

        public Timer Timer { get; private set; }

        public RSS()
        {
            Timer = new Timer();
            Timer.Elapsed += Tick;
            Timer.Interval = TimeSpan.FromSeconds(60).TotalMilliseconds;
            Timer.Start();

            Tick(null, null);
        }

        public void Dispose()
        {
            if (Timer != null)
            {
                Timer.Dispose();
                Timer = null;
            }
        }

        private static async void Tick(object sender, ElapsedEventArgs e)
        {
            DateTime lastPostDate;

            await using (var db = await Database.GetConnectionAsync())
            {
                lastPostDate = db.ExecuteScalar<DateTime>("SELECT `Value` FROM `LocalConfig` WHERE `ConfigKey` = @Key", new { Key = "backend.lastrsspost" });
            }

            var tasks = Settings.Current.RssFeeds.Select(uri => ProcessFeed(uri, lastPostDate));

            var dates = await Task.WhenAll(tasks);
            var maxDate = dates.Max();

            if (maxDate > lastPostDate)
            {
                await LocalConfig.Update("backend.lastrsspost", maxDate.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static async Task<DateTime> ProcessFeed(Uri feedUrl, DateTime lastPostDate)
        {
            var feed = await LoadRSS(feedUrl);

            if (feed == null)
            {
                return DateTime.MinValue;
            }

            if (feed.Items.Count == 0)
            {
                Log.WriteError(nameof(RSS), $"Did not find any items in {feedUrl}");
                return DateTime.MinValue;
            }

            await using var db = await Database.GetConnectionAsync();
            var items = (await db.QueryAsync<GenericFeedItem>("SELECT `Link` FROM `RSS` WHERE `Link` IN @Ids", new { Ids = feed.Items.Select(x => x.Link) })).ToDictionary(x => x.Link, _ => (byte)1);

            var newItems = feed.Items.Where(item => item.PubDate > lastPostDate && !items.ContainsKey(item.Link));
            var maxDate = DateTime.MinValue;

            foreach (var item in newItems)
            {
                if (maxDate < item.PubDate)
                {
                    maxDate = item.PubDate;
                }

                Log.WriteInfo(nameof(RSS), $"[{feed.Title}] {item.Title}: {item.Link} ({item.PubDate})");

                IRC.Instance.SendAnnounce($"{Colors.BLUE}{feed.Title}{Colors.NORMAL}: {item.Title} -{Colors.DARKBLUE} {item.Link}");

                await db.ExecuteAsync("INSERT INTO `RSS` (`Link`, `Title`, `Date`) VALUES(@Link, @Title, @PubDate)", new
                {
                    item.Link,
                    item.Title,
                    item.PubDate,
                });

                _ = TaskManager.Run(async () => await Utils.SendWebhook(new
                {
                    Type = "RSS",
                    item.Title,
                    Url = item.Link,
                }));

                if (!Settings.IsMillhaven)
                {
                    continue;
                }

                uint appID = 0;

                if (feed.Title == "Steam RSS News Feed")
                {
                    if (item.Title.StartsWith("Dota 2 Update", StringComparison.Ordinal))
                    {
                        appID = 570;
                    }
                    else if (item.Title == "Team Fortress 2 Update Released")
                    {
                        appID = 440;

                        // tf2 changelog cleanup
                        item.Content = item.Content.Replace("<br/>", "\n");
                        item.Content = item.Content.Replace("<ul style=\"padding-bottom: 0px; margin-bottom: 0px;\">", "\n");
                        item.Content = item.Content.Replace("<ul style=\"padding-bottom: 0px; margin-bottom: 0px;\" >", "\n");
                        item.Content = item.Content.Replace("</ul>", "\n");
                        item.Content = item.Content.Replace("<li>", "* ");
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
                else if (feed.Title.Contains("Counter-Strike: Global Offensive") && item.Title.StartsWith("Release Notes", StringComparison.Ordinal))
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
                        Log.WriteDebug(nameof(RSS), $"Got {appID} update patch notes, but there is no build within last 10 minutes. {item.Link}");
                        IRC.Instance.SendOps($"{Colors.GREEN}[Patch notes]{Colors.NORMAL} Got {appID} update patch notes, but there is no build within last 10 minutes. {item.Link}");
                        continue;
                    }

                    if (build.Official > 0)
                    {
                        Log.WriteDebug(nameof(RSS), $"Got {appID} update patch notes, but official patch notes is already filled. {item.Link}");
                        IRC.Instance.SendOps($"{Colors.GREEN}[Patch notes]{Colors.NORMAL} Got {appID} update patch notes, but official patch notes is already filled. {item.Link}");
                        continue;
                    }

                    // breaks
                    item.Content = new Regex(@"<br( \/)?>\r?\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant).Replace(item.Content, "\n");

                    // dashes (CS:GO mainly)
                    item.Content = new Regex("^(?:-|&#(?:8208|8209|8210|8211|8212|8213);|–|—) ?", RegexOptions.Multiline | RegexOptions.CultureInvariant).Replace(item.Content, "* ");

                    item.Content = WebUtility.HtmlDecode(item.Content);

                    Log.WriteDebug(nameof(RSS), $"Inserting {build.AppID} patchnotes for build {build.BuildID}:\n{item.Content}");

                    var accountId = Steam.Instance.Client?.SteamID?.AccountID ?? 0;

                    await db.ExecuteAsync(
                        "INSERT INTO `Patchnotes` (`BuildID`, `AppID`, `ChangeID`, `Date`, `Official`, `OfficialURL`) " +
                        "VALUES (@BuildID, @AppID, @ChangeID, @Date, @Content, @Link) ON DUPLICATE KEY UPDATE `Official` = VALUES(`Official`), `OfficialURL` = VALUES(`OfficialURL`), `LastEditor` = @AccountID",
                        new
                        {
                            build.BuildID,
                            build.AppID,
                            build.ChangeID,
                            Date = build.Date.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss"),
                            item.Content,
                            item.Link,
                            accountId
                        }
                    );

                    IRC.Instance.SendAnnounce($"\u2699 Official patch notes:{Colors.BLUE} {Steam.GetAppName(build.AppID)}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetPatchnotesUrl(build.BuildID)}");
                }
            }

            return maxDate;
        }

        private static async Task<GenericFeed> LoadRSS(Uri url)
        {
            var requestUri = url.ToString();

            if (!requestUri.EndsWith("/rss.xml"))
            {
                requestUri += $"?_={DateTime.UtcNow.Ticks}";
                url = new Uri(requestUri);
            }

            try
            {
                using var reader = new XmlTextReader(await Utils.HttpClient.GetStreamAsync(url));
                return ReadFeedItems(reader);
            }
            catch (Exception ex)
            {
                Log.WriteError(nameof(RSS), $"Unable to load RSS feed {url}: {ex.Message}");

                return null;
            }
        }

        // http://www.nullskull.com/a/1177/everything-rss--atom-feed-parser.aspx
        private static GenericFeed ReadFeedItems(XmlTextReader reader)
        {
            var feed = new GenericFeed();
            GenericFeedItem currentItem = null;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var name = reader.Name.ToLowerInvariant();

                    if (name == "item" || name == "entry")
                    {
                        if (currentItem != null)
                        {
                            feed.Items.Add(currentItem);
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
                            case "pubdate":
                                currentItem.PubDate = DateTime.Parse(reader.Value);
                                break;
                        }
                    }
                    else if (name == "title")
                    {
                        reader.Read();

                        feed.Title = reader.Value;
                    }
                }
            }

            return feed;
        }
    }
}
