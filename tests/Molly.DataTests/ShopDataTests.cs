using System.Text.Json;
using Molly.Shop;
using Molly.Unpack;
using Molly.World;

/// <summary>상점 데이터셋(게임 데이터 NPCShop 계열)과 `/상점검색` 응답.</summary>
internal static class ShopDataTests
{
    private static readonly Dictionary<string, string> s_Sheets = new()
    {
        ["NPCShop"] = """
            {"sheet":"NPCShop","rows":[
              {"Id":"Church_A","DisplayName":"엔델리온의 봉헌소","NPC":"Endelyon","Tabs":["Church_Consumable","Church_Barter"],"Activate":{"conditions":[]}},
              {"Id":"Church_B","DisplayName":"크리스텔의 봉헌소","NPC":"Kristell","Tabs":["Church_Consumable"],"Activate":{"conditions":[]}},
              {"Id":"Event_Shop","DisplayName":"이벤트 상점","NPC":"None","Tabs":["Event_Tab"],"Activate":{"conditions":[{"_0":"EventOnGoing","_1":[["Event_Halloween"]]}]}}
            ]}
            """,
        ["NPCShopTab"] = """
            {"sheet":"NPCShopTab","rows":[
              {"Id":"Church_Consumable","AutoRestockCycle":"Weekly","EnableRequirements":[]},
              {"Id":"Church_Barter","AutoRestockCycle":"Daily","EnableRequirements":[]},
              {"Id":"Event_Tab","AutoRestockCycle":"None","EnableRequirements":[]},
              {"Id":"TEST_Tab","AutoRestockCycle":"None","EnableRequirements":[]}
            ]}
            """,
        ["NPCShopProduct"] = """
            {"sheet":"NPCShopProduct","rows":[
              {"Id":"P1","ShopTabSheetId":"Church_Consumable","ProductData":["HolyWater_Gold",0],"BuyRestrictStandard":"Account","BuyRestrictAmount":5,"EnableRequirements":[]},
              {"Id":"P1_Level","ShopTabSheetId":"Church_Consumable","ProductData":["HolyWater_Gold",0],"BuyRestrictStandard":"Account","BuyRestrictAmount":5,"EnableRequirements":[]},
              {"Id":"P2","ShopTabSheetId":"Church_Barter","ProductData":["HolyWater_Bread",0],"BuyRestrictStandard":"Character","BuyRestrictAmount":1,"EnableRequirements":[]},
              {"Id":"P3","ShopTabSheetId":"Church_Consumable","ProductData":["Potion_Old",0],"BuyRestrictStandard":"None","BuyRestrictAmount":0,
               "EnableRequirements":[{"_0":"TimeRange","_1":[["KST"],["2025-01-01 06:00:00"],["2025-02-01 05:59:59"]]}]},
              {"Id":"P4","ShopTabSheetId":"Event_Tab","ProductData":["Event_Coin",0],"BuyRestrictStandard":"None","BuyRestrictAmount":0,"EnableRequirements":[]},
              {"Id":"P5","ShopTabSheetId":"Church_Consumable","ProductData":[0,"Random_Group"],"BuyRestrictStandard":"None","BuyRestrictAmount":0,"EnableRequirements":[]},
              {"Id":"P6","ShopTabSheetId":"TEST_Tab","ProductData":["HolyWater_Gold",0],"BuyRestrictStandard":"None","BuyRestrictAmount":0,"EnableRequirements":[]},
              {"Id":"P7","ShopTabSheetId":"Church_Consumable","ProductData":["Costume",0],"BuyRestrictStandard":"None","BuyRestrictAmount":0,"EnableRequirements":[]}
            ]}
            """,
        ["NPCShopProductPreset"] = """
            {"sheet":"NPCShopProductPreset","rows":[
              {"Id":"HolyWater_Gold","CurrencyInfo":"Gold","Price":1500,"RewardData":["Consumable","HolyWater",1,1]},
              {"Id":"HolyWater_Bread","CurrencyInfo":"Bread","Price":10,"RewardData":["Consumable","HolyWater",10,1]},
              {"Id":"Potion_Old","CurrencyInfo":"Gold","Price":100,"RewardData":["Consumable","Potion",1,1]},
              {"Id":"Event_Coin","CurrencyInfo":"Coin","Price":3,"RewardData":["Equipment","Sword",1,1]},
              {"Id":"Costume","CurrencyInfo":"Gold","Price":1,"RewardData":["CostumePreset","Hat",1,1]}
            ]}
            """,
        ["CurrencyInfo"] = """
            {"sheet":"CurrencyInfo","rows":[
              {"Id":"Gold","CurrencyData":["Money","Gold",0,0,0]},
              {"Id":"Bread","CurrencyData":["Consumable","Invalid","CaitinBread",0,0]},
              {"Id":"Coin","CurrencyData":["EventItem","Invalid",0,"HalloweenCoin",0]}
            ]}
            """,
        ["MoneyInfo"] = """{"sheet":"MoneyInfo","rows":[{"MoneyType":"Gold","DisplayName":"골드"}]}""",
        ["EventItem"] = """{"sheet":"EventItem","rows":[{"Id":"HalloweenCoin","DisplayName":"할로윈 코인"}]}""",
        ["Consumable"] = """
            {"sheet":"Consumable","rows":[
              {"Id":"HolyWater","DisplayName":"성수"},{"Id":"CaitinBread","DisplayName":"케이틴 특제 통밀빵"},{"Id":"Potion","DisplayName":"생명력 포션"}
            ]}
            """,
        ["Equipment"] = """{"sheet":"Equipment","rows":[{"Id":"Sword","DisplayName":"호박 검"}]}""",
        ["NPC"] = """{"sheet":"NPC","rows":[{"Id":"Endelyon","DisplayName":"엔델리온"},{"Id":"Kristell","DisplayName":"크리스텔"}]}""",
        ["Event"] = """
            {"sheet":"Event","rows":[
              {"Id":"Event_Halloween","UnlockCondition":["KST","2026-10-15 06:00:00","2026-11-05 05:59:59","2026-10-15 06:00:00","2026-11-05 05:59:59"]}
            ]}
            """,
    };

