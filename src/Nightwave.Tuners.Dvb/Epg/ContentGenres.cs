namespace Nightwave.Tuners.Dvb.Epg;

/// <summary>
/// The content nibbles of the content descriptor (EN 300 468, 6.2.9, table 29) as the English
/// genre names a guide shows. Level 1 is the broad genre, level 2 the specific one; a level 2
/// of zero is "general" and adds nothing to its level 1.
/// </summary>
internal static class ContentGenres
{
    /// <summary>The nibble that marks a live broadcast (special characteristics, level 2 = 3).</summary>
    public const int Live = 0xB3;

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x1] = "Movie / Drama",
        [0x11] = "Detective / Thriller",
        [0x12] = "Adventure / Western / War",
        [0x13] = "Science Fiction / Fantasy / Horror",
        [0x14] = "Comedy",
        [0x15] = "Soap / Melodrama / Folklore",
        [0x16] = "Romance",
        [0x17] = "Serious / Classical / Religious / Historical Drama",
        [0x18] = "Adult Movie / Drama",

        [0x2] = "News / Current Affairs",
        [0x21] = "News / Weather Report",
        [0x22] = "News Magazine",
        [0x23] = "Documentary",
        [0x24] = "Discussion / Interview / Debate",

        [0x3] = "Show / Game Show",
        [0x31] = "Game Show / Quiz / Contest",
        [0x32] = "Variety Show",
        [0x33] = "Talk Show",

        [0x4] = "Sports",
        [0x41] = "Special Events",
        [0x42] = "Sports Magazine",
        [0x43] = "Football / Soccer",
        [0x44] = "Tennis / Squash",
        [0x45] = "Team Sports",
        [0x46] = "Athletics",
        [0x47] = "Motor Sport",
        [0x48] = "Water Sport",
        [0x49] = "Winter Sports",
        [0x4A] = "Equestrian",
        [0x4B] = "Martial Sports",

        [0x5] = "Children's / Youth",
        [0x51] = "Pre-school",
        [0x52] = "Entertainment for 6 to 14",
        [0x53] = "Entertainment for 10 to 16",
        [0x54] = "Informational / Educational / School",
        [0x55] = "Cartoons / Puppets",

        [0x6] = "Music / Ballet / Dance",
        [0x61] = "Rock / Pop",
        [0x62] = "Serious Music / Classical Music",
        [0x63] = "Folk / Traditional Music",
        [0x64] = "Jazz",
        [0x65] = "Musical / Opera",
        [0x66] = "Ballet",

        [0x7] = "Arts / Culture",
        [0x71] = "Performing Arts",
        [0x72] = "Fine Arts",
        [0x73] = "Religion",
        [0x74] = "Popular Culture / Traditional Arts",
        [0x75] = "Literature",
        [0x76] = "Film / Cinema",
        [0x77] = "Experimental Film / Video",
        [0x78] = "Broadcasting / Press",
        [0x79] = "New Media",
        [0x7A] = "Arts / Culture Magazine",
        [0x7B] = "Fashion",

        [0x8] = "Social / Political Issues / Economics",
        [0x81] = "Magazine / Report / Documentary",
        [0x82] = "Economics / Social Advisory",
        [0x83] = "Remarkable People",

        [0x9] = "Education / Science / Factual",
        [0x91] = "Nature / Animals / Environment",
        [0x92] = "Technology / Natural Sciences",
        [0x93] = "Medicine / Physiology / Psychology",
        [0x94] = "Foreign Countries / Expeditions",
        [0x95] = "Social / Spiritual Sciences",
        [0x96] = "Further Education",
        [0x97] = "Languages",

        [0xA] = "Leisure / Hobbies",
        [0xA1] = "Tourism / Travel",
        [0xA2] = "Handicraft",
        [0xA3] = "Motoring",
        [0xA4] = "Fitness and Health",
        [0xA5] = "Cooking",
        [0xA6] = "Advertisement / Shopping",
        [0xA7] = "Gardening",
    };

    /// <summary>
    /// The genres for a list of content nibbles, broad before specific, each once. Level 1 values
    /// the standard reserves, and the special characteristics (original language, black and
    /// white, live), are not genres and are left out.
    /// </summary>
    public static IReadOnlyList<string> From(IEnumerable<int> nibbles)
    {
        ArgumentNullException.ThrowIfNull(nibbles);

        var genres = new List<string>();

        foreach (var nibble in nibbles)
        {
            if (Names.TryGetValue(nibble >> 4, out var broad) && !genres.Contains(broad))
            {
                genres.Add(broad);
            }

            if ((nibble & 0x0F) != 0 && Names.TryGetValue(nibble, out var specific) && !genres.Contains(specific))
            {
                genres.Add(specific);
            }
        }

        return genres;
    }
}
