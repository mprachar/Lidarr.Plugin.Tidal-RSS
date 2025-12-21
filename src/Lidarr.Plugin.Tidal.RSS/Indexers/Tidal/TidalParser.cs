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
            var content = new HttpResponse<TidalSearchResponse>(response.HttpResponse).Content;

            // Check request type from headers
            var requestType = response.HttpRequest.Headers.ContainsKey("X-Tidal-Request-Type")
                ? response.HttpRequest.Headers["X-Tidal-Request-Type"]
                : "";

            // Handle Home page response - extract new releases (primary source)
            if (requestType == "HOME")
            {
                Logger.Info("RSS Parser: Parsing Home page for new releases");
                return ParseHomePageResponse(content);
            }

            // Handle Explore page response - extract new releases (legacy)
            if (requestType == "EXPLORE")
            {
                Logger.Info("RSS Parser: Parsing Explore page for new releases");
                return ParseHomePageResponse(content); // Same structure as home
            }

            // Handle artist albums RSS request (legacy, may be removed)
            if (requestType == "RSS")
            {
                return ParseArtistAlbumsResponse(content);
            }

            // Regular search request
            return ParseSearchResponse(content);
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
                .OrderByDescending(o => o.Size)
                .ToArray();
        }

        private IList<ReleaseInfo> ParseHomePageResponse(string content)
        {
            var releases = new List<ReleaseInfo>();

            try
            {
                var json = JObject.Parse(content);

                Logger.Info("=== HOME PAGE RESPONSE STRUCTURE ===");

                // Log top-level keys
                foreach (var prop in json.Properties())
                {
                    Logger.Info($"Top-level key: {prop.Name}");
                }

                // Look for rows containing modules with albums
                var rows = json["rows"];
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

                                // Count items for logging
                                var pagedList = module["pagedList"];
                                var items = pagedList?["items"] ?? module["items"];
                                var itemCount = items?.Count() ?? 0;

                                Logger.Info($"Row {rowIndex} Module: '{title}' (type: {type}, items: {itemCount})");

                                // Look for album-related sections
                                // Match: "New Releases", "New Albums", "Top Albums", "Album Picks", "Recently Released", etc.
                                bool isAlbumSection =
                                    type == "ALBUM_LIST" ||
                                    title.Contains("New", StringComparison.OrdinalIgnoreCase) ||
                                    title.Contains("Release", StringComparison.OrdinalIgnoreCase) ||
                                    title.Contains("Album", StringComparison.OrdinalIgnoreCase) ||
                                    title.Contains("Top", StringComparison.OrdinalIgnoreCase);

                                if (isAlbumSection && items != null && itemCount > 0)
                                {
                                    Logger.Info($"  -> Processing album section: '{title}' with {itemCount} items");

                                    foreach (var item in items)
                                    {
                                        try
                                        {
                                            // Check if this is an album (has numberOfTracks or type=ALBUM)
                                            var itemType = item["type"]?.ToString();
                                            if (itemType == "ALBUM" || item["numberOfTracks"] != null)
                                            {
                                                var album = item.ToObject<TidalSearchResponse.Album>();
                                                if (album != null)
                                                {
                                                    var albumReleases = ProcessAlbumResult(album);
                                                    releases.AddRange(albumReleases);
                                                    Logger.Debug($"  -> Added album: {album.Title} by {album.Artists?.FirstOrDefault()?.Name}");
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.Debug($"  -> Failed to parse item: {ex.Message}");
                                        }
                                    }
                                }
                            }
                        }
                        rowIndex++;
                    }
                }

                Logger.Info($"=== END HOME PAGE - Found {releases.Count} releases ===");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to parse Home page response");
            }

            return releases
                .OrderByDescending(o => o.PublishDate)
                .ThenByDescending(o => o.Size)
                .ToArray();
        }

        private IList<ReleaseInfo> ParseArtistAlbumsResponse(string content)
        {
            var torrentInfos = new List<ReleaseInfo>();
            var json = JObject.Parse(content);

            // Artist albums endpoint returns { "limit": N, "offset": N, "totalNumberOfItems": N, "items": [...] }
            var items = json["items"]?.ToObject<TidalSearchResponse.Album[]>();

            if (items == null || items.Length == 0)
            {
                return torrentInfos;
            }

            // Filter albums by configured days back
            var daysBack = Settings.RssDaysBack > 0 ? Settings.RssDaysBack : 90;
            var cutoffDate = DateTime.UtcNow.AddDays(-daysBack);

            foreach (var album in items)
            {
                // Check release date to filter recent releases
                DateTime? releaseDate = null;
                if (DateTime.TryParse(album.ReleaseDate, out var parsedRelease))
                {
                    releaseDate = parsedRelease;
                }
                else if (DateTime.TryParse(album.StreamStartDate, out var parsedStream))
                {
                    releaseDate = parsedStream;
                }

                // Only include albums within the configured time window
                if (releaseDate.HasValue && releaseDate.Value >= cutoffDate)
                {
                    var releases = ProcessAlbumResult(album);
                    torrentInfos.AddRange(releases);
                }
            }

            return torrentInfos
                .OrderByDescending(o => o.PublishDate)
                .ThenByDescending(o => o.Size)
                .ToArray();
        }

        private IEnumerable<ReleaseInfo> ProcessAlbumResult(TidalSearchResponse.Album result)
        {
            // determine available audio qualities
            List<AudioQuality> qualityList = new() { AudioQuality.LOW, AudioQuality.HIGH };

            if (result.MediaMetadata.Tags.Contains("HIRES_LOSSLESS"))
            {
                qualityList.Add(AudioQuality.LOSSLESS);
                qualityList.Add(AudioQuality.HI_RES_LOSSLESS);
            }
            else if (result.MediaMetadata.Tags.Contains("LOSSLESS"))
                qualityList.Add(AudioQuality.LOSSLESS);

            var quality = Enum.Parse<AudioQuality>(result.AudioQuality);
            return qualityList.Select(q => ToReleaseInfo(result, q));
        }

        private async Task<IEnumerable<ReleaseInfo>> ProcessTrackAlbumResultAsync(TidalSearchResponse.Track result)
        {
            try
            {
                var album = (await TidalAPI.Instance.Client.API.GetAlbum(result.Album.Id)).ToObject<TidalSearchResponse.Album>(); // track albums hold much less data so we get the full one
                return ProcessAlbumResult(album);
            }
            catch (ResourceNotFoundException) // seems to occur in some cases, not sure why. i blame tidal
            {
                return null;
            }
        }

        private static ReleaseInfo ToReleaseInfo(TidalSearchResponse.Album x, AudioQuality bitrate)
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

            var url = x.Url;

            var result = new ReleaseInfo
            {
                Guid = $"Tidal-{x.Id}-{bitrate}",
                Artist = x.Artists.First().Name,
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
            var bps = bitrate switch
            {
                AudioQuality.HI_RES_LOSSLESS => 1152000,
                AudioQuality.LOSSLESS => 176400,
                AudioQuality.HIGH => 40000,
                AudioQuality.LOW => 12000,
                _ => 40000
            };
            var size = x.Duration * bps;

            result.Size = size;
            result.Title = $"{x.Artists.First().Name} - {x.Title}";

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
