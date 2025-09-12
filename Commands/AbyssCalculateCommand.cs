using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class AbyssCalculateCommand : InteractionModuleBase<SocketInteractionContext>
{
    //TODO - 데이터 테이블로 빼는게 좋을 것 같다.
    private const int ABYSS_ASSET_WEEK_REWARD = 300 + 15 + ABYSS_ASSET_WEEKEND_REWARD; // 심연의 화석 300 + 화석 주머니 15 + 주말은 어비스! 15
    private const int ABYSS_ASSET_WEEKEND_REWARD = 15; //주말은 어비스! 15
    private const int REQUIRE_ABYSS_ASSET_WEAPON = 900;
    private const int REQUIRE_ABYSS_ASSET_ARMOR = 600;
    private const int REQUIRE_ABYSS_ASSET_ACC= 600;

    private const string ABYSS_ASSET_COUNT_NAME = "보유_심연의_화석_갯수";
    private const string ABYSS_ASSET_COUNT_DESC = "보유한 심연의 화석 갯수를 입력하세요";
    private const string WEEKEND_REWARD_NAME = "주말보상_획득여부";
    private const string WEEKEND_REWARD_DESC = "현재 주말 보상 받았는지 여부 입력. (미입력시 자동 설정)";
    
    [SlashCommand("어비스무기정가", "무기 정가까지 남은 일수 계산")]
    public async Task Command_AbyssCalculateWeapon(
        [Summary(ABYSS_ASSET_COUNT_NAME, ABYSS_ASSET_COUNT_DESC)] int hasAbyssAssetCount = 0,
        [Summary(WEEKEND_REWARD_NAME, WEEKEND_REWARD_DESC)] bool? isReceivedWeekendReward = null)
        => await ProcessAbyssCalcurate(REQUIRE_ABYSS_ASSET_WEAPON, hasAbyssAssetCount, isReceivedWeekendReward);
    
    [SlashCommand("어비스방어구정가", "방어구 정가까지 남은 일수 계산")]
    public async Task Command_AbyssCalculateArmor(
        [Summary(ABYSS_ASSET_COUNT_NAME, ABYSS_ASSET_COUNT_DESC)] int hasAbyssAssetCount = 0,
        [Summary(WEEKEND_REWARD_NAME, WEEKEND_REWARD_DESC)] bool? isReceivedWeekendReward = null)
        => await ProcessAbyssCalcurate(REQUIRE_ABYSS_ASSET_ARMOR, hasAbyssAssetCount, isReceivedWeekendReward);
    
    [SlashCommand("어비스장신구정가", "장신구 정가까지 남은 일수 계산")]
    public async Task Command_AbyssCalculateAcc(
        [Summary(ABYSS_ASSET_COUNT_NAME, ABYSS_ASSET_COUNT_DESC)] int hasAbyssAssetCount = 0,
        [Summary(WEEKEND_REWARD_NAME, WEEKEND_REWARD_DESC)] bool? isReceivedWeekendReward = null)
        => await ProcessAbyssCalcurate(REQUIRE_ABYSS_ASSET_ACC, hasAbyssAssetCount, isReceivedWeekendReward);
    

    private async Task ProcessAbyssCalcurate(int requireCount, int hasCount, bool? isReceivedWeekendReward)
    {
        var dateTimeNow = MobiTime.now;
        if (isReceivedWeekendReward.HasValue == false)
            isReceivedWeekendReward = MobiTime.now.IsMobiResetTimeWeekend();
        
        var stateStr = $"보유량:{hasCount}, 필요량:{requireCount}, 주말보상:{isReceivedWeekendReward.Value}";
        
        if (hasCount >= requireCount)
        {
            await RespondAsync($"{stateStr}\n▶ 지금 당장 정가 가능합니다!");
            return;
        }

        if (isReceivedWeekendReward == false && (hasCount + ABYSS_ASSET_WEEKEND_REWARD) >= requireCount)
        {
            if (dateTimeNow.IsMobiResetTimeWeekend())
            {
                await RespondAsync($"{stateStr}\n▶ 주말보상을 받고 정가 가능합니다!");
            }
            else
            {
                var earliestWeekend = dateTimeNow.ToEarliestMobiResetTimeWeekend();
                await RespondAsync($"{stateStr}\n▶ 정가하는 날: 📅{earliestWeekend.Date.Year}년 {earliestWeekend.Date.Month}월 {earliestWeekend.Date.Day}일");
            }
            return;
        }
        
        var resultDateTime = ComputeFulfillmentDateTime(requireCount, hasCount, isReceivedWeekendReward.Value);
        await RespondAsync($"{stateStr}\n▶ 정가하는 날: 📅{resultDateTime.Date.Year}년 {resultDateTime.Date.Month}월 {resultDateTime.Date.Day}일");
    }
    
    private static DateTime ComputeFulfillmentDateTime(int requireCount, int hasCount, bool isReceivedWeekendReward)
    {
        var dateTimeNow = MobiTime.now;
        
        // 이미 충분하면 지금
        int remaining = requireCount - hasCount;
        if (remaining <= 0)
            return dateTimeNow;

        // 루프를 돌며 이벤트(주말15, 월요일315)를 시간 순으로 적용
        var t = dateTimeNow;
        bool claimedWeekendThisWeek = isReceivedWeekendReward;

        // 안전장치(최대 3년치 반복)
        for (int guard = 0; guard < 3 * 52 * 3; guard++)
        {
            var nextMon6 = t.ToEarliestMobiResetTimeWeekStart();

            // 다음 "주말 보상" 가능한 시각 계산
            DateTime nextWeekendEvent;
            if (t.IsMobiResetTimeWeekend())
            {
                // 주말 구간
                if (!claimedWeekendThisWeek)
                {
                    // 아직 이번 주 주말보상 안 받음 → 지금 바로 받을 수 있음
                    nextWeekendEvent = t;
                }
                else
                {
                    // 이미 이번 주는 받았음 → 다음 주 토 06:00
                    var thisWeekendStart = t.ToEarliestMobiResetTimeWeekendStart();
                    nextWeekendEvent = thisWeekendStart.AddDays(7); // 다음 토 06:00
                }
            }
            else
            {
                // 주말 구간 밖 → 다음 토 06:00
                nextWeekendEvent = t.ToNextMobiResetTimeWeekendStart();
                // (주중에 hasClaimedWeekendThisWeek=true 라는 상태는 의미가 없으므로 그대로 두되,
                // 월요일 06:00을 지나면 자동으로 새 주로 간주하고 false로 리셋됨)
            }

            // 다음 이벤트 결정(가장 이른 시각)
            DateTime nextEvent;
            int gain;
            bool isWeekendEvent;

            if (nextWeekendEvent <= nextMon6)
            {
                nextEvent = nextWeekendEvent;
                gain = 15;
                isWeekendEvent = true;
            }
            else
            {
                nextEvent = nextMon6;
                gain = 315;
                isWeekendEvent = false;
            }

            remaining -= gain;

            // 목표 달성 시 반환 로직
            if (remaining <= 0)
            {
                if (isWeekendEvent)
                {
                    // 마지막 보상이 주말 보상 → GetEarliestWeekendKst 규칙으로 반환
                    // nextEvent(KST)를 UTC로 변환해서 그 시점 기준으로 계산
                    var eventUtc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(nextEvent, DateTimeKind.Unspecified), MobiTime.timezone);

                    return eventUtc.ToEarliestMobiResetTimeWeekend();
                }
                else
                {
                    // 월요일 06:00 보상으로 달성 → 그 시각 반환
                    return nextEvent;
                }
            }

            // 다음 반복을 위한 상태 업데이트
            if (isWeekendEvent)
            {
                // 이번 주 주말 보상은 받았다고 표시
                claimedWeekendThisWeek = true;
            }
            else
            {
                // 월요일 06:00을 지났으니 "새 주" 시작 → 주말 보상 수령 여부 리셋
                claimedWeekendThisWeek = false;
            }

            // 같은 타임스탬프 재사용 방지 (다음 이벤트 탐색을 위해 +1초)
            t = nextEvent.AddSeconds(1);
        }

        throw new InvalidOperationException("예상치 못한 반복 과다. 입력을 확인하세요.");
    }
}
