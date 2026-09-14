using Discord.Interactions;
using Discord.WebSocket;
using Molly.Quiz;

namespace DiscordBot_Molly.Commands;

public sealed class TrueFalseQuizButtonCommand : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("true_false:*")]
    public async Task Select(string choice)
    {
        if (Context.Interaction is not SocketMessageComponent component || Context.Guild is null)
        {
            await RespondAsync("이 버튼은 서버의 진혹거퀴즈에서만 사용할 수 있어요.", ephemeral: true);
            return;
        }

        var selectedTrue = choice switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ArgumentException("지원하지 않는 답입니다.")
        };
        var result = Program.instance.TrueFalseQuizzes.SubmitChoice(Context.Guild.Id, Context.Channel.Id, component.Message.Id, Context.User.Id, selectedTrue);
        var response = result switch
        {
            TrueFalseChoiceResult.Accepted => selectedTrue ? "✅ **진실**을 선택했어요. 이 문제의 선택은 변경할 수 없어요." : "❌ **거짓**을 선택했어요. 이 문제의 선택은 변경할 수 없어요.",
            TrueFalseChoiceResult.AlreadySelected => "이 문제에서는 이미 답을 선택했어요. 첫 선택만 반영됩니다.",
            _ => "이 문제는 이미 종료되었어요."
        };
        await RespondAsync(response, ephemeral: true);
    }
}
