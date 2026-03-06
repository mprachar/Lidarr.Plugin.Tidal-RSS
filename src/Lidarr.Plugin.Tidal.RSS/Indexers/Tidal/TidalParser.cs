using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Tidal;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class TidalParser : IParseIndexerResponse
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public TidalIndexerSettings Settings { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            try
            {
                var content = new HttpResponse<TidalSearchResponse>(response.HttpResponse).Content;

                // Check request type from headers
                var requestType = response.HttpRequest.Headers.ContainsKey("X-Tidal-Request-Type")
                    ? response.HttpRequest.Headers["X-Tidal-Request-Type"]
                    : "";

                // Return cached results if available
                if (requestType == "CACHED")
                {
                    Logger.Info("RSS: Returning cached results");
                    return TidalRssCache.GetCachedResults();
                }

                // Parse Home page response and cache the results
                if (requestType == "HOME")
                {
                    Logger.Info("RSS: Parsing Tidal Home page for new releases");
                    var releases = ParseHomePageResponse(content);
                    TidalRssCache.SetCache(releases);
                    return releases;
                }

                // Direct album fetch from MusicBrainz cross-reference
                if (requestType == "MB_ALBUM_DIRECT")
                {
                    var lidarrArtist = response.HttpRequest.Headers.ContainsKey("X-Tidal-Lidarr-Artist")
                        ? response.HttpRequest.Headers["X-Tidal-Lidarr-Artist"]
                        : null;
                    Logger.Info("Parsing MusicBrainz direct album lookup response" +
                        (lidarrArtist != null ? $" (Lidarr artist override: {lidarrArtist})" : ""));
                    return ParseDirectAlbumResponse(content, lidarrArtist);
                }

                // Regular search request
                return ParseSearchResponse(content);
            }
            catch (Exception ex) when (ex.ToString().Contains("countryCode", StringComparison.OrdinalIgnoreCase))
            {
                // Tidal intermittently rejects valid requests with "countryCode parameter missing"
                // even when countryCode is present in the URL. This is a transient Tidal-side issue
                // (stale sessionId). Swallow it to prevent RecordFailure from blocking the indexer.
                Logger.Warn($"Tidal returned 'countryCode parameter missing' (transient, not blocking indexer): {response?.HttpRequest?.Url?.FullUri}");
                return new List<ReleaseInfo>();
            }
            catch (Exception ex)
            {
                var url = response?.HttpRequest?.Url?.FullUri ?? "unknown";
                var requestType = response?.HttpRequest?.Headers?.ContainsKey("X-Tidal-Request-Type") == true
                    ? response.HttpRequest.Headers["X-Tidal-Request-Type"] : "search";
                Logger.Error(ex, $"INDEXER-BLOCK-TRAP: Exception in ParseResponse (type={requestType}, url={url}). This will trigger RecordFailure and may block the indexer.");
                TidalRequestGenerator.WriteBlockTrap($"ParseResponse failed (type={requestType}, url={url})", ex);
                throw;
            }
        }

        private IList<ReleaseInfo> ParseDirectAlbumResponse(string content, string lidarrArtistOverride = null)
        {
            try
            {
                var album = JObject.Parse(content).ToObject<TidalSearchResponse.Album>();
                if (album == null)
                    return new List<ReleaseInfo>();

                return ProcessAlbumResult(album, lidarrArtistOverride).ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to parse MB direct album response, falling back to search");
                return new List<ReleaseInfo>();
            }
        }

        private IList<ReleaseInfo> ParseSearchResponse(string content)
        {
            var torrentInfos = new List<ReleaseInfo>();
            var jsonResponse = JObject.Parse(content).ToObject<TidalSearchResponse>();

            if (jsonResponse?.AlbumResults?.Items == null)
            {
                return torrentInfos;
            }

            var releases = jsonResponse.AlbumResults.Items.Select(result => ProcessAlbumResult(result)).ToArray();

            foreach (var task in releases)
            {
                torrentInfos.AddRange(task);
            }

            if (jsonResponse.TrackResults?.Items != null)
            {
                foreach (var track in jsonResponse.TrackResults.Items)
                {
                    // make sure the album hasn't already been processed before doing this
                    if (!jsonResponse.AlbumResults.Items.Any(a => a.Id == track.Album.Id))
                    {
                        var processTrackTask = ProcessTrackAlbumResultAsync(track);
                        processTrackTask.Wait();
                        if (processTrackTask.Result != null)
                            torrentInfos.AddRange(processTrackTask.Result);
                    }
                }
            }

            return torrentInfos
                .OrderByDescending(o => o.Title.Contains("[Explicit]"))
                .ThenByDescending(o => o.Size)
                .ToArray();
        }

        private IList<ReleaseInfo> ParseHomePageResponse(string content)
        {
            var releases = new List<ReleaseInfo>();

            try
            {
                var json = JObject.Parse(content);

                // Look for rows containing modules with albums
                var rows = json["rows"];
                if (rows != null)
                {
                    foreach (var row in rows)
                    {
                        var modules = row["modules"];
                        if (modules != null)
                        {
                            foreach (var module in modules)
                            {
                                var title = module["title"]?.ToString() ?? "";
                                var type = module["type"]?.ToString() ?? "";

                                var pagedList = module["pagedList"];
                                var items = pagedList?["items"] ?? module["items"];
                                var itemCount = items?.Count() ?? 0;

                                // Look for album-related sections
                                bool isAlbumSection =
                                    type == "ALBUM_LIST" ||
                                    title.Contains("New", StringComparison.OrdinalIgnoreCase) ||
                                    title.Contains("Release", StringComparison.OrdinalIgnoreCase) ||
                                    title.Contains("Album", StringComparison.OrdinalIgnoreCase);

                                if (isAlbumSection && items != null && itemCount > 0)
                                {
                                    Logger.Info($"RSS: Found '{title}' with {itemCount} albums");

                                    foreach (var item in items)
                                    {
                                        try
                                        {
                                            var itemType = item["type"]?.ToString();
                                            if (itemType == "ALBUM" || item["numberOfTracks"] != null)
                                            {
                                                var album = item.ToObject<TidalSearchResponse.Album>();
                                                if (album != null)
                                                {
                                                    var albumReleases = ProcessAlbumResult(album);
                                                    releases.AddRange(albumReleases);

                                                    var artistName = album.Artists?.FirstOrDefault()?.Name ?? "Unknown";
                                                    Logger.Debug($"RSS: Added {artistName} - {album.Title}");
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.Debug($"RSS: Failed to parse item: {ex.Message}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                Logger.Info($"RSS: Found {releases.Count} total releases from Tidal Home page");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to parse Home page response");
            }

            return releases
                .OrderByDescending(o => o.PublishDate)
                .ThenByDescending(o => o.Title.Contains("[Explicit]"))
                .ThenByDescending(o => o.Size)
                .ToArray();
        }

        private IEnumerable<ReleaseInfo> ProcessAlbumResult(TidalSearchResponse.Album result, string artistOverride = null)
        {
            // determine available audio qualities
            List<AudioQuality> qualityList = new() { AudioQuality.LOW, AudioQuality.HIGH };

            var tags = result.MediaMetadata?.Tags ?? Array.Empty<string>();
            if (tags.Contains("HIRES_LOSSLESS"))
            {
                qualityList.Add(AudioQuality.LOSSLESS);
                qualityList.Add(AudioQuality.HI_RES_LOSSLESS);
            }
            else if (tags.Contains("LOSSLESS"))
                qualityList.Add(AudioQuality.LOSSLESS);

            var quality = Enum.Parse<AudioQuality>(result.AudioQuality);
            return qualityList.Select(q => ToReleaseInfo(result, q, artistOverride));
        }

        private async Task<IEnumerable<ReleaseInfo>> ProcessTrackAlbumResultAsync(TidalSearchResponse.Track result)
        {
            try
            {
                var album = (await TidalAPI.Instance.Client.API.GetAlbum(result.Album.Id)).ToObject<TidalSearchResponse.Album>();
                return ProcessAlbumResult(album);
            }
            catch (ResourceNotFoundException)
            {
                return null;
            }
        }

        private static ReleaseInfo ToReleaseInfo(TidalSearchResponse.Album x, AudioQuality bitrate, string artistOverride = null)
        {
            var publishDate = DateTime.UtcNow;
            var year = 0;
            if (DateTime.TryParse(x.ReleaseDate, out var digitalReleaseDate))
            {
                publishDate = digitalReleaseDate;
                year = publishDate.Year;
            }
            else if (DateTime.TryParse(x.StreamStartDate, out var startStreamDate))
            {
                publishDate = startStreamDate;
                year = startStreamDate.Year;
            }

            var artistName = artistOverride ?? x.Artists.First().Name;
            var url = x.Url;

            var result = new ReleaseInfo
            {
                Guid = $"Tidal-{x.Id}-{bitrate}",
                Artist = artistName,
                Album = x.Title,
                DownloadUrl = url,
                InfoUrl = url,
                PublishDate = publishDate,
                DownloadProtocol = nameof(TidalDownloadProtocol)
            };

            string format;
            switch (bitrate)
            {
                case AudioQuality.LOW:
                    result.Codec = "AAC";
                    result.Container = "96";
                    format = "AAC (M4A) 96kbps";
                    break;
                case AudioQuality.HIGH:
                    result.Codec = "AAC";
                    result.Container = "320";
                    format = "AAC (M4A) 320kbps";
                    break;
                case AudioQuality.LOSSLESS:
                    result.Codec = "FLAC";
                    result.Container = "Lossless";
                    format = "FLAC (M4A) Lossless";
                    break;
                case AudioQuality.HI_RES_LOSSLESS:
                    result.Codec = "FLAC";
                    result.Container = "24bit Lossless";
                    format = "FLAC (M4A) 24bit Lossless";
                    break;
                default:
                    throw new NotImplementedException();
            }

            // estimated sizing as tidal doesn't provide exact sizes in its api
            // LOSSLESS/HI_RES use FLAC-compressed estimates (~60% of raw PCM)
            // Most hi-res content is 96kHz/24-bit, not 192kHz
            var bps = bitrate switch
            {
                AudioQuality.HI_RES_LOSSLESS => 345600, // 96kHz*24bit*2ch * 0.6 FLAC ratio
                AudioQuality.LOSSLESS => 105840,         // 44.1kHz*16bit*2ch * 0.6 FLAC ratio
                AudioQuality.HIGH => 40000,
                AudioQuality.LOW => 12000,
                _ => 40000
            };
            var size = x.Duration * bps;

            result.Size = size;
            result.Title = $"{artistName} - {x.Title}";

            if (year > 0)
            {
                result.Title += $" ({year})";
            }

            if (x.Explicit)
            {
                result.Title += " [Explicit]";
            }

            result.Title += $" [{format}] [WEB]";

            return result;
        }
    }
}
