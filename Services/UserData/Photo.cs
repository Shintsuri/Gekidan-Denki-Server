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
}
