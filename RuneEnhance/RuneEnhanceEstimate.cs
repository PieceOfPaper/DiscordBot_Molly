namespace Molly.RuneEnhance;

/// <summary>한 입력에 대한 촉매 미사용·사용(사용 가능 구간에서 매번) 두 결과. 값은 표시용 반올림 전입니다.</summary>
/// <param name="FirstCatalystLevel">계산 구간에서 촉매를 처음 쓸 수 있는 시작 단계(+10 → +11이면 10). 구간에 없으면 null.</param>
public sealed record RuneEnhanceEstimate(RuneEnhanceRequest Request, RuneEnhanceCost WithoutCatalyst, RuneEnhanceCost WithCatalyst, int? FirstCatalystLevel)
{
    public static RuneEnhanceEstimate Calculate(RuneEnhanceData data, RuneEnhanceRequest request)
    {
        var table = data.Tables[request.Category];
        int? first = null;
        for (var level = request.Current; level < request.Target && first is null; level++)
            if (RuneEnhanceCalculator.UsesCatalyst(table.StepFrom(level), CatalystPlan.WheneverAvailable)) first = level;
        return new RuneEnhanceEstimate(request,
            RuneEnhanceCalculator.Estimate(table, request, CatalystPlan.None),
            RuneEnhanceCalculator.Estimate(table, request, CatalystPlan.WheneverAvailable),
            first);
    }
}
