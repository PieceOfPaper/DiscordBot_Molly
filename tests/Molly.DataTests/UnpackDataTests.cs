using System.Net;
using System.Text.Json;
using Molly.Classes;
using Molly.HaeyeonCost;
using Molly.MagicResistance;
using Molly.Unpack;

// 게임 데이터(2026-10-31 저장소 기준)와 같은 마도저항 기준값. 마도저항·해연 비용 테스트가 함께 씁니다.
internal static class TestMagicResistance
{
    public static MagicResistanceData Data { get; } = new(3,
    [
        new("지옥1", 3_500, 4_400), new("지옥2", 6_000, 6_600), new("지옥3", 7_200, 7_800), new("지옥4", 8_500, 9_000),
    ],
    new Dictionary<ArcaneEquipmentPart, EquipmentResistanceRange>
    {
        [ArcaneEquipmentPart.Weapon] = new(840, 1_400),
        [ArcaneEquipmentPart.Armor] = new(420, 700),
        [ArcaneEquipmentPart.Necklace] = new(420, 700),
        [ArcaneEquipmentPart.Ring] = new(420, 700),
    });

    public static HaeyeonGrowth Growth { get; } = HaeyeonGrowth.From(Data);
}

/// <summary>클래스·마도저항 데이터셋과 여러 데이터의 커밋 확인 공유.</summary>
internal static class UnpackDataTests
{
    private const string Commit = "3333333333333333333333333333333333333333";

