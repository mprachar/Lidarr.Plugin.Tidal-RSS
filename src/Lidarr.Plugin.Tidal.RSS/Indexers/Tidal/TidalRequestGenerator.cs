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

            // If artist IDs are configured, fetch their recent albums
            if (!string.IsNullOrWhiteSpace(Settings.RssArtistIds))
            {
                var artistIds = Settings.RssArtistIds
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(id => id.Trim())
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToList();

                if (artistIds.Any())
                {
                    // Check if we have valid cached results for ALL artists
                    var cacheHours = Settings.RssCacheHours > 0 ? Settings.RssCacheHours : 24;
                    if (TidalRssCache.HasValidCachedResults(artistIds, cacheHours))
                    {
                        Logger?.Info($"RSS Cache: Using cached results for {artistIds.Count} artists (cache valid for {cacheHours} hours)");
                        // Return empty request chain - the parser will handle returning cached results
                        // We add a single dummy request that signals to use cache
                        pageableRequests.Add(GetCacheMarkerRequest(artistIds, cacheHours));
                        return pageableRequests;
                    }

                    Logger?.Info($"RSS Cache: Fetching fresh data for {artistIds.Count} artists");
                    pageableRequests.Add(GetArtistAlbumsRequests(artistIds));
                    return pageableRequests;
                }
            }

            // Fallback: use a generic search for recent music if no artists configured
            Logger?.Debug("No RSS artist IDs configured, using fallback search");
            pageableRequests.Add(GetRequests("new releases " + DateTime.UtcNow.Year));

            return pageableRequests;
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
