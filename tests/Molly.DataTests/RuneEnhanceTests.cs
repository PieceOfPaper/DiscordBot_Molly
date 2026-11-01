using DiscordBot_Molly.Commands;
using Molly.RuneEnhance;
using Molly.Runes;
using Molly.Unpack;

/// <summary>/룬강화비용: 언팩 비용표, 강화 포인트 규칙, 기댓값 계산 엔진, 입력 검증, 표시.</summary>
internal static class RuneEnhanceTests
{
    // 게임 데이터(3.4.40011)의 RuneEnhance_Tier11 비용표. 누적 포인트·확률은 네 부위가 같습니다.
    private static readonly long[] s_Points =
        [92, 220, 390, 618, 923, 1337, 1884, 2612, 3597, 4950, 7710, 12050, 21707, 42067, 90534, 256134, 546134, 918934, 1415934, 2409934];
    private static readonly double[] s_Probabilities =
        [1, 1, 1, 1, 0.8, 0.7000000000000001, 0.6000000000000001, 0.5, 0.4, 0.30000000000000004, 0.15, 0.1, 0.07, 0.05, 0.03, 0.01, 0.01, 0.01, 0.01, 0.01];
    private static readonly Dictionary<string, (long Gold, int Shards, int Catalysts)[]> s_Costs = new()
    {
        ["RuneEnhance_Tier11_Wea"] =
        [
            (6051, 76, 0), (34104, 107, 0), (45105, 141, 0), (60507, 189, 0), (64908, 203, 0), (77009, 241, 0), (86910, 272, 0), (96811, 303, 0),
            (104512, 327, 0), (107812, 337, 0), (110013, 344, 5), (115513, 361, 5), (179321, 560, 5), (270631, 846, 5), (386145, 1207, 5),
            (440051, 1375, 9), (770089, 2407, 9), (990115, 3094, 9), (1320153, 4125, 9), (2640305, 8251, 9),
        ],
        ["RuneEnhance_Tier11"] =
        [
            (3025, 38, 0), (17052, 53, 0), (22553, 70, 0), (30254, 95, 0), (32454, 101, 0), (38504, 120, 0), (43455, 136, 0), (48406, 151, 0),
            (52256, 163, 0), (53906, 168, 0), (55006, 172, 3), (57757, 180, 3), (89660, 280, 3), (135316, 423, 3), (193072, 603, 3),
            (220025, 688, 5), (385045, 1203, 5), (495057, 1547, 5), (660076, 2063, 5), (1320153, 4125, 5),
        ],
        ["RuneEnhance_Tier11_Emb"] =
        [
            (4556, 57, 0), (25681, 80, 0), (33965, 106, 0), (45563, 142, 0), (48876, 153, 0), (57989, 181, 0), (65444, 205, 0), (72900, 228, 0),
            (78699, 246, 0), (81184, 254, 0), (82841, 259, 4), (86983, 272, 4), (135031, 422, 4), (203789, 637, 4), (290772, 909, 4),
            (331364, 1036, 7), (579886, 1812, 7), (745568, 2330, 7), (994091, 3107, 7), (1988182, 6213, 7),
        ],
    };

    // NEXON NOW 확률 안내 '각인 강화 실패 시 강화 단계 별 강화 포인트(%) 증가량' 표의 ★6 이상 열(+4 ~ +19).
    private static readonly (int Level, string Percent)[] s_OfficialFailTable =
    [
        (4, "40.0"), (5, "35.0"), (6, "29.9"), (7, "25.0"), (8, "20.0"), (9, "15.0"), (10, "7.5"), (11, "5.0"),
        (12, "3.5"), (13, "2.5"), (14, "1.4"), (15, "0.5"), (16, "0.5"), (17, "0.5"), (18, "0.5"), (19, "0.5"),
    ];

    public static async Task RunAsync()
    {
        var data = await DatasetTests();
        RuleTests(data);
        CalculatorTests(data);
        ValidationTests(data);
        DisplayTests(data);
    }

    // ---------- 데이터셋 ----------

    private static string Sheet(string name, IEnumerable<string> rows) => $$"""{"sheet":"{{name}}","rows":[{{string.Join(",", rows)}}]}""";

    private static string RuneRow(string id, string season, string rarity, int tier, string type, string group, int max = 20) =>
        $$"""{"Id":"{{id}}","Rarity":"{{rarity}}","Season":"{{season}}","Tier":{{tier}},"RuneAttachableType":"{{type}}","EnhanceCostGroupId":"{{group}}","CarvingRuneEnhanceMaxLevel":{{max}},"IsOriginal":false}""";

