# Codex 작업 지침

## 프로젝트와 방향
- .NET 10 콘솔 애플리케이션과 Discord.Net InteractionService 기반 Discord 봇이다.
- 기존 이벤트·랭킹·상점 조회 기능을 유지한다.
- 향후 방향은 함께 노는 이벤트 기능이며, 룬 설명을 보고 이름 맞추기는 구상 중인 예시다.
- Google Sheets 룬 데이터는 Runes/에서 읽고 검증한다. 퀴즈 규칙은 아직 구현하거나 확정하지 않았다. 요청된 범위만 작업한다.

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
- MobiEvent*, MobiRankBrowser: Playwright 기반 수집과 이벤트 알림.
- MobiShop.cs, Commons/CsvTable.cs: 상점 테이블과 CSV 매핑.
- Commons/LocalStorage.cs: 서버별 JSON 저장. MOLLY_DATA_DIR로 저장 위치 지정.
- assets/: 배포에 포함해야 하는 이미지와 상점 테이블.

## 구현 원칙
- 사용자에게 보여주는 명령과 안내는 한국어로 작성한다.
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
- 실서버 검증은 테스트 서버 ID Discord__GuildId를 지정하고 사용자의 요청 범위 안에서 수행한다.
