using System;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;
using NLog;

namespace NzbDrone.Core.Indexers.Tidal
{
    /// <summary>
    /// Manages a client_credentials token for Tidal's OpenAPI v2 (openapi.tidal.com).
    /// The v2 API requires a THIRD_PARTY token from the developer portal,
    /// separate from the INTERNAL user token used by TidalSharp for v1 API.
    /// </summary>
    public static class TidalOpenApiToken
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly SemaphoreSlim Lock = new(1, 1);

        // Developer portal credentials (client_credentials grant)
        private const string ClientId = "J14b1I6fOvff5JIm";
        private const string ClientSecret = "0IlRDPVzas2Izoy2Psa2npFmjze3Iz6nkI5nVtYPIm0=";
        private const string TokenUrl = "https://auth.tidal.com/v1/oauth2/token";

        private static string _accessToken;
        private static DateTime _expiresAt = DateTime.MinValue;

        public static string GetAuthorizationHeader(NzbDrone.Common.Http.IHttpClient httpClient)
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expiresAt)
                return $"Bearer {_accessToken}";

            Lock.Wait();
            try
            {
                // Double-check after acquiring lock
                if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expiresAt)
                    return $"Bearer {_accessToken}";

                var request = new NzbDrone.Common.Http.HttpRequest(TokenUrl);
                request.Method = HttpMethod.Post;
                request.Headers.ContentType = "application/x-www-form-urlencoded";
                request.SetContent($"client_id={ClientId}&client_secret={Uri.EscapeDataString(ClientSecret)}&grant_type=client_credentials");

                var response = httpClient.Post(request);
                var json = JObject.Parse(response.Content);

                _accessToken = json["access_token"]?.ToString();
                var expiresIn = json["expires_in"]?.ToObject<int>() ?? 86400;
                // Refresh 60 seconds early to avoid edge cases
                _expiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 60);

                Logger.Info($"Tidal OpenAPI v2 token acquired (type=THIRD_PARTY), expires in {expiresIn}s");
                return $"Bearer {_accessToken}";
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to acquire Tidal OpenAPI v2 token");
                return null;
            }
            finally
            {
                Lock.Release();
            }
        }
    }
}
