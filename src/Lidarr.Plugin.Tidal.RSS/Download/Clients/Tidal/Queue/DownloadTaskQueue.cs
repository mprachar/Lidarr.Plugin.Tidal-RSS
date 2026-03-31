using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using NLog;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Common.Instrumentation.Extensions;

namespace NzbDrone.Core.Download.Clients.Tidal.Queue
{
    public class DownloadTaskQueue
    {
        private readonly Channel<DownloadItem> _queue;
        private readonly List<DownloadItem> _items;
        private readonly Dictionary<DownloadItem, CancellationTokenSource> _cancellationSources;

        private readonly List<Task> _runningTasks = new();
        private readonly object _lock = new();

        private TidalSettings _settings;
        private readonly Logger _logger;
        private string _persistPath;

        public DownloadTaskQueue(int capacity, TidalSettings settings, Logger logger)
        {
            BoundedChannelOptions options = new(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            };
            _queue = Channel.CreateBounded<DownloadItem>(options);
            _items = new();
            _cancellationSources = new();
            _settings = settings;
            _logger = logger;
        }

        public void SetSettings(TidalSettings settings) => _settings = settings;

        public void SetPersistPath(string path) => _persistPath = path;

        public void StartQueueHandler()
        {
            Task.Run(() => BackgroundProcessing());
        }

        private async Task BackgroundProcessing(CancellationToken stoppingToken = default)
        {
            using SemaphoreSlim semaphore = new(3, 3);

            async Task HandleTask(DownloadItem item, Task task)
            {
                try
                {
                    var token = GetTokenForItem(item);
                    item.Status = DownloadItemStatus.Downloading;
                    PersistQueue();
                    await task;
                }
                catch (TaskCanceledException) { }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    item.Status = DownloadItemStatus.Failed;
                    _logger.Error("Error while downloading Tidal album " + item.Title);
                    _logger.Error(ex.ToString());
                }
                finally
                {
                    semaphore.Release();
                    lock (_lock)
                        _runningTasks.Remove(task);
                    PersistQueue();
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await semaphore.WaitAsync(stoppingToken);

                var item = await DequeueAsync(stoppingToken);
                var token = GetTokenForItem(item);
                var downloadTask = item.DoDownload(_settings, _logger, token);

                lock (_lock)
                    _runningTasks.Add(HandleTask(item, downloadTask));
            }

            List<Task> remainingTasks;
            lock (_lock)
                remainingTasks = _runningTasks.ToList();
            await Task.WhenAll(remainingTasks);
        }

        public async ValueTask QueueBackgroundWorkItemAsync(DownloadItem workItem)
        {
            await _queue.Writer.WriteAsync(workItem);
            CancellationTokenSource token = new();
            lock (_lock)
            {
                _items.Add(workItem);
                _cancellationSources.Add(workItem, token);
            }
            PersistQueue();
        }

        private async ValueTask<DownloadItem> DequeueAsync(CancellationToken cancellationToken)
        {
            var workItem = await _queue.Reader.ReadAsync(cancellationToken);
            return workItem;
        }

        public void RemoveItem(DownloadItem workItem)
        {
            if (workItem == null)
                return;

            lock (_lock)
            {
                if (_cancellationSources.TryGetValue(workItem, out var cts))
                    cts.Cancel();

                _items.Remove(workItem);
                _cancellationSources.Remove(workItem);
            }
            PersistQueue();
        }

        public DownloadItem[] GetQueueListing()
        {
            lock (_lock)
                return _items.ToArray();
        }

        public CancellationToken GetTokenForItem(DownloadItem item)
        {
            lock (_lock)
            {
                if (_cancellationSources.TryGetValue(item, out var src))
                    return src!.Token;
                return default;
            }
        }

        public int LoadPersistedItems()
        {
            if (_persistPath == null) return 0;

            var persisted = QueuePersistence.Load(_persistPath, _logger);
            var loaded = 0;

            lock (_lock)
            {
                var existingUrls = new HashSet<string>(_items
                    .Where(i => i.TidalUrlInfo != null)
                    .Select(i => i.TidalUrlInfo.Url));

                foreach (var p in persisted)
                {
                    try
                    {
                        // Deduplicate by Tidal URL
                        if (existingUrls.Contains(p.TidalUrl))
                            continue;

                        var item = DownloadItem.FromPersisted(p);
                        if (item == null) continue;

                        _items.Add(item);
                        _cancellationSources.Add(item, new CancellationTokenSource());

                        // Only enqueue Queued items to the channel for processing
                        // Completed/Failed items just sit in _items for Lidarr to see
                        if (item.Status == DownloadItemStatus.Queued)
                        {
                            _queue.Writer.TryWrite(item);
                        }

                        existingUrls.Add(p.TidalUrl);
                        loaded++;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to restore persisted download item {p.Id}: {ex.Message}");
                    }
                }

            }

            // Re-persist to clean up any items that failed to restore
            if (loaded < persisted.Count)
                PersistQueue();

            return loaded;
        }

        private void PersistQueue()
        {
            if (_persistPath == null) return;
            List<PersistedDownloadItem> snapshot;
            lock (_lock)
            {
                snapshot = _items.Select(i => i.ToPersistedItem(i.RemoteAlbum?.Artist?.Name)).ToList();
            }
            QueuePersistence.Save(snapshot, _persistPath, _logger);
        }
    }
}