    private static IEnumerable<string> CostRows(string group)
    {
        var costs = s_Costs[group == "RuneEnhance_Tier11_Acc" ? "RuneEnhance_Tier11_Emb" : group];
        for (var i = 0; i < 20; i++)
        {
            var probability = s_Probabilities[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var ingredient = $$"""{"Consumable":"RunePowder_Season1","Amount":{{costs[i].Shards}},"ReturnAmount":{{costs[i].Shards * 3 / 4}}}""";
            var cost = $$"""{"Catalyst":"RuneEnhanceCatalyst","CatalystCount":{{costs[i].Catalysts}},"Gold":{{costs[i].Gold}},"Ingredients":[{{ingredient}}]}""";
            yield return $$"""{"GroupId":"{{group}}","EnhanceStep":{{i + 1}},"EnhancementPoint":{{s_Points[i]}},"SuccessProbability":{{probability}},"Cost":{{cost}},"PurifyIngredientAmountMultiplier":1}""";
        }
    }

    private static Dictionary<string, string> Sheets() => new()
    {
        ["Rune"] = Sheet("Rune",
        [
            RuneRow("Rune_Weapon_v30_Tier10_A01", "Season02", "Legendary", 10, "Weapon", "RuneEnhance_Tier11_Wea"),
            RuneRow("Rune_Weapon_v30_Tier10_A01_P", "Season02", "Legendary", 10, "Weapon", "RuneEnhance_Tier11_Wea"),
            RuneRow("Rune_Weapon_v30_Tier10M", "Season02", "Mythic", 10, "Weapon", "RuneEnhance_Tier11_Wea"),
            RuneRow("Rune_Armor_v30_Tier10", "Season02", "Legendary", 10, "Armor", "RuneEnhance_Tier11"),
            RuneRow("Rune_Emblem_v30_Tier10", "Season02", "Legendary", 10, "Emblem", "RuneEnhance_Tier11_Emb"),
            RuneRow("Rune_Accessory_v30_Tier10", "Season02", "Legendary", 10, "AccessoryAndRing", "RuneEnhance_Tier11_Acc"),
            // 범위 밖: 시즌 2 에픽(★9), 시즌 1 전설, 유니크 — 다른 비용표를 써도 검증에 걸리지 않아야 합니다.
            RuneRow("Rune_Weapon_v30_Tier9", "Season02", "Epic", 9, "Weapon", "RuneEnhance_Tier10_Wea"),
            RuneRow("Rune_Weapon_S1_Tier8", "Season01", "Legendary", 8, "Weapon", "RuneEnhance_Tier9_Wea"),
            RuneRow("Rune_Weapon_Unique", "Season02", "Unique", 10, "Weapon", "RuneEnhance_Tier7_Wea", 0),
        ]),
        ["RuneEnhanceCost"] = Sheet("RuneEnhanceCost",
            new[] { "RuneEnhance_Tier11_Wea", "RuneEnhance_Tier11", "RuneEnhance_Tier11_Emb", "RuneEnhance_Tier11_Acc" }.SelectMany(CostRows)
                .Append("""{"GroupId":"RuneEnhance_Tier10_Wea","EnhanceStep":1,"EnhancementPoint":0,"SuccessProbability":0,"Cost":{}}""")),
        ["Consumable"] = Sheet("Consumable",
        [
            """{"Id":"RunePowder_Season1","DisplayName":"룬의 파편"}""", """{"Id":"RuneEnhanceCatalyst","DisplayName":"강화 재연소 촉매 "}""",
            """{"Id":"Herb","DisplayName":"약초"}""",
        ]),
    };

    private static UnpackSheetFetcher Fetcher(IReadOnlyDictionary<string, string> sheets, List<string>? requested = null) => (sheet, _) =>
    {
        requested?.Add(sheet);
        return Task.FromResult(UnpackSheets.Parse(sheet, System.Text.Encoding.UTF8.GetBytes(sheets[sheet])));
    };

    private static async Task<RuneEnhanceData> DatasetTests()
    {
        var dataset = new UnpackRuneEnhanceDataset();
        var requested = new List<string>();
        var data = await dataset.BuildAsync(Fetcher(Sheets(), requested), default);
        Assert(requested.SequenceEqual(["Rune", "RuneEnhanceCost", "Consumable"]), "룬 각인 강화: 필요한 시트 3개만 받음");
        Assert(data.Season == 2 && data.Tier == 10 && data.ShardName == "룬의 파편" && data.CatalystName == "강화 재연소 촉매",
            "룬 각인 강화: 시즌 2 ★10, 재료·촉매 이름은 Consumable.DisplayName(앞뒤 공백 제거)");
        Assert(data.Tables[RuneCategory.Weapon].GroupId == "RuneEnhance_Tier11_Wea" && data.RuneCounts[RuneCategory.Weapon] == 3,
            "룬 각인 강화: 전설·전설+·신화 무기 룬이 EnhanceCostGroupId로 같은 비용표(Tier11_Wea)에 연결 — Rune.Tier(10)와 표 이름 숫자는 다름");
        Assert(data.Tables.Values.All(t => t.MaxLevel == 20 && t.Steps.Select(s => s.Step).SequenceEqual(Enumerable.Range(1, 20))),
            "룬 각인 강화: 부위별 1~20단계(CarvingRuneEnhanceMaxLevel)");
        Assert(data.Tables[RuneCategory.Weapon].StepFrom(10) is { Step: 11, Gold: 110013, ShardAmount: 344, CatalystCount: 5, CumulativePoint: 7710 }
               && data.Tables[RuneCategory.Weapon].StepFrom(10).SuccessProbability == 0.15,
            "룬 각인 강화: +10 → +11은 EnhanceStep 11 행(15%, 골드 110,013, 파편 344, 촉매 5)");

        var restored = Restore(dataset, data);
        Assert(restored.ShardName == data.ShardName && restored.CatalystName == data.CatalystName
               && restored.Tables.All(t => t.Value.Steps.SequenceEqual(data.Tables[t.Key].Steps) && t.Value.GroupId == data.Tables[t.Key].GroupId)
               && restored.RuneCounts.OrderBy(x => x.Key).SequenceEqual(data.RuneCounts.OrderBy(x => x.Key)),
            "룬 각인 강화: 캐시 저장·복원");

        var conflict = Sheets();
        conflict["Rune"] = conflict["Rune"].Replace("""Rune_Weapon_v30_Tier10M","Rarity":"Mythic","Season":"Season02","Tier":10,"RuneAttachableType":"Weapon","EnhanceCostGroupId":"RuneEnhance_Tier11_Wea""",
            """Rune_Weapon_v30_Tier10M","Rarity":"Mythic","Season":"Season02","Tier":10,"RuneAttachableType":"Weapon","EnhanceCostGroupId":"RuneEnhance_Tier11""");
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(conflict), default),
            "룬 각인 강화: 같은 부위 룬이 서로 다른 비용표를 쓰면 검증 실패(부위만 입력받기 때문)");

