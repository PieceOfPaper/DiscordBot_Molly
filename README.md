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
- .NET 9 SDK
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
dotnet tool restore
dotnet playwright install --with-deps
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