    private static readonly IReadOnlyDictionary<string, string> s_Sheets = new Dictionary<string, string>
    {
        ["CombatJob"] = Sheet("CombatJob",
            """{"Id":"Warrior","ShowClass":true,"Order":1,"DisplayName":"견습 전사","CombatJobType":"BasicCombatJob"}""",
            """{"Id":"HighWarrior","ShowClass":true,"Order":4,"DisplayName":"전사","CombatJobType":"FirstAdvanceCombatJob"}""",
            """{"Id":"SwordMaster","ShowClass":true,"Order":3,"DisplayName":"검술사 ","CombatJobType":"FirstAdvanceCombatJob"}""",
            """{"Id":"Barbarian","ShowClass":false,"Order":2,"DisplayName":"바바리안","CombatJobType":"FirstAdvanceCombatJob"}"""),
        ["AbyssDungeonGroup"] = Sheet("AbyssDungeonGroup",
            """{"Id":"SunkenRuin","Season":"Abyss_Season_01"}""",
            """{"Id":"IllusionAnchorage","Season":"Abyss_Season_03"}""",
            """{"Id":"CaveofMadness","Season":"Abyss_Season_03"}"""),
        ["AbyssDungeonDifficulty"] = Sheet("AbyssDungeonDifficulty",
            """{"Id":"Hard","HellStep":0,"DisplayName":"어려움"}""",
            """{"Id":"Hell_1","HellStep":1,"DisplayName":"지옥1"}""",
            """{"Id":"Hell_2","HellStep":2,"DisplayName":"지옥2"}""",
            """{"Id":"Hell_5","HellStep":5,"DisplayName":"지옥5"}"""),
        ["AbyssDungeon"] = Sheet("AbyssDungeon",
            // 이전 시즌 기준값은 버리고, 기준이 없는 지옥5와 지옥이 아닌 난이도도 담지 않습니다.
            """{"Id":"Abyss_SunkenRuin_Hell_Lv1","Group":"SunkenRuin","Difficulty":"Hell_1","RequiredArcaneResistance":100,"ArcanePressure":200}""",
            """{"Id":"Abyss_IllusionAnchorage_S3_Lv2","Group":"IllusionAnchorage","Difficulty":"Hard","RequiredArcaneResistance":1000,"ArcanePressure":1600}""",
            """{"Id":"Abyss_IllusionAnchorage_S3_Hell_Lv2","Group":"IllusionAnchorage","Difficulty":"Hell_2","RequiredArcaneResistance":6000,"ArcanePressure":6600}""",
            """{"Id":"Abyss_IllusionAnchorage_S3_Hell_Lv1","Group":"IllusionAnchorage","Difficulty":"Hell_1","RequiredArcaneResistance":3500,"ArcanePressure":4400}""",
            """{"Id":"Abyss_CaveofMadness_S3_Hell_Lv1","Group":"CaveofMadness","Difficulty":"Hell_1","RequiredArcaneResistance":3500,"ArcanePressure":4400}""",
            """{"Id":"Abyss_CaveofMadness_S3_Hell_Lv5","Group":"CaveofMadness","Difficulty":"Hell_5","RequiredArcaneResistance":0,"ArcanePressure":0}"""),
        ["Equipment"] = Sheet("Equipment",
            """{"Id":"SwordAndShield_20_1","DisplayName":"잔영의 숏소드ZZ","EquipmentType":"Weapon","EquipmentArcaneResistance":"ArcaneResistance_Tier20_Weapon_1"}""",
            """{"Id":"SwordAndShield_20_2","DisplayName":"해연의 숏소드ZZ","EquipmentType":"Weapon","EquipmentArcaneResistance":"ArcaneResistance_Tier20_Weapon_2"}""",
            """{"Id":"Bow_20_2","DisplayName":"해연의 숏보우ZZ","EquipmentType":"Weapon","EquipmentArcaneResistance":"ArcaneResistance_Tier20_Weapon_2"}""",
            """{"Id":"HeavyArmor_Head_20_2","DisplayName":"해연의 비늘 갑옷 투구ZZ","EquipmentType":"Hat","EquipmentArcaneResistance":"ArcaneResistance_Tier20_Armor_2"}""",
            """{"Id":"HeavyArmor_Foot_20_2","DisplayName":"해연의 비늘 갑옷 신발ZZ","EquipmentType":"Shoes","EquipmentArcaneResistance":"ArcaneResistance_Tier20_Armor_2"}""",
            """{"Id":"Accessory_20_2","DisplayName":"해연의 페리도트 네크리스ZZ","EquipmentType":"Accessory","EquipmentArcaneResistance":"ArcaneResistance_Tier20_Necklace_2"}""",
            """{"Id":"Ring_20_2","DisplayName":"해연의 페리도트 링ZZ","EquipmentType":"Ring","EquipmentArcaneResistance":"ArcaneResistance_Tier20_Ring_2"}"""),
        ["EquipmentArcaneResistance"] = Sheet("EquipmentArcaneResistance",
            """{"Id":"ArcaneResistance_Tier20_Weapon_1","MinValue":280,"MaxValue":840}""",
            """{"Id":"ArcaneResistance_Tier20_Weapon_2","MinValue":840,"MaxValue":1400}""",
            """{"Id":"ArcaneResistance_Tier20_Armor_2","MinValue":420,"MaxValue":700}""",
            """{"Id":"ArcaneResistance_Tier20_Necklace_2","MinValue":420,"MaxValue":700}""",
            """{"Id":"ArcaneResistance_Tier20_Ring_2","MinValue":420,"MaxValue":700}"""),
    };

    private static string Sheet(string name, params string[] rows) => $$"""{"sheet":"{{name}}","rows":[{{string.Join(",", rows)}}]}""";

    public static async Task RunAsync()
    {
        await ClassTests();
        await MagicResistanceDatasetTests();
        await SharedCommitTests();
    }

    private static UnpackSheetFetcher Fetcher(IReadOnlyDictionary<string, string> sheets, List<string>? requested = null) => (sheet, _) =>
    {
        requested?.Add(sheet);
        return Task.FromResult(UnpackSheets.Parse(sheet, System.Text.Encoding.UTF8.GetBytes(sheets[sheet])));
    };

