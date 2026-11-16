using Npgsql;

sealed partial class UserDataService
{
    static DateOnly CircleSupportDay(DateTimeOffset utcNow) => DateOnly.FromDateTime(CurrentDailyResetBoundaryUtc(utcNow).ToOffset(JapanStandardTimeOffset).Date);

    static async Task<int> ReadCircleDailyPointsAsync(NpgsqlConnection db,long user,string circle)
    {
        await using var query=new NpgsqlCommand("""
            select coalesce(sum(point),0)::int from user_circle_support_points where "userId"=$1 and circle_id=$2 and support_day=$3
            """,db);
        query.Parameters.AddWithValue(user); query.Parameters.AddWithValue(circle);
        query.Parameters.AddWithValue(CircleSupportDay(DateTimeOffset.UtcNow));
        return (int)(await query.ExecuteScalarAsync())!;
    }

    public async Task<object?[]> GetCircleRankingAsync(HttpContext context,string period)
    {
        var user=await RequireAuthenticatedUserAsync(context);
        var day=CircleSupportDay(DateTimeOffset.UtcNow);
        var start=period switch {
            "Daily"=>day,
            "Weekly"=>day.AddDays(-((int)day.DayOfWeek+6)%7),
            "Monthly"=>new DateOnly(day.Year,day.Month,1),
            _=>throw new ArgumentOutOfRangeException(nameof(period)) };
        await using var db=await database.OpenConnectionAsync();
        await using var query=new NpgsqlCommand("""
            with current_circle as (select circle_id from user_circle_memberships where "userId"=$1),
            points as (
              select "userId",circle_id,sum(point)::int as point from user_circle_support_points
              where support_day between $2 and $3 group by "userId",circle_id
            )
            select a.public_id,p.display_name,coalesce(points.point,0),coalesce(p.home_character_master_id,110010),
              coalesce(card.level,1),p.is_home_character_illust,coalesce(p.icon_frame_master_id,190001),p.title_master_id,
              p.trophy_master_id_1,p.trophy_master_id_2,p.trophy_master_id_3,
              row_number() over(order by coalesce(points.point,0) desc,a.public_id)::int
            from current_circle current join user_circle_memberships m on m.circle_id=current.circle_id
            join user_accounts a on a.id=m."userId" join user_profiles p on p."userId"=a.id
            left join points on points."userId"=a.id and points.circle_id=m.circle_id
            left join lateral (
              select level from user_character_cards where "userId"=a.id and character_master_id=coalesce(p.home_character_master_id,110010)
              order by id limit 1
            ) card on true
            order by coalesce(points.point,0) desc,a.public_id
            """,db);
        query.Parameters.AddWithValue(user); query.Parameters.AddWithValue(start); query.Parameters.AddWithValue(day);
        var ranks=new List<object?>(); var profiles=new List<object?>();
        var selfPublicId = await ReadPublicIdAsync(user);
        var myRank = 0; var myPoint = 0;
        await using var reader=await query.ExecuteReaderAsync();
        while(await reader.ReadAsync())
        {
            var id=reader.GetString(0);
            ranks.Add(new object?[] {reader.GetInt32(8),reader.GetInt32(2),id,0});
            // userProfiles 必须是完整的 13 字段（和事件排行同一结构），
            // 之前只给了 9 个字段，客户端解析越界就表现为列表抽搐。
            profiles.Add(new object?[] {
                id,                                              // 0 user_id
                reader.GetString(1),                             // 1 user_name
                GetNullableInt64(reader, 9),                     // 2 trophy1
                GetNullableInt64(reader, 10),                    // 3 trophy2
                GetNullableInt64(reader, 11),                    // 4 trophy3
                reader.GetInt64(3),                              // 5 main_m_character_id
                reader.GetInt32(4),                              // 6 main_character_level
                reader.GetBoolean(5),                            // 7 awakening
                reader.GetInt64(6),                              // 8 icon_frame_master_id
                0L,                                              // 9 name_color_master_id
                0L,                                              // 10 name_base_color_master_id
                0L,                                              // 11 nameplate_master_id
                (long?)null                                      // 12 nameplate_detail_master_id
            });
            // 自己的排名和贡献度（界面底部「あなたの順位 / 累計劇場貢献度」）
            if (string.Equals(id, selfPublicId, StringComparison.Ordinal))
            {
                myRank = reader.GetInt32(8);
                myPoint = reader.GetInt32(2);
            }
        }
        return new object?[] {ranks.ToArray(),profiles.ToArray(),myRank,myPoint};
    }
}
