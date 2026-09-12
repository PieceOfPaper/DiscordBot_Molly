using System.Text;
using Molly.Runes;

namespace Molly.Quiz;

public enum QuizTopic { Season2RuneEffect, Season2WeaponRuneEffect, Season2ArmorRuneEffect, Season2EmblemRuneEffect }
public sealed record QuizQuestion(string Prompt, string Answer);

public static class QuizQuestions
{
    public static string Description(QuizTopic topic) => topic switch
    {
        QuizTopic.Season2RuneEffect => "시즌2룬효과로이름: 시즌 2 룬의 효과를 보고 룬 이름을 맞혀주세요.",
        QuizTopic.Season2WeaponRuneEffect => "시즌2무기룬효과로이름: 시즌 2 무기 룬의 효과를 보고 룬 이름을 맞혀주세요.",
        QuizTopic.Season2ArmorRuneEffect => "시즌2방어구룬효과로이름: 시즌 2 방어구 룬의 효과를 보고 룬 이름을 맞혀주세요.",
        QuizTopic.Season2EmblemRuneEffect => "시즌2앰블럼룬효과로이름: 시즌 2 앰블럼 룬의 효과를 보고 룬 이름을 맞혀주세요.",
        _ => throw new ArgumentException("지원하지 않는 문제종목입니다.")
    };

    public static IReadOnlyList<QuizQuestion> Pick(QuizTopic topic, IEnumerable<RuneData> runes, int count)
    {
        _ = Description(topic);
        if (count < 1) throw new ArgumentException("문제수는 최소 1개입니다.");
        var category = topic switch
        {
            QuizTopic.Season2WeaponRuneEffect => "무기",
            QuizTopic.Season2ArmorRuneEffect => "방어구",
            QuizTopic.Season2EmblemRuneEffect => "앰블럼",
            _ => null
        };
        var pool = runes.Where(r => r.Season == 2 && (category == null || r.Category == category) && !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Effect))
            .Select(r => new QuizQuestion(r.Effect, r.Name.TrimEnd('+').Trim())).ToArray();
        if (pool.Length == 0) throw new ArgumentException("현재 출제 가능한 룬이 없습니다.");
        count = Math.Min(count, pool.Length);
        Random.Shared.Shuffle(pool);
        return Array.AsReadOnly(pool[..count]);
    }

    public static string NormalizeAnswer(string value)
        => string.Concat(value.Normalize(NormalizationForm.FormC).Where(c => !char.IsWhiteSpace(c) && c != '+')).ToLowerInvariant();
}
