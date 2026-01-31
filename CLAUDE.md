# Lidarr.Plugin.Tidal-RSS - Claude Code Context

> **Part of the Prachar Media Infrastructure ecosystem.**
> See [prachflix-hub/MEDIA-INFRASTRUCTURE.md](../prachflix-hub/MEDIA-INFRASTRUCTURE.md) for architecture overview and cross-project documentation.

| Related Projects | Purpose |
|------------------|---------|
| [PrachFlix](../PrachFlix/) | User-facing Wrapped reports for friends & family |
| [Lidarr Tools](../Lidarr/) | Admin CLI for library maintenance (Mike only) |
| **This project** | Lidarr plugin for Tidal integration |
| [prachflix-hub](../prachflix-hub/) | Central documentation hub |

---

## Project Overview
Lidarr plugin adding Tidal as an indexer and downloader with RSS feed support for monitoring artist releases. Fork of [TrevTV/Lidarr.Plugin.Tidal](https://github.com/TrevTV/Lidarr.Plugin.Tidal).

## Repository
- **GitHub**: `git@github.com:mprachar/Lidarr.Plugin.Tidal-RSS.git`
- **Upstream**: `git@github.com:TrevTV/Lidarr.Plugin.Tidal.git`
- **Based on**: TrevTV version 10.1.0.42

## Project Structure
```
Lidarr.Plugin.Tidal-RSS/
├── src/
│   ├── Lidarr.Plugin.Tidal.RSS/    # Main plugin code
│   │   ├── Download/               # Tidal download client
│   │   ├── Indexers/               # Tidal indexer + RSS
│   │   ├── Blocklisting/           # Failed download handling
│   │   ├── Plugin.cs               # Plugin entry point
│   │   └── FFMPEG.cs               # Audio conversion
│   └── TidalSharp/                 # Tidal API wrapper library
│       ├── Downloading/            # MPD parsing, stream handling
│       │   ├── MPD.cs              # MPEG-DASH manifest parser
│       │   ├── DashInfo.cs         # DASH stream info
│       │   └── XmlNodeExtensions.cs # XML parsing helpers
│       └── API/                    # Tidal API client
├── ext/
│   └── Lidarr/                     # Lidarr source (submodule)
└── .github/workflows/
    └── build.yml                   # GitHub Actions CI/CD
```

## Build & Deployment

### GitHub Actions (Automatic)
Pushes to `main` trigger the build workflow which:
1. Builds the plugin for .NET 8.0
2. Creates a versioned zip: `Lidarr.Plugin.Tidal.net8.0.zip`
3. Creates a draft GitHub release with the artifact

### Plugin Update in Lidarr
Lidarr can pull updates directly from GitHub:
1. Go to **System → Plugins**
2. Find the Tidal plugin
3. Click **Update** (if available) or reinstall from:
   `https://github.com/mprachar/Lidarr.Plugin.Tidal-RSS`
4. Restart Lidarr

### Manual Local Build
```bash
cd /home/mprachar/ClaudeWorkspace/Lidarr.Plugin.Tidal-RSS
dotnet restore src/*.sln
dotnet build src/*.sln -c Release -f net8.0
# Output: _plugins/net8.0/Lidarr.Plugin.Tidal/
```

### Deploy to Local Lidarr (Windows)
Debug builds auto-copy to: `C:\ProgramData\Lidarr\plugins\mprachar\Lidarr.Plugin.Tidal-RSS`

## Key Components

### TidalSharp Library (`src/TidalSharp/`)
.NET Tidal API wrapper handling:
- OAuth authentication flow
- Track/album metadata fetching
- MPEG-DASH stream downloading
- MPD manifest parsing

### MPD Parser (`src/TidalSharp/Downloading/MPD.cs`)
Parses Tidal's MPEG-DASH manifests for streaming. Key classes:
- `MPD` - Root manifest
- `Period` - Time period in stream
- `AdaptationSet` - Audio/video adaptation set
- `Representation` - Quality level
- `SegmentTemplate` - URL templates for chunks

**Common Issue**: Tidal sometimes changes manifest format. If downloads fail with `FormatException`, check that attribute types match what Tidal returns (e.g., `Group` changed from `uint` to `string` in Jan 2025).

### RSS Feature (`src/Lidarr.Plugin.Tidal.RSS/Indexers/`)
Monitors configured Tidal artists for new releases via RSS sync.
- Configure artist IDs in indexer settings
- `RSS Days Back` controls lookback window

## Environment

### Lidarr Instance
- **URL**: http://192.168.10.10:8686
- **Requires**: Lidarr `pr-plugins` branch (e.g., `ghcr.io/hotio/lidarr:pr-plugins`)
- **Min Version**: 3.0.0.4855

### Development
- **.NET**: 8.0
- **IDE**: Visual Studio or VS Code with C# extension
- **Build**: `dotnet build`

## Syncing with Upstream

```bash
cd /home/mprachar/ClaudeWorkspace/Lidarr.Plugin.Tidal-RSS

# Fetch upstream changes
git fetch upstream

# Check upstream releases for fixes
# https://github.com/TrevTV/Lidarr.Plugin.Tidal/releases

# Merge specific fixes or rebase
git cherry-pick <commit>
# or
git merge upstream/main
```

## Common Issues & Fixes

### FormatException on Download
**Error**: `The input string 'X' was not in a correct format` in MPD parsing
**Cause**: Tidal changed manifest attribute types
**Fix**: Update the attribute type in `MPD.cs` (e.g., `uint` → `string`)

### countryCode parameter missing
**Error**: RSS sync fails with countryCode error
**Cause**: Authentication issue or API change
**Fix**: Re-authenticate with Tidal in indexer settings

### FFMPEG Conversion Hangs
**Error**: Downloads stall during conversion
**Cause**: FFMPEG process deadlock
**Fix**: See commit `c2453d7` for process handling improvements

## Version History

| Version | Based On | Key Changes |
|---------|----------|-------------|
| 10.1.0.43+ | TrevTV 10.1.0.42 | AdaptationSet.Group string fix, FFMPEG deadlock fix, RSS improvements |
| 10.1.0.42-rss.1 | TrevTV 10.1.0.42 | Initial fork with RSS feed support |