    private static readonly DateTimeOffset s_Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(9));

    public static async Task RunAsync()
    {
        var dataset = new UnpackShopDataset();
        var requested = new List<string>();
        var table = await dataset.BuildAsync(Sheet(requested), default);
        Assert(requested.Take(5).SequenceEqual(["NPCShop", "NPCShopTab", "NPCShopProduct", "NPCShopProductPreset", "CurrencyInfo"]) &&
               !requested.Contains("EquipmentTool"), "상점은 상점→탭→상품→프리셋→재화 순서로 받고 보상이 없는 종류의 시트는 받지 않음");

        Assert(table.Items.Select(x => x.Item).SequenceEqual(["성수", "성수", "생명력 포션", "호박 검"]),
            "어느 상점에도 없는 탭·무작위 상품·이름 없는 보상(코스튬)은 빼고, 구매 조건만 다른 같은 상품은 하나로 합침");
        var gold = table.Items[0];
        Assert(gold is { Price: 1500, Currency: "골드", Barter: false, Restrict: "서버", Limit: 5, Cycle: "주간", Amount: 1 } &&
               gold.Sellers.Select(x => x.Name).SequenceEqual(["엔델리온", "크리스텔"]), "같은 탭을 쓰는 상점은 한 상품의 판매처로 모음");
        var barter = table.Items[1];
        Assert(barter is { Amount: 10, Price: 10, Currency: "케이틴 특제 통밀빵", Barter: true, Restrict: "캐릭터", Cycle: "일간" }, "아이템을 내는 상품은 물물교환");
        Assert(!table.Items[2].IsOnSale(s_Now) && table.Items[2].Periods.Count == 1, "판매 기간이 끝난 상품은 판매 중이 아님");
        var eventItem = table.Items[3];
        Assert(eventItem.Barter && eventItem.Currency == "할로윈 코인" && eventItem.Sellers.Single().Name == "이벤트 상점" &&
               eventItem.IsOnSale(s_Now) && !eventItem.IsOnSale(new DateTimeOffset(2026, 11, 5, 6, 0, 0, TimeSpan.FromHours(9))),
            "NPC가 없는 이벤트 상점은 상점 이름으로 적고 Event 기간에만 판매");

        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer)) dataset.Write(writer, table);
            using var cached = JsonDocument.Parse(buffer.ToArray());
            var read = dataset.Read(cached.RootElement);
            Assert(read.Items.Count == table.Items.Count && read.Items.Zip(table.Items).All(p =>
                    p.First with { Sellers = [], Periods = [] } == p.Second with { Sellers = [], Periods = [] } &&
                    p.First.Sellers.Select(s => (s.Name, s.Events.Count)).SequenceEqual(p.Second.Sellers.Select(s => (s.Name, s.Events.Count))) &&
                    p.First.Periods.SequenceEqual(p.Second.Periods) && p.First.IsOnSale(s_Now) == p.Second.IsOnSale(s_Now)),
                "상점 캐시 저장·읽기 왕복");
        }

        var world = new WorldTable([], [new NpcData("Endelyon", "엔델리온", ["티르코네일"], null), new NpcData("Kristell", "크리스텔", ["던바튼"], null)]);
        var found = ShopSearch.Find(table, world, "성 수", s_Now);
        Assert(found.Total == 2 && found.Items.Single().Item == "성수" &&
               found.Items[0].Offers.Select(x => string.Join(", ", x.Locations)).SequenceEqual(["티르코네일-엔델리온, 던바튼-크리스텔", "티르코네일-엔델리온"]),
            "상점검색은 공백을 무시하고 같은 아이템의 판매 방법을 묶으며 판매처를 NPC 배치 지역과 함께 적음");
        var embed = ShopMessages.Build(found.Items[0], "출처").Single();
        Assert(embed.Title == "🛒 성수" && embed.Footer?.Text == "출처" &&
               embed.Fields.Select(x => (x.Name, x.Value)).SequenceEqual([
                   ("💰 1,500 골드", "> 📍 티르코네일-엔델리온, 던바튼-크리스텔\n> ⏳ 서버 5회 · 주간\n\u200b"),
                   ("🔄 케이틴 특제 통밀빵 10개 → 10개", "> 📍 티르코네일-엔델리온\n> ⏳ 캐릭터 1회 · 일간")]),
            "상점 Embed는 아이템마다 하나이고 판매 방법마다 비용 필드에 판매처·구매 제한을 인용 줄로 묶고 방법 사이를 띄움");

        var crowded = new ShopItemResult("붕대", Enumerable.Range(1, 25).Select(i => new ShopOffer(
            new ShopProduct("붕대", 1, i, "골드", false, "", 0, "", [], []), Enumerable.Range(1, 31).Select(n => $"지역{n}-상인{n}").ToArray())).ToArray());
        var crowdedEmbeds = ShopMessages.Build(crowded, "출처");
        Assert(crowdedEmbeds.Count > 1 && crowdedEmbeds.All(e => e.Length <= ShopMessages.MaxCharactersPerMessage) &&
               crowdedEmbeds[1].Title == "🛒 붕대 (이어서)" && crowdedEmbeds[^1].Footer?.Text == "출처" && crowdedEmbeds[0].Footer is null &&
               crowdedEmbeds.Sum(e => e.Fields.Length) == 25 && crowdedEmbeds[0].Fields[0].Value.Contains("지역31-상인31"),
            "판매처는 줄이지 않고 모두 적으며, 한 아이템이 글자 수 한도를 넘으면 (이어서) Embed로 나눔");

        var byCurrency = ShopSearch.Find(table, null, "통밀빵", s_Now);
        Assert(byCurrency.Items.Single().Offers.Single() is { Product.Barter: true, Locations: ["엔델리온"] },
            "물물교환은 내는 아이템 이름으로도 찾고, NPC·지역 데이터가 없으면 NPC 이름만 적음");
        Assert(ShopSearch.Find(table, world, "생명력 포션", s_Now).Items.Count == 0, "판매 기간이 끝난 상품은 검색되지 않음");

        var many = new ShopTable(Enumerable.Range(1, 40).Select(i => new ShopProduct(i % 2 == 0 ? "포션" : $"큰 포션 {i}", 1, i, "골드", false, "", 0, "",
            [new ShopSeller("상인", [])], [])));
        var limited = ShopSearch.Find(many, null, "포션", s_Now);
        Assert(limited.Total == 40 && limited.Shown == ShopSearch.MaxResults && limited.Items[0].Item == "포션" && limited.Items[0].Offers.Count == 20,
            "결과가 많으면 이름이 같은 아이템을 먼저 두고 최대 개수까지만 보여줌");
        var pages = ShopMessages.BuildPages(limited);
        Assert(pages.All(p => p.Count <= ShopMessages.MaxEmbedsPerMessage && p.Sum(e => e.Length) <= ShopMessages.MaxCharactersPerMessage) &&
               pages.Sum(p => p.Count) == limited.Items.Count && ShopMessages.Header("포션", limited).Contains("25개만 보여드려요"),
            "상점 Embed는 Discord 메시지 제한에 맞춰 나누고 잘린 결과를 안내함");
        Assert(ShopMessages.Header("성수", found).Split('\n') is [_, ShopMessages.Legend], "상점 검색 요약 줄 아래에 아이콘 설명을 붙임");
        Assert(ShopMessages.Limit(many.Items[0]) == "♾️ 구매 제한 없음", "구매 제한이 없는 상품 표시");

        await Reject(dataset, "NPCShopProduct", """{"sheet":"NPCShopProduct","rows":[]}""", "상품이 하나도 없으면 거부");
        await Reject(dataset, "NPCShopTab", """
            {"sheet":"NPCShopTab","rows":[{"Id":"Church_Consumable","AutoRestockCycle":"Weekly","EnableRequirements":[{"_0":"TimeRange","_1":[["PST"],["2025-01-01 00:00:00"]]}]}]}
            """, "알 수 없는 시간대는 거부");
    }

    private static UnpackSheetFetcher Sheet(List<string>? requested = null, string? replaceSheet = null, string? replaceJson = null) => (sheet, _) =>
    {
        requested?.Add(sheet);
        var json = sheet == replaceSheet ? replaceJson : s_Sheets.GetValueOrDefault(sheet);
        return json is not null
            ? Task.FromResult(JsonDocument.Parse(json))
            : throw new UnpackDataException(UnpackRepoStatus.NotFound, sheet + " 없음");
    };

    private static async Task Reject(UnpackShopDataset dataset, string sheet, string json, string name)
    {
        try { await dataset.BuildAsync(Sheet(null, sheet, json), default); }
        catch (InvalidDataException) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
