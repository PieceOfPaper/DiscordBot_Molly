using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace DiscordBot_Molly.Commands;

public sealed class LiarGameCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("라이어게임", "몰리 놀이터 채널에서 참가형 라이어게임을 시작합니다.")]
    public async Task Start()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("이 명령은 서버 채널에서만 사용할 수 있어요.", ephemeral: true);
            return;
        }
        await RespondAsync("라이어게임 채널을 준비하고 있어요. 잠시만 기다려주세요.");
        try
        {
            var result = await Program.instance.LiarGames.StartAsync(Context.Guild, Context.Channel.Id, Context.User.Id);
            await ModifyOriginalResponseAsync(x => x.Content = result.Started
                ? $"라이어게임 채널 <#{result.ChannelId}>을 만들었어요. 그 채널에서 참가 버튼을 눌러주세요!"
                : "이 서버에서는 이미 라이어게임이 진행 중이에요.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[라이어게임] 시작 실패: {ex.Message}");
            await ModifyOriginalResponseAsync(x => x.Content = "라이어게임 채널을 만들지 못했어요. 봇의 채널 관리·채널 보기·메시지 보내기 권한을 확인해주세요.");
        }
    }

    [ComponentInteraction("liar:join")]
    public async Task Join()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("참가는 게임 채널에서만 할 수 있어요.", ephemeral: true);
            return;
        }
        var joined = Program.instance.LiarGames.Join(Context.Guild.Id, Context.Channel.Id, Context.User.Id);
        await RespondAsync(joined ? "참가했습니다! 시작까지 기다려주세요." : "이미 참가했거나, 참가 시간이 끝났거나, 정원이 찼어요.", ephemeral: true);
    }

    [ComponentInteraction("liar:question:*")]
    public async Task OpenQuestion(string guildIdRaw)
    {
        if (!ulong.TryParse(guildIdRaw, out var guildId) || !Program.instance.LiarGames.IsQuestionTurn(guildId, Context.User.Id))
        {
            await RespondAsync("지금은 질문을 작성할 차례가 아니에요.", ephemeral: true);
            return;
        }
        var modal = new ModalBuilder().WithTitle("O 또는 X 질문 작성").WithCustomId($"liar_question_modal:{guildId}")
            .AddTextInput("질문", "question", TextInputStyle.Paragraph, "예: 이 단어는 먹을 수 있나요?", required: true, maxLength: 300).Build();
        await RespondWithModalAsync(modal);
    }

    [ModalInteraction("liar_question_modal:*")]
    public async Task SubmitQuestion(string guildIdRaw, LiarQuestionModal modal)
    {
        var accepted = ulong.TryParse(guildIdRaw, out var guildId) && await Program.instance.LiarGames.SubmitQuestionAsync(guildId, Context.User.Id, modal.Question);
        await RespondAsync(accepted ? "질문을 제출했어요. 이제 참가자들의 투표를 기다립니다." : "질문 시간이 끝났거나 질문을 제출할 차례가 아니에요.", ephemeral: true);
    }

    [ComponentInteraction("liar:answer:*")]
    public async Task Answer(string value)
    {
        if (Context.Guild is null || value is not ("o" or "x")) { await RespondAsync("유효하지 않은 투표예요.", ephemeral: true); return; }
        var accepted = Program.instance.LiarGames.SubmitAnswerVote(Context.Guild.Id, Context.Channel.Id, Context.User.Id, value == "o");
        await RespondAsync(accepted ? $"{value.ToUpperInvariant()} 투표를 완료했어요." : "이미 투표했거나 지금은 투표 시간이 아니에요.", ephemeral: true);
    }

    [ComponentInteraction("liar:accuse:*")]
    public async Task Accuse(string target)
    {
        var valid = Context.Guild is not null && ulong.TryParse(target, out var targetId) && Program.instance.LiarGames.SubmitAccusation(Context.Guild.Id, Context.Channel.Id, Context.User.Id, targetId);
        await RespondAsync(valid ? "지목 투표를 완료했어요." : "이미 지목했거나 지금은 지목 시간이 아니에요.", ephemeral: true);
    }

    [ComponentInteraction("liar:guess:*")]
    public async Task OpenGuess(string guildIdRaw)
    {
        if (!ulong.TryParse(guildIdRaw, out var guildId) || !Program.instance.LiarGames.IsLiarGuessTurn(guildId, Context.User.Id)) { await RespondAsync("지금은 단어를 입력할 수 없어요.", ephemeral: true); return; }
        var modal = new ModalBuilder().WithTitle("단어 맞히기").WithCustomId($"liar_guess_modal:{guildId}")
            .AddTextInput("단어", "word", TextInputStyle.Short, "단어를 입력하세요", required: true, maxLength: 100).Build();
        await RespondWithModalAsync(modal);
    }

    [ModalInteraction("liar_guess_modal:*")]
    public async Task SubmitGuess(string guildIdRaw, LiarGuessModal modal)
    {
        var accepted = ulong.TryParse(guildIdRaw, out var guildId) && Program.instance.LiarGames.SubmitLiarGuess(guildId, Context.User.Id, modal.Word);
        await RespondAsync(accepted ? "단어를 제출했어요." : "입력 시간이 끝났거나 권한이 없어요.", ephemeral: true);
    }
}

public sealed class LiarQuestionModal : IModal
{
    public string Title => "O 또는 X 질문 작성";
    [InputLabel("질문")]
    [ModalTextInput("question", TextInputStyle.Paragraph, maxLength: 300)]
    public string Question { get; set; } = "";
}

public sealed class LiarGuessModal : IModal
{
    public string Title => "단어 맞히기";
    [InputLabel("단어")]
    [ModalTextInput("word", TextInputStyle.Short, maxLength: 100)]
    public string Word { get; set; } = "";
}
