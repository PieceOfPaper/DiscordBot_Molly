# 공식 소식 알림 기획서

> 상태: 구현(운영 활성화 전)  
> 기준일: 2026-10-29  
> 구현: `OfficialNews/`(목록 파서·수집기·저장소·발송 worker·Embed), `Commands/OfficialNewsCommand.cs`(구독 명령)

## 사이트 접근 방식

- 공식 소식 목록은 이벤트 목록처럼 쿠키·브라우저 없이 HTTP GET으로 완성된 HTML을 받는다. 브라우저 보안 검사(Cloudflare challenge)가 걸린 것은 `/Ranking/*`뿐이다.
- 그래서 사이트 접근은 **랭킹(브라우저)** 과 **공지·업데이트·이벤트·에린 노트(HTTP)** 둘로 나눈다. 공식 소식은 이벤트 수집과 같은 `MobiEventHttpSource`를 쓴다.
- 공식 사이트는 해외 IP를 글로벌 페이지로 보낸다(`/News/*` → `302 /en/Main`). 국내 IP에서 수집해야 한다.
- 목록 페이지 안의 `POST /News/{게시판}/GetList`도 있지만 페이지가 발급하는 `mmtoken` 쿠키가 필요해 요청이 두 번이 되고 응답도 HTML 조각이라 쓰지 않는다.

## 기능명

공식 소식 알림 — `/공지알림`, `/업데이트알림`, `/이벤트알림`, `/에린노트알림`

## 분류

편의

## 해결하려는 문제

점검·업데이트·이벤트·개발자노트를 놓치지 않으려면 공식 사이트 네 게시판을 반복해서 확인해야 한다.

## Discord에서 제공하는 이점

게임이나 사이트에 들어가지 않아도 새 글이 올라오면 길드 채널로 원문 링크와 썸네일을 받는다(비접속 알림·자동 관찰).

## 핵심 사용자 흐름

1. 운영자가 받을 채널에서 `/공지알림 작업:등록`(업데이트·이벤트·에린 노트도 각각) 실행
2. 몰리가 10분마다 네 게시판의 목록 첫 페이지를 HTTP로 읽어 새 글을 찾음
3. 새 글을 DB에 저장하면서 그 순간 구독한 서버마다 발송 작업을 만듦
4. 발송 worker가 Discord 연결 상태에서 채널마다 Embed 하나를 보냄
5. 누구나 `/공지알림`(기본 `확인`)으로 등록 채널·마지막 발송·공식 홈페이지 마지막 확인·실패 여부 확인

## 사용하는 기존 데이터

- SQLite 공용 DB(`MollyDataPaths.DatabasePath`)와 저장소 패턴, Discord Embed(마감 알림과 같은 큰 이미지 형식)
- 이벤트 수집의 HTTP 경로(`MobiEventHttpSource`: 브라우저형 User-Agent·`ko-KR`·쿠키 유지·15초 시간 제한)
- Google Sheets·모비라이프 API는 쓰지 않는다.

## 새로 필요한 데이터

| 테이블 | 키 | 내용 |
| --- | --- | --- |
| `official_news_subscriptions` | PK `(guild_id, board)` | channel_id, enabled, created/updated |
| `official_news_events` | PK `event_id` | board, post_id, title, url, category, thumbnail_url, detected_at(발견 시각), received_at |
| `official_news_deliveries` | UNIQUE `(event_id, guild_id)` | 작업 생성 당시 channel_id, status(pending·sending·sent·skipped·failed), attempts, next_attempt_at, lease_until, sent_message_id, last_error_code |
| `official_news_board_states` | PK `board` | floor_post_id(첫 기준의 가장 작은 글 ID), baselined_at, last_checked_at, last_success_at, last_error |
| `official_news_seen_posts` | PK `(board, post_id)` | seen_at(첫 기준 글과 발견한 글) |

`event_id`는 `mabinogi-mobile:{board}:{postId}:created` 형식이다.

첫 시작에 `CREATE TABLE IF NOT EXISTS`로 만든다. 기존 테이블은 바꾸지 않는다. 원문 본문·이미지 파일은 저장하지 않는다.

