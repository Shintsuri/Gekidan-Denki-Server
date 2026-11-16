static partial class ApiEndpointMappings
{
    static void MapGameplayEndpoints(WebApplication app, LocalServerState state, ApiCodec codec, LocalRequestLogger logger)
    {
        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted
                && context.Request.Path.StartsWithSegments("/localap/api/Lives")
                && error.Message is LiveProgressionErrors.InvalidStart or LiveProgressionErrors.InvalidFinish
                    or LiveProgressionErrors.InvalidJudges or LiveProgressionErrors.SessionMissing
                    or LiveProgressionErrors.SessionConflict or LiveProgressionErrors.GhostUnavailable)
            {
                await logger.LogAsync($"level=WARN operation=live path={context.Request.Path} requestId={context.TraceIdentifier} errorCode={error.Message} outcome=failed");
                await codec.WriteApiErrorAsync(context, error.Message, error.StatusCode);
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted
                && error.Message is CharacterFavoriteErrors.InvalidRequest or CharacterFavoriteErrors.CharacterMissing)
            {
                await logger.LogAsync($"level=WARN operation=character-favorite path={context.Request.Path} requestId={context.TraceIdentifier} errorCode={error.Message} outcome=failed");
                await codec.WriteApiErrorAsync(context, error.Message, error.StatusCode);
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted
                && context.Request.Path.StartsWithSegments("/localap/api/Parties")
                && error.Message is UserDataService.PartyInvalid or UserDataService.PartyNotFound or UserDataService.PartyMemberInvalid)
            {
                await logger.LogAsync($"level=WARN operation=party path={context.Request.Path} requestId={context.TraceIdentifier} errorCode={error.Message} outcome=failed");
                await codec.WriteApiErrorAsync(context, error.Message, error.StatusCode);
            }
        });
        MapLessonEndpoints(app, state, codec, logger);
        app.MapMethods("/localap/api/Lives/UpdateClearLamps/{multiLiveId:long}", new[] { "GET", "POST" }, async (HttpContext context, long multiLiveId) =>
        {
            var body = context.Request.Method == "POST" ? await codec.ReadRequestBodyAsync(context) : null;
            await logger.LogAsync($"lives-update-clear-lamps multiLiveId={multiLiveId} query={context.Request.QueryString} payload={ValueFormatter.Format(body)}");
            await codec.WriteApiResultAsync(context, new object?[] { 0 });
        });

        app.MapMethods("/localap/api/Lives/UpdateClearLamp/{multiLiveId:long}", new[] { "GET", "POST" }, async (HttpContext context, long multiLiveId) =>
        {
            var body = context.Request.Method == "POST" ? await codec.ReadRequestBodyAsync(context) : null;
            await logger.LogAsync($"lives-update-clear-lamp multiLiveId={multiLiveId} query={context.Request.QueryString} payload={ValueFormatter.Format(body)}");
            await codec.WriteApiResultAsync(context, new object?[] { 0 });
        });

        app.MapGet("/localap/api/Lives/GetMultiLiveInformation/{multiLiveId:long}", async (HttpContext context, long multiLiveId) =>
        {
            await logger.LogAsync($"lives-get-multi-live-information multiLiveId={multiLiveId} query={context.Request.QueryString}");
            var identity = await state.UserDataService.GetLiveIdentityAsync(context);
            var result = state.MultiLiveRealtime.ReadLiveInformation(identity, multiLiveId);
            await codec.WriteApiFramesAsync(
                context,
                result,
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapPost("/localap/api/Lives/MatchingGhostLive", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"lives-matching-ghost-live payload={ValueFormatter.Format(body)}");
            await codec.WriteApiFramesAsync(
                context,
                await state.UserDataService.MatchGhostLiveAsync(context),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapPost("/localap/api/Lives/Start", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"lives-start payload={ValueFormatter.Format(body)}");
            object?[] unit;
            try
            {
                unit = await state.UserDataService.RegisterLiveStartAsync(context, body, buildUnit: true);
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.WriteAllText(@"D:\WDS\wk\start_error.txt",
                        ex.GetType().FullName + "\n" + ex.Message + "\n" + ex.StackTrace);
                }
                catch { }
                throw;
            }
            await codec.WriteApiFramesAsync(
                context,
                unit,
                await state.UserDataService.GetMusicPresentDataAsync(context),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        // 演出（音游）相关：参考服务端也是空桩，这里复用 Start 的逻辑
        foreach (var concertPath in new[] { "/localap/api/Lives/StartConcert", "/localap/api/Lives/StartBonusLive", "/localap/api/Lives/StartTournament" })
        {
            app.MapPost(concertPath, async context =>
            {
                var body = await codec.ReadRequestBodyAsync(context);
                await logger.LogAsync($"lives-start-alt {concertPath} payload={ValueFormatter.Format(body)}");
                var unit = await state.UserDataService.RegisterLiveStartAsync(context, body, buildUnit: true);
                await codec.WriteApiFramesAsync(
                    context,
                    unit,
                    await state.UserDataService.GetMusicPresentDataAsync(context),
                    Array.Empty<object?>(),
                    Array.Empty<object?>(),
                    "five-frame");
            });
        }

        foreach (var tipPath in new[] { "/localap/api/Lives/ReadConcertTips", "/localap/api/Lives/CalculateTimingEvents" })
        {
            app.MapMethods(tipPath, new[] { "GET", "POST" }, async context =>
            {
                await logger.LogAsync($"lives-alt-simple {tipPath}");
                await codec.WriteApiResultAsync(context, Array.Empty<object?>(), "five-frame");
            });
        }

        app.MapPost("/localap/api/Lives/StartGhostLive", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"lives-start-ghost-live payload={ValueFormatter.Format(body)}");
            var unit = await state.UserDataService.RegisterLiveStartAsync(context, body, buildUnit: true,
                mode: LiveRewardRules.Mode.Ghost);
            await codec.WriteApiFramesAsync(
                context,
                unit,
                await state.UserDataService.GetMusicPresentDataAsync(context),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapPost("/localap/api/Lives/StartMultiLive", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"lives-start-multi-live payload={ValueFormatter.Format(body)}");
            if (body is not object?[] multi || multi.Length != 5)
                throw new BadHttpRequestException(LiveProgressionErrors.InvalidStart);
            var unit = await state.UserDataService.StartMultiLiveAsync(context, multi, state.MultiLiveRealtime);
            await codec.WriteApiFramesAsync(
                context,
                unit,
                await state.UserDataService.GetMusicPresentDataAsync(context),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapMethods("/localap/api/Lives/StartMusicCourseLive", new[] { "GET", "POST" }, async context =>
        {
            var body = context.Request.Method == "POST" ? await codec.ReadRequestBodyAsync(context) : null;
            await logger.LogAsync($"lives-start-music-course-live payload={ValueFormatter.Format(body)}");
            var unit = await state.UserDataService.RegisterLiveStartAsync(context, body, validateMusicUnlock:false, buildUnit: true, ordinaryRewards:false);
            await codec.WriteApiFramesAsync(
                context,
                unit,
                await state.UserDataService.GetMusicPresentDataAsync(context),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapPost("/localap/api/Lives/StartTripleCastLive", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"lives-start-triple-cast-live payload={ValueFormatter.Format(body)}");
            if (body is object?[] triple && triple.Length >= 1)
                await state.UserDataService.RegisterLiveStartAsync(context, new object?[] { null, triple[0], null, null, true, 1, null, false }, validateMusicUnlock:false, ordinaryRewards:false);
            await codec.WriteApiFramesAsync(
                context,
                GameResults.TripleCastLiveStartResult(body),
                (await state.UserDataService.GetMusicPresentDataAsync(context)).Concat(
                    GameResults.TripleCastLiveStartPresentData(body).OfType<object?[]>().Where(row=>row[0] is not 25)).ToArray(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-present-lz4-five-frame");
        });

        app.MapPost("/localap/api/Lives/Music/EditBookmark", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"lives-music-edit-bookmark payload={ValueFormatter.Format(body)}");
            await codec.WriteApiFramesAsync(
                context,
                new object?[] { true },
                await state.UserDataService.EditMusicBookmarkAsync(context, body),
                Array.Empty<object?>(),
                Array.Empty<object?>());
        });

        app.MapMethods("/localap/api/Lives/StartTrialPartyEventStage", new[] { "GET", "POST" }, async context =>
        {
            var body = context.Request.Method == "POST" ? await codec.ReadRequestBodyAsync(context) : null;
            await logger.LogAsync($"lives-start-trial-party-event-stage payload={ValueFormatter.Format(body)}");
            await state.UserDataService.RegisterLiveStartAsync(context, body, validateMusicUnlock:false, ordinaryRewards:false);
            await codec.WriteApiFramesAsync(
                context,
                GameResults.LiveStartResult(body),
                await state.UserDataService.GetMusicPresentDataAsync(context),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapPost("/localap/api/Lives/StartLesson", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            var lesson = await state.UserDataService.StartLessonLiveAsync(context, body);
            await logger.LogAsync("lives-start-lesson outcome=success");
            await codec.WriteApiFramesAsync(
                context,
                lesson.Result,
                lesson.Present,
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapMethods("/localap/api/Lives/Retire", new[] { "GET", "POST" }, async context =>
        {
            var body = context.Request.Method == "POST" ? await codec.ReadRequestBodyAsync(context) : null;
            await state.UserDataService.RetireLiveAsync(context);
            await logger.LogAsync($"lives-retire payload={ValueFormatter.Format(body)}");
            await codec.WriteApiFramesAsync(
                context,
                new object?[] { true },
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "lives-retire-five-frame");
        });

        app.MapPost("/localap/api/Lives/FinishAndValidate", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            // 诊断：把客户端上传的结构完整写盘
            try
            {
                static string Describe(object? v, int depth = 0)
                {
                    if (v is null) return "null";
                    if (v is object?[] array)
                        return depth >= 2 ? $"array[{array.Length}]"
                            : "[" + string.Join(",", array.Select(x => Describe(x, depth + 1))) + "]";
                    return v switch
                    {
                        string s => "\"" + s + "\"",
                        bool b => b ? "true" : "false",
                        _ => v.ToString() ?? "?"
                    };
                }
                System.IO.File.WriteAllText(@"D:\WDS\wk\finish_payload.txt", Describe(body));
            }
            catch { }
            await logger.LogAsync($"lives-finish-and-validate payload={ValueFormatter.Format(body)}");
            object?[] settlementResult;
            object?[] settlementPresent;
            try
            {
                var settlement = await state.UserDataService.BuildLiveSettlementAsync(context, body);
                settlementResult = settlement.Result;
                settlementPresent = settlement.Present;
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.WriteAllText(@"D:\WDS\wk\finish_error.txt",
                        ex.GetType().FullName + "\n" + ex.Message + "\n" + ex.StackTrace);
                }
                catch { }
                throw;
            }
            await codec.WriteApiFramesAsync(
                context,
                settlementResult,
                settlementPresent,
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-present-lz4-five-frame");
        });

        app.MapPost("/localap/api/Lives/FinishAnotherNotationLive", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            var settlement = await state.UserDataService.FinishAnotherNotationAsync(context, body);
            await codec.WriteApiFramesAsync(context, settlement.Result, settlement.Present,
                Array.Empty<object?>(), Array.Empty<object?>(), "result-present-lz4-five-frame");
        });

        app.MapPost("/localap/api/Items/UseStaminaRecoveryItems", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"items-use-stamina-recovery-items payload={ValueFormatter.Format(body)}");
            await codec.WriteApiFramesAsync(
                context,
                new object?[] { true },
                await state.UserDataService.UseStaminaRecoveryItemsAsync(context, body),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "five-frame");
        });

        app.MapPost("/localap/api/CharacterBases/{characterBaseMasterId:long}/SetCostume/{costumeMasterId:long}", async (HttpContext context, long characterBaseMasterId, long costumeMasterId) =>
        {
            await logger.LogAsync($"character-bases-set-costume characterBaseMasterId={characterBaseMasterId} costumeMasterId={costumeMasterId}");
            var present = await state.UserDataService.SetCostumeAsync(context, characterBaseMasterId, costumeMasterId);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present, Array.Empty<object?>(), Array.Empty<object?>());
        });

        app.MapPost("/localap/api/Characters/SetFavorite", async (HttpContext context) =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            var present = await state.UserDataService.SetCharacterFavoriteAsync(context, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapPost("/localap/api/Characters/SetPortalMCharacter", async (HttpContext context) =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            var present = await state.UserDataService.SetPortalCharacterAsync(context, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present, Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        // 客户端新版走这个路径（旧的 SetPortalMCharacter 保留兼容）
        app.MapPost("/localap/api/Characters/Portal/SetCharacter", async (HttpContext context) =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"characters-portal-set-character payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.SetPortalCharacterAsync(context, body);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present, Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapPost("/localap/api/Characters/{characterId:long}/AddExperience", async (HttpContext context, long characterId) =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"characters-add-experience characterId={characterId} payload={ValueFormatter.Format(body)}");
            var present = await state.UserDataService.AddCharacterExperienceAsync(context, characterId, body);
            await codec.WriteApiFramesAsync(
                context,
                new object?[] { true },
                present,
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "characters-add-experience-present-lz4-five-frame");
        });

        // 客户端 2.31.3 的才能开花：Characters/{id}/BloomTalent/{stageTo}
        app.MapPost("/localap/api/Characters/{characterId:long}/BloomTalent/{stageTo:int}",
            async (HttpContext context, long characterId, int stageTo) =>
        {
            var present = await state.UserDataService.BloomTalentAsync(context, characterId, stageTo);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        // 双人卡形态切换：selectionType 1=Primary 2=Secondary 99=Random
        app.MapPost("/localap/api/Characters/{characterId:long}/UpdateSelectionType",
            async (HttpContext context, long characterId) =>
        {
            var selectionType = int.TryParse(context.Request.Query["selectionType"], out var parsed) ? parsed : 1;
            var present = await state.UserDataService.UpdateSelectionTypeAsync(context, characterId, selectionType);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapPost("/localap/api/Characters/{characterId:long}/Awaken", async (HttpContext context, long characterId) =>
        {
            var present = await state.UserDataService.AwakenCharacterAsync(context, characterId);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapPost("/localap/api/Characters/{characterId:long}/SwitchCharacterDisplayAwakeningStatusAsync", async (HttpContext context, long characterId) =>
        {
            var present = await state.UserDataService.SwitchCharacterDisplayAwakeningStatusAsync(context, characterId);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapPost("/localap/api/Characters/{characterId:long}/EnhanceSenseLevel/{senseLevel:int}", async (HttpContext context, long characterId, int senseLevel) =>
        {
            var priority = 1;
            if (context.Request.Query.ContainsKey("priority") && !int.TryParse(context.Request.Query["priority"], out priority))
                throw new BadHttpRequestException(CharacterSenseErrors.InvalidRequest);
            var present = await state.UserDataService.EnhanceCharacterSenseAsync(context, characterId, senseLevel, priority);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapPost("/localap/api/Characters/{characterMasterId:long}/ReleaseSideStory", async (HttpContext context, long characterMasterId) =>
        {
            var order = ReadIntQuery(context, "order") ?? 1;
            await logger.LogAsync($"characters-release-side-story characterMasterId={characterMasterId} order={order} query={context.Request.QueryString}");
            await codec.WriteApiFramesAsync(
                context,
                new object?[] { true },
                await state.UserDataService.ReleaseSideStoryAsync(context, characterMasterId, order),
                Array.Empty<object?>(),
                Array.Empty<object?>());
        });

        app.MapPost("/localap/api/Characters/BulkLevelUp", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"characters-bulk-level-up payload={ValueFormatter.Format(body)}");
            // 真正执行一键升级（消耗经验道具、提升等级）
            var present = await state.UserDataService.BulkLevelUpAsync(context, body);
            await codec.WriteApiFramesAsync(
                context,
                new object?[] { true },
                present,
                Array.Empty<object?>(),
                GameResults.CharacterBulkLevelUpNotifications(),
                "five-frame");
        });

        app.MapPost("/localap/api/CharacterMissions/{characterBaseMasterId:long}/receiveAllMission", async (HttpContext context, long characterBaseMasterId) =>
        {
            await logger.LogAsync($"character-missions-receive-all characterBaseMasterId={characterBaseMasterId} query={context.Request.QueryString}");
            var received = await state.UserDataService.ReceiveCharacterMissionsAsync(context, new[] { characterBaseMasterId }, false);
            await codec.WriteApiFramesAsync(context,received.Result,received.Present,Array.Empty<object?>(),Array.Empty<object?>(),"five-frame");
        });

        app.MapMethods("/localap/api/CharacterMissions/BulkReceiveAllMission", new[] { "GET", "POST" }, async context =>
        {
            var body = context.Request.Method == "POST" ? await codec.ReadRequestBodyAsync(context) : null;
            await logger.LogAsync($"character-missions-bulk-receive-all payload={ValueFormatter.Format(body)}");
            if (body is not object?[] ids || ids.Length is < 1 or > 100 || ids.Any(id => id is not (byte or sbyte or short or ushort or int or uint or long)))
                throw new BadHttpRequestException(CharacterMissionErrors.InvalidRequest);
            var received = await state.UserDataService.ReceiveCharacterMissionsAsync(context, ids.Select(Convert.ToInt64).ToArray(), true);
            await codec.WriteApiFramesAsync(context,received.Result,received.Present,Array.Empty<object?>(),Array.Empty<object?>(),"five-frame");
        });
        app.MapPost("/localap/api/CharacterMissions/{characterBaseMasterId:long}/receiveKeyMission", async (HttpContext context,long characterBaseMasterId) =>
        {
            var received = await state.UserDataService.ReceiveCharacterMissionsAsync(context,new[] {characterBaseMasterId},false,true);
            await codec.WriteApiFramesAsync(context,received.Result,received.Present,Array.Empty<object?>(),Array.Empty<object?>(),"five-frame");
        });

        app.MapPost("/localap/api/Comics/Read/{comicEpisodeId:long}", async (HttpContext context, long comicEpisodeId) =>
        {
            await logger.LogAsync($"comics-read comicEpisodeId={comicEpisodeId} query={context.Request.QueryString}");
            var rewards = await state.UserDataService.ReadComicAsync(context, comicEpisodeId);
            await codec.WriteApiResultAsync(context, rewards);
        });

        app.MapPost("/localap/api/Missions/receiveRewards", async context =>
        {
            var missionCategory = ReadIntQuery(context, "missionCategory");
            await logger.LogAsync($"missions-receive-rewards missionCategory={missionCategory} query={context.Request.QueryString}");
            var claim = await state.UserDataService.ReceiveMissionRewardsAsync(context, null, missionCategory);
            await codec.WriteApiFramesAsync(
                context,
                claim.Rewards,
                claim.Present,
                Array.Empty<object?>(),
                Array.Empty<object?>(), "five-frame");
        });

        app.MapPost("/localap/api/Missions/{missionId:long}/receiveCurrentRewards", async (HttpContext context, long missionId) =>
        {
            await logger.LogAsync($"missions-receive-current-rewards missionId={missionId} query={context.Request.QueryString}");
            var claim = await state.UserDataService.ReceiveMissionRewardsAsync(context, missionId, null);
            await codec.WriteApiFramesAsync(
                context,
                claim.Rewards,
                claim.Present,
                Array.Empty<object?>(),
                Array.Empty<object?>(), "five-frame");
        });

        MapMissionPassEndpoints(app, state);

        app.MapGet("/localap/api/MultiRooms", async context =>
        {
            await logger.LogAsync($"multi-rooms query={context.Request.QueryString}");
            // 真实房间列表：没有房间就返回空，不再返回假房间
            var settingRaw = context.Request.Query["mMultiLiveHallType"].ToString();
            long setting = long.TryParse(settingRaw, out var s) ? s : 0;
            var rooms = state.MultiLiveRealtime.SearchPublicRooms(setting);
            await codec.WriteApiFramesAsync(
                context,
                rooms,
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapPost("/localap/api/MultiRooms/Detail", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"multi-rooms-detail payload={ValueFormatter.Format(body)}");
            await codec.WriteApiFramesAsync(
                context,
                GameResults.MultiRoomDetail(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapPost("/localap/api/MultiRooms/Create", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"multi-rooms-create payload={ValueFormatter.Format(body)}");
            await codec.WriteApiFramesAsync(
                context,
                GameResults.MultiRoomCreate(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapPost("/localap/api/MultiRooms/Join", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"multi-rooms-join payload={ValueFormatter.Format(body)}");
            await codec.WriteApiFramesAsync(
                context,
                GameResults.MultiRoomDetail(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "result-lz4-five-frame");
        });

        app.MapPost("/localap/api/MultiRooms/Joined", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"multi-rooms-joined payload={ValueFormatter.Format(body)}");
            await codec.WriteApiFramesAsync(
                context,
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>());
        });

        app.MapPost("/localap/api/MultiRooms/Invited", async context =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            await logger.LogAsync($"multi-rooms-invited payload={ValueFormatter.Format(body)}");
            await codec.WriteApiFramesAsync(
                context,
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                Array.Empty<object?>());
        });

        app.MapPost("/localap/api/Parties/{srcOrder:int}/CopyTo/{destOrder:int}", async (HttpContext context, int srcOrder, int destOrder) =>
        {
            // 客户端同时在路径和查询中传顺序；重复参数必须一致，避免复制错队。
            foreach (var (key, expected) in new[] { ("srcOrder", srcOrder), ("destOrder", destOrder) })
            {
                if (context.Request.Query.TryGetValue(key, out var values)
                    && (values.Count != 1 || !int.TryParse(values[0], out var actual) || actual != expected))
                    throw new BadHttpRequestException(UserDataService.PartyInvalid, 400);
            }
            var present = await state.UserDataService.CopyPartyAsync(context, srcOrder, destOrder);
            await codec.WriteApiFramesAsync(context, new object?[] { true }, present,
                Array.Empty<object?>(), Array.Empty<object?>(), "five-frame");
        });

        app.MapPost("/localap/api/Parties/{partyId:long}/EditParty", async (HttpContext context, long partyId) =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            var present = await state.UserDataService.EditPartyAsync(context, partyId, body);
            await logger.LogAsync($"parties-edit-party saved partyId={partyId} members=5");
            await codec.WriteApiFramesAsync(
                context,
                new object?[] { true },
                present,
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "five-frame");
        });

        app.MapPost("/localap/api/Parties/{partyId:long}/EditPartyWithRecommended", async (HttpContext context, long partyId) =>
        {
            var body = await codec.ReadRequestBodyAsync(context);
            var present = await state.UserDataService.EditPartyAsync(context, partyId, body);
            await logger.LogAsync($"parties-edit-party-with-recommended saved partyId={partyId} members=5");
            await codec.WriteApiFramesAsync(
                context,
                new object?[] { true },
                present,
                Array.Empty<object?>(),
                Array.Empty<object?>(),
                "five-frame");
        });
    }
}