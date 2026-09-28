using System.Text;
using Discord;
using Molly.Currency;

namespace Molly.Attendance;

/// <summary>출석부 메시지에 보여줄 내용. Discord 호출 없이 만들고 검사할 수 있도록 표시 데이터와 그리기를 나눈다.</summary>
public sealed record AttendanceBoard(DateOnly Date, IReadOnlyList<string> Names, long Reward, bool Active);

public static class AttendanceBoardRenderer
{
    public const string CheckInButtonId = "attendance:check_in";

    // 임베드 설명은 4096자까지다. 머리말과 "외 N명" 줄을 넣을 여유를 남긴다.
    public const int MaxNamesLength = 3500;
    private const int MaxNameLength = 32;

    private static readonly string[] s_DayNames = ["일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일"];

    public static string Title(DateOnly date) => $"📅 {date.Year}년 {date.Month}월 {date.Day}일 {s_DayNames[(int)date.DayOfWeek]} 출석부";

    public static Embed BuildEmbed(AttendanceBoard board)
    {
        var embed = new EmbedBuilder().WithTitle(Title(board.Date));
        if (!board.Active)
            return embed.WithColor(Color.LightGrey)
                .WithDescription("이 출석부는 더 이상 사용하지 않아요.")
                .Build();

        var text = new StringBuilder()
            .Append("오늘 몰리에 들른 사람은 출석해주세요!\n")
            .Append("출석 보상: ").Append(MollyToken.Amount(board.Reward)).Append("\n\n")
            .Append("**출석 인원: ").Append(board.Names.Count).Append("명**\n");
        if (board.Names.Count == 0) text.Append("아직 출석한 사람이 없어요.");
        else text.Append(FormatNames(board.Names));
        // 꼬리말은 서버 이모지를 그리지 않아 증표 안내는 본문 맨 아래 작은 글씨로 둔다.
        text.Append("\n\n-# ").Append(MollyToken.Label).Append("는 몰리 전용 놀이 재화예요");
        return embed.WithColor(new Color(0x9B59B6))
            .WithDescription(text.ToString())
            .WithFooter($"매일 오전 {AttendanceClock.ResetHourKst}시에 초기화됩니다")
            .Build();
    }

    public static MessageComponent BuildComponents(AttendanceBoard board)
        => new ComponentBuilder()
            .WithButton("출석", CheckInButtonId, ButtonStyle.Success, new Emoji("✅"), disabled: !board.Active)
            .Build();

    /// <summary>번호를 붙인 명단. 길이 제한을 넘으면 앞쪽 이름만 보여주고 "외 N명"으로 줄인다. 이름은 멘션·서식이 되지 않게 이스케이프한다.</summary>
    public static string FormatNames(IReadOnlyList<string> names)
    {
        var text = new StringBuilder();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i].Length > MaxNameLength ? names[i][..MaxNameLength] + "…" : names[i];
            var line = $"{i + 1}. {Format.Sanitize(name)}\n";
            if (text.Length + line.Length > MaxNamesLength)
                return text.Append($"… 외 {names.Count - i}명").ToString();
            text.Append(line);
        }
        return text.ToString().TrimEnd();
    }
}