        var missingStep = Sheets();
        missingStep["RuneEnhanceCost"] = Sheet("RuneEnhanceCost",
            new[] { "RuneEnhance_Tier11_Wea", "RuneEnhance_Tier11", "RuneEnhance_Tier11_Emb", "RuneEnhance_Tier11_Acc" }
                .SelectMany(CostRows).Where(r => !(r.Contains("\"RuneEnhance_Tier11\"") && r.Contains("\"EnhanceStep\":7,"))));
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(missingStep), default), "룬 각인 강화: 단계가 빠지면 검증 실패");

        var notIncreasing = Sheets();
        notIncreasing["RuneEnhanceCost"] = notIncreasing["RuneEnhanceCost"].Replace("\"EnhancementPoint\":12050", "\"EnhancementPoint\":7710");
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(notIncreasing), default), "룬 각인 강화: 누적 포인트가 늘지 않으면 검증 실패");

        var noName = Sheets();
        noName["Consumable"] = Sheet("Consumable", ["""{"Id":"RunePowder_Season1","DisplayName":"룬의 파편"}"""]);
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(noName), default), "룬 각인 강화: 촉매 이름이 없으면 검증 실패");

        var noEmblem = Sheets();
        noEmblem["Rune"] = noEmblem["Rune"].Replace("\"Season\":\"Season02\",\"Tier\":10,\"RuneAttachableType\":\"Emblem\"", "\"Season\":\"Season01\",\"Tier\":10,\"RuneAttachableType\":\"Emblem\"");
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(noEmblem), default), "룬 각인 강화: 지원 범위 부위 룬이 없으면 검증 실패");
        return data;
    }

    private static RuneEnhanceData Restore(UnpackRuneEnhanceDataset dataset, RuneEnhanceData data)
    {
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer)) dataset.Write(writer, data);
        using var document = System.Text.Json.JsonDocument.Parse(buffer.ToArray());
        return dataset.Read(document.RootElement);
    }

    // ---------- 규칙 ----------

    private static void RuleTests(RuneEnhanceData data)
    {
        var weapon = data.Tables[RuneCategory.Weapon];
        Assert(s_OfficialFailTable.All(row =>
                (RuneEnhanceRules.DisplayTenths(weapon.FailPoints(row.Level), weapon.Width(row.Level)) / 10m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) == row.Percent),
            "실패 포인트 round(폭×성공률÷2)가 공식 확률 안내 표(★6 이상, +4~+19)의 표시값과 모두 일치");
        Assert(Enumerable.Range(0, 4).All(l => weapon.FailPoints(l) == 0), "성공 확률 100% 단계는 실패 포인트가 없음");
        Assert(weapon.Width(13) == 20360 && weapon.FailPoints(13) == 509 && weapon.Width(14) == 48467 && weapon.FailPoints(14) == 727
               && weapon.Width(10) == 2760 && weapon.FailPoints(10) == 207,
            "단계 폭은 누적 포인트 차이(+13: 20,360, +14: 48,467), 실패 포인트 509·727·207");

        // 실측(무기 +14 → +15): 촉매 없이 실패 20.3% → 21.8% → 23.3%, 촉매로 두 번 실패 17.3% → 20.3%.
        long Shown(long points) => RuneEnhanceRules.DisplayTenths(points, weapon.Width(14));
        var start = RuneEnhanceRules.PointsFromTenths(203, weapon.Width(14));
        Assert(Shown(start) == 203 && Shown(start + 727) == 218 && Shown(start + 727 * 2) == 233, "실측: 촉매 없이 실패 1회마다 +1.5%(20.3 → 21.8 → 23.3)");
        Assert(Shown(RuneEnhanceRules.PointsFromTenths(173, weapon.Width(14)) + 727 * 2) == 203, "실측: 촉매로 두 번 실패하면 실패 포인트 2회분(17.3 → 20.3)");
        // 실측(엠블럼 +10 → +11): 촉매 두 번 실패 ×2로 42.1% → 72.1%.
        var emblem = data.Tables[RuneCategory.Emblem];
        Assert(RuneEnhanceRules.DisplayTenths(RuneEnhanceRules.PointsFromTenths(421, emblem.Width(10)) + 207 * 4, emblem.Width(10)) == 721,
            "실측: 엠블럼 +10에서 촉매 두 번 실패 2회 42.1% → 72.1%");
        // 공식 규칙으로는 1차 성공·2차 실패 후 다음 단계 50.6%, 실측은 50.5%(문서에 차이 기록).
        Assert(RuneEnhanceRules.DisplayTenths(RuneEnhanceRules.PointsFromTenths(721, emblem.Width(10)) + 207, emblem.Width(11)) == 506,
            "공식 규칙: 성공 포인트로 100%를 넘긴 초과분 + 실패 포인트가 다음 단계로 이월(72.1% → +11 50.6%, 실측 50.5%)");

        Assert(RuneEnhanceRules.PointsFromTenths(0, 2760) == 0 && RuneEnhanceRules.PointsFromTenths(1000, 2760) == 2760
               && RuneEnhanceRules.PointsFromTenths(999, 2760) == 2758 && RuneEnhanceRules.DisplayTenths(2758, 2760) == 999,
            "진행률 입력은 그 표시가 나오는 가장 작은 포인트로 환산(100%는 폭과 같음)");
    }

    // ---------- 계산 엔진 ----------

    private static void CalculatorTests(RuneEnhanceData data)
    {
        var weapon = data.Tables[RuneCategory.Weapon];
        var armor = data.Tables[RuneCategory.Armor];

        // 성공 확률 100% 구간은 1회 비용의 합.
        var sure = RuneEnhanceCalculator.Estimate(weapon, 0, 0, 4, CatalystPlan.WheneverAvailable);
        Assert(Near(sure.Gold, 6051 + 34104 + 45105 + 60507) && Near(sure.Shards, 76 + 107 + 141 + 189) && Near(sure.Catalysts, 0) && Near(sure.PaidAttempts, 4),
            "성공 확률 100% 구간(+0 → +4)은 단계별 1회 비용의 합");

        // +10 → +11: 실패 포인트 207로 0%에서 14번 실패하면 천장(2,898 ≥ 2,760). 기댓값은 Σ_{k<14} 0.85^k회.
        var single = RuneEnhanceCalculator.Estimate(weapon, 10, 0, 11, CatalystPlan.None);
        var attempts = Enumerable.Range(0, 14).Sum(k => Math.Pow(0.85, k));
        Assert(Near(single.PaidAttempts, attempts) && Near(single.Gold, attempts * 110013) && Near(single.Shards, attempts * 344),
            "+10 → +11은 EnhanceStep 11의 비용·확률을 쓰고, 천장(실패 14회) 뒤 확정 강화는 무료");
        Assert(Math.Abs(single.PaidAttempts - 1 / 0.15) > 0.5, "평균 비용은 1/성공 확률 × 1회 비용과 다름(천장 반영)");

        // 촉매: 두 번 실패 시 414포인트 → 7번 실패하면 천장. 성공 확률 1 − 0.85².
        var catalyst = RuneEnhanceCalculator.Estimate(weapon, 10, 0, 11, CatalystPlan.WheneverAvailable);
        var catalystAttempts = Enumerable.Range(0, 7).Sum(k => Math.Pow(0.85 * 0.85, k));
        Assert(Near(catalyst.PaidAttempts, catalystAttempts) && Near(catalyst.Catalysts, catalystAttempts * 5) && Near(catalyst.Gold, catalystAttempts * 110013),
            "촉매 사용: 한 번 분량 재료로 두 번 판정, 실패 포인트 2회분, 매 시도 촉매 5개");

        // 확정 강화 직전·경계.
        var w = weapon.Width(10);
        Assert(Near(RuneEnhanceCalculator.Estimate(weapon, 10, w, 11, CatalystPlan.None).PaidAttempts, 0), "강화 포인트 100%면 재료 소모 없이 확정 강화");
        Assert(Near(RuneEnhanceCalculator.Estimate(weapon, 10, w - 207, 11, CatalystPlan.None).PaidAttempts, 1),
            "실패 1회로 정확히 100%가 되는 상태는 1회만 비용(실패해도 다음은 무료)");
        Assert(Near(RuneEnhanceCalculator.Estimate(weapon, 10, w - 208, 11, CatalystPlan.None).PaidAttempts, 1 + 0.85),
            "100%까지 실패 포인트보다 1 부족하면 최대 2회 비용");
        Assert(Near(RuneEnhanceCalculator.Estimate(weapon, 10, w - 1, 11, CatalystPlan.WheneverAvailable).Catalysts, 5),
            "100% 직전에도 촉매를 쓸 수 있음(한 번의 실패로 100% 도달 가능한 상황)");

        // 촉매를 쓸 수 없는 구간은 두 방식이 같음.
        var low = RuneEnhanceCalculator.Estimate(weapon, 4, 0, 10, CatalystPlan.None);
        Assert(low == RuneEnhanceCalculator.Estimate(weapon, 4, 0, 10, CatalystPlan.WheneverAvailable) && Near(low.Catalysts, 0),
            "촉매를 쓸 수 없는 구간(+4 → +10)은 촉매 사용 방식도 일반 강화와 같음");

        // 초과분 이월: 100%를 넘긴 상태에서 다음 단계 계산을 이어감.
        var carried = RuneEnhanceCalculator.Estimate(weapon, 10, w + 300, 12, CatalystPlan.None);
        Assert(Near(carried.Gold, RuneEnhanceCalculator.Estimate(weapon, 11, 300, 12, CatalystPlan.None).Gold),
            "100% 초과분은 확정 강화 뒤 다음 단계 포인트로 이월");

        // 독립 참조 구현(누적 포인트 기준, 공식 문구 그대로)과 비교.
        foreach (var (current, startTenths, target) in new[] { (10, 0, 13), (10, 395, 14), (11, 999, 13), (12, 500, 14), (5, 0, 12), (14, 203, 15) })
            foreach (var plan in Enum.GetValues<CatalystPlan>())
            {
                var start = RuneEnhanceRules.PointsFromTenths(startTenths, weapon.Width(current));
                var actual = RuneEnhanceCalculator.Estimate(weapon, current, start, target, plan);
                var reference = new Reference(weapon, target, plan);
                Assert(RelNear(actual.Gold, reference.Expected(current, weapon.CumulativeAt(current) + start, s => s.Gold))
                       && RelNear(actual.Shards, reference.Expected(current, weapon.CumulativeAt(current) + start, s => s.ShardAmount))
                       && RelNear(actual.Catalysts, reference.Expected(current, weapon.CumulativeAt(current) + start, s => plan == CatalystPlan.WheneverAvailable && s.CatalystCount > 0 ? s.CatalystCount : 0)),
                    $"엔진 = 독립 참조 구현(+{current} {startTenths / 10m}% → +{target}, {plan}): 여러 단계 포인트 이월 포함");
            }

        // 작은 모델에서 가능한 모든 결과를 직접 열거한 기댓값과 비교(두 번 성공 시 다음 단계 100% 초과 → 연쇄 확정 강화 포함).
        var tiny = new RuneEnhanceCostTable(RuneCategory.Weapon, "tiny",
        [
            new RuneEnhanceStep(1, 10, 0.5, 100, 1, 0),
            new RuneEnhanceStep(2, 18, 0.4, 200, 2, 1),
            new RuneEnhanceStep(3, 30, 0.3, 300, 3, 2),
        ]);
        Assert(tiny.FailPoints(0) == 3 && tiny.FailPoints(1) == 2 && tiny.FailPoints(2) == 2, "작은 모델 실패 포인트 3·2·2(반올림 0.5 올림 포함)");
        foreach (var plan in Enum.GetValues<CatalystPlan>())
            foreach (var start in new long[] { 0, 4, 9, 10, 13 })
            {
                var enumerated = Enumerate(tiny, 0, start, 3, plan, 1.0);
                var solved = RuneEnhanceCalculator.Estimate(tiny, 0, start, 3, plan);
                Assert(Near(solved.Gold, enumerated.Gold) && Near(solved.Shards, enumerated.Shards) && Near(solved.Catalysts, enumerated.Catalysts),
                    $"작은 모델 전체 결과 열거와 일치(시작 포인트 {start}, {plan})");
            }

        // 부위별 차이: 포인트·확률은 같고 1회 비용만 다름.
        var weaponCost = RuneEnhanceCalculator.Estimate(weapon, 10, 0, 15, CatalystPlan.None);
        var armorCost = RuneEnhanceCalculator.Estimate(armor, 10, 0, 15, CatalystPlan.None);
        Assert(Near(weaponCost.PaidAttempts, armorCost.PaidAttempts) && weaponCost.Gold > armorCost.Gold * 1.9 && weaponCost.Shards > armorCost.Shards * 1.9,
            "부위별 비용 차이: 시도 횟수는 같고 무기가 방어구보다 약 2배");

        // 재현성.
        var again = RuneEnhanceCalculator.Estimate(weapon, 10, 0, 15, CatalystPlan.None);
        Assert(again == weaponCost, "같은 입력·같은 데이터는 같은 결과");

        // 전 구간(+0 → +20)도 계산 가능.
        var full = RuneEnhanceCalculator.Estimate(weapon, 0, 0, 20, CatalystPlan.WheneverAvailable);
        Assert(full.Gold > 0 && double.IsFinite(full.Gold) && full.Gold < RuneEnhanceCalculator.Estimate(weapon, 0, 0, 20, CatalystPlan.None).Gold * 2,
            "+0 → +20 전 구간 계산(촉매 사용)");
    }

    /// <summary>
    /// 공식 문구를 누적 포인트 기준으로 그대로 옮긴 참조 구현. 엔진(단계 안 포인트 배열 DP)과 독립적으로 메모 재귀로 계산합니다.
    /// </summary>
    private sealed class Reference(RuneEnhanceCostTable table, int target, CatalystPlan plan)
    {
        private readonly Dictionary<(int, long), double> m_Memo = [];
        private Func<RuneEnhanceStep, double> m_Cost = _ => 0;

        public double Expected(int level, long exp, Func<RuneEnhanceStep, double> cost)
        {
            m_Memo.Clear();
            m_Cost = cost;
            return Go(level, exp);
        }

        private double Go(int level, long exp)
        {
            // 강화 포인트 100% 이상: 재료 소모 없이 확정 강화, 누적 포인트는 그대로(초과분 이관).
            while (level < target && exp >= table.CumulativeAt(level + 1)) level++;
            if (level >= target) return 0;
            if (m_Memo.TryGetValue((level, exp), out var memo)) return memo;
            var step = table.StepFrom(level);
            var p = step.SuccessProbability;
            var success = table.CumulativeAt(level + 1) - table.CumulativeAt(level);
            var fail = (long)Math.Round(success * p / 2, MidpointRounding.AwayFromZero);
            double value;
            if (p >= 1) value = m_Cost(step) + Go(level + 1, exp + success);
            else if (plan == CatalystPlan.WheneverAvailable && step.CatalystCount > 0)
                value = m_Cost(step) + p * p * Go(level + 1, exp + success * 2) + p * (1 - p) * Go(level + 1, exp + success + fail)
                        + (1 - p) * p * Go(level + 1, exp + fail + success) + (1 - p) * (1 - p) * Go(level, exp + fail * 2);
            else value = m_Cost(step) + p * Go(level + 1, exp + success) + (1 - p) * Go(level, exp + fail);
            m_Memo[(level, exp)] = value;
            return value;
        }
    }

    /// <summary>결과 트리 전체를 메모 없이 열거한 기댓값(작은 모델 전용).</summary>
    private static (double Gold, double Shards, double Catalysts) Enumerate(RuneEnhanceCostTable table, int level, long offset, int target, CatalystPlan plan, double probability)
    {
        while (level < target && offset >= table.Width(level)) { offset -= table.Width(level); level++; }
        if (level >= target) return (0, 0, 0);
        var step = table.StepFrom(level);
        var p = step.SuccessProbability;
        var width = table.Width(level);
        var fail = table.FailPoints(level);
        var useCatalyst = plan == CatalystPlan.WheneverAvailable && step.CatalystCount > 0;
        var total = (Gold: probability * step.Gold, Shards: probability * step.ShardAmount, Catalysts: useCatalyst ? probability * step.CatalystCount : 0.0);
        (double, int, long)[] outcomes = useCatalyst
            ? [(p * p, level, offset + width * 2), (p * (1 - p), level, offset + width + fail), ((1 - p) * p, level, offset + fail + width), ((1 - p) * (1 - p), level, offset + fail * 2)]
            : [(p, level, offset + width), (1 - p, level, offset + fail)];
        foreach (var (chance, nextLevel, nextOffset) in outcomes)
        {
            if (chance <= 0) continue;
            var sub = Enumerate(table, nextLevel, nextOffset, target, plan, probability * chance);
            total = (total.Gold + sub.Gold, total.Shards + sub.Shards, total.Catalysts + sub.Catalysts);
        }
        return total;
    }

    // ---------- 입력 검증 ----------

    private static void ValidationTests(RuneEnhanceData data)
    {
        var weapon = data.Tables[RuneCategory.Weapon];
        Assert(RuneEnhanceRequest.Validate(weapon, 10, 15, null).Request is { Current: 10, Target: 15, StartTenths: null, StartPoints: 0 },
            "강화포인트 생략은 0%");
        Assert(RuneEnhanceRequest.Validate(weapon, 10, 15, 60).Request is { StartTenths: 600, StartPoints: 1656 }
               && RuneEnhanceRequest.Validate(weapon, 10, 15, 39.5).Request is { StartTenths: 395 }, "강화포인트 60% → 0.1% 단위 600, 포인트 1,656");
        Assert(RuneEnhanceRequest.Validate(weapon, 10, 15, 39.55) is { Request: null, IsError: true }, "소수 둘째 자리 입력은 보정하지 않고 거부");
        Assert(RuneEnhanceRequest.Validate(weapon, 10, 15, -0.1) is { IsError: true } && RuneEnhanceRequest.Validate(weapon, 10, 15, 100.1) is { IsError: true }
               && RuneEnhanceRequest.Validate(weapon, 10, 15, double.NaN) is { IsError: true }, "강화포인트 범위 밖·비정상 값 거부");
        Assert(RuneEnhanceRequest.Validate(weapon, 10, 15, 100).Request is { StartTenths: 1000 }, "강화포인트 100%(확정 강화 대기) 허용");
        Assert(RuneEnhanceRequest.Validate(weapon, 15, 10, null) is { IsError: true, Message: var lower } && lower!.Contains("낮아요"), "목표가 현재보다 낮으면 입력 오류");
        Assert(RuneEnhanceRequest.Validate(weapon, 12, 12, null) is { Request: null, IsError: false, Message: var same } && same!.Contains("비용은 없어요"), "현재 = 목표면 추가 비용 0 안내");
        Assert(RuneEnhanceRequest.Validate(weapon, 20, 20, null) is { IsError: false, Message: var max } && max!.Contains("최대 강화"), "최대 강화 상태 안내");
        Assert(RuneEnhanceRequest.Validate(weapon, 20, 20, 10) is { IsError: true }, "최대 강화 단계에 강화포인트를 넣으면 거부");
        Assert(RuneEnhanceRequest.Validate(weapon, -1, 5, null) is { IsError: true } && RuneEnhanceRequest.Validate(weapon, 0, 21, null) is { IsError: true },
            "지원 범위 밖 단계 거부");
        Assert(RuneEnhanceRules.ParsePart("엠블럼") == RuneCategory.Emblem && RuneEnhanceRules.ParsePart("반지") is null, "부위 이름 해석");
    }

    // ---------- 표시 ----------

    private static void DisplayTests(RuneEnhanceData data)
    {
        var request = RuneEnhanceRequest.Validate(data.Tables[RuneCategory.Weapon], 10, 15, null).Request!;
        var result = RuneEnhanceEstimate.Calculate(data, request);
        var embed = RuneEnhanceCostCommand.BuildEmbed(data, result, "R840cbf8_A4525d21");
        Assert(embed.Title == RuneEnhanceCostCommand.Title && embed.Description.Contains("시즌 2 ★10 전설·신화 · 무기")
               && embed.Description.Contains("각인 강화 +10 → +15") && embed.Description.Contains("0% (생략 시 0% 기준)"),
            "표시: 시즌·별·부위, 현재 → 목표, 생략한 강화 포인트는 0% 기준으로 표시");
        var fields = embed.Fields.ToDictionary(f => f.Name, f => f.Value);
        Assert(fields["촉매 미사용"] == $"골드 {RuneEnhanceCostCommand.Gold(result.WithoutCatalyst.Gold):N0}\n룬의 파편 {RuneEnhanceCostCommand.Material(result.WithoutCatalyst.Shards):N1}"
               && fields["촉매 사용"].EndsWith($"강화 재연소 촉매 {RuneEnhanceCostCommand.Material(result.WithCatalyst.Catalysts):N1}"),
            "표시: 골드 정수, 재료·촉매 소수 첫째 자리");
        var diff = result.WithCatalyst - result.WithoutCatalyst;
        Assert(fields["촉매 사용 시 차이"].StartsWith("골드 " + (diff.Gold < 0 ? "−" : "+") + Math.Abs(RuneEnhanceCostCommand.Gold(diff.Gold)).ToString("N0")),
            "표시: 차이는 반올림 전 내부값끼리 뺀 뒤 반올림");
        Assert(fields["계산 기준"].Contains("+10 → +11부터") && fields["계산 기준"].Contains("매번 사용") && fields["계산 기준"].Contains("저주 해제·수리·시세 환산 제외")
               && fields["계산 기준"].Contains("총 예상 소모량") && fields["계산 기준"].Contains("R840cbf8_A4525d21"),
            "표시: 촉매 비교 기준·제외 비용·총량·게임 데이터 패치");
        Assert(embed.Footer!.Value.Text == RuneEnhanceCostCommand.AverageNotice, "표시: 평균값 안내");
        Assert(embed.Length < 6000 && embed.Fields.All(f => f.Value.Length <= 1024), "표시: Discord 길이 제한");

        // 총합은 단계별 표시값의 합이 아니라 내부 합계를 반올림한 값.
        var weapon = data.Tables[RuneCategory.Weapon];
        var stepSum = Enumerable.Range(10, 5).Sum(l => RuneEnhanceCostCommand.Material(RuneEnhanceCalculator.Estimate(weapon, l, 0, l + 1, CatalystPlan.None).Shards));
        Assert(RuneEnhanceCostCommand.Material(result.WithoutCatalyst.Shards) != stepSum,
            "총합은 단계별 0% 시작 표시값을 더한 값이 아님(단계 사이 포인트 이월·내부값 합산)");

        var low = RuneEnhanceEstimate.Calculate(data, RuneEnhanceRequest.Validate(weapon, 5, 9, 100).Request!);
        var lowEmbed = RuneEnhanceCostCommand.BuildEmbed(data, low, "");
        Assert(low.FirstCatalystLevel is null && lowEmbed.Fields.Single(f => f.Name == "계산 기준").Value.Contains("촉매를 쓸 수 없어")
               && lowEmbed.Description.Contains("100% · 확정 강화 대기") && lowEmbed.Fields.Single(f => f.Name == "촉매 사용 시 차이").Value == "골드 0\n룬의 파편 0.0"
               && lowEmbed.Fields.Single(f => f.Name == "계산 기준").Value.Contains("패치 알 수 없음"),
            "표시: 촉매 사용 불가 구간·100% 시작·패치 정보 없음");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6 * Math.Max(1, Math.Abs(expected));
    private static bool RelNear(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9 * Math.Max(1, Math.Abs(expected));

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
