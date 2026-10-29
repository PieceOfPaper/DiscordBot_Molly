using System.Net;
using Discord.Net;
using Microsoft.Data.Sqlite;

namespace Molly.Status;

/// <summary>
/// 수집·전송 실패를 운영자에게 보여줄 짧은 분류로 바꾼다. DB에는 분류 코드만 저장하고 원문 메시지(주소·키·스택)는 저장·표시하지 않는다.
/// 상세 원인은 각 기능의 기존 로그로 확인한다.
/// </summary>
public static class StatusErrorKinds
{
    // 수집
    public const string Timeout = "timeout";
    public const string Network = "network";
    public const string HttpStatus = "http_status";
    public const string Parse = "parse";
    public const string Auth = "auth";
    public const string RateLimited = "rate_limited";
    public const string Unavailable = "unavailable";
    public const string NotConfigured = "not_configured";
    public const string EmptyResult = "empty_result";
    public const string SourceData = "source_data";
    public const string Storage = "storage";
    // 전송
    public const string ChannelMissing = "channel_missing";
    public const string NotMessageChannel = "not_message_channel";
    public const string Permission = "permission";
    public const string DiscordError = "discord_error";
    public const string Unknown = "unknown";

    public static string Describe(string? kind) => kind switch
    {
        Timeout => "응답 시간 초과",
        Network => "원본 사이트 연결 실패",
        HttpStatus => "원본 사이트 오류 응답",
        Parse => "원본 데이터 해석 실패",
        Auth => "API 인증 설정 확인 필요",
        RateLimited => "요청 한도 초과로 잠시 대기",
        Unavailable => "원본 서비스 응답 없음",
        NotConfigured => "API 설정 없음",
        EmptyResult => "검색 결과 없음(검색 조건 확인 필요)",
        SourceData => "원본 시트 데이터 확인 필요",
        Storage => "저장 실패",
        ChannelMissing => "채널 접근 불가",
        NotMessageChannel => "메시지를 보낼 수 없는 채널",
        Permission => "메시지 전송 권한 부족",
        DiscordError => "Discord 일시 오류",
        _ => "알 수 없는 오류",
    };

    /// <summary>수집 중 발생한 예외의 분류. 호출자 취소가 아닌 취소는 HttpClient 시간 초과다.</summary>
    public static string ClassifyCollection(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => Timeout,
        HttpRequestException => Network,
        MobiEventFetchException fetch when fetch.Message.StartsWith("HTTP ", StringComparison.Ordinal) => HttpStatus,
        MobiEventFetchException or Molly.OfficialNews.OfficialNewsFetchException or FormatException or InvalidDataException => Parse,
        SqliteException => Storage,
        _ => Unknown,
    };

    /// <summary>Discord 전송 예외의 분류.</summary>
    public static string ClassifyDelivery(Exception ex) => ex switch
    {
        AlertDeliveryException delivery => delivery.Kind,
        Molly.OfficialNews.NewsDeliveryException news => FromNewsDeliveryCode(news.Code),
        HttpException http => FromDiscord(http.HttpCode, (int?)http.DiscordCode),
        OperationCanceledException or TimeoutException => Timeout,
        _ => Unknown,
    };

    public static string FromDiscord(HttpStatusCode httpCode, int? discordCode) => (httpCode, discordCode) switch
    {
        (_, 50013) => Permission,
        (_, 50001) => ChannelMissing,
        (_, 10003) => ChannelMissing,
        (HttpStatusCode.Forbidden, _) => Permission,
        (HttpStatusCode.NotFound, _) => ChannelMissing,
        _ => DiscordError,
    };

    /// <summary>공식 소식 발송 작업에 저장된 오류 코드(discord_403_50013, channel_not_found, retry_exhausted:timeout …)의 분류.</summary>
    public static string FromNewsDeliveryCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return Unknown;
        if (code.StartsWith("retry_exhausted:", StringComparison.Ordinal)) code = code["retry_exhausted:".Length..];
        if (code is "channel_not_found") return ChannelMissing;
        if (code is "not_message_channel") return NotMessageChannel;
        if (code is "timeout") return Timeout;
        var parts = code.Split('_');
        if (parts.Length >= 2 && parts[0] == "discord" && int.TryParse(parts[1], out var http))
            return FromDiscord((HttpStatusCode)http, parts.Length >= 3 && int.TryParse(parts[2], out var discord) ? discord : null);
        return Unknown;
    }
}

/// <summary>채널을 찾지 못했거나 메시지 채널이 아닐 때 시세·이벤트 마감 알림 전송이 던지는 예외. Kind는 <see cref="StatusErrorKinds"/>의 전송 분류다.</summary>
public sealed class AlertDeliveryException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}
