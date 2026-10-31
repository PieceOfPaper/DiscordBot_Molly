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

    /// <summary>초월 단계의 값으로 바꾼 사본. 담겨 있지 않은 행(설명이 참조하지 않는 행)은 건너뜁니다.</summary>
    public RuneTooltipValues With(IEnumerable<RuneValueOverride> overrides)
    {
        var copy = new RuneTooltipValues();
        foreach (var (id, effect) in MagicEffects) copy.MagicEffects[id] = effect;
        foreach (var (id, buff) in Buffs) copy.Buffs[id] = buff;
        foreach (var (id, slots) in SkillEffects) copy.SkillEffects[id] = slots;
        foreach (var o in overrides)
        {
            switch (o.Column)
            {
                case RuneValueOverride.MagicEffectActionParams when copy.MagicEffects.TryGetValue(o.Id, out var effect):
                    copy.MagicEffects[o.Id] = effect with { Args = o.Value.Split(',') };
                    break;
                case RuneValueOverride.BuffDuration when copy.Buffs.TryGetValue(o.Id, out var buff) && ParseNumber(o.Value) is { } duration:
                    copy.Buffs[o.Id] = buff with { Duration = duration };
                    break;
                case var column when column.StartsWith("SkillEffect_", StringComparison.Ordinal) &&
                                     copy.SkillEffects.TryGetValue(o.Id, out var slots) && o.Slot >= 1 && o.Slot <= slots.Count &&
                                     ParseNumber(o.Value) is { } value:
                    var list = slots.ToArray();
                    var slot = list[o.Slot - 1];
                    list[o.Slot - 1] = column switch
                    {
                        "SkillEffect_Probability" => slot with { Probability = value },
                        "SkillEffect_Coefficient" => slot with { Coefficient = value },
                        "SkillEffect_Value" => slot with { Value = value },
                        "SkillEffect_Reserved1" => slot with { Reserved1 = value },
                        "SkillEffect_Reserved2" => slot with { Reserved2 = value },
                        _ => slot,
                    };
                    copy.SkillEffects[o.Id] = list;
                    break;
            }
        }
        return copy;
    }

    private static double? ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// RuneTranscend의 TargetN 하나. 초월 단계에서 바뀌는 시트 값이며 Value는 그 단계의 최종 값입니다(기본값에 더하지 않음).
/// MagicEffect_Action_Params는 인자 전체('AttackPowerMultiplier,0.22'), SkillEffect_*의 Slot은 EffectN 번호(sheetColumnOption)입니다.
/// </summary>
public sealed record RuneValueOverride(string Sheet, string Id, string Column, int Slot, string Value)
{
    public const string MagicEffectActionParams = "MagicEffect_Action_Params";
    public const string BuffDuration = "Buff_Duration";

    public static readonly IReadOnlySet<string> SupportedColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        MagicEffectActionParams, BuffDuration,
        "SkillEffect_Probability", "SkillEffect_Coefficient", "SkillEffect_Value", "SkillEffect_Reserved1", "SkillEffect_Reserved2",
    };
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

    /// <summary>
    /// 기본 값과 초월 단계별 값으로 각각 만든 문장을 하나로 합칩니다. 단계마다 다른 수치만 '24%/26%/28%'처럼 슬래시로 잇습니다.
    /// </summary>
    /// <param name="levels">[0]은 기본 값, 이후는 1초월부터 차례로. 찾지 못한 수치 개수는 기본 값 기준입니다.</param>
    public static string Render(string description, IReadOnlyList<RuneTooltipValues> levels, out int unresolved, string? fallbackMagicEffect = null)
    {
        var texts = new string[levels.Count];
        unresolved = 0;
        for (var i = 0; i < levels.Count; i++)
        {
            texts[i] = Render(description, levels[i], out var missing, fallbackMagicEffect);
            if (i == 0) unresolved = missing;
        }
        return MergeLevels(texts);
    }

    // 숫자와 바로 뒤 단위(%·초·m)를 한 덩어리로 봅니다.
    private static readonly Regex s_Number = new(@"(-?\d+(?:\.\d+)?(?:%|초|m)?)", RegexOptions.CultureInvariant);

    /// <summary>
    /// 같은 설명을 단계별 값으로 만든 문장들을 합칩니다. 숫자 밖의 글자가 단계마다 다르면 합칠 수 없어 기본 문장([0])을 그대로 씁니다.
    /// </summary>
    public static string MergeLevels(IReadOnlyList<string> texts)
    {
        if (texts.Count == 1 || texts.All(t => t == texts[0])) return texts[0];
        var parts = texts.Select(t => s_Number.Split(t)).ToArray();
        // Split은 캡처한 숫자를 홀수 번째에 둡니다.
        if (parts.Any(p => p.Length != parts[0].Length) ||
            Enumerable.Range(0, parts[0].Length).Where(i => i % 2 == 0).Any(i => parts.Any(p => p[i] != parts[0][i])))
            return texts[0];
        return string.Concat(Enumerable.Range(0, parts[0].Length).Select(i =>
            i % 2 == 0 || parts.All(p => p[i] == parts[0][i]) ? parts[0][i] : string.Join("/", parts.Select(p => p[i]))));
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
