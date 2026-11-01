using System.Text;

namespace Molly.Runes;

/// <summary>
/// /룬 검색식. 검색어를 AND·OR(영문 대소문자 무시)와 괄호로 묶고, AND가 OR보다 먼저 계산됩니다.
/// 연산자는 공백으로 나뉜 독립 단어일 때만 연산자이고(괄호 앞뒤는 단어 경계), 연산자 없이 이어진 단어는 검색어 하나입니다('공격 속도').
/// 암묵적 AND·NOT·와일드카드·따옴표는 지원하지 않으며, 입력은 문자열 비교에만 쓰고 정규식이나 코드로 실행하지 않습니다.
/// </summary>
public sealed class RuneQuery
{
    public const int MaxLength = 100;
    public const int MaxTerms = 10;
    public const int MaxDepth = 5;
    public const string Example = "(공격력 OR 치명타) AND 보호막";

    private abstract record Node;
    private sealed record Term(string Text) : Node;
    private sealed record And(Node Left, Node Right) : Node;
    private sealed record Or(Node Left, Node Right) : Node;

    private enum TokenKind { Term, And, Or, Open, Close }
    private readonly record struct Token(TokenKind Kind, string Text = "");

    private readonly Node m_Root;

    /// <summary>입력한 그대로(앞뒤 공백 제외). 결과 머리말에 씁니다.</summary>
    public string Text { get; }
    /// <summary>검색식에 쓰인 개별 검색어(정규화 후). 이름 일치 우선 정렬에 씁니다.</summary>
    public IReadOnlyList<string> Terms { get; }

    private RuneQuery(string text, Node root, IReadOnlyList<string> terms)
    {
        Text = text;
        m_Root = root;
        Terms = terms;
    }

