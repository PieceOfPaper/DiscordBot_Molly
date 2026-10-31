using System.Net;
using System.Text.Json;
using Molly.Runes;
using Molly.Unpack;

// 테스트용 룬 표. 한 줄이 "시즌,등급,분류,클래스,이름,효과"입니다(효과에는 쉼표를 써도 됩니다).
internal static class TestRunes
{
    public static RuneTable Table(string lines) => new(lines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select((line, index) =>
    {
        var fields = line.Split(',', 6);
        return new RuneData($"Test_{index}", int.Parse(fields[0]),
            fields[1] switch { "전설" => RuneGrade.Legendary, "신화" => RuneGrade.Mythic, _ => throw new ArgumentException(fields[1]) },
            fields[2] switch
            {
                "무기" => RuneCategory.Weapon, "방어구" => RuneCategory.Armor, "앰블럼" => RuneCategory.Emblem, "장신구" => RuneCategory.Accessory,
                _ => throw new ArgumentException(fields[2]),
            },
            fields[3], fields[4], fields[5]);
    }));
}

internal static class RuneUnpackTests
{
    private const string Token = "github_pat_test-secret";
    private const string FirstCommit = "1111111111111111111111111111111111111111";
    private const string SecondCommit = "2222222222222222222222222222222222222222";
    private const string VersionJson = """
        {"generatedAt":"2026-10-31T16:03:56+09:00","app":{"version":"3.4.40011"},"build":{"branch":"KR3.4"},"patch":{"display":"R840cbf8_A4525d21"}}
        """;

    public static async Task RunAsync()
    {
        RenderTests();
        MessageTests();
        await DatasetTests();
        await CatalogTests();
    }

