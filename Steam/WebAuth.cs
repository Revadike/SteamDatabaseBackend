/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class WebAuth : SteamHandler
    {
        public static bool IsAuthorized { get; private set; }
        private static CookieContainer Cookies = new CookieContainer();

        public WebAuth(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            TaskManager.RunAsync(async () => await AuthenticateUser());
        }

        public static async Task<bool> AuthenticateUser()
        {
            SteamUser.WebAPIUserNonceCallback nonce;

            try
            {
                nonce = await Steam.Instance.User.RequestWebAPIUserNonce();
            }
            catch (Exception e)
            {
                IsAuthorized = false;

                Log.WriteWarn("WebAuth", "Failed to get nonce: {0}", e.Message);

                return false;
            }

            // 32 byte random blob of data
            var sessionKey = CryptoHelper.GenerateRandomBlock(32);

            byte[] encryptedSessionKey;

            // ... which is then encrypted with RSA using the Steam system's public key
            using (var rsa = new RSACrypto(KeyDictionary.GetPublicKey(Steam.Instance.Client.Universe)))
            {
                encryptedSessionKey = rsa.Encrypt(sessionKey);
            }

            // users hashed loginkey, AES encrypted with the sessionkey
            var encryptedLoginKey = CryptoHelper.SymmetricEncrypt(Encoding.ASCII.GetBytes(nonce.Nonce), sessionKey);

            using (var userAuth = WebAPI.GetAsyncInterface("ISteamUserAuth"))
            {
                KeyValue result;

                try
                {
                    result = await userAuth.CallAsync(HttpMethod.Post, "AuthenticateUser", 1,
                        new Dictionary<string, string>
                        {
                            { "steamid", Steam.Instance.Client.SteamID.ConvertToUInt64().ToString() },
                            { "sessionkey", WebHelpers.UrlEncode(encryptedSessionKey) },
                            { "encrypted_loginkey", WebHelpers.UrlEncode(encryptedLoginKey) },
                        }
                    );
                }
                catch (HttpRequestException e)
                {
                    IsAuthorized = false;

                    Log.WriteWarn("WebAuth", "Failed to authenticate: {0}", e.Message);

                    return false;
                }

                File.WriteAllText(Path.Combine(Application.Path, "files", ".support", "cookie.txt"), $"steamLogin={result["token"].AsString()}; steamLoginSecure={result["tokensecure"].AsString()}");

                Cookies = new CookieContainer();
                Cookies.Add(new Cookie("steamLogin", result["token"].AsString(), "/", "store.steampowered.com"));
                Cookies.Add(new Cookie("steamLoginSecure", result["tokensecure"].AsString(), "/", "store.steampowered.com"));
            }

            IsAuthorized = true;

            Log.WriteInfo("WebAuth", "Authenticated");

            if (!Settings.IsFullRun)
            {
                await AccountInfo.RefreshAppsToIdle();
            }

            return true;
        }

        public static HttpWebResponse PerformRequest(string method, string url)
        {
            HttpWebResponse response = null;

            for (var i = 0; i < 5; i++)
            {
                if (!IsAuthorized && !AuthenticateUser().GetAwaiter().GetResult()) // TODO: async
                {
                    continue;
                }

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Timeout = 10000;
                request.AllowAutoRedirect = false;
                request.CookieContainer = Cookies;
                request.AutomaticDecompression = DecompressionMethods.GZip;
                request.UserAgent = SteamDB.USERAGENT;

                response = request.GetResponse() as HttpWebResponse;

                if (response == null || response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Redirect)
                {
                    IsAuthorized = false;

                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new WebException(string.Format("Invalid status code: {0} ({1})", response.StatusCode, (int)response.StatusCode));
                }

                break;
            }

            if (response == null)
            {
                throw new WebException("No data received");
            }

            return response;
        }
    }
}
