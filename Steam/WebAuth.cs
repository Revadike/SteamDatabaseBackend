/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class WebAuth : SteamHandler
    {
        public static bool IsAuthorized { get; private set; }
        private static CookieContainer Cookies = new CookieContainer();

        public WebAuth(CallbackManager manager)
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
            ulong steamid;

            try
            {
                nonce = await Steam.Instance.User.RequestWebAPIUserNonce();
                steamid = Steam.Instance.Client.SteamID.ConvertToUInt64();
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

            using (var userAuth = Steam.Configuration.GetAsyncWebAPIInterface("ISteamUserAuth"))
            {
                KeyValue result;

                try
                {
                    result = await userAuth.CallAsync(HttpMethod.Post, "AuthenticateUser", 1,
                        new Dictionary<string, object>
                        {
                            { "steamid", steamid },
                            { "sessionkey", encryptedSessionKey },
                            { "encrypted_loginkey", encryptedLoginKey },
                        }
                    );
                }
                catch (HttpRequestException e)
                {
                    IsAuthorized = false;

                    Log.WriteWarn("WebAuth", "Failed to authenticate: {0}", e.Message);

                    return false;
                }

                Cookies = new CookieContainer();
                Cookies.Add(new Cookie("steamLogin", result["token"].AsString(), "/", "store.steampowered.com"));
                Cookies.Add(new Cookie("steamLoginSecure", result["tokensecure"].AsString(), "/", "store.steampowered.com"));
            }

            IsAuthorized = true;

            Log.WriteInfo("WebAuth", "Authenticated");

            return true;
        }

        public static async Task<HttpResponseMessage> PerformRequest(HttpMethod method, string url)
        {
            HttpResponseMessage response = null;

            for (var i = 0; i < 3; i++)
            {
                if (!IsAuthorized && !await AuthenticateUser())
                {
                    continue;
                }

                var uri = new Uri(url);
                var cookies = string.Empty;

                foreach (var cookie in Cookies.GetCookies(uri))
                {
                    cookies += cookie.ToString() + ";";
                }

                using (var requestMessage = new HttpRequestMessage(method, uri))
                {
                    requestMessage.Headers.Add("Cookie", cookies); // Can't pass cookie container into a single req message

                    response = await Utils.HttpClient.SendAsync(requestMessage);

                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Redirect)
                    {
                        Log.WriteDebug(nameof(WebAuth), $"Got status code {response.StatusCode}");

                        IsAuthorized = false;

                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                }

                break;
            }

            return response;
        }
    }
}
