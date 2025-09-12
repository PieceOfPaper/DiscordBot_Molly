using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class AbyssCalculateCommand : InteractionModuleBase<SocketInteractionContext>
{
    //TODO - 데이터 테이블로 빼는게 좋을 것 같다.
    private const int ABYSS_ASSET_WEEK_REWARD = 300;
    private const int REQUIRE_ABYSS_ASSET_WEAPON = 900;
    private const int REQUIRE_ABYSS_ASSET_ARMOR = 600;
    private const int REQUIRE_ABYSS_ASSET_ACC= 600;

    private const string ABYSS_ASSET_COUNT_DESC = "보유한 심연의 화석 갯수를 입력하세요";
    
    [SlashCommand("어비스무기정가", "무기 정가까지 남은 일수 계산")]
    public async Task Command_AbyssCalculateWeapon(
        [Summary(description:ABYSS_ASSET_COUNT_DESC)] int 보유_심연의_화석_갯수 = 0)
    {
        var week = (int)Math.Ceiling((float)(REQUIRE_ABYSS_ASSET_WEAPON - 보유_심연의_화석_갯수) / (float)ABYSS_ASSET_WEEK_REWARD);
        if (week <= 0)
        {
            await RespondAsync($"지금 당장 가능합니다!");
            return;
        }
        
        var target = CalculateNextAbyssDateTime(week);
        await RespondAsync($"무기 정가하는 날: {target.Date.Year}년 {target.Date.Month}월 {target.Date.Day}일");
    }
    
    [SlashCommand("어비스방어구정가", "방어구 정가까지 남은 일수 계산")]
    public async Task Command_AbyssCalculateArmor(
        [Summary(description:ABYSS_ASSET_COUNT_DESC)] int 보유_심연의_화석_갯수 = 0)
    {
        var week = (int)Math.Ceiling((float)(REQUIRE_ABYSS_ASSET_ARMOR - 보유_심연의_화석_갯수) / (float)ABYSS_ASSET_WEEK_REWARD);
        if (week <= 0)
        {
            await RespondAsync($"지금 당장 가능합니다!");
            return;
        }
        
        var target = CalculateNextAbyssDateTime(week);
        await RespondAsync($"방어구 정가하는 날: {target.Date.Year}년 {target.Date.Month}월 {target.Date.Day}일");
    }
    
    [SlashCommand("어비스장신구정가", "장신구 정가까지 남은 일수 계산")]
    public async Task Command_AbyssCalculateAcc(
        [Summary(description:ABYSS_ASSET_COUNT_DESC)] int 보유_심연의_화석_갯수 = 0)
    {
        var week = (int)Math.Ceiling((float)(REQUIRE_ABYSS_ASSET_ACC - 보유_심연의_화석_갯수) / (float)ABYSS_ASSET_WEEK_REWARD);
        if (week <= 0)
        {
            await RespondAsync($"지금 당장 가능합니다!");
            return;
        }
        
        var target = CalculateNextAbyssDateTime(week);
        await RespondAsync($"장신구 정가하는 날: {target.Date.Year}년 {target.Date.Month}월 {target.Date.Day}일");
    }

    private DateTime CalculateNextAbyssDateTime(int week)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var dateTimeNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);

        // 규칙에 맞는 '가까운 월요일 06:00' (포함/제외 로직 반영)
        DateTime GetUpcomingMonday6(DateTime nowLocal)
        {
            int daysUntilMon = ((int)DayOfWeek.Monday - (int)nowLocal.DayOfWeek + 7) % 7;
            var nextMondayDate = nowLocal.Date.AddDays(daysUntilMon);

            // 후보: 이번(또는 다음) 월요일 06:00
            var candidate = new DateTime(nextMondayDate.Year, nextMondayDate.Month, nextMondayDate.Day, 6, 0, 0);

            // 오늘이 '월요일'이고 현재 시간이 06:00을 지났다면 → 다음 주 월요일 06:00
            if (daysUntilMon == 0 && nowLocal.TimeOfDay >= TimeSpan.FromHours(6))
                candidate = candidate.AddDays(7);

            return candidate;
        }

        return GetUpcomingMonday6(dateTimeNow).AddDays(7 * (week - 1)); // week-1 주 더하기
    }
}
