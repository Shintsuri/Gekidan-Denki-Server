using System.Text;
using System.Text.Json;
using Npgsql;

sealed partial class UserDataService
{
    const long DefaultHomeSkinMasterId = 200001L;
    const long DefaultNameBaseColorMasterId = 180001L;
    const long DefaultNameColorMasterId = 1L;
    const long DefaultIconFrameMasterId = 190001L;
    const long DefaultCharacterBaseMasterId = 101L;
    const long DefaultCharacterMasterId = 110010L;
    const long DefaultCharacterBaseId = 1L;
    const long DefaultCharacterId = 1L;
    const long DefaultCostumeId = 1L;
    const long DefaultCostumeMasterId = 11L;
    const long DefaultHomeBgmMasterId = 1L;
    const long DefaultHomeBgmDetailMasterId = 1001L;
    const long DefaultPartyId = 1L;
    const long DefaultPartySlotId = 1L;
    readonly PostgresDatabase database;
    readonly string? gachaResourcesRoot;

    public UserDataService(PostgresDatabase database, LocalConfig? config = null)
    {
        this.database = database;
        gachaResourcesRoot = config?.ResourcesRoot;
    }

    public async Task<object?[]> GetUserDataAsync(HttpContext context)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null)
        {
            return GameResults.UserData();
        }

        await EnsureDefaultUserDataAsync(userId.Value, context.RequestServices?.GetService<ILogger<UserDataService>>());
        return await ReadUserDataAsync(userId.Value);
    }

    public async Task<object?[]> GetCurrentUserResultAsync(HttpContext context)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null) return new object?[] { GameResults.UserData()[0] };

        await EnsureDefaultUserDataAsync(userId.Value);
        await using var connection = await database.OpenConnectionAsync();
        return new object?[] { await ReadUserAsync(connection, userId.Value) };
    }

    public async Task<object?[]> GetLoginPresentDataAsync(HttpContext context)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null) return new object?[] { GameResults.UserData()[0] };

        await EnsureDefaultUserDataAsync(userId.Value, context.RequestServices?.GetService<ILogger<UserDataService>>());
        await using var connection = await database.OpenConnectionAsync();
        // 对齐 acchan 的 Login 响应：只返回 key 0（用户）+ key 66
        var presentData = new List<object?>();
        presentData.Add(DataObject(0, await ReadUserAsync(connection, userId.Value)));
        presentData.Add(DataObject(66, new object?[] { 8, 8, true }));
        return presentData.ToArray();
    }

    public Task UpdateCapedPlayerRankAnnounceAsync(HttpContext context)
    {
        return Task.CompletedTask;
    }

    static string NormalizeNotificationBody(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return "[]";
        }

        if (trimmed.StartsWith('['))
        {
            return body;
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Serialize(new[] { document.RootElement })
                    : "[]";
            }
            catch (JsonException)
            {
                // Fall through and treat malformed JSON as plain notification text.
            }
        }

        return JsonSerializer.Serialize(new[]
        {
            new NotificationBodyContent(4, body)
        });
    }

    readonly record struct NotificationBodyContent(int Element, string Data);

    public static async Task InsertDefaultUserDataAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, string name)
    {
        var characterBaseId = UserScopedId(userId, DefaultCharacterBaseId);
        var characterId = UserScopedId(userId, DefaultCharacterId);
        var costumeId = UserScopedId(userId, DefaultCostumeId);

        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_game_states (
              "userId", stamina, max_stamina, free_jewel, paid_jewel, coin,
              tutorial_status, is_tutorial_finished
            )
            values ($1, 200, 50, 0, 0, 0, 0, false)
            on conflict ("userId") do nothing
            """,
            userId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_profiles (
              "userId",
              display_name,
              comment,
              favorite_character_master_id,
              home_character_master_id,
              home_skin_master_id,
              name_base_color_master_id,
              name_color_master_id,
              icon_frame_master_id,
              is_public,
              accepts_friend_requests,
              show_home_character
            )
            values ($1, $2, 'よろしくお願いします。', 110010, 110010, 200001, 180001, 1, 190001, false, false, false)
            on conflict ("userId") do update set
              name_base_color_master_id = case
                when user_profiles.name_base_color_master_id <= 0 then 180001
                else user_profiles.name_base_color_master_id
              end,
              name_color_master_id = case
                when user_profiles.name_color_master_id <= 0 then 1
                else user_profiles.name_color_master_id
              end,
              icon_frame_master_id = case
                when coalesce(user_profiles.icon_frame_master_id, 0) <= 0 then 190001
                else user_profiles.icon_frame_master_id
              end
            """,
            userId,
            name);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_home_display_preferences (
              "userId",
              character_base_master_id,
              sub_character_base_master_id_1,
              sub_character_base_master_id_2,
              sub_character_base_master_id_3,
              costume_master_id,
              sub_costume_master_id_1,
              sub_costume_master_id_2,
              sub_costume_master_id_3,
              character_master_id,
              login_bonus_character_base_master_id,
              login_bonus_costume_master_id
            )
            values ($1, 101, 101, 101, 101, 11, 11, 11, 11, 110010, 101, 11)
            on conflict ("userId") do nothing
            """,
            userId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_character_bases (
              id,
              "userId",
              character_base_master_id,
              costume_master_id,
              selected_character_id
            )
            values ($1, $2, 101, 11, $3)
            on conflict (id) do nothing
            """,
            characterBaseId,
            userId,
            characterId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_character_cards (
              id,
              "userId",
              character_master_id,
              character_base_id,
              skill_level
            )
            values ($1, $2, 110010, $3, 1)
            on conflict (id) do nothing
            """,
            characterId,
            userId,
            characterBaseId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_character_costumes (id, "userId", costume_master_id)
            values ($1, $2, 11)
            on conflict (id) do nothing
            """,
            costumeId,
            userId);

        for (var i = 1; i < BootstrapCharacterBaseMasterIds.Length; i++)
        {
            var baseMasterId = BootstrapCharacterBaseMasterIds[i];
            var cardMasterId = BootstrapCharacterMasterIds[i];
            var defaultCostumeId = BootstrapCostumeMasterIds[i];
            var baseId = UserScopedId(userId, 2001 + i);
            var cardId = UserScopedId(userId, 1001 + i);
            await ExecuteAsync(connection, transaction,
                """
                insert into user_character_bases
                  (id, "userId", character_base_master_id, costume_master_id, selected_character_id)
                values ($1, $2, $3, $4, $5)
                on conflict ("userId", character_base_master_id) do nothing
                """,
                baseId, userId, baseMasterId, defaultCostumeId, cardId);
            await using var findBase = new NpgsqlCommand(
                """
                select id from user_character_bases
                where "userId" = $1 and character_base_master_id = $2
                """, connection, transaction);
            findBase.Parameters.AddWithValue(userId);
            findBase.Parameters.AddWithValue(baseMasterId);
            var persistedBaseId = (long)(await findBase.ExecuteScalarAsync()
                ?? throw new InvalidOperationException($"角色基础 {baseMasterId} 保存后未找到"));
            await ExecuteAsync(connection, transaction,
                """
                insert into user_character_cards
                  (id, "userId", character_master_id, character_base_id)
                values ($1, $2, $3, $4)
                on conflict ("userId", character_master_id) do nothing
                """,
                cardId, userId, cardMasterId, persistedBaseId);
            await ExecuteAsync(connection, transaction,
                """
                insert into user_character_costumes (id, "userId", costume_master_id)
                values ($1, $2, $3)
                on conflict ("userId", costume_master_id) do nothing
                """,
                UserScopedId(userId, 6001 + i), userId, defaultCostumeId);
            await ExecuteAsync(connection, transaction,
                """
                update user_character_bases as character_base
                set selected_character_id = character_card.id
                from user_character_cards as character_card
                where character_base."userId" = $1
                  and character_base.character_base_master_id = $2
                  and character_card."userId" = $1
                  and character_card.character_master_id = $3
                  and character_card.character_base_id = character_base.id
                  and not exists (
                    select 1 from user_character_cards selected
                    where selected.id = character_base.selected_character_id
                      and selected."userId" = character_base."userId"
                      and selected.character_base_id = character_base.id
                  )
                """,
                userId, baseMasterId, cardMasterId);
        }
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_item_currencies ("userId", free_jewel, paid_jewel, coin)
            values ($1, 0, 0, 0)
            on conflict ("userId") do nothing
            """,
            userId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_home_bgms ("userId", home_bgm_master_id, selection_type, home_bgm_detail_master_id)
            values ($1, 1, 1, 1001)
            on conflict ("userId") do nothing
            """,
            userId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_home_skins ("userId", selected_home_skin_master_id)
            values ($1, 200001)
            on conflict ("userId") do nothing
            """,
            userId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_home_skin_possessions ("userId", home_skin_master_id)
            values ($1, 200001)
            on conflict ("userId", home_skin_master_id) do nothing
            """,
            userId);

        var selectedPartyId = UserScopedId(userId, 4001);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_player_preferences ("userId", selected_party_id)
            values ($1, $2)
            on conflict ("userId") do nothing
            """,
            userId,
            selectedPartyId);

        for (var i = 0; i < BootstrapCharacterBaseMasterIds.Length; i++)
        {
            var characterBaseMasterId = BootstrapCharacterBaseMasterIds[i];
            await ExecuteAsync(
                connection,
                transaction,
                """
                insert into user_character_resource_progress (
                  id, "userId", character_base_master_id, resource_type, resource_master_id,
                  level, exp, rank, progress
                )
                values ($1, $2, $3, 3, 10201, 1, 0, 0, 0)
                on conflict ("userId", character_base_master_id, resource_type) do nothing
                """,
                UserScopedId(userId, 3000 + (i * 2)),
                userId,
                characterBaseMasterId);

            if (characterBaseMasterId == 401) continue;
            await ExecuteAsync(
                connection,
                transaction,
                """
                insert into user_character_resource_progress (
                  id, "userId", character_base_master_id, resource_type, resource_master_id,
                  level, exp, rank, progress
                )
                values ($1, $2, $3, 5, 10205, 1, 0, 0, 0)
                on conflict ("userId", character_base_master_id, resource_type) do nothing
                """,
                UserScopedId(userId, 3001 + (i * 2)),
                userId,
                characterBaseMasterId);
        }

        for (var i = 0; i < 12; i++)
        {
            var partyId = UserScopedId(userId, 4000 + i + 1);
            await ExecuteAsync(
                connection,
                transaction,
                """
                insert into user_party_groups (id, "userId", display_index, name, party_type)
                values ($1, $2, $3, $4, 1)
                on conflict (id) do nothing
                """,
                partyId,
                userId,
                i,
                $"グループ{i + 1}");

            for (var slot = 1; slot <= 5; slot++)
            {
                var characterIndex = slot switch
                {
                    1 => 0,
                    2 => 2,
                    3 => 3,
                    4 => 4,
                    _ => 5
                };
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    insert into user_party_slots (
                      id, "userId", party_id, slot_number, character_card_id, accessory_id, poster_id, position
                    )
                    values ($1, $2, $3, $4, $5, null, null, 0)
                    on conflict (id) do nothing
                    """,
                    UserScopedId(userId, 5000 + (i * 10) + slot),
                    userId,
                    partyId,
                    slot,
                    UserScopedId(userId, 1000 + characterIndex + 1));
            }
        }

        for (var i = 0; i < BootstrapMissionStates.Length; i++)
        {
            var state = BootstrapMissionStates[i];
            await ExecuteAsync(
                connection,
                transaction,
                """
                insert into user_mission_statuses (
                  id, "userId", is_cleared, is_received, progress, completed_at, mission_from, mission_to
                )
                values ($1, $2, $3, false, $4, null, $5, $6)
                on conflict ("userId", mission_from, mission_to) do nothing
                """,
                UserScopedId(userId, 8001 + i),
                userId,
                state.Cleared,
                state.Progress,
                state.From,
                state.To);
        }
        await ExecuteAsync(connection, transaction,
            """
            update user_mission_statuses
            set is_cleared = false, is_received = false, progress = 0,
                completed_at = null, updated_at = now()
            where "userId" = $1
              and mission_from between 100000 and 100499
              and updated_at < $2
            """,
            userId, CurrentDailyResetBoundaryUtc(DateTimeOffset.UtcNow).UtcDateTime);

        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_live_user_stats ("userId", score_rating, technical_rating)
            values ($1, 0, 0)
            on conflict ("userId") do nothing
            """,
            userId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_player_settings (id, "userId", display_quality, download_category, is_push_enabled, is_live_push_enabled)
            values ($1, $2, 0, 36, false, false)
            on conflict ("userId") do nothing
            """,
            UserScopedId(userId, 9701),
            userId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_restriction_states ("userId", restricted_until, reason)
            values ($1, null, null)
            on conflict ("userId") do nothing
            """,
            userId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_invite_states ("userId", invited_count, invite_code, is_reward_received)
            values ($1, 0, $2, false)
            on conflict ("userId") do nothing
            """,
            userId,
            $"LOCAL{userId % 100000:D5}");
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_login_bonus_states (id, "userId", current_count, total_count, shown_at, status)
            values ($1, $2, 0, 0, now(), 0)
            on conflict ("userId") do nothing
            """,
            UserScopedId(userId, 10901),
            userId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_daily_limits (id, "userId", auto_play_times, daily_lesson_times, last_refreshed_at, music_course_free_challenge_times)
            values ($1, $2, 0, 0, $3, 0)
            on conflict ("userId") do nothing
            """,
            UserScopedId(userId, 10902),
            userId,
            CurrentDailyResetBoundaryUtc(DateTimeOffset.UtcNow).UtcDateTime);
        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into user_notification_states ("userId", notification_read_at, notice_read_at, present_read_at)
            values ($1, null, null, null)
            on conflict ("userId") do nothing
            """,
            userId);
        for (var i = 1; i <= 5; i++)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                insert into user_album_groups ("userId", album_index, name, display_order)
                values ($1, $2, $3, $2)
                on conflict ("userId", album_index) do nothing
                """,
                userId,
                i,
                $"アルバム{i}");
        }

        // 官方全新账号的礼品箱是空的（acchan 实测 CheckPackagesAsync 只返回 6 字节空列表）。
        // 之前这里默认塞 38 个礼包，导致每次登录客户端都要走一遍"领 38 个礼包"的流程。
        // 需要复现旧行为时设 WDS_BOOTSTRAP_INBOX=1。
        if (System.Environment.GetEnvironmentVariable("WDS_BOOTSTRAP_INBOX") == "1")
        for (var i = 0; i < BootstrapInboxRewards.Length; i++)
        {
            var reward = BootstrapInboxRewards[i];
            var bootstrapKey = $"registration-{i + 1}";
            var payload = JsonSerializer.Serialize(new
            {
                bootstrapKey,
                reward.ThingType,
                reward.MasterId,
                reward.Quantity,
                reward.Message
            });
            await ExecuteAsync(
                connection,
                transaction,
                """
                insert into user_item_inbox_packages ("userId", payload, expires_at)
                select $1, $2::jsonb, now() + interval '30 days'
                where not exists (
                  select 1
                  from user_item_inbox_packages
                  where "userId" = $1
                    and payload ->> 'bootstrapKey' = $3
                )
                """,
                userId,
                payload,
                bootstrapKey);
        }
    }

    // 每个用户在本进程内只需「确保默认数据」一次。
    // 之前每个接口都会跑一遍（开事务 + 6 个重例程 + 遍历全部歌曲/海报），
    // 并发一高就把连接池（Maximum Pool Size=60）抽干，进而线程池饥饿，整个服务假死。
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> EnsuredUsers = new();

    async Task EnsureDefaultUserDataAsync(long userId, ILogger? logger = null)
    {
        if (EnsuredUsers.ContainsKey(userId)) return;
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var name = await ReadUserNameAsync(connection, transaction, userId) ?? "LocalPlayer";
            await InsertDefaultUserDataAsync(connection, transaction, userId, name);
            await EnsureMissionPassAsync(connection, transaction, userId);
            await NormalizeRankAsync(connection, transaction, userId);
            await NormalizeCharacterGrowthAsync(connection, transaction, userId);
            await SynchronizeMusicUnlocksAsync(connection, transaction, userId);
            var posterCostumes = await SynchronizePosterCostumesAsync(connection, transaction, userId);
            await transaction.CommitAsync();
            EnsuredUsers[userId] = 1;
            if (posterCostumes > 0)
                logger?.LogInformation("补发海报服装 operation=poster-costume-reconcile userId={UserId} count={Count} outcome=success", userId, posterCostumes);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    async Task UpdateUserGameStateTimestampAsync(HttpContext context, string columnName)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null) return;

        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            update user_game_states
            set {columnName} = now(),
                updated_at = now()
            where "userId" = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId.Value);
        await command.ExecuteNonQueryAsync();
    }

    async Task<object?[]> ReadUserDataAsync(long userId)
    {
        await using var connection = await database.OpenConnectionAsync();
        var user = await ReadUserAsync(connection, userId);
        var profile = await ReadProfileAsync(connection, userId);
        var homeDisplayPreference = await ReadHomeDisplayPreferenceAsync(connection, userId);
        var characters = await ReadCharactersAsync(connection, userId);
        var characterBases = await ReadCharacterBasesAsync(connection, userId);
        var costumes = await ReadCostumesAsync(connection, userId);
        var currency = await ReadCurrencyAsync(connection, userId);
        var homeBgm = await ReadHomeBgmAsync(connection, userId);
        var cosmetics = await ReadCosmeticListsAsync(connection, userId);
        var activeEvents = await ReadActiveEventStatesAsync(connection, userId);
        var loginPasses = await ReadLoginPassStatusAsync(connection, userId);
        var trophies = await ReadTrophiesAsync(connection, userId);
        var posters = await ReadPostersAsync(connection, userId);
        var accessories = await ReadAccessoriesAsync(connection, userId);
        var homeSkin = await ReadHomeSkinAsync(connection, userId);
        var userPreference = await ReadUserPreferenceAsync(connection, userId);
        var itemPossessions = await ReadItemPossessionsAsync(connection, userId);
        var livePlayResults = await ReadLivePlayResultsAsync(connection, userId);
        var anotherNotationResults = await ReadAnotherNotationResultsAsync(connection, userId);
        var liveMusicStates = await ReadLiveMusicStatesAsync(connection, userId);
        var dugongRunStates = await ReadDugongRunStatesAsync(connection, userId);
        var parties = await ReadPartiesAsync(connection, userId);
        var partySlots = await ReadPartySlotsAsync(connection, userId);
        var lessonParties = await ReadLessonPartiesAsync(connection, userId);
        var notification = await ReadNotificationAsync(connection, userId);
        await using (var cleanupCommand = new NpgsqlCommand(
            """
            delete from user_item_inbox_packages
            where "userId" = $1
              and (
                payload ->> 'bootstrapKey' like 'login-bonus-20260928-%'
                or payload ->> 'bootstrapKey' like 'check-package-%'
              )
            """,
            connection))
        {
            cleanupCommand.Parameters.AddWithValue(userId);
            await cleanupCommand.ExecuteNonQueryAsync();
        }
        var inboxPackages = await ReadInboxPackagesAsync(connection, userId);
        var characterResourceProgress = await ReadCharacterMissionsAsync(connection, userId);
        var missions = await ReadMissionsAsync(connection, userId);
        var musicBookmarks = await ReadMusicBookmarksAsync(connection, userId);
        var gameHintReads = await ReadGameHintReadsAsync(connection, userId);
        var shopLastViews = await ReadShopLastViewsAsync(connection, userId);
        var episodeReadStates = await ReadEpisodeReadStatesAsync(connection, userId);
        var missionPass = await ReadMissionPassAsync(connection, userId);
        var restriction = await ReadRestrictionAsync(connection, userId);
        var albums = await ReadAlbumsAsync(connection, userId);
        var photoRecords = await ReadPhotoRecordsAsync(connection, userId);
        var photoStates = await ReadPhotoStatesAsync(connection, userId);
        var photoAlbumArrangements = await ReadPhotoAlbumArrangementsAsync(connection, userId);
        var musicVideoWatchStates = await ReadMusicVideoWatchStatesAsync(connection, userId);
        var dailyLimit = await ReadDailyLimitAsync(connection, userId);
        var inviteState = await ReadInviteStateAsync(connection, userId);

        var result = new List<object?>
        {
            await ReadGrowthBonusAsync(connection, userId),
            DataObject(0, user),
            DataObject(1, profile),
            DataObject(2, userPreference),
            DataObject(3, homeDisplayPreference)
        };
        result.AddRange(characters.Select(value => DataObject(4, value)));
        result.AddRange(characterBases.Select(value => DataObject(5, value)));
        result.AddRange(parties.Select(value => DataObject(6, value)));
        result.AddRange(partySlots.Select(value => DataObject(7, value)));
        result.AddRange(lessonParties.Select(value => DataObject(39, value)));
        result.AddRange(costumes.Select(value => DataObject(43, value)));
        result.AddRange(itemPossessions.Select(value => DataObject(27, value)));
        result.AddRange(livePlayResults.Select(value => DataObject(24, value)));
        result.AddRange(anotherNotationResults.Select(value => DataObject(AnotherNotationUnionKey, value)));
        result.AddRange((await ReadAuditionClearsAsync(connection, userId)).Select(value => DataObject(90, value)));
        result.AddRange(liveMusicStates.Select(value => DataObject(25, value)));
        result.Add(DataObject(130,await ReadLiveAchievementAsync(connection,userId)));
        result.AddRange(dugongRunStates.Select(value => DataObject(177, value)));
        result.AddRange(characterResourceProgress.Select(value => DataObject(96, value)));
        result.Add(DataObject(94, notification));
        result.Add(DataObject(97, MissionPassData(userId, missionPass,
            (await GameOperationsService.ReadAsync(connection)).Configuration.MissionPass)));
        result.AddRange(missions.Select(value => DataObject(48, value)));
        result.Add(DataObject(109, dailyLimit));
        result.Add(DataObject(128, currency));
        result.AddRange(musicBookmarks.Select(value => DataObject(146, value)));
        result.AddRange(gameHintReads.Select(value => DataObject(66, value)));
        result.AddRange(shopLastViews.Select(value => DataObject(65, value)));
        result.AddRange(episodeReadStates.Select(value => DataObject(95, value)));
        // 称号（名刺）：union 45 = 玩家已拥有的称号
        result.AddRange((await ReadNameplatesAsync(userId)).Select(value => DataObject(45, value)));
        // 称号图鉴主数据：union 58 = NameplateMaster
        // 58 不需要：客户端图鉴来自本地主数据 NameplateMaster
        result.Add(DataObject(148, restriction));
        // 实验：官方新账号会带这三项种子数据（66 提示已读 / 122 教程通关奖励 / 124 相片状态），
        // 我们之前完全没有，客户端主页初始化可能依赖它们。
        if (System.Environment.GetEnvironmentVariable("WDS_EXTRA_UNION_KEYS") == "1")
        {
            foreach (var hint in new[] { 1, 21, 29, 8 })
                result.Add(DataObject(66, new object?[] { hint, hint, true }));
            result.Add(DataObject(122, new object?[] { 1, 1, DateTimeOffset.UtcNow.UtcDateTime }));
            result.Add(DataObject(124, new object?[] { 1, 1, 1, 1 }));
        }
        result.Add(DataObject(161, homeBgm));
        // 批量简单状态（无数据的发空列表，让客户端 UI 正常初始化）
        var marketState = await ReadMarketStateAsync(connection, userId);
        result.Add(DataObject(64, marketState));
        result.Add(DataObject(42, new object?[] { userId, Array.Empty<object?>() }));
        result.Add(DataObject(46, new object?[] { userId, Array.Empty<object?>() }));
        // 47 Stamp：StampMasterIds 是 long[]，给出全部表情
        {
            var allStampIds = new long[]
            {
                10101, 10102, 10103, 10104, 10105, 10106, 10107, 10108, 10109, 10110,
                10111, 10112, 10170, 10180, 10181, 10182, 10183, 10191, 10192, 10201,
                10202, 10203, 10204, 10205, 10206, 10207, 10208, 10209, 10210, 10211,
                10212, 10280, 10281, 10282, 10283, 10284, 10291, 10292, 10301, 10302,
                10303, 10304, 10305, 10306, 10307, 10308, 10309, 10310, 10311, 10380,
                10381, 10382, 10383, 10391, 10392, 10401, 10402, 10403, 10404, 10405,
                10406, 10407, 10408, 10409, 10410, 10411, 10480, 10481, 10482, 10483,
                10484, 10491, 10492, 10501, 10502, 10503, 10504, 10505, 10506, 10507,
                10508, 10509, 10581, 10582, 10583, 10584, 10591, 10592, 10601, 10602,
                10603, 10604, 10605, 10606, 10607, 10608, 10610, 10681, 10682, 10683,
                10684, 10691, 10692, 20101, 20102, 20103, 20104, 20105, 20106, 20107,
                20108, 20109, 20110, 20111, 20181, 20182, 20183, 20184, 20185, 20191,
                20192, 20201, 20202, 20203, 20204, 20205, 20206, 20207, 20208, 20209,
                20281, 20282, 20283, 20284, 20291, 20292, 20301, 20302, 20303, 20304,
                20305, 20306, 20307, 20308, 20309, 20381, 20382, 20383, 20384, 20391,
                20392, 20401, 20402, 20403, 20404, 20405, 20406, 20407, 20408, 20409,
                20410, 20481, 20482, 20483, 20484, 20491, 20492, 20501, 20502, 20503,
                20504, 20505, 20506, 20507, 20508, 20509, 20510, 20580, 20581, 20582,
                20583, 20584, 20591, 20592, 30101, 30102, 30103, 30104, 30105, 30106,
                30107, 30108, 30109, 30110, 30111, 30181, 30182, 30183, 30184, 30191,
                30192, 30201, 30202, 30203, 30204, 30205, 30206, 30207, 30208, 30209,
                30210, 30281, 30282, 30283, 30291, 30292, 30301, 30302, 30303, 30304,
                30305, 30306, 30307, 30308, 30309, 30310, 30381, 30382, 30383, 30391,
                30392, 30401, 30402, 30403, 30404, 30405, 30406, 30407, 30408, 30409,
                30410, 30481, 30482, 30483, 30491, 30492, 30501, 30502, 30503, 30504,
                30505, 30506, 30507, 30508, 30509, 30510, 30581, 30582, 30583, 30591,
                30592, 40101, 40102, 40103, 40104, 40105, 40106, 40107, 40108, 40109,
                40110, 40111, 40112, 40180, 40181, 40182, 40183, 40184, 40185, 40191,
                40192, 40201, 40202, 40203, 40204, 40205, 40206, 40207, 40208, 40209,
                40210, 40280, 40281, 40282, 40283, 40291, 40292, 40301, 40302, 40303,
                40304, 40305, 40306, 40307, 40308, 40309, 40310, 40380, 40381, 40382,
                40383, 40384, 40391, 40392, 40401, 40402, 40403, 40404, 40405, 40406,
                40407, 40408, 40409, 40410, 40411, 40480, 40481, 40482, 40483, 40484,
                40491, 40492, 40501, 40502, 40503, 40504, 40505, 40506, 40507, 40508,
                40509, 40580, 40581, 40582, 40583, 40584, 40591, 40592, 90101, 90102,
                90201, 90401, 90601, 90901,
            };
            result.Add(DataObject(47, new object?[] { userId, allStampIds, Array.Empty<long>() }));
        }
        result.Add(DataObject(102, Array.Empty<object?>()));
        result.Add(DataObject(119, Array.Empty<object?>()));
        result.Add(DataObject(129, Array.Empty<object?>()));
        result.Add(DataObject(139, Array.Empty<object?>()));
        result.Add(DataObject(162, Array.Empty<object?>()));
        result.Add(DataObject(180, Array.Empty<object?>()));
        result.Add(DataObject(188, Array.Empty<object?>()));
        result.Add(DataObject(192, Array.Empty<object?>()));
        result.AddRange((await ReadAnotherNotationsAsync(connection, userId)).Select(v => DataObject(145, v)));
        result.AddRange((await ReadFavoriteCostumesAsync(connection, userId)).Select(v => DataObject(191, v)));
        // 活动参与状态：156 Event / 101 StoryEvent / 140 TotalPointEvent / 144 CharacterPointEvent
        foreach (var row in activeEvents) result.Add(DataObject((int)row[0], (object?[])row[1]!));
        // 108 宝石商店购买状态：[id, jewelShopItemMasterId, purchaseCount, totalPurchaseCount, repurchaseDate]
        foreach (var row in await ReadJewelShopStatesAsync(connection, userId))
            result.Add(DataObject(108, row));
        // 40 每日课程：[id, timesLeft, autoPlayTimes, dailyLessonTimes, lastRefreshedAt, musicCourseFreeChallengeTimes]
        result.Add(DataObject(40, new object?[] { userId, 3, 0, 0, DateTime.UtcNow, 1 }));
        // 190 饰品自动出售：[id, autoSellRarity]
        result.Add(DataObject(190, new object?[] { userId, 0 }));
        // 138 各种上限：[id, 照片上限, 提升次数, 饰品上限, 提升次数]
        result.Add(DataObject(138, new object?[] { userId, 0, 0, 0, 0 }));
        // 外观收集：44 名字颜色 / 181 名字底色 / 182 头像框
        // 44 NameColor：NameColorMasterIds 是 long[]，直接给全部 18 个颜色
        result.Add(DataObject(44, new object?[] { userId, new long[] { 1, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117 } }));
        result.Add(DataObject(181, new object?[] { userId, cosmetics.BaseColors }));
        result.Add(DataObject(182, new object?[] { userId, cosmetics.IconFrames }));
        // 月卡状态：2001 走 127(LoginPassStatus)，3003/3004 走 126(StarPassStatus)
        result.AddRange(loginPasses.Where(p => (int)p[2] == 0).Select(p => DataObject(127, new object?[] { p[0], p[1] })));
        result.AddRange(loginPasses.Where(p => (int)p[2] != 0).Select(p => DataObject(126, new object?[] { p[0], p[1] })));
        Console.WriteLine($"[union] userId={userId} trophies={trophies.Count} loginPasses={loginPasses.Count}");
        result.AddRange(trophies.Select(value => DataObject(63, value)));
        result.AddRange(posters.Select(value => DataObject(11, value)));
        result.AddRange(accessories.Select(value => DataObject(26, value)));
        result.AddRange(photoRecords.Select(value => DataObject(123, value)));
        result.AddRange(photoStates.Select(value => DataObject(124, value)));
        result.AddRange(photoAlbumArrangements.Select(value => DataObject(125, value)));
        result.AddRange(albums.Select(value => DataObject(174, value)));
        result.AddRange(musicVideoWatchStates.Select(value => DataObject(193, value)));
        result.Add(DataObject(179, inviteState));
        result.Add(DataObject(189, homeSkin));
        result.AddRange(inboxPackages.Select(value => DataObject(41, value)));
        result.AddRange(await ReadPermanentMarketDataAsync(connection,userId));
        result.AddRange((await ReadShopPurchaseStatesAsync(connection, userId)).Select(row => DataObject(108, row)));
        await using (var recovery = new NpgsqlCommand("select exists(select 1 from user_account_recovery where \"userId\"=$1)", connection))
        {
            recovery.Parameters.AddWithValue(userId);
            if (await recovery.ExecuteScalarAsync() is true) result.Add(DataObject(120,new object?[]{userId}));
        }
        AddOfficialBootstrapData(result, userId);

        // 实验开关：只保留指定的 union key，用来对比官方账号实际下发的 key 集合。
        // 官方新号（acchan uid 115）只发 23 个 key：0,1,2,3,4,5,6,7,25,41,48,66,67,94,96,97,109,122,124,128,148,174,179
        // 我们额外发了 27,39,43,65,90,95,108,120,123,125,130,146,161,166,167,177,189,193,24 等
        var allowedKeys = System.Environment.GetEnvironmentVariable("WDS_UNION_KEYS")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var parsed) ? parsed : (int?)null)
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!.Value)
            .ToHashSet();
        if (allowedKeys is { Count: > 0 })
        {
            result = result
                .Where(item => item is not object?[] { Length: >= 2 } dataObject
                    || dataObject[0] is not int key
                    || allowedKeys.Contains(key))
                .ToList();
        }

        return BuildOfficialUserDataBatches(result, userId);
    }

    static object?[] BuildOfficialUserDataBatches(List<object?> source, long userId)
    {
        // The official /api/data/user payload is split into logical batches with
        // nil entries between them.  These nils are significant to the client-side
        // data synchronizer; returning one flat list can leave the Home bootstrap
        // task waiting even though the HTTP request itself completed successfully.
        // Within each batch the union keys are also ordered.  The generated
        // bootstrap data is deliberately added piecemeal, so preserving source
        // order here produces a valid MessagePack document but not the stream
        // expected by the client's data synchronizer.
        int[] inventoryTypeOrder = { 28, 120, 4, 5, 96, 43, 45, 101, 177, 179, 139, 183, 94, 150, 41, 27, 100, 111, 106, 107, 168, 171, 170, 176, 39, 24, 25, 90, 147, 109 };
        int[] missionTypeOrder = { 145, 146, 97, 48, 122, 154, 193, 6, 7, 166, 167, 123, 124, 125, 131, 174, 0, 1, 3, 2, 66, 67, 128, 138, 148, 40 };
        int[] accountTypeOrder = { 156, 140, 144, 82, 47, 44, 11, 65, 64, 102, 129, 181, 182, 189, 161, 63, 126, 127, 42, 46, 119, 162, 180, 188, 190, 191, 192 };
        int[] shopTypeOrder = { 108, 149, 95 };

        static int? UnionKey(object? item)
        {
            return item is object?[] { Length: >= 2 } dataObject && dataObject[0] is int key
                ? key
                : null;
        }

        static object?[]? UnionValue(object? item)
        {
            return item is object?[] { Length: >= 2 } dataObject
                ? dataObject[1] as object?[]
                : null;
        }

        static long? NumericId(object? value)
        {
            return value switch
            {
                byte number => number,
                short number => number,
                int number => number,
                long number => number,
                uint number => number,
                ulong number when number <= long.MaxValue => (long)number,
                _ => null
            };
        }

        var result = new List<object?>();
        var bootstrapPartyIds = Enumerable.Range(0, 12)
            .Select(index => UserScopedId(userId, 4001 + index))
            .ToHashSet();

        IEnumerable<object?> ItemsOfType(int unionKey)
        {
            var items = source.Where(item => UnionKey(item) == unionKey);

            // The database seed contributes one legacy "Party 1" row in
            // addition to the twelve official-style groups.  Sending all
            // thirteen leaves the party cache inconsistent with its members.
            if (unionKey == 6)
            {
                items = items.Where(item =>
                {
                    var value = UnionValue(item);
                    return value is { Length: > 0 } &&
                           NumericId(value[0]) is long partyId &&
                           bootstrapPartyIds.Contains(partyId);
                });
            }
            else if (unionKey == 7)
            {
                items = items.Where(item =>
                {
                    var value = UnionValue(item);
                    return value is { Length: > 1 } &&
                           NumericId(value[1]) is long partyId &&
                           bootstrapPartyIds.Contains(partyId);
                });
            }

            return items;
        }

        void AddTypesInOrder(IEnumerable<int> unionKeys)
        {
            foreach (var unionKey in unionKeys)
            {
                result.AddRange(ItemsOfType(unionKey));
            }
        }

        // 2.31.x 会把饰品对象作为独立的首批数据发送；没有饰品时该批为空。
        AddTypesInOrder(new[] { 26 });
        result.Add(null);

        AddTypesInOrder(inventoryTypeOrder);
        result.Add(null);
        AddTypesInOrder(missionTypeOrder);
        result.Add(null);

        AddTypesInOrder(accountTypeOrder);
        result.Add(null);

        AddTypesInOrder(shopTypeOrder);

        return result.ToArray();
    }

    static void AddOfficialBootstrapData(List<object?> result, long userId)
    {
        // 数据库存档优先；预设角色只补缺，默认队伍必须引用实际同步的卡片 ID。
        var ownedCards = result.OfType<object?[]>()
            .Where(item => item[0] is 4).Select(item => (object?[])item[1]!)
            .ToDictionary(value => Convert.ToInt64(value[1]));
        var ownedBases = result.OfType<object?[]>()
            .Where(item => item[0] is 5).Select(item => (object?[])item[1]!)
            .ToDictionary(value => Convert.ToInt64(value[1]));
        var bootstrapPartyCharacters = new long[BootstrapCharacterMasterIds.Length];
        var existing = new HashSet<string>();
        foreach (var item in result.OfType<object?[]>())
        {
            if (item.Length < 2 || item[0] is not int unionKey || item[1] is not object?[] value || value.Length == 0) continue;
            existing.Add(DataObjectIdentity(unionKey, value));
        }

        void AddIfMissing(int unionKey, object?[] value)
        {
            if (existing.Add(DataObjectIdentity(unionKey, value)))
            {
                result.Add(DataObject(unionKey, value));
            }
        }

        for (var i = 0; i < BootstrapCharacterBaseMasterIds.Length; i++)
        {
            var characterBaseMasterId = BootstrapCharacterBaseMasterIds[i];
            var characterMasterId = BootstrapCharacterMasterIds[i];
            var costumeMasterId = BootstrapCostumeMasterIds[i];
            var hasCard = ownedCards.TryGetValue(characterMasterId, out var ownedCard);
            var hasBase = ownedBases.TryGetValue(characterBaseMasterId, out var ownedBase);
            var characterId = hasCard ? Convert.ToInt64(ownedCard![0]) : UserScopedId(userId, 1000 + i + 1);
            var characterBaseId = hasBase ? Convert.ToInt64(ownedBase![0]) : UserScopedId(userId, 2000 + i + 1);
            bootstrapPartyCharacters[i] = characterId;

            if (!hasCard) AddIfMissing(4, new object?[]
            {
                characterId,
                characterMasterId,
                1,
                0,
                0,
                0,
                characterBaseId,
                1,
                0,
                0,
                false,
                null,
                0,
                1,
                false
            });

            if (!hasBase) AddIfMissing(5, new object?[]
            {
                characterBaseId,
                characterBaseMasterId,
                0,
                0,
                costumeMasterId,
                0,
                // Portal 的资源查找使用 CharacterMasterId，队伍等内部关联仍使用实例 ID。
                characterMasterId,
                false
            });

        }

        for (var i = 0; i < 12; i++)
        {
            var partyId = UserScopedId(userId, 4000 + i + 1);
            AddIfMissing(6, new object?[] { partyId, i, $"グループ{i + 1}", 1 });
            AddIfMissing(166, new object?[] { i, i, $"グループ{i + 1}", 1 });

            for (var slot = 1; slot <= 5; slot++)
            {
                var characterIndex = slot switch
                {
                    1 => 0,
                    2 => 2,
                    3 => 3,
                    4 => 4,
                    _ => 5
                };
                AddIfMissing(7, new object?[]
                {
                    UserScopedId(userId, 5000 + (i * 10) + slot),
                    partyId,
                    slot,
                    bootstrapPartyCharacters[characterIndex],
                    null,
                    null,
                    0
                });
                AddIfMissing(167, new object?[]
                {
                    (i * 5) + slot,
                    i,
                    slot,
                    bootstrapPartyCharacters[characterIndex],
                    null,
                    null,
                    0
                });
            }
        }

        for (var i = 0; i < BootstrapMissionStates.Length; i++)
        {
            var state = BootstrapMissionStates[i];
            AddIfMissing(48, new object?[]
            {
                UserScopedId(userId, 8001 + i),
                state.Cleared,
                false,
                state.Progress,
                null,
                state.From,
                state.To
            });
        }

        ResolvePartyCharacterAliases(userId,
            result.OfType<object?[]>().Where(item => item[0] is 7 or 167).Select(item => (object?[])item[1]!),
            result.OfType<object?[]>().Where(item => item[0] is 4).Select(item => (object?[])item[1]!));

        AddIfMissing(109, new object?[] { UserScopedId(userId, 10902), 0, 0, CurrentDailyResetBoundaryUtc(DateTimeOffset.UtcNow).UtcDateTime, 0 });
        AddIfMissing(148, new object?[] { userId, null, true });
        AddIfMissing(174, new object?[] { 1, "アルバム1", 1 });
        AddIfMissing(174, new object?[] { 2, "アルバム2", 2 });
        AddIfMissing(174, new object?[] { 3, "アルバム3", 3 });
        AddIfMissing(174, new object?[] { 4, "アルバム4", 4 });
        AddIfMissing(174, new object?[] { 5, "アルバム5", 5 });
        AddIfMissing(179, BuildInviteCode(userId));
    }

    static object?[] BuildInviteCode(long userId)
    {
        return new object?[] { 0, $"LOCAL{userId % 100000:D5}", false };
    }

    static string DataObjectIdentity(int unionKey, object?[] value)
    {
        return unionKey switch
        {
            27 => $"{unionKey}:{(value.Length > 1 ? value[1] : value[0])}",
            96 => $"{unionKey}:{(value.Length > 1 ? value[1] : value[0])}:{(value.Length > 2 ? value[2] : 0)}",
            7 => $"{unionKey}:{(value.Length > 1 ? value[1] : value[0])}:{(value.Length > 2 ? value[2] : 0)}",
            48 => $"{unionKey}:{(value.Length > 5 ? value[5] : value[0])}:{(value.Length > 6 ? value[6] : 0)}",
            174 => $"{unionKey}:{value[0]}",
            _ => $"{unionKey}:{value[0]}"
        };
    }

    static readonly long[] BootstrapCharacterBaseMasterIds =
    {
        101, 102, 103, 104, 105, 106,
        201, 202, 203, 204, 205,
        301, 302, 303, 304, 305,
        401, 402, 403, 404, 405
    };

    static readonly long[] BootstrapCharacterMasterIds =
    {
        110010, 110020, 110030, 110040, 110050, 110060,
        110070, 110080, 110090, 110100, 110110,
        110120, 110130, 110140, 110150, 110160,
        110170, 110180, 110190, 110200, 110210
    };

    static readonly long[] BootstrapCostumeMasterIds =
    {
        11, 21, 31, 211, 41, 51,
        241, 61, 71, 81, 281,
        91, 101, 111, 121, 131,
        141, 151, 161, 171, 381
    };

    static readonly (bool Cleared, int Progress, int From, int To)[] BootstrapMissionStates =
    {
        (false, 1, 7, 7),
        (false, 1, 13, 13),
        (false, 1, 19, 19),
        (false, 1, 25, 25),
        (false, 1, 31, 31),
        (false, 1, 37, 37),
        (false, 6, 120, 121),
        (false, 5, 220, 221),
        (false, 5, 320, 321),
        (false, 10, 420, 421),
        (false, 1, 1000, 1001),
        (false, 1, 1100, 1100),
        (false, 21, 1200, 1201),
        (false, 1, 1900, 1901),
        (false, 0, 4500, 4501)
    };

    const string RegistrationCampaignMessage = "事前登録キャンペーンの報酬です。";
    const string Release100DaysMessage = "リリース100日目を記念した報酬です。";
    const string WelcomePresentMessage = "ユメステを始めていただいた皆さんへのプレゼントです。";
    const string FirstAnniversaryMessage = "ユメステ1周年のアップデートを記念した、皆様へのプレゼントです！";
    const string ProducerLetterMessage = "【プロデューサーレター】をお読みいただいた皆様へ";
    const string AppStoreRankingMessage = "AppStore無料ランキング2位記念の配布アイテムです。";
    const string TrendRankingMessage = "『#ユメステ特番』のトレンド30位以内達成を記念した報酬です。";

    static readonly BootstrapInboxReward[] BootstrapInboxRewards =
    {
        new(13, 0, 3000, RegistrationCampaignMessage),
        new(1, 210001, 10, RegistrationCampaignMessage),
        new(1, 130051, 10, RegistrationCampaignMessage),
        new(1, 130043, 100, RegistrationCampaignMessage),
        new(1, 130042, 100, RegistrationCampaignMessage),
        new(1, 130041, 100, RegistrationCampaignMessage),
        new(1, 120002, 500, RegistrationCampaignMessage),
        new(1, 130064, 100, RegistrationCampaignMessage),
        new(1, 130063, 100, RegistrationCampaignMessage),
        new(1, 130062, 100, RegistrationCampaignMessage),
        new(1, 130061, 100, RegistrationCampaignMessage),
        new(13, 0, 1000, Release100DaysMessage),
        new(1, 130001, 50, WelcomePresentMessage),
        new(1, 150000, 250, FirstAnniversaryMessage),
        new(1, 151001, 10, ProducerLetterMessage),
        new(1, 151002, 10, ProducerLetterMessage),
        new(1, 151003, 10, ProducerLetterMessage),
        new(1, 151004, 10, ProducerLetterMessage),
        new(1, 151005, 10, ProducerLetterMessage),
        new(1, 151006, 10, ProducerLetterMessage),
        new(1, 151007, 10, ProducerLetterMessage),
        new(1, 151008, 10, ProducerLetterMessage),
        new(1, 151009, 10, ProducerLetterMessage),
        new(1, 151010, 10, ProducerLetterMessage),
        new(1, 151011, 10, ProducerLetterMessage),
        new(1, 151012, 10, ProducerLetterMessage),
        new(1, 151013, 10, ProducerLetterMessage),
        new(1, 151014, 10, ProducerLetterMessage),
        new(1, 151015, 10, ProducerLetterMessage),
        new(1, 151016, 10, ProducerLetterMessage),
        new(1, 151017, 10, ProducerLetterMessage),
        new(1, 151018, 10, ProducerLetterMessage),
        new(1, 151019, 10, ProducerLetterMessage),
        new(1, 151020, 10, ProducerLetterMessage),
        new(1, 151021, 10, ProducerLetterMessage),
        new(1, 210424, 1, WelcomePresentMessage)
    };

    static readonly BootstrapInboxReward[] CheckPackageInboxRewards =
    {
        new(1, 220101, 5, AppStoreRankingMessage),
        new(1, 221101, 5, AppStoreRankingMessage),
        new(13, 0, 500, TrendRankingMessage)
    };

    readonly record struct BootstrapInboxReward(int ThingType, long MasterId, int Quantity, string Message);

    async Task<object?[]> ReadUserAsync(NpgsqlConnection connection, long userId)
    {
        await RefreshStaminaAsync(connection, userId);
        await using var command = new NpgsqlCommand(
            """
            select user_accounts.id,
                   user_game_states.rank,
                   user_game_states.exp,
                   user_game_states.stamina,
                   case when user_game_states.stamina >= user_game_states.max_stamina then now()
                     else user_game_states.stamina_recovery_checkpoint_at
                       + (user_game_states.max_stamina - user_game_states.stamina) * $2 * interval '1 second'
                   end,
                   user_game_states.free_jewel,
                   user_game_states.paid_jewel,
                   user_game_states.coin,
                   user_game_states.rank_limit,
                   user_game_states.stamina_jewel_recovery_count,
                   user_game_states.splash_last_displayed_at,
                   user_game_states.game_start_at,
                   user_accounts.public_id,
                   user_game_states.tutorial_status,
                   user_game_states.is_tutorial_finished,
                   user_game_states.has_unread_game_hint,
                   system_circles.display_id::text
            from user_accounts
            join user_game_states on user_game_states."userId" = user_accounts.id
            left join user_circle_memberships on user_circle_memberships."userId" = user_accounts.id
            left join system_circles on system_circles.id = user_circle_memberships.circle_id
            where user_accounts.id = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(StaminaRecoverySeconds);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return GameResults.UserData()[0] as object?[] ?? Array.Empty<object?>();

        var tutorialStatus = NormalizeTutorialStatus(reader.GetInt32(13));
        var unsetAt = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var publicId = reader.GetString(12);
        var publicNumericId = long.TryParse(publicId, out var parsedPublicId) ? parsedPublicId : reader.GetInt64(0);
        return new object?[]
        {
            // 客户端用 UserScopedId = (userId << 32) | local 生成所有带作用域的 ID。
            // 如果用 10 位的公开 ID，左移 32 位会溢出 long，导致全部 scoped ID 错乱，
            // 客户端无法匹配任何对象数据（表现为卡在加载/主页初始化）。
            // 官方服务端这里用的是内部自增 id（小数字），保持一致。
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            // 客户端 TimeAdjuster.Now 用 JST 墙上时间直接相减，此字段必须使用相同的时钟刻度。
            reader.GetDateTime(4).Add(JapanStandardTimeOffset),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            unsetAt,
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.GetDateTime(11),
            publicId,
            0,
            tutorialStatus,
            0,
            unsetAt,
            false,
            false
        };
    }

    async Task<object?[]> ReadProfileAsync(NpgsqlConnection connection, long userId)
    {
        var playerRating = await ReadPlayerRatingAsync(connection, userId);
        await using var command = new NpgsqlCommand(
            """
            select display_name,
                   comment,
                   favorite_character_id,
                   title_master_id,
                   nameplate_master_id,
                   icon_frame_master_id,
                   birthday,
                   support_character_id,
                   is_public,
                   total_power,
                   honor_point,
                   accepts_friend_requests,
                   selected_poster_id,
                   -- 主页角色必须是玩家拥有的角色，否则客户端查不到会空引用
                   case when exists (
                          select 1 from user_character_cards hc
                          where hc."userId" = user_profiles."userId"
                            and hc.character_master_id = user_profiles.home_character_master_id)
                        then user_profiles.home_character_master_id
                        else (select mc.character_master_id from user_character_cards mc
                               where mc."userId" = user_profiles."userId"
                               order by mc.is_favorite desc, mc.id limit 1)
                   end as home_character_master_id,
                   is_home_character_illust,
                   show_home_character,
                   name_base_color_master_id,
                   name_color_master_id,
                   home_skin_master_id,
                   is_public_activity_log,
                   (select c.id from user_character_cards c
                     where c."userId" = user_profiles."userId"
                     order by c.is_favorite desc, c.id limit 1) as main_card_id,
                   trophy_master_id_1,
                   trophy_master_id_2,
                   trophy_master_id_3
            from user_profiles
            where "userId" = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new object?[]
        {
            userId,
            reader.GetString(0),
            reader.GetString(1),
            // main_u_character_id 必须是有效卡片ID；为 0 会让 ProfileModel 空引用
            GetNullableInt64(reader, 2) is long favoriteCard && favoriteCard > 0
                ? favoriteCard
                : GetNullableInt64(reader, 20) ?? 0L,
            GetNullableInt64(reader, 4),
            reader.GetInt64(17) > 0 ? reader.GetInt64(17) : DefaultNameColorMasterId,
            // 资料页装备的三个称号（trophy_master_id_1/2/3，对应客户端 MTrophyId1/2/3）
            GetNullableInt64(reader, 21),
            GetNullableInt64(reader, 22),
            GetNullableInt64(reader, 23),
            // 官方线上该字段为整数（实测他们返回 0 而非 0.0），
            // 用 double 会被客户端的 msgpack 反序列化按错误宽度读取，导致后续字段全部错位。
            (long)Math.Round(playerRating),
            null,
            reader.GetBoolean(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetBoolean(11),
            null,
            GetNullableInt64(reader, 13) ?? DefaultCharacterMasterId,
            reader.GetBoolean(14),
            reader.GetBoolean(19),
            // These resources are loaded while the title flow transitions to
            // Home.  Legacy local rows used zero/guessed IDs which serialize
            // correctly but do not exist in the 2.31.0 master database.
            reader.GetInt64(16) > 0 ? reader.GetInt64(16) : DefaultNameBaseColorMasterId,
            GetNullableInt64(reader, 5) is long iconFrameMasterId && iconFrameMasterId > 0
                ? iconFrameMasterId
                : DefaultIconFrameMasterId,
            GetNullableInt64(reader, 18) is long homeSkinMasterId && homeSkinMasterId > 0
                ? homeSkinMasterId
                : DefaultHomeSkinMasterId
        };
    }

    async Task<object?[]> ReadUserProfileDetailAsync(long userId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select user_accounts.public_id,
                   user_profiles.display_name,
                   user_profiles.home_character_master_id,
                   user_profiles.is_home_character_illust,
                   user_profiles.icon_frame_master_id,
                   user_profiles.name_color_master_id,
                   user_profiles.name_base_color_master_id,
                   user_profiles.nameplate_master_id,
                   (select c.id from user_character_cards c
                     where c."userId" = user_accounts.id
                     order by c.is_favorite desc, c.id limit 1) as main_card_id
            from user_accounts
            join user_profiles on user_profiles."userId" = user_accounts.id
            where user_accounts.id = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return BuildUserProfileDetail(FormatNumericId(userId), "LocalPlayer", DefaultCharacterMasterId, 1, false, DefaultIconFrameMasterId, DefaultNameColorMasterId, DefaultNameBaseColorMasterId, null);
        }

        return BuildUserProfileDetail(
            reader.GetString(0),
            reader.GetString(1),
            // 客户端 ProfileModel 要用「卡片ID」去角色列表里查，传主数据ID会空引用
            GetNullableInt64(reader, 8) ?? GetNullableInt64(reader, 2) ?? DefaultCharacterMasterId,
            1,
            reader.GetBoolean(3),
            GetNullableInt64(reader, 4) ?? 0L,
            reader.GetInt64(5),
            reader.GetInt64(6),
            GetNullableInt64(reader, 7));
    }

    static async Task<int> ExecuteNonQueryAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object[] values)
    {
        await using var command = transaction is null
            ? new NpgsqlCommand(sql, connection)
            : new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }
        return await command.ExecuteNonQueryAsync();
    }

    async Task<object?[]> ReadHomeDisplayPreferenceAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select
              character_base_master_id,
              illustration_character_base_master_id,
              sub_character_base_master_id_1,
              sub_character_base_master_id_2,
              sub_character_base_master_id_3,
              costume_master_id,
              illustration_costume_master_id,
              sub_costume_master_id_1,
              sub_costume_master_id_2,
              sub_costume_master_id_3,
              character_master_id,
              is_illust,
              display_type,
              login_bonus_character_base_master_id,
              login_bonus_costume_master_id
            from user_home_display_preferences
            where "userId" = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"用户 {userId} 缺少主页展示偏好数据");
        }

        return new object?[]
        {
            userId,
            reader.GetInt64(0),
            GetNullableInt64(reader, 1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            GetNullableInt64(reader, 6),
            GetNullableInt64(reader, 7),
            GetNullableInt64(reader, 8),
            GetNullableInt64(reader, 9),
            reader.GetInt64(10),
            reader.GetBoolean(11),
            reader.GetInt32(12),
            reader.GetInt64(13),
            reader.GetInt64(14)
        };
    }

    async Task<List<object?[]>> ReadCharactersAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, character_master_id, level, exp, awakening, character_base_id,
                   skill_level, extra_1, extra_2, is_new, display_awakening_status, is_favorite,
                   talent_stage, selection_type
            from user_character_cards
            where "userId" = $1
            order by id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadUpdatedCharacter(reader));
        }
        return result;
    }

    async Task<List<object?[]>> ReadCharacterBasesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select character_base.*, selected.character_master_id as portal_character_master_id
            from user_character_bases character_base
            left join user_character_cards selected
              on selected.id = character_base.selected_character_id
             and selected."userId" = character_base."userId"
             and selected.character_base_id = character_base.id
            where character_base."userId" = $1
            order by character_base.id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // 必须严格对齐客户端 CharacterBase 的 [Key(n)]：
            // 0=Id 1=CharacterBaseMasterId 2=StarRank 3=TotalStarPoint 4=CostumeMasterId
            // 5=KeyMissionLevel 6=PortalCharacterId 7=PortalDisplayAwakeningStatus
            // 注意：客户端没有「角色等级」字段，游戏里显示的是 StarRank(rank) 和 TotalStarPoint(exp)。
            // level 列在这里复用为 KeyMissionLevel。
            result.Add(new object?[]
            {
                reader.GetInt64(reader.GetOrdinal("id")),                                 // 0 Id
                reader.GetInt64(reader.GetOrdinal("character_base_master_id")),            // 1 CharacterBaseMasterId
                reader.GetInt32(reader.GetOrdinal("rank")),                                // 2 StarRank
                reader.GetInt32(reader.GetOrdinal("exp")),                                 // 3 TotalStarPoint
                reader.GetInt64(reader.GetOrdinal("costume_master_id")),                   // 4 CostumeMasterId
                reader.GetInt32(reader.GetOrdinal("level")),                               // 5 KeyMissionLevel
                GetNullableInt64(reader, reader.GetOrdinal("portal_character_master_id"))
                    ?? throw new InvalidOperationException("Actor Portal 选中卡片关联无效"), // 6 PortalCharacterId
                reader.GetBoolean(reader.GetOrdinal("portal_display_awakening_status"))    // 7 PortalDisplayAwakeningStatus
            });
        }
        return result;
    }

    async Task<List<object?[]>> ReadCostumesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, costume_master_id
            from user_character_costumes
            where "userId" = $1
            order by id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[] { reader.GetInt64(0), reader.GetInt64(1) });
        }
        return result;
    }

    async Task<List<object?[]>> ReadItemPossessionsAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, item_master_id, quantity
            from user_item_possessions
            where "userId" = $1
            order by item_master_id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadLivePlayResultsAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id,
                   live_master_id,
                   difficulty,
                   achievement_rate,
                   notation_rate,
                   clear_count,
                   clear_lamp,
                   rate_grade
            from user_live_play_results
            where "userId" = $1
            order by live_master_id, difficulty
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(5),
                null,
                GetNullableDouble(reader, 3) ?? 0.0,
                GetNullableDouble(reader, 4) ?? 0.0,
                reader.GetInt32(6),
                2,
                reader.GetInt32(7)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadLiveMusicStatesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, music_master_id, is_unlocked, stella_released, olivier_release_status
            from user_live_music_states
            where "userId" = $1
            order by music_master_id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                null,
                reader.GetInt64(1),
                null,
                null,
                reader.GetBoolean(3),
                null,
                0,
                reader.GetInt32(4),
                reader.GetBoolean(2)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadDugongRunStatesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, cleared_course_ids, no_mistake_course_ids, status
            from user_dugong_run_states
            where "userId" = $1
            order by event_master_id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                DugongRunStatePublicId,
                (int[])reader[1],
                (int[])reader[2],
                reader.GetInt32(3)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadPhotoRecordsAsync(NpgsqlConnection connection, long userId)
    {
        // 字段顺序必须严格对齐客户端 Photo 的 [Key(n)]：
        // 3=PhotoEffectMasterId(可空) 4=Lock 5=UseAlbumPage 6=Level 7=Rarity
        // 8=SignMasterId 9=GeneratedAt 10=ThumbnailSasToken 11=Appeared 12=Tagged 13=UseDecoPage
        await using var command = new NpgsqlCommand(
            """
            select id,
                   file_name,
                   read_sas_token,
                   coalesce((payload->>'photoEffectMasterId')::bigint, null),
                   is_favorite,
                   album_group_id,
                   coalesce((payload->>'level')::int, 1),
                   coalesce((payload->>'rarity')::int, 5),
                   coalesce((payload->>'signMasterId')::bigint, null),
                   created_at,
                   coalesce((payload->>'appeared')::jsonb, '[]'::jsonb),
                   coalesce((payload->>'tagged')::jsonb, '[]'::jsonb),
                   coalesce((payload->>'useDecoPage')::int, 0)
            from user_photo_records
            where "userId" = $1
            order by created_at, id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        static object?[] ToLongArray(System.Text.Json.JsonElement element)
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Array) return Array.Empty<object?>();
            return element.EnumerateArray().Select(v => (object?)v.GetInt64()).ToArray();
        }

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),                                            // 0 Id
                reader.GetString(1),                                           // 1 FileName
                reader.IsDBNull(2) ? GameResults.BuildPhotoSasToken("photo") : reader.GetString(2), // 2 SasToken
                reader.IsDBNull(3) ? null : reader.GetInt64(3),                 // 3 PhotoEffectMasterId
                reader.GetBoolean(4),                                          // 4 Lock
                GetNullableInt64(reader, 5),                                   // 5 UseAlbumPage
                reader.GetInt32(6),                                            // 6 Level
                reader.GetInt32(7),                                            // 7 Rarity
                reader.IsDBNull(8) ? null : reader.GetInt64(8),                 // 8 SignMasterId
                reader.GetDateTime(9),                                         // 9 GeneratedAt
                GameResults.BuildPhotoSasToken("thumbnail"),                    // 10 ThumbnailSasToken
                ToLongArray(System.Text.Json.JsonDocument.Parse(reader.GetString(10)).RootElement), // 11 Appeared
                ToLongArray(System.Text.Json.JsonDocument.Parse(reader.GetString(11)).RootElement), // 12 Tagged
                reader.GetInt32(12)                                            // 13 UseDecoPage
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadPhotoStatesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, photo_category, current_count, max_count
            from user_photo_states
            where "userId" = $1
            order by photo_category
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadPhotoAlbumArrangementsAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, album_group_id, arrangement_type, is_public, layout_payload
            from user_photo_album_arrangements
            where "userId" = $1
            order by album_group_id, arrangement_type
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetBoolean(3),
                new MsgPack.Binary((byte[])reader[4]),
                null
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadMusicVideoWatchStatesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select music_video_id
            from user_music_video_watch_states
            where "userId" = $1
            order by music_video_id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[] { reader.GetInt64(0), "" });
        }
        return result;
    }

    async Task<object?[]> ReadCurrencyAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select coin, free_jewel, paid_jewel
            from user_item_currencies
            where "userId" = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new object?[] { userId, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) };
    }

    async Task<object?[]> ReadHomeBgmAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select home_bgm_master_id, selection_type, home_bgm_detail_master_id
            from user_home_bgms
            where "userId" = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new object?[] { reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2) };
    }

    // 名字颜色：本地直接解锁全部 NameColorMaster（客户端按 key 44 渲染列表）
    static object?[] BuildNameColors(long userId)
    {
        var ids = ReadEmbeddedMasterIds("Status.NameColorMaster.json");
        return new object?[] { userId, ids.Cast<object?>().ToArray() };
    }

    // 称号：TrophyMaster 全量下发，行格式 [id, trophyMasterId, trophyGroupMasterId, order]
    static List<object?[]> BuildTrophies(long userId)
    {
        var result = new List<object?[]>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("Status.TrophyMaster.json");
        if (stream is null) return result;
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        var order = 0;
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            var values = row.EnumerateArray().ToArray();
            if (values.Length < 6) continue;
            var trophyId = values[0].GetInt64();
            var groupId = values[5].GetInt64();
            order++;
            result.Add(new object?[] { userId * 1000000 + order, trophyId, groupId, order });
            if (result.Count >= 300) break;   // 全量 6455 会把客户端压垮，先限制数量
        }
        return result;
    }

    static List<long> ReadEmbeddedMasterIds(string resource)
    {
        var ids = new List<long>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return ids;
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            var values = row.EnumerateArray().ToArray();
            if (values.Length == 0) continue;
            ids.Add(values[0].GetInt64());
        }
        return ids;
    }

    // 外观收集：名字颜色 / 名字底色 / 头像框
    async Task<(object?[] Colors, object?[] BaseColors, object?[] IconFrames)> ReadCosmeticListsAsync(
        NpgsqlConnection connection, long userId)
    {
        // 名字颜色：取玩家已拥有的（user_name_colors），没有则给默认色
        var colors = new List<object?>();
        await using (var command = new NpgsqlCommand(
            """
            select name_color_master_id from user_name_colors where "userId" = $1 order by name_color_master_id
            """, connection))
        {
            command.Parameters.AddWithValue(userId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) colors.Add(reader.GetInt64(0));
        }
        if (colors.Count == 0) colors.Add(1L);

        // 名字底色与头像框：全部下发（这些资源不影响平衡，客户端按需展示）
        var baseColors = new object?[]
        {
            180001L, 180101L, 180102L, 180103L, 180104L, 180105L, 180106L,
            180201L, 180202L, 180203L, 180204L, 180205L,
            180301L, 180302L, 180303L, 180304L, 180305L,
            180401L, 180402L, 180403L, 180404L, 180405L
        };
        var iconFrames = new List<object?>();
        for (var i = 1; i <= 5; i++) iconFrames.Add((long)(190000 + i));
        for (var group = 1; group <= 4; group++)
            for (var i = 1; i <= (group == 1 ? 6 : 5); i++)
                iconFrames.Add((long)(190000 + group * 100 + i));

        return (colors.ToArray(), baseColors, iconFrames.ToArray());
    }

    // 当前正在进行的活动（主数据日期包含 now），并合并玩家点数
        // 活动交换所：从内嵌主数据里取 shop id == 活动 ID 的商店，展开商品列表
    // 返回 [id, isDisplayRequiredHavingItem, category, name, displayThingType,
    //       displayItemMasterId, bannerPath, startDate, endDate, lastRefreshedAt, lineup, order]
    async Task<List<object?[]>> ReadExchangeShopStatesAsync(long userId)
    {
        var shops = new List<object?[]>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("Event.ExchangeShopMaster.json");
        if (stream is null) return shops;
        using var doc = await JsonDocument.ParseAsync(stream);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 12) continue;
            if (!row[0].TryGetInt64(out var id)) continue;
            if (!row[2].TryGetInt32(out var category) || category != 3) continue;   // 只发活动点数交换所

            var lineup = new List<object?[]>();
            if (row[10].ValueKind == JsonValueKind.Array)
            {
                foreach (var item in row[10].EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 14) continue;
                    lineup.Add(new object?[]
                    {
                        item[0].TryGetInt64(out var v0) ? v0 : 0L,          // id
                        item[1].TryGetInt64(out var v1) ? v1 : 0L,          // thingId
                        item[2].TryGetInt32(out var v2) ? v2 : 0,           // thingType
                        item[3].TryGetInt32(out var v3) ? v3 : 1,           // quantity
                        item[4].ValueKind == JsonValueKind.Number ? item[4].GetInt64() : (long?)null, // limit
                        item[5].TryGetInt32(out var v5) ? v5 : 0,           // order
                        item[6].ValueKind == JsonValueKind.Number ? item[6].GetInt32() : (int?)null, // replaceType
                        false,                                              // isDisplayLocked
                        item[8].ValueKind == JsonValueKind.Number ? item[8].GetInt32() : (int?)null, // unlockType
                        item[9].ValueKind == JsonValueKind.Number ? item[9].GetInt64() : (long?)null,// unlockValue
                        (DateTime?)null,                                    // startDate
                        (DateTime?)null,                                    // endDate
                        item[12].TryGetInt64(out var v12) ? v12 : 0L,       // requiredItemMasterId
                        item[13].TryGetInt32(out var v13) ? v13 : 0         // requiredQuantity
                    });
                }
            }

            shops.Add(new object?[]
            {
                id,
                row[1].ValueKind == JsonValueKind.True,
                category,
                row[3].ValueKind == JsonValueKind.String ? row[3].GetString() : null,
                row[4].TryGetInt32(out var dt) ? dt : 0,
                row[5].ValueKind == JsonValueKind.Number ? row[5].GetInt64() : (long?)null,
                row[6].ValueKind == JsonValueKind.String ? row[6].GetString() : null,
                (DateTime?)null,
                (DateTime?)null,
                DateTime.UtcNow,
                lineup.ToArray(),
                row[11].TryGetInt32(out var ord) ? ord : 0
            });
        }
        return shops;
    }

    static DateTime? ToTime(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : (DateTime?)null;

async Task<List<object?[]>> ReadActiveEventStatesAsync(NpgsqlConnection connection, long userId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 玩家在各活动的点数
        var points = new Dictionary<long, (long Point, int Rank, bool Tips)>();
        await using (var command = new NpgsqlCommand(
            """
            select event_master_id, point, rank, is_tips_read
            from user_story_event_states where "userId" = $1
            """, connection))
        {
            command.Parameters.AddWithValue(userId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                points[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetInt32(2), reader.GetBoolean(3));
        }

        static async Task<List<(long Id, long Start, long End)>> LoadAsync(string resource, int kindKey)
        {
            var list = new List<(long, long, long)>();
            using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream(resource);
            if (stream is null) return list;
            using var doc = await JsonDocument.ParseAsync(stream);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 8) continue;
                if (!row[0].TryGetInt64(out var id)) continue;
                long start = 0, end = 0;
                for (var i = 2; i < row.GetArrayLength(); i++)
                {
                    if (row[i].ValueKind != JsonValueKind.Number || !row[i].TryGetInt64(out var n)) continue;
                    if (n < 1500000000) continue;
                    if (start == 0) start = n;
                    end = n;
                }
                list.Add((id, start, end));
            }
            return list;
        }

        var result = new List<object?[]>();

        // 156 Event：[id, eventMasterId, point, updatedAt, lastRank, readTips, loginDays]
        foreach (var (id, start, end) in await LoadAsync("Event.EventMaster.json", 156))
        {
            if (now < start || now > end) continue;
            var p = points.TryGetValue(id, out var v) ? v : (0L, 0, false);
            result.Add(new object?[]
            {
                156,
                new object?[]
                {
                    UserScopedId(userId, 156000 + id), id, p.Item1,
                    DateTime.UtcNow, p.Item2, p.Item3, 1
                }
            });
        }

        // 101 StoryEvent：优先用 GM 记录的活动（active_events.json），否则按主数据日期判断
        var activeOverride = new HashSet<long>();
        try
        {
            var activePath = System.IO.Path.Combine(AppContext.BaseDirectory, "active_events.json");
            if (System.IO.File.Exists(activePath))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(activePath));
                foreach (var item in doc.RootElement.EnumerateArray())
                    if (item.TryGetInt64(out var v)) activeOverride.Add(v);
            }
        }
        catch { }

        // 只下发玩家真正参加过的活动（有分数记录），没参加的不下发 -> 客户端显示未参加
        var emittedStory = new HashSet<long>();
        foreach (var (id, start, end) in await LoadAsync("Event.StoryEventMaster.json", 101))
        {
            if (!emittedStory.Add(id)) continue;
            if (!points.ContainsKey(id)) continue;
            var p = points.TryGetValue(id, out var v) ? v : (0L, 0, false);
            result.Add(new object?[]
            {
                101,
                new object?[]
                {
                    UserScopedId(userId, 101000 + id), id, p.Item1,
                    DateTime.UtcNow, p.Item2, p.Item3, 1
                }
            });

            // 140, // TotalPointEvent：[id, masterId, totalPoint, receivedRewardOrder]
            result.Add(new object?[]
            {
                140,
                new object?[]
                {
                    UserScopedId(userId, 140000 + id), id, p.Item1,
                    await ReadReceivedTotalPointOrderAsync(connection, userId, id)
                }
            });
        }

        // 82 ExchangeShopMaster：发当前开启的交换所（优先 active_shops.json，否则活动ID）
        var activeShops = new HashSet<long>();
        try
        {
            var shopPath = System.IO.Path.Combine(AppContext.BaseDirectory, "active_shops.json");
            if (System.IO.File.Exists(shopPath))
            {
                using var shopDoc = JsonDocument.Parse(System.IO.File.ReadAllText(shopPath));
                foreach (var item in shopDoc.RootElement.EnumerateArray())
                    if (item.TryGetInt64(out var sv)) activeShops.Add(sv);
            }
        }
        catch { }
        if (activeShops.Count == 0) activeShops = activeOverride;
        foreach (var row in await ReadExchangeShopStatesAsync(userId))
        {
            if (!activeShops.Contains((long)row[0]!)) continue;
            result.Add(DataObject(82, row));
        }

        // 交换所限购状态（客户端 ExchangeLimit，union 102）：
        // 客户端用 ExchangedCount 判断「已兑完」，到达限购数量时把商品下架，不再显示可兑换。
        await using (var limits = new NpgsqlCommand(
            """
            select id, exchange_shop_thing_id, exchanged_count
            from user_event_exchange_shop_states where "userId" = $1
            """, connection))
        {
            limits.Parameters.AddWithValue(userId);
            await using var reader = await limits.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(DataObject(102, new object?[]
                {
                    reader.GetInt64(0), reader.GetInt64(1), 0, null, reader.GetInt32(2), null
                }));
        }

        return result;
    }

    // 真实排行：按活动点数降序。
    // kind: top=前 100 / near=自己附近±5 / border=前 100（边界）/ friend=仅好友 / circle=仅同社团
    // 返回 (rawRanking[], userProfiles[])
    public async Task<(object?[] Ranking, object?[] Profiles)> ReadEventRankingAsync(
        HttpContext context, long eventMasterId, int limit = 100, string kind = "top")
    {
        // eventMasterId = 0 时用 GM 当前开启的活动
        if (eventMasterId == 0)
        {
            try
            {
                var activePath = System.IO.Path.Combine(AppContext.BaseDirectory, "active_events.json");
                if (System.IO.File.Exists(activePath))
                {
                    using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(activePath));
                    foreach (var item in doc.RootElement.EnumerateArray())
                        if (item.TryGetInt64(out var v)) { eventMasterId = v; break; }
                }
            }
            catch { }
        }
        var ranking = new List<object?[]>();
        var profiles = new List<object?[]>();
        await using var connection = await database.OpenConnectionAsync();
        // border（阶梯）：档位取自 StoryEventRewardMaster 的 rankTo（档位结束名次）：
        // 1,2,3,10,20,30,40,50,100,200,…,200000。每档取该名次的真实玩家。
        if (kind == "border")
        {
            var tiers = new[] { 1, 2, 3, 10, 20, 30, 40, 50, 100, 200, 300, 400, 500,
                1000, 2000, 3000, 4000, 5000, 10000, 20000, 30000, 40000, 50000, 100000, 200000 };
            foreach (var tier in tiers)
            {
                await using var tierCommand = new NpgsqlCommand("""
                    select s.point, coalesce(nullif(p.display_name, ''), a.name), a.public_id,
                           coalesce(p.home_character_master_id, 0),
                           coalesce(p.icon_frame_master_id, 190001),
                           coalesce(p.name_base_color_master_id, 180001),
                           coalesce(p.name_color_master_id, 1),
                           coalesce(p.nameplate_master_id, 0)
                    from user_story_event_states s
                    join user_accounts a on a.id = s."userId"
                    left join user_profiles p on p."userId" = s."userId"
                    where s.event_master_id = $1 and s.point > 0
                    order by s.point desc, s."userId" asc
                    offset $2 limit 1
                    """, connection);
                tierCommand.Parameters.AddWithValue(eventMasterId);
                tierCommand.Parameters.AddWithValue(tier - 1);
                await using var tierReader = await tierCommand.ExecuteReaderAsync();
                if (!await tierReader.ReadAsync()) continue;
                var score = tierReader.GetInt64(0);
                var name = tierReader.IsDBNull(1) ? "プレイヤー" : tierReader.GetString(1);
                var publicId = tierReader.IsDBNull(2) ? "" : tierReader.GetString(2);
                ranking.Add(new object?[] { tier, score, publicId, 0L });
                profiles.Add(new object?[]
                {
                    publicId, name, null, null, null,
                    tierReader.GetInt64(3), 1, false, tierReader.GetInt64(4),
                    tierReader.GetInt64(6), tierReader.GetInt64(5), tierReader.GetInt64(7), (long?)null
                });
            }
            return (ranking.ToArray(), profiles.ToArray());
        }
        // near：先算自己的名次，再取前后各 5 名（其他 kind 保持原来的前 N）
        var offset = 0;
        if (kind == "near")
        {
            var currentUserId = await GetCurrentUserIdAsync(context) ?? 0L;
            if (currentUserId > 0)
            {
                await using var mine = new NpgsqlCommand("""
                    select count(*) + 1
                    from user_story_event_states
                    where event_master_id = $1
                      and (point > (select point from user_story_event_states where event_master_id=$1 and "userId"=$2)
                        or (point = (select point from user_story_event_states where event_master_id=$1 and "userId"=$2)
                            and "userId" < $2))
                    """, connection);
                mine.Parameters.AddWithValue(eventMasterId);
                mine.Parameters.AddWithValue(currentUserId);
                var rankValue = Convert.ToInt32(await mine.ExecuteScalarAsync() ?? 1);
                offset = Math.Max(0, rankValue - 6);
                limit = 11;
            }
        }
        // friend / circle：先用子查询筛出目标用户集合
        var filterClause = kind switch
        {
            "friend" => """
                and (s."userId" = $3 or s."userId" in (
                    select r."friendUserId" from user_friend_relationships r where r."userId" = $3
                    union
                    select r."userId" from user_friend_relationships r where r."friendUserId" = $3
                ))
                """,
            "circle" => """
                and s."userId" in (
                    select m."userId" from user_circle_memberships m
                    where m.circle_id = (select circle_id from user_circle_memberships where "userId" = $3)
                )
                """,
            _ => ""
        };
        await using var command = new NpgsqlCommand($"""
            select s."userId", s.point, coalesce(nullif(p.display_name, ''), a.name) as name,
                   a.public_id,
                   coalesce(p.home_character_master_id, 0),
                   coalesce(p.icon_frame_master_id, 190001),
                   coalesce(p.name_base_color_master_id, 180001),
                   coalesce(p.name_color_master_id, 1),
                   coalesce(p.nameplate_master_id, 0),
                   p.trophy_master_id_1,
                   p.trophy_master_id_2,
                   p.trophy_master_id_3
            from user_story_event_states s
            join user_accounts a on a.id = s."userId"
            left join user_profiles p on p."userId" = s."userId"
            where s.event_master_id = $1 and s.point > 0 {filterClause}
            order by s.point desc, s."userId" asc
            limit $2 offset $4
            """, connection);
        command.Parameters.AddWithValue(eventMasterId);
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(await GetCurrentUserIdAsync(context) ?? 0L);
        command.Parameters.AddWithValue(offset);
        await using var reader = await command.ExecuteReaderAsync();
        var rank = 0;
        while (await reader.ReadAsync())
        {
            rank++;
            var uid = reader.GetInt64(0);
            var point = reader.GetInt64(1);
            var name = reader.IsDBNull(2) ? "プレイヤー" : reader.GetString(2);
            var publicId = reader.IsDBNull(3) ? uid.ToString() : reader.GetString(3);
            // user_id 必须是字符串（publicId），否则客户端解析失败、整行不显示
            ranking.Add(new object?[] { rank, point, publicId, 0L });
            profiles.Add(new object?[]
            {
                publicId,                      // 0 user_id (string)
                name,                          // 1 user_name
                reader.IsDBNull(9) ? null : reader.GetInt64(9),   // 2 trophy1
                reader.IsDBNull(10) ? null : reader.GetInt64(10), // 3 trophy2
                reader.IsDBNull(11) ? null : reader.GetInt64(11), // 4 trophy3
                reader.GetInt64(4),            // 5 main_m_character_id
                1,                             // 6 main_character_level
                false,                         // 7 awakening
                reader.GetInt64(5),            // 8 icon_frame_master_id
                reader.GetInt64(7),            // 9 name_color_master_id
                reader.GetInt64(6),            // 10 name_base_color_master_id
                reader.GetInt64(8),            // 11 nameplate_master_id
                (long?)null                    // 12 nameplate_detail_master_id
            });
        }
        return (ranking.ToArray(), profiles.ToArray());
    }

    // 排行结果（直接可写帧）：[rawRanking[], userProfiles[]]
    public async Task<object?[]> ReadEventRankingFrameAsync(HttpContext context, long eventMasterId, string kind = "top", int limit = 100)
    {
        var r = await ReadEventRankingAsync(context, eventMasterId, limit, kind);
        return new object?[] { r.Ranking, r.Profiles };
    }

    // 我在某活动的排名：[rank, point, publicId(字符串), 0]
    public async Task<object?[]> ReadMyEventRankingAsync(HttpContext context, long eventMasterId)
    {
        var userId = await GetCurrentUserIdAsync(context) ?? 0L;
        if (userId == 0) return new object?[] { 0, 0L, "000000", 0 };
        await using var connection = await database.OpenConnectionAsync();
        long point = 0;
        string publicId = "000000";
        await using (var p = new NpgsqlCommand(
            """
            select coalesce(point,0) from user_story_event_states
            where "userId" = $1 and event_master_id = $2
            """, connection))
        {
            p.Parameters.AddWithValue(userId);
            p.Parameters.AddWithValue(eventMasterId);
            var v = await p.ExecuteScalarAsync();
            if (v is not null and not DBNull) point = Convert.ToInt64(v);
        }
        await using (var a = new NpgsqlCommand("select public_id from user_accounts where id = $1", connection))
        {
            a.Parameters.AddWithValue(userId);
            var v = await a.ExecuteScalarAsync();
            if (v is string s && s.Length > 0) publicId = s;
        }
        // 名次 = 点数比我高的人数 + 1
        long rank = 1;
        await using (var r = new NpgsqlCommand(
            """
            select count(*) from user_story_event_states
            where event_master_id = $1 and point > $2
            """, connection))
        {
            r.Parameters.AddWithValue(eventMasterId);
            r.Parameters.AddWithValue(point);
            rank = Convert.ToInt64(await r.ExecuteScalarAsync()) + 1;
        }
        return new object?[] { rank, point, publicId, 0 };
    }

    // 月卡状态：[id, validUntil, passKind]
    async Task<List<object?[]>> ReadLoginPassStatusAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, valid_until, pass_kind
            from user_login_pass_status
            where "userId" = $1 and valid_until > now()
            order by id
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetDateTime(1).ToUniversalTime(),
                reader.GetInt32(2)
            });
        }
        return result;
    }

    // 从内嵌 JSON 读取称号主数据
    static async Task<object?[][]> ReadTrophyCatalogAsync(string resourceName)
    {
        await Task.CompletedTask;
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return Array.Empty<object?[]>();
        using var document = await JsonDocument.ParseAsync(stream);
        var rows = new List<object?[]>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var values = new List<object?>();
            foreach (var field in row.EnumerateArray())
            {
                values.Add(field.ValueKind switch
                {
                    JsonValueKind.Number => field.TryGetInt64(out var l) ? l : field.GetDouble(),
                    JsonValueKind.String => field.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                });
            }
            rows.Add(values.ToArray());
        }
        return rows.ToArray();
    }

    // 宝石商店购买状态
    async Task<List<object?[]>> ReadJewelShopStatesAsync(NpgsqlConnection connection, long userId)
    {
        var result = new List<object?[]>();
        await using var command = new NpgsqlCommand(
            """
            select id, shop_item_id, purchased_count, purchased_count, reset_at
            from user_shop_purchase_states
            where "userId" = $1 and shop_item_id in (2001, 3003, 3004)
            order by shop_item_id
            """, connection);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4).ToUniversalTime()
            });
        }
        return result;
    }

    // 市场状态：[id, lastRefreshedAt, refreshTimes]
    async Task<object?[]> ReadMarketStateAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select last_refreshed_at, refresh_times from user_market_states where "userId" = $1
            """, connection);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return new object?[] { userId, reader.GetDateTime(0).ToUniversalTime(), reader.GetInt32(1) };
        return new object?[] { userId, DateTime.UtcNow, 0 };
    }

    // 其他谱面成绩：[id, anotherNotationMasterId, clearLamp, rateGrade, achievementRatePercent]
    async Task<List<object?[]>> ReadAnotherNotationsAsync(NpgsqlConnection connection, long userId)
    {
        var result = new List<object?[]>();
        await using var command = new NpgsqlCommand(
            """
            select id, another_notation_master_id, clear_lamp, achievement_rate
            from user_another_notation_results where "userId" = $1 order by id
            """, connection);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var rate = reader.GetDecimal(3);
            var grade = rate >= 100m ? 5 : rate >= 95m ? 4 : rate >= 90m ? 3 : rate >= 80m ? 2 : rate > 0m ? 1 : 0;
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                grade,
                (long)Math.Round(rate)
            });
        }
        return result;
    }

    // 喜爱服装：[id, characterBaseMasterId, [costumeMasterIds]]
    async Task<List<object?[]>> ReadFavoriteCostumesAsync(NpgsqlConnection connection, long userId)
    {
        // user_character_costumes 只有 id/userId/costume_master_id，
        // 没有收藏标记与角色分组，因此这里不伪造数据，返回空列表。
        await Task.CompletedTask;
        return new List<object?[]>();
    }

    // 称号：[id, trophyMasterId, trophyGroupMasterId, currentOrder]
    async Task<List<object?[]>> ReadTrophiesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, trophy_master_id, trophy_group_master_id, current_order
            from user_trophies
            where "userId" = $1
            order by current_order, id
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt32(3)
            });
        }
        return result;
    }

    async Task<List<object?[]>> ReadPostersAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, poster_master_id, level, breakthrough_phase, released_episode,
                   item_consume_break_through_count, is_favorite, alternative_image_pattern
            from user_posters
            where "userId" = $1
            order by id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetBoolean(6),
                reader.GetInt32(7)
            });
        }
        return result;
    }

    async Task<List<object?[]>> ReadAccessoriesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, accessory_master_id, level, locked, accessory_effects, reference_counting, is_favorite
            from user_accessories
            where "userId" = $1
            order by id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var effects = reader.IsDBNull(4)
                ? Array.Empty<long>()
                : (long[])reader.GetValue(4);
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetBoolean(3),
                effects.Cast<object?>().ToArray(),
                reader.GetInt32(5),
                reader.GetBoolean(6)
            });
        }
        return result;
    }

    async Task<object?[]> ReadHomeSkinAsync(NpgsqlConnection connection, long userId)
    {
        await using var selectedCommand = new NpgsqlCommand(
            """
            select selected_home_skin_master_id
            from user_home_skins
            where "userId" = $1
            """,
            connection);
        selectedCommand.Parameters.AddWithValue(userId);
        var selected = (long?)await selectedCommand.ExecuteScalarAsync() ?? DefaultHomeSkinMasterId;

        await using var possessionCommand = new NpgsqlCommand(
            """
            select home_skin_master_id
            from user_home_skin_possessions
            where "userId" = $1
            order by home_skin_master_id
            """,
            connection);
        possessionCommand.Parameters.AddWithValue(userId);

        var possessed = new List<long>();
        await using var reader = await possessionCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            possessed.Add(reader.GetInt64(0));
        }

        if (possessed.Count == 0) possessed.Add(DefaultHomeSkinMasterId);
        return new object?[] { selected, possessed.ToArray() };
    }

    static async Task<object?[]> ReadUserPreferenceAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand("""
            select p.selected_party_id, u.birthday
            from user_player_preferences p join user_profiles u using("userId")
            where p."userId"=$1
            """, connection);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("缺少玩家偏好存档");
        DateTime? birthDate = reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeSeconds(checked((long)reader.GetDouble(1))).UtcDateTime;
        return new object?[] { userId, reader.IsDBNull(0) ? UserScopedId(userId, 4001) : reader.GetInt64(0), birthDate };
    }

    static async Task<List<object?[]>> ReadPartiesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, display_index, name, party_type as leader_position
            from user_party_groups
            where "userId" = $1
            order by display_index
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[] { reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3) });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadPartySlotsAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, party_id, slot_number, character_card_id, poster_id, accessory_id, position
            from user_party_slots
            where "userId" = $1
            order by party_id, slot_number
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                GetNullableInt64(reader, 3),
                GetNullableInt64(reader, 4),
                GetNullableInt64(reader, 5),
                reader.GetInt32(6)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadMissionsAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, is_cleared, is_received, progress, completed_at, mission_from, mission_to
            from user_mission_statuses
            where "userId" = $1
            order by mission_from, mission_to
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetInt32(3),
                GetNullableDateTime(reader, 4),
                reader.GetInt32(5),
                reader.GetInt32(6)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadMusicBookmarksAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select music_master_id, bookmark_state
            from user_music_bookmarks
            where "userId" = $1
            order by music_master_id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt32(1)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadGameHintReadsAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, category, is_read
            from user_game_hint_reads
            where "userId" = $1
            order by category
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetBoolean(2)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadShopLastViewsAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, shop_master_id, last_viewed_at, shop_category
            from user_shop_last_views
            where "userId" = $1
            order by shop_category, shop_master_id nulls first
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                GetNullableInt64(reader, 1),
                reader.GetDateTime(2),
                reader.GetInt32(3)
            });
        }
        return result;
    }

    static async Task<List<object?[]>> ReadEpisodeReadStatesAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select episode_master_id, has_read_all
            from user_episode_read_states
            where "userId" = $1
            order by episode_master_id
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetBoolean(1)
            });
        }
        return result;
    }

    static async Task<object?[]> ReadRestrictionAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select restricted_until, reason
            from user_restriction_states
            where "userId" = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new object?[] { userId, null, null };
        return new object?[] { userId, GetNullableDateTime(reader, 0), null };
    }

    static async Task<List<object?[]>> ReadAlbumsAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select album_index, name, display_order
            from user_album_groups
            where "userId" = $1
            order by display_order
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new object?[] { reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2) });
        }
        return result;
    }

    static async Task<object?[]> ReadInviteStateAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select invited_count, invite_code, is_reward_received
            from user_invite_states
            where "userId" = $1
            """,
            connection);
        command.Parameters.AddWithValue(userId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return BuildInviteCode(userId);
        return new object?[] { reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2) };
    }

    async Task<long?> GetCurrentUserIdAsync(HttpContext context)
    {
        return await RequireAuthenticatedUserAsync(context);
    }

    static async Task<string?> ReadUserNameAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long userId)
    {
        await using var command = new NpgsqlCommand("select name from user_accounts where id = $1", connection, transaction);
        command.Parameters.AddWithValue(userId);
        return await command.ExecuteScalarAsync() as string;
    }

    async Task<string> ReadPublicIdAsync(long userId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("select public_id from user_accounts where id = $1", connection);
        command.Parameters.AddWithValue(userId);
        return await command.ExecuteScalarAsync() as string ?? FormatNumericId(userId);
    }

    static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }
        await command.ExecuteNonQueryAsync();
    }

    static int? ExtractFirstInt(object? value)
    {
        return value switch
        {
            null => null,
            int number => number,
            long number => checked((int)number),
            short number => number,
            byte number => number,
            object?[] array => array.Select(ExtractFirstInt).FirstOrDefault(number => number is not null),
            IEnumerable<object?> values => values.Select(ExtractFirstInt).FirstOrDefault(number => number is not null),
            _ => null
        };
    }

    static int? ReadIntQuery(HttpContext context, string name)
    {
        var value = context.Request.Query[name].FirstOrDefault();
        return int.TryParse(value, out var result) ? result : null;
    }

    static int? GetInt(object?[] values, int index)
    {
        if (index >= values.Length) return null;
        return values[index] switch
        {
            int value => value,
            long value => checked((int)value),
            short value => value,
            byte value => value,
            string text when int.TryParse(text, out var value) => value,
            _ => null
        };
    }

    static DateTime? ExtractFirstDateTime(object? value)
    {
        return value switch
        {
            null => null,
            DateTime dateTime => dateTime,
            string text when DateTime.TryParse(text, out var parsed) => parsed,
            long seconds when seconds > 0 => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime,
            int seconds when seconds > 0 => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime,
            double seconds when seconds > 0 => DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime,
            object?[] array => array.Select(ExtractFirstDateTime).FirstOrDefault(dateTime => dateTime is not null),
            IEnumerable<object?> values => values.Select(ExtractFirstDateTime).FirstOrDefault(dateTime => dateTime is not null),
            _ => null
        };
    }

    static int NormalizeTutorialStatus(int tutorialStatus)
    {
        return tutorialStatus is >= 0 and <= 6 or 99 ? tutorialStatus : 99;
    }

    static long? GetNullableInt64(NpgsqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    static double? GetNullableDouble(NpgsqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    static DateTime? GetNullableDateTime(NpgsqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    static string? GetString(object?[] values, int index)
    {
        return index < values.Length ? values[index] as string : null;
    }

    static long? GetLong(object?[] values, int index)
    {
        if (index >= values.Length) return null;
        return values[index] switch
        {
            long value => value,
            int value => value,
            uint value => value,
            short value => value,
            ushort value => value,
            byte value => value,
            sbyte value => value,
            string text when long.TryParse(text, out var value) => value,
            _ => null
        };
    }

    static long? GetNullableLong(object?[] values, int index)
    {
        return index < values.Length && values[index] is null ? null : GetLong(values, index);
    }

    static bool? GetBool(object?[] values, int index)
    {
        if (index >= values.Length) return null;
        return values[index] switch
        {
            bool value => value,
            byte value => value != 0,
            int value => value != 0,
            long value => value != 0,
            _ => null
        };
    }

    static long UserScopedId(long userId, long localId)
    {
        return userId * 1000000 + localId;
    }

    static string FormatNumericId(long userId)
    {
        return (1000000000L + (userId % 9000000000L)).ToString("D10");
    }

    static object?[] DataObject(int unionKey, object?[] value)
    {
        return new object?[] { unionKey, value };
    }

    public sealed record LoginBonusReceiveResult(object?[] Bonuses, object?[] PresentData);

    public sealed record InboxCheckPackagesResult(bool IsSuccess, object?[] PresentData);

    public sealed record InboxReceivePackagesResult(object?[] Rewards, bool HasUnread, object?[] PresentData);

    sealed record InboxPackageRecord(
        long Id,
        int ThingType,
        long MasterId,
        int Quantity,
        string Message,
        DateTime CreatedAt,
        DateTime? ExpiresAt);

    /// <summary>读取玩家已拥有的称号（名刺）。union key 45。</summary>
    async Task<List<object?[]>> ReadNameplatesAsync(long userId)
    {
        var list = new List<object?[]>();
        try
        {
            await using var connection = await database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                @"select id, ""namePlateMasterId"" from nameplate where ""userId"" = $1 order by ""id""",
                connection);
            command.Parameters.AddWithValue(userId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new object?[] { reader.GetInt64(0), reader.GetInt64(1) });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[nameplate] " + ex.Message);
        }
        Console.Error.WriteLine("[nameplate] user=" + userId + " rows=" + list.Count);
        return list;
    }

    /// <summary>读取称号图鉴（NameplateMaster）。union key 58。</summary>
    async Task<List<object?[]>> ReadNameplateMastersAsync(long userId)
    {
        var list = new List<object?[]>();
        try
        {
            await using var connection = await database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                @"select id, name, description, ""order"", hidden, ""isDefault"", details, ""unlockText"", ""changeType"", ""changeValue1"", ""changeValue2""
                  from nameplate_master where ""userId"" = $1 order by ""order""",
                connection);
            command.Parameters.AddWithValue(userId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // Details 是 NameplateDetailMaster[]，用数据库里存的真实结构
                var detailsJson2 = reader.IsDBNull(6) ? null : reader.GetString(6);
                object? details = Array.Empty<object?>();
                if (detailsJson2 is not null)
                {
                    using var doc2 = System.Text.Json.JsonDocument.Parse(detailsJson2);
                    details = FromJsonElement(doc2.RootElement);
                }
                list.Add(new object?[]
                {
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5),
                    details,
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt32(8),
                    reader.IsDBNull(9) ? (long?)null : reader.GetInt64(9),
                    reader.IsDBNull(10) ? (long?)null : reader.GetInt64(10)
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[nameplate-master] " + ex.Message);
        }
        Console.Error.WriteLine("[nameplate-master] user=" + userId + " rows=" + list.Count);
        return list;
    }

    /// <summary>把 JsonElement 递归还原成普通对象数组，避免 MessagePack 把它序列化成字符串。</summary>
    static object? FromJsonElement(System.Text.Json.JsonElement e)
    {
        switch (e.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in e.EnumerateArray()) list.Add(FromJsonElement(item));
                return list.ToArray();
            case System.Text.Json.JsonValueKind.Object:
                var map = new Dictionary<string, object?>();
                foreach (var p in e.EnumerateObject()) map[p.Name] = FromJsonElement(p.Value);
                return map;
            case System.Text.Json.JsonValueKind.Number:
                if (e.TryGetInt64(out var l)) return l;
                return e.GetDouble();
            case System.Text.Json.JsonValueKind.String:
                return e.GetString();
            case System.Text.Json.JsonValueKind.True:
                return true;
            case System.Text.Json.JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

}


