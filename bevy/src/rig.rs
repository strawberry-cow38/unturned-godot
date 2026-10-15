//! Native Bevy skinning for the extracted human/animal rigs and their original clips.
//! JSON positions, UVs, inverse binds and tracks are already in the game's runtime
//! coordinate frame; unlike Wavefront UVs, these UVs must not be flipped again.
use crate::content::ContentError;
use bevy::{
    asset::RenderAssetUsages,
    camera::visibility::DynamicSkinnedMeshBounds,
    mesh::{
        Indices, PrimitiveTopology, VertexAttributeValues,
        skinning::{SkinnedMesh, SkinnedMeshInverseBindposes},
    },
    prelude::*,
};
use serde::Deserialize;
use std::{
    collections::HashMap,
    fs,
    path::{Path, PathBuf},
    sync::Arc,
};

pub struct RigPlugin;
/// Skeletons whose original AnimationPlayer runs at the 50 Hz physics cadence.
#[derive(Component)]
pub struct RigFixedAnimation;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct RigFixedUpdate;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct RigAnimation;
impl Plugin for RigPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<RigLibrary>()
            .add_systems(Update, animate_rigs.in_set(RigAnimation))
            .add_systems(
                FixedUpdate,
                animate_fixed_rigs
                    .in_set(RigFixedUpdate)
                    .after(bevy_rapier3d::plugin::PhysicsSet::Writeback),
            );
    }
}
#[derive(Clone, Deserialize, Debug)]
pub struct Bone {
    pub name: String,
    pub parent: i32,
    pub pos: [f32; 3],
    pub rot: [f32; 4],
    pub scale: [f32; 3],
}
impl Bone {
    fn transform(&self) -> Transform {
        Transform {
            translation: Vec3::from_array(self.pos),
            rotation: Quat::from_array(self.rot).normalize(),
            scale: Vec3::from_array(self.scale),
        }
    }
}
#[derive(Clone, Deserialize, Debug)]
pub struct SkinBind {
    pub bone: usize,
    pub pos: [f32; 3],
    pub rot: [f32; 4],
    pub scale: [f32; 3],
}
impl SkinBind {
    fn matrix(&self) -> Mat4 {
        Mat4::from_scale_rotation_translation(
            Vec3::from_array(self.scale),
            Quat::from_array(self.rot).normalize(),
            Vec3::from_array(self.pos),
        )
    }
}
#[derive(Clone, Deserialize, Debug, Default)]
pub struct RigMesh {
    pub positions: Vec<[f32; 3]>,
    pub normals: Vec<[f32; 3]>,
    pub uvs: Vec<[f32; 2]>,
    pub skin_index: Vec<Vec<u16>>,
    pub skin_weight: Vec<Vec<f32>>,
    pub faces: Vec<u32>,
}
#[derive(Clone, Deserialize, Debug, Default)]
pub struct Track {
    #[serde(default)]
    pub pos: Option<Vec<[f32; 4]>>,
    #[serde(default)]
    pub rot: Option<Vec<[f32; 5]>>,
    #[serde(default)]
    pub scale: Option<Vec<[f32; 4]>>,
}
#[derive(Clone, Deserialize, Debug)]
pub struct Clip {
    pub length: f32,
    #[serde(rename = "loop", default)]
    pub looping: bool,
    #[serde(default)]
    pub tracks: HashMap<String, Track>,
}
#[derive(Clone, Deserialize, Debug)]
pub struct RigData {
    #[serde(flatten)]
    pub mesh: RigMesh,
    pub bones: Vec<Bone>,
    pub skin: Vec<SkinBind>,
    pub anims: HashMap<String, Clip>,
    pub arms: Option<RigMesh>,
}
impl RigData {
    pub fn load(path: &Path) -> Result<Self, ContentError> {
        let mut data: Self = serde_json::from_slice(&fs::read(path)?)
            .map_err(|e| ContentError(format!("{}: {e}", path.display())))?;
        if path.file_name().is_some_and(|name| name == "rig.json") {
            for bank in ["consumable_anims.json", "throwable_anims.json"] {
                let bank = path.with_file_name(bank);
                if bank.is_file() {
                    let clips: HashMap<String, Clip> = serde_json::from_slice(&fs::read(&bank)?)
                        .map_err(|e| ContentError(format!("{}: {e}", bank.display())))?;
                    data.anims.extend(clips);
                }
            }
        }
        data.validate()?;
        Ok(data)
    }
    pub fn validate(&self) -> Result<(), ContentError> {
        if self.bones.is_empty() || self.bones.len() > 1024 || self.skin.is_empty() {
            return Err(ContentError("invalid rig bone count".into()));
        }
        for (i, bone) in self.bones.iter().enumerate() {
            if bone.parent >= i as i32 || bone.parent < -1 {
                return Err(ContentError(format!(
                    "bone {} has invalid parent",
                    bone.name
                )));
            }
            if bone
                .pos
                .iter()
                .chain(&bone.rot)
                .chain(&bone.scale)
                .any(|v| !v.is_finite())
                || Quat::from_array(bone.rot).length_squared() < 1e-12
            {
                return Err(ContentError(format!(
                    "bone {} has invalid transform",
                    bone.name
                )));
            }
        }
        if self.skin.iter().any(|b| b.bone >= self.bones.len()) {
            return Err(ContentError("skin bind refers to missing bone".into()));
        }
        self.validate_mesh(&self.mesh)?;
        if let Some(arms) = &self.arms {
            self.validate_mesh(arms)?;
        }
        for clip in self.anims.values() {
            if !clip.length.is_finite() || clip.length < 0. {
                return Err(ContentError("invalid clip length".into()));
            }
            for track in clip.tracks.values() {
                if !valid_keys(track.pos.as_deref())
                    || !valid_keys(track.rot.as_deref())
                    || !valid_keys(track.scale.as_deref())
                {
                    return Err(ContentError("invalid animation keyframes".into()));
                }
            }
        }
        Ok(())
    }
    fn validate_mesh(&self, mesh: &RigMesh) -> Result<(), ContentError> {
        let n = mesh.positions.len();
        if n == 0
            || mesh.normals.len() != n
            || mesh.uvs.len() != n
            || mesh.skin_index.len() != n
            || mesh.skin_weight.len() != n
            || mesh.faces.len() % 3 != 0
            || mesh.faces.iter().any(|i| *i as usize >= n)
        {
            return Err(ContentError(
                "rig vertex attribute lengths/indices disagree".into(),
            ));
        }
        for i in 0..n {
            if mesh.skin_index[i].len() != mesh.skin_weight[i].len()
                || mesh.skin_index[i].is_empty()
                || mesh.skin_index[i].len() > 4
                || mesh.skin_index[i]
                    .iter()
                    .any(|j| *j as usize >= self.skin.len())
            {
                return Err(ContentError("invalid rig vertex skinning indices".into()));
            }
            if mesh.positions[i]
                .iter()
                .chain(&mesh.normals[i])
                .chain(&mesh.uvs[i])
                .chain(mesh.skin_weight[i].iter())
                .any(|v| !v.is_finite())
            {
                return Err(ContentError("non-finite rig vertex".into()));
            }
        }
        Ok(())
    }
    pub fn build_mesh(&self, arms_only: bool) -> Result<Mesh, ContentError> {
        let src = if arms_only {
            self.arms.as_ref().unwrap_or(&self.mesh)
        } else {
            &self.mesh
        };
        let mut joints = Vec::with_capacity(src.positions.len());
        let mut weights = Vec::with_capacity(src.positions.len());
        for i in 0..src.positions.len() {
            let mut j = [0u16; 4];
            let mut w = [0_f32; 4];
            for n in 0..src.skin_index[i].len() {
                j[n] = src.skin_index[i][n];
                w[n] = src.skin_weight[i][n].max(0.);
            }
            let sum: f32 = w.iter().sum();
            if sum < 1e-6 {
                w[0] = 1.;
            } else {
                for v in &mut w {
                    *v /= sum;
                }
            }
            joints.push(j);
            weights.push(w);
        }
        let mut indices = src.faces.clone();
        for tri in indices.chunks_exact_mut(3) {
            let a = Vec3::from_array(src.positions[tri[0] as usize]);
            let b = Vec3::from_array(src.positions[tri[1] as usize]);
            let c = Vec3::from_array(src.positions[tri[2] as usize]);
            let n = Vec3::from_array(src.normals[tri[0] as usize])
                + Vec3::from_array(src.normals[tri[1] as usize])
                + Vec3::from_array(src.normals[tri[2] as usize]);
            if (b - a).cross(c - a).dot(n) < 0. {
                tri.swap(1, 2);
            }
        }
        Mesh::new(
            PrimitiveTopology::TriangleList,
            RenderAssetUsages::default(),
        )
        .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, src.positions.clone())
        .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, src.normals.clone())
        .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, src.uvs.clone())
        .with_inserted_attribute(
            Mesh::ATTRIBUTE_JOINT_INDEX,
            VertexAttributeValues::Uint16x4(joints),
        )
        .with_inserted_attribute(Mesh::ATTRIBUTE_JOINT_WEIGHT, weights)
        .with_inserted_indices(Indices::U32(indices))
        .with_generated_skinned_mesh_bounds()
        .map_err(|e| ContentError(format!("invalid skinned mesh bounds: {e:?}")))
    }
}
fn valid_keys<const N: usize>(keys: Option<&[[f32; N]]>) -> bool {
    keys.is_none_or(|keys| {
        keys.iter().all(|k| k.iter().all(|v| v.is_finite()))
            && keys.windows(2).all(|p| p[0][0] <= p[1][0])
    })
}
#[derive(Resource, Default)]
pub struct RigLibrary {
    pub data: HashMap<PathBuf, Arc<RigData>>,
    meshes: HashMap<(PathBuf, bool), Handle<Mesh>>,
    bindposes: HashMap<PathBuf, Handle<SkinnedMeshInverseBindposes>>,
}
#[derive(Component)]
pub struct RigPlayback {
    pub data: Arc<RigData>,
    pub bones: HashMap<String, Entity>,
    pub clip: String,
    pub time: f32,
    pub speed: f32,
    pub looping: bool,
    pub paused: bool,
    /// Optional upper-body/action layer; empty mask means every track in the layer.
    pub overlay: Option<RigOverlay>,
    /// Source SetupAimAdditive: end minus start, applied in the parent frame.
    pub additive: Option<RigAdditive>,
}
#[derive(Clone)]
pub struct RigAdditive {
    pub clip: String,
    pub weight: f32,
}
#[derive(Clone)]
pub struct RigOverlay {
    pub clip: String,
    pub time: f32,
    pub weight: f32,
    pub mask: Vec<String>,
    pub looping: bool,
}
impl RigPlayback {
    pub fn play(&mut self, clip: &str, looping: bool) -> bool {
        if !self.data.anims.contains_key(clip) {
            return false;
        }
        if self.clip != clip {
            self.clip = clip.into();
            self.time = 0.;
        }
        self.looping = looping;
        true
    }
    /// Held-item tracks share the same sampler as bones. Paths such as Item_Root/Bone_0
    /// remain distinct, matching the source's attachment-path fix.
    pub fn bind_item_track(&mut self, path: String, entity: Entity) {
        self.bones.insert(path, entity);
    }
}

