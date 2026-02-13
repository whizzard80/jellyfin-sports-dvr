#!/usr/bin/env bash
#
# Clear Jellyfin's Live TV guide cache so "Refresh Guide Data" actually loads new EPG.
# Use with cron (e.g. daily) so the guide doesn't stay stuck on "Game Complete".
#
# See: https://github.com/jellyfin/jellyfin/issues/6103
#
# Usage:
#   ./clear-jellyfin-guide-cache.sh [CACHE_DIR]
#   CACHE_DIR  Optional. Default: $JELLYFIN_CACHE_DIR or ~/.cache/jellyfin
#
# Optional (to trigger guide refresh after clearing cache):
#   JELLYFIN_URL=http://localhost:8096
#   JELLYFIN_API_KEY=your_api_key
#   JELLYFIN_REFRESH_TASK_ID=task_id_from_dashboard
#   export JELLYFIN_URL JELLYFIN_API_KEY JELLYFIN_REFRESH_TASK_ID
#   ./clear-jellyfin-guide-cache.sh
#
# Cron example (daily at 5:00 AM, then Jellyfin's own refresh can run after):
#   0 5 * * * /path/to/scripts/clear-jellyfin-guide-cache.sh /var/lib/jellyfin/cache
#

set -e

CACHE_DIR="${1:-${JELLYFIN_CACHE_DIR:-$HOME/.cache/jellyfin}}"
XMLTV_DIR="$CACHE_DIR/xmltv"
CHANNELS_GLOB="$CACHE_DIR/*_channels"

echo "Jellyfin guide cache dir: $CACHE_DIR"

if [[ ! -d "$CACHE_DIR" ]]; then
  echo "Error: Cache directory does not exist: $CACHE_DIR"
  echo "Set JELLYFIN_CACHE_DIR or pass the path as the first argument (e.g. /config/cache for Docker)."
  exit 1
fi

cleared=0

if [[ -d "$XMLTV_DIR" ]]; then
  rm -rf "${XMLTV_DIR:?}"/*
  echo "Cleared $XMLTV_DIR"
  cleared=1
fi

for d in $CHANNELS_GLOB; do
  if [[ -e "$d" ]]; then
    rm -rf "$d"
    echo "Removed $d"
    cleared=1
  fi
done

if [[ $cleared -eq 0 ]]; then
  echo "Nothing to clear (no xmltv or *_channels under $CACHE_DIR)."
fi

# Optionally trigger Jellyfin's "Refresh Guide Data" task so the guide repopulates immediately.
if [[ -n "${JELLYFIN_URL:-}" && -n "${JELLYFIN_API_KEY:-}" && -n "${JELLYFIN_REFRESH_TASK_ID:-}" ]]; then
  echo "Triggering Jellyfin guide refresh task..."
  if curl -sf -X POST \
    -H "Authorization: MediaBrowser Token=$JELLYFIN_API_KEY" \
    -H "Content-Type: application/json" \
    "${JELLYFIN_URL%/}/ScheduledTasks/Running/${JELLYFIN_REFRESH_TASK_ID}"; then
    echo "Refresh task started."
  else
    echo "Warning: Failed to trigger refresh task. Run 'Refresh Guide Data' from Dashboard → Live TV."
  fi
else
  echo "To auto-trigger guide refresh, set JELLYFIN_URL, JELLYFIN_API_KEY, and JELLYFIN_REFRESH_TASK_ID."
  echo "Otherwise run 'Refresh Guide Data' from Dashboard → Live TV after this script."
fi
