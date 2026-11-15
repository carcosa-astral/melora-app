//! What internet radio sends (ICY headers and titles, Ogg comments), tidying pasted addresses,
//! and station files (.m3u). Ported from 2.x's StreamMetadata and StationFile.

fn clean(s: &str) -> Option<String> {
    let t = s.trim();
    (!t.is_empty()).then(|| t.to_string())
}

/// The title in an ICY block like `StreamTitle='Artist - Title';StreamUrl='';`.
pub fn icy_title(meta: &str) -> Option<String> {
    const KEY: &str = "streamtitle='";
    let start = meta.to_lowercase().find(KEY)? + KEY.len();
    // Titles can contain apostrophes ("Guns N' Roses"), so end at the "';" that closes the field.
    let end = meta[start..]
        .find("';")
        .map(|i| start + i)
        .or_else(|| meta.rfind('\''))?;
    if end < start {
        return None;
    }
    clean(&meta[start..end])
}

/// "Artist - Title" from Ogg/Vorbis comments such as `ARTIST=...` and `TITLE=...`.
pub fn from_vorbis_comments<'a>(comments: impl IntoIterator<Item = &'a str>) -> Option<String> {
    let (mut artist, mut title) = (None, None);
    let value = |c: &str, key: &str| {
        c.get(..key.len())
            .filter(|k| k.eq_ignore_ascii_case(key))
            .and_then(|_| clean(&c[key.len()..]))
    };
    for c in comments {
        artist = artist.or_else(|| value(c, "ARTIST="));
        title = title.or_else(|| value(c, "TITLE="));
    }
    match (artist, title) {
        (Some(a), Some(t)) => Some(format!("{a} - {t}")),
        (a, t) => t.or(a),
    }
}

/// A header such as `icy-name: Groove Salad` from raw header lines.
pub fn header_value<'a>(headers: impl IntoIterator<Item = &'a str>, name: &str) -> Option<String> {
    headers.into_iter().find_map(|line| {
        let (key, value) = line.split_once(':')?;
        key.trim()
            .eq_ignore_ascii_case(name)
            .then(|| clean(value))
            .flatten()
    })
}

/// Tidies a pasted address: trims it and adds http:// when there's no scheme. None if it isn't
/// a web address.
pub fn normalize_url(input: &str) -> Option<String> {
    let text = input.trim();
    if text.is_empty() || text.contains(char::is_whitespace) {
        return None;
    }
    let text = if text.contains("://") {
        text.to_string()
    } else {
        format!("http://{text}")
    };
    let lower = text.to_lowercase();
    let rest = lower
        .strip_prefix("http://")
        .or_else(|| lower.strip_prefix("https://"))?;
    let host = rest.split(['/', '?', '#']).next()?.rsplit('@').next()?;
    let host = host.split(':').next()?;
    (!host.is_empty()).then_some(text)
}

/// The host of an address, shown under a station's name until it says what's playing.
pub fn host(url: &str) -> String {
    let rest = url.split_once("://").map_or(url, |(_, r)| r);
    rest.split(['/', '?', '#'])
        .next()
        .unwrap_or(rest)
        .rsplit('@')
        .next()
        .unwrap_or(rest)
        .to_string()
}

const HEADER: &str = "#EXTM3U";
const INFO: &str = "#EXTINF:";

/// Stations as an M3U list with each one's name, which other players can open too.
pub fn write_m3u<'a>(stations: impl IntoIterator<Item = (&'a str, &'a str)>) -> String {
    let mut text = format!("{HEADER}\r\n");
    for (name, url) in stations {
        text.push_str(&format!("{INFO}-1,{name}\r\n{url}\r\n"));
    }
    text
}

/// The stations in a file's text; lines that aren't web addresses are skipped, and plain
/// addresses are named after their host.
pub fn read_m3u(text: &str) -> Vec<(String, String)> {
    let mut stations = Vec::new();
    let mut name = None;
    for raw in text.lines() {
        let line = raw.trim();
        if line.len() >= INFO.len() && line[..INFO.len()].eq_ignore_ascii_case(INFO) {
            // "#EXTINF:-1,Name" (the name can contain commas; the duration can't)
            name = line.split_once(',').and_then(|(_, n)| clean(n));
        } else if !line.is_empty()
            && !line.starts_with('#')
            && let Some(url) = normalize_url(line)
        {
            stations.push((name.take().unwrap_or_else(|| host(&url)), url));
        }
    }
    stations
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn icy_titles() {
        assert_eq!(
            icy_title("StreamTitle='Daft Punk - Veridis Quo';StreamUrl='';").as_deref(),
            Some("Daft Punk - Veridis Quo")
        );
        assert_eq!(
            icy_title("StreamTitle='Guns N' Roses - Patience';").as_deref(),
            Some("Guns N' Roses - Patience")
        );
        assert_eq!(icy_title("streamtitle='  ';"), None);
        assert_eq!(icy_title("StreamUrl='x';"), None);
    }

    #[test]
    fn vorbis_comments() {
        assert_eq!(
            from_vorbis_comments(["TITLE=Song", "ARTIST=Band"]).as_deref(),
            Some("Band - Song")
        );
        assert_eq!(
            from_vorbis_comments(["title=Only"]).as_deref(),
            Some("Only")
        );
        assert_eq!(from_vorbis_comments(["ENCODER=x"]), None);
    }

    #[test]
    fn headers() {
        let h = ["HTTP/1.0 200 OK", "icy-name: Groove Salad", "icy-br:128"];
        assert_eq!(header_value(h, "ICY-NAME").as_deref(), Some("Groove Salad"));
        assert_eq!(header_value(h, "icy-br").as_deref(), Some("128"));
        assert_eq!(header_value(h, "icy-genre"), None);
    }

    #[test]
    fn pasted_addresses() {
        assert_eq!(
            normalize_url(" ice1.somafm.com/groovesalad-128-mp3 ").as_deref(),
            Some("http://ice1.somafm.com/groovesalad-128-mp3")
        );
        assert_eq!(
            normalize_url("https://a.b/x.m3u8").as_deref(),
            Some("https://a.b/x.m3u8")
        );
        assert_eq!(normalize_url("ftp://a.b/x"), None);
        assert_eq!(normalize_url("not an address"), None);
        assert_eq!(normalize_url(""), None);
        assert_eq!(
            host("http://user@ice1.somafm.com:8000/x?y"),
            "ice1.somafm.com:8000"
        );
    }

    #[test]
    fn m3u_round_trip_and_plain_lists() {
        let text = write_m3u([
            ("Groove Salad, chill", "http://a.b/gs"),
            ("BBC", "https://c.d/x.m3u8"),
        ]);
        assert_eq!(
            read_m3u(&text),
            [
                (
                    "Groove Salad, chill".to_string(),
                    "http://a.b/gs".to_string()
                ),
                ("BBC".into(), "https://c.d/x.m3u8".into())
            ]
        );
        assert_eq!(
            read_m3u("#EXTM3U\nradio.example.com/live\n# a comment\nnonsense here\n"),
            [(
                "radio.example.com".to_string(),
                "http://radio.example.com/live".to_string()
            )]
        );
    }
}
