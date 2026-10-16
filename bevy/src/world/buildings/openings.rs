//! Source DoorDeploy/ObjectDoor and GlassPane values for the authored stage.
//! This is a preview consumer, not an authority registry or damage adapter.
use super::{SOURCE_PROP, data};
use crate::vehicle::glazing::rain::{GlassMaterial, GlassMaterialPlugin, GlassUniform};
use bevy::{
    asset::RenderAssetUsages,
    image::{CompressedImageFormats, ImageSampler, ImageType},
    prelude::*,
};
use bevy_rapier3d::prelude::*;
use serde::Deserialize;
use std::path::Path;

#[derive(Clone, Deserialize)]
pub(super) struct Pose {
    pub position: [f32; 3],
    pub basis: [[f32; 3]; 3],
}
impl Pose {
    fn transform(&self) -> Transform {
        data::pose(self.position, self.basis)
    }
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Door {
    pub opening_index: usize,
    pub name: String,
    pub pose: Pose,
    pub leaves: Vec<Leaf>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Leaf {
    pub mesh: String,
    pub scale: [f32; 3],
    pub pivot: [f32; 3],
    pub axis: [f32; 3],
    pub angle_degrees: f32,
    pub duration_seconds: f32,
    pub body: Pose,
    pub slide: [f32; 3],
    pub action: String,
    pub open_curve: Option<Vec<[f32; 2]>>,
    pub close_curve: Option<Vec<[f32; 2]>>,
    pub collider_center: [f32; 3],
    pub collider_size: [f32; 3],
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Pane {
    pub opening_index: usize,
    pub position: [f32; 3],
    pub size: [f32; 3],
    pub axis: [f32; 3],
    pub color: [f32; 4],
    pub health: f32,
    pub indestructible: bool,
    pub covered: bool,
    pub layer: u32,
    pub mask: u32,
}
#[derive(Component)]
pub(super) struct DoorLeaf {
    pub wall: usize,
    pub opening: usize,
    pub definition: Leaf,
}
#[derive(Component)]
pub(super) struct Glass {
    pub opening: usize,
    pub covered: bool,
}

pub(crate) struct BuildingOpeningPlugin;
impl Plugin for BuildingOpeningPlugin {
    fn build(&self, app: &mut App) {
        if !app.is_plugin_added::<GlassMaterialPlugin>() {
            app.add_plugins(GlassMaterialPlugin);
        }
        app.add_systems(PostUpdate, weather.after(TransformSystems::Propagate));
    }
}
fn weather(
    time: Res<Time>,
    rain: Option<Res<crate::world::rain::RainState>>,
    panes: Query<(&Glass, &GlobalTransform, &MeshMaterial3d<GlassMaterial>)>,
    mut materials: ResMut<Assets<GlassMaterial>>,
) {
    for (pane, pose, handle) in &panes {
        if let Some(mut material) = materials.get_mut(&handle.0) {
            material.uniform.world_from_local = pose.to_matrix();
            material.uniform.local_from_world = pose.to_matrix().inverse();
            material.uniform.weather = Vec4::new(
                rain.as_ref().map_or(0., |r| r.wetness),
                rain.as_ref().map_or(0., |r| r.intensity),
                if pane.covered { 1. } else { 0. },
                time.elapsed_secs() % 3600.,
            );
        }
    }
}

pub(super) struct Prepared {
    doors: Vec<(Door, Vec<Mesh>, Option<Image>)>,
}
pub(super) fn prepare(wall: &data::Wall, root: &Path) -> anyhow::Result<Prepared> {
    let mut doors = Vec::new();
    for door in &wall.door_models {
        let mut meshes = Vec::new();
        for leaf in &door.leaves {
            let source = crate::content::ObjData::load(&root.join("objects").join(&leaf.mesh))?;
            let mut scaled = crate::content::ObjData::default();
            scaled.append_transformed(&source, Transform::from_scale(leaf.scale.into()));
            // DoorDeploy.MatFor does not enable vertex-color-as-albedo.
            scaled.colors.fill([1.; 4]);
            meshes.push(scaled.mesh());
        }
        let texture = root.join("objects").join(format!("{}_tex.png", door.name));
        let image = std::fs::read(texture).ok().and_then(|bytes| {
            Image::from_buffer(
                &bytes,
                ImageType::Extension("png"),
                CompressedImageFormats::NONE,
                true,
                ImageSampler::nearest(),
                RenderAssetUsages::default(),
            )
            .ok()
        });
        doors.push((door.clone(), meshes, image));
    }
    Ok(Prepared { doors })
}
pub(super) fn spawn(world: &mut World, wall_entity: Entity, wall: &data::Wall, prepared: Prepared) {
    if prepared.doors.is_empty() && wall.glass_models.is_empty() {
        return;
    }
    world.entity_mut(wall_entity).insert(RigidBody::Fixed);
    world.init_resource::<Assets<Image>>();
    world.init_resource::<Assets<GlassMaterial>>();
    for (door, meshes, image) in prepared.doors {
        let texture = image.map(|image| world.resource_mut::<Assets<Image>>().add(image));
        let base = if texture.is_some() {
            Color::WHITE
        } else {
            Color::srgb(0.62, 0.62, 0.64)
        };
        let material = world
            .resource_mut::<Assets<StandardMaterial>>()
            .add(StandardMaterial {
                base_color: base,
                base_color_texture: texture,
                perceptual_roughness: 1.,
                double_sided: true,
                cull_mode: None,
                ..default()
            });
        let host = world
            .spawn((
                Name::new(format!("Opening {} {}", door.opening_index, door.name)),
                door.pose.transform(),
                Visibility::Inherited,
                ChildOf(wall_entity),
            ))
            .id();
        for (leaf, mesh) in door.leaves.into_iter().zip(meshes) {
            let body = world
                .spawn((leaf.body.transform(), Visibility::Inherited, ChildOf(host)))
                .id();
            let mesh = world.resource_mut::<Assets<Mesh>>().add(mesh);
            let [x, y, z] = leaf.collider_size;
            world.spawn((
                Name::new("Original fitted door leaf"),
                Mesh3d(mesh),
                MeshMaterial3d(material.clone()),
                pose(&leaf, 0., true, false),
                Collider::compound(vec![(
                    leaf.collider_center.into(),
                    Quat::IDENTITY,
                    Collider::cuboid(x * 0.5, y * 0.5, z * 0.5),
                )]),
                CollisionGroups::new(Group::GROUP_4 | SOURCE_PROP, Group::GROUP_2),
                DoorLeaf {
                    wall: wall.index,
                    opening: door.opening_index,
                    definition: leaf,
                },
                ChildOf(body),
            ));
        }
    }
    for pane in &wall.glass_models {
        let [x, y, z] = pane.size;
        let mesh = world
            .resource_mut::<Assets<Mesh>>()
            .add(Cuboid::new(x, y, z));
        let [r, g, b, a] = pane.color;
        let base = StandardMaterial {
            base_color: Color::srgba(r, g, b, a),
            metallic: 0.,
            perceptual_roughness: 0.06,
            ..default()
        };
        let local = Transform::from_translation(pane.position.into());
        let global = data::pose(wall.position, wall.basis)
            .mul_transform(local)
            .to_matrix();
        let material = world
            .resource_mut::<Assets<GlassMaterial>>()
            .add(GlassMaterial {
                uniform: GlassUniform::new(
                    &base,
                    pane.axis.into(),
                    global,
                    Vec4::new(0., 0., if pane.covered { 1. } else { 0. }, 0.),
                ),
            });
        world.spawn((
            Name::new(format!("Original opening glass {}", pane.opening_index)),
            Glass {
                opening: pane.opening_index,
                covered: pane.covered,
            },
            Mesh3d(mesh),
            MeshMaterial3d(material),
            local,
            Collider::cuboid(x * 0.5, y * 0.5, z * 0.5),
            CollisionGroups::new(Group::GROUP_4 | SOURCE_PROP, Group::GROUP_2),
            ChildOf(wall_entity),
        ));
    }
}
pub(super) fn pose(leaf: &Leaf, t: f32, opening: bool, reverse: bool) -> Transform {
    if leaf.action == "Pocket" {
        Transform::from_translation(Vec3::from(leaf.slide) * (t * t * (3. - 2. * t)))
    } else {
        let curve = if opening {
            leaf.open_curve.as_deref()
        } else {
            leaf.close_curve.as_deref()
        };
        crate::world::interactables::pivot_pose(
            leaf.pivot.into(),
            leaf.axis.into(),
            leaf.angle_degrees,
            crate::world::interactables::easing(t, opening, curve),
            reverse && leaf.action == "DoubleSwing",
        )
    }
}
/// Explicit preview clock; no UI intent or world authority is changed. A later
/// installed ObjectDoor consumer must also retain source occupied-volume retry.
pub(crate) fn preview_door(
    world: &mut World,
    wall: usize,
    opening: usize,
    t: f32,
    opening_motion: bool,
    reverse: bool,
) -> anyhow::Result<()> {
    anyhow::ensure!(
        t.is_finite() && (0. ..=1.).contains(&t),
        "invalid door preview fraction"
    );
    let leaves: Vec<_> = world
        .query::<(Entity, &DoorLeaf)>()
        .iter(world)
        .filter(|(_, l)| l.wall == wall && l.opening == opening)
        .map(|(e, l)| (e, pose(&l.definition, t, opening_motion, reverse)))
        .collect();
    for (entity, pose) in leaves {
        let mut e = world.entity_mut(entity);
        e.insert(pose);
        if t <= 0.001 || t >= 0.999 {
            e.remove::<ColliderDisabled>();
        } else {
            e.insert(ColliderDisabled);
        }
    }
    Ok(())
}

pub(super) fn validate(wall: &data::Wall) -> anyhow::Result<()> {
    let vector = |v: &[f32; 3]| v.iter().all(|f| f.is_finite());
    let mut ids = std::collections::HashSet::new();
    for door in &wall.door_models {
        anyhow::ensure!(
            ids.insert(door.opening_index) && data::valid_pose(door.pose.position, door.pose.basis),
            "invalid door identity/pose"
        );
        anyhow::ensure!(
            !door.name.contains(['/', '\\']) && !door.leaves.is_empty(),
            "invalid source door"
        );
        for l in &door.leaves {
            anyhow::ensure!(
                !l.mesh.contains(['/', '\\'])
                    && vector(&l.scale)
                    && l.scale.iter().all(|f| *f > 0.)
                    && vector(&l.pivot)
                    && vector(&l.axis)
                    && vector(&l.slide)
                    && vector(&l.collider_center)
                    && l.collider_size.iter().all(|f| f.is_finite() && *f > 0.)
                    && l.duration_seconds.is_finite()
                    && l.duration_seconds > 0.
                    && l.angle_degrees.is_finite()
                    && data::valid_pose(l.body.position, l.body.basis)
                    && ["Swing", "DoubleSwing", "Pocket"].contains(&l.action.as_str()),
                "invalid source door leaf"
            );
            for c in [&l.open_curve, &l.close_curve].into_iter().flatten() {
                anyhow::ensure!(
                    c.iter().flatten().all(|f| f.is_finite()),
                    "invalid source door curve"
                );
            }
        }
    }
    ids.clear();
    for p in &wall.glass_models {
        anyhow::ensure!(
            ids.insert(p.opening_index)
                && vector(&p.position)
                && p.size.iter().all(|f| f.is_finite() && *f > 0.)
                && vector(&p.axis)
                && (p.axis == [0.; 3] || (Vec3::from(p.axis).length_squared() - 1.).abs() < 1e-5)
                && p.color.iter().all(|f| f.is_finite())
                && p.health.is_finite()
                && p.health >= 1.
                && p.layer == 64
                && p.mask == 0,
            "invalid source pane"
        );
    }
    Ok(())
}
