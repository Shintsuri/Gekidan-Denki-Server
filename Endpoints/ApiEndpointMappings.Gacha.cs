static partial class ApiEndpointMappings
{
    static void MapGachaEndpoints(WebApplication app,LocalServerState state,ApiCodec codec,LocalRequestLogger logger)
    {
        app.MapMethods("/localap/api/Gachas", new[] { "GET", "POST" }, async context=>
        {
            if(System.Environment.GetEnvironmentVariable("WDS_GACHA_EMPTY")=="1")
            {
                await codec.WriteApiResultAsync(context,Array.Empty<object?>());
                return;
            }
            var operations=await state.GameOperations.GetAsync();
            // 放开全部卡池（不再按资源过滤）
            var pools=GameResults.GachaListResult().Cast<object?[]>().Where(row=>!operations.Configuration.Gachas.TryGetValue(Convert.ToInt64(row[0]),out var on)||on).ToArray();
            // 客户端对 /api/Gachas 也按 5 帧读取：只回 2 帧会让抽卡界面一直等（卡在加载）。
            await codec.WriteApiFramesAsync(context,pools,Array.Empty<object?>(),Array.Empty<object?>(),Array.Empty<object?>(),"five-frame");
        });
        app.MapGet("/localap/api/Gachas/CharacterLineup/{gachaMasterId:long}",(HttpContext context,long gachaMasterId)=>Run(context,"gacha-lineup",async ()=>await codec.WriteApiResultAsync(context,GameResults.CharacterLineupResult(gachaMasterId))));
        app.MapGet("/localap/api/Gachas/PosterLineup/{gachaMasterId:long}",(HttpContext context,long gachaMasterId)=>Run(context,"gacha-lineup",async ()=>await codec.WriteApiResultAsync(context,GameResults.PosterLineupResult(gachaMasterId))));
        app.MapMethods("/localap/api/Gachas/GetGachaHistories",["GET","POST"],context=>Run(context,"gacha-history",async ()=>await codec.WriteApiResultAsync(context,await state.UserDataService.GetGachaHistoriesAsync(context))));
        app.MapPost("/localap/api/Gachas/Roll/{gachaDetailMasterId:long}",(HttpContext context,long gachaDetailMasterId)=>Run(context,"gacha-roll",async ()=> {
            var purchase=await state.UserDataService.RollGachaAsync(context,gachaDetailMasterId);await Write(context,purchase);
        }));
        app.MapMethods("/localap/api/Shops/ExchangeShopThing/{exchangeShopThingId:long}/{quantity:int}",["GET","POST"],(HttpContext context,long exchangeShopThingId,int quantity)=>Run(context,"gacha-exchange",async ()=> {
            // 先按抽卡交换处理；不是抽卡商品时走活动交换所
            if (GachaRules.Exchanges.ContainsKey(exchangeShopThingId))
                await Write(context,await state.UserDataService.ExchangeGachaAsync(context,[(exchangeShopThingId,quantity)]));
            else
                await Write(context,await state.UserDataService.ExchangeEventShopThingAsync(context,exchangeShopThingId,quantity));
        }));
        app.MapPost("/localap/api/Shops/ExchangeShopThings",context=>Run(context,"gacha-exchange",async ()=> {
            var body=await codec.ReadRequestBodyAsync(context);
            if(body is not object?[] rows)throw new BadHttpRequestException(GachaErrors.Invalid,400);
            if(rows is [object?[] nested] && nested is [object?[], ..])rows=nested;
            if(rows.Length is <1 or >100)
                throw new BadHttpRequestException(GachaErrors.Invalid,400);
            var requests=rows.Select(row=> {
                if(row is not object?[] values || values.Length!=2 || !long.TryParse(Convert.ToString(values[0]),out var id) || !int.TryParse(Convert.ToString(values[1]),out var quantity))
                    throw new BadHttpRequestException(GachaErrors.Invalid,400);
                return (id,quantity);
            }).ToArray();
            // 批量兑换里可能混着「抽卡交换」和「活动交换所」两种商品：
            // 之前一律走 ExchangeGachaAsync，活动商品查不到就会 404（客户端表现为 HE29-012-0）。
            var gachaRequests=requests.Where(r=>GachaRules.Exchanges.ContainsKey(r.id)).ToArray();
            var eventRequests=requests.Where(r=>!GachaRules.Exchanges.ContainsKey(r.id)).ToArray();
            var merged=new List<object?>();
            if(gachaRequests.Length>0)
            {
                var purchase=await state.UserDataService.ExchangeGachaAsync(context,gachaRequests);
                merged.AddRange(purchase.Result);
                merged.AddRange(purchase.Present);
            }
            foreach(var (id,quantity) in eventRequests)
            {
                var exchange=await state.UserDataService.ExchangeEventShopThingAsync(context,id,quantity);
                merged.AddRange(exchange.Result);
                merged.AddRange(exchange.Present);
            }
            await codec.WriteApiFramesAsync(context,merged.ToArray(),Array.Empty<object?>(),Array.Empty<object?>(),Array.Empty<object?>(),"result-present-lz4-five-frame");
        }));
        async Task Write(HttpContext context,UserDataService.GachaPurchase purchase)=>await codec.WriteApiFramesAsync(context,purchase.Result,purchase.Present,Array.Empty<object?>(),Array.Empty<object?>(),"result-present-lz4-five-frame");
        async Task Run(HttpContext context,string operation,Func<Task> action)
        {
            try {await action();}
            catch(BadHttpRequestException error)
            {
                await logger.LogAsync($"level=WARN operation={operation} errorCode={error.Message} outcome=failed");
                context.Response.StatusCode=error.StatusCode;
                await codec.WriteApiFramesAsync(context,Array.Empty<object?>(),Array.Empty<object?>(),Array.Empty<object?>(),Array.Empty<object?>(),"five-frame");
            }
        }
    }
}
