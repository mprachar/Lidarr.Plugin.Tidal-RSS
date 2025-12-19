using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Tidal;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class TidalParser : IParseIndexerResponse
    {
        public TidalIndexerSettings Settings { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var torrentInfos = new List<ReleaseInfo>();
            var content = new HttpResponse<TidalSearchResponse>(response.HttpResponse).Content;

            var jsonResponse = JObject.Parse(content).ToObject<TidalSearchResponse>();
            var releases = jsonResponse.AlbumResults.Items.Select(result => ProcessAlbumResult(result)).ToArray();

            foreach (var task in releases)
            {
                torrentInfos.AddRange(task);
            }

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

            return torrentInfos
                .OrderByDescending(o => o.Size)
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
