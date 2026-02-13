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
- **Smart LIVE Detection** - Only records actual live games, not replays or highlights (via Teamarr's `<live/>` tag)
- **Genre-Aware Matching** - "NCAA Basketball" won't match NCAA Baseball (uses EPG categories)
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
| **Program title** | `"NBA Basketball"` or `"NFL Football"` | `"NBA Basketball"` (generic, same) |
| **Subtitle** | `"Chicago Bulls at Boston Celtics"` | Empty or unrelated |
| **Team names** | In title and/or subtitle | Buried in description or missing |
| **League name** | In subtitle and categories | Appears more on talk shows than games |
| **Categories** | `Sports`, `Basketball`, `Sports Event` | Missing or generic |
| **Channel naming** | `"NBA: Bulls at Celtics"` | `"USA ESPN"` (no game info) |

**What happens without Teamarr:**
- League subscriptions (e.g., "NBA") match studio shows ("NBA Today", "NBA Countdown") more than actual games
- Team subscriptions find zero matches because team names aren't in the title
- "Premier League" matches only highlight reels, not real matches
- UFC/Boxing subscriptions match 24/7 replay channels full of old fights
- The `<live>` tag that the plugin uses to distinguish real games from replays is missing entirely
- No genre/category data means "NCAA Basketball" can't be distinguished from "NCAA Baseball"

**Bottom line:** Teamarr transforms messy IPTV data into clean, machine-readable EPG entries. This plugin is the scheduling engine that acts on that clean data.

## Teamarr Template Configuration

The plugin reads three key pieces of data from the EPG that Teamarr generates. Your templates
must include the right variables for the plugin to match correctly. Teamarr has
[187+ template variables](https://github.com/Pharaoh-Labs/teamarr) you can use to customize
titles, subtitles, descriptions, and more. The fields below are the ones the plugin actually
reads — set them up and everything else is yours to customize.

### What the Plugin Reads from EPG

| XMLTV Field | Jellyfin Maps To | Plugin Uses It For |
|-------------|-----------------|-------------------|
| `<title>` | Program Name | Team/event pattern matching, display |
| `<sub-title>` | Episode Title | League matching (AND logic with genres) |
| `<category>` | Genres | League matching, sport-type filtering |
| `<live/>` flag | IsLive | Primary live-vs-replay detection |

### Required: Enable the `<live/>` Flag

In your Teamarr template under **Other EPG Options → Tags**, make sure **Live** is checked:

```json
"xmltv_flags": {
    "new": true,
    "live": true,
    "date": true
}
```

This is the single most important setting. Without `<live/>`, the plugin cannot reliably
distinguish a real game from a replay, postgame show, or filler.

### Required: Categories with `{sport}`

Under **Other EPG Options → Categories**, include `{sport}` so the EPG gets sport-specific
genre tags. The plugin uses these to prevent cross-sport matching (e.g., "NCAA Basketball"
subscription won't match an NCAA Baseball game).

```json
"xmltv_categories": [
    "Sports",
    "{sport}",
    "Sports Event"
]
```

This produces XMLTV like:

```xml
<category>Sports</category>
<category>Basketball</category>
<category>Sports Event</category>
```

Set **Apply Categories To** to at least **Events** (applying to filler too is fine but not required).

### Recommended: Title and Subtitle Templates

The **title** should identify the league/sport. The **subtitle** should contain the matchup
with team names. This gives the plugin two separate fields to match against.

**Team template:**

| Field | Recommended Template | Example Output |
|-------|---------------------|----------------|
| Title | `{gracenote_category}` | `"NBA Basketball"`, `"NFL Football"` |
| Subtitle | `{away_team} at {home_team}` | `"Chicago Bulls at Boston Celtics"` |

**Event template:**

| Field | Recommended Template | Example Output |
|-------|---------------------|----------------|
| Title | `{gracenote_category}` | `"NHL Hockey"`, `"Women's College Basketball"` |
| Subtitle | `{away_team} at {home_team}` | `"Montreal Canadiens at Toronto Maple Leafs"` |

**Alternative title formats** (all work — pick what suits your guide):

| Template | Example Output | Notes |
|----------|---------------|-------|
| `{gracenote_category}` | `"NBA Basketball"` | Standard, clean league name |
| `{league}: {away_team} at {home_team}` | `"NBA: Bulls at Celtics"` | League + matchup in one field |
| `{sport}: {league}` | `"Basketball: NBA"` | Sport-first format |
| `LIVE: {gracenote_category}` | `"LIVE: NFL Football"` | Explicit live marker (redundant with `<live/>` tag but harmless) |

### Recommended: Event Channel Name

For event group channels (created dynamically in Dispatcharr), include the league
so the channel name is descriptive:

| Field | Recommended Template | Example Output |
|-------|---------------------|----------------|
| Channel Name | `{league}: {away_team} at {home_team}` | `"NBA: Bulls at Celtics"` |

### How the Plugin Matches Each Subscription Type

Understanding which EPG fields matter for each subscription type helps you
configure templates that produce good matches:

**Team subscriptions** (e.g., "Boston Celtics"):
- Searches the program **title** and **subtitle** for team name or aliases
- Example match: title `"NBA Basketball"` + subtitle `"Chicago Bulls at Boston Celtics"` → matched

**League subscriptions** (e.g., "NCAA Basketball"):
- Uses **AND logic**: every significant word in the subscription pattern must appear in
  the combined **genres** + **subtitle** text
- `"NCAA Basketball"` requires both "NCAA" and "Basketball" to appear — this prevents
  an NCAA Basketball subscription from matching NCAA Baseball or NCAA Hockey
- Example match: genres `["Sports", "Basketball", "Sports Event"]` + subtitle
  `"Duke at North Carolina"` → "Basketball" found in genres, "NCAA" found if
  `{gracenote_category}` includes it in the subtitle or title

**Event subscriptions** (e.g., "UFC", "Formula 1"):
- Searches the program **title** for the event name
- Example match: title `"UFC 325: Main Card"` → matched

### Common Teamarr Template Variables

Teamarr provides 187+ variables. Here are the ones most relevant to EPG matching:

| Variable | Description | Example |
|----------|-------------|---------|
| `{away_team}` | Away team full name | `"Chicago Bulls"` |
| `{home_team}` | Home team full name | `"Boston Celtics"` |
| `{away_team_pascal}` | Away team (PascalCase, no spaces) | `"ChicagoBulls"` |
| `{home_team_pascal}` | Home team (PascalCase, no spaces) | `"BostonCeltics"` |
| `{team_name}` | The channel's team name | `"Boston Celtics"` |
| `{opponent}` | The opposing team | `"Chicago Bulls"` |
| `{league}` | League abbreviation | `"NBA"`, `"NFL"`, `"NHL"` |
| `{league_id}` | League identifier | `"nba"`, `"nfl"`, `"nhl"` |
| `{sport}` | Sport name | `"Basketball"`, `"Football"`, `"Hockey"` |
| `{gracenote_category}` | Standard Gracenote title | `"NBA Basketball"`, `"NFL Football"` |
| `{game_time}` | Game start time (local) | `"7:30 PM"` |
| `{game_date}` | Game date | `"Monday, February 10"` |
| `{venue}` | Venue name | `"TD Garden"` |
| `{venue_city}` | Venue city | `"Boston"` |
| `{venue_state}` | Venue state | `"Massachusetts"` |
| `{vs_at}` | "vs" (home) or "at" (away) | `"vs"` |
| `{today_tonight}` | Time-appropriate word | `"tonight"` |
| `{away_team_record}` | Away team record | `"(30-15)"` |
| `{home_team_record}` | Home team record | `"(35-10)"` |
| `{result_text}` | Win/loss text | `"defeated"`, `"lost to"` |
| `{final_score}` | Final score | `"118-105"` |
| `{overtime_text}` | Overtime indicator | `"in overtime"` or `""` |

Use `.next` and `.last` suffixes in **team templates** to reference upcoming or previous games
(e.g., `{opponent.next}`, `{game_time.next}`, `{final_score.last}`). Event templates don't
use these suffixes.

> **Full variable list:** See the Teamarr template editor — it shows all available
> variables with live preview as you type. For the complete reference, see
> [Teamarr documentation](https://github.com/Pharaoh-Labs/teamarr).

### Quick-Start: Copy These Settings

If you just want it to work, apply these to both your Team and Event templates:

**Title:** `{gracenote_category}`

**Subtitle:** `{away_team} at {home_team}`

**Categories:**
```
Sports
{sport}
Sports Event
```

**Tags:** Enable `live`, `new`, and `date`

**Event Channel Name:** `{league}: {away_team} at {home_team}`

That's it. The plugin will handle the rest.

## LIVE Detection

The plugin's core filtering relies on the `<live>` EPG tag that Teamarr sets on live game broadcasts.

### Will Record
- Programs with `<live>` EPG tag **(set by Teamarr — this is the primary signal)**
- Titles containing "LIVE:" or "(Live)" as a fallback
- Programs with sport-specific genres AND a matchup pattern (vs/@/at) in the title

### Will Skip
- Programs without `<live>` and no explicit LIVE keyword (replays, encores, classics)
- Pregame/postgame filler (no `<live>` tag)
- Highlights, recaps, countdowns
- Old events with years: "(2008)", "(2016)"

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
# Requires .NET 9.0 SDK
cd plugin
bash build.sh
```

Output in `plugin/dist/`: DLL, meta.json, and icon — ready to copy into your Jellyfin plugins directory.

## Requirements

- Jellyfin 10.11+
- Live TV configured with Teamarr-enriched EPG data
- [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) for IPTV channel management
- [Teamarr](https://github.com/Pharaoh-Labs/teamarr) for sports event matching and `<live>` tagging
- [Comskip](https://github.com/whizzard80/Comskip) *(optional)* — sports-tuned commercial detection; [sports-dvr config](https://github.com/whizzard80/Comskip/tree/sports-detection/sports-dvr) for Jellyfin integration

## License

MIT License - see [LICENSE](LICENSE)
