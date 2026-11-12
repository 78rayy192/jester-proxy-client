const MAP: &[(&str, &[&str])] = &[
    ("🇳🇱", &["нидерланд", "netherlands", "holland", "amsterdam"]),
    ("🇩🇪", &["герман", "germany", "frankfurt", "berlin"]),
    ("🇵🇱", &["польш", "poland", "warsaw", "варшав"]),
    ("🇫🇮", &["финлянд", "finland", "helsinki", "хельсинк"]),
    ("🇺🇸", &["сша", "usa", "united states", "флорид", "florida", "техас", "texas", "нью-йорк", "new york"]),
    ("🇬🇧", &["великобритан", "англия", "britain", "england", "london", "лондон"]),
    ("🇫🇷", &["франц", "france", "paris", "париж"]),
    ("🇸🇪", &["швеци", "sweden", "stockholm"]),
    ("🇳🇴", &["норвег", "norway", "oslo"]),
    ("🇩🇰", &["дани", "denmark", "copenhagen"]),
    ("🇪🇪", &["эстон", "estonia", "tallinn"]),
    ("🇱🇻", &["латви", "latvia", "riga", "рига"]),
    ("🇱🇹", &["литв", "lithuania", "vilnius"]),
    ("🇹🇷", &["турци", "turkey", "istanbul", "стамбул"]),
    ("🇯🇵", &["япони", "japan", "tokyo", "токио"]),
    ("🇰🇷", &["коре", "korea", "seoul"]),
    ("🇸🇬", &["сингапур", "singapore"]),
    ("🇨🇦", &["канад", "canada"]),
    ("🇦🇺", &["австрал", "australia"]),
    ("🇰🇿", &["казахстан", "kazakhstan"]),
    ("🇺🇦", &["украин", "ukraine"]),
    ("🇨🇭", &["швейцар", "switzerland"]),
    ("🇦🇹", &["австри", "austria"]),
    ("🇮🇹", &["итали", "italy"]),
    ("🇪🇸", &["испан", "spain"]),
    ("🇨🇿", &["чехи", "czech"]),
    ("🇷🇴", &["румын", "romania"]),
    ("🇧🇬", &["болгар", "bulgaria"]),
    ("🇭🇺", &["венгр", "hungary"]),
    ("🇪🇺", &["авто", "auto", "баланс"]),
];

pub fn apply(name: &str) -> String {
    let n = name.trim();
    if n.is_empty() || has_flag(n) { return n.to_string(); }
    let lower = n.to_lowercase();
    for (flag, keys) in MAP {
        if keys.iter().any(|k| lower.contains(k)) {
            return format!("{flag} {n}");
        }
    }
    n.to_string()
}

fn has_flag(name: &str) -> bool {
    let chars: Vec<char> = name.chars().collect();
    chars.windows(2).any(|w| {
        let a = w[0] as u32;
        let b = w[1] as u32;
        (0x1F1E6..=0x1F1FF).contains(&a) && (0x1F1E6..=0x1F1FF).contains(&b)
    })
}

pub fn is_notice(name: &str) -> bool {
    let n = name.to_lowercase();
    ["hwid", "лимит", "устрой", "поддерж", "не передает", "не передаёт", "удалите"]
        .iter()
        .any(|k| n.contains(k))
}
