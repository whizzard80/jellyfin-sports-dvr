# Jellyfin Sports DVR

A Jellyfin plugin for smart sports recording with team subscriptions and automatic scheduling.

## Features

- **Team Subscriptions** - Subscribe to your favorite teams and automatically record all their games
- **League Subscriptions** - Record all games from a league (NBA, Premier League, etc.)
- **Event Subscriptions** - Record live events like UFC, WWE, F1
- **Smart LIVE Detection** - Only records actual live games, not replays or highlights
- **Time-Based Heuristics** - Knows when games typically air (NBA evening, European football morning)
- **Same-Game Deduplication** - Won't record the same game on multiple channels
- **Connection-Aware Scheduling** - Respects your IPTV connection limits (1, 2, 6, etc.)
- **Priority-Based Conflicts** - Higher priority subscriptions win tuner slots
- **Teamarr/Dispatcharr Compatible** - Works great with `<live>` tagged EPGs

## Installation

1. Download `Jellyfin.Plugin.SportsDVR.dll` from the [Releases](../../releases) page
2. Copy to your Jellyfin plugins folder:
   - **Linux**: `~/.local/share/jellyfin/plugins/SportsDVR/`
   - **Windows**: `%APPDATA%\Jellyfin\plugins\SportsDVR\`
   - **Docker**: `/config/plugins/SportsDVR/`
3. Restart Jellyfin
4. Go to **Dashboard → Plugins → Sports DVR** to configure

That's it! No additional software required.

## How It Works

```
┌─────────────────────────────────────────────────────────────┐
│                    Jellyfin Live TV Guide                   │
│         (EPG from Dispatcharr, Teamarr, or any source)      │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                      Sports DVR Plugin                       │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────────────┐   │
│  │ LIVE Filter │→ │Pattern Matcher│→ │ Smart Scheduler   │   │
│  │ (no replays)│  │ (matches subs)│  │ (respects limits) │   │
│  └─────────────┘  └──────────────┘  └───────────────────┘   │
│                                                              │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              Your Subscriptions                      │    │
│  │  • Boston Celtics (Team, Priority: 95)              │    │
│  │  • Premier League (League, Priority: 60)            │    │
│  │  • UFC (Event, Priority: 80)                        │    │
│  └─────────────────────────────────────────────────────┘    │
│                              │                               │
│                              ▼                               │
│              Jellyfin DVR (handles recording)               │
└─────────────────────────────────────────────────────────────┘
```

## Configuration

### Global Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Max Concurrent Recordings | Your IPTV connection limit | `2` |
| EPG Scan Interval | How often to check for matches | `5 min` |
| Enable Auto Scheduling | Automatically create recordings | `true` |

### Subscription Settings

| Setting | Description |
|---------|-------------|
| **Name** | Team, league, or event name (e.g., "Boston Celtics") |
| **Type** | Team, League, or Event |
| **Match Pattern** | Optional: custom pattern to match (auto-generated if blank) |
| **Exclude Patterns** | Skip if these match (e.g., "preview, countdown") |
| **Priority** | 1-100, higher wins conflicts (default: 50) |
| **Include Replays** | Record replays/encores/classics (default: off) |

## LIVE Detection

The plugin filters out non-live content:

### ✅ Will Record
- Programs with `<live>` EPG tag (Teamarr/Dispatcharr)
- Titles with "LIVE:" or "(Live)"
- Games in appropriate time windows:
  - **USA Sports** (NBA, NFL, NHL, MLB): 12 PM - 12 AM EST
  - **European Football** (Premier League, Serie A): 6 AM - 4 PM EST

### ❌ Will Skip
- Replays, encores, classics, throwbacks
- Highlights (HL), recaps, mini-games
- Pregame/postgame shows
- Old events with years: "(2008)", "(2016)"
- Past UFC events (UFC 273, UFC 311, etc.)
- Non-sports with "Premier League" (cricket T20, karate)

## Connection Scaling

The scheduler adapts to your IPTV plan:

| Connections | Behavior |
|-------------|----------|
| 1 | Only highest priority games |
| 2 | Good balance - your teams + alternates |
| 6+ | Captures most matching games |

When slots are full, higher priority subscriptions preempt lower ones.

## Pattern Matching Examples

| EPG Title | Detected As |
|-----------|-------------|
| `Live NBA: Celtics @ Mavericks` | ✅ Celtics game |
| `Boston Celtics at Houston Rockets` | ✅ Celtics game |
| `Serie A: Bologna vs AC Milan` | ✅ AC Milan + Serie A |
| `UFC 325: Volkanovski vs Lopes 2` | ✅ UFC event |
| `NBA 25/26: Lakers v Warriors` | ❌ No LIVE tag (replay) |
| `UFC 273: Main Card (2022)` | ❌ Old event |
| `Premier League Review` | ❌ Not a game |

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/SportsDVR/Subscriptions` | GET | List all subscriptions |
| `/SportsDVR/Subscriptions` | POST | Create subscription |
| `/SportsDVR/Subscriptions/{id}` | PUT | Update subscription |
| `/SportsDVR/Subscriptions/{id}` | DELETE | Delete subscription |
| `/SportsDVR/Status` | GET | Plugin status |
| `/SportsDVR/Aliases` | GET/POST | Manage team aliases |

## Building from Source

For developers only:

```bash
# Requires .NET 8.0 SDK
cd plugin/Jellyfin.Plugin.SportsDVR
dotnet build -c Release
```

Output: `bin/Release/net8.0/Jellyfin.Plugin.SportsDVR.dll`

## Requirements

- Jellyfin 10.9+
- Live TV configured with EPG data

## License

MIT License - see [LICENSE](LICENSE)

---

Built for sports fans who want automatic game recording without spoilers. 🏀⚽🏒🥊
