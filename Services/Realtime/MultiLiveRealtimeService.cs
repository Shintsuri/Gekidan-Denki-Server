using System.Collections.Concurrent;
using SiriusLocalServer.Realtime;

sealed partial class MultiLiveRealtimeService
{
    readonly ConcurrentDictionary<string, MultiLiveRoomState> roomsByHallId = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<long, string> hallIdByMultiLiveId = new();
    readonly ConcurrentDictionary<string, string> hallIdByHashUserId = new(StringComparer.Ordinal);
    static long nextMultiLiveId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    readonly LocalRequestLogger logger;
    internal object SyncRoot { get; } = new();

    public MultiLiveRealtimeService(LocalRequestLogger logger)
    {
        this.logger = logger;
    }

    // 公开房等待倒计时：满 60 秒不管人够不够都开演（开服时的做法）。
    // 客户端靠 OnCountDownDecideMember(剩余秒数) 显示倒计时，所以每秒推送一次。
    sealed class RoomCountdown
    {
        public Timer Timer = null!;
        public int Remaining;
    }

    readonly ConcurrentDictionary<string, RoomCountdown> publicRoomTimers = new(StringComparer.Ordinal);
    static readonly TimeSpan PublicRoomWait = TimeSpan.FromSeconds(60);

    /// <summary>公开房开始 60 秒倒计时；到点后即使不满 4 人也强制开演。</summary>
    void StartPublicRoomCountdown(MultiLiveRoomState room)
    {
        if (room.IsPrivate) return;
        room.RestrictionFinishedAt = DateTime.UtcNow + PublicRoomWait;
        var countdown = new RoomCountdown { Remaining = (int)PublicRoomWait.TotalSeconds };
        countdown.Timer = new Timer(_ => TickPublicRoomCountdown(room.HallId), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(1));
        if (publicRoomTimers.TryRemove(room.HallId, out var old)) old.Timer.Dispose();
        publicRoomTimers[room.HallId] = countdown;
        _ = logger.LogAsync($"公开房倒计时开始 hallId={room.HallId} members={room.Users.Count} wait=60s");
    }

    /// <summary>每秒推送剩余秒数给客户端显示倒计时；到 0 就强制开演。</summary>
    void TickPublicRoomCountdown(string hallId)
    {
        lock (SyncRoot)
        {
            if (!publicRoomTimers.TryGetValue(hallId, out var countdown)) return;
            if (!roomsByHallId.TryGetValue(hallId, out var room))
            {
                CancelPublicRoomCountdown(hallId);
                return;
            }
            // 人掉到 1 个以下就撤销倒计时，等第 2 个人再进来重新计时
            if (room.Users.Count < 2 || room.Status >= MultiLiveHallStatus.ReadyGame)
            {
                CancelPublicRoomCountdown(hallId);
                room.RestrictionFinishedAt = null;
                return;
            }
            countdown.Remaining--;
            var remaining = Math.Max(0, countdown.Remaining);
            // 客户端据这个数字显示倒计时
            Broadcast(room, "OnCountDownDecideMember", (object?)(int?)remaining);
            if (remaining <= 0)
            {
                CancelPublicRoomCountdown(hallId);
                ForceStartPublicRoom(hallId);
            }
        }
    }

    void CancelPublicRoomCountdown(string hallId)
    {
        if (publicRoomTimers.TryRemove(hallId, out var countdown)) countdown.Timer.Dispose();
    }

    /// <summary>倒计时到点：人不够也开，直接把所有成员置为准备并推进状态机。</summary>
    void ForceStartPublicRoom(string hallId)
    {
        lock (SyncRoot)
        {
            if (!roomsByHallId.TryGetValue(hallId, out var room)) return;
            if (room.Status >= MultiLiveHallStatus.ReadyGame) return;
            if (room.Users.Count == 0) return;
            // 还没「成员决定」的话先替主机做掉这一步
            if (room.Status == MultiLiveHallStatus.Recruiting)
            {
                room.Status = MultiLiveHallStatus.StandbyGame;
                Broadcast(room, "OnCountDownDecideMember", (object?)(int?)0);
                Broadcast(room, "OnReadyGroup", room.IsPrivate);
            }
            // 人不够也开：所有成员直接置为准备
            foreach (var member in room.Users.Values)
                if (member.Status < MultiLiveUserStatus.ReadyGame)
                    member.Status = MultiLiveUserStatus.ReadyGame;
            // 倒计时结束 → 清掉结束时间，客户端据此关闭倒计时对话框并进入开演
            room.RestrictionFinishedAt = null;
            Broadcast(room, "OnForcedStart", new object?[] { room.Users.Count });
            AdvanceGame(room);
            _ = logger.LogAsync($"公开房倒计时到点强制开演 hallId={hallId} members={room.Users.Count} status={room.Status}");
        }
    }

