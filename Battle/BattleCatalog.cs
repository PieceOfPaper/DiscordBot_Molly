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

/// <summary>전투용 탭을 한 요청 묶음으로 가져옵니다. 엔진은 이 공급자를 직접 사용하지 않습니다.</summary>
public sealed class GoogleSheetsBattleSource(HttpClient client, string spreadsheetId) : IBattleDataSource
{
    public static readonly string[] SheetNames = ["클래스", "스킬", "패시브스킬", "배틀스킬", "배틀스킬효과", "배틀스킬파생", "배틀스킬AI", "배틀패시브", "배틀패시브효과", "배틀자원", "배틀상태효과", "배틀규칙", "배틀돌발이벤트", "배틀돌발이벤트효과", "생활스킬", "배틀생활스킬", "배틀생활스킬효과"];
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
        var passiveSkills = BattleCsv.Read(tables["패시브스킬"], "패시브스킬");
        var battleSkills = BattleCsv.Read(tables["배틀스킬"], "배틀스킬");
        var effects = BattleCsv.Read(tables["배틀스킬효과"], "배틀스킬효과");
        var derivations = BattleCsv.Read(tables["배틀스킬파생"], "배틀스킬파생");
        var battlePassives = BattleCsv.Read(tables["배틀패시브"], "배틀패시브");
        var passiveEffects = BattleCsv.Read(tables["배틀패시브효과"], "배틀패시브효과");
        var resources = BattleCsv.Read(tables["배틀자원"], "배틀자원");
        var statuses = BattleCsv.Read(tables["배틀상태효과"], "배틀상태효과");
        var rules = BattleCsv.Read(tables["배틀규칙"], "배틀규칙");
        // 나머지 표도 누락/깨진 CSV를 허용하지 않습니다. 상세 효과는 이후 엔진 단계에서 공통 모델로 확장합니다.
        foreach (var name in GoogleSheetsBattleSource.SheetNames.Except(["클래스", "스킬", "패시브스킬", "배틀스킬", "배틀스킬효과", "배틀스킬파생", "배틀스킬AI", "배틀패시브", "배틀패시브효과", "배틀자원", "배틀상태효과", "배틀규칙", "생활스킬", "배틀생활스킬", "배틀생활스킬효과"])) BattleCsv.Read(tables[name], name);
        BattleCsv.Headers(classes, "클래스", "ID", "이름", "스킬1", "스킬2", "스킬3", "스킬4", "스킬5", "궁극기", "패시브1", "패시브2", "패시브3", "패시브4", "패시브5", "패시브6");
        BattleCsv.Headers(skills, "스킬", "ID", "이름", "스킬구분", "부모스킬ID");
        BattleCsv.Headers(passiveSkills, "패시브스킬", "ID", "이름");
        BattleCsv.Headers(battleSkills, "배틀스킬", "ID", "활성화", "기본쿨다운", "최초쿨다운", "사용우선순위", "자원유형", "자원소모", "자원획득");
        BattleCsv.Headers(effects, "배틀스킬효과", "ID", "스킬ID", "실행순서", "효과유형", "대상", "고정값", "횟수", "발동확률", "지속턴", "상태효과ID", "최대중첩", "효과문구", "조건대상", "조건유형", "조건ID", "조건연산자", "조건값", "수치참조ID", "수치참조방식");
        BattleCsv.Headers(derivations, "배틀스킬파생", "ID", "부모스킬ID", "파생스킬ID", "발동방식", "가중치", "발동확률", "조건유형", "조건값", "중복허용", "실행시점", "우선순위");
        BattleCsv.Headers(battlePassives, "배틀패시브", "ID", "활성화");
        BattleCsv.Headers(passiveEffects, "배틀패시브효과", "ID", "패시브ID", "실행순서", "효과유형", "대상", "고정값", "횟수", "발동확률", "지속턴", "상태효과ID", "최대중첩", "효과문구", "조건대상", "조건유형", "조건ID", "조건연산자", "조건값", "수치참조ID", "수치참조방식", "발동시점", "대상스킬ID", "대상자원ID");
        BattleCsv.Headers(resources, "배틀자원", "ID", "이름", "분류", "최대값", "초기값", "지속턴", "중첩방식", "전투종료시제거");
        BattleCsv.Headers(statuses, "배틀상태효과", "ID", "이름", "효과유형", "값", "설명", "대상스킬ID");
        BattleCsv.Headers(rules, "배틀규칙", "ID", "분류", "값유형", "값", "설명");

