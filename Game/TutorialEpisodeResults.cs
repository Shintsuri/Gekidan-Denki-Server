static class TutorialEpisodeResults
{
    public static object?[] Summary(long episodeMasterId)
    {
        if (!EpisodeProgressionRules.Summaries.TryGetValue(episodeMasterId, out var summary))
            throw new BadHttpRequestException(EpisodeErrors.NotFound, 404);
        return new object?[] { summary.Title, summary.StoryType, summary.Order, SceneAssetPath(episodeMasterId) };
    }

    public static object?[] Details(long episodeMasterId)
    {
        return new object?[]
        {
            Detail(episodeMasterId, 1, "Tutorial"),
            Detail(episodeMasterId, 2, "")
        };
    }

    static object?[] Detail(long episodeMasterId, int order, string title)
    {
        // 与客户端 SiriusApi.Shared.Models.EpisodeDetailResult 的 [Key(n)] 一一对应：
        // 0 Id / 1 EpisodeMasterId / 2 Order / 3 GroupOrder / 4 Effect / 5 SpeakerName
        // 6 FontSize / 7 Phrase / 8 Title / 9 BackgroundImageFileName
        // 10 BackgroundCharacterImageFileName / 11 FadeType? / 12-14 FadeValue1-3(float?)
        // 15 BgmFileName / 16 SeFileName / 17 StillPhotoFileName / 18 MovieFileName
        // 19 WindowEffect? / 20 SceneCameraMasterId(long?) / 21 VoiceFileName
        // 22 CharacterMotions[] / 23 SpeakerIconId
        return new object?[]
        {
            episodeMasterId * 100 + order,   // 0  Id
            episodeMasterId,                 // 1  EpisodeMasterId
            order,                           // 2  Order
            order,                           // 3  GroupOrder
            "",                              // 4  Effect
            "",                              // 5  SpeakerName
            2,                               // 6  FontSize
            "",                              // 7  Phrase
            title,                           // 8  Title
            "",                              // 9  BackgroundImageFileName
            "",                              // 10 BackgroundCharacterImageFileName
            null,                            // 11 FadeType?
            null,                            // 12 FadeValue1 (float?)
            null,                            // 13 FadeValue2 (float?)
            null,                            // 14 FadeValue3 (float?)
            "",                              // 15 BgmFileName
            "",                              // 16 SeFileName
            "",                              // 17 StillPhotoFileName
            "",                              // 18 MovieFileName
            null,                            // 19 WindowEffect?
            null,                            // 20 SceneCameraMasterId (long?)
            "",                              // 21 VoiceFileName
            Array.Empty<object?>(),          // 22 CharacterMotions
            ""                               // 23 SpeakerIconId
        };
    }

    public static string SceneAssetPath(long episodeMasterId)
    {
        return $"scenes/{episodeMasterId}.bin";
    }
}