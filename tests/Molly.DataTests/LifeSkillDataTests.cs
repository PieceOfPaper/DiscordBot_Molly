using System.Text.Json;
using Molly.Battle;
using Molly.Unpack;

/// <summary>생활스킬 데이터셋(게임 데이터 LivingSkill). 배틀생활스킬 이름 확인에 쓴다.</summary>
internal static class LifeSkillDataTests
{
    public static async Task RunAsync()
    {
        var dataset = new UnpackLifeSkillDataset();
        var requested = new List<string>();
        var table = await dataset.BuildAsync(Sheet("""
            {"sheet":"LivingSkill","rows":[
              {"Id":"UsualGathering","DisplayName":"일상 채집","LivingSkillType":"Gathering"},
              {"Id":"Logging","DisplayName":" 나무 베기 ","LivingSkillType":"Gathering"},
              {"Id":0,"DisplayName":"빈 행"},
              {"Id":"Alchemy","DisplayName":"연금술","LivingSkillType":"Alchemy"}
            ]}
            """, requested), default);
        Assert(requested.SequenceEqual(["LivingSkill"]), "생활스킬은 LivingSkill 시트 하나만 받음");
        Assert(table.Items.Select(x => (x.Id, x.Name)).SequenceEqual([("UsualGathering", "일상 채집"), ("Logging", "나무 베기"), ("Alchemy", "연금술")]),
            "생활스킬은 Id가 있는 행만 시트 순서대로 담고 이름 앞뒤 공백을 지움");
        Assert(table.Names.Contains("나무 베기") && table.Names.Count == 3, "생활스킬 이름 목록");

        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer)) dataset.Write(writer, table);
            using var cached = JsonDocument.Parse(buffer.ToArray());
            Assert(dataset.Read(cached.RootElement).Items.SequenceEqual(table.Items), "생활스킬 캐시 저장·읽기 왕복");
        }

        await Reject(dataset, """{"rows":[]}""", "생활스킬이 하나도 없으면 거부");
        await Reject(dataset, """{"rows":[{"Id":"Logging","DisplayName":" "}]}""", "빈 생활스킬 이름 거부");
        await Reject(dataset, """{"rows":[{"Id":"Logging","DisplayName":"나무 베기"},{"Id":"Logging2","DisplayName":"나무 베기"}]}""", "중복 생활스킬 이름 거부");
    }

    private static UnpackSheetFetcher Sheet(string json, List<string>? requested = null) => (sheet, _) =>
    {
        requested?.Add(sheet);
        return sheet == "LivingSkill"
            ? Task.FromResult(JsonDocument.Parse(json))
            : throw new UnpackDataException(UnpackRepoStatus.NotFound, sheet + " 없음");
    };

    private static async Task Reject(UnpackLifeSkillDataset dataset, string json, string name)
    {
        try { await dataset.BuildAsync(Sheet(json), default); }
        catch (InvalidDataException) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
