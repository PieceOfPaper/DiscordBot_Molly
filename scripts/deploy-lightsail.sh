#!/usr/bin/env bash
set -euo pipefail

archive_path="${1:?배포 아카이브 경로가 필요합니다.}"
release_id="${2:?배포 식별자가 필요합니다.}"
install_root="/opt/molly"
app_path="$install_root/app"
previous_path="$install_root/app-previous"
staging_path="$install_root/app-next-$release_id"
browser_path="$install_root/playwright"
data_path="$install_root/data"
service_name="molly"
service_override_dir="/etc/systemd/system/$service_name.service.d"
service_override_path="$service_override_dir/runtime.conf"
xvfb_service_name="molly-xvfb"
xvfb_service_path="/etc/systemd/system/$xvfb_service_name.service"

if [[ ! "$release_id" =~ ^[0-9a-f]{7,40}$ ]]; then
  echo "잘못된 배포 식별자: $release_id" >&2
  exit 1
fi
if [[ ! -s "$archive_path" ]]; then
  echo "배포 아카이브를 찾을 수 없습니다: $archive_path" >&2
  exit 1
fi

rm -rf "$staging_path"
mkdir -p "$staging_path" "$browser_path" "$data_path"
chown -R molly:molly "$browser_path" "$data_path"

if ! command -v Xvfb >/dev/null 2>&1; then
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends xvfb xauth
fi

cat > "$xvfb_service_path" <<'EOF'
[Unit]
Description=Molly 가상 디스플레이
Before=molly.service

[Service]
Type=simple
ExecStart=/usr/bin/Xvfb :99 -screen 0 1280x720x24 -nolisten tcp -ac
Restart=always
RestartSec=2

[Install]
WantedBy=multi-user.target
EOF

mkdir -p "$service_override_dir"
cat > "$service_override_path" <<EOF
[Unit]
Requires=$xvfb_service_name.service
After=$xvfb_service_name.service

[Service]
Environment=MOLLY_DATA_DIR=$data_path
Environment=DISPLAY=:99
EOF
systemctl daemon-reload
systemctl enable --now "$xvfb_service_name"

tar -xzf "$archive_path" -C "$staging_path"

sqlite_library="$(ldconfig -p | awk '/libsqlite3\.so\.0/{print $NF; exit}')"
if [[ -z "$sqlite_library" || ! -f "$sqlite_library" ]]; then
  echo "시스템 SQLite 라이브러리(libsqlite3.so.0)를 찾을 수 없습니다." >&2
  exit 1
fi
ln -sfn "$sqlite_library" "$staging_path/libsqlite3.so"

test -x "$staging_path/DiscordBot_Molly" || chmod +x "$staging_path/DiscordBot_Molly"
test -s "$staging_path/.playwright/package/cli.js"
chmod +x "$staging_path/.playwright/node/linux-x64/node"

PLAYWRIGHT_BROWSERS_PATH="$browser_path" \
  "$staging_path/.playwright/node/linux-x64/node" \
  "$staging_path/.playwright/package/cli.js" install chromium

chown -R root:root "$staging_path"
chown -R molly:molly "$browser_path" "$data_path"

systemctl stop "$service_name"
rm -rf "$previous_path"
if [[ -d "$app_path" ]]; then
  mv "$app_path" "$previous_path"
fi
mv "$staging_path" "$app_path"

if systemctl start "$service_name"; then
  sleep 5
fi
if systemctl is-active --quiet "$service_name"; then
  rm -f "$archive_path"
  echo "Molly 배포 완료: $release_id"
  exit 0
fi

echo "새 버전 시작 실패. 직전 버전으로 복구합니다." >&2
journalctl -u "$service_name" --since "2 minutes ago" --no-pager --lines=100 >&2 || true
systemctl stop "$service_name" || true
failed_path="$install_root/app-failed-$release_id"
rm -rf "$failed_path"
if [[ -d "$app_path" ]]; then
  mv "$app_path" "$failed_path"
fi
if [[ -d "$previous_path" ]]; then
  mv "$previous_path" "$app_path"
  systemctl start "$service_name"
fi
exit 1
