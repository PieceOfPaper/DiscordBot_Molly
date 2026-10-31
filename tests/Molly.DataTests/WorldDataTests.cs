using System.Net;
using System.Text.Json;
using Molly.Unpack;
using Molly.World;

/// <summary>NPC·지역 데이터셋(빠른 이동 지역·월드 배치 NPC)과 NPC 이미지 캐시(blob SHA 비교·정리), version.json 기준 변경 확인.</summary>
internal static class WorldDataTests
{
    private const string CommitA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CommitB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GlenisSha = "1111111111111111111111111111111111111111";
    private const string GlenisNewSha = "2222222222222222222222222222222222222222";
    private const string CatSha = "3333333333333333333333333333333333333333";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static readonly IReadOnlyDictionary<string, string> s_Sheets = new Dictionary<string, string>
    {
        ["QuickNavigatorShortcut"] = Sheet("QuickNavigatorShortcut",
            """{"Id":"Navi_Important_1","ChannelSheetData":0,"Destination":"Dunbarton"}""",
            """{"Id":"Navi_Dunbarton_1","ChannelSheetData":"Dunbarton"}""",
            """{"Id":"Navi_SenMag_1","ChannelSheetData":"SenMag_Hard"}""",
            """{"Id":"Navi_Lobby","ChannelSheetData":"MiniLobby"}"""),
        ["Channel"] = Sheet("Channel",
            """{"Id":"Dunbarton","ChannelRule":"Field","GameSpace":"Dunbarton"}""",
            """{"Id":"Dunbarton_Scenario","ChannelRule":"ScenarioField","GameSpace":"Dunbarton"}""",
            """{"Id":"SenMag","ChannelRule":"BattleField","GameSpace":"SenMag"}""",
            """{"Id":"SenMag_Hard","ChannelRule":"BattleField","GameSpace":"SenMag"}""",
            """{"Id":"MiniLobby","ChannelRule":"MiniGameLobby","GameSpace":"MiniSpace"}""",
            """{"Id":"Glan","ChannelRule":"Field","GameSpace":"Glan"}"""),
        ["GameSpace"] = Sheet("GameSpace",
            """{"Id":"Dunbarton","DisplayName":"던바튼","WorldDataKey":{"pstr":"Dunbarton"}}""",
            """{"Id":"SenMag","DisplayName":"센마이 평원","WorldDataKey":{"pstr":"SenMag"}}""",
            """{"Id":"MiniSpace","DisplayName":"미니게임","WorldDataKey":{"pstr":"MiniSpace"}}""",
            """{"Id":"Glan","DisplayName":"글란","WorldDataKey":{"pstr":"Glan"}}"""),
        ["NPC"] = Sheet("NPC",
            """{"Id":"None","DisplayName":"None","Title":"-","ProfileImage":""}""",
            """{"Id":"Glenis","DisplayName":"글리니스","Title":"수다쟁이","ProfileImage":"Sprites/NPCProfile/Glenis.png"}""",
            """{"Id":"Dunbarton_Notice","DisplayName":"임무 게시판","Title":"-","ProfileImage":"Sprites/NPCProfile/NoticeBoard_03.png"}""",
            """{"Id":"MerchantCat_Gold","DisplayName":"고양이 상인","Title":"특별한 행운의","ProfileImage":"Sprites/NPCProfile/MerchantCat.png"}""",
            """{"Id":"MerchantCat","DisplayName":"고양이 상인","Title":"행운의","ProfileImage":"Sprites/NPCProfile/MerchantCat.png"}""",
            """{"Id":"Worker","DisplayName":"인부","Title":"일하는","ProfileImage":"Sprites/NPCProfile/Extra.png"}""",
            """{"Id":"Event_Amy","DisplayName":"에이미","Title":"이벤트의","ProfileImage":"Sprites/NPCProfile/Amy.png"}""",
            """{"Id":"Ide","DisplayName":"이데","Title":"글란의","ProfileImage":"Sprites/NPCProfile/Ide.png"}"""),
    };

    private static readonly IReadOnlyDictionary<string, string> s_Files = new Dictionary<string, string>
    {
        [UnpackWorldDataset.NpcDeployPath("Dunbarton")] = Deploy("Glenis", "Dunbarton_Notice", "MerchantCat_Gold", "Worker", "None"),
        [UnpackWorldDataset.NpcDeployPath("SenMag")] = Deploy("MerchantCat"),
        [UnpackWorldDataset.NpcDeployPath("Glan")] = Deploy("Ide"),
    };