    private static void RenderTests()
    {
        var values = new RuneTooltipValues();
        values.MagicEffects["Atk"] = new RuneMagicEffectValues("ModifyParameter", ["AttackPowerMultiplier", "0.175"], 0, 100, ["-", "-", "-", "-", "-"]);
        values.MagicEffects["Slow"] = new RuneMagicEffectValues("ModifyParameter", ["MoveSpeed", "-0.15"], 0, 100, []);
        values.MagicEffects["Ult"] = new RuneMagicEffectValues("SkillGroupInstanceDamageAmplification", ["UltimateSkill_Group", "0.2"], 0.5, 35, ["50", "-"]);
        values.MagicEffects["Per"] = new RuneMagicEffectValues("AddParameterPerSourceUnit", ["SkillSpeed", "500", "CriticalDamageAmplification", "0.015", "2000"], 0, 100, []);
        values.Buffs["Stack"] = new RuneBuffValues(15, 12, 0.05);
        values.SkillEffects["Hit"] = [new RuneSkillEffectSlot(100, 14.700000000000001, 3, 7, 0), new RuneSkillEffectSlot(100, 0.141, 0, 0, 0)];

        string Render(string text) => RuneEffectText.Render(text, values);
        Assert(Render("공격력이 $#MagicEffect@개발명(Atk)!액션 인자{0}[%]|0$ 증가") == "공격력이 17.5% 증가",
            "ModifyParameter의 액션 인자{0}은 능력치 종류 다음 수치를 가리키고 |자릿수는 무시");
        Assert(Render("$#MagicEffect@개발명(Ult)!액션 인자{1}[%]$ / $#MagicEffect@개발명(Per)!액션 인자{1}$마다 $#MagicEffect@개발명(Per)!액션 인자{3}[%]$") == "20% / 500마다 1.5%",
            "그 밖의 액션은 인자 번호를 그대로 사용");
        Assert(Render("이동 속도가 $#MagicEffect@개발명(Slow)!액션 인자{0}[u%]$ 감소") == "이동 속도가 15% 감소", "[u%]는 부호 없이 표시");
        Assert(Render("$#MagicEffect@개발명(Ult)!쿨다운[s]$ · $#MagicEffect@개발명(Ult)!확률$ · $#MagicEffect@개발명(Ult)!활성화 조건 인자1$") == "0.5초 · 35 · 50",
            "MagicEffect 쿨다운·확률·활성화 조건 인자");
        Assert(Render("$#Buff@개발명(Stack)!지속시간[s]$ 동안 최대 $#Buff@개발명(Stack)!최대 중첩 수$회, $#Buff@개발명(Stack)!보호막 인자[mhp]$") == "15초 동안 최대 12회, 최대 체력의 5%",
            "Buff 지속시간·최대 중첩 수·보호막");
        Assert(Render("$#SkillEffect@개발명(Hit)!효과1 적용 효과 계수[d]$의 피해, 체력을 $#SkillEffect@개발명(Hit)!효과2 적용 효과 계수[mhp]$ 회복, $#SkillEffect@개발명(Hit)!효과1 적용 효과 값[s]$, $#SkillEffect@개발명(Hit)!효과1 공용칼럼1$")
               == "(공격력x147%)의 피해, 체력을 최대 체력의 14.1% 회복, 3초, 7", "SkillEffect 계수는 공격력 배율, 최대 체력 비율로 표시하고 부동소수 오차 제거");
        var missing = RuneEffectText.Render("전투 중 $#MagicEffect@개발명(없음)!쿨다운[s]$마다 $#SkillEffect@개발명(Hit)!효과9 적용 효과 계수[d]$", values, out var unresolved);
        Assert(missing == "전투 중 ?초마다 (공격력x?%)" && unresolved == 2, "찾지 못한 수치는 '?'로 남기고 개수를 셈");
        var fallback = RuneEffectText.Render("전투 중 $#MagicEffect@개발명(없음)!쿨다운[s]$마다, $#MagicEffect@개발명(atk)!액션 인자{1}[%]$, $#Buff@개발명(없음)!지속시간[s]$", values, out var fallbackMissing, "Ult");
        Assert(fallback == "전투 중 0.5초마다, 20%, ?초" && fallbackMissing == 1,
            "MagicEffect Id가 없으면(대소문자 다름 포함) 옵션의 MagicEffectList[0]으로 대체하고 Buff는 대체하지 않음");
        Assert(Render("$1.3[d]$의 피해를 $0.5[s]|0$마다") == "(공격력x13%)의 피해를 0.5초마다", "템플릿 없이 적힌 숫자 값도 단위대로 표시");
        Assert(Render("<< 4.6 - 0.1 >>초 동안 << 2% * 16 - 2% >>, 최소 <<(공격력x5.773%) * 25 >>, <<90 - 52>>초, << 피해 * 2 >>")
               == "4.5초 동안 30%, 최소 (공격력x144.325%), 38초, 피해 * 2", "<< >> 사칙연산은 계산하고 해석할 수 없으면 식만 남김");
        Assert(Render("  <color=#0FB376>강화 효과:  축복</color>을\r\n\n  부여한다.  ") == "강화 효과: 축복을\n부여한다.", "색상 태그 제거와 공백·빈 줄 정리");
        Assert(RuneEffectText.References("$#Buff@개발명(A)!지속시간[s]$ $#MagicEffect@개발명(B)!쿨다운[s]$").SequenceEqual([("Buff", "A"), ("MagicEffect", "B")]),
            "설명이 참조하는 시트·Id 목록");
    }