    /// <summary>검색식을 해석합니다. 문법이 틀리면 null과 사용자에게 보여줄 짧은 원인을 돌려줍니다.</summary>
    public static RuneQuery? Parse(string? input, out string error)
    {
        error = "";
        var text = (input ?? "").Normalize(NormalizationForm.FormC).Trim();
        if (text.Length == 0)
        {
            error = "검색어를 입력해주세요.";
            return null;
        }
        if (text.Length > MaxLength)
        {
            error = $"검색식이 너무 길어요. {MaxLength}자 이하로 입력해주세요.";
            return null;
        }
        var tokens = Tokenize(text);
        if (Validate(tokens) is { } problem)
        {
            error = problem;
            return null;
        }
        var terms = tokens.Where(t => t.Kind == TokenKind.Term).Select(t => t.Text).ToArray();
        if (terms.Length > MaxTerms)
        {
            error = $"검색어가 너무 많아요. 검색어는 {MaxTerms}개까지 쓸 수 있어요.";
            return null;
        }
        var position = 0;
        var root = ParseOr(tokens, ref position);
        return new RuneQuery(text, root, terms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>검색어 하나라도 <paramref name="target"/>(정규화한 문자열)에 들어 있는지.</summary>
    public bool AnyTermIn(string target) => Terms.Any(term => target.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 검색어마다 <paramref name="name"/> 또는 <paramref name="segments"/> 중 하나에 들어 있는지로 검색식을 계산합니다.
    /// 인자는 <see cref="Normalize"/>한 문자열이어야 합니다.
    /// </summary>
    public bool Matches(string name, IReadOnlyList<string> segments) => Evaluate(m_Root, name, segments);

    /// <summary>검색어와 검색 대상에 같이 쓰는 정규화: 유니코드 NFC, 모든 공백 제거. 영문 대소문자는 비교할 때 무시합니다.</summary>
    public static string Normalize(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormC).Where(c => !char.IsWhiteSpace(c)));

    private static bool Evaluate(Node node, string name, IReadOnlyList<string> segments) => node switch
    {
        Term term => name.Contains(term.Text, StringComparison.OrdinalIgnoreCase) ||
                     segments.Any(segment => segment.Contains(term.Text, StringComparison.OrdinalIgnoreCase)),
        And and => Evaluate(and.Left, name, segments) && Evaluate(and.Right, name, segments),
        Or or => Evaluate(or.Left, name, segments) || Evaluate(or.Right, name, segments),
        _ => throw new InvalidOperationException(),
    };

    // 괄호는 언제나 따로 떼고, 나머지는 공백으로 나눈 단어 중 AND·OR만 연산자로 봅니다. 연산자 사이의 단어들은 검색어 하나로 묶습니다.
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var words = new List<string>();
        void FlushTerm()
        {
            if (words.Count == 0) return;
            tokens.Add(new Token(TokenKind.Term, Normalize(string.Concat(words))));
            words.Clear();
        }
        var word = new StringBuilder();
        void FlushWord()
        {
            if (word.Length == 0) return;
            var value = word.ToString();
            word.Clear();
            if (value.Equals("AND", StringComparison.OrdinalIgnoreCase) || value.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                FlushTerm();
                tokens.Add(new Token(value.Equals("AND", StringComparison.OrdinalIgnoreCase) ? TokenKind.And : TokenKind.Or));
            }
            else words.Add(value);
        }
        foreach (var c in text)
        {
            if (c is '(' or ')')
            {
                FlushWord();
                FlushTerm();
                tokens.Add(new Token(c == '(' ? TokenKind.Open : TokenKind.Close));
            }
            else if (char.IsWhiteSpace(c)) FlushWord();
            else word.Append(c);
        }
        FlushWord();
        FlushTerm();
        return tokens;
    }

    // 앞뒤 토큰 짝으로 문법 오류를 찾습니다. 통과하면 ParseOr가 실패하지 않습니다.
    private static string? Validate(IReadOnlyList<Token> tokens)
    {
        if (tokens.Count == 0) return "검색어를 입력해주세요.";
        var depth = 0;
        Token? previous = null;
        foreach (var token in tokens)
        {
            var prev = previous?.Kind;
            switch (token.Kind)
            {
                case TokenKind.And or TokenKind.Or:
                    if (prev is null or TokenKind.Open) return "AND·OR 앞에 검색어가 없어요.";
                    if (prev is TokenKind.And or TokenKind.Or) return "AND·OR가 연속으로 있어요. 연산자 사이에 검색어를 넣어주세요.";
                    break;
                case TokenKind.Open:
                    if (prev is TokenKind.Term or TokenKind.Close) return "검색어와 괄호 사이에 AND 또는 OR가 필요해요.";
                    if (++depth > MaxDepth) return $"괄호가 너무 깊어요. 괄호는 {MaxDepth}단계까지 겹칠 수 있어요.";
                    break;
                case TokenKind.Close:
                    if (depth == 0) return "여는 괄호 없이 닫는 괄호 `)`가 있어요.";
                    if (prev is TokenKind.Open) return "빈 괄호 `()`가 있어요. 괄호 안에 검색어를 넣어주세요.";
                    if (prev is TokenKind.And or TokenKind.Or) return "AND·OR 뒤에 검색어가 없어요.";
                    depth--;
                    break;
                case TokenKind.Term:
                    if (prev is TokenKind.Close) return "검색어와 괄호 사이에 AND 또는 OR가 필요해요.";
                    break;
            }
            previous = token;
        }
        if (previous?.Kind is TokenKind.And or TokenKind.Or) return "AND·OR 뒤에 검색어가 없어요.";
        if (depth > 0) return "닫히지 않은 괄호 `(`가 있어요.";
        return null;
    }

    private static Node ParseOr(IReadOnlyList<Token> tokens, ref int position)
    {
        var left = ParseAnd(tokens, ref position);
        while (position < tokens.Count && tokens[position].Kind == TokenKind.Or)
        {
            position++;
            left = new Or(left, ParseAnd(tokens, ref position));
        }
        return left;
    }

    private static Node ParseAnd(IReadOnlyList<Token> tokens, ref int position)
    {
        var left = ParsePrimary(tokens, ref position);
        while (position < tokens.Count && tokens[position].Kind == TokenKind.And)
        {
            position++;
            left = new And(left, ParsePrimary(tokens, ref position));
        }
        return left;
    }

    private static Node ParsePrimary(IReadOnlyList<Token> tokens, ref int position)
    {
        var token = tokens[position++];
        if (token.Kind == TokenKind.Term) return new Term(token.Text);
        var inner = ParseOr(tokens, ref position);
        position++; // 닫는 괄호(Validate가 짝을 확인함)
        return inner;
    }
}