    /// <summary>
    /// 选完难度后的开演判断：全部人选完难度就立刻开；否则 10 秒倒计时后强开。
    /// </summary>
    internal void AfterDifficultySelected(MultiLiveRoomState room)
    {
        if (room.Status >= MultiLiveHallStatus.ReadyGame) return;
        var allSelected = room.Users.Values.All(u => u.Status >= MultiLiveUserStatus.SelectedDifficulty);
        if (allSelected)
        {
            CancelReadyCountdown(room.HallId);
            ForceReadyStart(room);
            return;
        }
        StartReadyCountdown(room);
    }

    // 未全准备时的 10 秒倒计时
    readonly ConcurrentDictionary<string, RoomCountdown> readyCountdowns = new(StringComparer.Ordinal);
    static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(10);

    void StartReadyCountdown(MultiLiveRoomState room)
    {
        if (readyCountdowns.ContainsKey(room.HallId)) return;
        var countdown = new RoomCountdown { Remaining = (int)ReadyWait.TotalSeconds };
        countdown.Timer = new Timer(_ => TickReadyCountdown(room.HallId), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(1));
        readyCountdowns[room.HallId] = countdown;
        _ = logger.LogAsync($"开演前倒计时开始 hallId={room.HallId} wait=10s");
    }

    void TickReadyCountdown(string hallId)
    {
        lock (SyncRoot)
        {
            if (!readyCountdowns.TryGetValue(hallId, out var countdown)) return;
            if (!roomsByHallId.TryGetValue(hallId, out var room))
            {
                CancelReadyCountdown(hallId);
                return;
            }
            if (room.Status >= MultiLiveHallStatus.ReadyGame)
            {
                CancelReadyCountdown(hallId);
                return;
            }
            // 中途所有人都选完了 → 立刻开，不用等满 10 秒
            if (room.Users.Values.All(u => u.Status >= MultiLiveUserStatus.SelectedDifficulty))
            {
                CancelReadyCountdown(hallId);
                ForceReadyStart(room);
                return;
            }
            countdown.Remaining--;
            var remaining = Math.Max(0, countdown.Remaining);
            Broadcast(room, "OnCountDownDecideMember", (object?)(int?)remaining);
            if (remaining <= 0)
            {
                CancelReadyCountdown(hallId);
                ForceReadyStart(room);
            }
        }
    }

    void CancelReadyCountdown(string hallId)
    {
        if (readyCountdowns.TryRemove(hallId, out var countdown)) countdown.Timer.Dispose();
    }

    /// <summary>把所有成员置为准备并推进状态机（→ OnGoGame）。</summary>
    void ForceReadyStart(MultiLiveRoomState room)
    {
        if (room.Status == MultiLiveHallStatus.Recruiting)
        {
            room.Status = MultiLiveHallStatus.StandbyGame;
            Broadcast(room, "OnReadyGroup", room.IsPrivate);
        }
        foreach (var member in room.Users.Values)
            if (member.Status < MultiLiveUserStatus.ReadyGame)
                member.Status = MultiLiveUserStatus.ReadyGame;
        Broadcast(room, "OnCountDownDecideMember", (object?)(int?)0);
        AdvanceGame(room);
        _ = logger.LogAsync($"开演判断 hallId={room.HallId} members={room.Users.Count} status={room.Status}");
    }

    /// <summary>
    /// HTTP 开演（Lives/StartMultiLive）成功后调用：把房间推到 PlayingGame 并广播 OnPlayGame。
    /// 否则客户端会一直停在帷幕的「他のプレイヤーを待っています」。
    /// </summary>
    public void NotifyGameStarted(string identity)
    {
        lock (SyncRoot)
        {
            if (!TryGetUser(identity, out var room, out _)) return;
            if (room.Status == MultiLiveHallStatus.PlayingGame) return;
            foreach (var member in room.Users.Values)
                if (member.Status < MultiLiveUserStatus.PlayingGame)
                    member.Status = MultiLiveUserStatus.PlayingGame;
            room.Status = MultiLiveHallStatus.PlayingGame;
            Broadcast(room, "OnPlayGame", null);
            _ = logger.LogAsync($"多人开演广播 hallId={room.HallId} members={room.Users.Count}");
        }
    }

