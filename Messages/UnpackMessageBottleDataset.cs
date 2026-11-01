using System.Text.Json;
using System.Text.RegularExpressions;
using Molly.Unpack;

namespace Molly.Messages;

/// <param name="Id">문자열 키(예: GlassBottleLetter_04).</param>
public sealed record MessageBottleEntry(string Id, string Message);

public sealed class MessageBottleTable(IEnumerable<MessageBottleEntry> items)
{
    public IReadOnlyList<MessageBottleEntry> Items { get; } = Array.AsReadOnly(items.ToArray());
}

/// <summary>
/// 게임 아이템 `병 속에 든 쪽지`(Consumable.EasterEgg_GlassBottleLetter)를 읽을 때 나오는 문구.
/// 아이템 시트에는 문구를 가리키는 값이 없고, 문자열 테이블의 EasterEgg 그룹에 `GlassBottleLetter_번호` 키로만 들어 있어
/// 이 키를 모두 모아 번호 순으로 담습니다.
/// </summary>
public sealed partial class UnpackMessageBottleDataset : IUnpackDataset<MessageBottleTable>
{
    public const string StringsPath = "Patch/StringTables/compiled.strings.json";
    private const string Group = "EasterEgg";
    // Embed 설명 4096자에서 인용 표시("> ")를 붙일 여유를 남깁니다.
    private const int MaxLength = 3500;

    public string Name => "병 속에 든 쪽지";
    public string CacheName => "message-bottles";

    [GeneratedRegex(@"^GlassBottleLetter_(\d+)$")]
    private static partial Regex KeyPattern();

    public Task<MessageBottleTable> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct) =>
        throw new NotSupportedException("병 속에 든 쪽지는 문자열 테이블 파일로 만듭니다.");

    public async Task<MessageBottleTable> BuildAsync(UnpackSheetFetcher sheets, UnpackFileFetcher files, CancellationToken ct)
    {
        using var strings = await files(StringsPath, ct);
        if (!strings.RootElement.TryGetProperty(Group, out var group) || !group.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"문자열 테이블에 {Group} 그룹이 없습니다.");
        var entries = new List<(int Order, MessageBottleEntry Entry)>();
        foreach (var item in items.EnumerateObject())
        {
            var match = KeyPattern().Match(item.Name);
            if (!match.Success) continue;
            var message = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString()!.Replace("\r\n", "\n").Trim() : "";
            if (message.Length == 0) throw new InvalidDataException($"쪽지 {item.Name}의 문구가 비어 있습니다.");
            if (message.Length > MaxLength) throw new InvalidDataException($"쪽지 {item.Name}의 문구가 {MaxLength}자를 넘습니다.");
            entries.Add((int.Parse(match.Groups[1].Value), new MessageBottleEntry(item.Name, message)));
        }
        if (entries.Count == 0) throw new InvalidDataException($"문자열 테이블 {Group} 그룹에 쪽지 문구가 없습니다.");
        return new MessageBottleTable(entries.OrderBy(x => x.Order).Select(x => x.Entry));
    }

    public void Write(Utf8JsonWriter writer, MessageBottleTable data)
    {
        writer.WriteStartArray();
        foreach (var item in data.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.Id);
            writer.WriteString("message", item.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public MessageBottleTable Read(JsonElement data)
    {
        var items = data.EnumerateArray().Select(x => new MessageBottleEntry(x.GetProperty("id").GetString()!, x.GetProperty("message").GetString()!)).ToArray();
        if (items.Length == 0) throw new InvalidDataException("캐시에 쪽지가 없습니다.");
        return new MessageBottleTable(items);
    }

    public string Describe(MessageBottleTable data) => $"쪽지 {data.Items.Count}개";
}
