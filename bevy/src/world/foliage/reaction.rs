//! Per-instance wind_sway inputs. Shared species materials never receive hits.
use super::*;

#[derive(Component, Clone, Copy)]
pub(super) struct Reaction {
    pub shake: f32,
    pub sway: f32,
    pub gust: Vec3,
    pub base: Vec3,
}
impl Default for Reaction {
    fn default() -> Self {
        Self {
            shake: 0.,
            sway: 0.05,
            gust: Vec3::ZERO,
            base: Vec3::ZERO,
        }
    }
}
#[derive(Component)]
pub(super) struct Instance(Handle<FoliageMaterial>);

pub(super) fn apply(
    mut commands: Commands,
    mut assets: ResMut<Assets<FoliageMaterial>>,
    mut query: Query<(
        Entity,
        &mut MeshMaterial3d<FoliageMaterial>,
        Option<&Reaction>,
        Option<&Instance>,
    )>,
) {
    for (entity, mut handle, reaction, instance) in &mut query {
        let Some(reaction) = reaction else {
            if let Some(instance) = instance {
                handle.0 = instance.0.clone();
                commands.entity(entity).remove::<Instance>();
            }
            continue;
        };
        if instance.is_none() {
            let Some(material) = assets.get(&handle.0).cloned() else {
                continue;
            };
            commands.entity(entity).insert(Instance(handle.0.clone()));
            handle.0 = assets.add(material);
        }
        if let Some(mut material) = assets.get_mut(&handle.0) {
            let motion = &mut material.extension.motion;
            motion.settings.y = reaction.sway;
            motion.settings.z = reaction.shake;
            motion.gust = reaction.gust.extend(0.);
            motion.base = reaction.base.extend(0.);
        }
    }
}
