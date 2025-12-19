using System;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Tidal;
using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Tidal;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class Tidal : HttpIndexerBase<TidalIndexerSettings>
    {
        public override string Name => "Tidal";
        public override string Protocol => nameof(TidalDownloadProtocol);
        public override bool SupportsRss => false;
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
                Logger = _logger
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new TidalParser()
            {
                Settings = Settings
            };
        }
    }
}