## 사이트 요청량

- 일반 회차는 게시판마다 목록 첫 페이지 1요청, 10분마다 4요청이다. 하루 약 576요청이며 Discord 서버·구독 수와 무관하다.
- 한 페이지(10개)가 모두 새 글일 때만 다음 페이지를 읽고, 한 번에 최대 5페이지까지 읽는다.
- 진행 중 이벤트 수집(`/News/Events?headlineId=2501`, 이벤트 마감 알림용)은 별도로 그대로 동작한다. 공식 소식은 필터 없는 전체 이벤트 목록을 본다.
- Discord 전송은 글 D건 × 구독 서버 G곳. 구독 확인 명령은 DB만 읽는다.
- 보관량: 글 D건/일, 발송 작업 약 D×G건/일, 본 글 ID 약 D건/일. 자동 삭제는 후속 합의 전까지 구현하지 않는다. 경량화할 때도 중복 방지를 위해 event_id와 본 글 ID는 남긴다.

## 수집 규칙

- 대상: `https://mabinogimobile.nexon.com/News/{Notice·Update·Events·Devnote}` 첫 페이지. 다음 페이지는 사이트 링크와 같은 `?pageno=N&blockStartNo=…&blockStartKey=…`.
- 시작 직후 한 번 확인하고 이후 10분마다 확인한다(`OfficialNewsCollector.Interval`). 게시판끼리 독립이라 한 게시판 실패가 다른 게시판을 막지 않는다.
- **첫 확인**: 상태가 없는 게시판은 지금 목록의 글을 본 것으로만 기록하고 보내지 않는다. 목록에서 가장 작은 글 ID를 기준(floor)으로 남긴다.
- **새 글 판정**: 목록의 글 중 기준보다 ID가 크고 아직 본 적 없는 글. 목록은 ID 순이 아닐 수 있어(이벤트 게시판) 가장 큰 ID만 비교하지 않는다. 기준보다 오래된 글이 수정·재노출로 목록 위에 올라와도 보내지 않는다.
- 새 글 기록·발송 작업 생성·본 글 표시·확인 성공 기록은 한 트랜잭션이다.
- **실패**: 응답 오류·네트워크 오류·목록이 없는 페이지(차단·지역 이동·구조 변경)는 기준과 본 글을 바꾸지 않고 오류만 남긴다. 다음 확인에서 그동안 올라온 글을 찾는다(최대 5페이지). 로그는 첫 실패와 이후 약 1시간마다, 복구될 때 남긴다.
- 브라우저 대체 경로는 두지 않는다. 몇 분 늦어도 다음 주기에 다시 확인하면 되고, 512MB 서버에서 브라우저를 주기적으로 띄우지 않기 위해서다. HTTP가 지속적으로 막히면 그때 이벤트 수집처럼 대체 경로를 검토한다.
- 분류: 공지는 말머리(점검·안내·완료·주요상품 등), 에린 노트는 개발자노트·CM노트를 그대로 쓴다. 업데이트·이벤트는 분류를 보내지 않는다(이벤트의 진행중·종료는 상태라서 제외).
- 썸네일: 목록 카드의 썸네일 이미지, 없으면 같은 글의 상단 고정 배너 이미지. 작성자 프로필 이미지는 쓰지 않는다. 몰리는 글 이미지를 내려받지 않고 주소만 쓴다.
- 기본 이미지: 썸네일이 없는 글은 `assets/official_news`의 게시판·분류별 이미지(공식 홈페이지 말머리 배너를 받아 봇과 함께 배포)를 파일로 첨부한다. 공지 `notice.png`(점검은 `notice_maintenance.png`, 주요상품은 `notice_shop.png`), 업데이트 `update.png`, 에린 노트 `devnote_developer.png`·`devnote_cm.png`. 이벤트는 모두 썸네일이 있어 기본 이미지가 없다.
- 제목·분류는 HTML 엔티티와 공백을 정리해 원문 그대로 저장하고, 표시할 때만 Discord 제한에 맞춘다.

