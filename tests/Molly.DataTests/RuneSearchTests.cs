using System.Text;
using Molly.Runes;

// /룬 이름·효과 통합 검색: 검색식 해석, 시즌·부위 필터, 검색용 효과 문장(수치 표현 제외), 정렬.
internal static class RuneSearchTests
{
    public static Task RunAsync()
    {
        QueryTests();
        SearchTextTests();
        FilterTests();
        return Task.CompletedTask;
    }

    private static RuneQuery Query(string text) => RuneQuery.Parse(text, out var error) ?? throw new Exception($"'{text}' 해석 실패: {error}");

    private static string[] Names(IReadOnlyList<RuneData> runes) => runes.Select(x => x.Name).ToArray();

    // 원본 설명(템플릿 그대로)으로 검색용 효과를 만든 룬. 표시 문장은 검색에 쓰지 않음을 확인하려고 따로 받습니다.
    private static RuneData Raw(string name, string display, string description, int season = 2, RuneCategory category = RuneCategory.Armor) =>
        new($"Raw_{name}", season, RuneGrade.Legendary, category, "", name, display) { SearchEffect = RuneEffectText.SearchText(description) };

    private static void QueryTests()
    {
        // 이름 '공격력'·효과 '치명타'·'보호막'으로 우선순위를 구분합니다.
        bool Eval(string query, string name, params string[] effect) =>
            Query(query).Matches(RuneQuery.Normalize(name), effect.Select(RuneQuery.Normalize).ToArray());
        Assert(Eval("공격력 OR 치명타 AND 보호막", "공격력", "없음") && !Eval("공격력 OR 치명타 AND 보호막", "없음", "치명타") &&
               !Eval("(공격력 OR 치명타) AND 보호막", "공격력", "없음") && Eval("(공격력 OR 치명타) AND 보호막", "없음", "치명타", "보호막"),
            "AND가 OR보다 먼저 계산되고 괄호로 우선순위를 바꿈");
        Assert(Eval("((공격력 OR (치명타 AND 회복)) AND (보호막 OR 방어력))", "없음", "치명타", "회복", "방어력") &&
               !Eval("((공격력 OR (치명타 AND 회복)) AND (보호막 OR 방어력))", "없음", "치명타", "방어력"),
            "중첩 괄호");
        Assert(Eval("공격력 and 치명타", "공격력", "치명타") && Eval("공격력 Or 없음", "공격력", "") && Eval("공격력 AnD 치명타 oR 없음", "x", "공격력", "치명타"),
            "AND·OR는 영문 대소문자를 구분하지 않음");
        Assert(Query("ORB AND BRAND OR ORDER").Terms.SequenceEqual(["ORB", "BRAND", "ORDER"]) && Query("공격력AND치명타").Terms.SequenceEqual(["공격력AND치명타"]) &&
               Eval("android", "Android 룬", ""),
            "단어 안의 AND·OR 글자는 연산자가 아니고 검색어의 일부");
        Assert(Query("공격 속도").Terms.SequenceEqual(["공격속도"]) && Eval("공격 속도", "", "공격 속도가") && Eval("공격  속도", "", "공격속도가"),
            "연산자 없이 이어진 여러 단어는 띄어쓰기를 무시한 검색어 하나");
        Assert(Query("(공격력)AND(치명타)").Terms.SequenceEqual(["공격력", "치명타"]) && Eval("(공격력)AND(치명타)", "공격력", "치명타"),
            "괄호 앞뒤는 단어 경계라 붙여 써도 연산자를 인식");
        Assert(Eval("분노 AND 보호막", "거대한 분노", "보호막을 얻는다") && !Eval("분노 AND 보호막", "거대한 분노", "피해를 준다"),
            "이름에 첫 검색어, 효과에 두 번째 검색어가 있어도 AND를 만족");
        Assert(Query(" 분노 ").Text == "분노" && Query("분노".Normalize(NormalizationForm.FormD)).Terms.SequenceEqual(["분노"]),
            "검색식 앞뒤 공백 제거와 유니코드 정규화");

        (string Input, string Reason)[] invalid =
        [
            ("", "검색어를 입력"), ("   ", "검색어를 입력"), ("　\t", "검색어를 입력"),
            ("공격력 AND", "뒤에 검색어가 없어요"), ("AND 공격력", "앞에 검색어가 없어요"), ("공격력 OR OR 치명타", "연속"),
            ("()", "빈 괄호"), ("(공격력 OR 치명타", "닫히지 않은 괄호"), ("공격력)", "여는 괄호 없이"), (")(", "여는 괄호 없이"),
            ("공격력 (치명타 OR 보호막)", "괄호 사이에 AND 또는 OR"), ("(공격력) 치명타", "괄호 사이에 AND 또는 OR"),
            ("(AND 공격력)", "앞에 검색어가 없어요"), ("(공격력 OR)", "뒤에 검색어가 없어요"), ("OR", "앞에 검색어가 없어요"),
            (new string('가', RuneQuery.MaxLength + 1), "너무 길어요"),
            (string.Join(" OR ", Enumerable.Range(0, RuneQuery.MaxTerms + 1).Select(i => $"a{i}")), "너무 많아요"),
            (new string('(', RuneQuery.MaxDepth + 1) + "a" + new string(')', RuneQuery.MaxDepth + 1), "너무 깊어요"),
        ];
        foreach (var (input, reason) in invalid)
            Assert(RuneQuery.Parse(input, out var error) is null && error.Contains(reason), $"잘못된 검색식 '{input[..Math.Min(input.Length, 20)]}' → {reason}");
        Assert(RuneQuery.Parse(null, out _) is null, "누락된 검색어 거부");
        Assert(RuneQuery.Parse(new string('(', RuneQuery.MaxDepth) + "a" + new string(')', RuneQuery.MaxDepth), out _) is not null &&
               RuneQuery.Parse(string.Join(" OR ", Enumerable.Range(0, RuneQuery.MaxTerms).Select(i => $"a{i}")), out _) is not null,
            "제한까지의 괄호 깊이·검색어 수는 허용");
    }

