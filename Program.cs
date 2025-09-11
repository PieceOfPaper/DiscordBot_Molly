using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;

class Program
{
    private readonly DiscordSocketClient _client;
    private readonly IConfiguration _cfg;

    static Task Main() => new Program().MainAsync();

    public Program()
    {
        _cfg = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
        });
    }

    public async Task MainAsync()
    {
        _client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += OnInteractionAsync;

        // 터미널에 입력
        // dotnet user-secrets set "Discord:Token" "여기에_봇_토큰"
        var token = _cfg["Discord:Token"];
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("Discord 토큰이 비었습니다. user-secrets 설정을 확인하세요.");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
        await Task.Delay(-1);
    }

    private async Task OnReadyAsync()
    {
        var ping = new SlashCommandBuilder()
            .WithName("ping")
            .WithDescription("봇 지연 확인");

        // 개발 초기에는 길드 명령(즉시 반영). 운영은 글로벌 명령(전파 수분~1시간)
        ulong guildId = 0; // TODO: 테스트 서버(길드) ID로 교체하면 즉시 등록
        if (guildId != 0)
            await _client.Rest.CreateGuildCommand(ping.Build(), guildId);
        else
            await _client.CreateGlobalApplicationCommandAsync(ping.Build());

        Console.WriteLine("Slash 명령 등록 완료: /ping");
    }

    private async Task OnInteractionAsync(SocketInteraction inter)
    {
        if (inter is SocketSlashCommand slash && slash.Data.Name == "ping")
            await slash.RespondAsync($"Pong! (latency: {_client.Latency} ms)");
    }
}