    /// <summary>
    /// HTTP 搜索页用的真实房间列表。没有房间时返回空数组（不再返回假房间，
    /// 否则客户端点进去会用不存在的 HallId 去连大厅，必然失败）。
    /// 行结构与客户端既有渲染保持一致（11 个字段）。
    /// </summary>
    public object?[] SearchPublicRooms(long liveSettingMasterId)
    {
        return roomsByHallId.Values
            .Where(r => !r.IsPrivate && r.Status == MultiLiveHallStatus.Recruiting)
            .Where(r => liveSettingMasterId <= 0 || r.LiveSettingMasterId == liveSettingMasterId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .Select(r => (object?)new object?[]
            {
                r.HallId,
                "ルーム " + r.HallId,
                0,
                r.Users.Count,
                Math.Max(4, r.Users.Count),
                r.CreatedAt,
                r.CreatedAt.AddHours(1),
                r.MusicMasterId,
                r.LiveSettingMasterId,
                0L,
                false
            })
            .ToArray();
    }

    public Task<MultiLiveCreatePrivateHallResult> CreatePrivateHallAsync(
        string hashUserId,
        MultiLiveHallType hallType,
        long liveSettingMasterId,
        string? circleId,
        string userName,
        long userNamePlateColorId,
        MultiLiveCharacter? leaderCharacter)
    {
        lock (SyncRoot)
        {
            var room = CreateRoom(liveSettingMasterId, hallType, isPrivate: true);
            var user = CreateUser(1, hashUserId, userName, userNamePlateColorId, leaderCharacter);
            room.Users[user.MemberId] = user;
            RegisterRoom(room, hashUserId);

            return Task.FromResult(new MultiLiveCreatePrivateHallResult
            {
                IsSucceeded = true,
                HallId = room.HallId,
                MemberId = user.MemberId,
                PrivateHallKeyCode = room.KeyCode
            });
        }
    }

    public Task<MultiLiveJoinResult> JoinPublicHallAsync(
        string hashUserId,
        MultiLiveHallType hallType,
        long liveSettingMasterId,
        string? circleId,
        string userName,
        long userNamePlateColorId,
        MultiLiveCharacter? leaderCharacter)
    {
        lock (SyncRoot)
        {
            var room = roomsByHallId.Values.OrderByDescending(x => x.Users.Values.Any(u => u.HashUserId == hashUserId)).FirstOrDefault(x =>
                !x.IsPrivate &&
                x.HallType == hallType &&
                x.LiveSettingMasterId == liveSettingMasterId &&
                (x.Users.Count < 4 || x.Users.Values.Any(u => u.HashUserId == hashUserId)) &&
                x.Status is MultiLiveHallStatus.Recruiting or MultiLiveHallStatus.StandbyGame);

            if (room is null)
            {
                room = CreateRoom(liveSettingMasterId, hallType, isPrivate: false);
                RegisterRoom(room, hashUserId);
            }

            var existing = room.Users.Values.FirstOrDefault(x => x.HashUserId == hashUserId);
            if (existing is not null)
            {
                hallIdByHashUserId[hashUserId] = room.HallId;
                return Task.FromResult(WithRestriction(MultiLiveJoinResult.Success(room.HallId, existing.MemberId, room.KeyCode, room.LiveSettingMasterId), room));
            }

            var memberId = NextMemberId(room);
            if (memberId > 4)
            {
                return Task.FromResult(MultiLiveJoinResult.Error(MultiLiveJoinErrorCodes.ReachedMaxMember));
            }

            var user = CreateUser(memberId, hashUserId, userName, userNamePlateColorId, leaderCharacter);
            room.Users[user.MemberId] = user;
            hallIdByHashUserId[hashUserId] = room.HallId;

            return Task.FromResult(WithRestriction(MultiLiveJoinResult.Success(room.HallId, user.MemberId, room.KeyCode, room.LiveSettingMasterId), room));
        }
    }

    public Task<MultiLiveJoinResult> JoinPrivateHallWithKeyCodeAsync(
        string hashUserId,
        string? circleId,
        int keyCode,
        string userName,
        long userNamePlateColorId,
        MultiLiveCharacter? leaderCharacter)
    {
        lock (SyncRoot)
        {
            var room = roomsByHallId.Values.FirstOrDefault(x => x.IsPrivate && x.KeyCode == keyCode);
            return room is null
                ? Task.FromResult(MultiLiveJoinResult.Error(MultiLiveJoinErrorCodes.NotFoundHall))
                : JoinRoomAsync(room, hashUserId, userName, userNamePlateColorId, leaderCharacter);
        }
    }

