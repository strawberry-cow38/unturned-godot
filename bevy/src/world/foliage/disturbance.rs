//! Source DisturbanceField: nearest 32 press/wash sources and 1.6 s wake crumbs.
use crate::{
    avatar::remotes::RemotePlayer,
    entities::WorldItemReplica,
    player::{PlayerBody, PlayerCamera, WorldSessionEpoch},
    ui::UiModel,
    vehicle::VehicleBody,
};
use bevy::prelude::*;
use std::collections::HashMap;

pub(super) const MAX: usize = 32;
const WAKE_SECONDS: f32 = 1.6;
const WAKE_SPACING: f32 = 0.9;

#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) struct Slot {
    pub position: Vec4,
    pub response: Vec4,
}
impl Slot {
    pub fn press(position: Vec3, radius: f32) -> Self {
        Self {
            position: position.extend(radius),
            response: Vec4::new(
                (radius * 0.5).clamp(0.6, 2.4),
                (1. + radius * 0.2).clamp(1., 2.2),
                0.,
                radius.max(3.),
            ),
        }
    }
    fn player(position: Vec3) -> Self {
        Self {
            position: position.extend(3.3),
            response: Vec4::new(1.4, 1.4, 0., 3.5),
        }
    }
    fn wash(position: Vec3, radius: f32, intensity: f32) -> Self {
        let k = intensity.clamp(0., 1.);
        Self {
            position: position.extend(radius * k),
            response: Vec4::new(6. * k, 1.6, 1., 18.),
        }
    }
}
#[derive(Clone, Copy)]
struct Crumb {
    position: Vec3,
    radius: f32,
    time: f32,
}
#[derive(Resource)]
pub(super) struct Field {
    pub slots: Vec<Slot>,
    pub enabled: bool,
    wake: Vec<Crumb>,
    last_wake: HashMap<(Entity, bool), Vec3>,
    smooth: Option<Vec3>,
    session: u64,
}
impl Default for Field {
    fn default() -> Self {
        Self {
            slots: Vec::new(),
            enabled: true,
            wake: Vec::new(),
            last_wake: HashMap::new(),
            smooth: None,
            session: 0,
        }
    }
}
impl Field {
    fn drop_wake(&mut self, id: (Entity, bool), position: Vec3, radius: f32, time: f32) {
        if self
            .last_wake
            .get(&id)
            .is_some_and(|p| p.distance(position) < WAKE_SPACING)
        {
            return;
        }
        self.last_wake.insert(id, position);
        self.wake.push(Crumb {
            position,
            radius,
            time,
        });
    }
    fn pack(&mut self, camera: Vec3, range: f32, time: f32, mut candidates: Vec<Slot>) {
        self.wake.retain(|crumb| time - crumb.time < WAKE_SECONDS);
        for crumb in &self.wake {
            let fade = 1. - (time - crumb.time) / WAKE_SECONDS;
            if fade > 0. {
                candidates.push(Slot::press(crumb.position, crumb.radius * fade));
            }
        }
        candidates.retain(|s| {
            s.position.w > 0.01 && s.position.xz().distance_squared(camera.xz()) <= range * range
        });
        candidates.sort_by(|a, b| {
            a.position
                .xz()
                .distance_squared(camera.xz())
                .total_cmp(&b.position.xz().distance_squared(camera.xz()))
        });
        candidates.truncate(MAX);
        self.slots = candidates;
    }
    fn shift(&mut self, shift: Vec3) {
        if let Some(p) = &mut self.smooth {
            *p -= shift;
        }
        for p in self.last_wake.values_mut() {
            *p -= shift;
        }
        for crumb in &mut self.wake {
            crumb.position -= shift;
        }
        for slot in &mut self.slots {
            slot.position -= shift.extend(0.);
        }
    }
}
pub(crate) fn shift_origin(world: &mut World, shift: Vec3) {
    if let Some(mut field) = world.get_resource_mut::<Field>() {
        field.shift(shift);
    }
}

