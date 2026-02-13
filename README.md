# Jellyfin Automatic Sports DVR
## Schedules teams, leagues, or events from your Teamarr EPG data

**Use this stack:** **[Teamarr](https://github.com/Pharaoh-Labs/teamarr)** → **[Dispatcharr](https://github.com/Dispatcharr/Dispatcharr)** → **this plugin** (in Jellyfin) → optionally **[Comskip](https://github.com/whizzard80/Comskip)**.  
Teamarr matches sports to streams and tags them; Dispatcharr serves the M3U/EPG to Jellyfin; this plugin schedules recordings from that guide. Teamarr does the heavy lifting—as long as your IPTV / Cable / Satellite / OTA provider has EPG data, you can plug it in for proper results. Without this stack, you must rely on Jellyfin's built-in "record series," which can lead to errors. For a consistent, archivable library of games, add [Comskip](https://github.com/whizzard80/Comskip) with its [sports-dvr config](https://github.com/whizzard80/Comskip/tree/sports-detection/sports-dvr) to strip commercials from recordings.

---

A Jellyfin plugin for smart sports recording with team subscriptions and automatic scheduling.

> **Important:** This plugin is designed to work with **[Teamarr](https://github.com/Pharaoh-Labs/teamarr)** and
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
| **[Teamarr](https://github.com/Pharaoh-Labs/teamarr)** | Matches IPTV streams to sports events, renames channels with clean titles and `<live>` tags |
| **Jellyfin** | Media server with Live TV/DVR configured |
| **This Plugin** | Scans the Teamarr-enriched EPG and schedules recordings |
| **[Comskip](https://github.com/whizzard80/Comskip)** *(optional)* | Commercial detection for sports—[sports-dvr config](https://github.com/whizzard80/Comskip/tree/sports-detection/sports-dvr) strips ads for a clean archive |

```
IPTV Provider → Dispatcharr → Teamarr → Dispatcharr EPG Output → Jellyfin → Sports DVR Plugin → Comskip (optional)
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

### Guide must refresh daily

This plugin **only reads** the guide—it does not fetch or update EPG data. If you see yesterday's data (e.g. everything showing "Game Complete"), the guide in Jellyfin is stale.

- **Refresh the guide** at least daily so Jellyfin has today's programs. In **Dashboard → Live TV**, use your tuner's option to refresh/reload the guide (or ensure the EPG URL is fetched on a schedule).
- **Dispatcharr** should regenerate the M3U/EPG (e.g. from Teamarr) on a schedule (e.g. daily or every few hours). Point Jellyfin's Live TV at Dispatcharr's EPG URL so each refresh pulls fresh data.
- Until the guide is updated, the plugin will only see old entries and may schedule nothing or the wrong slots.

### Guide still shows "Game Complete" after refreshing

Jellyfin has a [known bug](https://github.com/jellyfin/jellyfin/issues/6103): **"Refresh Guide Data" often does not clear the EPG cache**, so the UI keeps showing old program data even after you refresh in both Dispatcharr and Jellyfin.

**Workaround — manually clear Jellyfin's guide cache**, then run "Refresh Guide Data" again:

1. **Stop Jellyfin** (so nothing is using the cache).
2. Delete the Live TV cache directories (paths are under Jellyfin's **cache** directory):
   - `cache/xmltv/*` (all files inside the `xmltv` folder)
   - `cache/*_channels` (any folder whose name ends with `_channels`)
3. **Start Jellyfin**, then go to **Dashboard → Live TV** and run **Refresh Guide Data** for your tuner.

**Where is the cache directory?**

| Setup | Typical cache path |
|-------|---------------------|
| **Linux** (native) | `~/.cache/jellyfin` or `$XDG_CACHE_HOME/jellyfin` |
| **Docker** | Often `/config/cache` (if `/config` is your data volume) |
| **Windows** | Under Jellyfin data directory, e.g. `%APPDATA%\Jellyfin\cache` |

Example (Linux native):

```bash
# Stop Jellyfin first, then:
rm -rf ~/.cache/jellyfin/xmltv/*
rm -rf ~/.cache/jellyfin/*_channels
# Start Jellyfin, then in the UI: Live TV → Refresh Guide Data
```

Example (Docker, with `/config` as data volume):

```bash
docker stop jellyfin
rm -rf /path/to/config/cache/xmltv/*
rm -rf /path/to/config/cache/*_channels
docker start jellyfin
# Then in the UI: Live TV → Refresh Guide Data
```

After that, the guide should load fresh data from Dispatcharr and "Game Complete" placeholders should be replaced with today's programs.

**Built-in (recommended):** The plugin can purge the guide cache for you so you start each day with a clean slate. In **Dashboard → Plugins → Sports DVR**, enable **"Purge Live TV guide cache daily"** and set **"Cache purge at"** to the hour you want (e.g. 4 AM, server local time). The purge runs at most once every 24 hours when the "Scan EPG for Sports" task runs during that hour. After the purge, run **"Refresh Guide Data"** in Dashboard → Live TV (or schedule Jellyfin's own "Refresh Guide Data" task to run shortly after).

**Optional script/cron:** If you prefer to purge outside the plugin, see [scripts/README.md](scripts/README.md).

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
- [Teamarr](https://github.com/Pharaoh-Labs/teamarr) for sports event matching and `<live>` tagging
- [Comskip](https://github.com/whizzard80/Comskip) *(optional)* — sports-tuned commercial detection; [sports-dvr config](https://github.com/whizzard80/Comskip/tree/sports-detection/sports-dvr) for Jellyfin integration

## License

MIT License - see [LICENSE](LICENSE)
