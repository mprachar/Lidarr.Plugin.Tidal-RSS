using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Indexers.Tidal
{
    /// <summary>
    /// Manual album "pin" map: forces a specific Lidarr album to a specific Tidal album ID.
    ///
    /// Some releases are unreachable by every automatic strategy at once. Classical is the
    /// worst case: MusicBrainz has no Tidal URL relation for the release, the barcode returns
    /// no match on Tidal's OpenAPI v2, and the Tier 1 text search is (correctly) discarded by
    /// the artist pre-filter because Lidarr tracks the composer while Tidal credits the
    /// performer. Verdi's Aida under Karajan is the motivating example.
    ///
    /// A pin short-circuits all of that: the album is fetched directly by ID, with the Lidarr
    /// artist name forced via the existing X-Tidal-Lidarr-Artist override.
    ///
    /// Each entry's key is either "Artist - Album" (matched case-, spacing- and
    /// diacritic-insensitively) or the album's MusicBrainz release-group ID (exact).
    /// The value is a Tidal album ID or any tidal.com album URL.
    /// </summary>
    internal static class TidalAlbumPin
    {
        private static readonly Regex TidalUrlRegex =
            new(@"album/(\d+)", RegexOptions.Compiled);

        private static readonly Regex BareIdRegex =
            new(@"^\d+$", RegexOptions.Compiled);

        private static readonly Regex MbidRegex =
            new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NonAlphanumeric = new(@"[^a-z0-9]", RegexOptions.Compiled);

        /// <summary>
        /// Returns the pinned Tidal album ID for this search, or null if nothing matches.
        /// Never throws — a malformed entry is skipped, not fatal, because this runs inside
        /// the indexer request path where an exception can get the indexer blocked.
        /// </summary>
        public static string Resolve(IEnumerable<KeyValuePair<string, string>> pins, string mbid, string artist, string album)
        {
            if (pins == null)
                return null;

            var wanted = Normalize($"{artist} - {album}");

            foreach (var pin in pins)
            {
                var key = pin.Key?.Trim();
                var value = pin.Value?.Trim();

                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                    continue;

                // Accept a bare ID or any tidal.com album URL (query strings included).
                var urlMatch = TidalUrlRegex.Match(value);
                var tidalId = urlMatch.Success ? urlMatch.Groups[1].Value
                            : BareIdRegex.IsMatch(value) ? value
                            : null;

                if (tidalId == null)
                    continue;

                if (MbidRegex.IsMatch(key))
                {
                    if (!string.IsNullOrEmpty(mbid) &&
                        string.Equals(key, mbid, StringComparison.OrdinalIgnoreCase))
                        return tidalId;
                }
                else if (wanted.Length > 0 && Normalize(key) == wanted)
                {
                    return tidalId;
                }
            }

            return null;
        }

        /// <summary>
        /// Lowercase, strip diacritics, drop everything that isn't a letter or digit. Makes
        /// "Antonín Dvořák - The Nine Symphonies" match a hand-typed
        /// "Antonin Dvorak - the nine symphonies".
        /// </summary>
        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            var decomposed = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }

            return NonAlphanumeric.Replace(sb.ToString().ToLowerInvariant(), "");
        }
    }
}
