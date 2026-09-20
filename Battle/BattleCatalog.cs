using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Molly.Battle;

public interface IBattleDataSource
{
    string CacheKey { get; }
    Task<IReadOnlyDictionary<string, string>> FetchAsync(CancellationToken ct);
}

/// <summary>전투용 10개 탭을 한 요청 묶음으로 가져옵니다. 엔진은 이 공급자를 직접 사용하지 않습니다.</summary>
public sealed class GoogleSheetsBattleSource(HttpClient client, string spreadsheetId) : IBattleDataSource
{
    public static readonly string[] SheetNames = ["클래스", "스킬", "배틀스킬", "배틀스킬효과", "배틀스킬파생", "배틀자원", "배틀상태효과", "배틀규칙", "배틀돌발이벤트", "배틀돌발이벤트효과"];
    public string CacheKey => spreadsheetId;

    public async Task<IReadOnlyDictionary<string, string>> FetchAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spreadsheetId) || spreadsheetId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_'))
            throw new InvalidDataException("Google Sheets 문서 ID를 확인하세요.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sheet in SheetNames)
        {
            var uri = new Uri($"https://docs.google.com/spreadsheets/d/{spreadsheetId}/gviz/tq?tqx=out:csv&sheet={Uri.EscapeDataString(sheet)}");
            using var response = await client.GetAsync(uri, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var csv = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException($"{sheet} 시트가 CSV 대신 HTML을 반환했습니다. 공개 읽기 권한을 확인하세요.");
            result.Add(sheet, csv);
        }
        return result;
    }
}

public sealed class BattleCatalog
{
    private readonly IBattleDataSource source;
    private readonly string cachePath;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private BattleDataSnapshot current = BattleDataSnapshot.Empty;
    public BattleDataSnapshot Current => Volatile.Read(ref current);

