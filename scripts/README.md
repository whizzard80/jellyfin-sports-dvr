# Scripts

## clear-jellyfin-guide-cache-linux.sh (native Linux, non-Docker)

Same as below but for **native Linux only**: no Docker paths. Auto-detects cache dir (tries `/var/lib/jellyfin/cache`, `/var/cache/jellyfin`, `~/.cache/jellyfin`). Pass a path to override.

```bash
./clear-jellyfin-guide-cache-linux.sh
# or
./clear-jellyfin-guide-cache-linux.sh /var/lib/jellyfin/cache
```

**Cron (daily 5 AM):**
```bash
0 5 * * * /path/to/scripts/clear-jellyfin-guide-cache-linux.sh
```

## clear-jellyfin-guide-cache.sh

Clears Jellyfin's Live TV EPG cache so the guide actually updates (fixes stale "Game Complete" entries). See [Jellyfin issue #6103](https://github.com/jellyfin/jellyfin/issues/6103).

### Usage

```bash
# Default cache path: ~/.cache/jellyfin
./clear-jellyfin-guide-cache.sh

# Custom path (e.g. Docker data volume)
./clear-jellyfin-guide-cache.sh /path/to/jellyfin/cache
```

### Optional: trigger guide refresh after clearing

Set these before running so the script also starts Jellyfin's "Refresh Guide Data" task:

```bash
export JELLYFIN_URL="http://localhost:8096"
export JELLYFIN_API_KEY="your_api_key"   # Dashboard → API Keys
export JELLYFIN_REFRESH_TASK_ID="..."   # See below
./clear-jellyfin-guide-cache.sh /path/to/cache
```

**Finding the Refresh Guide Data task ID:**  
Dashboard → Scheduled Tasks → run "Refresh Guide Data" and check the browser Network tab for a request to `ScheduledTasks/Running/{id}`, or call `GET /ScheduledTasks` with your API key and look for the task named like "Refresh Guide Data".

### Cron (daily cache clear + optional refresh)

Run once per day so the guide can load fresh EPG from Dispatcharr. Example: clear cache at 5:00 AM, then trigger refresh (or rely on Jellyfin's scheduled refresh).

**Linux (native Jellyfin):**

```bash
# Install script (e.g. copy to /usr/local/bin or keep in repo)
chmod +x /path/to/jellyfin-sports-dvr/scripts/clear-jellyfin-guide-cache.sh

# Crontab - edit with: crontab -e
# Clear cache daily at 5 AM (default cache path)
0 5 * * * /path/to/jellyfin-sports-dvr/scripts/clear-jellyfin-guide-cache.sh

# With custom cache path (e.g. /var/lib/jellyfin/cache)
0 5 * * * /path/to/scripts/clear-jellyfin-guide-cache.sh /var/lib/jellyfin/cache

# With auto-trigger refresh (set env in crontab or a wrapper script)
0 5 * * * JELLYFIN_URL=http://localhost:8096 JELLYFIN_API_KEY=xxx JELLYFIN_REFRESH_TASK_ID=yyy /path/to/scripts/clear-jellyfin-guide-cache.sh /var/lib/jellyfin/cache
```

**Docker:** run the script on the host so it can delete files inside the volume, e.g.:

```bash
# Host cron; adjust path to your Jellyfin data volume
0 5 * * * /path/to/scripts/clear-jellyfin-guide-cache.sh /path/to/docker/jellyfin/config/cache
```

Then either trigger "Refresh Guide Data" via API from the host (with `JELLYFIN_URL=http://host:8096`) or let Jellyfin's own scheduled task run after the cache is cleared.
