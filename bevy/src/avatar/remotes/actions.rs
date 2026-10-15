//! Original ready holds, accepted melee events and gesture overlays. These only
//! pose the replica: they never select equipment or emit gameplay requests.
use super::*;
use crate::{avatar::gestures::Playback, entities::CoreEvent};

#[derive(Resource, Default)]
pub(super) struct Events {
    epoch: u64,
    last: u64,
}
#[derive(Component, Default)]
pub(super) struct Actions {
    pub(super) gesture: Playback,
    pub(super) swing: Option<(String, f32)>,
    held: u16,
}

fn clip(rig: &RigPlayback, asset: &str, suffix: &str, fallback: &str) -> Option<String> {
    let name = crate::avatar::held::clip(rig, asset, suffix, fallback);
    rig.data
        .anims
        .get(&name)
        .is_some_and(|clip| clip.length > 0.)
        .then_some(name)
}

pub(super) fn receive(
    mut events: MessageReader<CoreEvent>,
    mut cursor: ResMut<Events>,
    epoch: Res<WorldSessionEpoch>,
    snapshot: Res<GameSnapshot>,
    time: Res<Time>,
    catalog: Res<ItemCatalog>,
    mut bodies: Query<(&RemotePlayer, &RigPlayback, &held::Held, &mut Actions)>,
) {
    if cursor.epoch != epoch.0 {
        *cursor = Events {
            epoch: epoch.0,
            last: 0,
        };
        events.clear();
    }
    let incoming: Vec<_> = events
        .read()
        .filter(|event| event.sequence > cursor.last)
        .collect();
    let rows: HashMap<_, _> = replicas(&snapshot.0).collect();
    for (player, rig, held, mut action) in &mut bodies {
        let Some(row) = rows.get(&player.id) else {
            continue;
        };
        if action.held != held.id {
            action.swing = None;
            action.held = held.id;
        }
        action.gesture.adopt(
            row["gesture"].as_u64().unwrap_or(0) as u8,
            &catalog.gestures,
            &rig.data,
        );
        for event in incoming
            .iter()
            .filter(|e| e.data["playerId"].as_u64() == Some(player.id as u64))
        {
            if event.kind == "gesture" {
                let id = event.data["gesture"].as_u64().unwrap_or(0) as u8;
                if catalog
                    .gestures
                    .iter()
                    .any(|d| d.id == id && !d.loops && d.clip.is_some())
                {
                    action.gesture.play(id, &catalog.gestures, &rig.data);
                }
            } else if event.kind == "melee" && !held.gun {
                let strong = event.data["strong"].as_bool().unwrap_or(false);
                let selected = if held.id == 0 {
                    Some(if strong { "Punch_Right" } else { "Punch_Left" }.into())
                } else {
                    held.asset.as_deref().and_then(|asset| {
                        clip(
                            rig,
                            asset,
                            if strong { "_Strong" } else { "_Weak" },
                            if strong { "Melee_Strong" } else { "Melee_Weak" },
                        )
                    })
                };
                action.swing = selected
                    .filter(|name| rig.data.anims.contains_key(name))
                    .map(|name| (name, 0.));
            }
        }
        action
            .gesture
            .advance(time.delta_secs(), &catalog.gestures, &rig.data);
        if let Some((name, elapsed)) = &mut action.swing {
            *elapsed += time.delta_secs();
            if rig
                .data
                .anims
                .get(name)
                .is_none_or(|clip| *elapsed >= clip.length)
            {
                action.swing = None;
            }
        }
    }
    if let Some(last) = incoming.iter().map(|e| e.sequence).max() {
        cursor.last = last;
    }
}

pub(super) fn animate(
    snapshot: Res<GameSnapshot>,
    bodies: Query<(&RemotePlayer, &RigPlayback, &held::Held, &Actions)>,
    mut transforms: Query<&mut Transform>,
) {
    let rows: HashMap<_, _> = replicas(&snapshot.0).collect();
    for (player, rig, held, action) in &bodies {
        let Some(row) = rows.get(&player.id) else {
            continue;
        };
        let preserve = matches!(stance(row), "crouch" | "prone");
        let hold = held
            .asset
            .as_deref()
            .and_then(|asset| {
                clip(
                    rig,
                    asset,
                    "_Equip",
                    if held.gun { "Gun_Equip" } else { "Melee_Equip" },
                )
            })
            .map(|name| {
                let length = rig.data.anims[&name].length;
                (name, length)
            });
        if let Some((name, elapsed)) = action.swing.as_ref().or(hold.as_ref()) {
            if let Some(clip) = rig.data.anims.get(name) {
                crate::avatar::held::apply_upper_body(
                    rig,
                    clip,
                    *elapsed,
                    preserve,
                    &mut transforms,
                );
            }
        }
        if held.id == 0 {
            action.gesture.apply(rig, preserve, &mut transforms);
        }
    }
}
