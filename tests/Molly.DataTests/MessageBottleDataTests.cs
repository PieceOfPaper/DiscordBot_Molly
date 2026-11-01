using System.Text.Json;
using Molly.Messages;
using Molly.Unpack;

/// <summary>병 속에 든 쪽지 데이터셋(문자열 테이블 EasterEgg 그룹의 GlassBottleLetter_번호 문구).</summary>
internal static class MessageBottleDataTests
{
    public static async Task RunAsync()
    {
        var dataset = new UnpackMessageBottleDataset();
        var requested = new List<string>();
        var table = await dataset.BuildAsync(NoSheets, Files("""
            {"EasterEgg":{"codeGen":true,"items":{
              "GlassBottleLetter_10":"어둠이 깊을수록 새벽이 가까워요.\r\n곧 환한 빛이 찾아들 거에요.",
              "GlassBottleLetter_02":"오늘의 한마디!\n\"숨쉬는 것도 운동이다.\"",
              "BeggarFood_Common_00":"음~ 배고팠는데 잘됐네요.",
              "GlassBottleLetter_Title":"제목",
              "GlassBottleLetter_00":"  괜찮아. 충분히 잘하고 있어.  "
            },"groups":{}}}
            """, requested), default);
        Assert(requested.SequenceEqual([UnpackMessageBottleDataset.StringsPath]), "쪽지는 문자열 테이블 파일 하나만 받음");
        Assert(table.Items.Select(x => x.Id).SequenceEqual(["GlassBottleLetter_00", "GlassBottleLetter_02", "GlassBottleLetter_10"]),
            "쪽지는 GlassBottleLetter_번호 키만 번호 순으로 담음(다른 이스터에그 문구 제외)");
        Assert(table.Items[0].Message == "괜찮아. 충분히 잘하고 있어." && table.Items[1].Message == "오늘의 한마디!\n\"숨쉬는 것도 운동이다.\""
            && table.Items[2].Message == "어둠이 깊을수록 새벽이 가까워요.\n곧 환한 빛이 찾아들 거에요.", "쪽지 문구의 줄바꿈 유지·앞뒤 공백 제거");

        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer)) dataset.Write(writer, table);
            using var cached = JsonDocument.Parse(buffer.ToArray());
            var restored = dataset.Read(cached.RootElement);
            Assert(restored.Items.SequenceEqual(table.Items), "쪽지 캐시 저장·읽기 왕복");
        }

        await Reject(dataset, """{"Other":{"items":{}}}""", "EasterEgg 그룹이 없으면 거부");
        await Reject(dataset, """{"EasterEgg":{"items":{"BeggarFood_Common_00":"음~"}}}""", "쪽지 문구가 하나도 없으면 거부");
        await Reject(dataset, """{"EasterEgg":{"items":{"GlassBottleLetter_00":"  "}}}""", "빈 쪽지 문구 거부");
        await Reject(dataset, "{\"EasterEgg\":{\"items\":{\"GlassBottleLetter_00\":\"" + new string('가', 3501) + "\"}}}", "3500자를 넘는 쪽지 문구 거부");
    }

    private static readonly UnpackSheetFetcher NoSheets = (sheet, _) => throw new Exception(sheet + " 시트를 받으면 안 됨");

    private static UnpackFileFetcher Files(string strings, List<string>? requested = null) => (path, _) =>
    {
        requested?.Add(path);
        return path == UnpackMessageBottleDataset.StringsPath
            ? Task.FromResult(JsonDocument.Parse(strings))
            : throw new UnpackDataException(UnpackRepoStatus.NotFound, path + " 없음");
    };

    private static async Task Reject(UnpackMessageBottleDataset dataset, string strings, string name)
    {
        try { await dataset.BuildAsync(NoSheets, Files(strings), default); }
        catch (InvalidDataException) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
