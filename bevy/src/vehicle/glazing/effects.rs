use super::*;
use crate::{
    entities::CoreEvent, particles::glass::GlassShards, player::WorldSessionEpoch, ui::UiModel,
};

#[derive(Default)]
pub(super) struct Cursor {
    epoch: u64,
    sequence: u64,
}
pub(super) fn receive(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    epoch: Res<WorldSessionEpoch>,
    model: Res<UiModel>,
    mut cursor: Local<Cursor>,
    bodies: Query<(&VehicleBody, &GlobalTransform, &Glazing)>,
    panes: Query<(&Pane, &GlobalTransform)>,
    mut glass: GlassShards,
) {
    if cursor.epoch != epoch.0 {
        *cursor = Cursor {
            epoch: epoch.0,
            ..default()
        };
    }
    for event in events.read() {
        if !model.connected || event.sequence <= cursor.sequence {
            continue;
        }
        cursor.sequence = event.sequence;
        if event.kind != "vehicleGlassBroken" {
            continue;
        }
        let (Some(id), Some(label)) = (event.data["netId"].as_u64(), event.data["label"].as_str())
        else {
            continue;
        };
        let Some((_, hull, glazing)) = bodies.iter().find(|(b, _, _)| b.net_id as u64 == id) else {
            continue;
        };
        let Some((pane, pose)) = glazing
            .panes
            .iter()
            .filter_map(|e| panes.get(*e).ok())
            .find(|(p, _)| p.label() == label)
        else {
            continue;
        };
        let center = crate::player::vector(&event.data["position"])
            .unwrap_or_else(|| pose.transform_point(pane.center));
        let normal = crate::player::vector(&event.data["normal"])
            .unwrap_or_else(|| outward(pane, pose, hull.translation()));
        let half = crate::player::vector(&event.data["halfSize"])
            .unwrap_or(pane.size * 0.5)
            .max(Vec3::splat(0.2));
        // RainGlassMat is a ShaderMaterial, so source's StandardMaterial cast uses
        // this fallback even for the golden jet canopy. Debug panes use their tint.
        let c = pane
            .debug_color
            .unwrap_or(Color::srgb(0.62, 0.73, 0.78))
            .to_srgba();
        let tint = Color::srgba(
            c.red + (1. - c.red) * 0.35,
            c.green + (1. - c.green) * 0.35,
            c.blue + (1. - c.blue) * 0.35,
            0.5,
        );
        glass.spawn(
            &mut commands,
            center,
            normal,
            half,
            tint,
            1.4,
            0.8,
            event.sequence,
        );
    }
}
