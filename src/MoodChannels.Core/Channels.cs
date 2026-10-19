namespace MoodChannels;

/// <summary>
/// The 13 channels in their native order: five time zones (exactly one per classifiable track) and eight moods
/// (usually one, sometimes two). A track can also be unclassifiable: then it is in no channel at all.
/// </summary>
public static class Channels
{
    public static readonly string[] Names =
    {
        "Morning", "Daytime", "Evening", "Night", "Midnight",
        "Energetic", "Relax", "Mellow", "Upbeat", "Emotional", "Lounge", "Dance", "Extreme",
    };

    public const int TimeZoneCount = 5, MoodCount = 8, Count = 13;
    public const int FirstMood = 5;

    public static bool IsTimeZone(int channel) => channel is >= 0 and < FirstMood;
    public static bool IsMood(int channel) => channel is >= FirstMood and < Count;
}
