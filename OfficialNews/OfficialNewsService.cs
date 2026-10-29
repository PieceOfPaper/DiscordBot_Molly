namespace Molly.OfficialNews;

/// <summary>공식 소식 기능 묶음. 구독 명령은 Store만, 수집기는 새 글을 Store에 기록하고, Worker가 Discord로 보낸다.</summary>
public sealed class OfficialNewsService(OfficialNewsStore store, OfficialNewsDeliveryWorker worker, OfficialNewsCollector collector, IOfficialNewsSender sender, bool collecting)
{
    /// <summary>수집이 이 시간보다 오래 성공하지 못하면 확인 명령에서 알린다(정상 주기의 여섯 배).</summary>
    public static readonly TimeSpan StaleAfter = OfficialNewsCollector.Interval * 6;

    public OfficialNewsStore Store { get; } = store;
    public OfficialNewsDeliveryWorker Worker { get; } = worker;
    public OfficialNewsCollector Collector { get; } = collector;
    /// <summary>테스트 출력을 등록 채널로 보낼 때 쓴다. 실제 알림과 같은 경로라 채널 권한 문제도 함께 확인된다.</summary>
    public IOfficialNewsSender Sender { get; } = sender;

    /// <summary>OfficialNews:Enabled 설정. 꺼져 있어도 이미 만든 발송 작업은 보낸다.</summary>
    public bool IsCollecting { get; } = collecting;

    /// <summary>발송 worker와(설정이 켜져 있으면) 수집기를 시작한다. 둘 다 앱 수명 동안 하나만 실행된다.</summary>
    public void Start(CancellationToken ct)
    {
        _ = Worker.RunAsync(ct);
        if (IsCollecting)
            _ = Collector.RunAsync(ct);
        else
            Console.WriteLine("[공식소식] 공식 소식 수집이 꺼져 있습니다(OfficialNews:Enabled=false). 이미 만든 발송 작업은 계속 보냅니다.");
    }
}
