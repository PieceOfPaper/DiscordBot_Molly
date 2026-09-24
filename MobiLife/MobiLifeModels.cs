namespace Molly.MobiLife;

// 모비라이프 응답 DTO. JSON 필드는 snake_case이며 MobiLifeApiClient가 자동 변환합니다.
// 기능을 만들 때 필요한 응답만 이곳에 추가하고, 기능 코드에는 도메인 모델로 바꿔 넘기세요.

public sealed record MobiLifeCategory(string ParentCategory, int ItemCount);

public sealed record MobiLifeCategoriesResponse(IReadOnlyList<MobiLifeCategory> Data);
