using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Tidal
{
    /// <summary>
    /// In-memory cache for Tidal RSS parsed results.
    /// Reduces API calls when Lidarr's RSS sync runs more frequently than desired.
    /// </summary>
    public static class TidalRssCache
    {
        private static readonly object _lock = new();
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Cache the final parsed results, keyed by a hash of all artist IDs
        private static CachedRssResults _cachedResults;

        /// <summary>
        /// Checks if we have valid cached results for the specified artists.
        /// </summary>
        public static bool HasValidCachedResults(IEnumerable<string> artistIds, int cacheHours)
        {
            lock (_lock)
            {
                if (_cachedResults == null)
                    return false;

                // Check if the artist list matches
                var requestedIds = string.Join(",", artistIds.OrderBy(x => x));
                if (_cachedResults.ArtistIdsKey != requestedIds)
                {
                    Logger.Debug($"RSS Cache: Artist list changed, cache invalid");
                    return false;
                }

                // Check cache age
                var age = DateTime.UtcNow - _cachedResults.FetchedAt;
                if (age.TotalHours >= cacheHours)
                {
                    Logger.Debug($"RSS Cache: Expired (age: {age.TotalHours:F1} hours, max: {cacheHours} hours)");
                    return false;
                }

                Logger.Debug($"RSS Cache: Valid (age: {age.TotalMinutes:F0} minutes, {_cachedResults.Releases.Count} releases)");
                return true;
            }
        }

        /// <summary>
        /// Gets the cached results. Only call after HasValidCachedResults returns true.
        /// </summary>
        public static IList<ReleaseInfo> GetCachedResults()
        {
            lock (_lock)
            {
                if (_cachedResults == null)
                    return new List<ReleaseInfo>();

                Logger.Info($"RSS Cache: Returning {_cachedResults.Releases.Count} cached releases");
                return _cachedResults.Releases;
            }
        }

        /// <summary>
        /// Stores parsed results in the cache.
        /// </summary>
        public static void SetCachedResults(IEnumerable<string> artistIds, IList<ReleaseInfo> releases)
        {
            lock (_lock)
            {
                var artistIdsKey = string.Join(",", artistIds.OrderBy(x => x));
                _cachedResults = new CachedRssResults
                {
                    ArtistIdsKey = artistIdsKey,
                    Releases = releases.ToList(),
                    FetchedAt = DateTime.UtcNow
                };
                Logger.Info($"RSS Cache: Stored {releases.Count} releases for {artistIds.Count()} artists");
            }
        }

        /// <summary>
        /// Clears the cache.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _cachedResults = null;
                Logger.Debug("RSS Cache: Cleared");
            }
        }

        /// <summary>
        /// Gets cache statistics for logging.
        /// </summary>
        public static (int releaseCount, DateTime? fetchedAt, double? ageHours) GetStats()
        {
            lock (_lock)
            {
                if (_cachedResults == null)
                    return (0, null, null);

                var age = DateTime.UtcNow - _cachedResults.FetchedAt;
                return (_cachedResults.Releases.Count, _cachedResults.FetchedAt, age.TotalHours);
            }
        }

        private class CachedRssResults
        {
            public string ArtistIdsKey { get; set; }
            public List<ReleaseInfo> Releases { get; set; }
            public DateTime FetchedAt { get; set; }
        }
    }
}
