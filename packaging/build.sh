#!/usr/bin/env bash
# Builds, tests and publishes NitroPower into artifacts/ (run as your user).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# No lingering MSBuild/compiler servers after the build: leftover worker nodes
# have crashed later builds ("Internal CLR error") and keep pipes open
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false
NO_SERVERS=(--disable-build-servers -nodeReuse:false)

dotnet test "${NO_SERVERS[@]}"
rm -rf artifacts/nitropowerd artifacts/cli artifacts/gui
dotnet publish src/NitroPower.Daemon -c Release -o artifacts/nitropowerd "${NO_SERVERS[@]}"
dotnet publish src/NitroPower.Cli -c Release -o artifacts/cli "${NO_SERVERS[@]}"
# linux-x64 only: the GUI carries native Skia/HarfBuzz libraries per platform
dotnet publish src/NitroPower.Gui -c Release -r linux-x64 --self-contained false -o artifacts/gui "${NO_SERVERS[@]}"
echo "Built into $ROOT/artifacts. Install with: sudo $ROOT/packaging/install-dev.sh"