    private static string Sheet(string name, params string[] rows) => $$"""{"sheet":"{{name}}","rows":[{{string.Join(",", rows)}}]}""";

    private static string Deploy(params string[] npcs) =>
        """{"schema":{},"data":{"$type":"NPCDeployData","SpawnGroups":[{"Name":"G","Spawns":[""" +
        string.Join(",", npcs.Select((n, i) => $$"""{"Name":"NPC_{{n}}","NPC":[{{i}},"{{n}}"]}""")) +
        "]}]}}";

    public static async Task RunAsync()
    {
        await DatasetTests();
        await PortraitCacheTests();
        await MissingVersionCommitTests();
    }

    private static UnpackSheetFetcher Sheets(IReadOnlyDictionary<string, string> sheets) => (sheet, _) =>
        Task.FromResult(UnpackSheets.Parse(sheet, System.Text.Encoding.UTF8.GetBytes(sheets[sheet])));

    private static UnpackFileFetcher Files(IReadOnlyDictionary<string, string> files, List<string>? requested = null) => (path, _) =>
    {
        requested?.Add(path);
        return files.TryGetValue(path, out var body)
            ? Task.FromResult(JsonDocument.Parse(body))
            : throw new UnpackDataException(UnpackRepoStatus.NotFound, path + " 없음");
    };

