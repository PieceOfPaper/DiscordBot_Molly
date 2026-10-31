using System.Collections.ObjectModel;

namespace Molly.Runes;

// 몰리가 다루는 룬 등급. 게임 데이터의 Rarity 중 전설·신화만 담습니다.
public enum RuneGrade { Legendary, Mythic }

// 게임 데이터의 RuneAttachableType(Weapon·Armor·Emblem·AccessoryAndRing).
public enum RuneCategory { Weapon, Armor, Emblem, Accessory }

public static class RuneNames
{
    public static string Display(this RuneGrade grade) => grade switch
    {
        RuneGrade.Legendary => "전설",
        RuneGrade.Mythic => "신화",
        _ => throw new ArgumentOutOfRangeException(nameof(grade)),
    };

    // 게임 표시 이름은 '엠블럼 룬'이지만 몰리 명령·퀴즈 종목은 '앰블럼'으로 써 왔으므로 그대로 둡니다.
    public static string Display(this RuneCategory category) => category switch
    {
        RuneCategory.Weapon => "무기",
        RuneCategory.Armor => "방어구",
        RuneCategory.Emblem => "앰블럼",
        RuneCategory.Accessory => "장신구",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };
}

/// <param name="Id">게임 데이터의 Rune.Id(예: Rune_Weapon_v30_Tier10_A01).</param>
/// <param name="Season">RuneSeasonType의 숫자(Season02 → 2).</param>
/// <param name="Class">전용 클래스 표시 이름. 공용 룬은 빈 문자열입니다.</param>
/// <param name="Name">'무기 룬: ' 같은 접두어를 뺀 표시 이름. 같은 이름의 상위 룬은 끝에 '+'가 붙습니다.</param>
/// <param name="Effect">옵션 설명을 줄바꿈으로 이은 문장. 수치를 찾지 못한 자리는 '?'입니다.</param>
public sealed record RuneData(string Id, int Season, RuneGrade Grade, RuneCategory Category, string Class, string Name, string Effect);

public sealed class RuneTable
{
    public IReadOnlyList<RuneData> Items { get; }
    public IReadOnlyDictionary<string, RuneData> ById { get; }
    // 만들 때 제외했거나 수치를 다 찾지 못한 룬. 로그용이며 캐시에는 저장하지 않습니다.
    public IReadOnlyList<string> Warnings { get; }

    public RuneTable(IEnumerable<RuneData> items, IEnumerable<string>? warnings = null)
    {
        Items = Array.AsReadOnly(items.ToArray());
        ById = new ReadOnlyDictionary<string, RuneData>(Items.ToDictionary(x => x.Id, StringComparer.Ordinal));
        Warnings = Array.AsReadOnly((warnings ?? []).ToArray());
    }
}
