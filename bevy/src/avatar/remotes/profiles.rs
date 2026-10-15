//! Original accepted profile/nameplate adoption. Avatar bytes stay out of frame snapshots.
use super::*;
use crate::{
    bridge::CoreBridge,
    character::nameplate::{Nameplate, decode_picture},
    entities::CoreEvent,
};
use serde_json::json;

#[derive(Default)]
struct Profile {
    body: Option<Entity>,
    plate: Option<Entity>,
    raw_name: String,
    name: String,
    hash: u64,
    picture: Option<Handle<Image>>,
    ready: bool,
    retry_at: f32,
    avatar_pixels: u32,
}
#[derive(Resource, Default)]
pub(super) struct Profiles {
    epoch: u64,
    players: HashMap<u16, Profile>,
}

pub(super) fn update(
    mut commands: Commands,
    bridge: Option<Res<CoreBridge>>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    time: Res<Time<bevy::time::Real>>,
    mut profiles: ResMut<Profiles>,
    mut events: MessageReader<CoreEvent>,
    players: Query<(Entity, &RemotePlayer)>,
    mut plates: Query<&mut Nameplate>,
    mut images: ResMut<Assets<Image>>,
) {
    let arrivals: HashSet<_> = events
        .read()
        .filter(|e| e.kind == "profileAvatar")
        .filter_map(|e| e.data["avatarHash"].as_u64())
        .collect();
    if profiles.epoch != epoch.0 || !snapshot.0["connected"].as_bool().unwrap_or(false) {
        for (_, p) in profiles.players.drain() {
            if let Some(entity) = p.plate {
                commands.entity(entity).try_despawn();
            }
        }
        profiles.epoch = epoch.0;
    }
    if !snapshot.0["connected"].as_bool().unwrap_or(false) {
        return;
    }
    let live: HashMap<_, _> = players.iter().map(|(e, p)| (p.id, e)).collect();
    profiles
        .players
        .retain(|id, p| live.get(id).is_some_and(|e| Some(*e) == p.body));
    let Some(bridge) = bridge else {
        return;
    };
    for (id, row) in replicas(&snapshot.0) {
        let Some(&body) = live.get(&id) else {
            continue;
        };
        let Some(raw_name) = row["profile"]["name"].as_str() else {
            continue;
        };
        let p = profiles.players.entry(id).or_default();
        p.body = Some(body);
        if p.name.is_empty() || p.raw_name != raw_name {
            let Ok(reply) = bridge.request("profileRules", json!({"name":raw_name})) else {
                continue;
            };
            let Some(name) = reply["result"]["name"].as_str() else {
                continue;
            };
            p.name = name.to_owned();
            p.raw_name = raw_name.to_owned();
            p.avatar_pixels = reply["result"]["rules"]["avatarPixels"]
                .as_u64()
                .unwrap_or(0) as u32;
        }
        let hash = row["profile"]["avatarHash"].as_u64().unwrap_or(0);
        if hash != p.hash {
            p.hash = hash;
            p.picture = None;
            p.ready = false;
            p.retry_at = 0.;
        }
        if hash != 0 && !p.ready && (time.elapsed_secs() >= p.retry_at || arrivals.contains(&hash))
        {
            p.retry_at = time.elapsed_secs() + 0.5;
            if let Ok(reply) =
                bridge.request("profileAvatar", json!({"playerId":id,"avatarHash":hash}))
            {
                let result = &reply["result"];
                adopt_picture(p, result, &mut images);
            }
        }
        let value = Nameplate {
            name: p.name.clone(),
            picture: p.picture.clone(),
        };
        if let Some(mut plate) = p.plate.and_then(|e| plates.get_mut(e).ok()) {
            if *plate != value {
                *plate = value;
            }
        } else {
            p.plate = Some(
                commands
                    .spawn((
                        Name::new(format!("Player {id} profile")),
                        value,
                        Transform::from_xyz(0., Nameplate::HEIGHT, 0.),
                        ChildOf(body),
                    ))
                    .id(),
            );
        }
    }
}

fn adopt_picture(profile: &mut Profile, result: &Value, images: &mut Assets<Image>) {
    if result["status"] != "ready" || result["avatarHash"].as_u64() != Some(profile.hash) {
        return;
    }
    if let Some(bytes) = result["png"].as_array().and_then(|items| {
        items
            .iter()
            .map(|v| v.as_u64().and_then(|v| u8::try_from(v).ok()))
            .collect::<Option<Vec<u8>>>()
    }) {
        profile.picture = Some(images.add(decode_picture(&bytes, profile.avatar_pixels)));
        profile.ready = true;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pending_picture_can_arrive_without_profile_change_and_stale_hash_cannot_replace_it() {
        let mut profile = Profile {
            hash: u64::MAX - 4,
            avatar_pixels: 128,
            ..default()
        };
        let mut images = Assets::<Image>::default();
        let hash = profile.hash;
        adopt_picture(
            &mut profile,
            &json!({"status":"pending","avatarHash":hash}),
            &mut images,
        );
        assert!(!profile.ready);
        assert!(profile.picture.is_none());
        let bytes = include_bytes!("../../character/nameplate/tests/avatar.png");
        adopt_picture(
            &mut profile,
            &json!({"status":"ready","avatarHash":hash-1,"png":bytes.as_slice()}),
            &mut images,
        );
        assert!(profile.picture.is_none());
        adopt_picture(
            &mut profile,
            &json!({"status":"ready","avatarHash":hash,"png":bytes.as_slice()}),
            &mut images,
        );
        assert!(profile.ready);
        let accepted = profile.picture.clone().unwrap();
        assert_eq!(images.get(&accepted).unwrap().width(), 128);
        adopt_picture(
            &mut profile,
            &json!({"status":"ready","avatarHash":hash-1,"png":[0]}),
            &mut images,
        );
        assert_eq!(profile.picture, Some(accepted));
    }
}
