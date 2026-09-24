using Microsoft.Extensions.Configuration;

namespace Molly.MobiLife;

/// <summary>
/// 모비라이프 OpenAPI 설정. Discord 토큰과 같은 방식으로 user-secrets 또는 환경변수에서 읽습니다.
/// 개발용 키는 로컬 user-secrets(MobiLife:ApiKey), 서비스용 키는 서버 환경변수(MobiLife__ApiKey)에 둡니다.
/// </summary>
public sealed record MobiLifeOptions
{
    public const string DefaultBaseUrl = "https://open.mabimobi.life/v1/";

    // 공식 한도는 키당 분당 30회·하루 5,000회입니다. 여유를 두고 로컬에서 먼저 막습니다.
    public const int DefaultPerMinuteLimit = 25;
    public const int DefaultPerDayLimit = 4500;

    public string? ApiKey { get; init; }
    // false면 키가 있어도 호출하지 않습니다. API 종료·키 폐기 시 코드 변경 없이 끄는 스위치입니다.
    public bool Enabled { get; init; } = true;
    public Uri BaseUrl { get; init; } = new(DefaultBaseUrl);
    public int PerMinuteLimit { get; init; } = DefaultPerMinuteLimit;
    public int PerDayLimit { get; init; } = DefaultPerDayLimit;
    // 캐시가 비었을 때 응답이 8초 가까이 걸린 적이 있어 여유를 둡니다.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static MobiLifeOptions FromConfiguration(IConfiguration config)
    {
        var options = new MobiLifeOptions { ApiKey = config["MobiLife:ApiKey"]?.Trim() };
        if (bool.TryParse(config["MobiLife:Enabled"], out var enabled))
            options = options with { Enabled = enabled };
        var baseUrl = config["MobiLife:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("MobiLife:BaseUrl은 https 절대 주소여야 합니다.");
            options = options with { BaseUrl = uri };
        }
        return options;
    }
}
