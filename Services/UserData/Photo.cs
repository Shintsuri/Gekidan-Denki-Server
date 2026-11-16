using Npgsql;

sealed partial class UserDataService
{
    public async Task<object?[]> ArrangePhotoAlbumAsync(HttpContext context, object? payload, int arrangementType)
    {
        var userId = await GetCurrentUserIdAsync(context);
        var values = payload as object?[] ?? Array.Empty<object?>();
        var isPublic = GetBool(values, 0) ?? true;
        var albumGroupId = GetLong(values, 1) ?? 1;
        var rawLayout = values.ElementAtOrDefault(2);
        var layout = DecodePhotoAlbumLayout(rawLayout);
        var normalizedLayout = NormalizePhotoAlbumLayout(layout, arrangementType);
        var encodedLayout = MsgPack.Encode(normalizedLayout);
        var arrangementId = userId is long currentUserId
            ? UserScopedId(currentUserId, 125000 + albumGroupId * 10 + arrangementType)
            : Random.Shared.Next(300000, 399999);

        if (userId is not null)
        {
            await EnsureDefaultUserDataAsync(userId.Value);
            await using var connection = await database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                insert into user_photo_album_arrangements (
                  id, "userId", album_group_id, arrangement_type, is_public, layout_payload, updated_at
                )
                values ($1, $2, $3, $4, $5, $6, now())
                on conflict ("userId", album_group_id, arrangement_type)
                do update set is_public = excluded.is_public,
                              layout_payload = excluded.layout_payload,
                              updated_at = now()
                returning id
                """,
                connection);
            command.Parameters.AddWithValue(arrangementId);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue(albumGroupId);
            command.Parameters.AddWithValue(arrangementType);
            command.Parameters.AddWithValue(isPublic);
            command.Parameters.AddWithValue(encodedLayout);
            var returnedId = await command.ExecuteScalarAsync();
            if (returnedId is long id) arrangementId = id;
        }

        var presentData = new List<object?>
        {
            DataObject(125, new object?[]
            {
                arrangementId,
                albumGroupId,
                arrangementType,
                isPublic,
                new MsgPack.Binary(encodedLayout),
                null
            }),
            DataObject(48, new object?[] { Random.Shared.Next(126000000, 126999999), true, false, arrangementType, null, 11, 11 })
        };

        if (arrangementType == 1)
        {
            var firstPhoto = FirstPhotoId(normalizedLayout) ?? Random.Shared.Next(39000000, 39999999);
            var fileName = BuildPhotoFileName(firstPhoto);
            var sasToken = GameResults.BuildPhotoSasToken("photo");
            var thumbnailSasToken = GameResults.BuildPhotoSasToken("thumbnail");

            presentData.Add(DataObject(123, new object?[]
            {
                firstPhoto,
                fileName,
                sasToken,
                null,
                false,
                albumGroupId,
                1,
                5,
                null,
                DateTime.UtcNow,
                thumbnailSasToken,
                new object?[] { 404, 104 },
                new object?[] { 404, 104 },
                0
            }));
            presentData.Add(DataObject(124, new object?[] { UserScopedId(userId ?? 1, 124000 + albumGroupId), 1, 1, 1 }));
            presentData.Add(DataObject(48, new object?[] { Random.Shared.Next(126000000, 126999999), false, false, 4, null, 12, 12 }));
            presentData.Add(DataObject(48, new object?[] { 126984517L, false, false, 14, null, 42, 42 }));
        }

        return presentData.ToArray();
    }

    static object?[] DecodePhotoAlbumLayout(object? rawLayout)
    {
        if (rawLayout is byte[] bytes && bytes.Length > 0)
        {
            return MsgPack.Decode(bytes) as object?[] ?? Array.Empty<object?>();
        }

        return rawLayout as object?[] ?? Array.Empty<object?>();
    }

    static object?[] NormalizePhotoAlbumLayout(object?[] layout, int arrangementType)
    {
        var rows = layout.Length == 1 && layout[0] is object?[] nestedRows
            ? nestedRows
            : layout;

        var normalizedRows = rows
            .OfType<object?[]>()
            .Select(row => arrangementType == 2
                ? NormalizeDetailPhotoAlbumRow(row)
                : NormalizeSimplePhotoAlbumRow(row))
            .ToArray();

        return new object?[] { normalizedRows };
    }

    static object?[] NormalizeSimplePhotoAlbumRow(object?[] row)
    {
        var photoId = GetLong(row, 0) ?? 0;
        return new object?[]
        {
            photoId,
            BuildPhotoFileName(photoId),
            GameResults.BuildPhotoSasToken("photo"),
            GetLong(row, 3) ?? 0,
            row.ElementAtOrDefault(4)
        };
    }

    static object?[] NormalizeDetailPhotoAlbumRow(object?[] row)
    {
        var photoId = GetLong(row, 0) ?? 0;
        return new object?[]
        {
            photoId,
            GetLong(row, 1) ?? 1,
            BuildPhotoFileName(photoId),
            GameResults.BuildPhotoSasToken("photo"),
            ReadDouble(row, 4),
            ReadDouble(row, 5),
            ReadDouble(row, 6),
            ReadDouble(row, 7, 1.0),
            GetLong(row, 8) ?? 0,
            GetBool(row, 9) ?? true,
            GetBool(row, 10) ?? false,
            row.ElementAtOrDefault(11)
        };
    }

    static long? FirstPhotoId(object?[] normalizedLayout)
    {
        var rows = normalizedLayout.ElementAtOrDefault(0) as object?[];
        var firstRow = rows?.OfType<object?[]>().FirstOrDefault();
        return firstRow is null ? null : GetLong(firstRow, 0);
    }

    static double ReadDouble(object?[] values, int index, double defaultValue = 0.0)
    {
        if (index >= values.Length) return defaultValue;
        return values[index] switch
        {
            double value => value,
            float value => value,
            int value => value,
            long value => value,
            string text when double.TryParse(text, out var value) => value,
            _ => defaultValue
        };
    }

    static string BuildPhotoFileName(long photoId)
    {
        return photoId > 0
            ? $"{photoId:x8}-local-photo.jpg"
            : $"{Guid.NewGuid():D}.jpg";
    }

    /// <summary>拍照时写入 user_photo_records，相册才有真实记录。</summary>
    public async Task<long> CreatePhotoRecordAsync(HttpContext context, long itemMasterId)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null) return 0L;
        await EnsureDefaultUserDataAsync(userId.Value);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var photoId = UserScopedId(userId.Value, Random.Shared.Next(39000000, 39999999));
        var fileName = $"{Guid.NewGuid():D}.jpg";
        // payload 里存等级/稀有度/属性/角色，客户端 Photo 的 3/6/7/11/12 都从这里取。
        var payload = $"{{\"level\":1,\"rarity\":5,\"photoEffectMasterId\":null,\"appeared\":[],\"tagged\":[],\"useDecoPage\":0}}";
        await ExecuteAsync(connection, transaction, """
            insert into user_photo_records (id, "userId", file_name, read_sas_token, is_favorite, source_type, source_master_id, payload)
            values ($1, $2, $3, $4, false, 1, $5, $6::jsonb)
            """, photoId, userId.Value, fileName, GameResults.BuildPhotoSasToken("photo"), itemMasterId, payload);
        await transaction.CommitAsync();
        return photoId;
    }

    public async Task<string?> ReadPhotoFileNameAsync(HttpContext context, long photoId)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null) return null;
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select file_name from user_photo_records where id = $1 and \"userId\" = $2", connection);
        command.Parameters.AddWithValue(photoId);
        command.Parameters.AddWithValue(userId.Value);
        return await command.ExecuteScalarAsync() as string;
    }

    /// <summary>照片强化：按 PhotoLevelUpItemGroupMaster 扣素材并提升等级。</summary>
    public async Task<object?[]?> LevelUpPhotoAsync(HttpContext context, long photoId)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null || photoId <= 0) return null;
        await EnsureDefaultUserDataAsync(userId.Value);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockLessonUserAsync(connection, transaction, userId.Value);

        int level, rarity;
        await using (var read = new NpgsqlCommand(
            """
            select coalesce((payload->>'level')::int, 1), coalesce((payload->>'rarity')::int, 5)
            from user_photo_records where id = $1 and "userId" = $2 for update
            """, connection, transaction))
        {
            read.Parameters.AddWithValue(photoId);
            read.Parameters.AddWithValue(userId.Value);
            await using var reader = await read.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            level = reader.GetInt32(0);
            rarity = reader.GetInt32(1);
        }

        var step = PhotoProgressionRules.Next(rarity, level);
        if (step is null) return new object?[] { photoId, level, level };

        foreach (var material in step.Materials)
        {
            await using var consume = new NpgsqlCommand(
                """
                update user_item_possessions set quantity = quantity - $3, updated_at = now()
                where "userId" = $1 and item_master_id = $2 and quantity >= $3
                """, connection, transaction);
            consume.Parameters.AddWithValue(userId.Value);
            consume.Parameters.AddWithValue(material.ItemMasterId);
            consume.Parameters.AddWithValue(material.Quantity);
            if (await consume.ExecuteNonQueryAsync() != 1)
                throw new BadHttpRequestException("PHOTO_MATERIAL_INSUFFICIENT", StatusCodes.Status409Conflict);
        }

        var after = step.ToLevel;
        await using (var update = new NpgsqlCommand(
            """
            update user_photo_records
            set payload = payload || jsonb_build_object('level', $3), updated_at = now()
            where id = $1 and "userId" = $2
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(photoId);
            update.Parameters.AddWithValue(userId.Value);
            update.Parameters.AddWithValue(after);
            await update.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return new object?[] { photoId, level, after };
    }

    /// <summary>照片按等级/稀有度折算出售价。</summary>
    public async Task<object?[]> SellPhotosPricedAsync(HttpContext context, object? payload, List<long> ids)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null || ids.Count == 0) return Array.Empty<object?>();
        await EnsureDefaultUserDataAsync(userId.Value);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        long total = 0;
        await using (var read = new NpgsqlCommand(
            """
            select id, coalesce((payload->>'level')::int, 1), coalesce((payload->>'rarity')::int, 5)
            from user_photo_records where "userId" = $1 and id = any($2)
            """, connection, transaction))
        {
            read.Parameters.AddWithValue(userId.Value);
            read.Parameters.AddWithValue(ids.ToArray());
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var level = reader.GetInt32(1);
                var rarity = reader.GetInt32(2);
                total += 500 + (long)level * 100 + (long)rarity * 200;
            }
        }
        if (total <= 0) return Array.Empty<object?>();

        await using (var delete = new NpgsqlCommand(
            "delete from user_photo_records where \"userId\" = $1 and id = any($2)", connection, transaction))
        {
            delete.Parameters.AddWithValue(userId.Value);
            delete.Parameters.AddWithValue(ids.ToArray());
            await delete.ExecuteNonQueryAsync();
        }
        await AddCurrencyIncrementAsync(connection, transaction, userId.Value, "coin", (int)total);
        await transaction.CommitAsync();
        return new object?[]
        {
            DataObject(0, await ReadUserAsync(connection, userId.Value)),
            DataObject(128, await ReadCurrencyAsync(connection, userId.Value))
        };
    }

    /// <summary>批量生成照片（一次多张），每张都写库并返回 Photo 记录。</summary>
    public async Task<object?[]> GeneratePhotoRecordsAsync(HttpContext context, long itemMasterId, int count)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null) return Array.Empty<object?>();
        count = Math.Clamp(count, 1, 10);
        var created = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var id = await CreatePhotoRecordAsync(context, itemMasterId);
            if (id > 0) created.Add(id);
        }
        if (created.Count == 0) return Array.Empty<object?>();
        await using var connection = await database.OpenConnectionAsync();
        var records = await ReadPhotoRecordsAsync(connection, userId.Value);
        var ids = created.ToHashSet();
        return records.Where(r => ids.Contains(Convert.ToInt64(r[0]))).Cast<object?>().ToArray();
    }

    /// <summary>重新生成一张照片：删掉旧的，生成新的同数量记录。</summary>
    public async Task<object?[]> RegeneratePhotoAsync(HttpContext context, long photoId, long itemMasterId)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null) return Array.Empty<object?>();
        await using var connection = await database.OpenConnectionAsync();
        await using (var del = new NpgsqlCommand(
            "delete from user_photo_records where id = $1 and \"userId\" = $2", connection))
        {
            del.Parameters.AddWithValue(photoId);
            del.Parameters.AddWithValue(userId.Value);
            await del.ExecuteNonQueryAsync();
        }
        var newId = await CreatePhotoRecordAsync(context, itemMasterId);
        if (newId <= 0) return Array.Empty<object?>();
        var records = await ReadPhotoRecordsAsync(connection, userId.Value);
        return records.Where(r => Convert.ToInt64(r[0]) == newId).Cast<object?>().ToArray();
    }

    /// <summary>更换照片能力：把 payload 里的 photoEffectMasterId 换成指定值。</summary>
    public async Task<object?[]> ChangePhotoAbilityAsync(HttpContext context, long photoId, long effectMasterId)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null || photoId <= 0) return Array.Empty<object?>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            update user_photo_records
            set payload = payload || jsonb_build_object('photoEffectMasterId', $3), updated_at = now()
            where id = $1 and "userId" = $2
            returning id
            """, connection);
        command.Parameters.AddWithValue(photoId);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(effectMasterId);
        if (await command.ExecuteScalarAsync() is null) return Array.Empty<object?>();
        var records = await ReadPhotoRecordsAsync(connection, userId.Value);
        return records.Where(r => Convert.ToInt64(r[0]) == photoId).Cast<object?>().ToArray();
    }

    /// <summary>设置照片的角色标签（appeared / tagged）。</summary>
    public async Task<object?[]> SetPhotoTagsAsync(HttpContext context, long photoId, long[] tags)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null || photoId <= 0) return Array.Empty<object?>();
        var json = "[" + string.Join(",", tags.Where(t => t > 0).Distinct()) + "]";
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            update user_photo_records
            set payload = payload || jsonb_build_object('tagged', $3::jsonb), updated_at = now()
            where id = $1 and "userId" = $2
            returning id
            """, connection);
        command.Parameters.AddWithValue(photoId);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(json);
        if (await command.ExecuteScalarAsync() is null) return Array.Empty<object?>();
        var records = await ReadPhotoRecordsAsync(connection, userId.Value);
        return records.Where(r => Convert.ToInt64(r[0]) == photoId).Cast<object?>().ToArray();
    }

    /// <summary>相册重置：清空该相册编成的布局（保留公开设置）。</summary>
    internal async Task<object?[]> ResetPhotoAlbumAsync(long userId, long albumGroupId, int arrangementType)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            update user_photo_album_arrangements
            set layout_payload = $4, updated_at = now()
            where "userId" = $1 and album_group_id = $2 and arrangement_type = $3
            """, connection);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(albumGroupId);
        command.Parameters.AddWithValue(arrangementType);
        command.Parameters.AddWithValue(MsgPack.Encode(Array.Empty<object?>()));
        var changed = await command.ExecuteNonQueryAsync();
        return new object?[] { changed >= 0 };
    }

    /// <summary>相册公开设置。</summary>
    internal async Task<object?[]> SetPhotoAlbumPublishingAsync(long userId, long albumGroupId, int arrangementType, bool isPublic)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into user_photo_album_arrangements(id, "userId", album_group_id, arrangement_type, is_public, layout_payload, updated_at)
            values ($1, $2, $3, $4, $5, $6, now())
            on conflict ("userId", album_group_id, arrangement_type)
            do update set is_public = excluded.is_public, updated_at = now()
            """, connection);
        command.Parameters.AddWithValue(UserScopedId(userId, 125000 + albumGroupId * 10 + arrangementType));
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(albumGroupId);
        command.Parameters.AddWithValue(arrangementType);
        command.Parameters.AddWithValue(isPublic);
        command.Parameters.AddWithValue(MsgPack.Encode(Array.Empty<object?>()));
        await command.ExecuteNonQueryAsync();
        return new object?[] { true };
    }

    /// <summary>相册预设：保存布局（preset 用 arrangement_type = 9 存放）。</summary>
    internal async Task<object?[]> SavePhotoPresetAsync(long userId, long presetId, object? payload, string? name)
    {
        var values = payload as object?[] ?? Array.Empty<object?>();
        var layout = DecodePhotoAlbumLayout(values.ElementAtOrDefault(0) ?? values);
        var encodedLayout = MsgPack.Encode(layout);
        var extra = MsgPack.Encode(new object?[] { name ?? "" });
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into user_photo_album_arrangements(id, "userId", album_group_id, arrangement_type, is_public, layout_payload, extra_payload, updated_at)
            values ($1, $2, $3, 9, false, $4, $5, now())
            on conflict ("userId", album_group_id, arrangement_type)
            do update set layout_payload = excluded.layout_payload,
                          extra_payload = excluded.extra_payload,
                          updated_at = now()
            """, connection);
        command.Parameters.AddWithValue(UserScopedId(userId, 126000 + presetId));
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(presetId);
        command.Parameters.AddWithValue(encodedLayout);
        command.Parameters.AddWithValue(extra);
        await command.ExecuteNonQueryAsync();
        return new object?[] { true };
    }

    /// <summary>端点用：相册重置（payload = [albumGroupId, arrangementType]）。</summary>
    public async Task<object?[]> ResetPhotoAlbumAsync(HttpContext context, object? payload)
    {
        var userId = await GetCurrentUserIdAsync(context) ?? 0L;
        if (userId == 0) return new object?[] { false };
        var values = payload as object?[] ?? Array.Empty<object?>();
        var albumGroupId = GetLong(values, 0) ?? 1;
        var arrangementType = (int)(GetLong(values, 1) ?? 1);
        return await ResetPhotoAlbumAsync(userId, albumGroupId, arrangementType);
    }

    /// <summary>端点用：相册公开设置（payload = [albumGroupId, arrangementType, isPublic]）。</summary>
    public async Task<object?[]> SetPhotoAlbumPublishingAsync(HttpContext context, object? payload)
    {
        var userId = await GetCurrentUserIdAsync(context) ?? 0L;
        if (userId == 0) return new object?[] { false };
        var values = payload as object?[] ?? Array.Empty<object?>();
        var albumGroupId = GetLong(values, 0) ?? 1;
        var arrangementType = (int)(GetLong(values, 1) ?? 1);
        var isPublic = GetBool(values, 2) ?? true;
        return await SetPhotoAlbumPublishingAsync(userId, albumGroupId, arrangementType, isPublic);
    }

    /// <summary>端点用：相册预设保存（body = 布局 / 名称）。</summary>
    public async Task<object?[]> SavePhotoPresetAsync(HttpContext context, long presetId, object? payload, bool setname)
    {
        var userId = await GetCurrentUserIdAsync(context) ?? 0L;
        if (userId == 0) return new object?[] { false };
        string? name = null;
        if (setname)
        {
            var values = payload as object?[];
            name = values?.ElementAtOrDefault(0) as string ?? "";
        }
        return await SavePhotoPresetAsync(userId, presetId, payload, name);
    }

    /// <summary>剧场故事观看记录（持久化，重复观看不影响）。</summary>
    public async Task<object?[]> WatchTheaterStoryAsync(HttpContext context, object? payload)
    {
        var userId = await GetCurrentUserIdAsync(context) ?? 0L;
        if (userId == 0) return new object?[] { false };
        var values = payload as object?[] ?? Array.Empty<object?>();
        var storyId = GetLong(values, 0) ?? 0L;
        if (storyId <= 0) return new object?[] { false };
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            create table if not exists user_photo_theater_watches (
              "userId" bigint not null,
              theater_story_id bigint not null,
              watched_at timestamptz not null default now(),
              primary key ("userId", theater_story_id)
            )
            """, connection);
        await command.ExecuteNonQueryAsync();
        await using var insert = new NpgsqlCommand(
            """
            insert into user_photo_theater_watches("userId", theater_story_id)
            values ($1, $2) on conflict do nothing
            """, connection);
        insert.Parameters.AddWithValue(userId);
        insert.Parameters.AddWithValue(storyId);
        await insert.ExecuteNonQueryAsync();
        return new object?[] { true };
    }
}
