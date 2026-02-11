# Jellyfin Automatic Sports DVR
## Schedules teams, leagues, or events from your EPG data

**Use this stack:** **[Teamarr](https://github.com/Teamarr/Teamarr)** → **[Dispatcharr](https://github.com/Dispatcharr/Dispatcharr)** → **this plugin** (in Jellyfin).  
Teamarr matches sports to streams and tags them; Dispatcharr serves the M3U/EPG to Jellyfin; this plugin schedules recordings from that guide.

---

A Jellyfin plugin for smart sports recording with team subscriptions and automatic scheduling.

> **Important:** This plugin is designed to work with **[Teamarr](https://github.com/Teamarr/Teamarr)** and
> **[Dispatcharr](https://github.com/Dispatcharr/Dispatcharr)**. Teamarr provides clean, structured EPG data
> with `<live>` tags and proper team-vs-team formatting that the plugin relies on for accurate matching.
> Raw IPTV provider EPG data (without Teamarr) will produce poor results -- see
> [Why Teamarr is Required](#why-teamarr-is-required) below.

## Features

- **Team Subscriptions** - Subscribe to your favorite teams and automatically record all their games
- **League Subscriptions** - Record all games from a league (NBA, Premier League, etc.)
- **Event Subscriptions** - Record live events like UFC, WWE, F1
- **Smart LIVE Detection** - Only records actual live games, not replays or highlights
- **Time-Based Heuristics** - Knows when games typically air (NBA evening, European football morning)
- **Same-Game Deduplication** - Won't record the same game on multiple channels
- **Connection-Aware Scheduling** - Respects your IPTV connection limits (1, 2, 6, etc.)
- **Priority-Based Conflicts** - Higher priority subscriptions win tuner slots
- **Teamarr + Dispatcharr Integration** - Built for `<live>` tagged EPGs with clean matchup titles

## Required Stack

| Component | Purpose |
|-----------|---------|
| **[Dispatcharr](https://github.com/Dispatcharr/Dispatcharr)** | IPTV channel management, M3U/EPG output to Jellyfin |
| **[Teamarr](https://github.com/Teamarr/Teamarr)** | Matches IPTV streams to sports events, renames channels with clean titles and `<live>` tags |
| **Jellyfin** | Media server with Live TV/DVR configured |
| **This Plugin** | Scans the Teamarr-enriched EPG and schedules recordings |

```
IPTV Provider → Dispatcharr → Teamarr → Dispatcharr EPG Output → Jellyfin → Sports DVR Plugin
```

## Installation

1. Download `Jellyfin.Plugin.SportsDVR.dll` from the [Releases](../../releases) page
2. Copy to your Jellyfin plugins folder:
   - **Linux**: `~/.local/share/jellyfin/plugins/SportsDVR/`
   - **Windows**: `%APPDATA%\Jellyfin\plugins\SportsDVR\`
   - **Docker**: `/config/plugins/SportsDVR/`
3. Restart Jellyfin
4. Go to **Dashboard → Plugins → Sports DVR** to configure

**Prerequisites:** Dispatcharr and Teamarr must be set up and feeding EPG data into Jellyfin's Live TV before the plugin will find matches.

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

## Why Teamarr is Required

The plugin's matching engine depends on the clean, structured EPG data that Teamarr provides. Without Teamarr, raw IPTV provider EPG data is unreliable:

| Signal | With Teamarr | Raw Provider EPG |
|--------|-------------|-----------------|
| **`<live>` tag** | Set on all live games | Almost never set |
| **Program title** | `"Chicago Bulls at Boston Celtics"` | `"NBA Basketball"` (generic) |
| **Team names** | In the title | Buried in description or missing |
| **League name** | In EpisodeTitle field | Appears more on talk shows than games |
| **Replay detection** | Clean titles, easy to filter | Replays have better titles than live games |
| **Channel naming** | `"NBA: Bulls at Celtics"` | `"USA ESPN"` (no game info) |

**What happens without Teamarr:**
- League subscriptions (e.g., "NBA") match studio shows ("NBA Today", "NBA Countdown") more than actual games
- Team subscriptions find zero matches because team names aren't in the title
- "Premier League" matches only highlight reels, not real matches
- UFC/Boxing subscriptions match 24/7 replay channels full of old fights
- The `<live>` tag that the plugin uses to distinguish real games from replays is missing entirely

**Bottom line:** Teamarr transforms messy IPTV data into clean, machine-readable EPG entries. This plugin is the scheduling engine that acts on that clean data.

## LIVE Detection

The plugin's core filtering relies on the `<live>` EPG tag that Teamarr sets on live game broadcasts.

### ✅ Will Record
- Programs with `<live>` EPG tag **(set by Teamarr)**
- Titles with "LIVE:" or "(Live)"
- Games with matchup patterns (vs/@/at) in appropriate time windows:
  - **USA Sports** (NBA, NFL, NHL, MLB): 12 PM - 12 AM EST
  - **European Football** (Premier League, Serie A): 6 AM - 4 PM EST

### ❌ Will Skip
- Replays, encores, classics, throwbacks
- Highlights (HL), recaps, mini-games
- Pregame/postgame shows
- Old events with years: "(2008)", "(2016)"
- Past UFC events (UFC 273, UFC 311, etc.)
- Non-sports with "Premier League" (cricket T20, karate)
- Generic league titles without `<live>` tag (e.g., bare "NBA Basketball")

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
| `/SportsDVR/Schedule/ScanNow` | POST | Trigger immediate EPG scan |
| `/SportsDVR/Schedule/Upcoming` | GET | Get upcoming scheduled recordings |
| `/SportsDVR/Schedule/ClearCache` | POST | Reset internal schedule tracking |
| `/SportsDVR/Aliases/{teamName}` | GET | Lookup team aliases |
| `/SportsDVR/Aliases/Custom` | GET/POST/DELETE | Manage custom team aliases |
| `/SportsDVR/Analysis/EpgStats` | GET | EPG analysis with scoring breakdown |
| `/SportsDVR/Analysis/TestSubscriptions` | GET | Test subscriptions against current EPG |
| `/SportsDVR/Analysis/ScoreTitle` | POST | Score a single title for testing |

> **Note:** All endpoints require admin authentication. Use `?api_key=YOUR_KEY` or
> the `X-Emby-Token` header. Create an API key in **Dashboard > API Keys**.

## Building from Source

For developers only:

```bash
# Requires .NET 8.0+ SDK
cd plugin/Jellyfin.Plugin.SportsDVR
dotnet build -c Release
```

Output: `bin/Release/net9.0/Jellyfin.Plugin.SportsDVR.dll`

## Requirements

- Jellyfin 10.9+
- Live TV configured with Teamarr-enriched EPG data
- [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) for IPTV channel management
- [Teamarr](https://github.com/Teamarr/Teamarr) for sports event matching and `<live>` tagging

## License

MIT License - see [LICENSE](LICENSE)