    public Task<MultiLiveJoinResult> JoinPrivateHallFromInviteAsync(
        string hashUserId,
        string? circleId,
        string hallId,
        string userName,
        long userNamePlateColorId,
        MultiLiveCharacter? leaderCharacter)
    {
        lock (SyncRoot)
        {
            return roomsByHallId.TryGetValue(hallId, out var room)
                ? JoinRoomAsync(room, hashUserId, userName, userNamePlateColorId, leaderCharacter)
                : Task.FromResult(MultiLiveJoinResult.Error(MultiLiveJoinErrorCodes.NotFoundHall));
        }
    }

    public Task<MultiLiveFetchUsersResult> FetchUsersAsync(string hashUserId)
    {
        lock (SyncRoot)
        {
            return Task.FromResult(GetRoomForUser(hashUserId)?.ToFetchUsersResult() ?? EmptyFetchUsers());
        }
    }

    public Task SelectMusicAsync(string hashUserId, long musicId, bool isRandom, bool isAFK)
    {
        lock (SyncRoot)
        {
            if (TryGetUser(hashUserId, out _, out var user))
            {
                user.SelectedMusicId = musicId;
                user.IsRandomMusic = isRandom;
                user.IsAFK = isAFK;
                user.IsSelectedMusic = true;
                user.Status = MultiLiveUserStatus.SelectedMusic;
            }

            return Task.CompletedTask;
        }
    }

    public Task SelectDifficultyAsync(string hashUserId, MusicDifficulties difficulty, bool isAFK)
    {
        lock (SyncRoot)
        {
            if (TryGetUser(hashUserId, out _, out var user))
            {
                user.Difficulty = difficulty;
                user.IsAFK = isAFK;
                user.Status = MultiLiveUserStatus.SelectedDifficulty;
            }

            return Task.CompletedTask;
        }
    }

    public Task SelectStampAsync(string hashUserId, long stampId)
    {
        lock (SyncRoot)
        {
            return logger.LogAsync($"realtime-multi-live-stamp hashUserId={hashUserId} stampId={stampId}");
        }
    }

    public Task ReadyGameAsync(string hashUserId)
    {
        lock (SyncRoot)
        {
            if (TryGetUser(hashUserId, out var room, out var user))
            {
                user.Status = MultiLiveUserStatus.ReadyGame;
                foreach (var npc in room.Users.Values.Where(x => x.HashUserId.StartsWith("npc-", StringComparison.Ordinal)))
                {
                    npc.Status = MultiLiveUserStatus.ReadyGame;
                }

                room.Status = MultiLiveHallStatus.ReadyGame;
            }

            return Task.CompletedTask;
        }
    }

    public Task BeforeGameCalculateAsync(string hashUserId)
    {
        lock (SyncRoot)
        {
            if (TryGetUser(hashUserId, out var room, out var user))
            {
                user.Status = MultiLiveUserStatus.BeforeGameCalculate;
                foreach (var npc in room.Users.Values.Where(x => x.HashUserId.StartsWith("npc-", StringComparison.Ordinal)))
                {
                    npc.Status = MultiLiveUserStatus.BeforeGameCalculate;
                }

                room.Status = MultiLiveHallStatus.BeforeGameCalculate;
            }

            return Task.CompletedTask;
        }
    }

    public Task StartGameAsync(string hashUserId)
    {
        lock (SyncRoot)
        {
            if (TryGetUser(hashUserId, out var room, out var user))
            {
                user.Status = MultiLiveUserStatus.PlayingGame;
                foreach (var npc in room.Users.Values.Where(x => x.HashUserId.StartsWith("npc-", StringComparison.Ordinal)))
                {
                    npc.Status = MultiLiveUserStatus.PlayingGame;
                }

                room.Status = MultiLiveHallStatus.PlayingGame;
            }

            return Task.CompletedTask;
        }
    }

    public Task SyncInGameStatusAsync(string hashUserId, int comboCount, ComboTypes comboType, int life)
    {
        lock (SyncRoot)
        {
            if (TryGetUser(hashUserId, out _, out var user))
            {
                user.ComboCount = comboCount;
                user.ComboType = comboType;
                user.Life = life;
            }

            return Task.CompletedTask;
        }
    }

