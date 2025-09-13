﻿using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;

class Program
{
    private readonly DiscordSocketClient m_Client;
    private readonly IConfiguration m_Config;
    private readonly InteractionService m_InteractionService;

    static Task Main() => new Program().MainAsync();

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
        
        m_InteractionService = new InteractionService(m_Client.Rest);
        m_Client.Ready += async () =>
        {
            // 개발 초기에는 길드 명령(즉시 반영). 운영은 글로벌 명령(전파 수분~1시간)
            ulong guildId = 0; // TODO: 테스트 서버(길드) ID로 교체하면 즉시 등록
            if (guildId != 0)
                await m_InteractionService.RegisterCommandsToGuildAsync(guildId);
            else
                await m_InteractionService.RegisterCommandsGloballyAsync(); // 전파 지연 있을 수 있음
        };
        m_Client.InteractionCreated += async (SocketInteraction inter) =>
        {
            try
            {
                var ctx = new SocketInteractionContext(m_Client, inter);
                var result = await m_InteractionService.ExecuteCommandAsync(ctx, null);

                if (!result.IsSuccess)
                {
                    // 에러 응답(에페메랄)
                    if (inter.Type == InteractionType.ApplicationCommand)
                        await inter.RespondAsync($"에러: {result.ErrorReason}", ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                // 예외가 나도 Interaction에 응답은 해줘야 함(중복 응답 방지 주의)
                try { await inter.RespondAsync($"예외 발생: {ex.Message}", ephemeral: true); }
                catch { }
            }
        };
    }

    public async Task MainAsync()
    {
        Microsoft.Playwright.Program.Main(new[] { "install" });
        
        m_Client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
        m_InteractionService.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };

        // 터미널에 입력
        // dotnet user-secrets set "Discord:Token" "여기에_봇_토큰"
        var token = m_Config["Discord:Token"];
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("Discord 토큰이 비었습니다. user-secrets 설정을 확인하세요.");

        await m_InteractionService.AddModulesAsync(typeof(Program).Assembly, null);
        await m_Client.LoginAsync(TokenType.Bot, token);
        await m_Client.StartAsync();
        await Task.Delay(-1);
    }
}
