//! A widget-local cursor lease. Player input establishes the ordinary mode in
//! Update; a live SpinBox drag overrides it after that update and restores the
//! press position on release. Raw motion is enabled only after capture applies.
use super::super::{controller::Menu, *};
use bevy::window::{CursorGrabMode, CursorOptions, PrimaryWindow};
#[derive(Resource, Default)]
pub(in crate::ui::inventory) struct Lease {
    owned: Option<(Entity, Vec2)>,
}
pub(in crate::ui::inventory) fn apply(
    mut lease: ResMut<Lease>,
    mut menu: ResMut<Menu>,
    ui: Res<crate::ui::UiState>,
    mut windows: Query<(Entity, &mut Window, &mut CursorOptions), With<PrimaryWindow>>,
) {
    let Ok((entity, mut window, mut cursor)) = windows.single_mut() else {
        return;
    };
    if !window.focused || !menu.open {
        menu.spin.cancel();
    }
    if menu.spin.dragging {
        lease.owned.get_or_insert((entity, menu.spin.anchor));
        cursor.visible = false;
        cursor.grab_mode = CursorGrabMode::Locked;
        menu.spin.raw_mode = true;
    } else if let Some((owner, restore)) = lease.owned.take() {
        if owner != entity {
            return;
        }
        if window.focused {
            window.set_cursor_position(Some(restore));
        }
        let playing = !ui.captures_input();
        cursor.visible = !playing;
        cursor.grab_mode = if playing {
            CursorGrabMode::Locked
        } else {
            CursorGrabMode::None
        };
    }
}
