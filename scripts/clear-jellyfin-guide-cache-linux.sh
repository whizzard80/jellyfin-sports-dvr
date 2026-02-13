#!/usr/bin/env bash
#
# Clear Jellyfin Live TV guide cache (native Linux, non-Docker).
# Use with cron so the guide doesn't stay stuck on "Game Complete".
#
# See: https://github.com/jellyfin/jellyfin/issues/6103
#
# Usage:
#   ./clear-jellyfin-guide-cache-linux.sh [CACHE_DIR]
#
#   With no argument, uses first existing path:
#     - $JELLYFIN_CACHE_DIR (if set)
#     - /var/lib/jellyfin/cache   (system install)
#     - /var/cache/jellyfin       (alternate system)
#     - ~/.cache/jellyfin         (user install)
#
# Optional (trigger guide refresh after purge):
#   export JELLYFIN_URL=http://localhost:8096
#   export JELLYFIN_API_KEY=your_api_key
#   export JELLYFIN_REFRESH_TASK_ID=task_id
#   ./clear-jellyfin-guide-cache-linux.sh
#
# Cron (run as root or jellyfin user; daily at 5 AM):
#   0 5 * * * /path/to/scripts/clear-jellyfin-guide-cache-linux.sh
#

set -e

find_cache_dir() {
  if [[ -n "${JELLYFIN_CACHE_DIR:-}" && -d "$JELLYFIN_CACHE_DIR" ]]; then
    echo "$JELLYFIN_CACHE_DIR"
    return
  fi
  for dir in /var/lib/jellyfin/cache /var/cache/jellyfin "$HOME/.cache/jellyfin"; do
    if [[ -d "$dir" ]]; then
      echo "$dir"
      return
    fi
  done
  return 1
}

CACHE_DIR="${1:-}"
if [[ -z "$CACHE_DIR" ]]; then
  if ! CACHE_DIR=$(find_cache_dir); then
    echo "Error: No Jellyfin cache directory found. Set JELLYFIN_CACHE_DIR or pass the path:"
    echo "  ./clear-jellyfin-guide-cache-linux.sh /var/lib/jellyfin/cache"
    exit 1
  fi
fi

if [[ ! -d "$CACHE_DIR" ]]; then
  echo "Error: Cache directory does not exist: $CACHE_DIR"
  exit 1
fi

XMLTV_DIR="$CACHE_DIR/xmltv"
CHANNELS_GLOB="$CACHE_DIR/*_channels"

echo "Jellyfin guide cache: $CACHE_DIR"

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

if [[ -n "${JELLYFIN_URL:-}" && -n "${JELLYFIN_API_KEY:-}" && -n "${JELLYFIN_REFRESH_TASK_ID:-}" ]]; then
  echo "Triggering Jellyfin guide refresh..."
  if curl -sf -X POST \
    -H "Authorization: MediaBrowser Token=$JELLYFIN_API_KEY" \
    -H "Content-Type: application/json" \
    "${JELLYFIN_URL%/}/ScheduledTasks/Running/${JELLYFIN_REFRESH_TASK_ID}"; then
    echo "Refresh task started."
  else
    echo "Warning: Could not trigger refresh. Run 'Refresh Guide Data' in Dashboard → Live TV."
  fi
else
  echo "Run 'Refresh Guide Data' in Dashboard → Live TV to repopulate the guide."
fi