## 발송 규칙

- 글 저장과 그 순간 켜진 구독의 작업 생성은 한 트랜잭션이다. 구독이 없어도 글은 기록하고, 나중에 구독한 서버에 과거 글을 보내지 않는다.
- worker는 앱 수명 동안 하나만 실행한다(Discord 재연결과 무관). Discord가 연결되지 않았으면 작업을 가져가지 않고, 재연결(Ready) 때 바로 깨운다.
- 보내기 직전 구독이 여전히 켜져 있고 작업의 채널과 같은지 확인한다. 해제·교체됐으면 `skipped`로 두고 새 채널로 소급하지 않는다.
- 403·404(채널 삭제·봇 추방·권한 상실)와 채널을 찾을 수 없음은 `failed`로 두고 다시 시도하지 않는다.
- 네트워크·5xx·시간 초과(60초)는 1분→5분→15분→60분(상한) 간격으로 최대 8번까지 시도한 뒤 `failed`(`retry_exhausted:…`)로 둔다. 429는 Discord.Net이 기다렸다 다시 보낸다.
- 한 서버의 실패는 그 작업에만 기록하고 다른 서버 작업은 계속 처리한다.
- 멘션은 보내지 않는다(`AllowedMentions.None`).

### 중복의 한계

같은 글은 본 글 기록과 eventId 고유 제약으로 다시 만들지 않는다. 하지만 Discord 전송과 DB 기록은 한 트랜잭션으로 묶을 수 없다. 전송 직후 상태를 기록하기 전에 프로세스가 끝나면, 다음 시작 때 처리 중(`sending`) 작업을 다시 대기로 돌리므로 **같은 메시지가 한 번 더 나갈 수 있다.** 완전한 exactly-once는 보장하지 않는다. 시작 시 복구는 DB 하나를 봇 프로세스 하나가 쓰는 전제다.

## Embed

| 게시판 | author | 색상 | footer |
| --- | --- | --- | --- |
| 공지 | 📢 공식 공지 | 파랑 | 몰리 • 공식 공지 알림 |
| 업데이트 | 🛠️ 업데이트 | 초록 | 몰리 • 공식 업데이트 알림 |
| 이벤트 | 🎉 이벤트 | 보라 | 몰리 • 공식 이벤트 알림 |
| 에린 노트 | 📝 에린 노트 | 청록 | 몰리 • 공식 에린 노트 알림 |

title은 공식 제목(256자로 줄임), url은 원문, `분류` 필드(있을 때), `발견 시각` 필드(KST), 썸네일은 큰 Embed image. 게시 시각으로 오해하지 않도록 Embed timestamp는 쓰지 않는다. 본문 요약은 만들지 않는다.

## 구독 명령

- `작업`: 등록·확인·해제·테스트, 기본 확인. 응답은 모두 본인에게만 보인다.
- 모든 작업(등록·확인·해제·테스트)은 서버 관리 권한(`ManageGuild`)이 필요하다. 명령에 `DefaultMemberPermissions(ManageGuild)`를 붙여 권한 없는 사용자에게는 기본으로 보이지 않고, 실행할 때도 다시 확인한다. 채널 알림(이벤트 마감·시세 알림)도 같은 규칙이다.
- 등록은 실행한 채널을 쓰고 기존 설정을 교체한다. 일반 텍스트·공지 채널만(스레드·음성 채널 채팅·포럼 제외), 봇의 보기·메시지 보내기·링크 첨부·파일 첨부(기본 이미지) 권한을 확인한다. 공지 채널 자동 교차게시는 하지 않는다.
- `/이벤트알림`과 `/이벤트마감알림`은 별도 설정이며 같은 채널을 함께 써도 된다.
- `테스트`: 목록 첫 페이지를 바로 읽어 무작위 글 하나를 실제와 같은 Embed(footer에 `(테스트)`)로 보낸다. 등록돼 있으면 등록 채널로 보내 권한까지 확인하고, 아니면 실행한 사람에게만 보여준다. 수집 상태·발송 기록은 바꾸지 않는다.

