using System.Text.RegularExpressions;

namespace Jester.Host.Services;

public static class ServerFlags
{
    static readonly Regex Existing = new(@"\uD83C[\uDDE6-\uDDFF]\uD83C[\uDDE6-\uDDFF]", RegexOptions.Compiled);

    static readonly (string Flag, string[] Keys)[] Map =
    [
        ("🇳🇱", ["нидерланд", "netherlands", "holland", "amsterdam"]),
        ("🇩🇪", ["герман", "germany", "frankfurt", "berlin"]),
        ("🇵🇱", ["польш", "poland", "warsaw", "варшав"]),
        ("🇫🇮", ["финлянд", "finland", "helsinki", "хельсинк"]),
        ("🇺🇸", ["сша", "usa", "united states", "флорид", "florida", "техас", "texas", "нью-йорк", "new york"]),
        ("🇬🇧", ["великобритан", "англия", "britain", "england", "london", "лондон"]),
        ("🇫🇷", ["франц", "france", "paris", "париж"]),
        ("🇸🇪", ["швеци", "sweden", "stockholm"]),
        ("🇳🇴", ["норвег", "norway", "oslo"]),
        ("🇩🇰", ["дани", "denmark", "copenhagen"]),
        ("🇪🇪", ["эстон", "estonia", "tallinn"]),
        ("🇱🇻", ["латви", "latvia", "riga", "рига"]),
        ("🇱🇹", ["литв", "lithuania", "vilnius"]),
        ("🇹🇷", ["турци", "turkey", "istanbul", "стамбул"]),
        ("🇯🇵", ["япони", "japan", "tokyo", "токио"]),
        ("🇰🇷", ["коре", "korea", "seoul"]),
        ("🇸🇬", ["сингапур", "singapore"]),
        ("🇨🇦", ["канад", "canada"]),
        ("🇦🇺", ["австрал", "australia"]),
        ("🇰🇿", ["казахстан", "kazakhstan"]),
        ("🇺🇦", ["украин", "ukraine"]),
        ("🇨🇭", ["швейцар", "switzerland"]),
        ("🇦🇹", ["австри", "austria"]),
        ("🇮🇹", ["итали", "italy"]),
        ("🇪🇸", ["испан", "spain"]),
        ("🇨🇿", ["чехи", "czech"]),
        ("🇷🇴", ["румын", "romania"]),
        ("🇧🇬", ["болгар", "bulgaria"]),
        ("🇭🇺", ["венгр", "hungary"]),
        ("🇪🇺", ["авто", "auto", "баланс"]),
    ];

    public static string Apply(string name)
    {
        var n = name.Trim();
        if (n.Length == 0 || Existing.IsMatch(n)) return n;
        var lower = n.ToLowerInvariant();
        foreach (var (flag, keys) in Map)
        {
            if (keys.Any(k => lower.Contains(k, StringComparison.Ordinal)))
                return flag + " " + n;
        }
        return n;
    }

    public static bool IsNotice(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("hwid", StringComparison.Ordinal)
            || n.Contains("лимит", StringComparison.Ordinal)
            || n.Contains("устрой", StringComparison.Ordinal)
            || n.Contains("поддерж", StringComparison.Ordinal)
            || n.Contains("не передает", StringComparison.Ordinal)
            || n.Contains("не передаёт", StringComparison.Ordinal)
            || n.Contains("удалите", StringComparison.Ordinal);
    }
}
