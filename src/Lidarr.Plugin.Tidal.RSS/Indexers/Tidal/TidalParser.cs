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
                var album = (await TidalAPI.Instance.Client.API.GetAlbum(result.Album.Id)).ToObject<TidalSearchResponse.Album>();
                return ProcessAlbumResult(album);
            }
            catch (ResourceNotFoundException)
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