## 운영

| 설정 | 환경변수 | 기본값 |
| --- | --- | --- |
| `OfficialNews:Enabled` | `OfficialNews__Enabled` | true |

- 끄면 공식 홈페이지를 확인하지 않는다. 이미 만든 발송 작업은 계속 보낸다. 잘못된 값이면 켠 채로 시작하고 로그를 남긴다.
- 확인 명령은 마지막 성공 확인 시각을 보여주고, 1시간 넘게 성공하지 못하면 경고를 붙인다.
- 서버 IP가 해외로 판정되거나 Cloudflare가 데이터센터 IP를 막으면 확인이 계속 실패한다. 로그의 `목록 확인 실패`와 `official_news_board_states.last_error`로 알 수 있다.

### 활성화 순서

1. 배포 전 DB를 백업한다.
2. 배포·재시작하면 로그에 `공식 소식 수집 시작`과 게시판별 `첫 확인: 지금 목록의 글 N개를 기준으로 기록(보내지 않음)`이 보여야 한다. 첫 확인은 아무것도 보내지 않는다.
3. 테스트 서버(`Discord__GuildId`)의 허용된 채널에서 네 명령의 등록·확인·해제를 확인한다. 확인 응답에 `공식 홈페이지 마지막 확인`이 보여야 한다.
4. 새 글이 올라오면 약 10분 안에 테스트 채널로 Embed가 오는지 확인한다. 새 글이 없을 수 있으므로 사이트에 글을 만들거나 상태를 지워 시험하지 않는다.
5. 운영 채널을 구독한다.

로컬에서 서버 없이 파서만 확인하려면 `dotnet run --project tests/Molly.DataTests -c Release -- --news-live`(실제 사이트 4요청, DB·Discord 미사용)를 쓴다.

### 상태 확인

```bash
sqlite3 /var/lib/molly/database/molly.sqlite \
  "SELECT board, floor_post_id, last_checked_at_utc, last_success_at_utc, last_error FROM official_news_board_states;" \
  "SELECT status, COUNT(*) FROM official_news_deliveries GROUP BY status;" \
  "SELECT guild_id, channel_id, event_id, attempts, last_error_code FROM official_news_deliveries WHERE status = 'failed' ORDER BY id DESC LIMIT 20;"
```

DB 경로는 실제 `MOLLY_DATA_DIR`·`MollyDatabase__Path`에 맞춘다. 서버 운영자는 `/…알림` 확인에서 보내지 못한 알림 수를 볼 수 있다.

서버 운영자는 `/몰리상태`로 네 게시판의 최근 확인 결과·마지막 시도·마지막 성공과 이 서버의 등록 채널·권한·전송 상태를 함께 볼 수 있다([몰리 상태 기획서](molly-status.md)).

### 백업·롤백

- 배포 전 [수동 백업 절차](lightsail-manual-backup.md)로 DB를 백업한다.
- 기능만 끄려면 `OfficialNews__Enabled=false`로 재시작한다. 다시 켜면 그동안 올라온 글을 최대 5페이지까지 찾아 보낸다.
- 상태 테이블을 지우면 다음 시작에 첫 확인부터 다시 하므로 지우지 않는다.
- 이전 버전으로 되돌려도 새 테이블은 쓰이지 않을 뿐 다른 기능에 영향이 없다. 테이블을 지우지 않는다.

## 최소 구현 범위

네 게시판 수집·구독·발송, 재시작 복구, 확인 명령의 발송·확인 현황.

## 제외 범위

글 수정·삭제 감지, AI 요약·본문 복제, 분류별 필터, DM 구독, 멘션 역할, 한 게시판의 여러 채널 동시 구독, 기존 이벤트 마감 수집기 교체, 브라우저 대체 수집, 관리자 대시보드, 자동 보관 삭제.

## 성공 기준

- 등록 후 알림을 유지하는 서버 수, 게시판별 구독 수
- 글당 발송 성공률과 `failed` 비율, 목록 확인 실패 비율
- 중복 발송 신고 0건
