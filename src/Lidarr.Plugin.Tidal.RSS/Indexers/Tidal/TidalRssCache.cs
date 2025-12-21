using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Tidal
{
    /// <summary>
    /// In-memory cache for Tidal Home page results.
    /// Caches new releases from Tidal's home page to reduce API calls.
    /// Enforces a minimum 24-hour cache to avoid hitting Tidal too frequently.
    /// </summary>
    public static class TidalRssCache
    {
        private static readonly object _lock = new();
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const int MinimumCacheHours = 24;

        private static CachedHomePageResults _cachedResults;

        /// <summary>
        /// Checks if we have valid cached results.
        /// </summary>
        public static bool HasValidCache(int requestedCacheHours)
        {
            lock (_lock)
            {
                if (_cachedResults == null)
                    return false;

                // Enforce minimum 24 hours
                var cacheHours = Math.Max(requestedCacheHours, MinimumCacheHours);
                var age = DateTime.UtcNow - _cachedResults.FetchedAt;

                if (age.TotalHours >= cacheHours)
                {
                    Logger.Info($"RSS Cache: Expired (age: {age.TotalHours:F1} hours, max: {cacheHours} hours)");
                    return false;
                }

                Logger.Info($"RSS Cache: Valid (age: {age.TotalHours:F1} hours, {_cachedResults.Releases.Count} releases cached)");
                return true;
            }
        }

        /// <summary>
        /// Gets the cached results. Only call after HasValidCache returns true.
        /// </summary>
        public static IList<ReleaseInfo> GetCachedResults()
        {
            lock (_lock)
            {
                if (_cachedResults == null)
                    return new List<ReleaseInfo>();

                Logger.Info($"RSS Cache: Returning {_cachedResults.Releases.Count} cached releases (fetched {(DateTime.UtcNow - _cachedResults.FetchedAt).TotalHours:F1} hours ago)");
                return _cachedResults.Releases;
            }
        }

        /// <summary>
        /// Stores parsed results in the cache.
        /// </summary>
        public static void SetCache(IList<ReleaseInfo> releases)
        {
            lock (_lock)
            {
                _cachedResults = new CachedHomePageResults
                {
                    Releases = releases.ToList(),
                    FetchedAt = DateTime.UtcNow
                };
                Logger.Info($"RSS Cache: Stored {releases.Count} releases");
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

        private class CachedHomePageResults
        {
            public List<ReleaseInfo> Releases { get; set; }
            public DateTime FetchedAt { get; set; }
        }
    }
}
