//! Godot4.6 AlphaDepthPrePass without conflating the main0.99 and shadow0.1
//! thresholds. The visible Blend surface owns two colocated depth companions.
use bevy::{
    camera::{primitives::Aabb, visibility::RenderLayers},
    light::NotShadowCaster,
    mesh::{MeshVertexBufferLayoutRef, skinning::SkinnedMesh},
    pbr::{ExtendedMaterial, MaterialExtension, MaterialExtensionKey, MaterialExtensionPipeline},
    prelude::*,
    render::render_resource::{
        AsBindGroup, ColorWrites, RenderPipelineDescriptor, SpecializedMeshPipelineError,
    },
    shader::ShaderDefVal,
};
use std::collections::HashMap;

#[derive(Component)]
pub(crate) struct AlphaDepth;
#[derive(SystemSet, Debug, Clone, PartialEq, Eq, Hash)]
pub(crate) struct AlphaDepthUpdate;
pub(crate) struct AlphaDepthPlugin;
impl Plugin for AlphaDepthPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Materials>()
            .add_plugins((
                MaterialPlugin::<DepthMaterial>::default(),
                MaterialPlugin::<ShadowMaterial>::default(),
            ))
            .add_systems(Update, sync.in_set(AlphaDepthUpdate));
    }
}
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone, Default)]
pub(crate) struct DepthOnly {}
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone, Default)]
pub(crate) struct ShadowOnly {}
pub(crate) type DepthMaterial = ExtendedMaterial<StandardMaterial, DepthOnly>;
pub(crate) type ShadowMaterial = ExtendedMaterial<StandardMaterial, ShadowOnly>;

