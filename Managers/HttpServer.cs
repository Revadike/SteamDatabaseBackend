/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SteamDatabaseBackend
{
    internal class HttpServer : IDisposable
    {
        private Thread ServerThread;
        private HttpListener HttpListener;

        public HttpServer(uint port)
        {
            HttpListener = new HttpListener();
            HttpListener.Prefixes.Add($"http://localhost:{port}/");

            ServerThread = new Thread(this.ListenAsync);
            ServerThread.Start();
        }

        public void Dispose()
        {
            if (ServerThread != null)
            {
                ServerThread.Abort();
                ServerThread = null;
            }

            if (HttpListener != null)
            {
                HttpListener.Close();
                HttpListener = null;
            }
        }

        private async void ListenAsync()
        {
            HttpListener.Start();

            foreach (var prefix in HttpListener.Prefixes)
            {
                Log.WriteInfo(nameof(HttpServer), $"Started http listener: {prefix}");
            }

            while (true)
            {
                var context = await HttpListener.GetContextAsync();
                await ProcessAsync(context);
            }
        }

        private static async Task ProcessAsync(HttpListenerContext context)
        {
            Log.WriteInfo(nameof(HttpServer), $"Processing {context.Request.RawUrl}");

            context.Response.ContentType = "application/json; charset=utf-8";

            if (!Steam.Instance.Client.IsConnected)
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
                return;
            }

            if (context.Request.Url.LocalPath == "/GetPlayers")
            {
                try
                {
                    var appid = uint.Parse(context.Request.QueryString["appid"]);

                    var task = await Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appid);

                    await WriteJsonResponse(task, context.Response);
                }
                catch (Exception e)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

                    await WriteJsonResponse(e.Message, context.Response);
                }
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }

            context.Response.Close();
        }

        private static Task WriteJsonResponse(object value, HttpListenerResponse response)
        {
            using var stringInMemoryStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.Indented)));
            return stringInMemoryStream.CopyToAsync(response.OutputStream);
        }
    }
}
