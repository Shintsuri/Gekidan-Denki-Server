using Npgsql;

sealed partial class UserDataService
{
    internal async Task<long> RequireAuthenticatedUserAsync(HttpContext context)
    {
        var token = PayloadReaders.ReadApiToken(context);
        if (string.IsNullOrWhiteSpace(token)) throw new BadHttpRequestException(AccountErrors.Unauthorized, 401);
        var tokenHash = CryptoService.Sha256(token);

        // 强制指定账号：无论 token 是什么都返回该账号（客户端清数据后恢复进度用）
        var forced = System.Environment.GetEnvironmentVariable("WDS_FORCE_USER_ID");
        if (!string.IsNullOrWhiteSpace(forced) && long.TryParse(forced, out var forcedId))
        {
            await using var forcedConn = await database.OpenConnectionAsync();
            await using (var bind = new NpgsqlCommand("""
                insert into user_auth_sessions ("userId", token_hash, expires_at)
                values ($1, $2, now() + interval '365 days')
                """, forcedConn))
            {
                bind.Parameters.AddWithValue(forcedId);
                bind.Parameters.AddWithValue(tokenHash);
                try { await bind.ExecuteNonQueryAsync(); } catch { }
            }
            await Console.Out.WriteLineAsync($"level=INFO operation=force-user userId={forcedId}");
            return forcedId;
        }

        await using var connection = await database.OpenConnectionAsync();
        await using (var command = new NpgsqlCommand("""
            select "userId" from user_auth_sessions where token_hash = $1 and expires_at > now() order by id desc limit 1
            """, connection))
        {
            command.Parameters.AddWithValue(tokenHash);
            if (await command.ExecuteScalarAsync() is long known) return known;
        }

        // 本地单机调试：未知 token 自动建号并绑定会话，免登录即可进入游戏。
        return await AutoRegisterLocalUserAsync(connection, tokenHash, context.TraceIdentifier);
    }

    async Task<long> AutoRegisterLocalUserAsync(NpgsqlConnection connection, string tokenHash, string traceId)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var publicId = "L" + CryptoService.RandomToken(9);
            var name = "LocalPlayer";
            long userId;
            await using (var insert = new NpgsqlCommand("""
                insert into user_accounts (public_id, name, password_hash, role)
                values ($1, $2, $3, 'player')
                returning id
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue(publicId);
                insert.Parameters.AddWithValue(name);
                insert.Parameters.AddWithValue(CryptoService.Sha256(CryptoService.RandomToken()));
                userId = (long)(await insert.ExecuteScalarAsync())!;
            }

            await using (var update = new NpgsqlCommand("""
                update user_accounts set numeric_id = (100000 + (id % 900000))::integer where id = $1
                """, connection, transaction))
            {
                update.Parameters.AddWithValue(userId);
                await update.ExecuteNonQueryAsync();
            }

            await using (var st = new NpgsqlCommand("""
                insert into user_states ("userId", state) values ($1, $2::jsonb)
                """, connection, transaction))
            {
                st.Parameters.AddWithValue(userId);
                st.Parameters.AddWithValue(DefaultUserState.CreateJson(publicId, name));
                await st.ExecuteNonQueryAsync();
            }

            await UserDataService.InsertDefaultUserDataAsync(connection, transaction, userId, name);

            await using (var session = new NpgsqlCommand("""
                insert into user_auth_sessions ("userId", token_hash, expires_at)
                values ($1, $2, now() + interval '365 days')
                """, connection, transaction))
            {
                session.Parameters.AddWithValue(userId);
                session.Parameters.AddWithValue(tokenHash);
                await session.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            await Console.Out.WriteLineAsync($"level=INFO operation=auto-register userId={userId} requestId={traceId} outcome=success");
            return userId;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
