# AWS Lightsail 배포 및 운영

이 문서는 Ubuntu 24.04 x86_64 Lightsail 인스턴스에 Molly를 배포한 실제 절차와 문제 해결 기록을 정리한다. 기본 경로는 `/opt/molly`, 서비스 이름은 `molly`다.

## 구성

- 소스: `/opt/molly/src`
- 실행 파일: `/opt/molly/app`
- 직전 배포 백업: `/opt/molly/app-previous`
- Playwright 브라우저: `/opt/molly/playwright`
- 영구 데이터: `/var/lib/molly`
- 비밀 환경변수: `/etc/molly/molly.env`
- systemd: `/etc/systemd/system/molly.service`

Molly는 외부 HTTP 요청을 받지 않으므로 Lightsail 방화벽에는 SSH `22/TCP`만 필요하다. 고정 IP는 필수는 아니지만 GitHub Actions 배포를 위해 권장한다.

## 1. 서버 기본 준비

Ubuntu 24.04 LTS, x86_64 인스턴스를 기준으로 한다.

```bash
uname -m
free -h
lsb_release -a
sudo apt-get update
sudo apt-get upgrade -y
sudo apt-get install -y git curl ca-certificates
```

### 512MB 인스턴스 스왑

512MB 플랜은 실제 RAM이 약 414MiB로 표시되고 Playwright 실행 시 메모리가 부족할 수 있다. 1GiB 스왑을 추가한다.

```bash
sudo fallocate -l 1G /swapfile
sudo chmod 600 /swapfile
sudo mkswap /swapfile
sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
echo 'vm.swappiness=10' | sudo tee /etc/sysctl.d/99-molly-swap.conf
sudo sysctl --system
free -h
```

`/etc/fstab`에 `/swapfile`이 이미 있으면 같은 줄을 다시 추가하지 않는다.

## 2. 소스와 서비스 계정

```bash
sudo useradd --system --home /var/lib/molly --create-home --shell /usr/sbin/nologin molly
sudo mkdir -p /opt/molly/src /opt/molly/app /opt/molly/playwright /var/lib/molly
sudo chown -R ubuntu:ubuntu /opt/molly/src /opt/molly/app
sudo chown -R molly:molly /opt/molly/playwright /var/lib/molly
git clone https://github.com/PieceOfPaper/DiscordBot_Molly.git /opt/molly/src
cd /opt/molly/src
bash scripts/setup.sh
```

`useradd: user 'molly' already exists`는 재설정 과정이라면 무시할 수 있다.

## 3. 최초 수동 publish

```bash
cd /opt/molly/src
.tools/dotnet/dotnet publish DiscordBot_Molly.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --output /opt/molly/app
```

시스템에 .NET 10 SDK가 설치돼 `.tools/dotnet`이 없다면 `dotnet publish`를 사용한다.

### publish 권한 오류

`Access to the path '/opt/molly/app/.playwright' is denied`가 반복되면 `/opt/molly/app`이 root 소유인 것이다.

```bash
sudo chown -R ubuntu:ubuntu /opt/molly/app
```

권한을 고친 뒤 publish를 다시 실행한다. 하나의 권한 문제 때문에 수백 개의 MSB3021/MSB3027 오류가 연쇄적으로 표시될 수 있다.

## 4. Playwright Chromium

브라우저 위치를 서비스와 설치 명령에서 동일하게 고정한다.

```bash
sudo env PLAYWRIGHT_BROWSERS_PATH=/opt/molly/playwright \
  /opt/molly/app/.playwright/node/linux-x64/node \
  /opt/molly/app/.playwright/package/cli.js \
  install --with-deps chromium
sudo chown -R molly:molly /opt/molly/playwright
sudo chmod +x /opt/molly/app/.playwright/node/linux-x64/node
```

설치와 실행 권한을 확인한다.

```bash
find /opt/molly/playwright -maxdepth 3 -type f \( -name chromium -o -name headless_shell \)
sudo -u molly /opt/molly/app/.playwright/node/linux-x64/node --version
```

`Permission denied`와 함께 `.playwright/node/linux-x64/node` 실행이 실패하면 마지막 `chmod +x`가 빠진 것이다.

## 5. 환경변수

```bash
sudo mkdir -p /etc/molly
sudo nano /etc/molly/molly.env
```

```ini
Discord__Token=DISCORD_BOT_TOKEN
Discord__GuildId=TEST_GUILD_ID
MOLLY_DATA_DIR=/var/lib/molly
PLAYWRIGHT_BROWSERS_PATH=/opt/molly/playwright
GoogleSheets__SpreadsheetId=19kRuVIlZ1LEhU5lixEjRqYvZKbdGnXk0tjY6qznSRf0
GoogleSheets__RuneSheetId=0
# 선택: 모비라이프 OpenAPI 서비스용 키 (docs/mobilife-openapi.md)
MobiLife__ApiKey=MOBILIFE_SERVICE_API_KEY
```