pub(super) fn gather(
    mut field: ResMut<Field>,
    time: Res<Time<Real>>,
    epoch: Res<WorldSessionEpoch>,
    model: Res<UiModel>,
    ui: Option<Res<crate::ui::UiState>>,
    camera: Query<&GlobalTransform, With<PlayerCamera>>,
    player: Query<(Entity, &GlobalTransform), With<PlayerBody>>,
    sources: Query<
        (
            Entity,
            &GlobalTransform,
            Option<&VehicleBody>,
            Option<&WorldItemReplica>,
            Option<&RemotePlayer>,
        ),
        Or<(
            With<VehicleBody>,
            With<WorldItemReplica>,
            With<RemotePlayer>,
        )>,
    >,
) {
    if field.session != epoch.0 {
        let enabled = field.enabled;
        *field = Field {
            session: epoch.0,
            enabled,
            ..default()
        };
    }
    if ui
        .as_ref()
        .is_some_and(|ui| crate::player::simulation_paused(ui))
    {
        return;
    }
    let now = time.elapsed_secs();
    let mut center = camera
        .single()
        .map_or(Vec3::ZERO, GlobalTransform::translation);
    let mut candidates = Vec::new();
    if model.connected {
        if let Ok((entity, pose)) = player.single() {
            let target = pose.translation();
            let point = match field.smooth {
                Some(p) if p.distance_squared(target) <= 100. => {
                    p.lerp(target, 1. - (-25. * time.delta_secs()).exp())
                }
                _ => target,
            };
            field.smooth = Some(point);
            center = point;
            field.drop_wake((entity, false), point, 3.3, now);
            candidates.push(Slot::player(point + Vec3::Y * 0.5));
        }
    } else {
        field.smooth = None;
    }
    field
        .last_wake
        .retain(|(entity, _), _| sources.contains(*entity) || player.contains(*entity));
    for (entity, pose, vehicle, item, remote) in &sources {
        if let Some(vehicle) = vehicle {
            let position = pose.translation();
            candidates.push(Slot::press(position, 4.2));
            field.drop_wake((entity, false), position, 4.2, now);
            if let Some((hub, radius, intensity)) = vehicle.foliage_wash() {
                let hub = pose.transform_point(hub);
                // Source also leaves a press wake from the live rotor node (before
                // its groundDrop/intensity are applied by Pack).
                field.drop_wake((entity, true), hub, radius, now);
                if intensity > 0.001 {
                    candidates.push(Slot::wash(hub - Vec3::Y * 2.2, radius, intensity));
                }
            }
        } else if item.is_some() {
            candidates.push(Slot::press(pose.translation(), 0.4));
        } else if remote.is_some() {
            candidates.push(Slot::press(pose.translation(), 0.6));
        }
    }
    field.pack(center, 160., now, candidates);
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn live_replica_sources_retire_on_despawn_and_session_change() {
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .init_resource::<Field>()
            .init_resource::<WorldSessionEpoch>()
            .init_resource::<UiModel>()
            .add_systems(Update, gather);
        app.world_mut()
            .spawn((PlayerCamera, GlobalTransform::from_translation(Vec3::ZERO)));
        let item = app
            .world_mut()
            .spawn((
                WorldItemReplica {
                    net_id: 7,
                    item_id: 4,
                },
                GlobalTransform::from_translation(Vec3::X * 2.),
            ))
            .id();
        app.update();
        assert_eq!(
            app.world().resource::<Field>().slots[0],
            Slot::press(Vec3::X * 2., 0.4)
        );
        app.world_mut().despawn(item);
        app.update();
        assert!(app.world().resource::<Field>().slots.is_empty());
        app.world_mut().resource_mut::<Field>().drop_wake(
            (Entity::from_bits(1), false),
            Vec3::X,
            4.2,
            0.,
        );
        app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
        app.update();
        assert!(app.world().resource::<Field>().wake.is_empty());
    }
    #[test]
    fn source_press_and_spool_squared_wash_are_distinct() {
        assert!(
            Slot::press(Vec3::ZERO, 4.2)
                .response
                .abs_diff_eq(Vec4::new(2.1, 1.84, 0., 4.2), 1e-6)
        );
        let wash = Slot::wash(Vec3::Y, 13., 0.5_f32.powi(2));
        assert_eq!(wash.position.w, 3.25);
        assert_eq!(wash.response, Vec4::new(1.5, 1.6, 1., 18.));
        assert_eq!(
            Slot::player(Vec3::ZERO).response,
            Vec4::new(1.4, 1.4, 0., 3.5)
        );
    }
    #[test]
    fn wake_spacing_age_and_radius_recompute_match_source() {
        let mut f = Field::default();
        let id = (Entity::from_bits(1), false);
        f.drop_wake(id, Vec3::ZERO, 4.2, 0.);
        f.drop_wake(id, Vec3::X * 0.89, 4.2, 0.1);
        assert_eq!(f.wake.len(), 1);
        f.drop_wake(id, Vec3::X, 4.2, 0.2);
        f.pack(Vec3::ZERO, 160., 0.8, vec![]);
        assert_eq!(f.slots[0].position.w, 2.1);
        assert_eq!(f.slots[0].response.x, 1.05);
        f.pack(Vec3::ZERO, 160., 1.6, vec![]);
        assert_eq!(f.slots.len(), 1);
        f.pack(Vec3::ZERO, 160., 1.81, vec![]);
        assert!(f.slots.is_empty());
    }
    #[test]
    fn nearest_cap_and_rebase_preserve_the_physical_field() {
        let mut f = Field::default();
        let slots = (0..40)
            .rev()
            .map(|i| Slot::press(Vec3::X * i as f32, 1.))
            .collect();
        f.pack(Vec3::ZERO, 160., 0., slots);
        assert_eq!(f.slots.len(), 32);
        assert_eq!(f.slots[31].position.x, 31.);
        f.smooth = Some(Vec3::new(1050., 3., -2.));
        f.drop_wake(
            (Entity::from_bits(1), false),
            Vec3::new(1050., 3., -2.),
            3.3,
            0.,
        );
        f.shift(Vec3::X * 1024.);
        assert_eq!(f.smooth.unwrap().x, 26.);
        assert_eq!(f.wake[0].position.x, 26.);
        assert_eq!(f.slots[0].position.x, -1024.);
    }
}