/// The mesh entity is exposed for per-camera visibility layers (first-person arms).
#[derive(Component)]
pub struct RigSkin(pub Entity);

/// Spawn a complete skin + skeleton. Returned root owns all bone/mesh entities and
/// carries RigPlayback. Materials and clip names are caller-selected game state.
#[allow(clippy::too_many_arguments)]
pub fn spawn_rig<M: bevy::pbr::Material>(
    commands: &mut Commands,
    library: &mut RigLibrary,
    meshes: &mut Assets<Mesh>,
    bindposes: &mut Assets<SkinnedMeshInverseBindposes>,
    path: &Path,
    material: Handle<M>,
    transform: Transform,
    arms_only: bool,
    clip: &str,
) -> Result<Entity, ContentError> {
    let data = if let Some(data) = library.data.get(path) {
        data.clone()
    } else {
        let data = Arc::new(RigData::load(path)?);
        library.data.insert(path.into(), data.clone());
        data
    };
    let key = (path.to_owned(), arms_only);
    let mesh = if let Some(mesh) = library.meshes.get(&key) {
        mesh.clone()
    } else {
        let mesh = meshes.add(data.build_mesh(arms_only)?);
        library.meshes.insert(key, mesh.clone());
        mesh
    };
    let binds = if let Some(binds) = library.bindposes.get(path) {
        binds.clone()
    } else {
        let binds = bindposes.add(data.skin.iter().map(SkinBind::matrix).collect::<Vec<_>>());
        library.bindposes.insert(path.into(), binds.clone());
        binds
    };
    let root = commands
        .spawn((Name::new("Rig"), transform, Visibility::default()))
        .id();
    let mut entities = Vec::new();
    let mut names = HashMap::new();
    for bone in &data.bones {
        let parent = if bone.parent >= 0 {
            entities[bone.parent as usize]
        } else {
            root
        };
        let entity = commands
            .spawn((
                Name::new(bone.name.clone()),
                bone.transform(),
                Visibility::Inherited,
                ChildOf(parent),
            ))
            .id();
        entities.push(entity);
        names.insert(bone.name.clone(), entity);
    }
    let joints = data.skin.iter().map(|b| entities[b.bone]).collect();
    let skin_entity = commands
        .spawn((
            Name::new("Skin"),
            Mesh3d(mesh),
            MeshMaterial3d(material),
            SkinnedMesh {
                inverse_bindposes: binds,
                joints,
            },
            DynamicSkinnedMeshBounds,
            ChildOf(root),
        ))
        .id();
    commands.entity(root).insert(RigSkin(skin_entity));
    let initial = if data.anims.contains_key(clip) {
        clip.to_owned()
    } else {
        String::new()
    };
    commands.entity(root).insert(RigPlayback {
        data,
        bones: names,
        clip: initial,
        time: 0.,
        speed: 1.,
        looping: true,
        paused: false,
        overlay: None,
        additive: None,
    });
    Ok(root)
}
fn interval<const N: usize>(keys: &[[f32; N]], time: f32) -> Option<(&[f32; N], &[f32; N], f32)> {
    let first = keys.first()?;
    if time <= first[0] {
        return Some((first, first, 0.));
    }
    let i = keys.partition_point(|k| k[0] <= time);
    if i == keys.len() {
        let last = keys.last()?;
        return Some((last, last, 0.));
    }
    let a = &keys[i - 1];
    let b = &keys[i];
    let t = if b[0] > a[0] {
        (time - a[0]) / (b[0] - a[0])
    } else {
        0.
    };
    Some((a, b, t))
}
pub(crate) fn sample_track(track: &Track, time: f32, mut transform: Transform) -> Transform {
    if let Some((a, b, t)) = track.pos.as_deref().and_then(|k| interval(k, time)) {
        transform.translation = Vec3::new(a[1], a[2], a[3]).lerp(Vec3::new(b[1], b[2], b[3]), t);
    }
    if let Some((a, b, t)) = track.rot.as_deref().and_then(|k| interval(k, time)) {
        let q1 = Quat::from_xyzw(a[1], a[2], a[3], a[4]).normalize();
        let q2 = Quat::from_xyzw(b[1], b[2], b[3], b[4]).normalize();
        transform.rotation = q1.slerp(q2, t);
    }
    if let Some((a, b, t)) = track.scale.as_deref().and_then(|k| interval(k, time)) {
        transform.scale = Vec3::new(a[1], a[2], a[3]).lerp(Vec3::new(b[1], b[2], b[3]), t);
    }
    transform
}
fn clip_time(time: f32, length: f32, looping: bool) -> f32 {
    if length <= 0. {
        0.
    } else if looping {
        time.rem_euclid(length)
    } else {
        time.clamp(0., length)
    }
}
pub(crate) fn apply_additive(track: &Track, length: f32, weight: f32, mut pose: Transform) -> Transform {
    let start = sample_track(track, 0., Transform::IDENTITY);
    let end = sample_track(track, length, Transform::IDENTITY);
    let weight = weight.clamp(0., 1.);
    // Left multiplication is essential for pistols' multi-axis aim. Right
    // multiplication conjugates the delta by the current weapon pose.
    pose.rotation =
        Quat::IDENTITY.slerp(end.rotation * start.rotation.inverse(), weight) * pose.rotation;
    pose.translation += (end.translation - start.translation) * weight;
    pose
}
fn animate_rigs(
    time: Res<Time>,
    mut players: Query<&mut RigPlayback, Without<RigFixedAnimation>>,
    mut transforms: Query<&mut Transform>,
) {
    for mut player in &mut players {
        advance_rig(&mut player, &mut transforms, time.delta_secs());
    }
}
fn animate_fixed_rigs(
    time: Res<Time<Fixed>>,
    mut players: Query<&mut RigPlayback, With<RigFixedAnimation>>,
    mut transforms: Query<&mut Transform>,
) {
    for mut player in &mut players {
        advance_rig(&mut player, &mut transforms, time.delta_secs());
    }
}
fn advance_rig(player: &mut RigPlayback, transforms: &mut Query<&mut Transform>, delta: f32) {
    if player.paused {
        return;
    }
    player.time += delta * player.speed;
    let dt = delta * player.speed;
    if let Some(overlay) = &mut player.overlay {
        overlay.time += dt;
    }
    let clip = player.data.anims.get(&player.clip);
    let t = clip.map_or(0., |c| clip_time(player.time, c.length, player.looping));
    for bone in &player.data.bones {
        let Some(entity) = player.bones.get(&bone.name) else {
            continue;
        };
        let Ok(mut transform) = transforms.get_mut(*entity) else {
            continue;
        };
        let mut pose = bone.transform();
        if let Some(track) = clip.and_then(|c| c.tracks.get(&bone.name)) {
            pose = sample_track(track, t, pose);
        }
        if let Some(overlay) = &player.overlay {
            if overlay.mask.is_empty() || overlay.mask.contains(&bone.name) {
                if let Some(c) = player.data.anims.get(&overlay.clip) {
                    if let Some(track) = c.tracks.get(&bone.name) {
                        let other = sample_track(
                            track,
                            clip_time(overlay.time, c.length, overlay.looping),
                            pose,
                        );
                        let w = overlay.weight.clamp(0., 1.);
                        pose.translation = pose.translation.lerp(other.translation, w);
                        pose.rotation = pose.rotation.slerp(other.rotation, w);
                        pose.scale = pose.scale.lerp(other.scale, w);
                    }
                }
            }
        }
        if let Some(additive) = &player.additive {
            if let Some(clip) = player.data.anims.get(&additive.clip) {
                if let Some(track) = clip.tracks.get(&bone.name) {
                    pose = apply_additive(track, clip.length, additive.weight, pose);
                }
            }
        }
        *transform = pose;
    }
    if let Some(clip) = clip {
        for (path, track) in &clip.tracks {
            if !path.starts_with("Item_Root") {
                continue;
            }
            let Some(entity) = player.bones.get(path) else {
                continue;
            };
            if let Ok(mut transform) = transforms.get_mut(*entity) {
                *transform = sample_track(track, t, *transform);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn additive_aim_uses_parent_frame_and_preserves_the_live_base_pose() {
        let start = Quat::from_euler(EulerRot::XYZ, 0.5, -0.7, 0.3);
        let delta = Quat::from_euler(EulerRot::XYZ, -0.4, 0.6, 0.2);
        let end = delta * start;
        let key = |t, q: Quat| [t, q.x, q.y, q.z, q.w];
        let track = Track {
            pos: Some(vec![[0., 1., 2., 3.], [1., 1.1, 2.2, 3.3]]),
            rot: Some(vec![key(0., start), key(1., end)]),
            ..default()
        };
        let pose = Transform::from_rotation(start).with_translation(Vec3::new(7., 8., 9.));
        let full = apply_additive(&track, 1., 1., pose);
        assert!(full.rotation.angle_between(end) < 0.001);
        assert!((full.translation - Vec3::new(7.1, 8.2, 9.3)).length() < 0.0001);
        let zero = apply_additive(&track, 1., 0., pose);
        assert!(zero.rotation.angle_between(start) < 0.001);
        assert_eq!(zero.translation, pose.translation);
        // Reload/locomotion may have changed the base pose since the aim clip.
        let other = Quat::from_rotation_y(1.2);
        let mixed = apply_additive(&track, 1., 0.5, Transform::from_rotation(other));
        assert!(
            mixed
                .rotation
                .angle_between(Quat::IDENTITY.slerp(delta, 0.5) * other)
                < 0.001
        );
    }
    #[test]
    fn keyed_animation_interpolates_and_clamps() {
        let track = Track {
            pos: Some(vec![[0., 0., 0., 0.], [1., 2., 4., 6.]]),
            ..default()
        };
        assert_eq!(
            sample_track(&track, 0.5, Transform::IDENTITY).translation,
            Vec3::new(1., 2., 3.)
        );
        assert_eq!(
            sample_track(&track, 2., Transform::IDENTITY).translation,
            Vec3::new(2., 4., 6.)
        );
        assert!((clip_time(-0.25, 1., true) - 0.75).abs() < 1e-6);
    }
    #[test]
    fn original_rigs_have_valid_skin_data_and_clips() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        for file in [
            "rig.json",
            "cow_rig.json",
            "deer_rig.json",
            "horse_rig.json",
            "pig_rig.json",
        ] {
            let rig = RigData::load(&root.join(file)).unwrap();
            assert!(!rig.anims.is_empty());
            assert!(rig.build_mesh(false).is_ok());
            if file == "rig.json" {
                assert!(rig.anims.len() > 700);
                assert_eq!(rig.bones.len(), 17);
                assert!(rig.build_mesh(true).is_ok());
            }
        }
    }
    #[test]
    fn skinning_matches_independently_audited_animal_rest_bounds() {
        // The extracted animals' mesh bind coordinates are not their posed local
        // coordinates: Skeleton rotates +90 degrees around X. An identity-matrix
        // assertion would "fix" the data into a sideways animal. These bounds
        // come from notes/ANIMAL_MEASUREMENTS.md, independently reconstructed by
        // tools/measure_animals.py and checked against the original renders.
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        for (file, expected_min, expected_max) in [
            (
                "cow_rig.json",
                [-1.445239, -0.058751, -0.453737],
                [0.72, 1.645, 0.453737],
            ),
            (
                "deer_rig.json",
                [-1.668063, -0.058751, -0.731879],
                [0.72, 2.203179, 0.731880],
            ),
            (
                "pig_rig.json",
                [-0.860833, -0.035250, -0.181495],
                [0.432, 0.942253, 0.181495],
            ),
        ] {
            let rig = RigData::load(&root.join(file)).unwrap();
            let mut globals = Vec::new();
            for bone in &rig.bones {
                let local = bone.transform().to_matrix();
                globals.push(if bone.parent < 0 {
                    local
                } else {
                    globals[bone.parent as usize] * local
                });
            }
            let matrices: Vec<_> = rig
                .skin
                .iter()
                .map(|b| globals[b.bone] * b.matrix())
                .collect();
            let mut min = Vec3::splat(f32::INFINITY);
            let mut max = Vec3::splat(f32::NEG_INFINITY);
            for (i, vertex) in rig.mesh.positions.iter().enumerate() {
                let total: f32 = rig.mesh.skin_weight[i].iter().sum();
                let p: Vec3 = rig.mesh.skin_index[i]
                    .iter()
                    .zip(&rig.mesh.skin_weight[i])
                    .map(|(&joint, &weight)| {
                        matrices[joint as usize].transform_point3(Vec3::from_array(*vertex))
                            * (weight / total)
                    })
                    .sum();
                min = min.min(p);
                max = max.max(p);
            }
            assert!(
                (min - Vec3::from_array(expected_min)).abs().max_element() < 0.0001,
                "{file} min {min}"
            );
            assert!(
                (max - Vec3::from_array(expected_max)).abs().max_element() < 0.0001,
                "{file} max {max}"
            );
        }
    }
}
