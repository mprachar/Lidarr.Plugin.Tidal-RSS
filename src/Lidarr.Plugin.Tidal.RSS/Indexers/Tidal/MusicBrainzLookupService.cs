using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.Tidal
{
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

        public static string LookupTidalAlbumId(string releaseGroupMbid, IHttpClient httpClient)
        {
            if (string.IsNullOrWhiteSpace(releaseGroupMbid))
                return null;

            // Check cache
            if (Cache.TryGetValue(releaseGroupMbid, out var cached) && !cached.IsExpired)
            {
                Logger.Debug($"MB lookup cache {(cached.TidalId != null ? "hit" : "miss (negative)")}: {releaseGroupMbid}");
                return cached.TidalId;
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

                    var tidalId = QueryMusicBrainz(releaseGroupMbid, httpClient);
                    _lastRequestTime = DateTime.UtcNow;

                    // Cache both hits and misses
                    Cache[releaseGroupMbid] = new CachedResult(tidalId);

                    if (tidalId != null)
                        Logger.Info($"MB lookup found Tidal ID {tidalId} for release group {releaseGroupMbid}");
                    else
                        Logger.Debug($"MB lookup: no Tidal link for release group {releaseGroupMbid}");

                    return tidalId;
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
                Cache[releaseGroupMbid] = new CachedResult(null);
                return null;
            }
        }

        private static string QueryMusicBrainz(string releaseGroupMbid, IHttpClient httpClient)
        {
            var url = $"https://musicbrainz.org/ws/2/release?release-group={releaseGroupMbid}&inc=url-rels&fmt=json";

            var request = new HttpRequest(url);
            request.Headers.Add("User-Agent", UserAgent);
            request.Headers.Accept = "application/json";

            var response = httpClient.Get(request);
            var json = JObject.Parse(response.Content);
            var releases = json["releases"];

            if (releases == null)
                return null;

            foreach (var release in releases)
            {
                var relations = release["relations"];
                if (relations == null)
                    continue;

                foreach (var relation in relations)
                {
                    var relUrl = relation["url"]?["resource"]?.ToString();
                    if (string.IsNullOrEmpty(relUrl))
                        continue;

                    var match = TidalAlbumUrlRegex.Match(relUrl);
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }

            return null;
        }

        private class CachedResult
        {
            public string TidalId { get; }
            public DateTime CreatedAt { get; }
            public bool IsExpired => DateTime.UtcNow - CreatedAt > CacheTtl;

            public CachedResult(string tidalId)
            {
                TidalId = tidalId;
                CreatedAt = DateTime.UtcNow;
            }
        }
    }
}
