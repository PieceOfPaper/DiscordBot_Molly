using System.Text;
using System.Text.RegularExpressions;
using Molly.Battle;

namespace DiscordBot_Molly.Commands;

/// <summary>전투 로그 블록의 색 구분. 행동한 쪽(A·B)과 턴 시작 상태 효과를 서로 다른 색으로 보여준다.</summary>
public enum BattleLogTone { FighterA, FighterB, TurnStatus }

/// <summary>Discord 임베드 하나에 해당하는 로그 묶음. 제목은 행동(스킬 사용·일반 공격·턴 시작 상태 효과 등), 본문은 그 행동에 따른 효과들이다.</summary>
public sealed record BattleLogBlock(string? Title, string Body, BattleLogTone Tone);

/// <summary>한 턴의 로그. 턴마다 메시지 하나로 보내고, 블록마다 임베드 하나를 쓴다.</summary>
public sealed record BattleLogMessage(IReadOnlyList<BattleLogBlock> Blocks)
{
    /// <summary>콘솔 출력·테스트용 평문. 제목은 【】로 감싸고 본문은 그 아래에 붙인다.</summary>
    public string ToText() => string.Join("\n", Blocks.Select(x => (x.Title is null ? "" : "【" + x.Title + "】" + (x.Body.Length > 0 ? "\n" : "")) + x.Body));
}

/// <summary>전투 이벤트를 턴 단위 메시지와 행동 단위 임베드 블록으로 묶는다. 진행 로직과 분리해 테스트에서 문구를 검사한다.</summary>
public static class BattleLog
{
    // Discord 임베드 제한: 메시지당 10개, 본문 4096자, 메시지 전체 6000자. 제목·여유분을 고려해 조금 낮게 잡는다.
    public const int MaxEmbedsPerMessage = 10;
    public const int MaxBodyLength = 4000;
    public const int MaxMessageLength = 5800;

    // 엔진의 ResourceChanged Detail 형식: "{자원} +N (현재 V)", "{자원} -N (현재 V)", "{자원}이(가) 사라졌습니다."
    private static readonly Regex ResourceDelta = new(@"^(?<name>.+) (?<delta>[+-]\d+) \(현재 (?<value>\d+)\)$", RegexOptions.CultureInvariant);
    private static readonly Regex ResourceGone = new(@"^(?<name>.+)이\(가\) 사라졌습니다\.$", RegexOptions.CultureInvariant);
    // 재사용 대기 초기화는 쿨다운감소를 큰 값(궁수 작열의 궤적 99)으로 근사한다. 가장 긴 기본쿨다운(7턴)보다 크면 "N턴씩 감소" 대신 초기화로 알린다.
    private const int CooldownResetTurns = 9;

