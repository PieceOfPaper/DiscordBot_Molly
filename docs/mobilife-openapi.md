# 모비라이프 OpenAPI 연동 가이드

모비라이프(https://mabimobi.life/)가 제공하는 OpenAPI를 몰리 기능에 사용할 때의 기준이다.
기반 시스템(설정·클라이언트·출처 표기·장애 대비) 위에 다음 기능이 있다.

| 기능 | 사용 API | 요청량 |
| --- | --- | --- |
| `/해연시세모니터링` (`HaeyeonMarket/`) | `market/prices` (`Market/IMarketPriceSource` → `MobiLife/MobiLifeMarketPriceSource`) | 매 정각 검색어 5개 = 하루 약 120회 |
| `/상자시세모니터링`·`/패키지시세모니터링` (`KeywordMarket/`) | `market/prices` (같은 인터페이스, `parent_category` 조건 사용) | 매 정각 모니터링당 검색어 1개(결과 100건마다 1회, 최대 5회) = 2026-09-25 기준 하루 약 48회 |

- API 문서: https://open.mabimobi.life/docs
- 기본 주소: `https://open.mabimobi.life/v1/`
- 키 발급·관리: 모비라이프 계정 → 프로필 메뉴 → '모비라이프 OpenAPI' (https://mabimobi.life/api-keys)

## 1. 이용약관 (반드시 지킬 것)

> 모비라이프 OpenAPI 데이터는 모비라이프(mabimobi.life)가 제공하며, 이는 넥슨(Nexon) 또는 데브캣(devCAT)에서 제공하는 공식 API가 아닙니다. 모비라이프 OpenAPI는 무료로 제공되는 서비스이며, 비영리 목적 뿐만 아니라 상업적인 용도로 사용할 수 있습니다. 단, API 자체에 대한 재배포·재판매를 금지하며 남용 시 키가 무통보 폐기될 수 있습니다. 모비라이프 OpenAPI를 공개 서비스 혹은 자료에 활용할 경우 출처('모비라이프 제공')를 표기해야 합니다. 모비라이프는 API로 제공되는 데이터 자체의 내용 및 정확성, 그리고 그를 통해 만들어진 결과물에 대해 어떠한 법적인 책임도 지지 않습니다.

이 약관에서 나오는 개발 규칙:

| 약관 | 개발 규칙 |
| --- | --- |
| 출처 표기 의무 | 모비라이프 데이터를 보여주는 **모든** Discord 응답에 `모비라이프 제공`을 표기한다. Embed는 `WithMobiLifeAttribution()`, 일반 메시지는 `MobiLifeAttribution.AppendTo()`를 사용한다. 도메인 인터페이스 뒤의 기능은 `IMarketPriceSource.Attribution`처럼 공급 구현체가 준 문구를 표기한다. 문구를 바꾸지 않는다. |
| 공식 API 아님 | 넥슨 공식 데이터처럼 안내하지 않는다. 필요하면 "모비라이프 제공 데이터"라고 명시한다. |
| 재배포·재판매 금지 | API를 그대로 중계하는 명령(원본 JSON 덤프, 무제한 페이지 넘김, 대량 내보내기 파일 등)을 만들지 않는다. 가공·요약한 결과만 보여준다. 응답 원본을 저장소에 커밋하지 않는다. |
| 남용 시 키 무통보 폐기 | 요청 한도를 여유 있게 지키고(아래 3절), 같은 조회는 캐시를 공유한다. 사용자 입력 한 번이 여러 요청으로 불어나지 않게 설계한다. |
| 데이터 정확성 책임 없음 | 시세·채팅 등은 참고용이며 오류가 있을 수 있음을 전제로 설계한다. 데이터만 믿고 되돌릴 수 없는 동작(보상 지급 등)을 하지 않는다. |

## 2. 키 설정 (개발용 / 서비스용)

Discord 토큰과 같은 방식이다. 키 값은 파일·로그·커밋·Issue에 남기지 않는다.

| 설정 | 설명 |
| --- | --- |
| `MobiLife:ApiKey` / `MobiLife__ApiKey` | API 키. 없으면 연동 기능만 "연동이 꺼져 있음" 안내를 하고 봇은 정상 동작한다. |
| `MobiLife:Enabled` / `MobiLife__Enabled` | 선택, 기본 `true`. `false`면 키가 있어도 호출하지 않는다(긴급 차단 스위치). |
| `MobiLife:BaseUrl` / `MobiLife__BaseUrl` | 선택, 기본 `https://open.mabimobi.life/v1/`. 버전·주소 변경 시 코드 수정 없이 바꾼다. https만 허용한다. |

```bash
# 로컬 개발: 개발용 키
dotnet user-secrets set "MobiLife:ApiKey" "개발용_키"
```

```ini
# 서버(/etc/molly/molly.env): 서비스용 키
MobiLife__ApiKey=서비스용_키
```

개발용과 서비스용 키를 분리해 두면 한도(키당 분당 30회·하루 5,000회)가 따로 적용되어 개발 중 테스트가 서비스를 막지 않는다. 단 계정 전체 한도(하루 20,000회)는 두 키가 공유한다.

키 확인(요청 1회, Discord 미사용):

```bash
dotnet run --project tests/Molly.DataTests/Molly.DataTests.csproj --configuration Release -- --mobilife-live
```

## 3. 기반 코드 구조 (`MobiLife/`)

| 파일 | 역할 |
| --- | --- |
| `MobiLifeOptions.cs` | 설정 읽기와 기본값(주소, 로컬 한도, 30초 시간 제한) |
| `MobiLifeApiClient.cs` | Bearer 인증, GET + snake_case JSON 변환, 로컬 한도, 장애 감지·일시 차단. 봇 전체에서 하나만 사용(`Program.instance.MobiLife`) |
| `MobiLifeAttribution.cs` | 출처 표기 문구와 Embed/텍스트 도우미 |
| `MobiLifeModels.cs` | 응답 DTO. 기능에 필요한 것만 추가 |

클라이언트는 예외 대신 `MobiLifeResult<T>`를 돌려준다. 호출자 취소만 `OperationCanceledException`으로 전달한다.

| 상태 | 원인 | 클라이언트 동작 |
| --- | --- | --- |
| `Success` | 정상 | 연속 실패 수 초기화 |
| `NotConfigured` / `Disabled` | 키 없음 / 운영자가 끔 | 요청하지 않음 |
| `RateLimited` | 로컬 한도(분당 25·24시간 4,500) 또는 서버 429 | 429는 `Retry-After`(없으면 1분) 동안 요청하지 않음 |
| `Unauthorized` | 401(키 오류·폐기) | 재시작 전까지 요청하지 않고 로그에 경고 |
| `Unavailable` | 통신 오류·시간 초과·5xx·403 차단·404/410 | 연속 3회 실패 시 10분, 404/410은 즉시 1시간 요청하지 않음 |
| `InvalidResponse` | 응답 JSON이 예상과 다름(API 변경 가능성) | 실패로 집계 |

사용자에게는 `result.UserMessage`(한국어 안내)만 보여주고, `Detail`은 로그용이다.

## 4. 기능을 추가할 때의 규칙

1. **도메인 인터페이스로 분리한다.** 기능은 `MobiLifeApiClient`를 직접 호출하지 않는다. 예: 시세 기능이면 `IMarketPriceSource`(도메인 모델 반환)를 정의하고 `MobiLifeMarketPriceSource`가 이를 구현한다. API가 사라지면 구현체만 교체하거나 제거한다. 룬 데이터의 `IRuneSource`, 이벤트의 `IMobiEventPageSource`와 같은 방식이다.
2. **캐시와 마지막 정상 데이터를 둔다.** 같은 조회는 캐시(시세는 원본이 약 5분 간격 갱신이므로 최소 1~5분)를 공유하고, 동시 요청은 한 번의 호출로 합친다. 실패하면 마지막 정상 데이터를 "수집 시각"과 함께 보여준다(`MobiEventService` 참고). 새 데이터는 검증 후 교체한다.
3. **시작을 막지 않는다.** 시작 시 네트워크 호출을 하지 않거나, 하더라도 실패를 삼키고 다른 기능을 계속 시작한다. 키가 없는 개발 환경에서도 봇이 떠야 한다.
4. **Interaction은 먼저 `DeferAsync`한다.** 네트워크 호출(최대 30초)이 있으므로 3초 응답 제한을 넘길 수 있다.
5. **출처를 표기한다.** 1절 참고. 사용자에게 보이는 결과에서 빠지지 않도록 기능 테스트에 표기 여부 확인을 넣는다.
6. **한도를 설계 단계에서 계산한다.** 명령 1회당 요청 수 × 예상 사용량이 키당 하루 5,000회(로컬 4,500회)를 넘지 않게 한다. 페이지 넘김·자동완성·주기 작업(알림 등)은 특히 주의한다. 자동완성에는 `market/items`를 캐시해 쓰고 매 입력마다 호출하지 않는다.
7. **DTO는 필요한 필드만 정의한다.** 응답에 필드가 추가되어도 깨지지 않고, 필수 필드가 빠지면 `InvalidResponse`나 도메인 검증 실패로 처리한다.
8. **테스트는 오프라인 스텁으로 작성한다.** `tests/Molly.DataTests/MobiLifeTests.cs`의 `StubHandler`처럼 `HttpMessageHandler`를 주입한다. CI에서 실제 API를 호출하지 않는다.
9. **README를 갱신한다.** 모비라이프 데이터를 쓰는 명령은 `슬래시 커맨드` 표와 `모비라이프 OpenAPI 사용` 절에 적는다.

## 5. API 중단·키 폐기 대비

- 무료 서비스이고 키가 무통보 폐기될 수 있으므로, 모비라이프 기능은 **없어도 봇이 정상인 부가 기능**으로 설계한다.
- 긴급 대응: 서버 `molly.env`에 `MobiLife__Enabled=false`를 넣고 재시작하면 모든 호출이 멈추고 명령은 안내 메시지만 표시한다.
- 키 폐기(401): 로그에 `[모비라이프] API 키가 거부되었습니다`가 남는다. 모비라이프에서 키 상태를 확인하고 새 키로 교체 후 재시작한다.
- 주소·버전 변경: `MobiLife__BaseUrl`로 새 주소를 지정한다. 응답 형식까지 바뀌었다면 DTO와 공급 구현체를 수정한다.
- 서비스 종료: 도메인 인터페이스의 다른 구현체(직접 수집, 다른 데이터 원본)로 교체하거나, 해당 명령을 제거하고 README를 갱신한다. 기능 설계 시 "대체 원본이 없으면 어떻게 안내할지"를 미리 적어둔다.
- 명령을 숨길지 여부: Discord 명령 등록은 시작 시 한 번이므로, 호출 불가 상태에서도 명령은 남고 `UserMessage`로 안내한다.

## 6. 제공 API 요약 (2026-09-24 문서 기준)

모든 요청에 `Authorization: Bearer {API_KEY}`가 필요하다(장비 스키마·예제 GET 제외). 오류 응답은 `{"code": 401, "message": "..."}` 형식이다.

| 엔드포인트 | 내용 |
| --- | --- |
| `GET market/prices` | 거래소 최신 시세(최저가·등록 수량·1시간/24시간/7일 변동률). `parent_category`, `search`, `sort`(기본 `pct_change_24h_desc`, 급락 `_asc`, 품귀 `count_change_24h_asc`, 재입고 `_desc`), `min_count`, `limit`(기본 100), `offset` |
| `GET market/prices/history` | 아이템 시세 OHLC 추이. `kind_id`(필수), `days`(기본 7, 최대 90), `interval` |
| `GET market/items` | 아이템 `kind_id`·이름·분류 목록(자동완성·매핑용) |
| `GET market/categories` | 거래소 대분류와 아이템 수 |
| `GET world-chat/messages` | 월드 채팅. `server`, `cursor`(`next_cursor`로 과거 조회), `limit`(기본 50), `channel`(`global` 기본/`realm`/`all`) |
| `GET world-chat/search` | 월드 채팅 검색. `q`, `scope`(`message`/`player`/`all`), `server`, `start_time`/`end_time`(최대 7일), `limit`, `offset`, `channel` |
| `GET equipment/schema/1.0`, `equipment/example/1.0` | 장비 표준 데이터 스키마·예제(인증 불필요) |
| `POST equipment/validate` | 장비 문서 형식 검증(최대 1 MiB). 저장하지 않음 |

### 거래소 대분류(`parent_category`) 값

`market/categories` 응답과 `market/prices` 응답의 `parent_category`, `market/prices`의 `parent_category` 조회 조건에는 아래 한글 문자열을 그대로 쓴다. 영문 코드값은 없다.

| 값 | 아이템 수(2026-09-25 확인) |
| --- | --- |
| `데코` | 978 |
| `도구` | 16 |
| `무기` | 43 |
| `방어구` | 30 |
| `아이템` | 238 |
| `장신구` | 4 |

- 2026-09-25에 `market/categories`를 한 번 호출해 확인한 값이다. 아이템 수는 계속 바뀌므로 참고만 한다.
- 대분류가 추가·변경될 수 있다. 목록 전체가 필요한 기능(선택지·자동완성 등)은 이 표를 코드에 고정하지 말고 `market/categories`를 캐시해 쓴다. 특정 분류만 쓰는 기능은 상수로 둬도 되지만, 결과가 0건이면 분류 이름이 바뀌었을 가능성을 로그에 남긴다.
- 값이 바뀐 것 같으면 `market/categories`로 현재 목록을 확인하고 이 표를 갱신한다.

세부 필드는 API 문서를 직접 확인한다. 월드 채팅은 다른 사용자의 발언이므로 공개 채널에 옮길 때 개인정보·비공개 내용 노출을 따로 검토한다.