    private static void MessageTests()
    {
        RuneData[] runes =
        [
            new("M", 2, RuneGrade.Mythic, RuneCategory.Weapon, "", "죽음", "공격력이 20% 증가한다.\n행동 불능 상태가 될 경우 공격력이 감소한다."),
            new("A", 2, RuneGrade.Legendary, RuneCategory.Accessory, "대검전사", "분노+", "대검전사의 스킬에 변화를 준다."),
        ];
        string Label(string name) => "<:class:1>" + name;
        var mythic = RuneMessages.Build(runes[0], Label);
        Assert(mythic.Title == "⚔️ 죽음" && mythic.Color == RuneMessages.Mythic &&
               mythic.Description == "🟨 **신화** · 무기 룬 · 시즌 2\n\n▸ 공격력이 20% 증가한다.\n▸ 행동 불능 상태가 될 경우 공격력이 감소한다." && mythic.Footer is null,
            "룬 Embed: 부위 이모지 제목, 신화 색, 등급·분류·시즌 줄과 효과 줄마다 표시");
        var accessory = RuneMessages.Build(runes[1], Label, "몰리 • 게임 데이터 패치 P");
        Assert(accessory.Title == "💍 분노+" && accessory.Color == RuneMessages.Legendary &&
               accessory.Description!.StartsWith("🟧 **전설** · 장신구 룬 · 시즌 2 · <:class:1>대검전사 전용\n\n▸ ") && accessory.Footer!.Value.Text == "몰리 • 게임 데이터 패치 P",
            "전용 클래스는 클래스 이모지 표기로, 출처는 footer로 표시");
        Assert(RuneMessages.Header(" 분*노 ", 5) == "🔍 **분\\*노** 룬 검색 결과 **5개**", "검색어의 마크다운 문자는 이스케이프");

        var many = TestRunes.Table(string.Join("\n", Enumerable.Range(0, 23).Select(i => $"2,전설,방어구,,룬 {i},{new string('가', 300)}"))).Items;
        var pages = RuneMessages.BuildPages(many, Label, "출처")!;
        Assert(pages.Count == 3 && pages.Sum(x => x.Count) == 23 && pages.All(p => p.Count <= RuneMessages.MaxEmbedsPerMessage && p.Sum(e => e.Length) <= RuneMessages.MaxCharactersPerMessage) &&
               pages[^1][^1].Footer?.Text == "출처" && pages.SelectMany(x => x).Count(e => e.Footer is not null) == 1,
            "메시지당 Embed 10개·글자 수 한도로 나누고 출처는 마지막 Embed에만");
        var longEffects = TestRunes.Table(string.Join("\n", Enumerable.Range(0, 4).Select(i => $"2,전설,방어구,,긴 룬 {i},{new string('가', 3000)}"))).Items;
        Assert(RuneMessages.BuildPages(longEffects, Label) is { Count: 4 } longPages && longPages.All(p => p.Sum(e => e.Length) <= RuneMessages.MaxCharactersPerMessage), "긴 효과는 글자 수 한도 안에서 메시지를 나눔");
        var tooMany = TestRunes.Table(string.Join("\n", Enumerable.Range(0, 51).Select(i => $"2,전설,장신구,,룬 {i},효과"))).Items;
        Assert(RuneMessages.BuildPages(tooMany, Label) is null, "메시지가 5개를 넘으면 목록·파일로 대신하도록 null");
        var summary = RuneMessages.BuildSummary("룬", tooMany);
        Assert(summary.Title == "🔍 '룬' 룬 51개" && summary.Description!.Contains("**시즌 2 · 💍 장신구** (51개)") && summary.Description.Contains("룬 50"),
            "결과가 많으면 시즌·부위별 이름 목록 Embed");
    }

