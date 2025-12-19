using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using NLog;
using NzbDrone.Common.Http;
using TidalSharp;

namespace NzbDrone.Plugin.Tidal
{
    public class TidalAPI
    {
        public static TidalAPI Instance { get; private set; }

        public static void Initialize(string configDir, IHttpClient httpClient, Logger logger)
        {
            if (Instance != null)
                return;
            Instance = new TidalAPI(configDir, httpClient);
        }

        private TidalAPI(string configDir, IHttpClient httpClient)
        {
            Instance = this;
            _client = new(configDir, httpClient);
        }

        public TidalClient Client => _client;

        private TidalClient _client;

        public string GetAPIUrl(string method, Dictionary<string, string> parameters = null)
        {
            parameters ??= new();
            parameters["sessionId"] = _client.ActiveUser?.SessionID ?? "";
            parameters["countryCode"] = _client.ActiveUser?.CountryCode ?? "";
            if (!parameters.ContainsKey("limit"))
                parameters["limit"] = "1000";

            StringBuilder stringBuilder = new("https://api.tidal.com/v1/");
            stringBuilder.Append(method);
            for (var i = 0; i < parameters.Count; i++)
            {
                var start = i == 0 ? "?" : "&";
                var key = WebUtility.UrlEncode(parameters.ElementAt(i).Key);
                var value = WebUtility.UrlEncode(parameters.ElementAt(i).Value);
                stringBuilder.Append(start + key + "=" + value);
            }
            return stringBuilder.ToString();
        }
    }
}
