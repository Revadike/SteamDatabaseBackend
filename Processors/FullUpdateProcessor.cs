using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    internal static class FullUpdateProcessor
    {
        public static void PerformSync()
        {
            List<uint> apps;
            List<uint> packages;

            using (var db = Database.Get())
            {
                if (Settings.Current.FullRun == FullRunState.Enumerate)
                {
                    // TODO: Remove WHERE when normal appids approach 2mil
                    var lastAppID = 50000 + db.ExecuteScalar<int>("SELECT `AppID` FROM `Apps` WHERE `AppID` < 2000000 ORDER BY `AppID` DESC LIMIT 1");
                    var lastSubID = 10000 + db.ExecuteScalar<int>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC LIMIT 1");

                    Log.WriteInfo("Full Run", "Will enumerate {0} apps and {1} packages", lastAppID, lastSubID);

                    // greatest code you've ever seen
                    apps = Enumerable.Range(0, lastAppID).Reverse().Select(i => (uint)i).ToList();
                    packages = Enumerable.Range(0, lastSubID).Reverse().Select(i => (uint)i).ToList();
                }
                else if (Settings.Current.FullRun == FullRunState.TokensOnly)
                {
                    Log.WriteInfo("Full Run", $"Enumerating {PICSTokens.AppTokens.Count} apps and {PICSTokens.PackageTokens.Count} packages that have a token.");

                    apps = PICSTokens.AppTokens.Keys.ToList();
                    packages = PICSTokens.PackageTokens.Keys.ToList();
                }
                else
                {
                    Log.WriteInfo("Full Run", "Doing a full run on all apps and packages in the database.");

                    if (Settings.Current.FullRun == FullRunState.PackagesNormal)
                    {
                        apps = new List<uint>();
                    }
                    else
                    {
                        apps = db.Query<uint>("(SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC) UNION DISTINCT (SELECT `AppID` FROM `SubsApps` WHERE `Type` = 'app') ORDER BY `AppID` DESC").ToList();
                    }

                    packages = db.Query<uint>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC").ToList();
                }
            }

            TaskManager.RunAsync(async () => await RequestUpdateForList(apps, packages));
        }

        private static async Task RequestUpdateForList(List<uint> appIDs, List<uint> packageIDs)
        {
            Log.WriteInfo("Full Run", "Requesting info for {0} apps and {1} packages", appIDs.Count, packageIDs.Count);

            var metadataOnly = Settings.Current.FullRun == FullRunState.NormalUsingMetadata;

            foreach (var list in appIDs.Split(metadataOnly ? 10000 : 200))
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(list, Enumerable.Empty<uint>()),
                    new PICSTokens.RequestedTokens
                    {
                        MetadataOnly = metadataOnly,
                        Apps = list.ToList()
                    });

                do
                {
                    await Task.Delay(100);
                }
                while (IsBusy());
            }

            if (Settings.Current.FullRun == FullRunState.WithForcedDepots)
            {
                return;
            }

            foreach (var list in packageIDs.Split(metadataOnly ? 10000 : 1000))
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(Enumerable.Empty<uint>(), list),
                    new PICSTokens.RequestedTokens
                    {
                        MetadataOnly = metadataOnly,
                        Packages = list.ToList()
                    });

                do
                {
                    await Task.Delay(100);
                }
                while (IsBusy());
            }
        }

        public static bool IsBusy()
        {
            Log.WriteInfo("Full Run", "Jobs: {0} - Tasks: {1} - Processing: {2} - Depot locks: {3}",
                JobManager.JobsCount,
                TaskManager.TasksCount,
                PICSProductInfo.CurrentlyProcessingCount,
                Steam.Instance.DepotProcessor.DepotLocksCount);

            return TaskManager.TasksCount > 0
                   || JobManager.JobsCount > 0
                   || PICSProductInfo.CurrentlyProcessingCount > 50
                   || Steam.Instance.DepotProcessor.DepotLocksCount > 4;
        }
    }
}
