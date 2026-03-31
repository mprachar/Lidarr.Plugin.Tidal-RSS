using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using NLog;

namespace NzbDrone.Core.Download.Clients.Tidal.Queue
{
    public static class QueuePersistence
    {
        public static void Save(IEnumerable<PersistedDownloadItem> items, string filePath, Logger logger)
        {
            try
            {
                var json = JsonConvert.SerializeObject(items, Formatting.Indented);
                var tmpPath = filePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to persist Tidal download queue to {filePath}: {ex.Message}");
            }
        }

        public static List<PersistedDownloadItem> Load(string filePath, Logger logger)
        {
            try
            {
                if (!File.Exists(filePath))
                    return new List<PersistedDownloadItem>();

                var json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<List<PersistedDownloadItem>>(json)
                       ?? new List<PersistedDownloadItem>();
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to load persisted Tidal download queue from {filePath}: {ex.Message}");
                return new List<PersistedDownloadItem>();
            }
        }
    }
}
