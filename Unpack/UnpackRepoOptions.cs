using Microsoft.Extensions.Configuration;

namespace Molly.Unpack;

/// <summary>
/// 게임 데이터 저장소(PieceOfPaper/MabinogiMobileUnpack, private) 접근 설정.
/// 토큰은 Discord 토큰과 같은 방식으로 user-secrets(Unpack:Token) 또는 서버 환경변수(Unpack__Token)에 둡니다.
/// </summary>
public sealed record UnpackRepoOptions
{
    public const string DefaultRepository = "PieceOfPaper/MabinogiMobileUnpack";
    public const string DefaultBranch = "main";
    public const string ApiBaseUrl = "https://api.github.com/";

    // 이 저장소 하나에 Contents 읽기 권한만 준 fine-grained personal access token입니다.
    public string? Token { get; init; }
    // false면 토큰이 있어도 호출하지 않습니다. 저장소 이전·토큰 문제 시 코드 변경 없이 끄는 스위치입니다.
    public bool Enabled { get; init; } = true;
    public string Repository { get; init; } = DefaultRepository;
    public string Branch { get; init; } = DefaultBranch;
    // 데이터 파일은 수 MB까지 커서 여유를 둡니다.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    public static UnpackRepoOptions FromConfiguration(IConfiguration config)
    {
        var options = new UnpackRepoOptions { Token = config["Unpack:Token"]?.Trim() };
        if (bool.TryParse(config["Unpack:Enabled"], out var enabled))
            options = options with { Enabled = enabled };
        var repository = config["Unpack:Repository"]?.Trim();
        if (!string.IsNullOrEmpty(repository))
        {
            var parts = repository.Split('/');
            if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
                throw new ArgumentException("Unpack:Repository는 '소유자/저장소' 형식이어야 합니다.");
            options = options with { Repository = repository };
        }
        var branch = config["Unpack:Branch"]?.Trim();
        if (!string.IsNullOrEmpty(branch))
            options = options with { Branch = branch };
        return options;
    }
}
