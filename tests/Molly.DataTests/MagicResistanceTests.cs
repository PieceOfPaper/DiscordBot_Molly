using DiscordBot_Molly.Commands;
using Molly.MagicResistance;

internal static class MagicResistanceTests
{
    private static readonly MagicPressureContent s_Content = new("시험", 6_000, 6_600);

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;

    public static void Run()
    {
        var below = MagicResistanceCalculator.Evaluate(6_000, s_Content);
        Assert(below.OutgoingPercent < 0 && below.IncomingPercent > 0, "저항이 압력보다 낮으면 주는 피해 감소·받는 피해 증가");

        var equal = MagicResistanceCalculator.Evaluate(6_600, s_Content);
        Assert(Near(equal.OutgoingMultiplier, 1) && Near(equal.IncomingMultiplier, 1)
               && MagicResistanceCalculator.FormatPercent(equal.OutgoingPercent) == "0%"
               && MagicResistanceCalculator.FormatPercent(equal.IncomingPercent) == "0%", "저항이 압력과 같으면 양쪽 모두 0%");

        var above = MagicResistanceCalculator.Evaluate(7_000, s_Content);
        Assert(above.OutgoingPercent > 0 && Near(above.IncomingMultiplier, 1), "저항이 압력을 넘으면 주는 피해 증가·받는 피해 0%");

        Assert(Near(MagicResistanceCalculator.OutgoingMultiplier(10_600, 6_600), 1.25), "초과 저항 +4,000에서 주는 피해 +25%");
        Assert(Near(MagicResistanceCalculator.OutgoingMultiplier(14_600, 6_600), 1.375), "초과 저항 +8,000에서 주는 피해 +37.5%");
        Assert(MagicResistanceCalculator.OutgoingMultiplier(40_000, 0) < 1.5 && MagicResistanceCalculator.OutgoingMultiplier(int.MaxValue, 0) <= 1.5,
            "초과 저항이 아주 커도 주는 피해는 +50%를 넘지 않음");

        var shortBy1000 = MagicResistanceCalculator.Evaluate(5_600, s_Content);
        Assert(Near(shortBy1000.OutgoingMultiplier, 0.5) && Near(shortBy1000.IncomingMultiplier, 2),
            "저항이 압력보다 1,000 낮으면 주는 피해 −50%, 받는 피해 +100%");
        Assert(!Near(MagicResistanceCalculator.IncomingMultiplier(4_600, 6_600), 1 / MagicResistanceCalculator.OutgoingMultiplier(4_600, 6_600)),
            "받는 피해 배율은 주는 피해 배율의 역수가 아님(2,000 미달: 1+2^0.75 ≠ 4)");

        // 입력 7,200: 출력 예시와 같은 필드
        var embed = MagicResistanceCommand.BuildEmbed(7_200, TestMagicResistance.Data);
        Assert(embed.Title == "마도저항 확인 · 7,200" && embed.Description == "룬다 어비스 난이도별 마도 압력과 비교한 결과예요.",
            "제목에 천 단위 쉼표를 넣은 입력 마도저항, 설명에 게임 데이터의 어비스 시즌 이름 표시");
        var fields = embed.Fields.Select(f => (f.Name, f.Value)).ToList();
        Assert(fields.SequenceEqual(new[]
        {
            ("지옥1", "주는 피해 +19.2% · 받는 피해 0%"),
            ("지옥2", "주는 피해 +4.9% · 받는 피해 0%"),
            ("지옥3", "주는 피해 −34.0% · 받는 피해 +68.2%"),
            ("지옥4 ⚠️ 입장 기준 미달", "주는 피해 −71.3% · 받는 피해 +155.4%"),
        }), "입력 7,200에서 지옥1→지옥4 순서로 출력 예시와 일치");
        Assert(embed.Footer!.Value.Text.Contains(MagicResistanceCommand.EstimateNotice) && embed.Footer.Value.Text.Contains(MagicResistanceCommand.PartyNotice),
            "미달 항목이 있으면 추정식 안내와 파티장 동반 입장 예외를 함께 안내");
        Assert(fields.Count(f => f.Name.Contains("⚠️")) == 1, "지옥3 필요 마도저항 7,200과 같으면 미달이 아님");

        var hell4 = TestMagicResistance.Data.AbyssHell.Single(c => c.Name == "지옥4");
        Assert(MagicResistanceCalculator.Evaluate(8_499, hell4).BelowRequirement
               && !MagicResistanceCalculator.Evaluate(8_500, hell4).BelowRequirement
               && !MagicResistanceCalculator.Evaluate(8_501, hell4).BelowRequirement, "필요 저항 미만만 미달, 같거나 초과면 미달 아님");
        var full = MagicResistanceCommand.BuildEmbed(9_000, TestMagicResistance.Data);
        Assert(full.Fields.All(f => !f.Name.Contains("⚠️")) && !full.Footer!.Value.Text.Contains(MagicResistanceCommand.PartyNotice),
            "모든 기준을 충족하면 미달 표시와 파티장 예외 안내를 붙이지 않음");

        var zero = MagicResistanceCommand.BuildEmbed(0, TestMagicResistance.Data);
        Assert(zero.Title == "마도저항 확인 · 0" && zero.Fields.Length == 4 && zero.Fields.All(f => f.Name.Contains("⚠️"))
               && zero.Fields[0].Value == "주는 피해 −95.3% · 받는 피해 +303.8%", "입력 0에서도 오류 없이 계산");

        Assert(MagicResistanceCalculator.FormatPercent(-0.04) == "0%" && MagicResistanceCalculator.FormatPercent(0.04) == "0%"
               && MagicResistanceCalculator.FormatPercent(-0.0) == "0%", "반올림 결과가 0이면 음수 0 없이 0%");
        Assert(MagicResistanceCalculator.FormatPercent(-0.05) == "−0.1%" && MagicResistanceCalculator.FormatPercent(12.345) == "+12.3%",
            "소수점 한 자리 반올림과 증감 부호 표기");
        var calmEmbed = MagicResistanceCommand.BuildEmbed(4_400, TestMagicResistance.Data);
        Assert(calmEmbed.Fields[0].Value == "주는 피해 0% · 받는 피해 0%", "압력과 같은 난이도는 0%로 표시");

        var hell5 = TestMagicResistance.Data with { AbyssHell = [.. TestMagicResistance.Data.AbyssHell, new MagicPressureContent("지옥5", 9_500, 10_000)] };
        Assert(MagicResistanceCommand.BuildEmbed(7_200, hell5).Fields.Select(f => f.Name).Last() == "지옥5 ⚠️ 입장 기준 미달",
            "게임 데이터에 새 지옥 난이도가 생기면 코드 수정 없이 함께 비교");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