    public BattleCatalog(IBattleDataSource source, string dataDirectory, Action<string>? log = null)
    {
        this.source = source;
        this.log = log ?? Console.WriteLine;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.CacheKey)));
        cachePath = Path.Combine(dataDirectory, "battle", key + ".json");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(cachePath))
            {
                try
                {
                    var tables = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false));
                    if (tables is not null) Volatile.Write(ref current, Parse(tables, File.GetLastWriteTimeUtc(cachePath)));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { log($"[배틀] 로컬 캐시 로딩 실패: {ex.Message}"); }
            }
        }
        finally { gate.Release(); }
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tables = await source.FetchAsync(ct).ConfigureAwait(false);
            var snapshot = Parse(tables, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(tables);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try { await File.WriteAllTextAsync(temporary, json, Encoding.UTF8, ct).ConfigureAwait(false); File.Move(temporary, cachePath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Volatile.Write(ref current, snapshot);
            log($"[배틀] 시트 스냅샷 갱신 완료: 클래스 {snapshot.Classes.Count}개, 스킬 {snapshot.Skills.Count}개");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log($"[배틀] 시트 갱신 실패, 마지막 정상 스냅샷 유지: {ex.Message}"); return false; }
        finally { gate.Release(); }
    }

    public static BattleDataSnapshot Parse(IReadOnlyDictionary<string, string> tables, DateTimeOffset loadedAt)
    {
        foreach (var name in GoogleSheetsBattleSource.SheetNames)
            if (!tables.ContainsKey(name)) throw new InvalidDataException($"전투 데이터에 {name} 시트가 없습니다.");
        var classes = BattleCsv.Read(tables["클래스"], "클래스");
        var skills = BattleCsv.Read(tables["스킬"], "스킬");
        var battleSkills = BattleCsv.Read(tables["배틀스킬"], "배틀스킬");
        var effects = BattleCsv.Read(tables["배틀스킬효과"], "배틀스킬효과");
        var derivations = BattleCsv.Read(tables["배틀스킬파생"], "배틀스킬파생");
        var resources = BattleCsv.Read(tables["배틀자원"], "배틀자원");
        var statuses = BattleCsv.Read(tables["배틀상태효과"], "배틀상태효과");
        var rules = BattleCsv.Read(tables["배틀규칙"], "배틀규칙");
        // 나머지 표도 누락/깨진 CSV를 허용하지 않습니다. 상세 효과는 이후 엔진 단계에서 공통 모델로 확장합니다.
        foreach (var name in GoogleSheetsBattleSource.SheetNames.Except(["클래스", "스킬", "배틀스킬", "배틀스킬효과", "배틀스킬파생", "배틀자원", "배틀상태효과", "배틀규칙"])) BattleCsv.Read(tables[name], name);
        BattleCsv.Headers(classes, "클래스", "ID", "이름", "스킬1", "스킬2", "스킬3", "스킬4", "스킬5", "궁극기");
        BattleCsv.Headers(skills, "스킬", "ID", "이름", "스킬구분", "부모스킬ID");
        BattleCsv.Headers(battleSkills, "배틀스킬", "ID", "활성화", "기본쿨다운", "최초쿨다운", "사용우선순위", "자원유형", "자원소모", "자원획득");
        BattleCsv.Headers(effects, "배틀스킬효과", "ID", "스킬ID", "실행순서", "효과유형", "대상", "고정값", "횟수", "발동확률", "지속턴", "상태효과ID", "최대중첩", "효과문구", "조건대상", "조건유형", "조건ID", "조건연산자", "조건값", "수치참조ID", "수치참조방식");
        BattleCsv.Headers(derivations, "배틀스킬파생", "ID", "부모스킬ID", "파생스킬ID", "발동방식", "가중치", "발동확률", "조건유형", "조건값", "중복허용", "실행시점", "우선순위");
        BattleCsv.Headers(resources, "배틀자원", "ID", "이름", "분류", "최대값", "초기값", "지속턴", "중첩방식", "전투종료시제거");
        BattleCsv.Headers(statuses, "배틀상태효과", "ID", "이름", "효과유형", "값", "설명");
        BattleCsv.Headers(rules, "배틀규칙", "ID", "분류", "값유형", "값", "설명");

        var ruleMap = Unique(rules, "배틀규칙").ToDictionary(x => x.Required("ID", "배틀규칙", 0), x => new BattleRule(x["ID"], x["분류"], x["값유형"], x["값"], x["설명"]), StringComparer.Ordinal);
        var rawSkills = Unique(skills, "스킬").ToDictionary(x => x["ID"], StringComparer.Ordinal);
        foreach (var (row, index) in skills.Select((x, i) => (x, i + 2)))
        {
            var id = row.Required("ID", "스킬", index); var kind = row.Required("스킬구분", "스킬", index);
            var parent = row["부모스킬ID"];
            if (kind == "파생" && (string.IsNullOrEmpty(parent) || !rawSkills.ContainsKey(parent))) throw new InvalidDataException($"스킬 시트 {index}행의 파생 부모 ID가 올바르지 않습니다.");
            if (kind is not ("일반" or "궁극기" or "파생")) throw new InvalidDataException($"스킬 시트 {index}행의 스킬구분이 올바르지 않습니다.");
        }
        var effectMap = effects.GroupBy(x => x.Required("스킬ID", "배틀스킬효과", 0), StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<BattleEffect>)g.Select((x, i) => new BattleEffect(
            x.Required("ID", "배틀스킬효과", i + 2), BattleCsv.Int(x.Required("실행순서", "배틀스킬효과", i + 2), "배틀스킬효과", i + 2, "실행순서", 1), x["효과유형"], x["대상"], BattleCsv.Int(x["고정값"], "배틀스킬효과", i + 2, "고정값"), BattleCsv.Int(x["횟수"], "배틀스킬효과", i + 2, "횟수", 1), BattleCsv.Double(x["발동확률"], "배틀스킬효과", i + 2, "발동확률", 0, 1),
            BattleCsv.Int(x["지속턴"], "배틀스킬효과", i + 2, "지속턴"), EmptyAsNull(x["상태효과ID"]), BattleCsv.Int(x["최대중첩"], "배틀스킬효과", i + 2, "최대중첩"), EmptyAsNull(x["효과문구"]),
            EmptyAsNull(x["조건대상"]), EmptyAsNull(x["조건유형"]), EmptyAsNull(x["조건ID"]), EmptyAsNull(x["조건연산자"]), EmptyAsNull(x["조건값"]), EmptyAsNull(x["수치참조ID"]), EmptyAsNull(x["수치참조방식"]))).OrderBy(x => x.Order).ToArray());
        var skillMap = new Dictionary<string, BattleSkill>(StringComparer.Ordinal);
        foreach (var (row, index) in battleSkills.Select((x, i) => (x, i + 2)))
        {
            var id = row.Required("ID", "배틀스킬", index);
            if (!rawSkills.TryGetValue(id, out var sourceSkill)) throw new InvalidDataException($"배틀스킬 시트 {index}행이 존재하지 않는 스킬 ID '{id}'를 참조합니다.");
            if (!skillMap.TryAdd(id, new BattleSkill(id, sourceSkill["이름"], sourceSkill["스킬구분"], sourceSkill["부모스킬ID"], BattleCsv.Bool(row["활성화"], "배틀스킬", index, "활성화"), BattleCsv.Int(row["기본쿨다운"], "배틀스킬", index, "기본쿨다운"), BattleCsv.Int(row["최초쿨다운"], "배틀스킬", index, "최초쿨다운"), BattleCsv.Int(row["사용우선순위"], "배틀스킬", index, "사용우선순위"), 1d, effectMap.GetValueOrDefault(id, []), EmptyAsNull(row["자원유형"]), EmptyAsNull(row["자원소모"]), EmptyAsNull(row["자원획득"])))) throw new InvalidDataException($"배틀스킬 ID '{id}'가 중복되었습니다.");
        }
        var classMap = new Dictionary<string, BattleClass>(StringComparer.Ordinal);
        foreach (var (row, index) in classes.Select((x, i) => (x, i + 2)))
        {
            var ids = new[] { row["스킬1"], row["스킬2"], row["스킬3"], row["스킬4"], row["스킬5"], row["궁극기"] };
            // 아직 전투 스킬을 입력하지 않은 클래스는 원본 데이터로 보존하되 배틀 후보에서 제외합니다.
            // 일부만 비어 있는 행은 오타 또는 반쯤 반영된 데이터이므로 전체 스냅샷을 거부합니다.
            var isBattleReady = ids.All(id => !string.IsNullOrWhiteSpace(id));
            if (!isBattleReady && ids.Any(id => !string.IsNullOrWhiteSpace(id))) throw new InvalidDataException($"클래스 시트 {index}행의 배틀 스킬 구성이 일부만 입력되었습니다.");
            if (isBattleReady && ids.Any(id => !rawSkills.ContainsKey(id))) throw new InvalidDataException($"클래스 시트 {index}행이 존재하지 않는 스킬 ID를 참조합니다.");
            var id = row.Required("ID", "클래스", index);
            if (!classMap.TryAdd(id, new BattleClass(id, row.Required("이름", "클래스", index), isBattleReady ? ids : Array.Empty<string>(), isBattleReady))) throw new InvalidDataException($"클래스 ID '{id}'가 중복되었습니다.");
        }
        var resourceMap = Unique(resources, "배틀자원").ToDictionary(x => x["ID"], x => new BattleResource(x["ID"], x["이름"], x["분류"], BattleCsv.Int(x["최대값"], "배틀자원", 0, "최대값"), BattleCsv.Int(x["초기값"], "배틀자원", 0, "초기값"), BattleCsv.Int(x["지속턴"], "배틀자원", 0, "지속턴"), x["중첩방식"]), StringComparer.Ordinal);
        var statusMap = Unique(statuses, "배틀상태효과").ToDictionary(x => x["ID"], x => new BattleStatus(x["ID"], x["이름"], x["효과유형"], BattleCsv.Double(x["값"], "배틀상태효과", 0, "값"), x["설명"]), StringComparer.Ordinal);
        foreach (var effect in effectMap.Values.SelectMany(x => x))
        {
            if (effect.Type is "자원설정" or "자원증가" or "자원소모")
            {
                if (effect.StatusId is null || !resourceMap.ContainsKey(effect.StatusId)) throw new InvalidDataException($"배틀스킬효과 '{effect.Id}'가 존재하지 않는 자원을 참조합니다.");
            }
            else if (effect.Duration > 0 && effect.StatusId is { } statusId && !statusMap.ContainsKey(statusId))
                throw new InvalidDataException($"배틀스킬효과 '{effect.Id}'가 존재하지 않는 상태 효과 ID '{statusId}'를 참조합니다.");
            if (effect.ConditionType == "자원보유" && (effect.ConditionId is null || !resourceMap.ContainsKey(effect.ConditionId))) throw new InvalidDataException($"배틀스킬효과 '{effect.Id}'의 자원 조건 ID가 올바르지 않습니다.");
            if (effect.ConditionType == "분류자원미보유" && (effect.ConditionId is null || !resourceMap.Values.Any(x => x.Kind == effect.ConditionId))) throw new InvalidDataException($"배틀스킬효과 '{effect.Id}'의 자원 분류 조건이 올바르지 않습니다.");
            if (effect.NumericReferenceId is { } referenceId && !resourceMap.ContainsKey(referenceId)) throw new InvalidDataException($"배틀스킬효과 '{effect.Id}'의 수치 참조 자원이 올바르지 않습니다.");
        }
        var derivationList = Unique(derivations, "배틀스킬파생").Select((x, i) => new BattleDerivation(x["ID"], x["부모스킬ID"], x["파생스킬ID"], x["발동방식"], BattleCsv.Double(x["가중치"], "배틀스킬파생", i + 2, "가중치"), BattleCsv.Double(x["발동확률"], "배틀스킬파생", i + 2, "발동확률", 0, 1), EmptyAsNull(x["조건유형"]), EmptyAsNull(x["조건값"]), string.IsNullOrWhiteSpace(x["중복허용"]) ? false : BattleCsv.Bool(x["중복허용"], "배틀스킬파생", i + 2, "중복허용"), x["실행시점"], string.IsNullOrWhiteSpace(x["우선순위"]) ? 0 : BattleCsv.Int(x["우선순위"], "배틀스킬파생", i + 2, "우선순위"))).ToArray();
        if (derivationList.Any(x => !skillMap.ContainsKey(x.ParentSkillId) || !skillMap.ContainsKey(x.ChildSkillId))) throw new InvalidDataException("배틀스킬파생 시트가 존재하지 않는 배틀 스킬 ID를 참조합니다.");
        return new BattleDataSnapshot { Rules = new ReadOnlyDictionary<string, BattleRule>(ruleMap), Classes = new ReadOnlyDictionary<string, BattleClass>(classMap), Skills = new ReadOnlyDictionary<string, BattleSkill>(skillMap), Resources = new ReadOnlyDictionary<string, BattleResource>(resourceMap), Statuses = new ReadOnlyDictionary<string, BattleStatus>(statusMap), Derivations = derivationList, LoadedAt = loadedAt };
    }

    private static IEnumerable<Dictionary<string, string>> Unique(IReadOnlyList<Dictionary<string, string>> rows, string sheet)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, index) in rows.Select((x, i) => (x, i + 2)))
        {
            var id = row.Required("ID", sheet, index);
            if (!seen.Add(id)) throw new InvalidDataException($"{sheet} 시트의 ID '{id}'가 중복되었습니다.");
            yield return row;
        }
    }

    private static string? EmptyAsNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
