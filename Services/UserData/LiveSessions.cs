using Npgsql;

sealed partial class UserDataService
{
    static async Task<LiveRewardRules.Snapshot?> ReadPendingLiveStartAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, long userId, byte[] hash)
    {
        await using var command = new NpgsqlCommand(
            """
            select start_snapshot from user_live_sessions v where v."userId"=$1
              and completion is null and start_hash=$2
              and not exists(select 1 from user_lesson_sessions l where l."userId"=v."userId" and l.started_at>v.started_at)
            """, connection, transaction);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(hash);
        var value = await command.ExecuteScalarAsync();
        return value is byte[] bytes
            ? LiveRewardRules.Snapshot.FromWire((object?[])MsgPack.Decode(bytes)!)
            : null;
    }

    public async Task RetireLiveAsync(HttpContext context)
    {
        var userId = await GetCurrentUserIdAsync(context) ?? throw new BadHttpRequestException("ACCOUNT_UNAUTHORIZED", 401);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockLessonUserAsync(connection, transaction, userId);
        // 中途退出（Retire）要把开演时扣掉的体力退回去：
        // 开演快照里存着 BaseStamina 和实际倍率，StaminaCost = 两者乘积。
        var refund = 0;
        await using (var read = new NpgsqlCommand(
            "select start_snapshot from user_live_sessions where \"userId\"=$1 and completion is null limit 1",
            connection, transaction))
        {
            read.Parameters.AddWithValue(userId);
            if (await read.ExecuteScalarAsync() is byte[] bytes && bytes.Length > 0)
            {
                try
                {
                    var snapshot = LiveRewardRules.Snapshot.FromWire((object?[])MsgPack.Decode(bytes)!);
                    refund = snapshot.StaminaCost;
                }
                catch
                {
                    refund = 0;
                }
            }
        }
        await ExecuteAsync(connection, transaction,
            "delete from user_live_sessions where \"userId\"=$1 and completion is null", userId);
        if (refund > 0)
            await ExecuteAsync(connection, transaction,
                """
                update user_game_states
                set stamina = least(stamina + $2, 3000), updated_at = now()
                where "userId" = $1
                """, userId, refund);
        await transaction.CommitAsync();
        context.RequestServices.GetRequiredService<ILogger<UserDataService>>().LogInformation(
            "演出退出 operation=live-retire userId={UserId} staminaRefund={Refund} outcome=success", userId, refund);
    }
}
