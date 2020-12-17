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
using SteamKit2;

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

            ServerThread = new Thread(ListenAsync)
            {
                Name = nameof(HttpServer)
            };
            ServerThread.Start();
        }

        public void Dispose()
        {
            ServerThread = null;

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

            while (Steam.Instance.IsRunning)
            {
                try
                {
                    var context = await HttpListener.GetContextAsync();
                    await ProcessAsync(context);
                }
                catch (Exception e)
                {
                    Log.WriteDebug(nameof(HttpServer), e.Message);
                }
            }
        }

        private static async Task ProcessAsync(HttpListenerContext context)
        {
            Log.WriteInfo(nameof(HttpServer), $"Processing {context.Request.RawUrl}");

            context.Response.ContentType = "application/json; charset=utf-8";

            if (!Steam.Instance.Client.IsConnected)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                context.Response.Close();
                return;
            }

            try
            {
                switch (context.Request.Url.LocalPath)
                {
                    case "/GetApp":
                        await GetApp(context);
                        break;
                        
                    case "/GetPackage":
                        await GetPackage(context);
                        break;

                    case "/GetPlayers":
                        await GetPlayers(context);
                        break;

                    case "/ReloadTokens":
                        await ReloadTokens(context);
                        break;

                    case "/ReloadImportant":
                        await ReloadImportant(context);
                        break;

                    default:
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonResponse("No such route", context.Response);
                        break;
                }
            }
            catch (Exception e)
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

                await WriteJsonResponse(e.Message, context.Response);
            }

            context.Response.Close();
        }

        private static Task WriteJsonResponse(object value, HttpListenerResponse response)
        {
            using var stringInMemoryStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.Indented)));
            return stringInMemoryStream.CopyToAsync(response.OutputStream);
        }

        private static async Task GetPlayers(HttpListenerContext context)
        {
            var appid = context.Request.QueryString.Get("appid");

            if (appid == null)
            {
                throw new MissingFieldException("appid parameter is missing");
            }

            var result = await Steam.Instance.UserStats.GetNumberOfCurrentPlayers(uint.Parse(appid));

            await WriteJsonResponse(
            new {
                Success = result.Result == EResult.OK,
                result.Result,
                result.NumPlayers,
            }, context.Response);
        }
        
        private static async Task GetApp(HttpListenerContext context)
        {
            var appid = context.Request.QueryString.Get("appid");

            if (appid == null)
            {
                throw new MissingFieldException("appid parameter is missing");
            }

            var result = await AppCommand.GetAppData(uint.Parse(appid));

            await WriteJsonResponse(result, context.Response);
        }
        
        private static async Task GetPackage(HttpListenerContext context)
        {
            var subid = context.Request.QueryString.Get("subid");

            if (subid == null)
            {
                throw new MissingFieldException("subid parameter is missing");
            }

            var result = await PackageCommand.GetPackageData(uint.Parse(subid));

            await WriteJsonResponse(result, context.Response);
        }

        private static async Task ReloadTokens(HttpListenerContext context)
        {
            await PICSTokens.Reload();

            await WriteJsonResponse("Tokens reloaded", context.Response);
        }

        private static async Task ReloadImportant(HttpListenerContext context)
        {
            await Application.ReloadImportant();

            await WriteJsonResponse("Important lists reloaded", context.Response);
        }
    }
}
