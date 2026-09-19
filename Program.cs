using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using System.IO;
using Molly.Runes;
using Molly.Quiz;
using Molly.Nunchi;
using Molly.Messages;
using Molly.LiarGame;
using Molly.Lottery;
using DiscordBot_Molly.Commands;

class Program
{
    private readonly DiscordSocketClient m_Client;
    public DiscordSocketClient client => m_Client;
    public RuneCatalog Runes { get; private set; } = null!;
    public ConsonantCatalog Consonants { get; private set; } = null!;
    public MessageBottleCatalog MessageBottles { get; private set; } = null!;
    public SpeedQuizService Quizzes { get; } = new();
    public TrueFalseQuizService TrueFalseQuizzes { get; } = new();
    public NunchiGameService NunchiGames { get; } = new();
    public LiarGameService LiarGames { get; } = new();
    public LotteryService Lotteries { get; } = new();
    
    private readonly IConfiguration m_Config;
    private readonly InteractionService m_InteractionService;

    private static Program s_instance = null!;
    public static Program instance => s_instance;
    private static Task Main()
    {
        s_instance = new Program();
        return s_instance.MainAsync();
    }

    public Program()
    {
        m_Config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        m_Client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 0, // 필요 시 10~50 등 소량만
        });
        
        m_InteractionService = new InteractionService(m_Client.Rest);
        m_Client.MessageReceived += async message =>
        {
            if (!message.Author.IsBot && message.Source == MessageSource.User && message.Channel is SocketTextChannel channel)
            {
                Quizzes.Submit(channel.Guild.Id, channel.Id, message.Author.Id, message.Id, message.Content);
                await NunchiGames.SubmitAsync(channel.Guild.Id, channel.Id, message.Author.Id, message.Content, message.Timestamp);
            }
        };
        m_Client.ChannelDestroyed += channel =>
        {
            Quizzes.CancelChannel(channel.Id);
            TrueFalseQuizzes.CancelChannel(channel.Id);
            NunchiGames.CancelChannel(channel.Id);
            LiarGames.CancelChannel(channel.Id);
            Lotteries.CancelChannel(channel.Id);
            return Task.CompletedTask;
        };
        m_Client.Ready += async () =>
        {
            // 개발 초기에는 길드 명령(즉시 반영). 운영은 글로벌 명령(전파 수분~1시간)
            // 환경변수/설정: Discord:GuildId (환경변수는 Discord__GuildId)
            var guildIdRaw = m_Config["Discord:GuildId"];
            _ = ulong.TryParse(guildIdRaw, out var guildId);
            if (guildId != 0)
                await m_InteractionService.RegisterCommandsToGuildAsync(guildId);
            else
                await m_InteractionService.RegisterCommandsGloballyAsync(); // 전파 지연 있을 수 있음
        };
        m_Client.InteractionCreated += async (SocketInteraction inter) =>
        {
            try
            {
                var commandName = inter is SocketSlashCommand slash ? slash.CommandName : "(명령 아님)";
                Console.WriteLine($"[Discord] interaction 수신 type={inter.Type} name={commandName} id={inter.Id}");
                if (inter is SocketSlashCommand slashCommand)
                    Console.WriteLine($"[Discord] 수신 옵션: {string.Join(", ", slashCommand.Data.Options.Select(o => $"{o.Name}={o.Value}"))}");
                Console.WriteLine($"[Discord] 로컬 슬래시 명령: {string.Join(", ", m_InteractionService.SlashCommands.Select(x => x.Name))}");
                var ctx = new SocketInteractionContext(m_Client, inter);
                var result = await m_InteractionService.ExecuteCommandAsync(ctx, null);
                Console.WriteLine($"[Discord] interaction 처리 결과 success={result.IsSuccess} reason={result.ErrorReason}");

                if (!result.IsSuccess)
                {
                    // 에러 응답(에페메랄)
                    if (inter.Type == InteractionType.ApplicationCommand)
                    {
                        if (inter.HasResponded) await inter.FollowupAsync($"에러: {result.ErrorReason}", ephemeral: true);
                        else await inter.RespondAsync($"에러: {result.ErrorReason}", ephemeral: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Discord] interaction 처리 예외: {ex}");
                // 예외가 나도 Interaction에 응답은 해줘야 함(중복 응답 방지 주의)
                try
                {
                    if (inter.HasResponded) await inter.FollowupAsync($"예외 발생: {ex.Message}", ephemeral: true);
                    else await inter.RespondAsync($"예외 발생: {ex.Message}", ephemeral: true);
                }
                catch { }
            }
        };
    }

    public async Task MainAsync()
    {
        MobiShop.LoadTable();
        
        var appCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            appCts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, __) => appCts.Cancel();

        using var runeHttp = new HttpClient();
        Runes = new RuneCatalog(new GoogleSheetsRuneSource(runeHttp,
            m_Config["GoogleSheets:SpreadsheetId"] ?? GoogleSheetsRuneSource.DefaultSpreadsheetId,
            m_Config["GoogleSheets:RuneSheetId"] ?? "0"),
            Environment.GetEnvironmentVariable("MOLLY_DATA_DIR") ?? AppContext.BaseDirectory);
        await Runes.InitializeAsync(appCts.Token);
        Consonants = new ConsonantCatalog(new GoogleSheetsConsonantSource(runeHttp,
            m_Config["GoogleSheets:SpreadsheetId"] ?? GoogleSheetsRuneSource.DefaultSpreadsheetId),
            Environment.GetEnvironmentVariable("MOLLY_DATA_DIR") ?? AppContext.BaseDirectory);
        await Consonants.InitializeAsync(appCts.Token);
        MessageBottles = new MessageBottleCatalog(new GoogleSheetsMessageBottleSource(runeHttp,
            m_Config["GoogleSheets:SpreadsheetId"] ?? GoogleSheetsRuneSource.DefaultSpreadsheetId,
            m_Config["GoogleSheets:MessageBottleSheetName"] ?? GoogleSheetsMessageBottleSource.DefaultSheetName),
            Environment.GetEnvironmentVariable("MOLLY_DATA_DIR") ?? AppContext.BaseDirectory);
        await MessageBottles.InitializeAsync(appCts.Token);
        try
        {
            try
            {
                var fingerPrint = await MobiEventFingerprint.ComputeAsync();
                await MobiEventBrowser.CacheAsync(fingerPrint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[이벤트] 초기 수집 실패, 다른 기능은 계속 시작합니다: {ex.Message}");
            }

            m_Client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
            m_InteractionService.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };

            // 터미널에 입력
            // dotnet user-secrets set "Discord:Token" "여기에_봇_토큰"
            var token = m_Config["Discord:Token"];
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Discord 토큰이 비었습니다. user-secrets 설정을 확인하세요.");

            var loadedModules = await m_InteractionService.AddModulesAsync(typeof(Program).Assembly, null);
            if (!loadedModules.Any(x => x.Name == nameof(SpeedQuizCommand)))
            {
                var speedQuizModule = await m_InteractionService.AddModuleAsync<SpeedQuizCommand>(null);
                Console.WriteLine($"[Discord] SpeedQuizCommand 명시적 로딩: {speedQuizModule.Name}");
            }
            Console.WriteLine($"[Discord] Interaction 모듈 로딩: {loadedModules.Count()}개 / " +
                string.Join(", ", loadedModules.Select(x => x.Name)));
            await m_Client.LoginAsync(TokenType.Bot, token);
            await m_Client.StartAsync();

            try { await MobiEventExpireAlert.RegistEventExpireAlertAll(); }
            catch (Exception ex) { Console.WriteLine($"[이벤트] 초기 알림 등록 실패: {ex.Message}"); }
            MobiEventExpireAlert.RunUpdateTask(appCts.Token);
            try
            {
                await Task.Delay(Timeout.Infinite, appCts.Token);
            }
            catch (TaskCanceledException)
            {
                // 정상 종료
            }
        }
        finally
        {
            await appCts.CancelAsync();
            await Quizzes.StopAsync();
            await TrueFalseQuizzes.StopAsync();
        }
    }
}
