namespace NzbDrone.Core.Download.Clients.Tidal.Queue
{
    public class PersistedTrack
    {
        public string Id { get; set; }
        public int Chunks { get; set; }
    }

    public class PersistedDownloadItem
    {
        public string Id { get; set; }
        public string TidalUrl { get; set; }
        public string TidalId { get; set; }
        public string Quality { get; set; }
        public string Status { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string LidarrArtistName { get; set; }
        public bool Explicit { get; set; }
        public long TotalSize { get; set; }
        public string DownloadFolder { get; set; }
        public string TidalAlbumJson { get; set; }
        public PersistedTrack[] Tracks { get; set; }
    }
}
