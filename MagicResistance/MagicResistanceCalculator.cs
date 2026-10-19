using System.Globalization;

namespace Molly.MagicResistance;

/// <summary>한 콘텐츠에 대한 계산 결과. 배율은 반올림하지 않은 값이다.</summary>
public sealed record MagicResistanceResult(MagicPressureContent Content, double OutgoingMultiplier, double IncomingMultiplier)
{
    /// <summary>필요 마도저항보다 낮을 때만 미달이다(같으면 충족).</summary>
    public bool BelowRequirement { get; init; }
    public double OutgoingPercent => (OutgoingMultiplier - 1) * 100;
    public double IncomingPercent => (IncomingMultiplier - 1) * 100;
}

/// <summary>
/// 마도저항과 마도 압력 차이로 주는·받는 피해 배율을 계산한다. 공식이 공개되지 않아 커뮤니티 추정식을 쓴다.
/// 주는 피해: Δ&lt;0이면 2^(Δ/1000), Δ≥0이면 1 + 0.5×(1 − 2^(−Δ/4000)) (공식 초과 예시 +4,000→25%, +8,000→37.5%와 일치)
/// 받는 피해: Δ&lt;0이면 1 + ((P−R)/1000)^0.75, Δ≥0이면 1 (원 실험 자료 미확인)
/// </summary>
public static class MagicResistanceCalculator
{
    public static double OutgoingMultiplier(int resistance, int pressure)
    {
        double delta = (long)resistance - pressure;
        return delta < 0 ? Math.Pow(2, delta / 1000) : 1 + 0.5 * (1 - Math.Pow(2, -delta / 4000));
    }

    public static double IncomingMultiplier(int resistance, int pressure)
    {
        double shortfall = (long)pressure - resistance;
        return shortfall > 0 ? 1 + Math.Pow(shortfall / 1000, 0.75) : 1;
    }

    public static MagicResistanceResult Evaluate(int resistance, MagicPressureContent content) =>
        new(content, OutgoingMultiplier(resistance, content.Pressure), IncomingMultiplier(resistance, content.Pressure))
        {
            BelowRequirement = resistance < content.RequiredResistance,
        };

    public static IReadOnlyList<MagicResistanceResult> EvaluateAll(int resistance, IEnumerable<MagicPressureContent> contents) =>
        contents.Select(c => Evaluate(resistance, c)).ToList();

    /// <summary>소수점 한 자리로 반올림해 `+19.2%`·`−34.0%`로 표기한다. 반올림 결과가 0이면 부호 없이 `0%`.</summary>
    public static string FormatPercent(double percent)
    {
        var rounded = Math.Round(percent, 1, MidpointRounding.AwayFromZero);
        if (rounded == 0) return "0%";
        var text = Math.Abs(rounded).ToString("0.0", CultureInfo.InvariantCulture);
        return (rounded > 0 ? "+" : "−") + text + "%";
    }
}
