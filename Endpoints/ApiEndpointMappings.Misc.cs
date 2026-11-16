using Microsoft.AspNetCore.Http;
using Npgsql;

static partial class ApiEndpointMappings
{
    /// <summary>
    /// 杂项接口：饰品、社团、编队进阶、抽卡重抽、碎片兑换、账号绑定等。
    /// 这些功能在单机服务器上大多没有实际副作用，返回成功即可让客户端流程走通；
    /// 有真实数据的（收藏、锁定）会写库。
    /// </summary>
    static void MapMiscEndpoints(WebApplication app, LocalServerState state, ApiCodec codec, LocalRequestLogger logger)
    {
        static object?[] Ok() => new object?[] { true };

        // ---- 饰品 ----
        app.MapMethods("/localap/api/Accessories/{accessoryId:long}/LevelUp/{levelTo:int}", new[] { "GET", "POST" }, async (HttpContext context, long accessoryId, int levelTo) =>
        {
            var present = await state.UserDataService.LevelUpAccessoryAsync(context, accessoryId, levelTo);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapMethods("/localap/api/Accessories/{accessoryId:long}/SwitchLock", new[] { "GET", "POST" }, async (HttpContext context, long accessoryId) =>
        {
            var present = await state.UserDataService.SwitchAccessoryLockAsync(context, accessoryId);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapMethods("/localap/api/Accessories/Sell", new[] { "GET", "POST" }, async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"accessories-sell payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.SellAccessoriesAsync(context, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        // 饰品自动售卖：客户端把不要的稀有度位图放在 rarityFlag（1=R, 2=SR, 4=SSR…拼接）。
        // 之前没有这个路由，界面一操作就 HE2-005-0。
        app.MapMethods("/localap/api/Accessories/SetAccessoryAutoSell", new[] { "GET", "POST" }, async context =>
        {
            var rarityFlag = context.Request.Query["rarityFlag"].FirstOrDefault();
            await logger.LogAsync($"accessories-auto-sell rarityFlag={rarityFlag}");
            var present = await state.UserDataService.SetAccessoryAutoSellAsync(context, rarityFlag);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });
        app.MapMethods("/localap/api/Accessories/SetFavorite", new[] { "GET", "POST" }, async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"accessories-favorite payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.SetAccessoryFavoriteAsync(context, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });


        foreach (var favPath in new[] { "/localap/api/Posters/SetFavorite", "/localap/api/Possessions/SetFavorite" })
        {
            app.MapMethods(favPath, new[] { "GET", "POST" }, async context =>
            {
                var body = await codec.ReadRequestBodyAsync(context);
                var present = await state.UserDataService.SetPosterFavoriteAsync(context, body);
                await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                    Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
            });
        }
        app.MapMethods("/localap/api/Circles/Release", new[] { "GET", "POST" }, async context =>
        {
            var present = await state.UserDataService.ReleaseCircleAsync(context);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });
        app.MapMethods("/localap/api/Photos/{photoId:long}/SwitchLock", new[] { "GET", "POST" }, async (HttpContext context, long photoId) =>
        {
            var present = await state.UserDataService.SwitchPhotoLockAsync(context, photoId);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        // ---- 社团 ----
        foreach (var path in new[]
        {
            "/localap/api/Circles/Expulsion",
        })
        {
            app.MapMethods(path, new[] { "GET", "POST" }, async context =>
            {
                await codec.WriteApiResultAsync(context, Ok());
            });
        }
        app.MapMethods("/localap/api/Circles/Resignation", new[] { "GET", "POST" }, async context =>
        {
            var present = await state.UserDataService.ResignCircleAsync(context);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        // ---- 编队进阶 ----
        app.MapMethods("/localap/api/Parties/{partyId:long}/ChangeName", new[] { "GET", "POST" }, async (HttpContext context, long partyId) =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"parties-change-name partyId={partyId} payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.ChangePartyNameAsync(context, partyId, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapMethods("/localap/api/Parties/{partyId:long}/EditPosition", new[] { "GET", "POST" }, async (HttpContext context, long partyId) =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"parties-edit-position partyId={partyId} payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.EditPartyPositionAsync(context, partyId, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapMethods("/localap/api/Parties/{partyId:long}/SetMultiParty", new[] { "GET", "POST" }, async (HttpContext context, long partyId) =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"parties-set-multi-party partyId={partyId} payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.SetMultiPartyAsync(context, partyId, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapMethods("/localap/api/Parties/EditTripleCastBasic", new[] { "GET", "POST" }, async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"parties-edit-triple-basic payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.EditTripleCastBasicAsync(context, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });
        app.MapMethods("/localap/api/Parties/EditTripleCastParty", new[] { "GET", "POST" }, async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"parties-edit-triple payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.EditTripleCastPartyRealAsync(context, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        // ---- 抽卡重抽 ----
        foreach (var path in new[]
        {
            "/localap/api/Gachas/ReRollGacha",
            "/localap/api/Gachas/DecideReRollGacha",
            "/localap/api/Gachas/GetReRollGachaResults",
        })
        {
            app.MapMethods(path, new[] { "GET", "POST" }, async context =>
            {
                await codec.WriteApiResultAsync(context, Ok());
            });
        }

        // ---- 道具 / 任务点数 ----
        foreach (var path in new[]
        {
            "/localap/api/Items/ExchangeCharacterPiece",
            "/localap/api/Items/UseBuffItem",
            "/localap/api/Missions/ExchangeMissionPoint",
        })
        {
            app.MapMethods(path, new[] { "GET", "POST" }, async context =>
            {
                await codec.WriteApiResultAsync(context, Ok());
            });
        }
        app.MapMethods("/localap/api/Player/ReleasePlayerRankCap", new[] { "GET", "POST" }, async context =>
        {
            var present = await state.UserDataService.ReleasePlayerRankCapAsync(context);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        // ---- 账号绑定 ----
        app.MapMethods("/localap/api/Account/ConnectAccount", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, Ok());
        });

        app.MapMethods("/localap/api/Account/Delete", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, Ok());
        });

        app.MapMethods("/localap/api/Characters/LinkCharacter", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, Ok());
        });

        // ---- 闪购 / 扭蛋 ----
        app.MapMethods("/localap/api/FlashSale/ReadFlashSaleStage", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, new object?[] { Array.Empty<object?>() });
        });

        app.MapMethods("/localap/api/Roulettes/Roll", new[] { "GET", "POST" }, async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"roulette-roll payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.RollRouletteAsync(context, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        // ---- 多人房间（单机返回空列表） ----
        app.MapMethods("/localap/api/MultiRoom/GetSearchMultiRooms", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, new object?[] { Array.Empty<object?>() });
        });

        app.MapMethods("/localap/api/MultiRooms/Send/Invite", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, Ok());
        });

        // ---- 摄影 ----
        // 返回结构参照官方：GeneratePhotoResult / LevelUpPhotoResult
        static object?[] GeneratePhotoResult() => new object?[]
        {
            0L,      // photo_id
            null,    // file_name
            null,    // sas_token
            3,       // rarity (Rare3)
            null,    // sign_master_id
            null     // photo_effect_master_id
        };

        app.MapMethods("/localap/api/Photos/GeneratePhotos", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, new object?[] { GeneratePhotoResult() });
        });

        app.MapMethods("/localap/api/Photos/RegeneratePhoto", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, GeneratePhotoResult());
        });

        app.MapMethods("/localap/api/Photos/PhotoLevelUp", new[] { "GET", "POST" }, async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            var photoId = ExtractFirstLong(body) ?? 0L;
            await logger.LogAsync($"photos-photo-level-up photoId={photoId} payload={ValueFormatter.Format(body)}");
            var result = await state.UserDataService.LevelUpPhotoAsync(context, photoId)
                ?? new object?[] { 0L, 1, 1 };
            await codec.WriteApiResultAsync(context, result);
        });

        foreach (var path in new[]
        {
            "/localap/api/Photo/AbilityVarietyUp",
            "/localap/api/Photo/AlbumReset",
            "/localap/api/Photo/ChangePhotoAbility",
            "/localap/api/Photo/Preset/Change/{presetId:long}",
            "/localap/api/Photo/Preset/SetName/{presetId:long}",
            "/localap/api/Photo/SetAlbumPublishing",
            "/localap/api/Photo/SetCharacterBaseTags",
            "/localap/api/Photo/WatchTheaterStory",
            "/localap/api/Photos/Sell",
        })
        {
            app.MapMethods(path, new[] { "GET", "POST" }, async context =>
            {
                await codec.WriteApiResultAsync(context, Ok());
            });
        }
    }
}