```bash
sudo chown root:root /etc/molly/molly.env
sudo chmod 600 /etc/molly/molly.env
```

실제 토큰은 저장소, 로그, GitHub Actions 변수에 넣지 않는다. `/etc/molly/molly.env`는 자동 배포에서 유지된다.

## 6. systemd

`/etc/systemd/system/molly.service`:

```ini
[Unit]
Description=Molly Mabinogi Mobile Discord Bot
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=molly
Group=molly
WorkingDirectory=/opt/molly/app
ExecStart=/opt/molly/app/DiscordBot_Molly
EnvironmentFile=/etc/molly/molly.env
Environment=HOME=/var/lib/molly
Environment=DOTNET_EnableDiagnostics=0
Restart=on-failure
RestartSec=10
TimeoutStopSec=30
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
ReadWritePaths=/var/lib/molly

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable molly
sudo systemctl start molly
sudo systemctl status molly
sudo journalctl -u molly -n 100 --no-pager
```

정상 상태는 `Active: active (running)`이다. SSH 연결을 종료해도 서비스는 계속 실행된다.

## 7. GitHub Actions 자동 배포 준비

CI workflow는 `main` push의 검증이 성공하면 자체 포함 Linux 배포물을 만들고 Lightsail에 전송한다. `LIGHTSAIL_DEPLOY_ENABLED=true`일 때만 배포 job이 실행된다.

### 전용 SSH 키

Mac에서 자동 배포 전용 키를 생성한다. 암호는 비워야 Actions가 비대화식으로 사용할 수 있다.

```bash
ssh-keygen -t ed25519 -f ~/.ssh/molly-github-actions -C molly-github-actions
ssh-copy-id -i ~/.ssh/molly-github-actions.pub molly-lightsail
```

`ssh-copy-id`가 없다면 공개키 한 줄을 서버의 `/home/ubuntu/.ssh/authorized_keys`에 추가한다. 기존 Lightsail 기본 키는 삭제하지 않는다.

서버 호스트 키는 기존에 신뢰한 SSH 연결에서 확인한 뒤 저장한다.

```bash
ssh-keyscan -H LIGHTSAIL_STATIC_IP
```

### GitHub repository variables

`Settings → Secrets and variables → Actions → Variables`:

| 이름 | 값 |
| --- | --- |
| `LIGHTSAIL_DEPLOY_ENABLED` | 처음에는 `false`, 준비 완료 후 `true` |
| `LIGHTSAIL_HOST` | Lightsail 고정 IP |
| `LIGHTSAIL_USER` | `ubuntu` |
| `LIGHTSAIL_PORT` | `22` |

### GitHub repository secrets

`Settings → Secrets and variables → Actions → Secrets`:

| 이름 | 값 |
| --- | --- |
| `LIGHTSAIL_SSH_PRIVATE_KEY` | `~/.ssh/molly-github-actions`의 전체 내용 |
| `LIGHTSAIL_KNOWN_HOSTS` | `ssh-keyscan -H` 결과 전체 줄 |

Discord 토큰은 서버의 `/etc/molly/molly.env`에만 존재하므로 GitHub Secrets에 넣을 필요가 없다.

### 서버 sudo 권한

기본 Ubuntu Lightsail의 `ubuntu` 계정은 비대화식 sudo가 가능하다. 확인한다.

```bash
sudo -n true
```

오류가 없으면 준비된 것이다. 자동 배포는 `/tmp`에 전송된 아카이브를 `scripts/deploy-lightsail.sh`로 설치한다.

## 8. 자동 배포 동작과 복구

`main` push → 기존 CI 검증 → `linux-x64` 자체 포함 publish → SSH 전송 → Chromium 버전 확인/설치 → 서비스 교체 → 상태 확인 순서다.

새 버전이 `active`가 되지 않으면 배포 스크립트가 `/opt/molly/app-previous`를 `/opt/molly/app`으로 되돌리고 기존 버전을 다시 시작한다. 성공한 배포 뒤에도 직전 버전은 남는다.

수동 재배포는 Actions의 `Run workflow`로 실행할 수 있다. 자동 배포를 잠시 끄려면 `LIGHTSAIL_DEPLOY_ENABLED=false`로 변경한다.

## 9. 운영 확인

```bash
sudo systemctl status molly
sudo journalctl -u molly -f
free -h
ps -o pid,ppid,%mem,rss,cmd -u molly --sort=-rss
sudo journalctl -k --no-pager | grep -i -E 'out of memory|oom|killed process'
```

512MB에서는 이벤트용과 랭킹용 Chromium이 동시에 유지되면 스왑을 사용한다. 조회 실패가 반복되거나 OOM이 발생하면 1GB 플랜으로 올리거나 브라우저 동시 실행·유휴 종료 정책을 코드에서 조정한다.