        var ruleMap = Unique(rules, "배틀규칙").ToDictionary(x => x.Required("ID", "배틀규칙", 0), x => new BattleRule(x["ID"], x["분류"], x["값유형"], x["값"], x["설명"]), StringComparer.Ordinal);
        var rawSkills = Unique(skills, "스킬").ToDictionary(x => x["ID"], StringComparer.Ordinal);
        var rawPassives = Unique(passiveSkills, "패시브스킬").ToDictionary(x => x["ID"], StringComparer.Ordinal);
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
            EmptyAsNull(x["조건대상"]), EmptyAsNull(x["조건유형"]), EmptyAsNull(x["조건ID"]), EmptyAsNull(x["조건연산자"]), EmptyAsNull(x["조건값"]), EmptyAsNull(x["수치참조ID"]), EmptyAsNull(x["수치참조방식"]),
            EmptyAsNull(x.GetValueOrDefault("연속치명타배율", "")) is { } criticalMultiplier ? BattleCsv.Double(criticalMultiplier, "배틀스킬효과", i + 2, "연속치명타배율", 0, 1) : 1d)).OrderBy(x => x.Order).ToArray());
        var passiveEffectMap = passiveEffects.GroupBy(x => x.Required("패시브ID", "배틀패시브효과", 0), StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<BattleEffect>)g.Select((x, i) => new BattleEffect(
            x.Required("ID", "배틀패시브효과", i + 2), BattleCsv.Int(x.Required("실행순서", "배틀패시브효과", i + 2), "배틀패시브효과", i + 2, "실행순서", 1), x["효과유형"], x["대상"], BattleCsv.Int(x["고정값"], "배틀패시브효과", i + 2, "고정값"), BattleCsv.Int(x["횟수"], "배틀패시브효과", i + 2, "횟수", 1), BattleCsv.Double(x["발동확률"], "배틀패시브효과", i + 2, "발동확률", 0, 1),
            BattleCsv.Int(x["지속턴"], "배틀패시브효과", i + 2, "지속턴"), EmptyAsNull(x["상태효과ID"]), BattleCsv.Int(x["최대중첩"], "배틀패시브효과", i + 2, "최대중첩"), EmptyAsNull(x["효과문구"]),
            EmptyAsNull(x["조건대상"]), EmptyAsNull(x["조건유형"]), EmptyAsNull(x["조건ID"]), EmptyAsNull(x["조건연산자"]), EmptyAsNull(x["조건값"]), EmptyAsNull(x["수치참조ID"]), EmptyAsNull(x["수치참조방식"]),
            1d, x.Required("발동시점", "배틀패시브효과", i + 2), EmptyAsNull(x["대상스킬ID"]), EmptyAsNull(x["대상자원ID"]),
            // 재발동대기턴은 선택 컬럼이다. 활력처럼 같은 효과가 한 번 발동한 뒤 보유자의 N턴 동안 다시 발동하지 않게 한다.
            EmptyAsNull(x.GetValueOrDefault("재발동대기턴", "")) is { } reactivation ? BattleCsv.Int(reactivation, "배틀패시브효과", i + 2, "재발동대기턴") : 0)).OrderBy(x => x.Order).ToArray());
        var skillMap = new Dictionary<string, BattleSkill>(StringComparer.Ordinal);
        foreach (var (row, index) in battleSkills.Select((x, i) => (x, i + 2)))
        {
            var id = row.Required("ID", "배틀스킬", index);
            if (!rawSkills.TryGetValue(id, out var sourceSkill)) throw new InvalidDataException($"배틀스킬 시트 {index}행이 존재하지 않는 스킬 ID '{id}'를 참조합니다.");
            if (!skillMap.TryAdd(id, new BattleSkill(id, sourceSkill["이름"], sourceSkill["스킬구분"], sourceSkill["부모스킬ID"], BattleCsv.Bool(row["활성화"], "배틀스킬", index, "활성화"), BattleCsv.Int(row["기본쿨다운"], "배틀스킬", index, "기본쿨다운"), BattleCsv.Int(row["최초쿨다운"], "배틀스킬", index, "최초쿨다운"), BattleCsv.Int(row["사용우선순위"], "배틀스킬", index, "사용우선순위"), 1d, effectMap.GetValueOrDefault(id, []), EmptyAsNull(row["자원유형"]), EmptyAsNull(row["자원소모"]), EmptyAsNull(row["자원획득"])) { UltimateQuote = EmptyAsNull(row.GetValueOrDefault("궁극기문구", "")) })) throw new InvalidDataException($"배틀스킬 ID '{id}'가 중복되었습니다.");
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
            // 패시브는 스킬과 달리 클래스마다 개수가 다를 수 있어 부분 채움을 허용합니다. 아직 배틀패시브가 없는 ID도
            // 원본에는 존재해야 하며(오타 방지), 구현 전까지는 전투 시작 시 조용히 무시됩니다(스킬의 활성화=FALSE와 동일한 패턴).
            var passiveIds = new[] { row["패시브1"], row["패시브2"], row["패시브3"], row["패시브4"], row["패시브5"], row["패시브6"] }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (passiveIds.Any(id => !rawPassives.ContainsKey(id))) throw new InvalidDataException($"클래스 시트 {index}행이 존재하지 않는 패시브 ID를 참조합니다.");
            var id = row.Required("ID", "클래스", index);
            if (!classMap.TryAdd(id, new BattleClass(id, row.Required("이름", "클래스", index), isBattleReady ? ids : Array.Empty<string>(), isBattleReady, passiveIds))) throw new InvalidDataException($"클래스 ID '{id}'가 중복되었습니다.");
        }
        var passiveMap = new Dictionary<string, BattlePassive>(StringComparer.Ordinal);
        foreach (var (row, index) in battlePassives.Select((x, i) => (x, i + 2)))
        {
            var id = row.Required("ID", "배틀패시브", index);
            if (!rawPassives.ContainsKey(id)) throw new InvalidDataException($"배틀패시브 시트 {index}행이 존재하지 않는 패시브 ID '{id}'를 참조합니다.");
            var passive = new BattlePassive(id, BattleCsv.Bool(row["활성화"], "배틀패시브", index, "활성화"), passiveEffectMap.GetValueOrDefault(id, []));
            if (passive.Enabled && passive.Effects.Count == 0) throw new InvalidDataException($"배틀패시브 '{id}'가 활성화되어 있지만 효과가 하나도 없습니다.");
            if (!passiveMap.TryAdd(id, passive)) throw new InvalidDataException($"배틀패시브 ID '{id}'가 중복되었습니다.");
        }
        var resourceMap = Unique(resources, "배틀자원").ToDictionary(x => x["ID"], x => new BattleResource(x["ID"], x["이름"], x["분류"], BattleCsv.Int(x["최대값"], "배틀자원", 0, "최대값"), BattleCsv.Int(x["초기값"], "배틀자원", 0, "초기값"), BattleCsv.Int(x["지속턴"], "배틀자원", 0, "지속턴"), x["중첩방식"]) { HideLog = OptionalBool(x, "로그숨김", "배틀자원") }, StringComparer.Ordinal);
        if (resourceMap.Values.FirstOrDefault(x => x.Stacking == "개별" && x.Duration <= 0) is { } untimedStack) throw new InvalidDataException($"배틀자원 '{untimedStack.Id}'의 중첩방식=개별은 지속턴이 1 이상이어야 합니다.");
        var statusMap = Unique(statuses, "배틀상태효과").Select((x, i) => ParseStatus(x, i + 2)).ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var status in statusMap.Values)
            if (status.TargetSkillId is { } targetSkillId && !skillMap.ContainsKey(targetSkillId))
                throw new InvalidDataException($"배틀상태효과 '{status.Id}'의 대상스킬ID가 존재하지 않는 배틀 스킬 ID '{targetSkillId}'를 참조합니다.");
        // 스킬피해증가는 대상스킬ID(스킬 또는 부모 스킬)로 한정한 피해 증가다. 대상 없이 쓰면 주는피해증가와 같아지므로 거부한다.
        foreach (var status in statusMap.Values)
            if (status.HasEffectType("스킬피해증가") && status.TargetSkillId is null)
                throw new InvalidDataException($"배틀상태효과 '{status.Id}'의 스킬피해증가에는 대상스킬ID가 필요합니다.");
        // 턴당자원증가는 대상자원ID의 자원을 턴 시작마다 채운다. 자원이 없으면 효과가 조용히 사라지므로 로딩 단계에서 거부한다.
        foreach (var status in statusMap.Values)
            if (status.HasEffectType("턴당자원증가") && (status.TargetResourceId is not { } turnResourceId || !resourceMap.ContainsKey(turnResourceId)))
                throw new InvalidDataException($"배틀상태효과 '{status.Id}'의 턴당자원증가에는 존재하는 대상자원ID가 필요합니다.");
        foreach (var status in statusMap.Values)
            if (status.SustainResourceId is { } sustainResourceId && !resourceMap.ContainsKey(sustainResourceId))
                throw new InvalidDataException($"배틀상태효과 '{status.Id}'의 유지자원ID가 존재하지 않는 자원 ID '{sustainResourceId}'를 참조합니다.");
        foreach (var status in statusMap.Values)
            if (status.StackResourceId is { } stackResourceId && !resourceMap.ContainsKey(stackResourceId))
                throw new InvalidDataException($"배틀상태효과 '{status.Id}'의 중첩자원ID가 존재하지 않는 자원 ID '{stackResourceId}'를 참조합니다.");
        var passiveTriggers = new HashSet<string>(["전투시작", "자원획득시", "자원최대치도달시", "자원소진시", "브레이크발생시", "스킬사용완료시", "스킬적중완료시", "치명타적중시", "치명타미적중시", "피격시", "회복적용시", "추가타적중시", "기본공격적중시"], StringComparer.Ordinal);
        void ValidateEffect(BattleEffect effect, string sheet)
        {
            if (effect.Type is "자원설정" or "자원증가" or "자원소모")
            {
                if (effect.StatusId is null || !resourceMap.ContainsKey(effect.StatusId)) throw new InvalidDataException($"{sheet} '{effect.Id}'가 존재하지 않는 자원을 참조합니다.");
            }
            else if ((effect.Duration > 0 || effect.Type is "상태해제" or "지속피해폭발") && effect.StatusId is { } statusId && !statusMap.ContainsKey(statusId))
                throw new InvalidDataException($"{sheet} '{effect.Id}'가 존재하지 않는 상태 효과 ID '{statusId}'를 참조합니다.");
            // 지속피해폭발(포이즌 익스플로전)은 폭발시킬 지속 피해 상태가 없으면 조용히 아무 일도 하지 않으므로 상태 ID를 필수로 검사한다.
            if (effect.Type == "지속피해폭발" && effect.StatusId is null) throw new InvalidDataException($"{sheet} '{effect.Id}'의 지속피해폭발에는 상태효과ID가 필요합니다.");
            // 브레이크익스텐드(아이시클 섀터)는 브레이크를 바꿔 끼울 익스텐드 상태가 필요하다. 브레이크 효과유형이 없으면 행동 상실이 사라진다.
            if (effect.Type == "브레이크익스텐드" && (effect.StatusId is null || !statusMap.TryGetValue(effect.StatusId, out var extended) || !extended.HasEffectType("브레이크"))) throw new InvalidDataException($"{sheet} '{effect.Id}'의 브레이크익스텐드에는 효과유형에 브레이크가 있는 상태효과ID가 필요합니다.");
            if (effect.ConditionType == "자원보유" && (effect.ConditionId is null || !resourceMap.ContainsKey(effect.ConditionId))) throw new InvalidDataException($"{sheet} '{effect.Id}'의 자원 조건 ID가 올바르지 않습니다.");
            if (effect.ConditionType == "분류자원미보유" && (effect.ConditionId is null || !resourceMap.Values.Any(x => x.Kind == effect.ConditionId))) throw new InvalidDataException($"{sheet} '{effect.Id}'의 자원 분류 조건이 올바르지 않습니다.");
            if (effect.ConditionType is "상태효과보유" or "상태효과미보유" && (effect.ConditionId is null || !statusMap.ContainsKey(effect.ConditionId))) throw new InvalidDataException($"{sheet} '{effect.Id}'의 상태 조건 ID가 올바르지 않습니다.");
            if (effect.ConditionType is "상태효과유형보유" or "상태효과유형미보유" && (effect.ConditionId is null || !statusMap.Values.Any(x => x.HasEffectType(effect.ConditionId)))) throw new InvalidDataException($"{sheet} '{effect.Id}'의 상태 효과유형 조건 '{effect.ConditionId}'을(를) 가진 상태가 없습니다.");
            if (effect.NumericReferenceId is { } referenceId && !resourceMap.ContainsKey(referenceId)) throw new InvalidDataException($"{sheet} '{effect.Id}'의 수치 참조 자원이 올바르지 않습니다.");
        }
        foreach (var effect in effectMap.Values.SelectMany(x => x))
        {
            ValidateEffect(effect, "배틀스킬효과");
            if (effect.ConditionType == "효과미발동") throw new InvalidDataException($"배틀스킬효과 '{effect.Id}'의 효과미발동 조건은 배틀패시브효과에서만 쓸 수 있습니다.");
        }
        foreach (var group in passiveEffectMap.Values)
            foreach (var effect in group.Where(x => x.ConditionType == "효과미발동"))
                if (!group.Any(x => x.Id == effect.ConditionId && x.Order < effect.Order && x.Trigger == effect.Trigger))
                    throw new InvalidDataException($"배틀패시브효과 '{effect.Id}'의 효과미발동 조건ID는 같은 패시브·발동시점에서 먼저 실행되는 효과 ID여야 합니다.");
        foreach (var effect in passiveEffectMap.Values.SelectMany(x => x))
        {
            ValidateEffect(effect, "배틀패시브효과");
            if (effect.Trigger is not { } trigger || !passiveTriggers.Contains(trigger)) throw new InvalidDataException($"배틀패시브효과 '{effect.Id}'의 발동시점이 올바르지 않습니다.");
            if (effect.TriggerSkillId is { } triggerSkillId && !skillMap.ContainsKey(triggerSkillId)) throw new InvalidDataException($"배틀패시브효과 '{effect.Id}'의 대상스킬ID가 존재하지 않는 배틀 스킬 ID '{triggerSkillId}'를 참조합니다.");
            if (effect.TriggerResourceId is { } triggerResourceId && !resourceMap.ContainsKey(triggerResourceId) && !resourceMap.Values.Any(x => x.Kind == triggerResourceId)) throw new InvalidDataException($"배틀패시브효과 '{effect.Id}'의 대상자원ID가 존재하지 않는 자원 ID 또는 분류 '{triggerResourceId}'를 참조합니다.");
        }
        if (passiveEffectMap.Keys.Any(id => !passiveMap.ContainsKey(id))) throw new InvalidDataException("배틀패시브효과 시트가 존재하지 않는 배틀패시브 ID를 참조합니다.");
        var derivationList = Unique(derivations, "배틀스킬파생").Select((x, i) => new BattleDerivation(x["ID"], x["부모스킬ID"], x["파생스킬ID"], x["발동방식"], BattleCsv.Double(x["가중치"], "배틀스킬파생", i + 2, "가중치"), BattleCsv.Double(x["발동확률"], "배틀스킬파생", i + 2, "발동확률", 0, 1), EmptyAsNull(x["조건유형"]), EmptyAsNull(x["조건값"]), string.IsNullOrWhiteSpace(x["중복허용"]) ? false : BattleCsv.Bool(x["중복허용"], "배틀스킬파생", i + 2, "중복허용"), x["실행시점"], string.IsNullOrWhiteSpace(x["우선순위"]) ? 0 : BattleCsv.Int(x["우선순위"], "배틀스킬파생", i + 2, "우선순위"))).ToArray();
        if (derivationList.Any(x => !skillMap.ContainsKey(x.ParentSkillId) || !skillMap.ContainsKey(x.ChildSkillId))) throw new InvalidDataException("배틀스킬파생 시트가 존재하지 않는 배틀 스킬 ID를 참조합니다.");
        // 엔진이 모르는 조건유형은 항상 거짓으로 판정되어 파생이 조용히 사라지므로 로딩 단계에서 거부한다.
        if (derivationList.FirstOrDefault(x => x.ConditionType is not (null or "자원보유" or "상태효과보유" or "상대상태효과보유" or "악상")) is { } unknownCondition) throw new InvalidDataException($"배틀스킬파생 '{unknownCondition.Id}'의 조건유형 '{unknownCondition.ConditionType}'을(를) 지원하지 않습니다.");
        if (derivationList.FirstOrDefault(x => x.ConditionType == "상대상태효과보유" && (x.ConditionValue is null || !statusMap.ContainsKey(x.ConditionValue))) is { } unknownStatus) throw new InvalidDataException($"배틀스킬파생 '{unknownStatus.Id}'의 상대상태효과보유 조건값이 존재하지 않는 상태 ID입니다.");
        var skillAiRules = ParseSkillAiRules(BattleCsv.Read(tables["배틀스킬AI"], "배틀스킬AI"), skillMap, resourceMap, statusMap);
        return new BattleDataSnapshot { Rules = new ReadOnlyDictionary<string, BattleRule>(ruleMap), Classes = new ReadOnlyDictionary<string, BattleClass>(classMap), Skills = new ReadOnlyDictionary<string, BattleSkill>(skillMap), BattleReadyClassIds = BattleDataSnapshot.ComputeBattleReadyClassIds(classMap, skillMap), Passives = new ReadOnlyDictionary<string, BattlePassive>(passiveMap), Resources = new ReadOnlyDictionary<string, BattleResource>(resourceMap), Statuses = new ReadOnlyDictionary<string, BattleStatus>(statusMap), Derivations = derivationList, SkillAiRules = skillAiRules, LifeSkills = ParseLifeSkills(tables), LoadedAt = loadedAt };
    }

    /// <summary>수치를 비교하는 AI 조건. 비례 가산(단계당가산)은 이 조건에서만 쓸 수 있다.</summary>
    private static readonly HashSet<string> AiNumericConditionTypes = new(["HP비율", "자원보유", "브레이크게이지", "해로운상태개수"], StringComparer.Ordinal);
    /// <summary>보유 여부만 보는 AI 조건. 조건ID에 상태 ID(상태효과보유) 또는 효과유형(상태효과유형보유, 예: 브레이크)을 적는다.</summary>
    private static readonly HashSet<string> AiPresenceConditionTypes = new(["상태효과보유", "상태효과미보유", "상태효과유형보유", "상태효과유형미보유"], StringComparer.Ordinal);

    /// <summary>
    /// <c>배틀스킬AI</c>를 스킬 ID별 조건 목록으로 만든다. 엔진이 모르는 조건은 조용히 거짓이 되어 가중치가 사라지므로 로딩 단계에서 거부한다.
    /// 활성화=FALSE 행도 검증한 뒤 제외한다.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<BattleSkillAiRule>> ParseSkillAiRules(IReadOnlyList<Dictionary<string, string>> rows,
        IReadOnlyDictionary<string, BattleSkill> skills, IReadOnlyDictionary<string, BattleResource> resources, IReadOnlyDictionary<string, BattleStatus> statuses)
    {
        const string sheet = "배틀스킬AI";
        // 헤더만 있는 시트는 AI 조건이 없다는 뜻이다. BattleCsv.Headers는 첫 행에서 헤더를 읽으므로 행이 없으면 검사를 건너뛴다.
        if (rows.Count > 0) BattleCsv.Headers(rows, sheet, "ID", "스킬ID", "활성화", "조건대상", "조건유형", "조건ID", "조건연산자", "조건값", "가산가중치", "단계크기", "단계당가산", "최대가산");
        var effectTypes = statuses.Values.SelectMany(x => x.EffectTypes).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, List<BattleSkillAiRule>>(StringComparer.Ordinal);
        foreach (var (row, index) in Unique(rows, sheet).Select((x, i) => (x, i + 2)))
        {
            var id = row["ID"];
            var skillId = row.Required("스킬ID", sheet, index);
            if (!skills.TryGetValue(skillId, out var skill)) throw new InvalidDataException($"{sheet} '{id}'가 존재하지 않는 배틀 스킬 ID '{skillId}'를 참조합니다.");
            if (skill.Kind == "파생") throw new InvalidDataException($"{sheet} '{id}'의 스킬 '{skillId}'은(는) 파생 전용이라 행동 후보로 추첨되지 않습니다.");
            var target = row.Required("조건대상", sheet, index);
            if (target is not ("자신" or "상대")) throw new InvalidDataException($"{sheet} '{id}'의 조건대상은 자신 또는 상대여야 합니다.");
            var type = row.Required("조건유형", sheet, index);
            var conditionId = EmptyAsNull(row["조건ID"]);
            var op = EmptyAsNull(row["조건연산자"]);
            var bonus = BattleCsv.Double(row.Required("가산가중치", sheet, index), sheet, index, "가산가중치", double.MinValue);
            var stepBonus = EmptyAsNull(row["단계당가산"]) is { } rawStepBonus ? BattleCsv.Double(rawStepBonus, sheet, index, "단계당가산", double.MinValue) : 0d;
            var stepSize = 0d;
            var maxBonus = 0d;
            var conditionValue = 0d;
            if (AiNumericConditionTypes.Contains(type))
            {
                if (op is null || !ConditionOperators.Contains(op)) throw new InvalidDataException($"{sheet} '{id}'의 조건연산자 '{op}'을(를) 지원하지 않습니다.");
                conditionValue = BattleCsv.Double(row.Required("조건값", sheet, index), sheet, index, "조건값");
                if (type == "HP비율" && conditionValue > 1) throw new InvalidDataException($"{sheet} '{id}'의 HP비율 조건값은 0~1 비율로 적습니다(50% = 0.5).");
                if (type == "자원보유" && (conditionId is null || !resources.ContainsKey(conditionId))) throw new InvalidDataException($"{sheet} '{id}'의 자원 조건 ID가 올바르지 않습니다.");
                if (type != "자원보유" && conditionId is not null) throw new InvalidDataException($"{sheet} '{id}'의 {type} 조건은 조건ID를 비워 둡니다.");
            }
            else if (AiPresenceConditionTypes.Contains(type))
            {
                var known = conditionId is not null && (type.StartsWith("상태효과유형", StringComparison.Ordinal) ? effectTypes.Contains(conditionId) : statuses.ContainsKey(conditionId));
                if (!known) throw new InvalidDataException($"{sheet} '{id}'의 {type} 조건ID '{conditionId}'가 올바르지 않습니다.");
                if (op is not null || EmptyAsNull(row["조건값"]) is not null) throw new InvalidDataException($"{sheet} '{id}'의 {type} 조건은 조건연산자·조건값을 비워 둡니다.");
            }
            else throw new InvalidDataException($"{sheet} '{id}'의 조건유형 '{type}'을(를) 지원하지 않습니다.");
            if (stepBonus != 0)
            {
                // 비례 가산은 조건값에서 얼마나 멀어졌는지를 센다. 같다(=) 조건과 보유 여부 조건에는 거리가 없다.
                if (!AiNumericConditionTypes.Contains(type) || op is "=" or "==") throw new InvalidDataException($"{sheet} '{id}'의 단계당가산은 수치 조건의 <, <=, >, >= 에서만 쓸 수 있습니다.");
                stepSize = BattleCsv.Double(row.Required("단계크기", sheet, index), sheet, index, "단계크기", double.Epsilon);
                maxBonus = BattleCsv.Double(row.Required("최대가산", sheet, index), sheet, index, "최대가산", double.MinValue);
                if (Math.Sign(maxBonus) != Math.Sign(stepBonus) || Math.Abs(maxBonus) < Math.Abs(bonus)) throw new InvalidDataException($"{sheet} '{id}'의 최대가산은 단계당가산과 부호가 같고 절댓값이 가산가중치 이상이어야 합니다.");
            }
            else if (EmptyAsNull(row["단계크기"]) is not null || EmptyAsNull(row["최대가산"]) is not null) throw new InvalidDataException($"{sheet} '{id}'는 단계당가산이 없으면 단계크기·최대가산을 비워 둡니다.");
            if (bonus == 0 && stepBonus == 0) throw new InvalidDataException($"{sheet} '{id}'의 가산가중치와 단계당가산이 모두 0입니다.");
            if (!BattleCsv.Bool(row["활성화"], sheet, index, "활성화")) continue;
            if (!result.TryGetValue(skillId, out var list)) result[skillId] = list = [];
            list.Add(new BattleSkillAiRule(id, skillId, target, type, conditionId, op, conditionValue, bonus, stepSize, stepBonus, maxBonus));
        }
        return new ReadOnlyDictionary<string, IReadOnlyList<BattleSkillAiRule>>(result.ToDictionary(x => x.Key, x => (IReadOnlyList<BattleSkillAiRule>)x.Value.ToArray(), StringComparer.Ordinal));
    }

    /// <summary>생활력 성장과 무관해 배틀생활스킬을 만들지 않는 원본 생활스킬.</summary>
    private static readonly HashSet<string> NonBattleLifeSkillNames = new(["연금술"], StringComparer.Ordinal);
    private static readonly HashSet<string> LifeConditionTypes = new(["생존", "HP비율", "해로운상태개수", "재사용대기중스킬개수"], StringComparer.Ordinal);
    private static readonly HashSet<string> ConditionOperators = new(["=", "==", "<", "<=", ">", ">="], StringComparer.Ordinal);
    /// <summary>생활스킬 효과유형별로 허용하는 계수기준. 엔진이 모르는 조합을 조용히 무시하지 않도록 로딩 단계에서 거부한다.</summary>
    private static readonly Dictionary<string, string> LifeEffectBasis = new(StringComparer.Ordinal)
    {
        ["피해"] = "공격력", ["회복"] = "최대HP", ["지속회복"] = "최대HP",
        ["브레이크피해"] = "개수", ["해로운상태제거"] = "개수", ["쿨다운감소"] = "턴", ["브레이크면역"] = "턴",
        ["받는피해감소"] = "비율", ["주는피해증가"] = "비율", ["다음피해감소"] = "비율", ["다음행동피해감소"] = "비율", ["회피확률증가"] = "비율",
        ["다음스킬피해증가"] = "비율", ["다음스킬치명타확률증가"] = "비율", ["다음스킬치명타피해증가"] = "비율"
    };

    private static IReadOnlyList<BattleLifeSkill> ParseLifeSkills(IReadOnlyDictionary<string, string> tables)
    {
        var sourceRows = BattleCsv.Read(tables["생활스킬"], "생활스킬");
        var skillRows = BattleCsv.Read(tables["배틀생활스킬"], "배틀생활스킬");
        var effectRows = BattleCsv.Read(tables["배틀생활스킬효과"], "배틀생활스킬효과");
        // BattleCsv.Headers는 첫 행에서 헤더를 읽는다. 행이 없는 시트는 불러올 배틀생활스킬이 없다는 뜻이므로 헤더 검사를 건너뛴다.
        if (sourceRows.Count > 0) BattleCsv.Headers(sourceRows, "생활스킬", "이름");
        if (skillRows.Count > 0) BattleCsv.Headers(skillRows, "배틀생활스킬", "ID", "생활스킬이름", "활성화", "기본사용확률", "생활력기준값", "생활력확률보정계수", "최대사용확률", "전투당최대횟수", "선택가중치", "조건대상", "조건유형", "조건연산자", "조건값", "행동문구");
        if (effectRows.Count > 0) BattleCsv.Headers(effectRows, "배틀생활스킬효과", "ID", "배틀생활스킬ID", "실행순서", "효과유형", "대상", "계수기준", "계수", "고정값", "생활력비례", "생활력기준값", "생활력최소배율", "생활력최대배율", "횟수", "지속턴", "효과문구");
        var sourceNames = sourceRows.Select((x, i) => x.Required("이름", "생활스킬", i + 2)).ToHashSet(StringComparer.Ordinal);

        var effects = new List<BattleLifeSkillEffect>();
        foreach (var (row, index) in Unique(effectRows, "배틀생활스킬효과").Select((x, i) => (x, i + 2)))
        {
            const string sheet = "배틀생활스킬효과";
            var type = row.Required("효과유형", sheet, index);
            if (!LifeEffectBasis.TryGetValue(type, out var basis)) throw new InvalidDataException($"{sheet} 시트 {index}행의 효과유형 '{type}'을(를) 지원하지 않습니다.");
            if (row["계수기준"] != basis) throw new InvalidDataException($"{sheet} 시트 {index}행의 {type} 효과는 계수기준이 '{basis}'여야 합니다.");
            var target = row.Required("대상", sheet, index);
            if (target is not ("자신" or "상대")) throw new InvalidDataException($"{sheet} 시트 {index}행의 대상은 자신 또는 상대여야 합니다.");
            var group = EmptyAsNull(row.GetValueOrDefault("선택그룹", ""));
            var groupWeight = group is null ? 0d : BattleCsv.Double(row.GetValueOrDefault("선택가중치", ""), sheet, index, "선택가중치", double.Epsilon);
            var effect = new BattleLifeSkillEffect(row["ID"], row.Required("배틀생활스킬ID", sheet, index), BattleCsv.Int(row["실행순서"], sheet, index, "실행순서", 1), type, target,
                basis, BattleCsv.Double(row["계수"], sheet, index, "계수"), BattleCsv.Int(row["고정값"], sheet, index, "고정값"), BattleCsv.Bool(row["생활력비례"], sheet, index, "생활력비례"),
                BattleCsv.Double(row["생활력기준값"], sheet, index, "생활력기준값", 1), BattleCsv.Double(row["생활력최소배율"], sheet, index, "생활력최소배율"), BattleCsv.Double(row["생활력최대배율"], sheet, index, "생활력최대배율"),
                BattleCsv.Int(row["횟수"], sheet, index, "횟수", 1), BattleCsv.Int(row["지속턴"], sheet, index, "지속턴"), EmptyAsNull(row["효과문구"]), group, groupWeight);
            if (effect.MinMultiplier > effect.MaxMultiplier) throw new InvalidDataException($"{sheet} 시트 {index}행의 생활력최소배율이 최대배율보다 큽니다.");
            if (basis is "공격력" or "최대HP" or "비율" && effect.Coefficient <= 0) throw new InvalidDataException($"{sheet} 시트 {index}행의 계수는 0보다 커야 합니다.");
            if (basis is "개수" && effect.FixedValue <= 0 || type == "쿨다운감소" && effect.FixedValue <= 0) throw new InvalidDataException($"{sheet} 시트 {index}행의 고정값은 1 이상이어야 합니다.");
            // 상태형 효과는 지속턴 동안 유지된다. 다음 스킬 강화는 다음 전투 스킬에 소모될 때까지 남으므로 지속턴을 1회 표기로만 쓴다.
            if (basis is "비율" || type is "브레이크면역" or "지속회복")
                if (effect.Duration < 1) throw new InvalidDataException($"{sheet} 시트 {index}행의 {type} 효과는 지속턴이 1 이상이어야 합니다.");
            effects.Add(effect);
        }

        var result = new List<BattleLifeSkill>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, index) in Unique(skillRows, "배틀생활스킬").Select((x, i) => (x, i + 2)))
        {
            const string sheet = "배틀생활스킬";
            var id = row["ID"];
            var name = row.Required("생활스킬이름", sheet, index);
            if (!sourceNames.Contains(name)) throw new InvalidDataException($"{sheet} 시트 {index}행의 생활스킬이름 '{name}'이(가) 생활스킬 시트에 없습니다.");
            if (NonBattleLifeSkillNames.Contains(name)) throw new InvalidDataException($"{sheet} 시트 {index}행의 '{name}'은(는) 생활력과 무관해 배틀생활스킬로 만들지 않습니다.");
            if (!usedNames.Add(name)) throw new InvalidDataException($"{sheet} 시트의 생활스킬이름 '{name}'이(가) 중복되었습니다.");
            var conditionTarget = row.Required("조건대상", sheet, index);
            var conditionType = row.Required("조건유형", sheet, index);
            var conditionOperator = row.Required("조건연산자", sheet, index);
            if (conditionTarget is not ("자신" or "상대")) throw new InvalidDataException($"{sheet} 시트 {index}행의 조건대상은 자신 또는 상대여야 합니다.");
            if (!LifeConditionTypes.Contains(conditionType)) throw new InvalidDataException($"{sheet} 시트 {index}행의 조건유형 '{conditionType}'을(를) 지원하지 않습니다.");
            if (!ConditionOperators.Contains(conditionOperator)) throw new InvalidDataException($"{sheet} 시트 {index}행의 조건연산자 '{conditionOperator}'을(를) 지원하지 않습니다.");
            var skillEffects = effects.Where(x => x.LifeSkillId == id).OrderBy(x => x.Order).ToArray();
            var skill = new BattleLifeSkill(id, name, BattleCsv.Bool(row["활성화"], sheet, index, "활성화"),
                BattleCsv.Double(row["기본사용확률"], sheet, index, "기본사용확률", 0, 1), BattleCsv.Double(row["생활력기준값"], sheet, index, "생활력기준값", 1),
                BattleCsv.Double(row["생활력확률보정계수"], sheet, index, "생활력확률보정계수", 0, 1), BattleCsv.Double(row["최대사용확률"], sheet, index, "최대사용확률", 0, 1),
                BattleCsv.Int(row["전투당최대횟수"], sheet, index, "전투당최대횟수", 1), BattleCsv.Double(row["선택가중치"], sheet, index, "선택가중치", double.Epsilon),
                conditionTarget, conditionType, conditionOperator, BattleCsv.Double(row.Required("조건값", sheet, index), sheet, index, "조건값"),
                row.Required("행동문구", sheet, index), skillEffects);
            if (skill.Enabled && skillEffects.Length == 0) throw new InvalidDataException($"배틀생활스킬 '{id}'가 활성화되어 있지만 효과가 하나도 없습니다.");
            result.Add(skill);
        }
        var missing = sourceNames.Where(x => !NonBattleLifeSkillNames.Contains(x) && !usedNames.Contains(x)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"배틀생활스킬 시트에 생활스킬이 누락되었습니다: {string.Join(", ", missing)}");
        var knownIds = result.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (effects.FirstOrDefault(x => !knownIds.Contains(x.LifeSkillId)) is { } orphan) throw new InvalidDataException($"배틀생활스킬효과 '{orphan.Id}'가 존재하지 않는 배틀생활스킬 ID '{orphan.LifeSkillId}'를 참조합니다.");
        // 사용 판정은 후보를 고르기 전에 한 번만 하므로, 행마다 다른 판정 값이 있으면 어느 값을 쓸지 정의되지 않는다.
        var enabled = result.Where(x => x.Enabled).ToArray();
        if (enabled.Select(x => (x.BaseChance, x.LifeReference, x.LifeChanceCoefficient, x.MaxChance)).Distinct().Count() > 1)
            throw new InvalidDataException("배틀생활스킬의 기본사용확률·생활력기준값·생활력확률보정계수·최대사용확률은 활성화된 모든 행에서 같아야 합니다.");
        return result;
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

    private static BattleStatus ParseStatus(Dictionary<string, string> row, int index)
    {
        var effectTypes = row["효과유형"].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var value = BattleCsv.Double(row["값"], "배틀상태효과", index, "값");
        // 효과유형마다 값이 다르면 선택 컬럼 효과별값에 `1|0.1`처럼 효과유형 순서대로 적는다. 비어 있으면 모든 효과유형이 값을 공유한다.
        // 값 컬럼에 섞어 쓰지 않는 이유: gviz CSV는 숫자 열의 문자열 셀을 빈 값으로 내보낸다.
        var values = (EmptyAsNull(row.GetValueOrDefault("효과별값", "")) ?? "").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => BattleCsv.Double(x, "배틀상태효과", index, "효과별값")).ToArray();
        if (values.Length > 0 && values.Length != effectTypes.Length) throw new InvalidDataException($"배틀상태효과 시트 {index}행의 효과별값 개수가 효과유형 개수와 다릅니다.");
        // 시너지·지속방식은 선택 컬럼이다. 시너지에는 [시너지] 옵션인 효과유형을 `|`로 적는다.
        var synergy = (EmptyAsNull(row.GetValueOrDefault("시너지", "")) ?? "").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (synergy.Any(x => !effectTypes.Contains(x, StringComparer.Ordinal))) throw new InvalidDataException($"배틀상태효과 시트 {index}행의 시너지에 효과유형에 없는 항목이 있습니다.");
        var durationMode = EmptyAsNull(row.GetValueOrDefault("지속방식", "")) ?? "갱신";
        if (durationMode is not ("갱신" or "누적")) throw new InvalidDataException($"배틀상태효과 시트 {index}행의 지속방식은 갱신 또는 누적이어야 합니다.");
        return new BattleStatus(row.Required("ID", "배틀상태효과", index), row["이름"], row["효과유형"], value, row["설명"], EmptyAsNull(row["대상스킬ID"]), EmptyAsNull(row.GetValueOrDefault("중첩자원ID", "")))
        {
            Values = values,
            SynergyTypes = synergy,
            AccumulatesDuration = durationMode == "누적",
            TargetResourceId = EmptyAsNull(row.GetValueOrDefault("대상자원ID", "")),
            SustainResourceId = EmptyAsNull(row.GetValueOrDefault("유지자원ID", "")),
            HideLog = OptionalBool(row, "로그숨김", "배틀상태효과", index)
        };
    }

    private static string? EmptyAsNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    /// <summary>선택 불리언 컬럼. 컬럼이 없거나 비어 있으면 false다.</summary>
    private static bool OptionalBool(Dictionary<string, string> row, string header, string sheet, int index = 0)
        => EmptyAsNull(row.GetValueOrDefault(header, "")) is { } value && BattleCsv.Bool(value, sheet, index, header);
}
