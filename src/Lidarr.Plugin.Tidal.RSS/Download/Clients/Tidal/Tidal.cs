using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;
using NzbDrone.Plugin.Tidal;

namespace NzbDrone.Core.Download.Clients.Tidal
{
    public class Tidal : DownloadClientBase<TidalSettings>
    {
        private readonly ITidalProxy _proxy;

        public Tidal(ITidalProxy proxy,
                      IConfigService configService,
                      IDiskProvider diskProvider,
                      IRemotePathMappingService remotePathMappingService,
                      ILocalizationService localizationService,
                      Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _proxy = proxy;
        }

        public override string Protocol => nameof(TidalDownloadProtocol);

        public override string Name => "Tidal";

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var queue = _proxy.GetQueue(Settings);

            foreach (var item in queue)
            {
                item.DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);
            }

            return queue;
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
                DeleteItemData(item);

            _proxy.RemoveFromQueue(item.DownloadId, Settings);
        }

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            return _proxy.Download(remoteAlbum, Settings);
        }

        public override DownloadClientInfo GetStatus()
        {
            return new DownloadClientInfo
            {
                IsLocalhost = true,
                OutputRootFolders = new() { new OsPath(Settings.DownloadPath) }
            };
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            // given the way the code is setup, we don't really need to do anything here
        }
    }
}
