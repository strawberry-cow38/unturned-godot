//! One original player per bus, retriggered only by an accepted transition.
use crate::{
    entities::CoreEvent,
    settings::Settings,
    vehicle::{VehicleBody, audio, data},
    world::WorldConfig,
};
use bevy::prelude::*;
#[derive(Component)]
pub(super) struct DoorVoice(Entity);
pub(super) fn events(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    bodies: Query<(Entity, &VehicleBody, Option<&DoorVoice>), With<super::Built>>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Res<Settings>,
) {
    // Several reliable edges can arrive in one rendered frame. Source owns one
    // AudioStreamPlayer3D and Play restarts it, so only the latest edge survives.
    let mut latest = std::collections::HashMap::new();
    for event in events.read().filter(|e| e.kind == "vehicleDoor") {
        if let Some(id) = event.data["netId"].as_u64() {
            latest.insert(id, event);
        }
    }
    for event in latest.into_values() {
        let Some((entity, body, old)) = bodies
            .iter()
            .find(|(_, b, _)| event.data["netId"].as_u64() == Some(b.net_id.into()))
        else {
            continue;
        };
        let Some(definition) = data::definition(body.type_id) else {
            continue;
        };
        if !config.content_root.join("busdoor_open.wav").is_file() {
            continue;
        }
        if let Some(old) = old
            && let Ok(mut voice) = commands.get_entity(old.0)
        {
            voice.despawn();
        }
        let voice = audio::spawn_sound(
            &mut commands,
            &assets,
            entity,
            "busdoor_open.wav",
            false,
            10_f32.powf(-2. / 20.),
            1.,
            6.,
            40.,
            settings.master_volume,
        );
        commands.entity(voice).insert((
            Name::new("BiFoldAudio"),
            Transform::from_translation(
                (Vec3::from(definition.spec.door_zone_min)
                    + Vec3::from(definition.spec.door_zone_max))
                    * 0.5,
            ),
        ));
        commands.entity(entity).insert(DoorVoice(voice));
    }
}
