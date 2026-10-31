# 게임 데이터 저장소 연동

마비노기 모바일 클라이언트에서 꺼낸 데이터를 올려 두는 private 저장소
[PieceOfPaper/MabinogiMobileUnpack](https://github.com/PieceOfPaper/MabinogiMobileUnpack)를 몰리가 읽는 방법이다.

저장소가 수 GB라 clone하지 않는다. GitHub REST API로 **브랜치 최신 커밋 SHA → `version.json` → 필요한 파일**만 받는다.

## 1. 접근 방식

| 선택지 | 판단 |
| --- | --- |
| **Fine-grained personal access token (채택)** | 저장소 하나·`Contents: Read-only`로 범위를 좁힐 수 있고, HTTP 요청에 헤더만 붙이면 되어 파일 단위로 받기 쉽다. 만료 전에 교체해야 한다. |
| Deploy key(SSH) | 만료가 없지만 git 프로토콜만 쓸 수 있어 파일 몇 개만 받으려면 서버에 git 작업 사본을 둬야 한다. |
| GitHub App | 토큰 자동 갱신이 되지만 앱 생성·설치·JWT 서명이 필요해 지금 규모에 비해 과하다. |

## 2. 토큰 발급

### 사용처별로 따로 발급한다

토큰은 기능이 아니라 **실행 환경(기기)마다** 하나씩 발급한다. 봇 안의 기능은 모두 같은 `UnpackRepoClient`를 쓰므로 환경당 하나면 된다.

| 토큰 이름 | 넣는 곳 | 용도 |
| --- | --- | --- |
| `molly-unpack-lightsail` | 서버 `/etc/molly/molly.env`의 `Unpack__Token` | 운영 |
| `molly-unpack-local` | 로컬 user-secrets `Unpack:Token` | 개발·`--unpack-live` 확인 |

- 한쪽이 유출되거나 만료되어도 그 토큰만 폐기·교체하면 되고 다른 환경은 계속 동작한다.
- GitHub 토큰 목록에 토큰별 마지막 사용 시각이 보여 어디서 쓰는지 구분된다.
- GitHub Actions(CI)는 이 저장소를 읽지 않으므로 CI용 토큰은 만들지 않는다. 서버·PC가 늘면 그때 하나씩 추가한다.

### 발급 절차

GitHub → Settings → Developer settings → Personal access tokens → **Fine-grained tokens** → Generate new token

| 항목 | 값 |
| --- | --- |
| Token name | 위 표의 이름처럼 사용처가 보이는 이름 |
| Expiration | 만료일 지정(예: 1년). 만료일을 일정에 기록해 두고 미리 교체한다. |
| Resource owner | `PieceOfPaper` |
| Repository access | **Only select repositories** → `MabinogiMobileUnpack` |
| Repository permissions | **Contents: Read-only** (Metadata: Read-only는 자동 포함). 나머지는 모두 No access |
| Account permissions | 모두 No access |

Generate token을 누르면 토큰 값(`github_pat_...`)은 **이 화면에서 한 번만** 보인다. 바로 3절의 위치에 넣고, 메모장·채팅·Issue에 붙여 두지 않는다. 잃어버리면 새로 발급한다.

### 교체와 폐기

1. 같은 설정으로 새 토큰을 발급한다(이름 뒤에 연도 등을 붙여 구분해도 된다).
2. 3절 위치의 값을 새 토큰으로 바꾸고, 서버는 `sudo systemctl restart molly` 후 4절 로그로 접근을 확인한다.
3. 확인되면 GitHub 토큰 목록에서 이전 토큰을 Delete한다.

유출이 의심되면 순서를 바꿔 이전 토큰부터 즉시 Delete한다. 그동안 봇은 401로 저장소 호출만 멈추고 나머지 기능은 동작한다.

## 3. 설정

Discord 토큰과 같은 방식이다. 토큰 값은 파일·로그·커밋·Issue에 남기지 않는다.

| 설정 | 설명 |
| --- | --- |
| `Unpack:Token` / `Unpack__Token` | 토큰. 없으면 저장소를 읽지 않고 봇은 정상 동작한다. |
| `Unpack:Enabled` / `Unpack__Enabled` | 선택, 기본 `true`. `false`면 토큰이 있어도 호출하지 않는다. |
| `Unpack:Repository` / `Unpack__Repository` | 선택, 기본 `PieceOfPaper/MabinogiMobileUnpack`. |
| `Unpack:Branch` / `Unpack__Branch` | 선택, 기본 `main`. |

```bash
# 로컬 개발
dotnet user-secrets set "Unpack:Token" "github_pat_..."
```

```ini
# 서버(/etc/molly/molly.env)
Unpack__Token=github_pat_...
```

서버는 `sudo nano /etc/molly/molly.env`로 한 줄을 추가하고 `sudo systemctl restart molly`로 다시 시작한다. 자동 배포는 이 파일을 덮어쓰지 않는다.

## 4. 접근 확인

로컬(요청 2회, Discord 미사용):

```bash
dotnet run --project tests/Molly.DataTests/Molly.DataTests.csproj --configuration Release -- --unpack-live
```

서버: 봇 시작 로그에 결과가 남는다.

```bash
sudo journalctl -u molly -n 200 --no-pager | grep 게임데이터
# [게임데이터] 토큰 설정됨, PieceOfPaper/MabinogiMobileUnpack@main
# [게임데이터] 저장소 접근 확인: 커밋 9b9fe9a, 앱 3.4.40011, 패치 R840cbf8_A4525d21, 생성 2026-10-31 16:03
```

봇을 재시작하지 않고 서버에서 토큰만 확인하려면(토큰이 셸 기록에 남지 않게 env 파일에서 읽는다):

```bash
sudo bash -c 'set -a; . /etc/molly/molly.env; curl -fsS -H "Authorization: Bearer $Unpack__Token" \
  -H "Accept: application/vnd.github.sha" https://api.github.com/repos/PieceOfPaper/MabinogiMobileUnpack/commits/main; echo'
```

| 로그 상태 | 원인 |
| --- | --- |
| `NotFound` (404) | 토큰의 Repository access에 저장소가 빠졌거나 Resource owner가 다름. GitHub은 권한 없는 private 저장소를 404로 숨긴다. |
| `Unauthorized` (401·403) | 토큰 만료·폐기, 또는 Contents 권한 없음. 재시작 전까지 호출하지 않는다. |
| `RateLimited` | 시간당 5,000회 한도 초과. reset 시각까지 쉰다. |

## 5. 코드 구조 (`Unpack/`)

| 파일 | 역할 |
| --- | --- |
| `UnpackRepoOptions.cs` | 설정 읽기와 검증 |
| `UnpackRepoClient.cs` | 인증 헤더, 최신 커밋 SHA·파일 원본·`version.json` 조회, 401/403/404/한도 처리 |

- 한 번의 갱신 안에서는 `GetHeadCommitAsync`로 받은 SHA를 모든 파일 조회의 `gitRef`로 넘긴다. 푸시 도중 다른 커밋의 파일이 섞이지 않는다.
- 파일 원본은 raw 응답(`application/vnd.github.raw+json`)으로 받는다. 파일당 100MB까지 가능하다.
- 요청 수: 확인 1회당 커밋 SHA 1회(+ 바뀌었을 때 `version.json` 1회와 필요한 파일 수). 길드 수와 무관하다. 인증 요청 한도는 시간당 5,000회다.

주기적 갱신·데이터 교체 범위는 정해지면 이 문서에 추가한다.
