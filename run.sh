#!/usr/bin/env bash
# Build the mod and start the Windows game client with it loaded from WSL.
set -euo pipefail
cd "$(dirname "$0")"
: "${VINTAGE_STORY:?set VINTAGE_STORY to the game folder}"

dotnet build FarmingRevamp
"$VINTAGE_STORY/Vintagestory.exe" --tracelog \
  --addModPath "$(wslpath -w FarmingRevamp/bin/Debug/Mods)" \
  --addOrigin "$(wslpath -w FarmingRevamp/assets)" "$@"
