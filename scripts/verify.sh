#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -x "$PWD/.tools/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$PWD/.tools/dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi
dotnet --version
dotnet restore DiscordBot_Molly.csproj
dotnet build DiscordBot_Molly.csproj --configuration Release --no-restore
dotnet publish DiscordBot_Molly.csproj --configuration Release --no-build --no-restore --output artifacts/publish
for asset in hollymolly.png shop_table.csv shop_exchange_table.csv shop_share_table.csv; do
  test -s "artifacts/publish/assets/$asset" || { echo "Missing publish asset: $asset" >&2; exit 1; }
done
test -s artifacts/publish/DiscordBot_Molly.dll
test -s artifacts/publish/playwright.ps1
echo "Build, publish and required assets verified."