    private static void SearchTextTests()
    {
        string Search(string description) => RuneEffectText.SearchText(description);
        Assert(Search("공격력이 $#MagicEffect@개발명(Atk)!액션 인자{0}[%]$ 증가한다.") == "공격력이\n증가한다.",
            "수치 템플릿 자리를 지우고 일반 문장의 '공격력'은 유지");
        Assert(Search("적에게 <color=#0FB376>$#SkillEffect@개발명(Hit)!효과1 적용 효과 계수[d]$</color>의 피해를 준다.") == "적에게\n의 피해를 준다.",
            "'(공격력x…)'로 렌더링되는 계수 템플릿은 '공격력' 글자까지 지우고 색상 태그 제거");
        Assert(Search("$#MagicEffect@개발명(P)!확률$% 확률로 $#Buff@개발명(B)!지속시간[s]$ 동안 보호막을 얻는다.") == "확률로\n동안 보호막을 얻는다.",
            "템플릿 뒤에 붙은 % 기호와 초 단위 수치를 지움");
        Assert(Search("체력을 $#SkillEffect@개발명(H)!효과1 적용 효과 계수[mhp]$ 회복") == "체력을\n회복" &&
               Search("최대 체력의 10% 만큼 보호막") == "만큼 보호막" &&
               Search("$1.3[d]$의 피해를 $0.5[s]|0$마다") == "의 피해를\n마다",
            "'최대 체력의 N%'로 렌더링되는 템플릿과 숫자 값 템플릿도 통째로 지움");
        Assert(Search("<< $#SkillEffect@개발명(D)!효과1 적용 효과 계수[d]$ * $#SkillEffect@개발명(C)!효과1 적용 효과 값$ >>의 피해") == "의 피해" &&
               Search("공격력이 << 2% * 16 - 2% >> 증가") == "공격력이\n증가",
            "<< >> 계산식은 안의 템플릿·연산자까지 통째로 지움");
        Assert(Search("공격 적중 시, 35% 확률로 5초 동안 공격력 증가 효과를 얻는다. 주위에서 적이 5/10/20명 처치될 경우") ==
               "공격 적중 시,\n확률로\n동안 공격력 증가 효과를 얻는다. 주위에서 적이\n명 처치될 경우",
            "설명에 직접 적힌 숫자와 슬래시로 이은 수치도 지우고 일반 문장의 '공격력 증가'는 유지");
        Assert(Search("변환된 전력 수치 × $#SkillEffect@개발명(D)!효과1 적용 효과 계수[d]$만큼 피해") == "변환된 전력\n만큼 피해",
            "수치와 곱해지는 '수치 ×' 피연산자도 지움");
        Assert(Search("피해량이 단계마다 1.5% 증가한다\n초월 각인 시, 최대 $#Buff@개발명(B)!최대 중첩 수$\nm 단위") == "피해량이 단계마다\n증가한다\n초월 각인 시, 최대\nm 단위",
            "수치를 지운 자리가 아닌 원래 줄 첫머리의 '초'·'m'은 단위로 보지 않음");
        Assert(Search("(재사용 대기 시간: $#MagicEffect@개발명(A)!쿨다운[s]$)\n(중첩 시 효과 강화)") == "(재사용 대기 시간:\n(중첩 시 효과 강화)",
            "일반 괄호와 그 안의 설명은 지우지 않음");
        Assert(RuneEffectText.SearchTextFromDisplay("적에게 (공격력x147%/155.5%/164%)의 피해, 최대 체력의 5% 회복, ?초마다 공격 속도가 10%/12%/14% 증가") ==
               "적에게\n의 피해,\n회복,\n마다 공격 속도가\n증가",
            "표시 문장으로 추정할 때도 공격력 계수·초월 수치·찾지 못한 수치를 지움");

        var runes = new RuneTable([
            Raw("보호의 룬", "25% 확률로 5초 동안 보호막을 얻는다.", "$#MagicEffect@개발명(P)!확률$% 확률로 $#Buff@개발명(B)!지속시간[s]$ 동안 보호막을 얻는다."),
            Raw("타격의 룬", "적에게 (공격력x12.5%)의 피해를 준다.", "적에게 $#SkillEffect@개발명(Hit)!효과1 적용 효과 계수[d]$의 피해를 준다."),
            Raw("성장의 룬", "공격력이 24%/26%/28% 증가한다.", "공격력이 $#MagicEffect@개발명(Atk)!액션 인자{0}[%]$ 증가한다."),
            Raw("25번째 룬", "치명타 확률이 3% 증가한다.", "치명타 확률이 $#MagicEffect@개발명(C)!액션 인자{0}[%]$ 증가한다."),
            Raw("냉정의 룬", "(재사용 대기 시간: 10초)\n방어력이 증가한다.", "(재사용 대기 시간: $#MagicEffect@개발명(A)!쿨다운[s]$)\n방어력이 증가한다."),
        ]);
        string[] Find(string query) => Names(RuneSearch.Find(runes, Query(query), 2));
        Assert(Find("25").SequenceEqual(["25번째 룬"]), "효과 수치 25로는 검색되지 않고 룬 이름의 숫자는 그대로 검색");
        Assert(Find("공격력").SequenceEqual(["성장의 룬"]), "'(공격력x12.5%)'의 공격력으로는 검색되지 않고 '공격력이 증가한다'는 검색");
        Assert(Find("공격력이 증가").Length == 0 && Find("적에게의").Length == 0 && Find("확률로동안").Length == 0,
            "지운 수치 표현 앞뒤의 단어를 이어 붙여 검색되지 않음");
        Assert(Find("24%").Length == 0 && Find("28").Length == 0 && Find("12.5").Length == 0 && Find("5초").Length == 0,
            "초월 수치와 계수는 검색에서 제외");
        Assert(RuneSearch.Find(runes, Query("성장"), 2)[0].Effect == "공격력이 24%/26%/28% 증가한다.", "표시용 효과는 초월 수치 병기를 그대로 유지");
        Assert(Find("재사용 대기 시간").SequenceEqual(["냉정의 룬"]) && RuneQuery.Parse("(재사용", out _) is null && Find("방어력").SequenceEqual(["냉정의 룬"]),
            "일반 괄호 안 설명은 검색되고 괄호 문자는 검색식의 괄호로 해석");
        Assert(Find("보호막 OR 피해").SequenceEqual(["보호의 룬", "타격의 룬"]), "효과 일치 OR 검색");
    }

