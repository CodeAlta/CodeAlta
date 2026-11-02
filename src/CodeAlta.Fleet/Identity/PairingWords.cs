using System.Collections.Immutable;

namespace CodeAlta.Fleet.Identity;

/// <summary>Turns bytes into short, distinct English words, one word per byte, for people to compare.</summary>
internal static class PairingWords
{
    /// <summary>256 words, one for each byte value: short, common and unlike each other.</summary>
    internal static readonly ImmutableArray<string> Words =
    [
        "acid", "acorn", "actor", "agent", "alarm", "album", "alien", "alpha", "amber", "anchor", "angel", "ankle", "apple", "apron", "arena", "arrow",
        "atlas", "attic", "autumn", "award", "bacon", "badge", "bagel", "baker", "bamboo", "banjo", "barn", "basil", "basket", "beach", "beacon", "bear",
        "beaver", "bell", "bench", "berry", "bike", "bird", "bison", "blade", "blanket", "blossom", "boat", "bonus", "book", "boot", "bottle", "bowl",
        "brain", "branch", "bread", "brick", "bridge", "broom", "bubble", "bucket", "buffalo", "bunny", "butter", "button", "cabin", "cactus", "camel", "camera",
        "candle", "candy", "canoe", "canyon", "captain", "carbon", "carpet", "carrot", "castle", "cedar", "cello", "chalk", "cherry", "chess", "circus", "citrus",
        "clock", "cloud", "clover", "coast", "cobra", "cocoa", "coconut", "comet", "copper", "coral", "cotton", "cougar", "crane", "crayon", "cricket", "crown",
        "crystal", "cube", "cupcake", "dahlia", "daisy", "delta", "desert", "diamond", "dinner", "dolphin", "donkey", "dragon", "drum", "eagle", "echo", "eclipse",
        "elbow", "ember", "emerald", "engine", "falcon", "feather", "fern", "ferry", "fiddle", "finch", "flame", "flute", "forest", "fossil", "fox", "galaxy",
        "garden", "garlic", "gecko", "ginger", "giraffe", "glacier", "globe", "goose", "grape", "guitar", "hammer", "harbor", "harp", "hazel", "helmet", "heron",
        "honey", "hornet", "hotel", "iceberg", "igloo", "island", "ivory", "jacket", "jaguar", "jasmine", "jelly", "jungle", "kayak", "kettle", "kiwi", "koala",
        "ladder", "lagoon", "lantern", "lemon", "leopard", "lily", "lizard", "lobster", "locket", "lotus", "magnet", "mango", "maple", "marble", "meadow", "melon",
        "meteor", "mirror", "mitten", "monkey", "moose", "mosaic", "muffin", "needle", "nectar", "noodle", "oasis", "ocean", "olive", "onion", "orange", "orbit",
        "orchid", "otter", "owl", "oyster", "paddle", "palm", "panda", "panther", "parrot", "peach", "peanut", "pearl", "pebble", "pelican", "pepper", "piano",
        "pickle", "pigeon", "pillow", "pine", "pirate", "planet", "plum", "pocket", "polar", "pony", "poppy", "potato", "pretzel", "prism", "pumpkin", "puzzle",
        "quartz", "quill", "rabbit", "radar", "radish", "raven", "ribbon", "river", "robin", "rocket", "ruby", "saddle", "salmon", "sandal", "saturn", "scarf",
        "shark", "shell", "silver", "sketch", "sled", "snail", "spider", "spruce", "squid", "summit", "sunset", "tiger", "tomato", "tulip", "turtle", "violin",
    ];

    /// <summary>Gets one word for each byte, separated by spaces.</summary>
    public static string FromBytes(ReadOnlySpan<byte> bytes)
    {
        var words = new string[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
        {
            words[index] = Words[bytes[index]];
        }

        return string.Join(' ', words);
    }
}
