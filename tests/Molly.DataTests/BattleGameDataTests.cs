using System.Text.Json;
using Molly.Battle;
using Molly.Unpack;

/// <summary>
/// 배틀 스킬 원본(게임 데이터 CombatJob·SkillSlot·Skill) 데이터셋과, 이것으로 클래스 구성·스킬 이름·스킬구분을 만드는 배틀 로더 규칙.
/// 시트는 석궁사수 사본(<see cref="CrossbowBattleTests.Sheets"/>)을 고쳐 쓴다.
/// </summary>
internal static class BattleGameDataTests
{
    public static async Task RunAsync()
    {
        await DatasetTests();
        CompositionTests();
        NameTests();
        VirtualSkillTests();
        RejectTests();
    }

    private static async Task DatasetTests()
    {
        var dataset = new UnpackBattleGameDataset();
        var requested = new List<string>();
        var sheets = new Dictionary<string, string>
        {
            ["CombatJob"] = """
                {"rows":[
                  {"Id":"Arbalist","DisplayName":"석궁사수","CombatJobType":"FirstAdvanceCombatJob","ShowClass":true,"Order":9},
                  {"Id":"HighWarrior","DisplayName":" 전사 ","CombatJobType":"FirstAdvanceCombatJob","ShowClass":true,"Order":3},
                  {"Id":"Barbarian","DisplayName":"바바리안","CombatJobType":"FirstAdvanceCombatJob","ShowClass":false,"Order":5},
                  {"Id":"Warrior","DisplayName":"견습 전사","CombatJobType":"BasicCombatJob","ShowClass":true,"Order":0}
                ]}
                """,
            ["SkillSlot"] = """
                {"rows":[
                  {"RuneSeason":"Season00","CombatJobId":"HighWarrior","SlotIndex":"Slot01","SkillModdingId":0,"SkillId":"ExpertWarrior_RapidSlash","UIOrder":1},
                  {"RuneSeason":"Season01","CombatJobId":"HighWarrior","SlotIndex":"Slot01","SkillModdingId":3,"SkillId":"ExpertWarrior_RapidSlash","UIOrder":1},
                  {"RuneSeason":"Season00","CombatJobId":"HighWarrior","SlotIndex":"DefaultAttack","SkillModdingId":0,"SkillId":"ExpertWarrior_DefaultAttack","UIOrder":999},
                  {"RuneSeason":"Season02","CombatJobId":"Arbalist","SlotIndex":"PassiveSlot06","SkillModdingId":0,"SkillId":"Arbalist_Equilibrium","UIOrder":1},
                  {"RuneSeason":"Season00","CombatJobId":"Warrior","SlotIndex":"Slot01","SkillModdingId":0,"SkillId":"NoviceWarrior_RapidSlash","UIOrder":1},
                  {"RuneSeason":"Season00","CombatJobId":"Barbarian","SlotIndex":"Slot01","SkillModdingId":0,"SkillId":"Barbarian_Smash","UIOrder":1}
                ]}
                """,
            ["Skill"] = """
                {"rows":[
                  {"Id":"ExpertWarrior_RapidSlash","DisplayName":"연속 베기"},
                  {"Id":"ExpertWarrior_DefaultAttack","DisplayName":"기본 공격"},
                  {"Id":"Arbalist_Equilibrium","DisplayName":"포츈 볼트"},
                  {"Id":"NoviceWarrior_RapidSlash","DisplayName":"연속 베기"},
                  {"Id":"Unused_Skill","DisplayName":"슬롯 없음"}
                ]}
                """
        };
        var data = await dataset.BuildAsync(Sheets(sheets, requested), default);
        Assert(requested.SequenceEqual(["CombatJob", "SkillSlot", "Skill"]), "배틀 스킬 원본은 CombatJob·SkillSlot·Skill 시트만 받음");
        Assert(data.Classes.Select(x => (x.CombatJobId, x.Name)).SequenceEqual([("HighWarrior", "전사"), ("Arbalist", "석궁사수")]),
            "배틀 스킬 원본 클래스는 공개된 전직 클래스만 CombatJob.Order 순서로 담고 이름 앞뒤 공백을 지움");
        Assert(data.Skills.Keys.Order(StringComparer.Ordinal).SequenceEqual(["Arbalist_Equilibrium", "ExpertWarrior_RapidSlash"]),
            "배틀 스킬 원본 스킬은 공개 전직 클래스의 Slot01~06·PassiveSlot01~06에 든 것만 담음(기본 공격·견습·미공개 클래스 제외)");
        Assert(data.Skills["ExpertWarrior_RapidSlash"].Slots.SequenceEqual([new BattleGameSlot("HighWarrior", "Slot01")])
            && data.Skills["Arbalist_Equilibrium"].Slots.Single() is { IsPassive: true, Slot: "PassiveSlot06" },
            "시즌·룬 변형으로 같은 슬롯이 여러 번 나와도 한 번만 담고, 시즌 2 패시브 슬롯도 담음");
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer)) dataset.Write(writer, data);
            using var cached = JsonDocument.Parse(buffer.ToArray());
            var read = dataset.Read(cached.RootElement);
            Assert(read.Classes.SequenceEqual(data.Classes) && read.Skills.Count == data.Skills.Count
                && read.Skills.Values.All(x => data.Skills[x.Id].Name == x.Name && data.Skills[x.Id].Slots.SequenceEqual(x.Slots)), "배틀 스킬 원본 캐시 저장·읽기 왕복");
        }
        try
        {
            await dataset.BuildAsync(Sheets(new Dictionary<string, string>(sheets) { ["SkillSlot"] = """{"rows":[]}""" }), default);
            throw new Exception("공개 클래스의 스킬 슬롯이 하나도 없으면 거부");
        }
        catch (InvalidDataException) { Console.WriteLine("PASS 공개 클래스의 스킬 슬롯이 하나도 없으면 거부"); }
    }

    private static void CompositionTests()
    {
        var data = Parse(CrossbowBattleTests.Sheets);
        var crossbow = data.Classes["crossbowman"];
        Assert(data.IsClassBattleReady("crossbowman") && crossbow.Name == "석궁사수"
            && crossbow.SkillIds.SequenceEqual(["Arbalist_MountingShock", "Arbalist_ShockExplosion", "Arbalist_SlipThrough", "Arbalist_GustingVolt", "Arbalist_SpreadingVolt", "Arbalist_BoltExplosion"])
            && crossbow.PassiveIds.SequenceEqual(["Arbalist_ExtraAction", "Arbalist_DrivingForce", "Arbalist_Critical", "Arbalist_QuickAttack", "Arbalist_ReinforcedBolt", "Arbalist_Equilibrium"]),
            "클래스 구성은 게임 데이터 슬롯 순서(Slot01~06·PassiveSlot01~06)로 배틀스킬·배틀패시브 ID를 배정하고, 몰리 클래스 ID·이름을 씀");
        Assert(data.Classes.Count == 21 && !data.IsClassBattleReady("warrior") && data.Classes["warrior"].SkillIds.Count == 0,
            "배틀스킬이 없는 공개 클래스도 캐릭터 등록용으로 담지만 배틀 가능 클래스는 아님");
        Assert(data.Skills["Arbalist_BoltExplosion"].Kind == "궁극기" && data.Skills["Arbalist_MountingShock"].Kind == "일반",
            "부모가 없는 배틀스킬은 궁극기여부로 궁극기·일반을 나눔");

        // 헬 파이어에 부모를 지정하면 파생이 되어 Slot06이 빈다: 배틀 데이터는 거부하지 않고 클래스만 배틀에서 빠진다.
        var warnings = new List<string>();
        var partial = Parse(WithSkillColumns(CrossbowBattleTests.Sheets, "Arbalist_BoltExplosion", "Arbalist_MountingShock", ""), warnings.Add);
        Assert(partial.Skills["Arbalist_BoltExplosion"].Kind == "파생" && !partial.IsClassBattleReady("crossbowman")
            && warnings.Any(x => x.Contains("석궁사수") && x.Contains("5/6") && x.Contains("Slot06")),
            "부모스킬ID가 있으면 궁극기여부와 관계없이 파생이고, 슬롯이 일부만 채워진 클래스는 경고와 함께 배틀에서 빠짐");

        var renamedJobs = new BattleGameData(BattleTestGame.Data.Classes.Append(new BattleGameClass("Barbarian", "바바리안")), BattleTestGame.Data.Skills.Values);
        warnings.Clear();
        var unmapped = BattleCatalog.Parse(CrossbowBattleTests.Sheets, DateTimeOffset.UtcNow, renamedJobs, warn: warnings.Add);
        Assert(unmapped.Classes.Count == 21 && warnings.Any(x => x.Contains("바바리안") && x.Contains("ClassIcons")),
            "몰리 클래스 ID 매핑이 없는 새 공개 클래스는 경고하고 제외함");

        var sharedSlot = new BattleGameData(BattleTestGame.Data.Classes, BattleTestGame.Data.Skills.Values.Select(x => x.Id == "Arbalist_ShockExplosion" ? x with { Slots = [new BattleGameSlot("Arbalist", "Slot01")] } : x));
        Reject(() => BattleCatalog.Parse(CrossbowBattleTests.Sheets, DateTimeOffset.UtcNow, sharedSlot), "한 클래스의 같은 슬롯에 배틀스킬이 둘이면 거부");
    }

    private static void NameTests()
    {
        var data = Parse(CrossbowBattleTests.Sheets);
        Assert(data.Skills["Arbalist_MountingShock"].Name == "버스터 샷", "배틀스킬 이름은 게임 데이터 Skill.DisplayName을 씀");
        var renamed = Parse(WithTable(CrossbowBattleTests.Sheets, "배틀이름교체", "ID,표시이름,비고\nArbalist_MountingShock,버스터 샷: 강화,테스트\n"));
        Assert(renamed.Skills["Arbalist_MountingShock"].Name == "버스터 샷: 강화" && renamed.Skills["Arbalist_ShockExplosion"].Name == "쇼크 익스플로전",
            "배틀이름교체 시트의 표시이름이 게임 데이터 이름을 대신함");
        Reject(() => Parse(WithTable(CrossbowBattleTests.Sheets, "배틀이름교체", "ID,표시이름,비고\nArbalist_Typo,오타,\n")), "배틀이름교체 ID가 게임 데이터에 없으면 거부");
        Reject(() => Parse(WithTable(CrossbowBattleTests.Sheets, "배틀이름교체", "ID,표시이름,비고\nExpertWarrior_RapidSlash,연속 베기,\n")), "배틀이름교체 ID가 배틀스킬에 없으면 거부");
        Reject(() => Parse(WithTable(CrossbowBattleTests.Sheets, "배틀이름교체", "ID,표시이름,비고\nArbalist_MountingShock,,\n")), "배틀이름교체 표시이름이 비어 있으면 거부");
        Reject(() => Parse(WithSkillColumns(CrossbowBattleTests.Sheets, "Arbalist_MountingShock", "", "다른 이름")), "게임 데이터에 있는 배틀스킬에 이름 칸을 채우면 거부");
    }

    private static void VirtualSkillTests()
    {
        // 애드리브처럼 게임 데이터에 없는 버튼 스킬: 이름은 배틀스킬 이름 칸에서, 슬롯은 파생 스킬에서 물려받는다.
        var sheets = WithSkillColumns(AddButton(CrossbowBattleTests.Sheets, "Molly_Test_Button", "볼트 선택"), "Arbalist_SpreadingVolt", "Molly_Test_Button", "");
        var data = Parse(sheets);
        Assert(data.Skills["Molly_Test_Button"] is { Name: "볼트 선택", Kind: "일반" } && data.Skills["Arbalist_SpreadingVolt"].Kind == "파생"
            && data.IsClassBattleReady("crossbowman") && data.Classes["crossbowman"].SkillIds[4] == "Molly_Test_Button",
            "게임 데이터에 없는 배틀 전용 스킬은 이름 칸의 이름을 쓰고 파생 스킬의 슬롯(Slot05)을 물려받음");
        Reject(() => Parse(AddButton(CrossbowBattleTests.Sheets, "Molly_Test_Button", "볼트 선택")), "배틀 전용 스킬에 파생 스킬이 없으면 거부");
        Reject(() => Parse(AddButton(CrossbowBattleTests.Sheets, "Molly_Test_Button", "")), "게임 데이터에 없는 배틀스킬에 이름이 없으면 거부");
        Reject(() => Parse(WithSkillColumns(CrossbowBattleTests.Sheets, "Arbalist_SpreadingVolt", "Molly_Missing", "")), "부모스킬ID가 배틀스킬에 없으면 거부");
    }

    private static void RejectTests()
    {
        Reject(() => BattleCatalog.Parse(CrossbowBattleTests.Sheets, DateTimeOffset.UtcNow, null), "게임 데이터 배틀 스킬 원본이 없으면 거부(마지막 정상 스냅샷 유지)");
        Reject(() => Parse(Replace(CrossbowBattleTests.Sheets, "배틀패시브효과", ",Arbalist_SpreadingVolt,", ",Arbalist_Typo,")), "다음행동지정의 스킬 ID가 배틀스킬에 없으면 거부");
        Reject(() => Parse(Replace(CrossbowBattleTests.Sheets, "배틀패시브", "\nArbalist_ExtraAction,", "\nArbalist_MountingShock,")), "배틀패시브 ID가 게임 데이터 패시브가 아니면 거부");
    }

    private static BattleDataSnapshot Parse(IReadOnlyDictionary<string, string> sheets, Action<string>? warn = null)
        => BattleCatalog.Parse(sheets, DateTimeOffset.UtcNow, BattleTestGame.Data, warn: warn);

    private static Dictionary<string, string> WithTable(IReadOnlyDictionary<string, string> sheets, string name, string csv)
        => new(sheets) { [name] = csv };

    private static Dictionary<string, string> Replace(IReadOnlyDictionary<string, string> sheets, string name, string from, string to)
    {
        if (!sheets[name].Contains(from, StringComparison.Ordinal)) throw new Exception($"테스트 준비: {name}에 '{from}'가 없음");
        return WithTable(sheets, name, sheets[name].Replace(from, to, StringComparison.Ordinal));
    }

    /// <summary>배틀스킬 행의 마지막 두 칸(부모스킬ID·이름)을 바꾼다. 석궁사수 사본은 한 행이 한 줄이다.</summary>
    private static Dictionary<string, string> WithSkillColumns(IReadOnlyDictionary<string, string> sheets, string id, string parent, string name)
    {
        var lines = sheets["배틀스킬"].Split('\n');
        var index = Array.FindIndex(lines, x => x.StartsWith(id + ",", StringComparison.Ordinal));
        if (index < 0 || !lines[index].EndsWith(",,", StringComparison.Ordinal)) throw new Exception($"테스트 준비: 배틀스킬 {id} 행을 찾지 못함");
        lines[index] = lines[index][..^2] + $",{parent},{name}";
        return WithTable(sheets, "배틀스킬", string.Join('\n', lines));
    }

    /// <summary>스프레딩 볼트 행을 복사해 효과 없는 버튼 스킬 행을 더한다.</summary>
    private static Dictionary<string, string> AddButton(IReadOnlyDictionary<string, string> sheets, string id, string name)
    {
        var line = sheets["배틀스킬"].Split('\n').First(x => x.StartsWith("Arbalist_SpreadingVolt,", StringComparison.Ordinal));
        return WithTable(sheets, "배틀스킬", sheets["배틀스킬"].TrimEnd('\n') + "\n" + id + line["Arbalist_SpreadingVolt".Length..^2] + $",,{name}\n");
    }

    private static UnpackSheetFetcher Sheets(IReadOnlyDictionary<string, string> sheets, List<string>? requested = null) => (sheet, _) =>
    {
        requested?.Add(sheet);
        return sheets.TryGetValue(sheet, out var json)
            ? Task.FromResult(JsonDocument.Parse(json))
            : throw new UnpackDataException(UnpackRepoStatus.NotFound, sheet + " 없음");
    };

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
