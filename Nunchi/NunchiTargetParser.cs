using System.Text.RegularExpressions;

namespace Molly.Nunchi;

public static partial class NunchiTargetParser
{
    public static IReadOnlyList<ulong> ParseMentions(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : UserMentionPattern().Matches(text)
                .Select(match => ulong.Parse(match.Groups[1].Value))
                .Distinct()
                .ToArray();

    [GeneratedRegex("<@!?(\\d{17,20})>", RegexOptions.CultureInvariant)]
    private static partial Regex UserMentionPattern();
}
