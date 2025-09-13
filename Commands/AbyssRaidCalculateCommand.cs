using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class AbyssRaidCalculateCommand : InteractionModuleBase<SocketInteractionContext>
{
    //TODO - 데이터 테이블로 빼는게 좋을 것 같다.
    private const int ABYSS_ASSET_WEEK_REWARD = 300 + 15; // 심연의 화석 300 + 화석 주머니 15
    private const int ABYSS_ASSET_WEEKEND_REWARD = 15; //주말은 어비스! 15
    private const int REQUIRE_ABYSS_ASSET_WEAPON = 900;
    private const int REQUIRE_ABYSS_ASSET_ARMOR = 600;
    private const int REQUIRE_ABYSS_ASSET_ACC= 600;
    private const int RAID_ASSET_WEEK_REWARD = 20;
    private const int RAID_ASSET_WEEKEND_REWARD = 2; //주말에는 레이드!
    private const int RAID_ABYSS_ASSET_ARMOR = 70;

    private const string ABYSS_ASSET_COUNT_NAME = "보유_심연의_화석_갯수";
    private const string ABYSS_ASSET_COUNT_DESC = "보유한 심연의 화석 갯수를 입력하세요";
    private const string WEEKEND_REWARD_NAME = "주말보상_획득여부";
    private const string WEEKEND_REWARD_DESC = "현재 주말 보상 받았는지 여부 입력. (미입력시 자동 설정)";
    
    [SlashCommand("어비스무기정가", "무기 정가까지 남은 일수 계산")]
    public async Task Command_AbyssCalculateWeapon(
        [Summary(ABYSS_ASSET_COUNT_NAME, ABYSS_ASSET_COUNT_DESC)] int hasAbyssAssetCount = 0,
        [Summary(WEEKEND_REWARD_NAME, WEEKEND_REWARD_DESC)] bool? isReceivedWeekendReward = null)
        => await ProcessAbyssRaidCalculate(REQUIRE_ABYSS_ASSET_WEAPON, hasAbyssAssetCount, isReceivedWeekendReward, ABYSS_ASSET_WEEK_REWARD, ABYSS_ASSET_WEEKEND_REWARD);
    
    [SlashCommand("어비스방어구정가", "방어구 정가까지 남은 일수 계산")]
    public async Task Command_AbyssCalculateArmor(
        [Summary(ABYSS_ASSET_COUNT_NAME, ABYSS_ASSET_COUNT_DESC)] int hasAbyssAssetCount = 0,
        [Summary(WEEKEND_REWARD_NAME, WEEKEND_REWARD_DESC)] bool? isReceivedWeekendReward = null)
        => await ProcessAbyssRaidCalculate(REQUIRE_ABYSS_ASSET_ARMOR, hasAbyssAssetCount, isReceivedWeekendReward, ABYSS_ASSET_WEEK_REWARD, ABYSS_ASSET_WEEKEND_REWARD);
    
    [SlashCommand("어비스장신구정가", "장신구 정가까지 남은 일수 계산")]
    public async Task Command_AbyssCalculateAcc(
        [Summary(ABYSS_ASSET_COUNT_NAME, ABYSS_ASSET_COUNT_DESC)] int hasAbyssAssetCount = 0,
        [Summary(WEEKEND_REWARD_NAME, WEEKEND_REWARD_DESC)] bool? isReceivedWeekendReward = null)
        => await ProcessAbyssRaidCalculate(REQUIRE_ABYSS_ASSET_ACC, hasAbyssAssetCount, isReceivedWeekendReward, ABYSS_ASSET_WEEK_REWARD, ABYSS_ASSET_WEEKEND_REWARD);
    
    [SlashCommand("레이드방어구정가", "방어구 정가까지 남은 일수 계산")]
    public async Task Command_RaidCalculateArmor(
        [Summary("보유_원정의_증거_갯수", "보유한 원정의 증거 갯수를 입력하세요")] int hasAbyssAssetCount = 0,
        [Summary("주말보상_획득여부", "현재 주말 보상 받았는지 여부 입력. (미입력시 자동 설정)")] bool? isReceivedWeekendReward = null)
        => await ProcessAbyssRaidCalculate(RAID_ABYSS_ASSET_ARMOR, hasAbyssAssetCount, isReceivedWeekendReward, RAID_ASSET_WEEK_REWARD, RAID_ASSET_WEEKEND_REWARD);

    private async Task ProcessAbyssRaidCalculate(int requireCount, int hasCount, bool? isReceivedWeekendReward, int weekReward, int weekendReward)
    {
        var dateTimeNow = MobiTime.now;
        if (isReceivedWeekendReward.HasValue == false)
            isReceivedWeekendReward = MobiTime.now.IsMobiResetTimeWeekend();
        
        var stateStr = $"(보유량:{hasCount}, 필요량:{requireCount}, 주말보상:{isReceivedWeekendReward.Value})";
        
        if (hasCount >= requireCount)
        {
            await RespondAsync($"🎉 지금 당장 정가 가능합니다!\n{stateStr}");
            return;
        }

        if (isReceivedWeekendReward == false && (hasCount + weekendReward) >= requireCount)
        {
            if (dateTimeNow.IsMobiResetTimeWeekend())
            {
                await RespondAsync($"🙌 주말보상을 받고 정가 가능합니다!\n{stateStr}");
            }
            else
            {
                var earliestWeekend = dateTimeNow.ToEarliestMobiResetTimeWeekend();
                await RespondAsync($"📅 정가하는 날: {earliestWeekend.Date.Year}년 {earliestWeekend.Date.Month}월 {earliestWeekend.Date.Day}일\n{stateStr}");
            }
            return;
        }
        
        var resultDateTime = MobiTime.CalculateRewardNextDateTime(requireCount, hasCount, isReceivedWeekendReward.Value, weekReward, weekendReward);
        await RespondAsync($"📅 정가하는 날: {resultDateTime.Date.Year}년 {resultDateTime.Date.Month}월 {resultDateTime.Date.Day}일\n{stateStr}");
    }
}