    public Task ExitGameAsync(
        string hashUserId,
        long score,
        ClearLamps clearLamp,
        IReadOnlyDictionary<int, int>? timingCounts,
        int maxCombo)
    {
        lock (SyncRoot)
        {
            if (TryGetUser(hashUserId, out var room, out var user))
            {
                user.Score = score;
                user.ClearLamp = clearLamp;
                user.MaxCombo = maxCombo;
                user.IsExitGame = true;
                user.Status = MultiLiveUserStatus.ExitGame;
                user.TimingCounts.Clear();

                if (timingCounts is not null)
                {
                    foreach (var pair in timingCounts)
                    {
                        user.TimingCounts[pair.Key] = pair.Value;
                    }
                }

                foreach (var npc in room.Users.Values.Where(x => x.HashUserId.StartsWith("npc-", StringComparison.Ordinal)))
                {
                    npc.Score = Math.Max(1, score - Random.Shared.Next(10000, 80000));
                    npc.ClearLamp = ClearLamps.Clear;
                    npc.MaxCombo = Math.Max(1, maxCombo - Random.Shared.Next(20, 180));
                    npc.IsExitGame = true;
                    npc.Status = MultiLiveUserStatus.ExitGame;
                }
            }

            return Task.CompletedTask;
        }
    }

    public Task EntryFinalResultAsync(string hashUserId)
    {
        lock (SyncRoot)
        {
            if (TryGetUser(hashUserId, out var room, out _))
            {
                room.Status = MultiLiveHallStatus.None;
            }

            return Task.CompletedTask;
        }
    }

    public Task ContinuePlayAsync(string hashUserId)
    {
        lock (SyncRoot)
        {
            if (TryGetUser(hashUserId, out var room, out var user))
            {
                user.IsExitGame = false;
                user.Status = MultiLiveUserStatus.Joined;
                room.Status = MultiLiveHallStatus.StandbyGame;
            }

            return Task.CompletedTask;
        }
    }

    public object?[] GetMultiLiveInformation(long multiLiveId)
    {
        lock (SyncRoot)
        {
            if (hallIdByMultiLiveId.TryGetValue(multiLiveId, out var hallId) &&
                roomsByHallId.TryGetValue(hallId, out var room))
            {
                var map = new Dictionary<string, object?>();
                foreach (var user in room.Users.Values.Where(x => x.Status >= MultiLiveUserStatus.PlayingGame))
                {
                    var key = Math.Max(user.MaxCombo, user.MemberId * 13).ToString();
                    if (!map.TryGetValue(key, out var bucket) || bucket is not List<object?> list)
                    {
                        list = new List<object?>();
                        map[key] = list;
                    }

                    list.Add(new object?[] { StableIntId(user.HashUserId, 10000000, 99999999), user.HashUserId, int.Parse(key) });
                }

                return new object?[] { map.ToDictionary(x => x.Key, x => (object?)((List<object?>)x.Value!).ToArray()) };
            }

            return Array.Empty<object?>();
        }
    }

    public MultiLiveRoomState? GetRoomByMultiLiveId(long multiLiveId)
    {
        lock (SyncRoot)
        {
            return hallIdByMultiLiveId.TryGetValue(multiLiveId, out var hallId) &&
                   roomsByHallId.TryGetValue(hallId, out var room)
                ? room
                : null;
        }
    }

    public MultiLiveRoomState? GetRoomForUser(string hashUserId)
    {
        lock (SyncRoot)
        {
            return hallIdByHashUserId.TryGetValue(hashUserId, out var hallId) &&
                   roomsByHallId.TryGetValue(hallId, out var room)
                ? room
                : null;
        }
    }

    public IReadOnlyCollection<MultiLiveRoomState> SnapshotRooms()
    {
        lock (SyncRoot)
        {
            return roomsByHallId.Values.OrderByDescending(x => x.CreatedAt).ToArray();
        }
    }

