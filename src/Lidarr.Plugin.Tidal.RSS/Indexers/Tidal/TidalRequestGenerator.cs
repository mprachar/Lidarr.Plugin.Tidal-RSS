using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        public IHttpClient HttpClient { get; set; }

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

            if (string.IsNullOrEmpty(TidalAPI.Instance.Client.ActiveUser.CountryCode))
            {
                Logger?.Warn("CountryCode is empty after token refresh, re-fetching session data");
                TidalAPI.Instance.Client.RefreshSession().Wait();
            }
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            // Capture the Lidarr artist name for Tier 0 requests.
            // For classical music, Lidarr uses the composer (e.g. "Franz Schubert") while
            // Tidal uses the performer (e.g. "Alfred Brendel"). Passing the Lidarr name
            // via header lets the parser substitute it, preventing "Wrong artist" rejections.
            var lidarrArtistName = searchCriteria.ArtistQuery;

            // Tier 0: MusicBrainz cross-reference for exact Tidal album match
            var mbid = searchCriteria.Albums?.FirstOrDefault()?.ForeignAlbumId;
            if (!string.IsNullOrEmpty(mbid) && HttpClient != null)
            {
                var mbResult = MusicBrainzLookupService.Lookup(mbid, HttpClient);

                // Strategy A: Direct Tidal URL from MB relations
                if (mbResult.HasTidalId)
                {
                    Logger?.Info($"Tier 0 Strategy A: direct Tidal album {mbResult.TidalAlbumId} from MB URL relation");
                    chain.Add(GetDirectAlbumRequest(mbResult.TidalAlbumId, lidarrArtistName));
                }

                // Strategy B: Barcode lookup via Tidal OpenAPI v2, then direct album fetch via v1
                if (mbResult.HasBarcodes)
                {
                    var barcodeAlbumIds = LookupBarcodeAlbumIds(mbResult.Barcodes);
                    foreach (var albumId in barcodeAlbumIds)
                    {
                        Logger?.Info($"Tier 0 Strategy B: barcode resolved to Tidal album {albumId}");
                        chain.Add(GetDirectAlbumRequest(albumId, lidarrArtistName));
                    }
                }
            }

            // Tier 1 (fallback): text search
            chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}"));
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery));
            return chain;
        }

        private IEnumerable<IndexerRequest> GetDirectAlbumRequest(string tidalId, string lidarrArtistName = null)
        {
            EnsureTokenValid();

            var url = TidalAPI.Instance!.GetAPIUrl($"albums/{tidalId}");
            var req = new IndexerRequest(url, HttpAccept.Json);
            req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
            req.HttpRequest.Headers.Add("Authorization", $"{TidalAPI.Instance.Client.ActiveUser.TokenType} {TidalAPI.Instance.Client.ActiveUser.AccessToken}");
            req.HttpRequest.Headers.Add("X-Tidal-Request-Type", "MB_ALBUM_DIRECT");
            if (!string.IsNullOrEmpty(lidarrArtistName))
                req.HttpRequest.Headers.Add("X-Tidal-Lidarr-Artist", lidarrArtistName);
            yield return req;
        }

        /// <summary>
        /// Queries Tidal OpenAPI v2 for album IDs matching the given barcodes.
        /// Uses System.Net.Http.HttpClient directly to bypass Lidarr's HTTP dispatcher,
        /// which mangles requests to openapi.tidal.com (causing persistent 404s).
        /// Returns album IDs that can be fetched via the v1 API (GetDirectAlbumRequest).
        /// </summary>
        private List<string> LookupBarcodeAlbumIds(IReadOnlyList<string> barcodes)
        {
            var albumIds = new List<string>();
            var authHeader = TidalOpenApiToken.GetAuthorizationHeader(HttpClient);
            if (authHeader == null)
            {
                Logger?.Warn("Cannot perform barcode lookup: failed to get OpenAPI v2 token");
                return albumIds;
            }

            var countryCode = TidalAPI.Instance!.Client.ActiveUser.CountryCode;
            if (string.IsNullOrEmpty(countryCode))
            {
                Logger?.Warn("Barcode lookup skipped: countryCode is empty (session not initialized). Falling back to text search.");
                return albumIds;
            }

            using var client = new System.Net.Http.HttpClient();
            for (var i = 0; i < barcodes.Count; i++)
            {
                if (i > 0)
                    Thread.Sleep(500);

                var barcode = barcodes[i];
                try
                {
                    var (success, matchedIds) = SendBarcodeRequest(client, barcode, countryCode, authHeader);

                    if (!success)
                    {
                        // Retry once after 2s on rate limit
                        Logger?.Warn($"Barcode {barcode}: rate limited, retrying in 2s...");
                        Thread.Sleep(2000);
                        (success, matchedIds) = SendBarcodeRequest(client, barcode, countryCode, authHeader);
                        if (!success)
                        {
                            Logger?.Warn($"Barcode {barcode}: still rate limited after retry, skipping");
                            continue;
                        }
                    }

                    if (matchedIds.Count > 0)
                    {
                        albumIds.AddRange(matchedIds);
                        var remaining = barcodes.Count - i - 1;
                        if (remaining > 0)
                            Logger?.Info($"Barcode {barcode}: found match, skipping remaining {remaining} barcodes");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Logger?.Warn($"Barcode lookup error for {barcode}: {ex.Message}");
                }
            }

            return albumIds;
        }

        private (bool success, List<string> albumIds) SendBarcodeRequest(
            System.Net.Http.HttpClient client, string barcode, string countryCode, string authHeader)
        {
            var url = $"https://openapi.tidal.com/v2/albums?filter%5BbarcodeId%5D={barcode}&countryCode={countryCode}";
            var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);

            var response = client.SendAsync(request).Result;
            var content = response.Content.ReadAsStringAsync().Result;

            if ((int)response.StatusCode == 429)
                return (false, new List<string>());

            if (!response.IsSuccessStatusCode)
            {
                Logger?.Warn($"Barcode lookup failed for {barcode}: HTTP {(int)response.StatusCode} - {content?.Substring(0, Math.Min(content?.Length ?? 0, 200))}");
                return (true, new List<string>());
            }

            var albumIds = new List<string>();
            var json = Newtonsoft.Json.Linq.JObject.Parse(content);
            var data = json["data"];
            if (data != null)
            {
                foreach (var item in data)
                {
                    var id = item["id"]?.ToString();
                    var title = item["attributes"]?["title"]?.ToString() ?? "unknown";
                    if (!string.IsNullOrEmpty(id) && !albumIds.Contains(id))
                    {
                        albumIds.Add(id);
                        Logger?.Info($"Barcode {barcode} matched Tidal album {id}: {title}");
                    }
                }
            }

            return (true, albumIds);
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
