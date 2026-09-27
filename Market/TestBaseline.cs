namespace Molly.Market;

/// <summary>시세 알림 테스트용 가상 과거시세. 현재시세와 비교한 변동률이 기준(threshold) 이상 +10%p 이내가 되도록 역산합니다.</summary>
public static class TestBaseline
{
    public static long Create(long current, decimal threshold, Random random)
    {
        var magnitude = (double)threshold + random.NextDouble() * 0.10;
        // 100% 가까이 떨어지는 과거시세는 만들 수 없어 그때는 오른 경우로만 만듭니다.
        var rise = magnitude >= 0.95 || random.Next(2) == 0;
        // 작은 가격에서 반올림으로 변동률이 기준보다 작아지지 않게, 오르면 내림·내리면 올림으로 과거시세를 정합니다.
        var baseline = rise ? Math.Floor(current / (1 + magnitude)) : Math.Ceiling(current / (1 - magnitude));
        return Math.Max(1, (long)baseline);
    }
}
