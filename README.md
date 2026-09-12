# DiscordBot_Molly
마비노기 모바일용 디스코드 봇 **Molly**.  
이벤트/랭킹 크롤링, 간단 유틸 커맨드를 제공합니다.

## 핵심 기능
- 진행 중 이벤트 조회(마감일/상시 구분)
- 이벤트 마감 알림 등록(지정 시간 전)
- 전투력/매력/생활력/종합 랭킹 조회
- 간단 유틸(핑/시간/이미지/개발자)

## 슬래시 커맨드
| 명령어 | 설명 | 옵션 |
| --- | --- | --- |
| `/핑` | 지연 확인 | 없음 |
| `/시간` | 현재 KST 시간 출력 | 없음 |
| `/홀리몰리` | 이미지 출력 | 없음 |
| `/개발자` | 제작자 출력 | 없음 |
| `/진행중인이벤트` | 진행 중 이벤트 목록 | `마감미정`(bool, 기본: false) |
| `/이벤트마감알림등록` | 마감 알림 등록 | `시간`(1~240, 기본 24시간) |
| `/이벤트마감알림해제` | 마감 알림 해제 | 없음 |
| `/이벤트마감알림확인` | 마감 알림 상태 확인 | 없음 |
| `/이벤트마감알림테스트` | 마감 알림 테스트 | 없음 |
| `/전투력랭킹` | 전투력 랭킹 조회 | `캐릭터이름`(필수), `서버`(기본: 칼릭스), `클래스이름` |
| `/매력랭킹` | 매력 랭킹 조회 | `캐릭터이름`(필수), `서버`(기본: 칼릭스), `클래스이름` |
| `/생활력랭킹` | 생활력 랭킹 조회 | `캐릭터이름`(필수), `서버`(기본: 칼릭스), `클래스이름` |
| `/종합랭킹` | 종합 랭킹 조회 | `캐릭터이름`(필수), `서버`(기본: 칼릭스), `클래스이름` |
| `/상점검색` | 아이템 기준 상점/공유상점/교환상점 검색 | `아이템`(필수) |

## 지원 서버
- 데이안
- 아이라
- 던컨
- 알리사
- 메이븐
- 라사
- 칼릭스

## 요구 사항
- .NET 10 SDK
- Discord Bot 토큰
- Playwright(Chromium) 설치 권장
- Linux/컨테이너 환경: tzdata 설치 권장(타임존 데이터)

## 빠른 시작
1. 패키지 복원
```bash
dotnet restore
```

2. Playwright(Chromium) 설치
```bash
dotnet build
pwsh bin/Debug/net10.0/playwright.ps1 install chromium
```

3. Discord 토큰 등록
```bash
dotnet user-secrets set "Discord:Token" "YOUR_TOKEN"
```

4. (선택) 테스트 길드 ID 등록
```powershell
# Windows PowerShell
setx Discord__GuildId "TEST_GUILD_ID"
```
```bash
# macOS/Linux
export Discord__GuildId="TEST_GUILD_ID"
```

5. 실행
```bash
dotnet run
```

## 설정
- `Discord:Token`  
  `user-secrets`로 설정합니다.
- `Discord:GuildId`  
  테스트 길드 ID(선택). 설정 시 길드에만 슬래시 명령을 등록(즉시 반영).  
  비어 있거나 0이면 글로벌 등록(전파 지연 가능).  
  환경변수로는 `Discord__GuildId` 사용.
- `MOLLY_DATA_DIR`  
  이벤트 마감 알림 설정 저장 위치(기본: 실행 폴더).
- `assets/shop_table.csv`  
  일반 상점 데이터. 실행 시 자동 로드됩니다.
- `assets/shop_exchange_table.csv`  
  교환 상점 데이터. 실행 시 자동 로드됩니다.
- `assets/shop_share_table.csv`  
  공유 상점 데이터. 실행 시 자동 로드됩니다.

## 동작 개요
- 이벤트/랭킹 조회는 Playwright(Headless Chromium)로 페이지를 로드합니다.
- 이벤트 마감 알림은 KST 기준 **09:00 / 21:00**에 갱신됩니다.
- 이벤트 목록은 페이지 해시를 비교하여 변경 시에만 갱신합니다.

## 참고
- `assets/` 폴더의 이미지 파일은 실행 시 출력 디렉터리로 복사됩니다.
- 길드 테스트는 `Discord:GuildId`로 설정하면 슬래시 명령이 즉시 등록됩니다.

## 자동 검증과 Codex 개발 환경

- `main` push, pull request, 수동 실행 시 GitHub Actions CI가 실행됩니다.
- CI는 .NET 10 Release 빌드·publish 및 이미지/CSV/Playwright 설치 스크립트의 배포 포함 여부를 검사합니다.
- Discord 접속·외부 페이지 수집·기능 테스트는 이 검증에 포함되지 않습니다.
- SDK는 `global.json`의 .NET 10 안정 버전을 사용합니다. Discord.Net은 3.20.1로 고정했습니다.

### 로컬 Mac / Codex CLI
.NET 10 SDK를 설치한 뒤 프로젝트를 갱신하고 검증하세요.

```bash
git pull --ff-only
bash scripts/setup.sh
```

스크립트는 .NET 10 SDK가 없으면 `.tools/dotnet`에 설치하고, 빌드 검증까지 수행합니다.
최초 설치·패키지 복원에는 네트워크가 필요합니다. 이후 변경 검증 명령은 다음과 같습니다.

```bash
bash scripts/verify.sh
```

스크립트가 설치한 SDK를 터미널에서 직접 사용하려면:
```bash
export DOTNET_ROOT="$PWD/.tools/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```
시스템에 SDK를 설치했다면 위 환경변수 설정은 필요 없습니다.

### Codex 클라우드 환경
- 이 저장소를 작업 대상으로 연결합니다.
- 환경의 setup 명령: `bash scripts/setup.sh`
- 준비 단계에서 dot.net, Microsoft SDK 다운로드 호스트 및 NuGet 접근이 필요합니다.
- 일반 코드·빌드 작업에는 Discord 토큰과 Chromium이 필요하지 않습니다.
- Codex는 루트 `AGENTS.md`를 개발 지침으로 사용합니다.
- 이 저장소에는 환경 준비 스크립트가 포함되어 있으며, 계정의 Codex 환경 설정 자체는 별도로 지정해야 합니다.

### 실제 실행에 필요한 추가 준비
Playwright 브라우저 설치에는 PowerShell 7의 `pwsh`가 필요합니다.
Release 빌드 후:
```bash
pwsh bin/Release/net10.0/playwright.ps1 install chromium
```
Linux에서 브라우저 시스템 라이브러리가 부족하면 관리자 권한으로
`pwsh bin/Release/net10.0/playwright.ps1 install --with-deps chromium`을 실행하세요.

실제 실행은 기존 빠른 시작의 토큰·테스트 서버 설정을 따르세요.
저장 경로는 예를 들어 `MOLLY_DATA_DIR="$PWD/.molly-data"`로 지정할 수 있습니다.