    private static async Task ClassTests()
    {
        var dataset = new UnpackClassDataset();
        var table = await dataset.BuildAsync(Fetcher(s_Sheets), default);
        Assert(table.Items.Select(x => x.Name).SequenceEqual(["검술사", "전사"]) && table.Items[0].Id == "SwordMaster",
            "클래스는 공개된 전직 클래스만 Order 순으로(견습·미공개 제외, 이름 앞뒤 공백 제거)");

        var duplicated = new Dictionary<string, string>(s_Sheets) { ["CombatJob"] = s_Sheets["CombatJob"].Replace("\"검술사 \"", "\"전사\"") };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(duplicated), default), "클래스 이름이 겹치면 검증 실패");
        var hidden = new Dictionary<string, string>(s_Sheets) { ["CombatJob"] = s_Sheets["CombatJob"].Replace("\"ShowClass\":true", "\"ShowClass\":false") };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(hidden), default), "공개된 전직 클래스가 하나도 없으면 검증 실패");

        Assert(Restore(dataset, table).Items.SequenceEqual(table.Items), "클래스 캐시 저장·복원");
    }

    private static async Task MagicResistanceDatasetTests()
    {
        var dataset = new UnpackMagicResistanceDataset();
        var requested = new List<string>();
        var data = await dataset.BuildAsync(Fetcher(s_Sheets, requested), default);
        Assert(data.AbyssSeason == 3 && data.AbyssName == "룬다 어비스"
               && data.AbyssHell.SequenceEqual([new MagicPressureContent("지옥1", 3_500, 4_400), new MagicPressureContent("지옥2", 6_000, 6_600)]),
            "기준값이 있는 가장 최근 어비스 시즌의 지옥 난이도만 지옥 단계 순으로 담음");
        Assert(data.HaeyeonEquipment.OrderBy(x => x.Key).SequenceEqual(TestMagicResistance.Data.HaeyeonEquipment.OrderBy(x => x.Key)),
            "해연 장비 부위별 마도저항은 '해연의' 장비가 가리키는 범위(잔영 장비 제외)");
        Assert(requested.SequenceEqual(["AbyssDungeonGroup", "AbyssDungeonDifficulty", "AbyssDungeon", "Equipment", "EquipmentArcaneResistance"]), "필요한 시트만 받음");
        Assert(new MagicResistanceData(4, data.AbyssHell, data.HaeyeonEquipment).AbyssName == "어비스 시즌 4", "이름을 모르는 시즌은 시즌 번호로 표시");

        var conflict = new Dictionary<string, string>(s_Sheets)
        {
            ["AbyssDungeon"] = s_Sheets["AbyssDungeon"].Replace("""CaveofMadness","Difficulty":"Hell_1","RequiredArcaneResistance":3500""", """CaveofMadness","Difficulty":"Hell_1","RequiredArcaneResistance":3600"""),
        };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(conflict), default), "같은 시즌·난이도의 기준값이 던전마다 다르면 검증 실패");
        var noRing = new Dictionary<string, string>(s_Sheets) { ["Equipment"] = s_Sheets["Equipment"].Replace("해연의 페리도트 링", "잔영의 페리도트 링") };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(noRing), default), "해연 장비 부위가 하나라도 없으면 검증 실패");
        var mixed = new Dictionary<string, string>(s_Sheets) { ["Equipment"] = s_Sheets["Equipment"].Replace("\"Bow_20_2\",\"DisplayName\":\"해연의 숏보우ZZ\",\"EquipmentType\":\"Weapon\",\"EquipmentArcaneResistance\":\"ArcaneResistance_Tier20_Weapon_2\"", "\"Bow_20_2\",\"DisplayName\":\"해연의 숏보우ZZ\",\"EquipmentType\":\"Weapon\",\"EquipmentArcaneResistance\":\"ArcaneResistance_Tier20_Weapon_1\"") };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Fetcher(mixed), default), "같은 부위 해연 장비의 마도저항 기준이 다르면 검증 실패");

        var restored = Restore(dataset, data);
        Assert(restored.AbyssSeason == 3 && restored.AbyssHell.SequenceEqual(data.AbyssHell)
               && restored.HaeyeonEquipment.OrderBy(x => x.Key).SequenceEqual(data.HaeyeonEquipment.OrderBy(x => x.Key)), "마도저항 캐시 저장·복원");

        var growth = HaeyeonGrowth.From(data);
        Assert(growth.InitialResistance == 7_200 && growth.MaxResistance == 10_000 && growth.MaxUnits == 200,
            "게임 데이터의 해연 장비 범위로 해연 비용 계산 범위 7,200~10,000을 만듦");
        var offStep = new Dictionary<ArcaneEquipmentPart, EquipmentResistanceRange>(data.HaeyeonEquipment) { [ArcaneEquipmentPart.Weapon] = new(840, 1_414) };
        Assert(ThrowsInvalid(() => new HaeyeonGrowth(offStep)), "범위가 감정 단위(무기 28)와 맞지 않으면 계산하지 않음");
    }

    private static async Task SharedCommitTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "molly-unpack-data-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var commitRequests = 0;
            var unauthorized = false;
            var handler = new StubHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (unauthorized) return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"message":"Bad credentials"}""") };
                if (path.EndsWith("/commits", StringComparison.Ordinal)) { commitRequests++; return Json($"[{{\"sha\":\"{Commit}\"}}]"); }
                if (path.EndsWith("/contents/version.json", StringComparison.Ordinal)) return Json("""{"patch":{"display":"P"},"app":{"version":"1"}}""");
                const string prefix = "/contents/Patch/Sheetdata/client/";
                var index = path.IndexOf(prefix, StringComparison.Ordinal);
                if (index >= 0 && s_Sheets.TryGetValue(Path.GetFileNameWithoutExtension(path[(index + prefix.Length)..]), out var body)) return Json(body);
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };
            });
            var client = new UnpackRepoClient(new UnpackRepoOptions { Token = "github_pat_test" }, handler);
            var classes = new UnpackDataCatalog<ClassTable>(client, new UnpackClassDataset(), dir, _ => { });
            var resistance = new UnpackDataCatalog<MagicResistanceData>(client, new UnpackMagicResistanceDataset(), dir, _ => { });

            var first = await UnpackDataCatalogs.RefreshAllAsync(client, [classes, resistance], default);
            Assert(first.All(x => x is { IsSuccess: true, Changed: true }) && commitRequests == 1 && classes.Data!.Items.Count == 2 && resistance.Data!.AbyssHell.Count == 2
                   && File.Exists(Path.Combine(dir, "unpack", "classes.json")) && File.Exists(Path.Combine(dir, "unpack", "magic-resistance.json")),
                "여러 데이터가 시트 폴더 커밋 확인 1회를 함께 쓰고 각자 캐시에 저장");
            var before = handler.Requests;
            var second = await UnpackDataCatalogs.RefreshAllAsync(client, [classes, resistance], default);
            Assert(second.All(x => x is { IsSuccess: true, Changed: false }) && handler.Requests == before + 1, "커밋이 같으면 데이터 수와 상관없이 요청 1회");

            unauthorized = true;
            var original = resistance.Current;
            var denied = new UnpackDataCatalog<MagicResistanceData>(client, new UnpackMagicResistanceDataset(), dir, _ => { });
            await denied.InitializeAsync();
            var run = UnpackDataCatalogs.RunAsync(client, [classes, resistance, denied], default);
            Assert(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))) == run && ReferenceEquals(original, resistance.Current) && denied.Data is not null,
                "토큰이 거부되면 주기 확인을 멈추고 마지막 데이터·캐시를 계속 씀");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    private static T Restore<T>(IUnpackDataset<T> dataset, T data) where T : class
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) dataset.Write(writer, data);
        using var document = JsonDocument.Parse(buffer.ToArray());
        return dataset.Read(document.RootElement);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(respond(request));
        }
    }

    private static bool ThrowsInvalid(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
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