    private static void FilterTests()
    {
        var table = TestRunes.Table(
            "2,전설,무기,전사,거대한 분노,공격력이 20% 증가한다.\n" +
            "2,전설,방어구,궁수,분노의 힘,분노 상태에서 보호막을 얻는다.\n" +
            "2,전설,방어구,,평온,분노가 증가한다.\n" +
            "2,신화,엠블럼,,침착,공격 속도가 증가한다.\n" +
            "2,전설,장신구,,Ancient Orb,치명타 피해가 증가한다.\n" +
            "1,전설,방어구,,보호의 장막,보호막을 얻는다.\n" +
            "0,전설,방어구,,태초의 보호막,보호막을 얻는다.\n" +
            "0,전설,무기,,태초의 분노,분노를 얻는다.");
        string[] Find(string query, int season = 2, RuneCategory? category = null) => Names(RuneSearch.Find(table, Query(query), season, category));

        Assert(Find("분노").SequenceEqual(["거대한 분노", "분노의 힘", "평온"]), "기본 시즌 2만 검색하고 이름·효과 일치를 함께 찾음");
        Assert(Find("보호막", 0).SequenceEqual(["태초의 보호막"]) && Find("보호막", 1).SequenceEqual(["보호의 장막"]), "시즌 0·1도 해당 시즌만 검색");
        Assert(Find("분노", category: RuneCategory.Armor).SequenceEqual(["분노의 힘", "평온"]) &&
               Find("분노", category: RuneCategory.Weapon).SequenceEqual(["거대한 분노"]) &&
               Find("공격 속도", category: RuneCategory.Emblem).SequenceEqual(["침착"]) &&
               Find("치명타", category: RuneCategory.Accessory).SequenceEqual(["Ancient Orb"]) && Find("침착", category: RuneCategory.Weapon).Length == 0,
            "부위별 필터와 시즌·부위·검색식 AND 조건");
        Assert(Find("분노 OR 보호막").SequenceEqual(["거대한 분노", "분노의 힘", "평온"]) && Find("분노의 힘 OR 보호막").SequenceEqual(["분노의 힘"]),
            "이름과 효과에 모두 일치해도 한 번만 표시");
        Assert(Find("평온 OR 분노").SequenceEqual(["거대한 분노", "분노의 힘", "평온"]) && Find("증가").SequenceEqual(["거대한 분노", "평온", "침착", "Ancient Orb"]) &&
               Find("공격 OR 침착").SequenceEqual(["침착", "거대한 분노"]) && Find("힘 OR 증가").SequenceEqual(["분노의 힘", "거대한 분노", "평온", "침착", "Ancient Orb"]),
            "검색어 하나라도 이름에 든 룬을 먼저, 나머지는 효과 일치 순서로 각 묶음 안은 기존 순서 유지");
        Assert(Find("ancient orb").SequenceEqual(["Ancient Orb"]) && Find("ORB AND 치명타").SequenceEqual(["Ancient Orb"]) &&
               Find("거대한분노").SequenceEqual(["거대한 분노"]) && Find("분 노 의 힘").SequenceEqual(["분노의 힘"]) &&
               Find("분노".Normalize(NormalizationForm.FormD)).Length == 3,
            "영문 대소문자·띄어쓰기·유니코드 정규화를 무시");
        Assert(Find("분노 AND 보호막").SequenceEqual(["분노의 힘"]) && Find("거대한 AND 공격력").SequenceEqual(["거대한 분노"]),
            "이름과 효과에 나뉘어 있는 AND 조건");
        Assert(Find("없는말").Length == 0, "검색 결과 없음");

        var text = RuneSearch.Format(RuneSearch.Find(table, Query("분노"), 2), "검색어: 분노 · " + RuneSearch.Conditions(2, null));
        Assert(text.StartsWith("검색어: 분노 · 시즌 2 · 전체 부위\n룬 검색 결과: 3개") && text.Contains("거대한 분노 · 시즌 2 · 전설 · 무기 · 전사") &&
               text.Contains("\n분노가 증가한다.") && !text.Contains("\n\n\n"),
            "전체 결과 파일에 적용 조건과 표시용 효과를 함께 적음");
        Assert(RuneNames.ParseCategory(" 방어구 ") == RuneCategory.Armor && RuneNames.ParseCategory("엠블럼") == RuneCategory.Emblem &&
               RuneNames.ParseCategory("반지") is null, "부위 옵션 이름 변환");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
