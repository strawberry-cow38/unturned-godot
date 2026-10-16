//! Native half of the original WorldSaveDriver lifecycle. The host bridge owns
//! the file, the 60 s autosave and the console save/wipe verbs; the native client
//! names the per-map file (WorldSaveDriver.PathFor) and asks the host to restore
//! it once the map's authority registration is in place — the source's
//! "after the map has built, before anyone plays" window. Leaving and quitting
//! write nothing, exactly like MpLoopback/DedicatedServer teardown.
use super::Session;
use crate::{
    entities::Replicas,
    settings::Settings,
    ui::{Screen, UiIntent, UiModel, UiState},
    world::WorldConfig,
};
use bevy::prelude::*;
use std::path::{Path, PathBuf};

/// WorldSaveDriver.PathFor: `UG_SAVE_DIR` (or the native `UG_BEVY_SAVES`) moves
/// the whole directory; the file is the sanitized map id.
pub(crate) fn save_path(map: &str) -> PathBuf {
    save_dir().join(format!("{}.json", sanitize(map)))
}
fn save_dir() -> PathBuf {
    // Tests never touch a real save, even where UG_SAVE_DIR is exported.
    let overridden = if cfg!(test) {
        None
    } else {
        std::env::var_os("UG_BEVY_SAVES").or_else(|| std::env::var_os("UG_SAVE_DIR"))
    };
    overridden
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            Settings::path()
                .parent()
                .unwrap_or(Path::new("."))
                .join("saves")
        })
}

/// The earlier native pause-menu Save wrote ASCII-alphanumeric, case-kept names
/// (`PEI.json`). Adopt such a file once under the source name instead of
/// silently starting a fresh world beside it.
pub(crate) fn adopt_legacy_save(map: &str) -> PathBuf {
    adopt_legacy_in(&save_dir(), map)
}
fn adopt_legacy_in(dir: &Path, map: &str) -> PathBuf {
    let path = dir.join(format!("{}.json", sanitize(map)));
    let legacy: String = map
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() { c } else { '_' })
        .collect();
    let legacy = dir.join(format!("{legacy}.json"));
    if legacy != path && !path.exists() && legacy.is_file() {
        if let Err(error) = std::fs::rename(&legacy, &path) {
            warn!("Cannot adopt legacy save {}: {error}", legacy.display());
        }
    }
    path
}

/// The screen a connected host session shows: the loading screen while the
/// saved world is still owed, otherwise play.
pub(crate) fn entry_screen(restore_pending: bool) -> Screen {
    if restore_pending { Screen::Connecting } else { Screen::Playing }
}

/// WorldSaveDriver.Sanitize: letters, digits, '_' and '-' kept (lowercased),
/// anything else '_', and an empty id is "world".
pub(crate) fn sanitize(map: &str) -> String {
    let name: String = map
        .chars()
        .flat_map(|c| {
            let keep = c.is_alphanumeric() || c == '_' || c == '-';
            let lower: Vec<char> = if keep { c.to_lowercase().collect() } else { vec!['_'] };
            lower
        })
        .collect();
    if name.is_empty() { "world".into() } else { name }
}