    Task<MultiLiveJoinResult> JoinRoomAsync(
        MultiLiveRoomState room,
        string hashUserId,
        string userName,
        long userNamePlateColorId,
        MultiLiveCharacter? leaderCharacter)
    {
        if (room.Users.Values.Any(x => x.HashUserId == hashUserId))
        {
            var existing = room.Users.Values.First(x => x.HashUserId == hashUserId);
            return Task.FromResult(WithRestriction(MultiLiveJoinResult.Success(room.HallId, existing.MemberId, room.KeyCode, room.LiveSettingMasterId), room));
        }

        var memberId = NextMemberId(room);
        if (memberId > 4)
        {
            return Task.FromResult(MultiLiveJoinResult.Error(MultiLiveJoinErrorCodes.ReachedMaxMember));
        }

        var user = CreateUser(memberId, hashUserId, userName, userNamePlateColorId, leaderCharacter);
        room.Users[user.MemberId] = user;
        hallIdByHashUserId[hashUserId] = room.HallId;
        // 公开房要等第 2 个人进来才开始 60 秒倒计时（只有 1 个人时不计时）。
        if (!room.IsPrivate && room.Users.Count >= 2 && !publicRoomTimers.ContainsKey(room.HallId))
            StartPublicRoomCountdown(room);
        return Task.FromResult(WithRestriction(MultiLiveJoinResult.Success(room.HallId, memberId, room.KeyCode, room.LiveSettingMasterId), room));
    }


    /// <summary>把房间的倒计时结束时间带到返回结果里，客户端据此显示倒计时。</summary>
    static MultiLiveJoinResult WithRestriction(MultiLiveJoinResult result, MultiLiveRoomState room)
    {
        result.RestrictionFinishedAt = room.RestrictionFinishedAt;
        return result;
    }

    MultiLiveRoomState CreateRoom(long liveSettingMasterId, MultiLiveHallType hallType, bool isPrivate)
    {
        var multiLiveId = Interlocked.Increment(ref nextMultiLiveId);
        int keyCode;
        do { keyCode = Random.Shared.Next(100000, 1000000); }
        while (roomsByHallId.Values.Any(room => room.KeyCode == keyCode));

        return new MultiLiveRoomState
        {
            HallId = Guid.NewGuid().ToString("N"),
            KeyCode = keyCode,
            MultiLiveId = multiLiveId,
            LiveSettingMasterId = liveSettingMasterId,
            HallType = hallType,
            IsPrivate = isPrivate,
            Status = MultiLiveHallStatus.Recruiting
        };
    }

    void RegisterRoom(MultiLiveRoomState room, string hashUserId)
    {
        roomsByHallId[room.HallId] = room;
        hallIdByMultiLiveId[room.MultiLiveId] = room.HallId;
        hallIdByHashUserId[hashUserId] = room.HallId;
        // 倒计时不在这里开始：公开房要等第 2 个人进来才计时。
    }

    static MultiLiveUser CreateUser(
        int memberId,
        string hashUserId,
        string userName,
        long userNamePlateColorId,
        MultiLiveCharacter? leaderCharacter)
    {
        return new MultiLiveUser
        {
            MemberId = memberId,
            HashUserId = string.IsNullOrWhiteSpace(hashUserId) ? $"local-user-{memberId}" : hashUserId,
            UserName = string.IsNullOrWhiteSpace(userName) ? "LocalPlayer" : userName,
            UserNamePlateColorId = userNamePlateColorId,
            LeaderCharacter = leaderCharacter ?? MultiLiveCharacter.Default(110040),
            Difficulty = MusicDifficulties.Extra,
            Status = MultiLiveUserStatus.Joined
        };
    }

    static int NextMemberId(MultiLiveRoomState room)
    {
        for (var i = 1; i <= 4; i++)
        {
            if (!room.Users.ContainsKey(i)) return i;
        }

        return 5;
    }

    bool TryGetUser(string hashUserId, out MultiLiveRoomState room, out MultiLiveUser user)
    {
        room = null!;
        user = null!;

        var foundRoom = GetRoomForUser(hashUserId);
        if (foundRoom is null) return false;

        var foundUser = foundRoom.Users.Values.FirstOrDefault(x => x.HashUserId == hashUserId);
        if (foundUser is null) return false;

        room = foundRoom;
        user = foundUser;
        return true;
    }

    static MultiLiveFetchUsersResult EmptyFetchUsers()
    {
        return new MultiLiveFetchUsersResult
        {
            HostMemberId = 0,
            Users = Array.Empty<MultiLiveUser>(),
            CanOpenHall = false
        };
    }

    static int StableIntId(string value, int min, int max)
    {
        var hash = StringComparer.Ordinal.GetHashCode(value);
        var positive = hash == int.MinValue ? 0 : Math.Abs(hash);
        return min + positive % (max - min + 1);
    }
}
