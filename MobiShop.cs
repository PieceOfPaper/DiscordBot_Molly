using System;
using System.Collections.Generic;
using System.IO;

public sealed record class ShopTableData
{
    [CsvField("메모")]
    public string? Memo { get; init; }

    [CsvField("NPC")]
    public string? Npc { get; init; }

    [CsvField("가성비")]
    public string? Value { get; init; }

    [CsvField("구매제한 1")]
    public string? PurchaseLimitType { get; init; }

    [CsvField("구매제한 횟수")]
    public int? PurchaseLimitCount { get; init; }

    [CsvField("비용")]
    public int? Cost { get; init; }

    [CsvField("아이템")]
    public string? Item { get; init; }

    [CsvField("재화")]
    public string? Currency { get; init; }

    [CsvField("지역")]
    public string? Region { get; init; }
}

public sealed record class ShopExchangeTableData
{
    [CsvField("메모")]
    public string? Memo { get; init; }

    [CsvField("NPC")]
    public string? Npc { get; init; }

    [CsvField("가성비")]
    public string? Value { get; init; }

    [CsvField("구매제한 1")]
    public string? PurchaseLimitType1 { get; init; }

    [CsvField("구매제한 2")]
    public string? PurchaseLimitType2 { get; init; }

    [CsvField("구매제한 횟수")]
    public int? PurchaseLimitCount { get; init; }

    [CsvField("구입 수량")]
    public int? BuyCount { get; init; }

    [CsvField("구입 아이템")]
    public string? BuyItem { get; init; }

    [CsvField("재화 수량")]
    public int? CurrencyCount { get; init; }

    [CsvField("재화 아이템")]
    public string? CurrencyItem { get; init; }

    [CsvField("지역")]
    public string? Region { get; init; }
}

public sealed record class ShopShareTableData
{
    [CsvField("메모")]
    public string? Memo { get; init; }

    [CsvField("NPC")]
    public string? Npc { get; init; }

    [CsvField("가성비")]
    public string? Value { get; init; }

    [CsvField("구매제한 1")]
    public string? PurchaseLimitType1 { get; init; }

    [CsvField("구매제한 2")]
    public string? PurchaseLimitType2 { get; init; }

    [CsvField("구매제한 횟수")]
    public int? PurchaseLimitCount { get; init; }

    [CsvField("그룹")]
    public string? Group { get; init; }

    [CsvField("비용")]
    public int? Cost { get; init; }

    [CsvField("아이템")]
    public string? Item { get; init; }

    [CsvField("재화")]
    public string? Currency { get; init; }
}

public static class MobiShop
{
    private static List<ShopTableData> m_ShopTableDataList = new();
    public static IReadOnlyList<ShopTableData> ShopTableDataList => m_ShopTableDataList;

    private static List<ShopExchangeTableData> m_ShopExchangeTableDataList = new();
    public static IReadOnlyList<ShopExchangeTableData> ShopExchangeTableDataList => m_ShopExchangeTableDataList;

    private static List<ShopShareTableData> m_ShopShareTableDataList = new();
    public static IReadOnlyList<ShopShareTableData> ShopShareTableDataList => m_ShopShareTableDataList;
    
    public static void LoadTable()
    {
        m_ShopTableDataList.Clear();
        m_ShopExchangeTableDataList.Clear();
        m_ShopShareTableDataList.Clear();

        var shopCsvPath = Path.Combine(AppContext.BaseDirectory, "assets", "shop_table.csv");
        if (File.Exists(shopCsvPath))
        {
            var shopTable = CsvParser.Load(shopCsvPath);
            var shopItems = CsvMapper.MapByAttributes<ShopTableData>(shopTable);
            m_ShopTableDataList.AddRange(shopItems);
            Console.WriteLine($"[MobiShop] LoadTable (ShopTable) - {m_ShopTableDataList.Count}");
        }
        else
        {
            Console.WriteLine($"[MobiShop] LoadTable (ShopTable) - file not found: {shopCsvPath}");
        }

        var exchangeCsvPath = Path.Combine(AppContext.BaseDirectory, "assets", "shop_exchange_table.csv");
        if (File.Exists(exchangeCsvPath))
        {
            var exchangeTable = CsvParser.Load(exchangeCsvPath);
            var exchangeItems = CsvMapper.MapByAttributes<ShopExchangeTableData>(exchangeTable);
            m_ShopExchangeTableDataList.AddRange(exchangeItems);
            Console.WriteLine($"[MobiShop] LoadTable (ShopExchangeTable) - {m_ShopExchangeTableDataList.Count}");
        }
        else
        {
            Console.WriteLine($"[MobiShop] LoadTable (ShopExchangeTable) - file not found: {exchangeCsvPath}");
        }

        var shareCsvPath = Path.Combine(AppContext.BaseDirectory, "assets", "shop_share_table.csv");
        if (File.Exists(shareCsvPath))
        {
            var shareTable = CsvParser.Load(shareCsvPath);
            var shareItems = CsvMapper.MapByAttributes<ShopShareTableData>(shareTable);
            m_ShopShareTableDataList.AddRange(shareItems);
            Console.WriteLine($"[MobiShop] LoadTable (ShopShareTable) - {m_ShopShareTableDataList.Count}");
        }
        else
        {
            Console.WriteLine($"[MobiShop] LoadTable (ShopShareTable) - file not found: {shareCsvPath}");
        }
    }
}
