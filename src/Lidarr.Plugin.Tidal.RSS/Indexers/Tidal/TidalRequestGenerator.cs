using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Tidal;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class TidalRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 100;
        private const int MaxPages = 3;
        public TidalIndexerSettings Settings { get; set; }
        public Logger Logger { get; set; }

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            var pageableRequests = new IndexerPageableRequestChain();

            // Try to get new releases from Tidal's Home page (which has New Releases, Top Albums, etc.)
            try
            {
                Logger?.Info("RSS: Fetching Tidal Home page to find new releases...");
                LogHomePageCategories();

                // Use the home page request which contains New Releases, Top Albums, etc.
                pageableRequests.Add(GetHomePageRequest());
                return pageableRequests;
            }
            catch (Exception ex)
            {
                Logger?.Error(ex, "RSS: Failed to fetch Home page, falling back to search");
            }

            // Fallback: use a generic search for recent music
            Logger?.Debug("RSS: Using fallback search for new releases");
            pageableRequests.Add(GetRequests("new releases " + DateTime.UtcNow.Year));

            return pageableRequests;
        }

        private void LogHomePageCategories()
        {
            try
            {
                EnsureTokenValid();
                var homeTask = TidalAPI.Instance!.Client.API.GetHomePage();
                homeTask.Wait();
                var homePage = homeTask.Result;

                Logger?.Info("=== TIDAL HOME PAGE STRUCTURE ===");

                // Log the top-level keys
                foreach (var prop in homePage.Properties())
                {
                    Logger?.Info($"Top-level key: {prop.Name}");
                }

                // Try to find rows/categories
                var rows = homePage["rows"];
                if (rows != null)
                {
                    int rowIndex = 0;
                    foreach (var row in rows)
                    {
                        var modules = row["modules"];
                        if (modules != null)
                        {
                            foreach (var module in modules)
                            {
                                var title = module["title"]?.ToString() ?? "(no title)";
                                var type = module["type"]?.ToString() ?? "(no type)";
                                var itemCount = module["pagedList"]?["items"]?.Count() ?? module["items"]?.Count() ?? 0;
                                Logger?.Info($"Row {rowIndex}: '{title}' (type: {type}, items: {itemCount})");
                            }
                        }
                        rowIndex++;
                    }
                }

                // Also check for tabs
                var tabs = homePage["tabs"];
                if (tabs != null)
                {
                    foreach (var tab in tabs)
                    {
                        var tabTitle = tab["title"]?.ToString() ?? "(no title)";
                        Logger?.Info($"Tab: '{tabTitle}'");
                    }
                }

                Logger?.Info("=== END HOME PAGE STRUCTURE ===");
            }
            catch (Exception ex)
            {
                Logger?.Error(ex, "Failed to log home page categories");
            }
        }

        private IEnumerable<IndexerRequest> GetHomePageRequest()
        {
            EnsureTokenValid();

            var url = TidalAPI.Instance!.GetAPIUrl("pages/home", new Dictionary<string, string>
            {
                ["deviceType"] = "BROWSER"
            });

            var req = new IndexerRequest(url, HttpAccept.Json);
            req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
            req.HttpRequest.Headers.Add("Authorization", $"{TidalAPI.Instance.Client.ActiveUser.TokenType} {TidalAPI.Instance.Client.ActiveUser.AccessToken}");
            req.HttpRequest.Headers.Add("X-Tidal-Request-Type", "HOME");
            yield return req;
        }

        private IEnumerable<IndexerRequest> GetCacheMarkerRequest(List<string> artistIds, int cacheHours)
        {
            // Create a minimal request to Tidal that we'll use to trigger cache retrieval
            // We still need at least one valid request for the indexer to work
            // But we'll mark it so the parser knows to use cached data
            EnsureTokenValid();

            var data = new Dictionary<string, string>()
            {
                ["limit"] = "1",
                ["offset"] = "0",
            };

            // Just fetch minimal data from first artist to keep the HTTP pipeline happy
            var url = TidalAPI.Instance!.GetAPIUrl($"artists/{artistIds.First()}/albums", data);
            var req = new IndexerRequest(url, HttpAccept.Json);
            req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
            req.HttpRequest.Headers.Add("Authorization", $"{TidalAPI.Instance.Client.ActiveUser.TokenType} {TidalAPI.Instance.Client.ActiveUser.AccessToken}");
            req.HttpRequest.Headers.Add("X-Tidal-Request-Type", "RSS-USE-CACHE");
            req.HttpRequest.Headers.Add("X-Tidal-Cache-Hours", cacheHours.ToString());
            yield return req;
        }

        private IEnumerable<IndexerRequest> GetArtistAlbumsRequests(List<string> artistIds)
        {
            EnsureTokenValid();

            foreach (var artistId in artistIds)
            {
                var data = new Dictionary<string, string>()
                {
                    ["limit"] = $"{PageSize}",
                    ["offset"] = "0",
                };

                var url = TidalAPI.Instance!.GetAPIUrl($"artists/{artistId}/albums", data);
                var req = new IndexerRequest(url, HttpAccept.Json);
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
                req.HttpRequest.Headers.Add("Authorization", $"{TidalAPI.Instance.Client.ActiveUser.TokenType} {TidalAPI.Instance.Client.ActiveUser.AccessToken}");
                req.HttpRequest.Headers.Add("X-Tidal-Request-Type", "RSS");
                req.HttpRequest.Headers.Add("X-Tidal-Artist-Id", artistId);
                yield return req;
            }
        }

        private void EnsureTokenValid()
        {
            if (DateTime.UtcNow > TidalAPI.Instance.Client.ActiveUser.ExpirationDate)
            {
                if (TidalAPI.Instance.Client.ActiveUser.ExpirationDate == DateTime.MinValue)
                    TidalAPI.Instance.Client.ForceRefreshToken().Wait();
                else
                    TidalAPI.Instance.Client.IsLoggedIn().Wait();
            }
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}"));

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            chain.AddTier(GetRequests(searchCriteria.ArtistQuery));

            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters)
        {
            EnsureTokenValid();

            for (var page = 0; page < MaxPages; page++)
            {
                var data = new Dictionary<string, string>()
                {
                    ["query"] = searchParameters,
                    ["limit"] = $"{PageSize}",
                    ["types"] = "albums,tracks",
                    ["offset"] = $"{page * PageSize}",
                };

                var url = TidalAPI.Instance!.GetAPIUrl("search", data);
                var req = new IndexerRequest(url, HttpAccept.Json);
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
                req.HttpRequest.Headers.Add("Authorization", $"{TidalAPI.Instance.Client.ActiveUser.TokenType} {TidalAPI.Instance.Client.ActiveUser.AccessToken}");
                yield return req;
            }
        }
    }
}
