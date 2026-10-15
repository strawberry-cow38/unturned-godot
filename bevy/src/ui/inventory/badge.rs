//! Source dashboard player badge; avatar bytes are fetched by accepted hash only.
use super::{controller::Menu, tiles};
use crate::{
    bridge::CoreBridge,
    character::nameplate::decode_picture,
    player::{GameSnapshot, WorldSessionEpoch},
};
use bevy::{picking::Pickable, prelude::*};
use serde_json::json;
#[derive(Resource, Default)]
pub(super) struct Badge {
    epoch: u64,
    hash: u64,
    owner: u64,
    retry: f32,
    pixels: u32,
    pub picture: Option<Handle<Image>>,
}
pub(super) fn refresh(
    bridge: Res<CoreBridge>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    time: Res<Time>,
    mut badge: ResMut<Badge>,
    mut menu: ResMut<Menu>,
    mut images: ResMut<Assets<Image>>,
) {
    let owner = snapshot.0["playerId"].as_u64().unwrap_or(0);
    let hash = snapshot.0["player"]["profile"]["avatarHash"]
        .as_u64()
        .unwrap_or(0);
    if badge.epoch != epoch.0 || badge.owner != owner || badge.hash != hash {
        *badge = Badge {
            epoch: epoch.0,
            owner,
            hash,
            ..default()
        };
        menu.dirty();
    }
    if !menu.open || hash == 0 || badge.picture.is_some() {
        return;
    }
    badge.retry -= time.delta_secs();
    if badge.retry > 0. {
        return;
    }
    badge.retry = 0.5;
    if badge.pixels == 0 {
        let Ok(reply) = bridge.request("profileRules", json!({})) else {
            return;
        };
        badge.pixels = reply["result"]["rules"]["avatarPixels"]
            .as_u64()
            .unwrap_or(0) as u32;
    }
    if badge.pixels == 0 {
        return;
    }
    let Ok(reply) = bridge.request("profileAvatar", json!({"playerId":owner,"avatarHash":hash}))
    else {
        return;
    };
    let r = &reply["result"];
    if r["status"] != "ready" || r["avatarHash"].as_u64() != Some(hash) {
        return;
    }
    let Some(bytes) = r["png"].as_array().and_then(|a| {
        a.iter()
            .map(|n| n.as_u64().and_then(|n| u8::try_from(n).ok()))
            .collect::<Option<Vec<_>>>()
    }) else {
        return;
    };
    badge.picture = Some(images.add(decode_picture(&bytes, badge.pixels)));
    menu.dirty();
}
pub(super) fn draw(
    p: &mut ChildSpawnerCommands,
    width: f32,
    name: &str,
    picture: Option<&Handle<Image>>,
    font: &Handle<Font>,
) {
    super::render::panel(
        p,
        Rect::new(10., 10., 66., 66.),
        crate::ui::navbar::neutral(0.17, 0.24, 0.32, 0.78),
        |p| {
            if let Some(image) = picture {
                p.spawn((
                    tiles::at(2., 2., 52., 52.),
                    ImageNode::new(image.clone()),
                    Pickable::IGNORE,
                ));
            }
        },
    );
    // Original VBox occupies the rest of the badge, reserving the plus marker.
    tiles::label(
        p,
        name,
        Rect::new(78., 8., width - 40., 43.),
        28.,
        crate::ui::ACCENT,
        font,
        Justify::Left,
    );
    tiles::label(
        p,
        "Neutral [0]",
        Rect::new(78., 43., width - 40., 68.),
        18.,
        Color::srgb(0.79, 0.79, 0.81),
        font,
        Justify::Left,
    );
    tiles::label(
        p,
        "+",
        Rect::new(width - 48., 20., width - 10., 58.),
        30.,
        crate::ui::ACCENT,
        font,
        Justify::Center,
    );
}
