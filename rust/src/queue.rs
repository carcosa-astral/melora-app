//! The play order, ported from 2.x's PlayQueue. There are no playlists: playing a track plays
//! the list it's in, in order or shuffled. Tracks are indices into that list's library.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Repeat {
    Off,
    All,
    One,
}

impl Repeat {
    /// As 2.x stores it in settings.
    pub fn name(self) -> &'static str {
        match self {
            Repeat::Off => "Off",
            Repeat::All => "All",
            Repeat::One => "One",
        }
    }

    pub fn parse(s: &str) -> Option<Repeat> {
        [Repeat::Off, Repeat::All, Repeat::One]
            .into_iter()
            .find(|r| r.name() == s)
    }

    /// The button's order: off, all, one, off.
    pub fn cycled(self) -> Repeat {
        match self {
            Repeat::Off => Repeat::All,
            Repeat::All => Repeat::One,
            Repeat::One => Repeat::Off,
        }
    }
}

/// What the shuffle needs to know about a track: its artist (lower case) and its album.
#[derive(Clone, Debug, PartialEq)]
pub struct Info {
    pub artist: String,
    pub album: usize,
}

pub struct Queue {
    source: Vec<usize>,
    info: Vec<Info>,
    order: Vec<usize>,
    pos: Option<usize>,
    pub shuffle: bool,
    pub repeat: Repeat,
    rng: u64,
}

