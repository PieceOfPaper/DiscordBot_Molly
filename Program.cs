using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;

class Program
{
    private readonly DiscordSocketClient m_Client;
    private readonly IConfiguration m_Config;

    static Task Main() => new Program().MainAsync();

    private static readonly (string cmd, string desc)[] s_Commands = new (string, string)[]
    {
        ("핑", "봇 지연 확인"),
    };

    public Program()
    {
        m_Config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        m_Client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
        });
    }

    public async Task MainAsync()
    {
        m_Client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
        m_Client.Ready += OnReadyAsync;
        m_Client.InteractionCreated += OnInteractionAsync;

        // 터미널에 입력
        // dotnet user-secrets set "Discord:Token" "여기에_봇_토큰"
        var token = m_Config["Discord:Token"];
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("Discord 토큰이 비었습니다. user-secrets 설정을 확인하세요.");

        await m_Client.LoginAsync(TokenType.Bot, token);
        await m_Client.StartAsync();
        await Task.Delay(-1);
    }

    private async Task OnReadyAsync()
    {
        for (var i = 0; i < s_Commands.Length; i ++)
        {
            var ping = new SlashCommandBuilder()
                .WithName(s_Commands[i].cmd)
                .WithDescription(s_Commands[i].desc);

            // 개발 초기에는 길드 명령(즉시 반영). 운영은 글로벌 명령(전파 수분~1시간)
            ulong guildId = 0; // TODO: 테스트 서버(길드) ID로 교체하면 즉시 등록
            if (guildId != 0)
                await m_Client.Rest.CreateGuildCommand(ping.Build(), guildId);
            else
                await m_Client.CreateGlobalApplicationCommandAsync(ping.Build());

            Console.WriteLine($"Slash 명령 등록 완료: /{s_Commands[i].cmd}");
        }
    }

    private async Task OnInteractionAsync(SocketInteraction inter)
    {
        if (inter is SocketSlashCommand slash)
        {
            switch (slash.Data.Name)
            {
                case "핑":
                    await slash.RespondAsync($"퐁! (지연: {m_Client.Latency} ms)");
                    break;
            }
        }
    }
}