    private static readonly Dictionary<string, string> s_Sheets = new()
    {
        ["Rune"] = """
            {"sheet":"Rune","align":"exact","rowCount":7,"rows":[
             {"Index":0,"Id":"W1","Rarity":"Legendary","Season":"Season02","CombatJobSheetId":0,"IsOriginal":false,"RuneAttachableType":"Weapon",
              "Option1":{"RuneOptionSheetId":"Atk","RuneOptionPoolSheetGroupId":0},"Option2":{"RuneOptionSheetId":0,"RuneOptionPoolSheetGroupId":"Pool"},
              "Option3":{"RuneOptionSheetId":0,"RuneOptionPoolSheetGroupId":0},"Option4":{"RuneOptionSheetId":"W1","RuneOptionPoolSheetGroupId":0}},
             {"Index":1,"Id":"W1_Original","Rarity":"Legendary","Season":"Season02","CombatJobSheetId":0,"IsOriginal":true,"RuneAttachableType":"Weapon",
              "Option1":{"RuneOptionSheetId":"W1_Old","RuneOptionPoolSheetGroupId":0}},
             {"Index":2,"Id":"W2","Rarity":"Epic","Season":"Season02","CombatJobSheetId":0,"IsOriginal":false,"RuneAttachableType":"Weapon",
              "Option1":{"RuneOptionSheetId":"W1","RuneOptionPoolSheetGroupId":0}},
             {"Index":3,"Id":"A1","Rarity":"Mythic","Season":"Season02","CombatJobSheetId":0,"IsOriginal":false,"RuneAttachableType":"Armor",
              "Option4":{"RuneOptionSheetId":"A1","RuneOptionPoolSheetGroupId":0}},
             {"Index":4,"Id":"E1","Rarity":"Legendary","Season":"Season01","CombatJobSheetId":0,"IsOriginal":false,"RuneAttachableType":"Emblem",
              "Option4":{"RuneOptionSheetId":"E1","RuneOptionPoolSheetGroupId":0}},
             {"Index":5,"Id":"C1","Rarity":"Legendary","Season":"Season02","CombatJobSheetId":"FireMage","IsOriginal":false,"RuneAttachableType":"AccessoryAndRing",
              "Option4":{"RuneOptionSheetId":"C1","RuneOptionPoolSheetGroupId":0}},
             {"Index":6,"Id":"N1","Rarity":"Legendary","Season":"Season02","CombatJobSheetId":0,"IsOriginal":false,"RuneAttachableType":"Weapon",
              "Option4":{"RuneOptionSheetId":"C1","RuneOptionPoolSheetGroupId":0}}]}
            """,
        ["Socketable"] = """
            {"sheet":"Socketable","rows":[
             {"Id":"W1","DisplayName":"무기 룬: 눈부신 잔영","RuneSheetId":"W1","JewelSheetId":0},
             {"Id":"W1_Original","DisplayName":"무기 룬: 눈부신 잔영","RuneSheetId":"W1_Original","JewelSheetId":0},
             {"Id":"A1","DisplayName":"방어구 룬: 쐐기돌","RuneSheetId":"A1","JewelSheetId":0},
             {"Id":"E1","DisplayName":"엠블럼 룬: 백금 천칭","RuneSheetId":"E1","JewelSheetId":0},
             {"Id":"C1","DisplayName":"장신구 룬: 폭염+","RuneSheetId":"C1","JewelSheetId":0},
             {"Id":"Jewel","DisplayName":"보석","RuneSheetId":0,"JewelSheetId":"Ruby"}]}
            """,
        ["CombatJob"] = """{"sheet":"CombatJob","rows":[{"Id":"FireMage","DisplayName":"화염술사"},{"Id":"Mage","DisplayName":"견습 마법사"}]}""",
        ["RuneOption"] = """
            {"sheet":"RuneOption","rows":[
             {"Id":"Atk","Description":""},
             {"Id":"W1","Description":"스킬 사용 시 <color=#0FB376>$#SkillEffect@개발명(W1)!효과1 적용 효과 계수[d]$</color>의 피해를 주고,\n공격 속도가 $#MagicEffect@개발명(W1_Speed)!액션 인자{0}[%]$ 증가한다."},
             {"Id":"W1_Old","Description":"이전 효과"},
             {"Id":"A1","MagicEffectList":["A1_Tick"],"Description":"$#MagicEffect@개발명(A1)!쿨다운[s]$마다 $#Buff@개발명(A1_Buff)!지속시간[s]$ 동안 최대 $#Buff@개발명(A1_Buff)!최대 중첩 수$회"},
             {"Id":"E1","Description":"공격력이 $#MagicEffect@개발명(E1_Ult)!액션 인자{1}[u%]|0$ 증가한다."},
             {"Id":"C1","Description":"화염술사의 스킬에 변화를 준다."}]}
            """,
        ["MagicEffect"] = """
            {"sheet":"MagicEffect","rows":[
             {"Id":"W1_Speed","Activation":{"Params1":"-"},"Execution":{"SuccessRate":100,"Cooldown":0.0},"Action":["ModifyParameter",["DefaultAttackSpeed","0.1"]]},
             {"Id":"E1_Ult","Activation":{"Params1":"-"},"Execution":{"SuccessRate":100,"Cooldown":0.0},"Action":["SkillGroupInstanceDamageAmplification",["G","-0.2"]]},
             {"Id":"A1_Tick","Activation":{"Params1":"-"},"Execution":{"SuccessRate":100,"Cooldown":6.0},"Action":["AttachBuff",["A1_Buff"]]},
             {"Id":"Other","Action":["DoNothing",[]]}]}
            """,
        ["Buff"] = """{"sheet":"Buff","rows":[{"Id":"A1_Buff","Duration":15.0,"MaxStackCount":3,"ShieldValue":0}]}""",
        ["SkillEffect"] = """
            {"sheet":"SkillEffect","rows":[{"Id":"W1","Effect1":{"probability":100,"coefficient":14.700000000000001,"value":0,"reserved1":0,"reserved2":0}}]}
            """,
    };

    private static UnpackSheetFetcher Fetcher(IReadOnlyDictionary<string, string> sheets, List<string>? requested = null) => (sheet, _) =>
    {
        requested?.Add(sheet);
        return Task.FromResult(UnpackSheets.Parse(sheet, System.Text.Encoding.UTF8.GetBytes(sheets[sheet])));
    };

