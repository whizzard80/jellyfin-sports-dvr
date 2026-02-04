# Jellyfin Automatic Sports DVR
## Schedules teams, leagues, or events from your EPG data


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
| `/SportsDVR/Library/CheckFile/{itemId}` | GET | Check if file is safe to move (for post-processing scripts) |

## Post-Processing Script Integration

If you use a post-processing script with Jellyfin (for transcoding, commercial skipping, etc.), you can integrate it with Sports DVR to prevent moving files that are in use.

### Option 1: Use Plugin API (Recommended)

The plugin provides an API endpoint to check if a file is safe to move:

```bash
#!/bin/bash
# Example: Check if file is safe before moving

RECORDING_PATH="$1"
JELLYFIN_URL="http://localhost:8096"
API_KEY="your-jellyfin-api-key"

# Extract item ID from recording (you'll need to parse this from Jellyfin metadata)
ITEM_ID="..." # Get from Jellyfin recording info

# Check if plugin says file is safe to move
RESPONSE=$(curl -s -H "X-Emby-Token: $API_KEY" \
    "$JELLYFIN_URL/SportsDVR/Library/CheckFile/$ITEM_ID")

if echo "$RESPONSE" | grep -q '"isSafeToMove":true'; then
    # File is safe, proceed with post-processing
    # ... your transcoding/moving logic here ...
else
    echo "File is in use or too recent, skipping post-processing..."
    exit 0
fi
```

### Option 2: Check File Locks Directly

Alternatively, check if the file is locked before moving:

```bash
#!/bin/bash
# Check if file is in use before moving

RECORDING_PATH="$1"

# Method 1: Use lsof
if lsof "$RECORDING_PATH" >/dev/null 2>&1; then
    echo "File is in use, skipping..."
    exit 0
fi

# Method 2: Use flock
(
    flock -n 9 || exit 1
    # Move/transcode file here
) 9>"$RECORDING_PATH.lock"
```

**Note**: Post-processing script integration is optional. If you don't use post-processing scripts, the plugin's organization feature will handle everything automatically with built-in safeguards (1.5 hour delay, playback checks, file lock detection).

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
