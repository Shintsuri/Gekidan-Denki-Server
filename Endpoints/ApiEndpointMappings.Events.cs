using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;

/// <summary>玩家剧情活动状态。</summary>
sealed record StoryEventStateRow(long UserId, long EventMasterId, long Point, int Rank, bool TipsRead);

static partial class ApiEndpointMappings
{
    /// <summary>
    /// 活动相关接口：排行在单机服务器上没有真实数据，返回空列表让客户端界面正常工作；
    /// 玩家自己的点数、名次和 tips 状态从 user_story_event_states 读取。
    /// </summary>
    static void MapEventEndpoints(WebApplication app, LocalServerState state, ApiCodec codec, LocalRequestLogger logger)
    {
        // 空排行结果：[rawRanking[], userProfiles[]]
        static object?[] EmptyRanking() => new object?[]
        {
            Array.Empty<object?>(),
            Array.Empty<object?>()
        };

        static object?[] EmptyRankingWithMyRank(StoryEventStateRow row) => new object?[]
        {
            Array.Empty<object?>(),
            new object?[]
            {
                new object?[] { row.Rank, row.Point, row.UserId, 0L }
            }
        };

        app.MapMethods("/localap/api/Events/GetConcoursInformation", new[] { "GET", "POST" }, async context =>
        {
            await logger.LogAsync("events-concours-information");
            await codec.WriteApiResultAsync(context, new object?[] { Array.Empty<object?>(), 0L });
        });

        app.MapMethods("/localap/api/Events/GetEventBorderRanking", new[] { "GET", "POST" }, async context =>
        {
            var ranking = await state.UserDataService.ReadEventRankingAsync(context, 0, 100, "border");
            await codec.WriteApiResultAsync(context, new object?[] { ranking.Ranking, ranking.Profiles });
        });

        app.MapMethods("/localap/api/Events/GetEventTopRanking", new[] { "GET", "POST" }, async context =>
        {
            var ranking = await state.UserDataService.ReadEventRankingAsync(context, 0);
            await codec.WriteApiResultAsync(context, new object?[] { ranking.Ranking, ranking.Profiles });
        });

        app.MapMethods("/localap/api/Events/GetEventNearRanking", new[] { "GET", "POST" }, async context =>
        {
            var ranking = await state.UserDataService.ReadEventRankingAsync(context, 0, 11, "near");
            await codec.WriteApiResultAsync(context, new object?[] { ranking.Ranking, ranking.Profiles });
        });

        app.MapMethods("/localap/api/Events/GetEventFriendRanking", new[] { "GET", "POST" }, async context =>
        {
            var ranking = await state.UserDataService.ReadEventRankingAsync(context, 0, 100, "friend");
            await codec.WriteApiResultAsync(context, new object?[] { ranking.Ranking, ranking.Profiles });
        });

        app.MapMethods("/localap/api/Events/GetEventCircleRanking", new[] { "GET", "POST" }, async context =>
        {
            var ranking = await state.UserDataService.ReadEventRankingAsync(context, 0, 100, "circle");
            await codec.WriteApiResultAsync(context, new object?[] { ranking.Ranking, ranking.Profiles });
        });

        app.MapMethods("/localap/api/Events/GetEventCampTopRanking", new[] { "GET", "POST" }, async context =>
        {
            var ranking = await state.UserDataService.ReadEventRankingAsync(context, 0);
            await codec.WriteApiResultAsync(context, new object?[] { ranking.Ranking, ranking.Profiles });
        });

        app.MapMethods("/localap/api/Events/GetEventMyRanking", new[] { "GET", "POST" }, async context =>
        {
            var row = await state.UserDataService.ReadStoryEventStateAsync(context);
            await codec.WriteApiResultAsync(context, new object?[] { row?.Rank ?? 0, row?.Point ?? 0L, row?.UserId ?? 0L, 0L });
        });

        app.MapMethods("/localap/api/Events/GetEventCampMyRanking", new[] { "GET", "POST" }, async context =>
        {
            var row = await state.UserDataService.ReadStoryEventStateAsync(context);
            await codec.WriteApiResultAsync(context, new object?[]
            {
                Array.Empty<object?>(),
                row?.Point ?? 0L,
                row?.Point ?? 0L
            });
        });

        app.MapMethods("/localap/api/Events/GetEventCampBorderRanking", new[] { "GET", "POST" }, async context =>
        {
            var ranking = await state.UserDataService.ReadEventRankingAsync(context, 0);
            await codec.WriteApiResultAsync(context, new object?[] { ranking.Ranking, ranking.Profiles });
        });

        // 剧情活动排行接口已由 HomePlayerEvents.cs 提供，此处不重复注册

        app.MapMethods("/localap/api/Events/GetStoryEventHighScoreTopRanking/{mStoryEventId:long}", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, new object?[]
            {
                Array.Empty<object?>(), Array.Empty<object?>(), Array.Empty<object?>(), Array.Empty<object?>()
            });
        });

        app.MapMethods("/localap/api/Events/GetStoryEventCircleMissionProgresses/{mStoryEventId:long}", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, new object?[] { Array.Empty<object?>() });
        });

        // 总点数活动
        foreach (var path in new[]
        {
            "/localap/api/Events/GetTotalPointEventTopRanking",
            "/localap/api/Events/GetTotalPointEventNearRanking",
            "/localap/api/Events/GetTotalPointEventFriendRanking",
            "/localap/api/Events/GetTotalPointEventCircleRanking",
        })
        {
            app.MapMethods(path, new[] { "GET", "POST" }, async context =>
            {
                var ranking = await state.UserDataService.ReadEventRankingAsync(context, 0);
                await codec.WriteApiResultAsync(context, new object?[] { ranking.Ranking, ranking.Profiles });
            });
        }

        app.MapMethods("/localap/api/Events/GetTotalPointEventInformation", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, await state.UserDataService.ReadTotalPointEventInformationAsync(context));
        });

        // 累计 PT 奖励领取：官方界面有「受け取る」按钮。奖励已按“达到即发放”实时进背包，
        // 这里做成幂等补发，客户端点了也不会重复给（已发档位有记录）。
        foreach (var path in new[]
        {
            "/localap/api/Events/ReceiveTotalPointReward/{eventMasterId:long?}",
            "/localap/api/Events/ReceiveTotalPointEventReward/{eventMasterId:long?}",
        })
        {
            app.MapMethods(path, new[] { "GET", "POST" }, async (HttpContext context, long? eventMasterId) =>
            {
                await logger.LogAsync($"events-receive-total-point-reward eventId={eventMasterId} query={context.Request.QueryString}");
                await codec.WriteApiResultAsync(context, await state.UserDataService.ReceiveTotalPointRewardsAsync(context, eventMasterId ?? 0));
            });
        }

        // 角色点数活动
        foreach (var path in new[]
        {
            "/localap/api/Events/CharacterPoint/GetCharacterTopRanking/{characterPointEventMasterId:long}",
            "/localap/api/Events/CharacterPoint/GetCharacterNearRanking/{characterPointEventMasterId:long}",
            "/localap/api/Events/CharacterPoint/GetCharacterBorderRanking/{characterPointEventMasterId:long}",
            "/localap/api/Events/CharacterPoint/GetOverallTopRanking/{characterPointEventMasterId:long}",
        })
        {
            app.MapMethods(path, new[] { "GET", "POST" }, async context =>
            {
                var ranking = await state.UserDataService.ReadEventRankingAsync(context, 0);
                await codec.WriteApiResultAsync(context, new object?[] { ranking.Ranking, ranking.Profiles });
            });
        }

        app.MapMethods("/localap/api/Events/CharacterPoint/GetInformation/{characterPointEventMasterId:long}", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, new object?[] { Array.Empty<object?>(), 0L });
        });

        // 社团活动
        foreach (var path in new[]
        {
            "/localap/api/Events/CircleEvent/Raking/Border/{circleEventMasterId:long}",
            "/localap/api/Events/CircleEvent/Raking/Circle/{circleEventMasterId:long}",
            "/localap/api/Events/CircleEvent/Raking/Near/{circleEventMasterId:long}",
        })
        {
            app.MapMethods(path, new[] { "GET", "POST" }, async context =>
            {
                await codec.WriteApiResultAsync(context, new object?[] { Array.Empty<object?>(), 0L, 0L });
            });
        }

        app.MapMethods("/localap/api/Events/CircleEvent/Information/{circleEventMasterId:long}", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, new object?[] { Array.Empty<object?>(), 0L });
        });

        // 提示已读、营地选择（返回 true）
        foreach (var path in new[]
        {
            "/localap/api/Events/ReadSpecialEventTips",

            "/localap/api/Events/SelectCamp",
            "/localap/api/Events/CharacterPoint/ReadTips/{characterPointEventMasterId:long}",
            "/localap/api/Events/TrialPartyEvent/EditParty",
            "/localap/api/Events/GrowStoryEventHighScoreBuffSetting/{mStoryEventId:long}/{mStoryEventHighScoreBuffSettingId:long}/{levelTo:int}",
        })
        {
            app.MapMethods(path, new[] { "GET", "POST" }, async context =>
            {
                await codec.WriteApiResultAsync(context, new object?[] { true });
            });
        }

        app.MapMethods("/localap/api/Events/TournamentQualifying/Information/{tournamentQualifyingMasterId:long}", new[] { "GET", "POST" }, async context =>
        {
            await codec.WriteApiResultAsync(context, new object?[] { Array.Empty<object?>(), 0L, 0L });
        });

    }
}
