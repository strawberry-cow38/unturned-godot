//! InventoryUI's independent Idle_Stand portrait, sharing live character clothes
//! and body-held geometry. It consumes accepted state and never owns a body.
pub(super) mod stage;
use crate::{
    avatar::held_item::{self, Held, Library},
    character::{Appearance, CharacterLayers, CharacterUpdate},
    content::ContentCache,
    entities::ItemCatalog,
    player::{GameSnapshot, WorldSessionEpoch},
    rig::{RigAnimation, RigLibrary, RigPlayback, spawn_rig},
    world::WorldConfig,
};
use bevy::{
    camera::visibility::RenderLayers, mesh::skinning::SkinnedMeshInverseBindposes, prelude::*,
};

pub(super) const LAYER: usize = 19;
#[derive(Resource, Default)]
pub(super) struct Portrait {
    pub active: bool,
    pub yaw: f32,
    pub root: Option<Entity>,
    epoch: u64,
}
#[derive(Component)]
pub(super) struct Paperdoll;
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(super) struct PortraitUpdate;
pub(super) struct PaperdollPlugin;
impl Plugin for PaperdollPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Portrait>()
            .init_resource::<Library>()
            .init_resource::<ContentCache>()
            .add_systems(Update, sync.in_set(PortraitUpdate).before(RigAnimation))
            .add_systems(Update, pose.after(RigAnimation).before(CharacterUpdate));
    }
}
#[allow(clippy::too_many_arguments)]
fn sync(
    mut commands: Commands,
    mut portrait: ResMut<Portrait>,
    epoch: Res<WorldSessionEpoch>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut rigs: ResMut<RigLibrary>,
    mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut library: ResMut<Library>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut bodies: Query<
        (
            &mut Transform,
            &mut Visibility,
            &mut RigPlayback,
            &mut Appearance,
            &mut Held,
        ),
        With<Paperdoll>,
    >,
) {
    if portrait.epoch != epoch.0 || !snapshot.0["connected"].as_bool().unwrap_or(false) {
        if let Some(root) = portrait.root.take() {
            commands.entity(root).try_despawn();
        }
        portrait.epoch = epoch.0;
    }
    if portrait.root.is_none() && portrait.active && snapshot.0["connected"].as_bool() == Some(true)
    {
        if let Ok(root) = spawn_rig(
            &mut commands,
            &mut rigs,
            &mut meshes,
            &mut binds,
            &config.content_root.join("rig.json"),
            materials.add(StandardMaterial::default()),
            Transform::from_rotation(Quat::from_rotation_y(std::f32::consts::PI + portrait.yaw)),
            false,
            "Idle_Stand",
        ) {
            commands.entity(root).insert((
                Name::new("Inventory paperdoll"),
                Paperdoll,
                crate::avatar::outfit(&snapshot.0, false, &catalog),
                CharacterLayers(RenderLayers::layer(LAYER)),
                Held::default(),
            ));
            portrait.root = Some(root);
        }
    }
    let Some(root) = portrait.root else { return };
    let Ok((mut transform, mut visibility, mut rig, mut appearance, mut held)) =
        bodies.get_mut(root)
    else {
        return;
    };
    *visibility = if portrait.active {
        Visibility::Inherited
    } else {
        Visibility::Hidden
    };
    rig.paused = !portrait.active;
    if !portrait.active {
        return;
    }
    // Never inherit the walking/seated/ADS/reload pose of the physical owner.
    rig.play("Idle_Stand", true);
    transform.rotation = Quat::from_rotation_y(std::f32::consts::PI + portrait.yaw);
    let next = crate::avatar::outfit(&snapshot.0, false, &catalog);
    if *appearance != next {
        *appearance = next;
    }
    let id = snapshot.0["player"]["heldItemId"].as_u64().unwrap_or(0) as u16;
    held_item::sync(
        &mut commands,
        &mut held,
        &rig,
        id,
        &snapshot.0["weapon"]["attachments"],
        &RenderLayers::layer(LAYER),
        "Paperdoll",
        &catalog,
        &config,
        &assets,
        &mut library,
        &mut cache,
        &mut meshes,
        &mut materials,
    );
}
fn pose(
    bodies: Query<(&RigPlayback, &Held), With<Paperdoll>>,
    mut transforms: Query<&mut Transform>,
) {
    for (rig, held) in &bodies {
        held_item::apply_ready(rig, held, &mut transforms);
    }
}

#[cfg(test)]
mod tests;
