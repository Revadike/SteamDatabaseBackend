using System;
using System.Configuration;
using System.Threading;
using SteamKit2;
using SteamKit2.GC.Internal;
using SteamKit2.GC;

namespace PICSUpdater
{
    public class SteamDota
    {
        const uint DOTA_2 = 570;

        public SteamClient steamClient;
        private SteamUser steamUser;
        private SteamFriends steamFriends;

        public bool isRunning = true;

        public void Run()
        {
            steamClient = new SteamClient();
            steamUser = steamClient.GetHandler<SteamUser>();
            steamFriends = steamClient.GetHandler<SteamFriends>();

            CallbackManager manager = new CallbackManager(steamClient);

            new Callback<SteamClient.ConnectedCallback>(OnConnected, manager);
            new Callback<SteamClient.DisconnectedCallback>(OnDisconnected, manager);
            new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo, manager);
            new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn, manager);
            new Callback<SteamGameCoordinator.MessageCallback>(OnGameCoordinatorMessage, manager);

            steamClient.Connect();

            while (isRunning == true)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(5));
            }
        }

        private void OnGameCoordinatorMessage(SteamGameCoordinator.MessageCallback callback)
        {
            SteamProxy.GameCoordinatorMessage(DOTA_2, callback);
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                SteamProxy.PlayGame(steamClient, DOTA_2);

                Thread.Sleep(TimeSpan.FromSeconds(3));

                var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>( ( uint )EGCBaseClientMsg.k_EMsgGCClientHello );

                steamClient.GetHandler<SteamGameCoordinator>().Send(clientHello, DOTA_2);
            }
        }

        private void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            steamFriends.SetPersonaState(EPersonaState.Busy);
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                throw new Exception("Could not connect: " + callback.Result);
            }

            Log.WriteInfo("Steam Dota", "Connected! Logging in...");

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = ConfigurationManager.AppSettings["steam2-username"],
                Password = ConfigurationManager.AppSettings["steam2-password"]
            });
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (!isRunning)
            {
                Log.WriteInfo("Steam Dota", "Disconnected from Steam");
                return;
            }

            Log.WriteInfo("Steam Dota", "Disconnected from Steam. Retrying in 15 seconds...");

            Thread.Sleep(TimeSpan.FromSeconds(15));

            steamClient.Connect();
        }
    }
}
