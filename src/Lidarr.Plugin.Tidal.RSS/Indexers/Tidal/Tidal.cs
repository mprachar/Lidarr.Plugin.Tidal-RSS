using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Tidal;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Tidal;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class Tidal : HttpIndexerBase<TidalIndexerSettings>
    {
        public override string Name => "Tidal";
        public override string Protocol => nameof(TidalDownloadProtocol);
        public override bool SupportsRss => true;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(2);

        private readonly ITidalProxy _tidalProxy;

        public Tidal(ITidalProxy tidalProxy,
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _tidalProxy = tidalProxy;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            if (!string.IsNullOrEmpty(Settings.ConfigPath))
            {
                TidalAPI.Initialize(Settings.ConfigPath, _httpClient, _logger);
                try
                {
                    var loginTask = TidalAPI.Instance.Client.Login(Settings.RedirectUrl);
                    loginTask.Wait();

                    // the url was submitted to the api so it likely cannot be reused
                    TidalAPI.Instance.Client.RegeneratePkceCodes();

                    var success = loginTask.Result;
                    if (!success)
                    {
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Tidal login failed:\n{ex}");
                }
            }
            else
                return null;

            return new TidalRequestGenerator()
            {
                Settings = Settings,
                Logger = _logger,
                HttpClient = _httpClient
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new TidalParser()
            {
                Settings = Settings
            };
        }

        protected override async Task<IList<ReleaseInfo>> FetchPage(IndexerRequest request, IParseIndexerResponse parser)
        {
            try
            {
                return await base.FetchPage(request, parser);
            }
            catch (Exception ex)
            {
                // Swallow ALL exceptions during fetch/parse to prevent RecordFailure from
                // blocking the indexer. The Tidal API is unreliable (transient 401s, countryCode
                // errors, stale sessions) and Lidarr's escalating backoff is too aggressive
                // for a single-indexer setup. Log to persistent file for diagnostics.
                _logger.Warn($"Tidal request failed (swallowed to prevent indexer block): {ex.Message}");
                TidalRequestGenerator.WriteBlockTrap($"FetchPage swallowed: {request?.Url?.FullUri}", ex);
                return new List<ReleaseInfo>();
            }
        }
    }
}