impl Queue {
    pub fn new(shuffle: bool, repeat: Repeat) -> Queue {
        let seed = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0x9E37_79B9_7F4A_7C15, |d| d.as_nanos() as u64);
        Queue {
            source: Vec::new(),
            info: Vec::new(),
            order: Vec::new(),
            pos: None,
            shuffle,
            repeat,
            rng: seed | 1,
        }
    }

    pub fn current(&self) -> Option<usize> {
        self.pos.and_then(|p| self.order.get(p).copied())
    }

    /// Starts a new run over a list's tracks (`info` describes each, by index), beginning at
    /// `first`, or the first track (a random one when shuffling).
    pub fn start(&mut self, info: Vec<Info>, first: Option<usize>) -> Option<usize> {
        self.source = (0..info.len()).collect();
        self.info = info;
        if self.source.is_empty() {
            self.order.clear();
            self.pos = None;
            return None;
        }
        let first = first.unwrap_or_else(|| {
            if self.shuffle {
                self.pick(self.source.len())
            } else {
                0
            }
        });
        self.rebuild(first);
        self.current()
    }

    pub fn set_shuffle(&mut self, shuffle: bool) {
        self.shuffle = shuffle;
        if let Some(current) = self.current() {
            self.rebuild(current);
        }
    }

    /// The next track. `manual` (the Next button) skips even in repeat-one.
    pub fn next(&mut self, manual: bool) -> Option<usize> {
        let pos = self.pos?;
        if self.repeat == Repeat::One && !manual {
            return self.current();
        }
        if pos + 1 < self.order.len() {
            self.pos = Some(pos + 1);
        } else if self.repeat == Repeat::Off {
            return None;
        } else {
            // Wrapped around: a shuffled run gets a fresh order.
            if self.shuffle {
                let last = self.current();
                let all: Vec<usize> = self.source.clone();
                self.order = self.spread(all, last);
            }
            self.pos = Some(0);
        }
        self.current()
    }

    /// What `next` would play when this track ends by itself, without moving on. None at the end
    /// of a shuffled pass, whose next order isn't drawn until it's needed.
    pub fn peek_next(&self) -> Option<usize> {
        let pos = self.pos?;
        match self.repeat {
            Repeat::One => self.current(),
            _ if pos + 1 < self.order.len() => Some(self.order[pos + 1]),
            Repeat::All if !self.shuffle => self.order.first().copied(),
            _ => None,
        }
    }

    pub fn previous(&mut self) -> Option<usize> {
        let pos = self.pos?;
        self.pos = Some(pos.saturating_sub(1));
        self.current()
    }

    fn rebuild(&mut self, current: usize) {
        if self.shuffle {
            let rest: Vec<usize> = self
                .source
                .iter()
                .copied()
                .filter(|&t| t != current)
                .collect();
            let mut order = vec![current];
            order.extend(self.spread(rest, Some(current)));
            self.order = order;
            self.pos = Some(0);
        } else {
            self.order = self.source.clone();
            self.pos = Some(self.order.iter().position(|&t| t == current).unwrap_or(0));
        }
    }

    /// A random order with no repeats, where the same artist or album never plays twice in a row
    /// unless the list leaves no other choice. Each step picks an artist at random, weighted by
    /// how many of their tracks are left, then one of their tracks; an artist holding more than
    /// half of what's left is picked straight away, or they'd end up clumped at the end.
    fn spread(&mut self, tracks: Vec<usize>, mut previous: Option<usize>) -> Vec<usize> {
        let mut by_artist: Vec<Vec<usize>> = Vec::new();
        for t in tracks {
            match by_artist
                .iter_mut()
                .find(|a| self.info[a[0]].artist == self.info[t].artist)
            {
                Some(a) => a.push(t),
                None => by_artist.push(vec![t]),
            }
        }
        for a in &mut by_artist {
            self.shuffle_in_place(a);
        }
        let mut remaining: usize = by_artist.iter().map(Vec::len).sum();
        let mut result = Vec::with_capacity(remaining);
        while remaining > 0 {
            let same = |a: &Vec<usize>, p: Option<usize>| {
                p.is_some_and(|p| self.info[a[0]].artist == self.info[p].artist)
            };
            let mut allowed: Vec<usize> = (0..by_artist.len())
                .filter(|&i| !same(&by_artist[i], previous))
                .collect();
            if allowed.is_empty() {
                allowed = (0..by_artist.len()).collect(); // only the last artist is left
            }
            let dominant = *allowed
                .iter()
                .max_by_key(|&&i| by_artist[i].len())
                .unwrap_or(&allowed[0]);
            let chosen = if by_artist[dominant].len() * 2 > remaining {
                dominant
            } else {
                let total: usize = allowed.iter().map(|&i| by_artist[i].len()).sum();
                let mut roll = self.pick(total);
                *allowed
                    .iter()
                    .find(|&&i| {
                        let n = by_artist[i].len();
                        if roll < n {
                            true
                        } else {
                            roll -= n;
                            false
                        }
                    })
                    .unwrap_or(&allowed[allowed.len() - 1])
            };
            // Within the artist, avoid the album that just played (matters for compilations).
            let artist = &mut by_artist[chosen];
            let k = previous
                .and_then(|p| {
                    artist
                        .iter()
                        .position(|&t| self.info[t].album != self.info[p].album)
                })
                .unwrap_or(0);
            let t = artist.remove(k);
            if artist.is_empty() {
                by_artist.remove(chosen);
            }
            previous = Some(t);
            result.push(t);
            remaining -= 1;
        }
        result
    }

    fn shuffle_in_place(&mut self, v: &mut [usize]) {
        for i in (1..v.len()).rev() {
            let j = self.pick(i + 1);
            v.swap(i, j);
        }
    }

    /// A random number below `n` (xorshift; good enough for a play order).
    fn pick(&mut self, n: usize) -> usize {
        self.rng ^= self.rng << 13;
        self.rng ^= self.rng >> 7;
        self.rng ^= self.rng << 17;
        (self.rng % n.max(1) as u64) as usize
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn info(artists: &[&str]) -> Vec<Info> {
        artists
            .iter()
            .enumerate()
            .map(|(i, a)| Info {
                artist: a.to_string(),
                album: i / 3,
            })
            .collect()
    }

    #[test]
    fn in_order_with_repeat_all_and_off() {
        let mut q = Queue::new(false, Repeat::All);
        assert_eq!(q.start(info(&["a", "a", "b"]), Some(1)), Some(1));
        assert_eq!(q.peek_next(), Some(2));
        assert_eq!(q.next(false), Some(2));
        assert_eq!(q.peek_next(), Some(0)); // wraps
        assert_eq!(q.next(false), Some(0));
        q.repeat = Repeat::Off;
        q.next(false);
        q.next(false);
        assert_eq!(q.peek_next(), None);
        assert_eq!(q.next(false), None); // the end
    }

    #[test]
    fn repeat_one_stays_unless_skipped() {
        let mut q = Queue::new(false, Repeat::One);
        q.start(info(&["a", "b", "c"]), Some(0));
        assert_eq!(q.peek_next(), Some(0));
        assert_eq!(q.next(false), Some(0));
        assert_eq!(q.next(true), Some(1)); // the Next button moves on
        assert_eq!(q.previous(), Some(0));
        assert_eq!(q.previous(), Some(0)); // stops at the start
    }

    #[test]
    fn shuffle_plays_everything_once_and_spreads_artists() {
        let artists = ["a", "a", "a", "b", "b", "b", "c", "c", "c", "d"];
        for _ in 0..50 {
            let mut q = Queue::new(true, Repeat::Off);
            let mut seen = vec![q.start(info(&artists), None).expect("first")];
            while let Some(t) = q.next(false) {
                seen.push(t);
            }
            let mut sorted = seen.clone();
            sorted.sort();
            assert_eq!(sorted, (0..10).collect::<Vec<_>>()); // each once
            for w in seen.windows(2) {
                assert_ne!(artists[w[0]], artists[w[1]], "{seen:?}"); // never the same artist twice running
            }
        }
    }

    #[test]
    fn turning_shuffle_on_keeps_the_current_track() {
        let mut q = Queue::new(false, Repeat::All);
        q.start(info(&["a", "b", "c", "d"]), Some(2));
        q.set_shuffle(true);
        assert_eq!(q.current(), Some(2));
        q.set_shuffle(false);
        assert_eq!((q.current(), q.peek_next()), (Some(2), Some(3)));
    }

    #[test]
    fn repeat_names_round_trip() {
        for r in [Repeat::Off, Repeat::All, Repeat::One] {
            assert_eq!(Repeat::parse(r.name()), Some(r));
        }
        assert_eq!(Repeat::Off.cycled().cycled().cycled(), Repeat::Off);
    }
}
