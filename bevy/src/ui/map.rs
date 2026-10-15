//! Information dashboard from MapUI.cs: presentation-only map annotations and
//! a roster of actual accepted players, alongside the shared accepted quest log.
use super::*;
use crate::{
    player::{GameSnapshot, PlayerBody, PlayerCamera, WorldSessionEpoch},
    world::{WorldConfig, origin::WorldOrigin},
};
use bevy::{
    input::mouse::{MouseScrollUnit, MouseWheel},
    window::PrimaryWindow,
};
mod assets;
mod geometry;
mod render;
use assets::MapAssets;

#[derive(Resource, Default)]
pub(crate) struct MapDiagnostics(pub Value);

pub(super) struct MapPlugin;
impl Plugin for MapPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<crate::character::nameplate::OriginalFont>()
            .init_resource::<Dashboard>()
            .init_resource::<MapDiagnostics>()
            .init_resource::<MapAssets>()
            .add_systems(
                Update,
                (
                    sync,
                    input,
                    assets::mipmaps,
                    render::build,
                    render::font,
                    render::place,
                )
                    .chain()
                    .in_set(UiUpdate)
                    .after(npc::NpcUpdate)
                    .before(rebuild_overlay),
            );
    }
}
#[derive(Resource, Default)]
struct Dashboard {
    epoch: u64,
    map: String,
    view: geometry::View,
    cursor: Option<Vec2>,
    dragging: bool,
    roster: Vec<Person>,
    roster_time: f32,
    roster_scroll: f32,
    layout: Option<geometry::Layout>,
    position: Vec3,
    angle: f32,
}
#[derive(Clone, Debug, PartialEq)]
struct Person {
    id: u16,
    name: String,
    position: Vec3,
    me: bool,
}
pub(super) fn available(name: &str) -> bool {
    matches!(name, "PEI" | "Washington" | "Yukon")
}

fn sync(
    mut map: ResMut<Dashboard>,
    mut art: ResMut<MapAssets>,
    config: Res<WorldConfig>,
    state: Res<UiState>,
    model: Res<UiModel>,
    epoch: Res<WorldSessionEpoch>,
    snapshot: Res<GameSnapshot>,
    body: Query<&Transform, With<PlayerBody>>,
    camera: Query<&Transform, With<PlayerCamera>>,
    peers: Query<(&crate::avatar::remotes::RemotePlayer, &Transform)>,
    origin: Res<WorldOrigin>,
    time: Res<Time>,
    windows: Query<&Window, With<PrimaryWindow>>,
    server: Res<AssetServer>,
    mut images: ResMut<Assets<Image>>,
) {
    if map.epoch != epoch.0 || map.map != config.map_name || !model.connected {
        *map = Dashboard {
            epoch: epoch.0,
            map: config.map_name.clone(),
            ..default()
        };
    }
    if state.screen != Screen::Information || !model.connected || !available(&config.map_name) {
        return;
    }
    let Ok(window) = windows.single() else {
        return;
    };
    let layout = geometry::Layout::new(Vec2::new(window.width(), window.height()));
    map.view.clamp(layout.map.width());
    map.layout = Some(layout);
    art.load(&config, &server, &mut images);
    if let Ok(transform) = body.single() {
        map.position = origin.to_absolute(transform.translation).as_vec3();
    }
    if let Ok(transform) = camera.single() {
        map.angle = geometry::facing(transform.rotation);
    }
    map.roster_time -= time.delta_secs();
    if map.roster_time <= 0. {
        map.roster_time = 0.4;
        let owner = snapshot.0["playerId"].as_u64().unwrap_or(0) as u16;
        let mut roster = vec![Person {
            id: owner,
            name: snapshot.0["player"]["profile"]["name"]
                .as_str()
                .unwrap_or("Survivor")
                .into(),
            position: map.position,
            me: true,
        }];
        for (player, transform) in &peers {
            if player.id == owner {
                continue;
            }
            let name = snapshot.0["players"]
                .as_array()
                .into_iter()
                .flatten()
                .find(|r| r["playerId"].as_u64() == Some(player.id as u64))
                .and_then(|r| r["profile"]["name"].as_str())
                .unwrap_or("Survivor");
            roster.push(Person {
                id: player.id,
                name: name.into(),
                position: origin.to_absolute(transform.translation).as_vec3(),
                me: false,
            });
        }
        roster[1..].sort_by_key(|p| p.id);
        map.roster = roster;
    }
}
fn input(
    mut map: ResMut<Dashboard>,
    state: Res<UiState>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mouse: Res<ButtonInput<MouseButton>>,
    mut wheel: MessageReader<MouseWheel>,
    mut art: ResMut<MapAssets>,
    config: Res<WorldConfig>,
    server: Res<AssetServer>,
) {
    let events: Vec<_> = wheel.read().map(|e| (e.y, e.unit)).collect();
    if state.screen != Screen::Information {
        map.dragging = false;
        map.cursor = None;
        return;
    }
    let (Some(layout), Ok(window)) = (map.layout, windows.single()) else {
        return;
    };
    let cursor = window.cursor_position();
    if !mouse.pressed(MouseButton::Left) {
        map.dragging = false;
    }
    if let Some(at) = cursor {
        if mouse.just_pressed(MouseButton::Left) && layout.map.contains(at) {
            map.dragging = true;
            map.cursor = Some(at);
        }
        if map.dragging {
            if let Some(previous) = map.cursor {
                map.view.pan += at - previous;
                map.view.clamp(layout.map.width());
            }
        }
        if layout.map.contains(at) {
            for (delta, _) in events {
                if delta != 0. {
                    map.view.zoom_at(
                        at - layout.map.min,
                        1.18f32.powf(delta.signum()),
                        layout.map.width(),
                    );
                }
            }
            if mouse.just_pressed(MouseButton::Right) {
                map.view.marker(at - layout.map.min, layout.map.width());
            }
        } else if layout.roster.contains(at) {
            for (delta, unit) in events {
                map.roster_scroll = (map.roster_scroll
                    - delta
                        * if unit == MouseScrollUnit::Line {
                            28.
                        } else {
                            1.
                        })
                .max(0.);
            }
        }
    }
    map.cursor = cursor;
    art.tiles(&map.view, layout.map.width(), &config, &server);
}
#[cfg(test)]
mod tests;
