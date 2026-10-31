using System.Globalization;
using System.Text.RegularExpressions;

namespace Molly.Runes;

// 룬 옵션 설명의 수치 템플릿이 가리키는 시트 값. 설명에 쓰이는 행만 골라 담습니다.
public sealed record RuneMagicEffectValues(string Action, IReadOnlyList<string> Args, double Cooldown, double SuccessRate, IReadOnlyList<string> ActivationParams);
public sealed record RuneBuffValues(double Duration, double MaxStackCount, double ShieldValue);
public sealed record RuneSkillEffectSlot(double Probability, double Coefficient, double Value, double Reserved1, double Reserved2);

public sealed class RuneTooltipValues
{
    public Dictionary<string, RuneMagicEffectValues> MagicEffects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, RuneBuffValues> Buffs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<RuneSkillEffectSlot>> SkillEffects { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// RuneOption.Description의 게임 툴팁 문법을 일반 문장으로 바꿉니다.
/// <c>$#시트@개발명(Id)!필드[단위]|자릿수$</c>는 MagicEffect·Buff·SkillEffect 값으로, <c>$1.3[d]$</c>는 숫자 그대로,
/// <c>&lt;&lt; 식 &gt;&gt;</c>는 계산 결과로 바꾸고 색상 태그는 지웁니다. 값을 찾지 못한 자리는 '?'로 남깁니다.
/// 게임 클라이언트(SpecificationFormatParser·MagicEffectSpecificationAdapter)와 같이 Id는 대소문자까지 정확히 일치해야 하고,
/// MagicEffect 행을 찾지 못하면 그 옵션의 MagicEffectList[0] 행을 대신 씁니다. Buff·SkillEffect는 대체 행이 없습니다.
/// </summary>
public static class RuneEffectText
{
    public const string Unknown = "?";

    private static readonly Regex s_Template = new(@"\$#(?<sheet>\w+)@개발명\((?<id>[^)]*)\)!(?<spec>[^$]*)\$", RegexOptions.CultureInvariant);
    private static readonly Regex s_Literal = new(@"\$(?<value>-?\d+(?:\.\d+)?)(?:\[(?<unit>[^\]]*)\])?(?:\|\d*)?\$", RegexOptions.CultureInvariant);
    private static readonly Regex s_Spec = new(@"^(?<name>.*?)(?:\[(?<unit>[^\]]*)\])?(?:\|\d*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex s_Expression = new(@"<<(?<expr>.*?)>>", RegexOptions.CultureInvariant);
    private static readonly Regex s_RichTag = new(@"</?(?:color|b|i|u|size)(?:=[^>]*)?>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex s_ActionArg = new(@"^액션 인자\{(?<index>\d+)\}$", RegexOptions.CultureInvariant);
    private static readonly Regex s_ActivationParam = new(@"^활성화 조건 인자(?<index>\d)$", RegexOptions.CultureInvariant);
    private static readonly Regex s_SkillEffectField = new(@"^효과(?<slot>\d) (?<field>.+)$", RegexOptions.CultureInvariant);

    /// <summary>설명이 참조하는 (시트, Id). 큰 시트에서 필요한 행만 고를 때 씁니다.</summary>
    public static IEnumerable<(string Sheet, string Id)> References(string description) =>
        s_Template.Matches(description).Select(m => (m.Groups["sheet"].Value, m.Groups["id"].Value));

    public static string Render(string description, RuneTooltipValues values, string? fallbackMagicEffect = null) =>
        Render(description, values, out _, fallbackMagicEffect);

    /// <param name="fallbackMagicEffect">RuneOption.MagicEffectList[0]. MagicEffect 참조 Id가 없을 때 대신 읽는 행입니다.</param>
    public static string Render(string description, RuneTooltipValues values, out int unresolved, string? fallbackMagicEffect = null)
    {
        var missing = 0;
        var text = s_Template.Replace(description, m =>
        {
            var spec = s_Spec.Match(m.Groups["spec"].Value);
            var unit = spec.Groups["unit"].Value;
            var value = Lookup(values, m.Groups["sheet"].Value, m.Groups["id"].Value, spec.Groups["name"].Value.Trim(), fallbackMagicEffect);
            if (value is null) missing++;
            return Format(value, unit);
        });
        text = s_Literal.Replace(text, m => Format(double.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture), m.Groups["unit"].Value));
        text = s_Expression.Replace(text, m => Evaluate(m.Groups["expr"].Value) ?? m.Groups["expr"].Value.Trim());
        text = s_RichTag.Replace(text, "");
        unresolved = missing;
        return Normalize(text);
    }

    private static double? Lookup(RuneTooltipValues values, string sheet, string id, string field, string? fallbackMagicEffect)
    {
        switch (sheet)
        {
            case "MagicEffect" when values.MagicEffects.TryGetValue(id, out var effect) ||
                                    (fallbackMagicEffect is not null && values.MagicEffects.TryGetValue(fallbackMagicEffect, out effect)):
                if (s_ActionArg.Match(field) is { Success: true } arg)
                {
                    // ModifyParameter 계열은 첫 인자가 능력치 종류라 수치 인자 번호가 하나씩 밀립니다.
                    var index = int.Parse(arg.Groups["index"].Value, CultureInfo.InvariantCulture) +
                                (effect.Action.StartsWith("ModifyParameter", StringComparison.Ordinal) ? 1 : 0);
                    return index < effect.Args.Count ? ParseNumber(effect.Args[index]) : null;
                }
                if (s_ActivationParam.Match(field) is { Success: true } param)
                {
                    var index = int.Parse(param.Groups["index"].Value, CultureInfo.InvariantCulture) - 1;
                    return index >= 0 && index < effect.ActivationParams.Count ? ParseNumber(effect.ActivationParams[index]) : null;
                }
                return field switch { "쿨다운" => effect.Cooldown, "확률" => effect.SuccessRate, _ => null };
            case "Buff" when values.Buffs.TryGetValue(id, out var buff):
                return field switch { "지속시간" => buff.Duration, "최대 중첩 수" => buff.MaxStackCount, "보호막 인자" => buff.ShieldValue, _ => null };
            case "SkillEffect" when values.SkillEffects.TryGetValue(id, out var slots):
                if (s_SkillEffectField.Match(field) is not { Success: true } skill) return null;
                var slotIndex = int.Parse(skill.Groups["slot"].Value, CultureInfo.InvariantCulture) - 1;
                if (slotIndex < 0 || slotIndex >= slots.Count) return null;
                var slot = slots[slotIndex];
                return skill.Groups["field"].Value switch
                {
                    "적용 효과 계수" => slot.Coefficient,
                    "적용 효과 값" => slot.Value,
                    "발동 확률" => slot.Probability,
                    "공용칼럼1" => slot.Reserved1,
                    "공용칼럼2" => slot.Reserved2,
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static double? ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    // 단위: % 비율(×100), u% 부호 없는 비율, s 초, d·h 공격력 계수(×10%), mhp 최대 체력 비율, m 거리.
    private static string Format(double? value, string unit)
    {
        string Number(double v) => value is null ? Unknown : FormatNumber(v);
        var v = value ?? 0;
        return unit switch
        {
            "%" => Number(v * 100) + "%",
            "u%" => Number(Math.Abs(v) * 100) + "%",
            "s" => Number(v) + "초",
            "d" or "h" => $"(공격력x{Number(v * 10)}%)",
            "mhp" => $"최대 체력의 {Number(v * 100)}%",
            "m" => Number(v) + "m",
            _ => Number(v),
        };
    }

    private static string FormatNumber(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join("\n", lines);
    }

    // << 2% * 16 - 2% >>, << (공격력x5.773%) * 25 >> 같은 사칙연산. 해석하지 못하면 null.
    private static string? Evaluate(string expression)
    {
        var parser = new ExpressionParser(expression);
        var result = parser.Parse();
        if (result is not { } r) return null;
        return r.Unit switch
        {
            ExpressionUnit.Attack => $"(공격력x{FormatNumber(r.Value)}%)",
            ExpressionUnit.Percent => FormatNumber(r.Value) + "%",
            ExpressionUnit.Second => FormatNumber(r.Value) + "초",
            _ => FormatNumber(r.Value),
        };
    }

    // 결과 단위는 피연산자 중 가장 구체적인 것을 따릅니다(공격력 계수 > 비율 > 초).
    private enum ExpressionUnit { None, Second, Percent, Attack }

    private readonly record struct Operand(double Value, ExpressionUnit Unit);

    private sealed class ExpressionParser(string text)
    {
        private int m_Position;

        public Operand? Parse()
        {
            try
            {
                var result = ParseSum();
                SkipSpaces();
                return m_Position == text.Length ? result : null;
            }
            catch (FormatException) { return null; }
        }

        private Operand ParseSum()
        {
            var left = ParseProduct();
            while (TryConsume('+', '-') is { } op)
            {
                var right = ParseProduct();
                left = new Operand(op == '+' ? left.Value + right.Value : left.Value - right.Value, Max(left.Unit, right.Unit));
            }
            return left;
        }

        private Operand ParseProduct()
        {
            var left = ParseFactor();
            while (TryConsume('*', '/', '×') is { } op)
            {
                var right = ParseFactor();
                if (op == '/' && right.Value == 0) throw new FormatException();
                left = new Operand(op == '/' ? left.Value / right.Value : left.Value * right.Value, Max(left.Unit, right.Unit));
            }
            return left;
        }

        private Operand ParseFactor()
        {
            SkipSpaces();
            const string attack = "(공격력x";
            if (string.CompareOrdinal(text, m_Position, attack, 0, attack.Length) == 0)
            {
                m_Position += attack.Length;
                var value = ParseNumber();
                Expect('%');
                Expect(')');
                return new Operand(value, ExpressionUnit.Attack);
            }
            if (TryConsume('(') is not null)
            {
                var inner = ParseSum();
                Expect(')');
                return inner;
            }
            if (TryConsume('-') is not null)
            {
                var negated = ParseFactor();
                return negated with { Value = -negated.Value };
            }
            var number = ParseNumber();
            var unit = TryConsume('%') is not null ? ExpressionUnit.Percent
                : TryConsume('초') is not null ? ExpressionUnit.Second
                : ExpressionUnit.None;
            return new Operand(number, unit);
        }

        private double ParseNumber()
        {
            SkipSpaces();
            var start = m_Position;
            while (m_Position < text.Length && (char.IsAsciiDigit(text[m_Position]) || text[m_Position] == '.')) m_Position++;
            if (!double.TryParse(text.AsSpan(start, m_Position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException();
            return value;
        }

        private char? TryConsume(params char[] candidates)
        {
            SkipSpaces();
            if (m_Position < text.Length && candidates.Contains(text[m_Position])) return text[m_Position++];
            return null;
        }

        private void Expect(char c)
        {
            if (TryConsume(c) is null) throw new FormatException();
        }

        private void SkipSpaces()
        {
            while (m_Position < text.Length && char.IsWhiteSpace(text[m_Position])) m_Position++;
        }

        private static ExpressionUnit Max(ExpressionUnit a, ExpressionUnit b) => a > b ? a : b;
    }
}
