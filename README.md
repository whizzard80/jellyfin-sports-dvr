# Jellyfin Sports DVR

A Jellyfin plugin for sports DVR with time-shifting, automatic recording, and a dedicated "Sports for You" home section.

## Features

- **Automatic Recording**: Record select games based on your preferences
- **Time-Shifting**: Watch live games from the beginning while they're still recording
- **Sports for You**: Dedicated home section showing your recorded games
- **Auto-Cleanup**: Automatically delete old recordings after configurable days
- **Integration**: Works with Teamarr/Dispatcharr for EPG and stream management

## Architecture

This project consists of two components:

### 1. Jellyfin Plugin (C#)
Located in `plugin/` - provides:
- "Sports for You" home section
- Sports metadata provider
- Cleanup scheduled task
- Configuration UI

### 2. Recording Service (Python)
Located in `service/` - provides:
- Stream recording via FFmpeg
- Time-shift HLS generation
- Connection limit management
- API for recording control

## Requirements

- Jellyfin 10.9.0 or later
- .NET 8.0 SDK (for plugin development)
- Python 3.11+ (for recording service)
- FFmpeg (for recording)
- Docker (optional, for running the service)

## Installation

### Plugin
1. Download the latest release from GitHub Releases
2. Extract to your Jellyfin plugins directory
3. Restart Jellyfin
4. Configure in Dashboard → Plugins → Sports DVR

### Recording Service
```bash
cd service
docker-compose up -d
```

Or without Docker:
```bash
cd service
pip install -e .
sports-dvr serve
```

## Configuration

### Plugin Settings
- **Recording Service URL**: URL of the recording service API
- **Sports Library Path**: Where recordings are stored
- **Retention Days**: How long to keep recordings (default: 3)
- **Connection Limit**: Max simultaneous recordings

### Integration with Teamarr/Dispatcharr
The recording service can pull EPG data from Teamarr and stream URLs from Dispatcharr.

## Development

### Building the Plugin
```bash
cd plugin
dotnet build
```

### Running the Service
```bash
cd service
python -m sports_dvr
```

## License

GPL-3.0 - Same as Jellyfin

## Related Projects

- [Jellyfin](https://github.com/jellyfin/jellyfin) - The Free Software Media System
- [Teamarr](https://github.com/Dispatcharr/Teamarr) - Sports EPG Generator
- [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) - IPTV Proxy and EPG Manager
