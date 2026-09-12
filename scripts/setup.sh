#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -x "$PWD/.tools/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$PWD/.tools/dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | awk '{print $1}' | grep -q '^10\.'; then
  mkdir -p .tools/dotnet
  curl --fail --silent --show-error --location https://dot.net/v1/dotnet-install.sh --output .tools/dotnet-install.sh
  bash .tools/dotnet-install.sh --channel 10.0 --quality GA --install-dir "$PWD/.tools/dotnet" --no-path
  export DOTNET_ROOT="$PWD/.tools/dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi
bash scripts/verify.sh
if [[ "${INSTALL_PLAYWRIGHT:-0}" == "1" ]]; then
  command -v pwsh >/dev/null 2>&1 || { echo "Install PowerShell (pwsh) before requesting Chromium installation." >&2; exit 1; }
  pwsh bin/Release/net10.0/playwright.ps1 install chromium
fi
