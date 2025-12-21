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

            // Check cache first - only hit Tidal once per day (minimum 24 hours)
            var cacheHours = Math.Max(Settings.RssCacheHours, 24);
            if (TidalRssCache.HasValidCache(cacheHours))
            {
                // Return empty request chain - parser will use cached results
                Logger?.Info("RSS: Using cached results, skipping Tidal API call");
                pageableRequests.Add(GetCacheMarkerRequest());
                return pageableRequests;
            }

            // Fetch fresh data from Tidal's Home page
            try
            {
                Logger?.Info("RSS: Fetching Tidal Home page for new releases...");
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

        private IEnumerable<IndexerRequest> GetCacheMarkerRequest()
        {
            // Return a minimal marker request that tells the parser to use cached data
            // We need at least one request for the indexer pipeline to work
            EnsureTokenValid();

            var url = TidalAPI.Instance!.GetAPIUrl("pages/home", new Dictionary<string, string>
            {
                ["deviceType"] = "BROWSER",
                ["limit"] = "1"
            });

            var req = new IndexerRequest(url, HttpAccept.Json);
            req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
            req.HttpRequest.Headers.Add("Authorization", $"{TidalAPI.Instance.Client.ActiveUser.TokenType} {TidalAPI.Instance.Client.ActiveUser.AccessToken}");
            req.HttpRequest.Headers.Add("X-Tidal-Request-Type", "CACHED");
            yield return req;
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
