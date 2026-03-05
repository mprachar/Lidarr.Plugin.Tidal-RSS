using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.Tidal
{
    public struct MusicBrainzLookupResult
    {
        public string TidalAlbumId { get; }
        public IReadOnlyList<string> Barcodes { get; }

        public MusicBrainzLookupResult(string tidalAlbumId, IReadOnlyList<string> barcodes)
        {
            TidalAlbumId = tidalAlbumId;
            Barcodes = barcodes ?? Array.Empty<string>();
        }

        public bool HasTidalId => !string.IsNullOrEmpty(TidalAlbumId);
        public bool HasBarcodes => Barcodes.Count > 0;
        public bool IsEmpty => !HasTidalId && !HasBarcodes;
    }

    public static class MusicBrainzLookupService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly ConcurrentDictionary<string, CachedResult> Cache = new();
        private static readonly SemaphoreSlim RateLimiter = new(1, 1);
        private static DateTime _lastRequestTime = DateTime.MinValue;
        private static readonly TimeSpan RateInterval = TimeSpan.FromMilliseconds(1100);
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
        private static readonly Regex TidalAlbumUrlRegex = new(@"tidal\.com(?:/browse)?/album/(\d+)", RegexOptions.Compiled);

        private const string UserAgent = "Lidarr.Plugin.Tidal-RSS/1.0 (https://github.com/mprachar/Lidarr.Plugin.Tidal-RSS)";

        public static MusicBrainzLookupResult Lookup(string releaseGroupMbid, IHttpClient httpClient)
        {
            if (string.IsNullOrWhiteSpace(releaseGroupMbid))
                return default;

            // Check cache
            if (Cache.TryGetValue(releaseGroupMbid, out var cached) && !cached.IsExpired)
            {
                Logger.Debug($"MB lookup cache hit: {releaseGroupMbid} (TidalId={cached.Result.TidalAlbumId ?? "null"}, Barcodes={cached.Result.Barcodes.Count})");
                return cached.Result;
            }

            try
            {
                // Rate limit: 1 request per 1.1 seconds
                RateLimiter.Wait();
                try
                {
                    var elapsed = DateTime.UtcNow - _lastRequestTime;
                    if (elapsed < RateInterval)
                    {
                        Thread.Sleep(RateInterval - elapsed);
                    }

                    var result = QueryMusicBrainz(releaseGroupMbid, httpClient);
                    _lastRequestTime = DateTime.UtcNow;

                    // Cache both hits and misses
                    Cache[releaseGroupMbid] = new CachedResult(result);

                    if (result.HasTidalId)
                        Logger.Info($"MB lookup found Tidal ID {result.TidalAlbumId} for release group {releaseGroupMbid}");
                    if (result.HasBarcodes)
                        Logger.Info($"MB lookup found {result.Barcodes.Count} barcode(s) for release group {releaseGroupMbid}");
                    if (result.IsEmpty)
                        Logger.Debug($"MB lookup: no Tidal link or barcodes for release group {releaseGroupMbid}");

                    return result;
                }
                finally
                {
                    RateLimiter.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"MB lookup failed for {releaseGroupMbid}, falling back to text search");
                // Cache the failure as a negative result to avoid hammering MB on errors
                Cache[releaseGroupMbid] = new CachedResult(default);
                return default;
            }
        }

        private static MusicBrainzLookupResult QueryMusicBrainz(string releaseGroupMbid, IHttpClient httpClient)
        {
            var url = $"https://musicbrainz.org/ws/2/release?release-group={releaseGroupMbid}&inc=url-rels&fmt=json";

            var request = new HttpRequest(url);
            request.Headers.Add("User-Agent", UserAgent);
            request.Headers.Accept = "application/json";

            var response = httpClient.Get(request);
            var json = JObject.Parse(response.Content);
            var releases = json["releases"];

            if (releases == null)
                return default;

            string tidalId = null;
            var barcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var release in releases)
            {
                // Extract barcode
                var barcode = release["barcode"]?.ToString();
                if (!string.IsNullOrWhiteSpace(barcode))
                    barcodes.Add(barcode);

                // Extract Tidal URL relation (only need the first one)
                if (tidalId == null)
                {
                    var relations = release["relations"];
                    if (relations != null)
                    {
                        foreach (var relation in relations)
                        {
                            var relUrl = relation["url"]?["resource"]?.ToString();
                            if (string.IsNullOrEmpty(relUrl))
                                continue;

                            var match = TidalAlbumUrlRegex.Match(relUrl);
                            if (match.Success)
                            {
                                tidalId = match.Groups[1].Value;
                                break;
                            }
                        }
                    }
                }
            }

            return new MusicBrainzLookupResult(tidalId, barcodes.ToList());
        }

        private class CachedResult
        {
            public MusicBrainzLookupResult Result { get; }
            public DateTime CreatedAt { get; }
            public bool IsExpired => DateTime.UtcNow - CreatedAt > CacheTtl;

            public CachedResult(MusicBrainzLookupResult result)
            {
                Result = result;
                CreatedAt = DateTime.UtcNow;
            }
        }
    }
}