/// The same DEPTH_PREPASS key also appears on a normal camera's color pass.
/// The shader definition identifies the actual pipeline, including point-light
/// shadows, without assuming an orthographic projection or a color attachment.
fn prepass(descriptor: &RenderPipelineDescriptor) -> bool {
    descriptor
        .vertex
        .shader_defs
        .iter()
        .any(|define| match define {
            ShaderDefVal::Bool(name, enabled) => name == "PREPASS_PIPELINE" && *enabled,
            _ => false,
        })
}
fn only_depth(descriptor: &mut RenderPipelineDescriptor, shadow_only: bool) {
    if prepass(descriptor) {
        return;
    }
    if let Some(fragment) = &mut descriptor.fragment {
        for target in fragment.targets.iter_mut().flatten() {
            target.write_mask = ColorWrites::empty();
        }
    }
    if shadow_only {
        if let Some(depth) = &mut descriptor.depth_stencil {
            depth.depth_write_enabled = Some(false);
        }
    }
}
impl MaterialExtension for DepthOnly {
    fn enable_shadows() -> bool {
        false
    }
    fn specialize(
        _: &MaterialExtensionPipeline,
        descriptor: &mut RenderPipelineDescriptor,
        _: &MeshVertexBufferLayoutRef,
        _: MaterialExtensionKey<Self>,
    ) -> Result<(), SpecializedMeshPipelineError> {
        only_depth(descriptor, false);
        Ok(())
    }
}
impl MaterialExtension for ShadowOnly {
    fn enable_prepass() -> bool {
        false
    }
    fn specialize(
        _: &MaterialExtensionPipeline,
        descriptor: &mut RenderPipelineDescriptor,
        _: &MeshVertexBufferLayoutRef,
        _: MaterialExtensionKey<Self>,
    ) -> Result<(), SpecializedMeshPipelineError> {
        only_depth(descriptor, true);
        Ok(())
    }
}
#[derive(Clone)]
struct Pair {
    depth: Handle<DepthMaterial>,
    shadow: Handle<ShadowMaterial>,
}
#[derive(Resource, Default)]
struct Materials(HashMap<AssetId<StandardMaterial>, Pair>);
#[derive(Component)]
struct Companions {
    depth: Entity,
    shadow: Entity,
    mesh: AssetId<Mesh>,
    material: AssetId<StandardMaterial>,
    layers: RenderLayers,
    skin: Option<SkinnedMesh>,
}
fn same_skin(left: Option<&SkinnedMesh>, right: Option<&SkinnedMesh>) -> bool {
    match (left, right) {
        (None, None) => true,
        (Some(a), Some(b)) => a.inverse_bindposes == b.inverse_bindposes && a.joints == b.joints,
        _ => false,
    }
}
fn base(source: &StandardMaterial, threshold: f32) -> StandardMaterial {
    StandardMaterial {
        alpha_mode: AlphaMode::Mask(threshold),
        ..source.clone()
    }
}
#[allow(clippy::type_complexity)]
fn sync(
    mut commands: Commands,
    source: Res<Assets<StandardMaterial>>,
    mut depth: ResMut<Assets<DepthMaterial>>,
    mut shadow: ResMut<Assets<ShadowMaterial>>,
    mut cache: ResMut<Materials>,
    mut changed: MessageReader<AssetEvent<StandardMaterial>>,
    parents: Query<
        (
            Entity,
            &Mesh3d,
            &MeshMaterial3d<StandardMaterial>,
            Option<&Companions>,
            Option<&RenderLayers>,
            Option<&SkinnedMesh>,
        ),
        With<AlphaDepth>,
    >,
    retired: Query<
        (Entity, &Companions),
        Or<(
            Without<AlphaDepth>,
            Without<Mesh3d>,
            Without<MeshMaterial3d<StandardMaterial>>,
        )>,
    >,
) {
    for event in changed.read() {
        if let AssetEvent::Modified { id } | AssetEvent::Removed { id } = event {
            cache.0.remove(id);
        }
    }
    for (entity, companions) in &retired {
        commands.entity(companions.depth).try_despawn();
        commands.entity(companions.shadow).try_despawn();
        commands
            .entity(entity)
            .remove::<(Companions, NotShadowCaster)>();
    }
    for (entity, mesh, material, companions, layers, skin) in &parents {
        let layers = layers.cloned().unwrap_or_default();
        if companions.is_some_and(|c| {
            c.mesh == mesh.0.id()
                && c.material == material.0.id()
                && c.layers == layers
                && same_skin(c.skin.as_ref(), skin)
        }) && cache.0.contains_key(&material.0.id())
        {
            continue;
        }
        let Some(source) = source.get(&material.0) else {
            continue;
        };
        let pair = cache.0.entry(material.0.id()).or_insert_with(|| Pair {
            depth: depth.add(DepthMaterial {
                base: base(source, 0.99),
                extension: DepthOnly::default(),
            }),
            shadow: shadow.add(ShadowMaterial {
                base: base(source, 0.1),
                extension: ShadowOnly::default(),
            }),
        });
        let (depth_entity, shadow_entity) = companions.map_or_else(
            || {
                let depth = commands
                    .spawn((
                        Name::new("Alpha-depth opaque pixels"),
                        ChildOf(entity),
                        Transform::IDENTITY,
                        Visibility::Inherited,
                    ))
                    .id();
                let shadow = commands
                    .spawn((
                        Name::new("Alpha-depth shadow pixels"),
                        ChildOf(entity),
                        Transform::IDENTITY,
                        Visibility::Inherited,
                    ))
                    .id();
                (depth, shadow)
            },
            |c| (c.depth, c.shadow),
        );
        commands
            .entity(depth_entity)
            .insert((mesh.clone(), MeshMaterial3d(pair.depth.clone())));
        commands
            .entity(shadow_entity)
            .insert((mesh.clone(), MeshMaterial3d(pair.shadow.clone())));
        for companion in [depth_entity, shadow_entity] {
            let mut child = commands.entity(companion);
            child.insert(layers.clone()).remove::<Aabb>();
            if let Some(skin) = skin {
                child.insert(skin.clone());
            } else {
                child.remove::<SkinnedMesh>();
            }
        }
        commands.entity(entity).insert((
            NotShadowCaster,
            Companions {
                depth: depth_entity,
                shadow: shadow_entity,
                mesh: mesh.0.id(),
                material: material.0.id(),
                layers,
                skin: skin.cloned(),
            },
        ));
    }
}

#[cfg(test)]
mod tests;
