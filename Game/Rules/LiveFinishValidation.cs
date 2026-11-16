// FinishAndValidate 发送逐音符记录，汇总字段由服务端计算，不能把空 Judges 当成非法成绩。
static class LiveFinishValidation
{
    public static object?[] Normalize(object?[] finish)
    {
        if (finish.Length <= 2 || finish[2] is not null) return finish;
        // 私有服务器：不同客户端版本上传的段落数不同，只要拿到逐音符数组就接受。
        if (finish.Length < 5 || finish[4] is not object?[] notes || notes.Length == 0)
            throw new BadHttpRequestException("LIVE_FINISH_F1");
        var counts = new int[7];
        var noteIds = new HashSet<long>();
        long score = 0;
        var maxCombo = 0;
        var life = 0;
        foreach (var value in notes)
        {
            var row = ReadBlock(value, 6);
            var noteId = Integer(row[3]);
            var timing = Integer(row[4]);
            var combo = Integer(row[5]);
            if (noteId <= 0 || timing is < 1 or > 7 || combo < 0)
                throw new BadHttpRequestException("LIVE_FINISH_F5");
            counts[(int)timing]++;
            AddScore(ref score, Integer(row[1]));
            life = checked((int)Integer(row[2]));
            maxCombo = Math.Max(maxCombo, (int)combo);
        }
        // 技能记录的 Score 是本次加分，不是 Hash；哈希字段只用于协议完整性读取。
        // 客户端只上传 5 段（逐音符 + 4 个标量），后面的技能段要判越界
        for (var field = 5; field <= 7 && field < finish.Length; field++)
        {
            if (finish[field] is null) continue;
            if (finish[field] is not object?[] blocks)
                throw new BadHttpRequestException("LIVE_FINISH_F7");
            foreach (var value in blocks) AddScore(ref score, Integer(ReadBlock(value, field == 5 ? 6 : 5)[1]));
        }
        var normalized = (object?[])finish.Clone();
        normalized[0] = score;
        normalized[1] = maxCombo;
        normalized[2] = Enumerable.Range(1, 6).Select(timing => (object?)new object?[] { timing, counts[timing] }).ToArray();
        normalized[3] = life > 0;
        return normalized;
    }

    static object?[] ReadBlock(object? value, int length)
    {
        if (value is not object?[] row || row.Length < length)
            throw new BadHttpRequestException("LIVE_FINISH_F2");
        // Hash 是无符号 64 位协议字段，不参与有符号分数运算。
        if (row[0] is not (byte or sbyte or short or ushort or int or uint or long or ulong))
            throw new BadHttpRequestException("LIVE_FINISH_F3");
        foreach (var field in row.Skip(1)) _ = Integer(field);
        if (Integer(row[1]) < 0 || Integer(row[2]) is < 0 or > int.MaxValue)
            throw new BadHttpRequestException("LIVE_FINISH_F4");
        return row;
    }

    static long Integer(object? value) => value switch
    {
        byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value),
        _ => throw new BadHttpRequestException("LIVE_FINISH_F8")
    };

    static void AddScore(ref long score, long gain)
    {
        if (gain < 0 || gain > long.MaxValue - score)
            throw new BadHttpRequestException("LIVE_FINISH_F6");
        score += gain;
    }
}
