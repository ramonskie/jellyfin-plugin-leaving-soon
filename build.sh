#!/bin/bash
set -e

# Build script for Jellyfin.Plugin.LeavingSoon
# This script builds the plugin DLL and prepares it for release

PROJECT_DIR="Jellyfin.Plugin.LeavingSoon"
VERSION="${1:-1.0.0}"
OUTPUT_DIR="build"

echo "Building Jellyfin.Plugin.LeavingSoon v${VERSION}"
echo "================================================"

# Clean previous builds
echo "Cleaning previous builds..."
rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}"

# Restore dependencies
echo "Restoring dependencies..."
cd "${PROJECT_DIR}"
dotnet restore

# Build the plugin
echo "Building plugin..."
dotnet build --configuration Release --output "../${OUTPUT_DIR}"

# Create a zip file for the release
echo "Creating release package..."
cd "../${OUTPUT_DIR}"
zip -r "../Jellyfin.Plugin.LeavingSoon-${VERSION}.zip" *.dll

# Generate checksum
echo "Generating checksum..."
cd ..
md5sum "Jellyfin.Plugin.LeavingSoon-${VERSION}.zip" > "Jellyfin.Plugin.LeavingSoon-${VERSION}.md5"

echo ""
echo "Build complete!"
echo "================================================"
echo "DLL location: ${OUTPUT_DIR}/"
echo "Release package: Jellyfin.Plugin.LeavingSoon-${VERSION}.zip"
echo "Checksum: $(cat Jellyfin.Plugin.LeavingSoon-${VERSION}.md5)"
echo ""
echo "Next steps:"
echo "1. Upload the ZIP file to GitHub releases"
echo "2. Update manifest.json with the release URL and checksum"
echo "3. Commit and push manifest.json to GitHub"