    private static async Task DatasetTests()
    {
        var dataset = new UnpackRuneDataset();
        var requested = new List<string>();
        var table = await dataset.BuildAsync(Fetcher(s_Sheets, requested), default);
        Assert(table.Items.Select(x => x.Id).SequenceEqual(["W1", "A1", "C1", "E1"]),
            "전설·신화만 담고 이전 버전(IsOriginal)·이름 없는 룬은 제외, 최신 시즌부터 무기·방어구·앰블럼·장신구 순");
        var weapon = table.ById["W1"];
        Assert(weapon is { Season: 2, Grade: RuneGrade.Legendary, Category: RuneCategory.Weapon, Class: "", Name: "눈부신 잔영" } &&
               weapon.Effect == "스킬 사용 시 (공격력x147%)의 피해를 주고,\n공격 속도가 10% 증가한다.",
            "Socketable 이름의 '무기 룬: ' 접두어를 떼고 옵션 설명 수치를 채움");
        Assert(table.ById["A1"] is { Grade: RuneGrade.Mythic, Effect: "6초마다 15초 동안 최대 3회" } &&
               table.ById["E1"] is { Season: 1, Category: RuneCategory.Emblem, Name: "백금 천칭", Effect: "공격력이 20% 증가한다." } &&
               table.ById["C1"] is { Class: "화염술사", Name: "폭염+", Category: RuneCategory.Accessory },
            "신화 등급·시즌 번호·엠블럼 분류·전용 클래스(CombatJob) 변환");
        Assert(table.Warnings.Any(x => x.Contains("N1") && x.Contains("이름")), "이름이 없는 룬은 경고로 남김");
        Assert(requested.SequenceEqual(["Rune", "Socketable", "CombatJob", "RuneOption", "MagicEffect", "Buff", "SkillEffect"]), "필요한 시트만 차례로 받음");

        var noEmblem = new Dictionary<string, string>(s_Sheets) { ["Rune"] = s_Sheets["Rune"].Replace("\"Emblem\"", "\"Weapon\"") };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(noEmblem), default), "분류 하나가 통째로 비면 검증 실패");
        var unresolved = new Dictionary<string, string>(s_Sheets) { ["Buff"] = """{"sheet":"Buff","rows":[]}""" };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(unresolved), default), "수치를 찾지 못한 룬이 많으면 검증 실패");
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(new Dictionary<string, string>(s_Sheets) { ["Rune"] = """{"sheet":"Skill","rows":[]}""" }), default),
            "다른 시트 파일은 거부");

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) dataset.Write(writer, table);
        using var document = JsonDocument.Parse(buffer.ToArray());
        var restored = dataset.Read(document.RootElement);
        Assert(restored.Items.SequenceEqual(table.Items), "캐시 저장·복원 시 룬 정보 보존");

        var search = RuneSearch.Format(RuneSearch.Find(table, "눈부신"));
        Assert(search.Contains("눈부신 잔영 · 시즌 2 · 전설 · 무기") && search.Contains("\n스킬 사용 시"), "검색 결과에 시즌·등급·분류와 여러 줄 효과 표시");
    }

    private static async Task CatalogTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "molly-rune-unpack-tests-" + Guid.NewGuid().ToString("N"));
        var logs = new List<string>();
        try
        {
            var commit = FirstCommit;
            var sheets = new Dictionary<string, string>(s_Sheets);
            var failSheets = false;
            var handler = new StubHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/commits", StringComparison.Ordinal))
                    return Json($"[{{\"sha\":\"{commit}\"}}]");
                if (uri.AbsolutePath.EndsWith("/contents/version.json", StringComparison.Ordinal)) return Json(VersionJson);
                const string prefix = "/contents/Patch/Sheetdata/client/";
                var index = uri.AbsolutePath.IndexOf(prefix, StringComparison.Ordinal);
                if (index >= 0 && !failSheets)
                {
                    var sheet = Path.GetFileNameWithoutExtension(uri.AbsolutePath[(index + prefix.Length)..]);
                    if (sheets.TryGetValue(sheet, out var body)) return Json(body);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };
            });
            var client = new UnpackRepoClient(new UnpackRepoOptions { Token = Token }, handler, log: msg => logs.Add(msg));
            UnpackDataCatalog<RuneTable> Create(UnpackRepoClient c, IUnpackDataset<RuneTable>? dataset = null, string? directory = null) =>
                new(c, dataset ?? new UnpackRuneDataset(), directory ?? dir, msg => logs.Add(msg));

            var offline = Create(new UnpackRepoClient(new UnpackRepoOptions(), handler));
            await offline.InitializeAsync();
            await offline.RunAsync(default);
            Assert(offline.Data is null && handler.Requests == 0 && UnpackDataMessages.NoData(offline.Name).Contains("룬 데이터가 없어서"),
                "토큰이 없고 캐시도 없으면 데이터 없음(요청 0회)으로 명령이 안내");

            var catalog = Create(client);
            await catalog.InitializeAsync();
            var first = await catalog.RefreshAsync();
            Assert(first is { IsSuccess: true, Changed: true } && catalog.Data!.Items.Count == 4 &&
                   catalog.Current is { Commit: FirstCommit, PatchDisplay: "R840cbf8_A4525d21", AppVersion: "3.4.40011" } &&
                   File.Exists(Path.Combine(dir, "unpack", "runes.json")),
                "시트 폴더의 마지막 커밋 기준으로 룬을 만들고 캐시에 저장");
            Assert(handler.Paths.Any(x => x.Contains("/commits") && x.Contains("path=Patch%2FSheetdata%2Fclient") && x.Contains("per_page=1")) &&
                   handler.Paths.Where(x => x.Contains("/contents/")).All(x => x.EndsWith("ref=" + FirstCommit, StringComparison.Ordinal)),
                "변경 확인은 시트 폴더 커밋 1회, 파일은 같은 커밋 기준으로 받음");

            var before = handler.Requests;
            var unchanged = await catalog.RefreshAsync();
            Assert(unchanged is { IsSuccess: true, Changed: false } && handler.Requests == before + 1, "커밋이 같으면 확인 요청 1회만 하고 다시 받지 않음");

            var restarted = Create(client);
            await restarted.InitializeAsync();
            before = handler.Requests;
            Assert(restarted.Data!.Items.SequenceEqual(catalog.Data!.Items) && handler.Requests == before, "재시작 후 저장소 요청 없이 캐시로 복구");
            before = handler.Requests;
            Assert(await restarted.RefreshAsync() is { IsSuccess: true, Changed: true } && handler.Requests > before + 1 &&
                   await restarted.RefreshAsync() is { Changed: false },
                "재시작 후 첫 확인은 같은 커밋이어도 새로 만들고, 그다음부터는 커밋이 바뀔 때만 만듦");

            var original = catalog.Current;
            commit = SecondCommit;
            sheets["Rune"] = """{"sheet":"Rune","rows":[]}""";
            Assert(await catalog.RefreshAsync() is { IsSuccess: false, Status: UnpackRepoStatus.InvalidResponse } && ReferenceEquals(original, catalog.Current),
                "새 커밋의 시트가 검증에 실패하면 마지막 정상 데이터 유지");
            sheets["Rune"] = s_Sheets["Rune"];
            failSheets = true;
            Assert(await catalog.RefreshAsync() is { IsSuccess: false, Status: UnpackRepoStatus.NotFound } && ReferenceEquals(original, catalog.Current),
                "시트를 받지 못하면 마지막 정상 데이터 유지");
            failSheets = false;
            var blocked = Path.Combine(dir, "blocked");
            await File.WriteAllTextAsync(blocked, "not a directory");
            var unwritable = Create(client, directory: blocked);
            Assert(await unwritable.RefreshAsync() is { IsSuccess: false } && unwritable.Data is null, "캐시 저장에 실패하면 새 데이터를 공개하지 않음");
            Assert(await catalog.RefreshAsync() is { IsSuccess: true, Changed: true } && catalog.Current!.Commit == SecondCommit, "복구되면 새 커밋으로 교체");

            await File.WriteAllTextAsync(Path.Combine(dir, "unpack", "runes.json"), "{broken");
            var broken = Create(new UnpackRepoClient(new UnpackRepoOptions(), handler));
            await broken.InitializeAsync();
            Assert(broken.Data is null, "손상된 캐시는 버리고 데이터 없음으로 시작");
            Assert(logs.All(x => !x.Contains(Token)), "로그에 토큰을 남기지 않음");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests++;
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