/// Restore the saved world once the host has registered this map's services and
/// geometry: the same accepted Load path the pause menu uses, so every system
/// that adopts a restored world reacts identically.
pub(super) fn entry_restore(
    mut session: ResMut<Session>,
    replicas: Option<Res<Replicas>>,
    config: Option<Res<WorldConfig>>,
    model: Res<UiModel>,
    mut ui: ResMut<UiState>,
    mut intents: MessageWriter<UiIntent>,
) {
    // Partial apps without world services have nothing to restore into.
    let (Some(replicas), Some(config)) = (replicas, config) else {
        return;
    };
    // A failed authority setup has nothing to restore into either: release the
    // loading screen so its error is visible. The host keeps holding autosave,
    // so a half-registered world never overwrites the file.
    if session.restore_pending && replicas.setup_failed() {
        session.restore_pending = false;
        if ui.screen == Screen::Connecting {
            ui.screen = Screen::Playing;
        }
        return;
    }
    if !session.restore_pending
        || !session.initialized
        || !model.connected
        || !model.network.owns_world
        || !replicas.geometry_registered(&config.map_name)
    {
        return;
    }
    session.restore_pending = false;
    intents.write(UiIntent::Load);
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn legacy_native_name_is_adopted_once_and_never_overwrites_a_source_named_save() {
        let dir = std::env::temp_dir().join(format!("bevy-legacy-save-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("PEI.json"), b"legacy").unwrap();
        assert_eq!(adopt_legacy_in(&dir, "PEI"), dir.join("pei.json"));
        assert_eq!(std::fs::read(dir.join("pei.json")).unwrap(), b"legacy");
        assert!(!dir.join("PEI.json").exists());
        std::fs::write(dir.join("PEI.json"), b"older").unwrap();
        adopt_legacy_in(&dir, "PEI");
        assert_eq!(std::fs::read(dir.join("pei.json")).unwrap(), b"legacy", "never replaces the source file");
        assert_eq!(entry_screen(true), Screen::Connecting);
        assert_eq!(entry_screen(false), Screen::Playing);
        std::fs::remove_dir_all(&dir).unwrap();
    }
    #[test]
    fn save_identity_follows_source_path_for_sanitize() {
        assert_eq!(sanitize("PEI"), "pei");
        assert_eq!(sanitize("Washington"), "washington");
        assert_eq!(sanitize("My map-2_x"), "my_map-2_x");
        assert_eq!(sanitize("a/b\\c.d"), "a_b_c_d");
        assert_eq!(sanitize(""), "world");
        assert!(save_path("PEI").ends_with("pei.json"));
    }
    fn app() -> App {
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .add_message::<UiIntent>()
            .init_resource::<Replicas>()
            .insert_resource(WorldConfig {
                map_name: "PEI".into(),
                ..default()
            })
            .insert_resource(Session {
                initialized: true,
                restore_pending: true,
                ..default()
            })
            .init_resource::<UiModel>()
            .insert_resource(UiState {
                screen: Screen::Connecting,
                ..default()
            })
            .add_systems(Update, entry_restore);
        app
    }
    #[test]
    fn failed_authority_setup_releases_the_loading_screen_without_loading() {
        let mut app = app();
        let mut cursor = app.world().resource::<Messages<UiIntent>>().get_cursor();
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&json!({"connected":true,"network":{"mode":"local","ownsWorld":true}}));
        app.world_mut().resource_mut::<Replicas>().fail_setup_for_test();
        app.update();
        assert_eq!(loads(&mut app, &mut cursor), 0);
        assert!(!app.world().resource::<Session>().restore_pending);
        assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    }
    fn loads(app: &mut App, cursor: &mut bevy::ecs::message::MessageCursor<UiIntent>) -> usize {
        let messages = app.world().resource::<Messages<UiIntent>>();
        cursor
            .read(messages)
            .filter(|i| matches!(i, UiIntent::Load))
            .count()
    }
    #[test]
    fn entry_restore_waits_for_this_maps_authority_then_loads_exactly_once() {
        let mut app = app();
        let mut cursor = app.world().resource::<Messages<UiIntent>>().get_cursor();
        let cursor = &mut cursor;
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&json!({"connected":true,"network":{"mode":"local","ownsWorld":true}}));
        app.update();
        assert_eq!(loads(&mut app, cursor), 0, "the map is not registered with the host yet");
        app.world_mut()
            .resource_mut::<Replicas>()
            .mark_geometry_for_test("Washington");
        app.update();
        assert_eq!(loads(&mut app, cursor), 0, "another map's registration is not this world");
        app.world_mut()
            .resource_mut::<Replicas>()
            .mark_geometry_for_test("PEI");
        app.update();
        assert_eq!(loads(&mut app, cursor), 1);
        assert!(!app.world().resource::<Session>().restore_pending);
        app.update();
        assert_eq!(loads(&mut app, cursor), 0, "one restore per session");
    }
    #[test]
    fn remote_clients_never_restore_a_local_file() {
        let mut app = app();
        let mut cursor = app.world().resource::<Messages<UiIntent>>().get_cursor();
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&json!({"connected":true,"network":{"mode":"client","ownsWorld":false}}));
        app.world_mut()
            .resource_mut::<Replicas>()
            .mark_geometry_for_test("PEI");
        app.update();
        assert_eq!(loads(&mut app, &mut cursor), 0);
        assert!(app.world().resource::<Session>().restore_pending, "still owed to a host session only");
    }
}