    /// <param name="fighterAName">A 쪽 캐릭터 이름. 비우면 먼저 행동한 쪽을 A 색으로 쓴다.</param>
    public static IEnumerable<BattleLogMessage> Format(IEnumerable<BattleEvent> events, string? fighterAName = null)
    {
        var blocks = new List<Block>();
        Block? current = null;
        var pendingCritical = false;
        // 가드(전사 방패)는 다단 공격마다 따로 줄을 만들지 않고 다음 피해 줄 앞에 붙인다.
        var pendingGuard = false;
        // 턴 시작 상태 효과 묶음의 제목은 그 턴의 주인이다. 지속 피해 이벤트의 Actor는 피해를 건 쪽이라 제목에 쓰면 상대의 상태처럼 보인다.
        string? turnOwner = null;
        BattleLogTone ToneOf(string actor) { fighterAName ??= actor; return actor == fighterAName ? BattleLogTone.FighterA : BattleLogTone.FighterB; }
        Block Current(string actor) { if (current is null) { current = new Block { Tone = ToneOf(actor) }; blocks.Add(current); } return current; }
        // 행동 제목보다 먼저 나오는 줄의 블록. 턴 시작 상태 효과 블록 뒤라면 다음 행동의 블록을 새로 연다.
        Block Prelude(string actor) { if (current is { IsTurnStatus: true }) { current = new Block { Tone = ToneOf(actor) }; blocks.Add(current); } return Current(actor); }
        // 행동 제목: 턴의 첫 행동은 새 블록의 제목, 같은 턴 안의 이어지는 행동(파생·생활스킬)은 본문 안의 굵은 소제목이다.
        void Heading(string actor, string title)
        {
            if (current is { IsAction: true }) { current.AddLine("**↳ " + title + "**"); return; }
            if (current is null || current.Title is not null) { current = new Block { Tone = ToneOf(actor) }; blocks.Add(current); }
            current.Title = title;
            current.Tone = ToneOf(actor);
            current.IsAction = true;
        }
        foreach (var x in events)
        {
            if (x.Type is "BattleStarted" or "BattleEnded") continue;
            if (x.Type == "TurnStarted")
            {
                turnOwner = x.Actor;
                ToneOf(x.Actor);
                if (blocks.Count > 0) yield return Flush(blocks);
                current = null;
                continue;
            }
            // 돌발 이벤트·궁극기 연출처럼 행동 제목보다 먼저 나오는 줄은 제목 없는 블록에 쌓였다가, 이어지는 행동 제목이 그 블록의 제목이 된다.
            if (x.Type == "SurpriseEventTriggered") { Prelude(x.Actor).AddLine("✨ " + x.Actor + "의 **" + x.Detail + "**!"); continue; }
            if (x.Type == "UltimateUsed") { Prelude(x.Actor).AddLine("## 🌟 " + x.Detail + "!!"); continue; }
            if (x.Type == "UltimateQuote") { Prelude(x.Actor).AddLine("-# " + x.Detail); continue; }
            if (x.Type is "NormalAttackUsed" or "SkillUsed" or "DerivedSkillUsed") { Heading(x.Actor, x.Type == "NormalAttackUsed" ? x.Actor + "의 일반 공격!" : x.Actor + "이(가) " + x.Detail + "을(를) 사용합니다!"); continue; }
            // 브레이크로 잃은 행동은 스킬 사용처럼 행동 제목으로 쓴다. 턴을 마치며 생기는 일(상태·자원 만료, 그에 반응한 패시브)이 그 본문에 붙는다.
            if (x.Type == "BreakActionLost") { Heading(x.Target!, "💢 " + x.Target + "은(는) 브레이크로 행동하지 못했습니다!"); continue; }
            if (x.Type == "LifeSkillUsed") { Heading(x.Actor, "🌿 " + x.Actor + "의 생활스킬 " + x.Detail + "!"); continue; }
            if (x.Type == "CriticalHit") { pendingCritical = true; continue; }
            if (x.Type == "AttackGuarded") { pendingGuard = true; continue; }
            if (x.Type is "BreakActivated" or "BreakExtended")
            {
                // 브레이크·브레이크 익스텐드는 굵은 글씨 한 줄로 강조한다. 큰 글씨는 궁극기에만 써서 스킬 사용 문구가 묻히지 않게 한다.
                var block = Current(x.Actor);
                block.AddLine(x.Type == "BreakActivated" ? "💢 **브레이크!!**" : "🧊 **브레이크 익스텐드!!**");
                block.AddLine(x.Type == "BreakActivated" ? x.Target + "이(가) **브레이크** 상태에 빠졌습니다!" : x.Target + "의 브레이크가 연장되어 " + x.Amount + "턴 더 행동하지 못합니다!");
                pendingCritical = false; pendingGuard = false;
                continue;
            }
            // 자원 증감은 본문 끝의 작은 글씨 한 줄로 모은다.
            if (x.Type == "ResourceChanged") { Current(x.Actor).AddResource(x.Actor, x.Detail ?? ""); continue; }
            var isTurnStatus = current is not { IsAction: true } && x.Type is "StatusDamage" or "StatusHeal" or "StatusExpired" or "HpStatus";
            if (isTurnStatus && current is not { IsTurnStatus: true })
            {
                // 제목 없는 블록(턴 시작 자원 증가 등)은 상태 효과 블록으로 바꾸고, 그 밖에는 새 블록을 연다.
                if (current is null || current.Title is not null) { current = new Block(); blocks.Add(current); }
                current.Title = "⏳ " + (turnOwner ?? x.Actor) + "의 상태 효과";
                current.Tone = BattleLogTone.TurnStatus;
                current.IsTurnStatus = true;
            }
            // 상태 적용·해제·소모는 같은 대상의 연속된 줄을 한 줄로 묶는다.
            switch (x.Type)
            {
                case "StatusApplied": Current(x.Actor).AddStatus("적용", x.Actor, x.Detail ?? "", x.Amount); pendingCritical = false; pendingGuard = false; continue;
                case "StatusExpired": Current(x.Actor).AddStatus("해제", x.Actor, x.Detail ?? "", null); pendingCritical = false; pendingGuard = false; continue;
                case "StatusConsumed": Current(x.Actor).AddStatus("소모", x.Actor, x.Detail ?? "", null); pendingCritical = false; pendingGuard = false; continue;
                case "StatusCleansed": Current(x.Actor).AddStatus("정화", x.Actor, x.Detail ?? "", null); pendingCritical = false; pendingGuard = false; continue;
            }
            var text = x.Type switch
            {
                "LifeSkillNarration" => x.Detail, "LifeSkillEffect" => (pendingCritical ? "💥 **치명타!** " : "") + x.Detail, "DamageDealt" => (pendingGuard ? "🛡️ **가드!** " : "") + (pendingCritical ? "💥 **치명타!** " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 치명타 피해를 입혔습니다!" : x.Target + "에게 " + x.Amount?.ToString("N0") + "의 피해를 입혔습니다!"), "AttackEvaded" => "💨 " + x.Target + "은(는) 상대의 시야에서 벗어나 공격을 흘려냈습니다!", "ShieldAbsorbed" => "🛡️ " + x.Target + "의 **" + x.Detail + "**이(가) " + x.Amount?.ToString("N0") + "의 피해를 흡수했습니다!", "AdditionalHit" => "⚡ **추가타!** " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 추가 피해!", "AdditionalDamage" => "✨ " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 추가 피해를 입혔습니다!", "StatusDetonated" => "💥 **" + x.Detail + " 폭발!** " + x.Target + "에게 남은 지속 피해 " + x.Amount?.ToString("N0") + "을(를) 한꺼번에 입혔습니다!", "StatusDamage" => "🌒 **" + x.Detail + "!** " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 지속 피해를 입혔습니다!", "BreakGaugeChanged" => x.Target + "의 브레이크 게이지가 " + x.Amount + "/" + x.Detail + "이 되었습니다.", "BreakGaugeBlocked" => x.Target + "은(는) 이미 브레이크 상태라 브레이크 게이지가 오르지 않습니다.", "BreakImmune" => "🛡️ " + x.Target + "은(는) 브레이크를 버텨냈습니다!", "CooldownReduced" => int.TryParse(x.Detail, out var reducedTurns) && reducedTurns >= CooldownResetTurns ? x.Actor + "의 모든 스킬 쿨다운이 초기화되었습니다!" : x.Actor + "의 스킬 쿨다운이 " + x.Detail + "턴씩 감소했습니다.", "HealApplied" => x.Actor + "의 HP가 " + x.Amount?.ToString("N0") + " 회복되었습니다!", "StatusHeal" => "💚 **" + x.Detail + "!** " + x.Target + "의 HP가 " + x.Amount?.ToString("N0") + " 회복되었습니다!", "HpStatus" => x.Actor + "은 " + x.Detail, "CharacterDefeated" => x.Target + "이(가) 쓰러졌습니다!", _ => null
            };
            pendingCritical = false; pendingGuard = false;
            if (text is not null) Current(x.Actor).AddLine(text);
        }
        if (blocks.Count > 0) yield return Flush(blocks);
    }

    /// <summary>한 턴의 블록을 Discord 임베드 제한에 맞게 여러 메시지로 나눈다. 대부분의 턴은 메시지 하나다.</summary>
    public static IEnumerable<IReadOnlyList<BattleLogBlock>> SplitForDiscord(BattleLogMessage message)
    {
        var chunk = new List<BattleLogBlock>();
        var length = 0;
        foreach (var block in message.Blocks)
        {
            var size = (block.Title?.Length ?? 0) + block.Body.Length;
            if (chunk.Count > 0 && (chunk.Count >= MaxEmbedsPerMessage || length + size > MaxMessageLength)) { yield return chunk; chunk = []; length = 0; }
            chunk.Add(block);
            length += size;
        }
        if (chunk.Count > 0) yield return chunk;
    }

    private static BattleLogMessage Flush(List<Block> blocks)
    {
        var message = new BattleLogMessage(blocks.Select(x => x.Build()).Where(x => x.Title is not null || x.Body.Length > 0).ToArray());
        blocks.Clear();
        return message;
    }

    private sealed class Block
    {
        public string? Title;
        public BattleLogTone Tone;
        public bool IsAction;
        public bool IsTurnStatus;
        private readonly List<Line> lines = [];
        // 캐릭터별 자원 요약. 처음 나온 순서대로 보여주고, 같은 자원이 여러 번 바뀌면 순변화량과 마지막 값만 남긴다.
        private readonly List<(string Actor, List<Resource> Items)> resources = [];

        public void AddLine(string text) => lines.Add(new Line(text, null, null, []));

        public void AddStatus(string kind, string actor, string name, int? turns)
        {
            if (lines.Count > 0 && lines[^1] is { Kind: not null } last && last.Kind == kind && last.Actor == actor) { last.Items.Add((name, turns)); return; }
            lines.Add(new Line(null, kind, actor, [(name, turns)]));
        }

        public void AddResource(string actor, string detail)
        {
            var group = resources.FirstOrDefault(x => x.Actor == actor);
            if (group.Items is null) { group = (actor, []); resources.Add(group); }
            string name; int? delta; int value;
            if (ResourceDelta.Match(detail) is { Success: true } changed) { name = changed.Groups["name"].Value; delta = int.Parse(changed.Groups["delta"].Value); value = int.Parse(changed.Groups["value"].Value); }
            else if (ResourceGone.Match(detail) is { Success: true } gone) { name = gone.Groups["name"].Value; delta = null; value = 0; }
            else { lines.Add(new Line(actor + "의 " + detail, null, null, [])); return; }
            var existing = group.Items.FirstOrDefault(x => x.Name == name);
            if (existing is null) group.Items.Add(new Resource(name) { Delta = delta, Value = value, Gone = delta is null });
            else { existing.Delta = existing.Delta is { } d && delta is { } n ? d + n : null; existing.Value = value; existing.Gone = delta is null; }
        }

        public BattleLogBlock Build()
        {
            var body = new StringBuilder();
            foreach (var line in lines) body.Append(body.Length > 0 ? "\n" : "").Append(line.Text ?? StatusText(line));
            foreach (var (actor, items) in resources)
                body.Append(body.Length > 0 ? "\n" : "").Append("-# 📊 " + actor + " · " + string.Join(" · ", items.Select(ResourceText)));
            var text = body.ToString();
            if (text.Length > MaxBodyLength) text = text[..(MaxBodyLength - 2)] + "\n…";
            return new BattleLogBlock(Title, text, Tone);
        }

        private static string StatusText(Line line)
        {
            var sameTurns = line.Items.Select(x => x.Turns ?? 0).Distinct().Count() == 1;
            var names = string.Join("·", line.Items.Select(x => "**" + x.Name + "**" + (!sameTurns && x.Turns is > 0 ? "(" + x.Turns + "턴)" : "")));
            var suffix = sameTurns && line.Items[0].Turns is > 0 ? " (" + line.Items[0].Turns + "턴)" : "";
            return line.Kind switch
            {
                "적용" => line.Actor + "에게 " + names + " 상태가 적용되었습니다!" + suffix,
                "해제" => line.Actor + "의 " + names + " 상태가 풀렸습니다.",
                "소모" => line.Actor + "의 " + names + " 상태가 공격에 소모되었습니다.",
                _ => "🌿 " + line.Actor + "의 " + names + " 상태가 사라졌습니다."
            };
        }

        private static string ResourceText(Resource x)
            => x.Gone ? x.Name + " 사라짐" : x.Name + " " + x.Value + (x.Delta is { } d && d != 0 ? " (" + (d > 0 ? "+" : "") + d + ")" : "");
    }

    private sealed record Line(string? Text, string? Kind, string? Actor, List<(string Name, int? Turns)> Items);

    private sealed class Resource(string name)
    {
        public string Name { get; } = name;
        public int? Delta;
        public int Value;
        public bool Gone;
    }
}
