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
dotnet run --project tests/Molly.DataTests/Molly.DataTests.csproj --configuration Release
dotnet publish DiscordBot_Molly.csproj --configuration Release --no-build --no-restore --output artifacts/publish
for asset in hollymolly.png shop_table.csv shop_exchange_table.csv shop_share_table.csv; do
  test -s "artifacts/publish/assets/$asset" || { echo "Missing publish asset: $asset" >&2; exit 1; }
done
class_icon_count=$(find artifacts/publish/assets/class_icons -name '*.png' -size +0 | wc -l | tr -d ' ')
[[ "$class_icon_count" -eq 21 ]] || { echo "Missing publish class icons: $class_icon_count/21" >&2; exit 1; }
test -s artifacts/publish/DiscordBot_Molly.dll
test -s artifacts/publish/playwright.ps1
echo "Build, offline data tests, publish and required assets verified."
