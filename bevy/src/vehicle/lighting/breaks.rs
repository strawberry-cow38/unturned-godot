//! One-shot accepted glass feedback. Replica masks alone never replay a break.
use super::geometry::BuiltLighting;
use crate::{
    entities::CoreEvent, particles::glass::GlassShards, player::WorldSessionEpoch, ui::UiModel,
    vehicle::VehicleBody,
};
use bevy::prelude::*;
use std::collections::HashMap;

// Snapshots and reliable facts travel on separate channels. Preserve the first
// pre-break albedo until its fact arrives. Source road lamps restore fixed colors;
// lightbars stay grey forever after their first break, so no transition FIFO or
// renderer color on the wire is needed even if snapshots skip repair cycles.
#[derive(Component, Default)]
pub(super) struct BreakColors(HashMap<&'static str, ColorHistory>);
#[derive(Default)]
struct ColorHistory {
    intact: Option<Color>,
    first_broken: Option<Color>,
    initialized: bool,
    event_seen: bool,
}
impl BreakColors {
    pub(super) fn observe(
        &mut self,
        label: &'static str,
        broken: bool,
        previous: Color,
        applied: Color,
    ) {
        let history = self.0.entry(label).or_default();
        if broken {
            let first = if !history.initialized && label.starts_with("lightbar") {
                // A first replica already damaged supplies no earlier break fact.
                // Its source lightbar is already grey, including after repair.
                Color::srgb(0.12, 0.12, 0.12)
            } else {
                history.intact.unwrap_or(previous)
            };
            history.first_broken.get_or_insert(first);
        } else if !label.starts_with("lightbar")
            || history.first_broken.is_none() && !history.event_seen
        {
            history.intact = Some(applied);
        }
        history.initialized = true;
    }
    pub(super) fn dark_bar(&self, label: &'static str) -> bool {
        self.0
            .get(label)
            .is_some_and(|h| h.event_seen || h.first_broken.is_some())
    }
    fn take(&mut self, label: &'static str, live: Color) -> Color {
        let h = self.0.entry(label).or_default();
        let color = if label.starts_with("lightbar") && label != "lightbar_c" {
            if h.event_seen {
                Color::srgb(0.12, 0.12, 0.12)
            } else {
                h.first_broken.or(h.intact).unwrap_or(live)
            }
        } else {
            h.intact.or(h.first_broken).unwrap_or(live)
        };
        h.event_seen = true;
        color
    }
}

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
    mut bodies: Query<(&VehicleBody, &Transform, &BuiltLighting, &mut BreakColors)>,
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
        if event.kind != "vehicleLampBroken" {
            continue;
        }
        let (Some(id), Some(label), Some(position)) = (
            event.data["netId"].as_u64(),
            event.data["label"].as_str(),
            crate::player::vector(&event.data["position"]),
        ) else {
            continue;
        };
        let Some((_, pose, built, mut colors)) = bodies
            .iter_mut()
            .find(|(body, _, _, _)| body.net_id as u64 == id)
        else {
            continue;
        };
        let Some(lamp) = built.lamps.iter().find(|l| l.label == label) else {
            continue;
        };
        let live = glass
            .material_color(lamp.material.as_ref())
            .unwrap_or(Color::srgb(0.94, 0.89, 0.73));
        let color = colors.take(lamp.label, live);
        let outward = pose.rotation
            * if label.starts_with("taillight") {
                Vec3::Z
            } else {
                Vec3::NEG_Z
            };
        glass.spawn(
            &mut commands,
            position,
            outward,
            lamp.half_extents.max(Vec3::splat(0.06)),
            color.with_alpha(0.5),
            1.5,
            0.6,
            event.sequence,
        );
    }
}

#[cfg(test)]
mod tests;
