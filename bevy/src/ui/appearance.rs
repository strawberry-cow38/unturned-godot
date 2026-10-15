//! MainMenu.BuildAppearancePanel: the original 32 face textures, eight columns.
use super::*;
use crate::{bridge::CoreBridge, profile::LocalProfile};

#[derive(Component)]
struct Face(u8);

pub(super) fn panel(p: &mut ChildSpawnerCommands, profile: &LocalProfile, assets: &AssetServer) {
    p.spawn(text("APPEARANCE", 28., TEXT));
    p.spawn(text("Face", 17., DIM));
    if profile.ready {
        p.spawn(Node {
            flex_direction: FlexDirection::Column,
            row_gap: px(6),
            ..default()
        })
        .with_children(|p| {
            for row_index in 0..usize::from(profile.face_count).div_ceil(8) {
                p.spawn((Node {
                    flex_direction: FlexDirection::Row,
                    column_gap: px(6),
                    ..default()
                },))
                    .with_children(|row| {
                        for index in
                            row_index * 8..((row_index + 1) * 8).min(profile.face_count as usize)
                        {
                            let face = index as u8;
                            let tint = if profile.face == face { 1. } else { 0.55 };
                            row.spawn((
                                Button,
                                Face(face),
                                Node {
                                    width: px(64),
                                    height: px(64),
                                    border: UiRect::all(px(2)),
                                    padding: UiRect::all(px(4)),
                                    ..default()
                                },
                                BackgroundColor(BUTTON),
                                BorderColor::all(if profile.face == face {
                                    Color::WHITE
                                } else {
                                    Color::srgb(0.55, 0.55, 0.55)
                                }),
                            ))
                            .observe(choose)
                            .with_children(|button| {
                                button.spawn((
                                    ImageNode {
                                        image: assets.load(format!("faces/face_{index}.png")),
                                        color: Color::srgb(tint, tint, tint),
                                        ..default()
                                    },
                                    BackgroundColor(Color::srgb(
                                        0.82 * tint,
                                        0.66 * tint,
                                        0.52 * tint,
                                    )),
                                    Node {
                                        width: percent(100),
                                        height: percent(100),
                                        ..default()
                                    },
                                ));
                            });
                        }
                    });
            }
        });
        p.spawn(text(
            format!("{} - Face {}", profile.name, profile.face),
            15.,
            TEXT,
        ));
        p.spawn(text(
            if profile.has_avatar {
                "Profile picture loaded"
            } else {
                "No profile picture"
            },
            13.,
            DIM,
        ));
    }
    for notice in [&profile.avatar_notice, &profile.save_notice]
        .into_iter()
        .flatten()
    {
        p.spawn(text(notice, 13., DIM));
    }
    button(p, "Back", UiButton::Open(Screen::MainMenu));
}

fn choose(
    event: On<Activate>,
    faces: Query<&Face>,
    state: Res<UiState>,
    mut profile: ResMut<LocalProfile>,
    bridge: Option<Res<CoreBridge>>,
) {
    if state.screen != Screen::Appearance {
        return;
    }
    if let (Ok(face), Some(bridge)) = (faces.get(event.entity), bridge) {
        profile.select(&bridge, face.0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn actual_button_persists_preference_without_changing_a_player_replica() {
        let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        let path =
            std::env::temp_dir().join(format!("bevy-face-button-{}.cfg", std::process::id()));
        let reply = bridge
            .request("localProfile", json!({"path":path,"name":"Face menu"}))
            .unwrap();
        let mut profile: LocalProfile = serde_json::from_value(reply["result"].clone()).unwrap();
        profile.ready = true;
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .insert_resource(bridge)
            .insert_resource(profile)
            .insert_resource(UiState {
                screen: Screen::Appearance,
                ..default()
            })
            .insert_resource(crate::player::GameSnapshot(
                json!({"player":{"profile":{"face":2}}}),
            ));
        let face = app
            .world_mut()
            .spawn((Button, Face(31)))
            .observe(choose)
            .id();
        app.update();
        app.world_mut().trigger(Activate { entity: face });
        assert_eq!(app.world().resource::<LocalProfile>().face, 31);
        assert!(std::fs::read_to_string(&path).unwrap().contains("face=31"));
        assert_eq!(
            app.world().resource::<crate::player::GameSnapshot>().0["player"]["profile"]["face"],
            2
        );
        app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
        let stray = app
            .world_mut()
            .spawn((Button, Face(3)))
            .observe(choose)
            .id();
        app.world_mut().trigger(Activate { entity: stray });
        assert_eq!(
            app.world().resource::<LocalProfile>().face,
            31,
            "Main-menu controls cannot mutate an active session"
        );
        std::fs::remove_file(path).unwrap();
    }
    #[test]
    fn menu_uses_every_original_face_and_returns_to_menu_without_starting_play() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, AssetPlugin::default()));
        app.init_asset::<Image>();
        app.insert_resource(LocalProfile {
            ready: true,
            face: 19,
            face_count: 32,
            ..default()
        });
        app.insert_resource(UiState {
            screen: Screen::Appearance,
            ..default()
        });
        app.add_systems(
            Startup,
            |mut commands: Commands, profile: Res<LocalProfile>, assets: Res<AssetServer>| {
                commands
                    .spawn(Node::default())
                    .with_children(|p| panel(p, &profile, &assets));
            },
        );
        app.update();
        let world = app.world_mut();
        let mut faces = world.query::<(&Face, &BorderColor)>();
        let mut ids: Vec<_> = faces.iter(world).map(|(f, _)| f.0).collect();
        ids.sort();
        assert_eq!(ids, (0..32).collect::<Vec<_>>());
        let selected = faces.iter(world).find(|(f, _)| f.0 == 19).unwrap().1;
        assert_eq!(*selected, BorderColor::all(Color::WHITE));
        world.resource_mut::<UiState>().close_overlay();
        assert_eq!(world.resource::<UiState>().screen, Screen::MainMenu);
    }
}