    private static async Task DatasetTests()
    {
        var dataset = new UnpackWorldDataset();
        var requested = new List<string>();
        var table = await dataset.BuildAsync(Sheets(s_Sheets), Files(s_Files, requested), default);
        Assert(table.Regions.SequenceEqual([new RegionData("Dunbarton", "던바튼", "마을"), new RegionData("SenMag", "센마이 평원", "사냥터")]),
            "지역은 빠른 이동이 있는 마을(Field)·사냥터(BattleField)만(미니게임·개발 중 지역 제외)");
        Assert(requested.SequenceEqual([UnpackWorldDataset.NpcDeployPath("Dunbarton"), UnpackWorldDataset.NpcDeployPath("SenMag")]),
            "지역 월드의 NPC 배치 파일만 받음");
        Assert(table.Npcs.Select(x => x.Name).SequenceEqual(["글리니스", "고양이 상인", "인부"]),
            "NPC는 지역에 배치되고 칭호가 있는 NPC만(게시판·이벤트·개발 중 지역 NPC 제외)");
        var cat = table.Npcs.Single(x => x.Name == "고양이 상인");
        Assert(cat.Id == "MerchantCat" && cat.Regions.SequenceEqual(["던바튼", "센마이 평원"]) && cat.ProfileImage == "Sprites/NPCProfile/MerchantCat.png",
            "같은 이름 NPC는 하나로 합치고 배치 지역을 모두 담음");
        Assert(table.Npcs.Single(x => x.Name == "인부").ProfileImage is null && table.Npcs.Single(x => x.Name == "글리니스").Regions.SequenceEqual(["던바튼"]),
            "기본 실루엣은 프로필 이미지 없음으로 담음");

        var noSenMag = new Dictionary<string, string>(s_Files);
        noSenMag.Remove(UnpackWorldDataset.NpcDeployPath("SenMag"));
        var partial = await dataset.BuildAsync(Sheets(s_Sheets), Files(noSenMag), default);
        Assert(partial.Npcs.Single(x => x.Name == "고양이 상인").Regions.SequenceEqual(["던바튼"]), "배치 파일이 없는 월드는 배치된 NPC가 없는 것으로 봄");

        var conflict = new Dictionary<string, string>(s_Sheets) { ["Channel"] = s_Sheets["Channel"].Replace("\"ScenarioField\"", "\"BattleField\"") };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Sheets(conflict), Files(s_Files), default), "한 지역이 마을·사냥터 채널에 모두 속하면 검증 실패");
        var noRegion = new Dictionary<string, string>(s_Sheets) { ["QuickNavigatorShortcut"] = Sheet("QuickNavigatorShortcut") };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Sheets(noRegion), Files(s_Files), default), "지역이 하나도 없으면 검증 실패");
        var brokenDeploy = new Dictionary<string, string>(s_Files) { [UnpackWorldDataset.NpcDeployPath("SenMag")] = """{"data":{}}""" };
        await AssertThrowsAsync<InvalidDataException>(() => dataset.BuildAsync(Sheets(s_Sheets), Files(brokenDeploy), default), "NPC 배치 파일 형식이 다르면 검증 실패");

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) dataset.Write(writer, table);
        using var document = JsonDocument.Parse(buffer.ToArray());
        var restored = dataset.Read(document.RootElement);
        Assert(restored.Regions.SequenceEqual(table.Regions) && restored.Npcs.Zip(table.Npcs).All(x =>
                (x.First.Id, x.First.Name, x.First.ProfileImage) == (x.Second.Id, x.Second.Name, x.Second.ProfileImage) && x.First.Regions.SequenceEqual(x.Second.Regions)),
            "NPC·지역 캐시 저장·복원");
    }

    private static async Task PortraitCacheTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "molly-world-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var commit = CommitA;
            var tree = new Dictionary<string, string> { ["Glenis.png"] = GlenisSha, ["MerchantCat.png"] = CatSha, ["Other.png"] = "4444444444444444444444444444444444444444" };
            var imageRequests = new List<string>();
            var versionChecks = 0;
            var handler = new StubHandler(request =>
            {
                var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
                if (path.EndsWith("/commits", StringComparison.Ordinal))
                {
                    if (!request.RequestUri.Query.Contains("path=version.json", StringComparison.Ordinal)) return NotFound();
                    versionChecks++;
                    return Json($"[{{\"sha\":\"{commit}\"}}]");
                }
                if (path.Contains("/git/trees/", StringComparison.Ordinal))
                {
                    if (!path.Contains($"{commit}:{NpcPortraitCache.AssetRoot}/Sprites/NPCProfile", StringComparison.Ordinal)) return NotFound();
                    var entries = string.Join(",", tree.Select(x => $$"""{"path":"{{x.Key}}","type":"blob","sha":"{{x.Value}}","size":10}"""));
                    return Json($$"""{"sha":"t","tree":[{{entries}}],"truncated":false}""");
                }
                if (path.EndsWith("/contents/version.json", StringComparison.Ordinal)) return Json("""{"patch":{"display":"P"},"app":{"version":"1"}}""");
                const string contents = "/contents/";
                var index = path.IndexOf(contents, StringComparison.Ordinal);
                if (index < 0) return NotFound();
                var file = path[(index + contents.Length)..];
                if (file.StartsWith(NpcPortraitCache.AssetRoot + "/", StringComparison.Ordinal))
                {
                    imageRequests.Add(Path.GetFileName(file));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
                }
                if (file.StartsWith(UnpackSheets.ClientDirectory + "/", StringComparison.Ordinal) && s_Sheets.TryGetValue(Path.GetFileNameWithoutExtension(file), out var sheet)) return Json(sheet);
                return s_Files.TryGetValue(file, out var body) ? Json(body) : NotFound();
            });
            using var client = new UnpackRepoClient(new UnpackRepoOptions { Token = "github_pat_test" }, handler, log: _ => { });
            var world = new UnpackDataCatalog<WorldTable>(client, new UnpackWorldDataset(), dir, _ => { });
            var portraits = new NpcPortraitCache(client, world, dir, _ => { });
            var folder = Path.Combine(dir, "unpack", "npc-portraits");

            var first = await UnpackDataCatalogs.RefreshAllAsync(client, [world, portraits], default);
            Assert(first.All(x => x.IsSuccess) && versionChecks == 1 && world.Data!.Npcs.Count == 3 && portraits.Count == 2
                   && imageRequests.Order().SequenceEqual(["Glenis.png", "MerchantCat.png"])
                   && File.Exists(Path.Combine(folder, GlenisSha + ".png")) && File.Exists(Path.Combine(folder, CatSha + ".png")),
                "version.json 커밋 1회로 NPC·지역 데이터를 만든 뒤 필요한 NPC 이미지만 SHA 이름으로 받음");
            Assert(portraits.GetPath("Sprites/NPCProfile/Glenis.png") == Path.Combine(folder, GlenisSha + ".png") && portraits.GetPath("Sprites/NPCProfile/Extra.png") is null,
                "에셋 경로로 받아 둔 이미지 찾기");

            var before = handler.Requests;
            await UnpackDataCatalogs.RefreshAllAsync(client, [world, portraits], default);
            Assert(handler.Requests == before + 1, "버전이 같으면 이미지까지 합쳐 요청 1회");

            commit = CommitB;
            tree = new Dictionary<string, string> { ["Glenis.png"] = GlenisNewSha };
            imageRequests.Clear();
            var second = await UnpackDataCatalogs.RefreshAllAsync(client, [world, portraits], default);
            Assert(second.All(x => x.IsSuccess) && imageRequests.SequenceEqual(["Glenis.png"]) && portraits.Count == 1
                   && File.Exists(Path.Combine(folder, GlenisNewSha + ".png")) && !File.Exists(Path.Combine(folder, GlenisSha + ".png")) && !File.Exists(Path.Combine(folder, CatSha + ".png")),
                "버전이 바뀌면 SHA가 바뀐 이미지만 다시 받고, 저장소에서 사라지거나 쓰지 않는 이미지는 지움");

            var restarted = new NpcPortraitCache(client, world, dir, _ => { });
            await restarted.InitializeAsync();
            Assert(restarted.Count == 1 && restarted.GetPath("Sprites/NPCProfile/Glenis.png") == Path.Combine(folder, GlenisNewSha + ".png"),
                "재시작하면 요청 없이 받아 둔 이미지 목록을 읽음");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // version.json이 덤프보다 먼저 올라가 버전 커밋에 시트·파일이 없을 때.
    private static async Task MissingVersionCommitTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "molly-world-missing-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var versionCommit = CommitA;
            var headRequests = 0;
            var handler = new StubHandler(request =>
            {
                var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (path.EndsWith("/commits", StringComparison.Ordinal)) return Json($"[{{\"sha\":\"{versionCommit}\"}}]");
                if (path.EndsWith("/commits/main", StringComparison.Ordinal))
                {
                    headRequests++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CommitB) };
                }
                // 버전 커밋(A)에는 version.json만 있고, 최신 커밋(B)에 시트·파일·이미지가 모두 있습니다.
                var atHead = query.Contains("ref=" + CommitB, StringComparison.Ordinal) || path.Contains(CommitB + ":", StringComparison.Ordinal);
                if (path.EndsWith("/contents/version.json", StringComparison.Ordinal)) return Json("""{"patch":{"display":"P"},"app":{"version":"1"}}""");
                if (!atHead) return NotFound();
                if (path.Contains("/git/trees/", StringComparison.Ordinal))
                    return Json($$"""{"tree":[{"path":"Glenis.png","type":"blob","sha":"{{GlenisSha}}"},{"path":"MerchantCat.png","type":"blob","sha":"{{CatSha}}"}],"truncated":false}""");
                var file = path[(path.IndexOf("/contents/", StringComparison.Ordinal) + "/contents/".Length)..];
                if (file.StartsWith(NpcPortraitCache.AssetRoot + "/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
                if (file.StartsWith(UnpackSheets.ClientDirectory + "/", StringComparison.Ordinal) && s_Sheets.TryGetValue(Path.GetFileNameWithoutExtension(file), out var sheet)) return Json(sheet);
                return s_Files.TryGetValue(file, out var body) ? Json(body) : NotFound();
            });
            using var client = new UnpackRepoClient(new UnpackRepoOptions { Token = "github_pat_test" }, handler, log: _ => { });
            var world = new UnpackDataCatalog<WorldTable>(client, new UnpackWorldDataset(), dir, _ => { });
            var portraits = new NpcPortraitCache(client, world, dir, _ => { });

            var first = await UnpackDataCatalogs.RefreshAllAsync(client, [world, portraits], default);
            Assert(first.All(x => x.IsSuccess) && world.Current?.Commit == CommitB && world.Data!.Npcs.Count == 3 && portraits.Count == 2 && headRequests == 1,
                "데이터가 없는데 버전 커밋에 파일이 없으면 최신 커밋으로 만들고 이미지도 그 커밋 기준으로 받음");
            var before = handler.Requests;
            var second = await UnpackDataCatalogs.RefreshAllAsync(client, [world, portraits], default);
            Assert(second.All(x => x.IsSuccess) && handler.Requests == before + 1, "같은 버전 커밋이면 최신 커밋으로 만든 데이터를 그대로 쓰고 다시 시도하지 않음");

            var cached = new UnpackDataCatalog<WorldTable>(client, new UnpackWorldDataset(), dir, _ => { });
            await cached.InitializeAsync();
            headRequests = 0;
            var kept = await cached.RefreshAsync(CommitA, default);
            Assert(kept.Status == UnpackRepoStatus.NotFound && headRequests == 0 && cached.Current?.Commit == CommitB,
                "캐시가 있으면 최신 커밋으로 대신 만들지 않고 캐시를 유지");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };

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
