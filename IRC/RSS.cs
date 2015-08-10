/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Timers;
using System.Xml;
using Dapper;

namespace SteamDatabaseBackend
{
    class RSS
    {
        private class GenericFeedItem
        {
            public string Title { get; set; }
            public string Link { get; set; }
        }

        public Timer Timer { get; private set; }

        public RSS()
        {
            Timer = new Timer();
            Timer.Elapsed += Tick;
            Timer.Interval = TimeSpan.FromSeconds(60).TotalMilliseconds;
            Timer.Start();
        }

        private void Tick(object sender, ElapsedEventArgs e)
        {
            Parallel.ForEach(Settings.Current.RssFeeds, feed =>
            {
                string feedTitle;
                var rssItems = LoadRSS(feed, out feedTitle);

                if (rssItems == null)
                {
                    return;
                }

                using (var db = Database.GetConnection())
                {
                    var items = db.Query<GenericFeedItem>("SELECT `Link` FROM `RSS` WHERE `Link` IN @Ids", new { Ids = rssItems.Select(x => x.Link) }).ToDictionary(x => x.Link, x => (byte)1);

                    var newItems = rssItems.Where(item => !items.ContainsKey(item.Link));

                    foreach (var item in newItems)
                    {
                        Log.WriteInfo("RSS", "[{0}] {1}: {2}", feedTitle, item.Title, item.Link);

                        IRC.Instance.SendMain("{0}{1}{2}: {3} -{4} {5}", Colors.BLUE, feedTitle, Colors.NORMAL, item.Title, Colors.DARKBLUE, item.Link);

                        db.Execute("INSERT INTO `RSS` (`Link`, `Title`) VALUES(@Link, @Title)", new { item.Link, item.Title });
                    }
                }
            });
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
