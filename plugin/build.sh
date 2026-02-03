#!/bin/bash
# Build script for Jellyfin Sports DVR Plugin

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/Jellyfin.Plugin.SportsDVR"
OUTPUT_DIR="$SCRIPT_DIR/dist"

# Ensure .NET is available
if ! command -v dotnet &> /dev/null; then
    echo "Installing .NET SDK..."
    wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --channel 8.0 --install-dir ~/.dotnet
    export PATH="$HOME/.dotnet:$PATH"
fi

cd "$PROJECT_DIR"

echo "Building plugin..."
dotnet build -c Release

# Create output directory
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Copy plugin files
echo "Packaging plugin..."
cp bin/Release/net8.0/Jellyfin.Plugin.SportsDVR.dll "$OUTPUT_DIR/"

# Create meta.json for the plugin
cat > "$OUTPUT_DIR/meta.json" << 'EOF'
{
    "guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "name": "Sports DVR",
    "description": "DVR recording and time-shifting for sports with a dedicated home section",
    "overview": "Record sports games, watch live games from the beginning, and manage recordings in a dedicated Sports for You section.",
    "owner": "whizzard80",
    "category": "Live TV",
    "versions": [
        {
            "version": "1.0.0.0",
            "changelog": "Initial release",
            "targetAbi": "10.9.0.0",
            "sourceUrl": "https://github.com/whizzard80/jellyfin-sports-dvr/releases",
            "timestamp": "2026-02-02T00:00:00Z"
        }
    ]
}
EOF

echo ""
echo "Build complete!"
echo "Plugin files are in: $OUTPUT_DIR"
echo ""
echo "To install:"
echo "1. Copy the contents of $OUTPUT_DIR to your Jellyfin plugins directory:"
echo "   mkdir -p /var/lib/jellyfin/plugins/SportsDVR"
echo "   cp $OUTPUT_DIR/* /var/lib/jellyfin/plugins/SportsDVR/"
echo "2. Restart Jellyfin"
echo "3. Configure the plugin in Dashboard → Plugins → Sports DVR"
