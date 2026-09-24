# Codex 작업 지침

## 프로젝트와 방향
- .NET 10 콘솔 애플리케이션과 Discord.Net InteractionService 기반 Discord 봇이다.
- 기존 이벤트·랭킹·상점 조회 기능을 유지한다.
- 향후 방향은 함께 노는 이벤트 기능이며, 룬 설명을 보고 이름 맞추기는 구상 중인 예시다.
- Google Sheets 룬 데이터는 Runes/에서 읽고 검증한다. 퀴즈 규칙은 아직 구현하거나 확정하지 않았다. 요청된 범위만 작업한다.
- 모비라이프 OpenAPI(https://open.mabimobi.life/docs)를 쓰는 작업은 `docs/mobilife-openapi.md`의 이용약관·출처 표기·요청 한도·중단 대비 규칙을 따른다. 기능은 `MobiLife/`의 공용 클라이언트를 도메인 인터페이스 뒤에서만 사용한다.
- 배틀(자동전투) 기능의 설계는 `docs/battle/README.md`를 따른다. 클래스·스킬 시트에 새 클래스 원본 데이터가 추가되어 배틀 관련 시트에 반영할 때는 `docs/battle/new-class-workflow.md`의 절차를 따른다.

## 환경과 검증
- SDK 선택은 global.json을 따른다. 프리릴리스 SDK는 사용하지 않는다.
- 준비: `bash scripts/setup.sh`
- 변경 후 필수 검증: `bash scripts/verify.sh`
- 검증은 restore, Release build, publish와 필수 에셋 포함 여부를 검사한다.
- tests/Molly.DataTests는 외부 테스트 패키지가 없는 콘솔 회귀 테스트이며 verify.sh에서 실행한다. 기능 변경 시 정답 판정·동시 제출·저장 등 실제 동작을 확인하는 테스트를 적절히 추가한다.
- 프로젝트 루트가 csproj의 기본 컴파일 범위다. 하위에 테스트 프로젝트를 추가하면 앱 csproj에서 해당 소스를 제외한다.
- CI에서는 실제 봇 실행, Discord 로그인, 사이트 수집을 하지 않는다.
- 실제 봇 실행은 시작 과정에서 웹 수집 및 알림 전송을 수행한다. 오프라인 검증 용도로 dotnet run을 사용하지 않는다.
- 네트워크/SDK 문제로 검증할 수 없으면 실패 원인과 수행하지 못한 검증을 명확히 보고한다.

## 코드 구조
- Program.cs: 설정, Discord 연결, Interaction 등록, 초기 데이터 로딩.
- Commands/: 슬래시 명령 처리.
- MobiEventParser·MobiEventService: 이벤트 목록 HTML 파싱과 HTTP 수집·캐시. MobiEventBrowserSource는 HTTP 차단 시에만 쓰는 Playwright 대체 경로.
- MobiRankBrowser: Playwright 기반 랭킹 수집. MobiEventExpireAlert: 이벤트 마감 알림.
- MobiShop.cs, Commons/CsvTable.cs: 상점 테이블과 CSV 매핑.
- Commons/LocalStorage.cs: 서버별 JSON 저장. MOLLY_DATA_DIR로 저장 위치 지정.
- assets/: 배포에 포함해야 하는 이미지와 상점 테이블.
- MobiLife/: 모비라이프 OpenAPI 클라이언트·출처 표기. Market/: 거래소 시세 도메인 인터페이스.
- Crafting/: 제작 시트 공용 레시피 카탈로그(여러 기능에서 재사용). HaeyeonMarket/: /해연시세모니터링 판정·저장·정각 수집.

## 구현 원칙
- 사용자에게 보여주는 명령과 안내는 한국어로 작성한다.
- Git 커밋 메시지의 제목과 본문은 한국어로 작성한다.
- 슬래시 명령을 추가·변경·삭제할 때는 README.md의 `슬래시 커맨드` 표도 함께 갱신한다. 명령어, 설명, 옵션의 필수 여부·기본값·제한을 실제 코드와 일치시킨다.
- Discord Interaction은 응답을 지연할 작업이면 먼저 DeferAsync하고, 중복 응답을 피한다.
- 게임 상태는 서버·채널별로 분리하고 동시 제출과 시간 초과를 고려한다.
- 테이블 갱신은 새 데이터를 검증한 뒤 교체하고 실패 시 마지막 정상 데이터를 유지한다.
- 게임 진행 로직과 데이터 공급 경로(CSV/Sheets)를 분리한다.
- 외부 사이트 수집 실패가 독립적인 놀이 기능의 시작을 막지 않도록 한다.
- 패키지 버전은 명시적으로 고정하고 갱신 이유를 설명한다.

## 비밀 정보
- Discord 토큰은 user-secrets 또는 Discord__Token 환경변수로 설정한다.
- .env는 자동 로딩되지 않는다. 실제 비밀 값을 파일·로그·커밋에 넣지 않는다.
- Google 인증 파일도 저장소 밖에서 관리한다.
- 모비라이프 API 키는 `MobiLife:ApiKey`(user-secrets, 개발용 키) 또는 `MobiLife__ApiKey` 환경변수(서버, 서비스용 키)로 설정한다.
- 실서버 검증은 테스트 서버 ID Discord__GuildId를 지정하고 사용자의 요청 범위 안에서 수행한다.

## GitHub Issue 작업 규칙
- 버그, 건의·개선사항, GPT의 코드·데이터 검토 결과는 GitHub Issue를 단일 작업함으로 사용한다.
- 사람이 직접 제보할 때는 `.github/ISSUE_TEMPLATE/`의 버그 또는 건의 양식을 사용한다. 작성 기준과 공개 금지 정보는 `.github/ISSUE_TEMPLATE/README.md`를 따른다.
- GPT 검토는 제목을 `[AI 검토] 주제` 형식으로 작성하고, 본문에 상태, 기준 커밋, 시트 확인 시각, 결론, 필수 수정, 선택 개선, 유지할 부분, 완료 조건을 기록한다.
- 새 Issue를 만들기 전에 열린 Issue에서 같은 내용이 있는지 확인한다. 중복이면 새로 만들지 않고 기존 Issue에 정보를 보강한다.
- Issue가 열려 있다는 사실만으로 구현이 승인된 것은 아니다. 조사와 의견 정리는 가능하지만, 코드·시트 수정은 사용자가 현재 요청에서 Issue 번호와 반영 범위를 명시하거나 명확히 구현을 승인했을 때만 진행한다.
- `판단 필요` 또는 추가 질문이 남은 Issue를 임의로 구현하지 않는다. 서로 충돌하는 제안이나 선택지가 있으면 구현 전에 사용자에게 알린다.
- Issue 작업 전 현재 본문과 댓글, 기준 커밋 이후의 코드 변경을 확인한다. Google Sheets 데이터가 관련되면 구현 직전에 최신 시트를 다시 읽는다.
- 구현 중에는 Issue 체크리스트를 실제 진행 상태와 일치시킨다. 구현 후 원인, 변경 파일·시트, 실행한 검증, 미반영 항목을 Issue 댓글에 기록한다.
- 승인 범위와 검증이 모두 끝났을 때만 Issue를 닫는다. 일부 항목이 보류되었으면 본문이나 댓글에 남기고 필요하면 후속 Issue로 분리한다.
- 버그 수정에는 가능한 범위에서 재현 테스트 또는 회귀 테스트를 추가한다. 재현하지 못한 제보는 추측으로 수정하지 않고 필요한 정보를 요청한다.
- 공개 Issue에 Discord 토큰, Google 인증 정보, 서버 IP·SSH 정보, 개인정보, 비공개 채널 내용, 구체적인 취약점 악용 방법을 기록하지 않는다.
- Issue 반영으로 확정 설계가 달라졌다면 관련 기획서와 데이터 설명도 함께 갱신한다.
