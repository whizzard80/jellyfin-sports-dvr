# Jellyfin Sports DVR

A Jellyfin plugin for smart sports recording with team subscriptions, automatic scheduling, and spoiler-free metadata.

## Features

- **Team Subscriptions** - Subscribe to your favorite teams and automatically record all their games
- **Smart Pattern Matching** - Extracts teams and leagues from EPG titles using intelligent parsing
- **Replay Detection** - Automatically skips replays/encores (optional per-subscription)
- **Per-Subscription Retention** - Different retention rules for different teams
- **Favorite Teams** - Mark teams to never auto-delete
- **Priority Conflicts** - Higher priority subscriptions win tuner slots
- **Rolling Window** - Keep the last N games per team, drop oldest for newest
- **Spoiler-Free** - Metadata never includes scores, results, or game outcomes

## How It Works

```
┌─────────────────────────────────────────────────────────────┐
│                    Jellyfin Live TV Guide                   │
│         (EPG from Dispatcharr or any tuner source)          │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                      Sports DVR Plugin                       │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────────────┐   │
│  │  EPG Parser │→ │Pattern Matcher│→ │Recording Scheduler│   │
│  │ (extracts   │  │ (matches     │  │ (creates Jellyfin │   │
│  │  teams)     │  │  subs)       │  │  timers)          │   │
│  └─────────────┘  └──────────────┘  └───────────────────┘   │
│                                                              │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              Subscription Manager                    │    │
│  │  • Lakers (Team, Pri:90, Keep:∞, Favorite)          │    │
│  │  • UFC (Event, Pri:80, Keep:5, Ret:14d)             │    │
│  │  • Premier League (League, Pri:50, Keep:10)          │    │
│  └─────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              Retention Manager                       │    │
│  │  • Enforces max recordings per subscription          │    │
│  │  • Applies retention days                           │    │
│  │  • Protects favorites from deletion                 │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

## Installation

1. Download the latest release from the Releases page
2. Extract `Jellyfin.Plugin.SportsDVR.dll` to your Jellyfin plugins folder:
   - Linux: `~/.local/share/jellyfin/plugins/SportsDVR/`
   - Windows: `%APPDATA%\Jellyfin\plugins\SportsDVR\`
   - Docker: `/config/plugins/SportsDVR/`
3. Restart Jellyfin
4. Go to Dashboard → Plugins → Sports DVR to configure

## Configuration

### Global Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Sports Library Path | Where recordings are stored | `/media/sports` |
| Max Concurrent Recordings | Tuner/stream limit | `2` |
| Default Priority | Priority for new subscriptions | `50` |
| Default Retention | Days to keep recordings | `7` |
| Default Max Recordings | Max games per subscription | `10` |
| EPG Scan Interval | How often to check for matches | `5 min` |

### Subscription Settings

Each subscription can have:

| Setting | Description |
|---------|-------------|
| **Name** | Display name (e.g., "Lakers", "UFC") |
| **Type** | Team, League, or Event |
| **Match Pattern** | Simple text or regex (e.g., `/lakers\|la lakers/i`) |
| **Exclude Patterns** | Skip if these match (e.g., "countdown, preview") |
| **Priority** | 1-100, higher wins conflicts |
| **Keep Last** | Max recordings to keep (0 = unlimited) |
| **Retention Days** | Auto-delete after N days (0 = never) |
| **Favorite** | Never auto-delete |
| **Include Replays** | Record replays/encores/classics |

## Pattern Matching

The plugin intelligently parses EPG titles:

| EPG Title | Extracted |
|-----------|-----------|
| `Lakers vs Warriors - NBA` | Teams: Lakers, Warriors / League: NBA |
| `UFC 300: Main Card (LIVE)` | Event: UFC / Live: Yes |
| `Premier League: Man City vs Leeds` | Teams: Man City, Leeds / League: Premier League |
| `NFL Sunday: Patriots @ Bills` | Teams: Patriots, Bills / League: NFL |
| `NBA Classic: 1995 Finals (Replay)` | Replay: Yes (skipped by default) |

### Replay Detection

These words trigger replay detection (skipped unless "Include Replays" is enabled):
- replay, encore, classic, rerun, re-air
- rebroadcast, throwback, vintage, best of
- Year patterns like "1995", "2018" (indicates old games)

### Team vs Event Subscriptions

- **Team** subscriptions require a matchup pattern (`vs`, `@`, `v`) in the title to avoid false positives like documentaries about the team
- **Event** subscriptions (UFC, WWE, F1) don't require matchup patterns since they're event series, not team matchups

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/SportsDVR/Subscriptions` | GET | List all subscriptions |
| `/SportsDVR/Subscriptions` | POST | Create subscription |
| `/SportsDVR/Subscriptions/{id}` | GET | Get subscription |
| `/SportsDVR/Subscriptions/{id}` | PUT | Update subscription |
| `/SportsDVR/Subscriptions/{id}` | DELETE | Delete subscription |
| `/SportsDVR/Subscriptions/{id}/Toggle` | POST | Enable/disable |
| `/SportsDVR/Parse` | POST | Parse a program title |
| `/SportsDVR/Recordings` | GET | List all recordings |
| `/SportsDVR/Recordings/Upcoming` | GET | Scheduled recordings |
| `/SportsDVR/Recordings/Recent` | GET | Recent recordings |
| `/SportsDVR/Status` | GET | Plugin status summary |

## Building from Source

```bash
cd plugin/Jellyfin.Plugin.SportsDVR
dotnet build -c Release
```

The DLL will be in `bin/Release/net8.0/`.

## Requirements

- Jellyfin 10.9+
- Live TV configured with EPG data (Dispatcharr, HDHomeRun, etc.)
- .NET 8.0 Runtime

## License

MIT License - see [LICENSE](LICENSE)

## Credits

Built for sports fans who want a dedicated DVR experience in Jellyfin without spoilers.
