//! The notification-area icon, as in 2.x: left-click shows or hides Melora, right-click opens a
//! menu with what's playing, Play/Pause, Next, Previous, Show Melora and Quit, and the tooltip
//! says what's playing.

use std::sync::Arc;

use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
use tray_icon::{Icon, MouseButton, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Action {
    /// A left-click on the icon: show Melora, or hide it if it's in front.
    Toggle,
    PlayPause,
    Next,
    Previous,
    Show,
    Quit,
}

pub struct Tray {
    icon: TrayIcon,
    now: MenuItem,
    play: MenuItem,
    /// Playing, and whether it's radio, as the Play item last said.
    state: (bool, bool),
}

/// Windows cuts tray tooltips at 127 characters.
const MAX_TOOLTIP: usize = 127;

fn tooltip(text: &str) -> String {
    if text.chars().count() <= MAX_TOOLTIP {
        return text.to_string();
    }
    let mut cut: String = text.chars().take(MAX_TOOLTIP - 1).collect();
    cut.push('…');
    cut
}

impl Tray {
    /// Puts the icon in the notification area. `act` is called on the event-loop thread's side of
    /// the tray, so it should hand the action to the UI (as main.rs does). None if Windows refused.
    pub fn new(act: impl Fn(Action) + Send + Sync + 'static) -> Option<Tray> {
        let picture = image::load_from_memory(include_bytes!("../ui/icon-64.png"))
            .ok()?
            .to_rgba8();
        let (w, h) = picture.dimensions();
        let icon = Icon::from_rgba(picture.into_raw(), w, h).ok()?;

        let now = MenuItem::new("Nothing playing", false, None);
        let play = MenuItem::new("Play", true, None);
        let next = MenuItem::new("Next", true, None);
        let previous = MenuItem::new("Previous", true, None);
        let show = MenuItem::new("Show Melora", true, None);
        let quit = MenuItem::new("Quit\tCtrl+Q", true, None);
        let menu = Menu::new();
        menu.append_items(&[
            &now,
            &PredefinedMenuItem::separator(),
            &play,
            &next,
            &previous,
            &PredefinedMenuItem::separator(),
            &show,
            &quit,
        ])
        .ok()?;

        let act = Arc::new(act);
        let ids = [
            (play.id().clone(), Action::PlayPause),
            (next.id().clone(), Action::Next),
            (previous.id().clone(), Action::Previous),
            (show.id().clone(), Action::Show),
            (quit.id().clone(), Action::Quit),
        ];
        let on_menu = act.clone();
        MenuEvent::set_event_handler(Some(move |e: MenuEvent| {
            if let Some((_, a)) = ids.iter().find(|(id, _)| *id == e.id) {
                on_menu(*a);
            }
        }));
        TrayIconEvent::set_event_handler(Some(move |e: TrayIconEvent| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = e
            {
                act(Action::Toggle);
            }
        }));

        let icon = TrayIconBuilder::new()
            .with_icon(icon)
            .with_tooltip("Melora")
            .with_menu(Box::new(menu))
            .with_menu_on_left_click(false)
            .build()
            .ok()?;
        Some(Tray {
            icon,
            now,
            play,
            state: (false, false),
        })
    }

    /// A balloon on the tray icon, as 2.x shows the first time Melora hides there.
    pub fn show_balloon(&self, title: &str, text: &str) -> bool {
        // shortcut: tray-icon doesn't expose its window, so this relies on its hidden window's
        // class name ("tray_icon_app") and on our icon being its first (number 1); check both
        // if tray-icon is upgraded and the balloon stops showing.
        crate::window::tray_balloon("tray_icon_app", 1, title, text)
    }

    /// What's playing, for the tooltip and the top of the menu; empty for nothing.
    pub fn set_now_playing(&self, text: &str) {
        let shown = if text.is_empty() {
            "Nothing playing"
        } else {
            text
        };
        self.now.set_text(shown);
        let _ = self.icon.set_tooltip(Some(if text.is_empty() {
            "Melora".to_string()
        } else {
            tooltip(text)
        }));
    }

    /// Play/Pause says what it'll do.
    pub fn set_playing(&mut self, playing: bool, live: bool) {
        if (playing, live) != self.state {
            self.state = (playing, live);
            self.play.set_text(match (playing, live) {
                (false, _) => "Play",
                (true, true) => "Stop",
                (true, false) => "Pause",
            });
        }
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn long_tooltips_are_cut_to_fit() {
        assert_eq!(super::tooltip("Short"), "Short");
        let long = "x".repeat(200);
        let cut = super::tooltip(&long);
        assert_eq!(cut.chars().count(), 127);
        assert!(cut.ends_with('…'));
    }
}
