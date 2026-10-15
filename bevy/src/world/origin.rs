//! The source 256 m region origin: authority and native bodies rebase together.
use super::{WorldConfig, WorldGeometry, WorldState};
use crate::{
    bridge::CoreBridge,
    player::{GameSnapshot, PlayerBody, WorldSessionEpoch},
    ui::UiModel,
};
use bevy::{
    camera::visibility::RenderLayers,
    math::{DVec2, DVec3},
    prelude::*,
};
use bevy_rapier3d::prelude::*;
use serde_json::{Value, json};
use std::collections::HashSet;

#[derive(Resource, Default, Debug, Clone)]
pub struct WorldOrigin {
    pub offset: DVec2,
    pub epoch: u64,
    pub(super) session_epoch: u64,
}
impl WorldOrigin {
    pub fn to_local(&self, absolute: Vec3) -> Vec3 {
        self.local(absolute.as_dvec3())
    }
    pub fn local(&self, absolute: DVec3) -> Vec3 {
        Vec3::new(
            (absolute.x - self.offset.x) as f32,
            absolute.y as f32,
            (absolute.z - self.offset.y) as f32,
        )
    }
    pub fn to_absolute(&self, local: Vec3) -> DVec3 {
        DVec3::new(
            self.offset.x + local.x as f64,
            local.y as f64,
            self.offset.y + local.z as f64,
        )
    }
    pub fn shader_offset(&self) -> Vec2 {
        Vec2::new(
            self.offset.x.rem_euclid(32768.) as f32,
            self.offset.y.rem_euclid(32768.) as f32,
        )
    }
}
pub struct OriginPlugin;
impl Plugin for OriginPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<WorldOrigin>()
            .add_systems(First, (adopt_authority, automatic_rebase).chain())
            .add_systems(FixedFirst, adopt_authority)
            .add_systems(
                Update,
                adopt_authority
                    .after(crate::player::PlayerUpdate)
                    .before(crate::entities::WorldServicesReady)
                    .before(crate::vehicle::VehicleUpdate),
            )
            .add_systems(
                FixedUpdate,
                adopt_authority
                    .after(crate::player::PublishPlayer)
                    .before(crate::projectiles::ProjectileSync),
            );
    }
}
pub(super) fn parse_absolute(text: &str) -> Option<Vec<f64>> {
    text.split(',')
        .map(|v| v.trim().parse::<f64>().ok().filter(|v| v.is_finite()))
        .collect()
}
fn snapshot_origin(state: &Value) -> Option<(DVec2, u64)> {
    let origin = &state["origin"];
    let p = DVec2::new(origin["x"].as_f64()?, origin["z"].as_f64()?);
    p.is_finite().then_some((p, origin["epoch"].as_u64()?))
}
fn threshold_shift(position: Vec3, threshold: f32) -> Option<Vec3> {
    if position.x.abs() <= threshold && position.z.abs() <= threshold {
        return None;
    }
    let shift = Vec3::new(
        (position.x / 256.).floor() * 256.,
        0.,
        (position.z / 256.).floor() * 256.,
    );
    (shift != Vec3::ZERO && shift.is_finite()).then_some(shift)
}
/// Called before body/action publication and immediately after a response may
/// have rebased for respawn/load. No simulation step occurs inside this barrier.
pub(crate) fn adopt_authority(world: &mut World) {
    let Some(snapshot) = world.get_resource::<GameSnapshot>() else {
        return;
    };
    let Some((offset, epoch)) = snapshot_origin(&snapshot.0) else {
        return;
    };
    let current = world.resource::<WorldOrigin>().clone();
    let session = world.get_resource::<WorldSessionEpoch>().map_or(0, |s| s.0);
    if current.offset == offset && current.epoch == epoch && current.session_epoch == session {
        return;
    }
    let authority_state = snapshot.0.clone();
    let position = crate::player::vector(&authority_state["player"]["position"]);
    let delta = offset - current.offset;
    let shift = Vec3::new(delta.x as f32, 0., delta.y as f32);
    if shift != Vec3::ZERO {
        shift_world(world, shift, offset);
        crate::player::adopt_origin_correction(world, &authority_state);
        // publish_body/load may already have consumed the new-frame correction.
        // Restore the authoritative owner pose after translating the old scene.
        if let Some(position) = position {
            let entities: Vec<_> = world
                .query_filtered::<Entity, With<PlayerBody>>()
                .iter(world)
                .collect();
            for entity in entities {
                if let Some(mut transform) = world.get_mut::<Transform>(entity) {
                    transform.translation = position;
                }
                if let Some(mut global) = world.get_mut::<GlobalTransform>(entity) {
                    let mut affine = global.affine();
                    affine.translation = position.into();
                    *global = GlobalTransform::from(affine);
                }
                move_physics_owner(world, entity, position);
            }
        }
    }
    *world.resource_mut::<WorldOrigin>() = WorldOrigin {
        offset,
        epoch,
        session_epoch: session,
    };
    world.resource::<CoreBridge>().adopt_origin(epoch);
}
fn automatic_rebase(world: &mut World) {
    let config = world.resource::<WorldConfig>();
    if config.procedural_path.is_none() || !world.resource::<WorldState>().ready {
        return;
    }
    let snapshot = &world.resource::<GameSnapshot>().0;
    if snapshot["player"]["alive"].as_bool() != Some(true) {
        return;
    }
    let origin = world.resource::<WorldOrigin>().clone();
    let position = if let Some((position, _, _)) = config.fixed_camera {
        origin.local(position)
    } else {
        let Some(p) = world
            .query_filtered::<&Transform, With<PlayerBody>>()
            .iter(world)
            .next()
        else {
            return;
        };
        p.translation
    };
    let threshold = std::env::var("UG_INF_REBASE")
        .ok()
        .and_then(|s| s.parse::<f32>().ok())
        .filter(|v| v.is_finite() && *v > 0.)
        .unwrap_or(1024.);
    let Some(shift) = threshold_shift(position, threshold) else {
        return;
    };
    let response = world.resource::<CoreBridge>().request(
        "rebase",
        json!({"shift":shift.to_array(),"expectedEpoch":origin.epoch}),
    );
    match response {
        Ok(response) => {
            let Some((offset, epoch)) = snapshot_origin(&response["state"]) else {
                return;
            };
            shift_world(world, shift, offset);
            for (entity, position) in
                crate::player::adopt_origin_correction(world, &response["state"])
            {
                if let Some(mut global) = world.get_mut::<GlobalTransform>(entity) {
                    let mut affine = global.affine();
                    affine.translation = position.into();
                    *global = GlobalTransform::from(affine);
                }
                move_physics_owner(world, entity, position);
            }
            *world.resource_mut::<WorldOrigin>() = WorldOrigin {
                offset,
                epoch,
                ..origin
            };
            world.resource::<CoreBridge>().adopt_origin(epoch);
            world.resource_mut::<GameSnapshot>().0 = response["state"].clone();
            world
                .resource_mut::<UiModel>()
                .adopt_snapshot(&response["state"]);
            info!("World origin epoch {epoch}: local shift {shift:?}, absolute {offset:?}");
        }
        Err(error) => error!("World origin barrier refused: {error}"),
    }
}
fn shift_world(world: &mut World, shift: Vec3, offset: DVec2) {
    // UI and first-person render-layer cameras are separate coordinate spaces.
    // Every other spatial root, including audio/debris/replicas, moves once.
    let roots: Vec<_> = world
        .query_filtered::<(Entity, Option<&RenderLayers>, Has<super::water::WaterSurface>), (
            With<Transform>,
            Without<ChildOf>,
            Without<Node>,
            Without<Camera2d>,
            Without<DirectionalLight>,
        )>()
        .iter(world)
        .filter(|(_, layers, water)| *water || layers.is_none_or(|l| l.intersects(&RenderLayers::layer(0))))
        .map(|(entity, _, _)| entity)
        .collect();
    let mut shifted = HashSet::new();
    for root in roots {
        if let Some(mut transform) = world.get_mut::<Transform>(root) {
            transform.translation -= shift;
        }
        let mut pending = vec![root];
        while let Some(entity) = pending.pop() {
            if !shifted.insert(entity) {
                continue;
            }
            if let Some(children) = world.get::<Children>(entity) {
                pending.extend(children.iter());
            }
            if let Some(mut global) = world.get_mut::<GlobalTransform>(entity) {
                let mut affine = global.affine();
                affine.translation -= bevy::math::Vec3A::from(shift);
                *global = GlobalTransform::from(affine);
            }
        }
    }
    for mut interp in world.query::<&mut TransformInterpolation>().iter_mut(world) {
        if let Some(p) = &mut interp.start {
            p.translation -= shift;
        }
        if let Some(p) = &mut interp.end {
            p.translation -= shift;
        }
    }
    shift_physics(world, shift);
    if let Some(mut state) = world.get_resource_mut::<WorldState>() {
        state.spawn -= shift;
        state.focus -= shift;
    }
    if let Some(mut geometry) = world.get_resource_mut::<WorldGeometry>() {
        geometry.loaded_center -= shift;
        geometry.stream_anchor -= shift;
        for patch in &mut geometry.terrain {
            patch.origin -= shift.xz();
            if let Some(splat) = &mut patch.splat {
                std::sync::Arc::make_mut(splat).origin -= shift.xz();
            }
        }
        for object in &mut geometry.objects {
            object.min -= shift;
            object.max -= shift;
            object.transform = Mat4::from_translation(-shift) * object.transform;
            object.inverse = object.transform.inverse();
        }
        geometry.revision = geometry.revision.wrapping_add(1);
    }
    crate::player::shift_origin(world, shift);
    crate::vehicle::shift_origin(world, shift);
    super::interactables::shift_origin(world, shift);
    super::terrain::shift_origin(
        world,
        shift,
        Vec2::new(
            offset.x.rem_euclid(32768.) as f32,
            offset.y.rem_euclid(32768.) as f32,
        ),
    );
    for mut emitter in world
        .query::<&mut crate::particles::Emitter>()
        .iter_mut(world)
    {
        emitter.shift_origin(shift);
    }
    // Readers can consume a fact one frame after another reader. Translate the
    // retained messages in place, preserving sequence/cursors and deduplication.
    if let Some(mut events) = world.get_resource_mut::<Messages<crate::entities::CoreEvent>>() {
        let mut cursor = events.get_cursor();
        for event in cursor.read_mut(&mut events) {
            for key in ["pos", "position", "point", "sourcePos", "origin"] {
                if let Some(p) = crate::player::vector(&event.data[key]) {
                    event.data[key] = json!((p - shift).to_array());
                }
            }
        }
    }
    if let Some(mut services) = world.get_resource_mut::<crate::entities::ServiceManifest>() {
        for entry in services.objects.values_mut() {
            shift_entry(entry, shift);
        }
        for entry in services.resources.values_mut() {
            shift_entry(entry, shift);
        }
        for entry in &mut services.object_doors {
            shift_entry(entry, shift);
        }
        for entry in &mut services.container_doors {
            shift_entry(entry, shift);
        }
        for entry in &mut services.seats {
            shift_entry(entry, shift);
        }
    }
}
fn shift_entry(value: &mut Value, shift: Vec3) {
    for key in ["position", "interactionPosition"] {
        if let Some(p) = crate::player::vector(&value[key]) {
            value[key] = json!((p - shift).to_array());
        }
    }
}
/// Shift both current and next poses without changing velocity/sleep/timers.
/// Updating the collision pipeline now makes pre-SyncBackend player/vehicle rays
/// query the new frame immediately, without advancing the physics simulation.
fn shift_physics(world: &mut World, shift: Vec3) {
    for (mut bodies, mut colliders, mut simulation) in world
        .query::<(
            &mut RapierRigidBodySet,
            &mut RapierContextColliders,
            &mut RapierContextSimulation,
        )>()
        .iter_mut(world)
    {
        for (_, body) in bodies.bodies.iter_mut() {
            let mut next = *body.next_position();
            next.translation -= shift;
            body.set_translation(body.translation() - shift, false);
            body.set_next_kinematic_position(next);
        }
        for (_, collider) in colliders.colliders.iter_mut() {
            if collider.parent().is_none() {
                collider.set_translation(collider.translation() - shift);
            }
        }
        bodies.propagate_modified_body_positions_to_colliders(&mut colliders);
        bodies.rebuild_entity_maps();
        refresh_queries(&mut simulation, &mut bodies, &mut colliders);
    }
}
fn refresh_queries(
    simulation: &mut RapierContextSimulation,
    bodies: &mut RapierRigidBodySet,
    colliders: &mut RapierContextColliders,
) {
    simulation.collision_pipeline.step(
        simulation.integration_parameters.prediction_distance(),
        &mut simulation.islands,
        &mut simulation.broad_phase,
        &mut simulation.narrow_phase,
        &mut bodies.bodies,
        &mut colliders.colliders,
        &(),
        &(),
    );
}
fn move_physics_owner(world: &mut World, entity: Entity, position: Vec3) {
    if let Some(mut interpolation) = world.get_mut::<TransformInterpolation>(entity) {
        *interpolation = default();
    }
    let handle = world.get::<RapierRigidBodyHandle>(entity).map(|h| h.0);
    for (mut bodies, mut colliders, mut simulation) in world
        .query::<(
            &mut RapierRigidBodySet,
            &mut RapierContextColliders,
            &mut RapierContextSimulation,
        )>()
        .iter_mut(world)
    {
        if let Some(body) = handle.and_then(|h| bodies.bodies.get_mut(h)) {
            body.set_translation(position, false);
        }
        bodies.propagate_modified_body_positions_to_colliders(&mut colliders);
        refresh_queries(&mut simulation, &mut bodies, &mut colliders);
    }
}
#[cfg(test)]
mod tests;
