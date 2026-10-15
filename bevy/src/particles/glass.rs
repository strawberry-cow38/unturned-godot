//! Shared original GlassPane.SpawnShards / PlayBreakSound (effect64).
//! Visual bursts only: accepted damage remains the caller's authority.
use super::{BurstTiming, ParticleAssets, Spec};
use crate::{player::WorldSessionEpoch, settings::Settings, world::WorldConfig};
use bevy::{ecs::system::SystemParam, prelude::*};
use serde::Deserialize;
use std::path::{Path, PathBuf};

#[derive(Clone, Deserialize)]
struct Rubble {
    count: usize,
    life: [f32; 2],
    speed: [f32; 2],
    size: [f32; 2],
    gravity: f32,
    tex: String,
}
#[derive(Default)]
struct Cache {
    root: Option<PathBuf>,
    rubble: Option<Rubble>,
    sound: Option<String>,
    material: Option<Handle<StandardMaterial>>,
}
impl Cache {
    fn load(&mut self, root: &Path) {
        if self.root.as_deref() == Some(root) {
            return;
        }
        self.root = Some(root.to_owned());
        let read = |path: &str| -> Option<serde_json::Value> {
            serde_json::from_slice(&std::fs::read(root.join(path)).ok()?).ok()
        };
        self.rubble = read("effects/rubble_fx.json")
            .and_then(|v| serde_json::from_value::<Rubble>(v["64"].clone()).ok())
            .filter(|r| root.join("effects/rubble").join(&r.tex).is_file());
        self.sound = read("effects/rubble_snd.json")
            .and_then(|v| {
                v["64"]["snd"]
                    .as_str()
                    .map(|s| format!("effects/rubble_snd/{s}"))
            })
            .filter(|s| root.join(s).is_file())
            .or_else(|| {
                root.join("impact_glass.wav")
                    .is_file()
                    .then(|| "impact_glass.wav".into())
            });
    }
}
#[derive(Component)]
pub(crate) struct GlassTransient(u64);

/// Source glass presentation dependencies, reusable by lamps and window panes.
#[derive(SystemParam)]
pub(crate) struct GlassShards<'w, 's> {
    meshes: ResMut<'w, Assets<Mesh>>,
    materials: ResMut<'w, Assets<StandardMaterial>>,
    particles: Res<'w, ParticleAssets>,
    assets: Res<'w, AssetServer>,
    config: Res<'w, WorldConfig>,
    settings: Res<'w, Settings>,
    epoch: Res<'w, WorldSessionEpoch>,
    cache: Local<'s, Cache>,
}
impl GlassShards<'_, '_> {
    pub(crate) fn material_color(
        &self,
        material: Option<&Handle<StandardMaterial>>,
    ) -> Option<Color> {
        material
            .and_then(|m| self.materials.get(m))
            .map(|m| m.base_color)
    }
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn spawn(
        &mut self,
        commands: &mut Commands,
        center: Vec3,
        outward: Vec3,
        half_extents: Vec3,
        tint: Color,
        count_scale: f32,
        size_scale: f32,
        seed: u64,
    ) -> Option<Entity> {
        self.cache.load(&self.config.content_root);
        let emitter = if let Some(rubble) = self.cache.rubble.clone() {
            let material = self
                .cache
                .material
                .get_or_insert_with(|| {
                    self.materials.add(StandardMaterial {
                        base_color: Color::WHITE,
                        alpha_mode: AlphaMode::Blend,
                        unlit: true,
                        cull_mode: None,
                        double_sided: true,
                        ..default()
                    })
                })
                .clone();
            let mut spec = specification(
                &rubble,
                outward,
                half_extents,
                tint,
                count_scale,
                size_scale,
            );
            spec.material = Some(material);
            let entity = super::spawn(
                commands,
                &mut self.meshes,
                &self.particles,
                spec,
                center,
                None,
                seed,
            );
            commands.entity(entity).insert((
                Name::new("Source glass shards"),
                GlassTransient(self.epoch.0),
            ));
            Some(entity)
        } else {
            None
        };
        // The sound is independent of the sprite gate, as in GlassPane.Shatter.
        if let Some(path) = &self.cache.sound {
            let voice = crate::vehicle::spawn_ranged_audio(
                commands,
                &self.assets,
                None,
                path,
                false,
                10_f32.powf(-2. / 20.),
                1.,
                6.,
                80.,
                self.settings.master_volume,
            );
            commands.entity(voice).insert((
                Transform::from_translation(center),
                GlassTransient(self.epoch.0),
            ));
        }
        emitter
    }
}
fn specification(
    r: &Rubble,
    direction: Vec3,
    extents: Vec3,
    tint: Color,
    count: f32,
    size: f32,
) -> Spec {
    // ParticleFx defaults: amount and physical size are both25%. Lens callers
    // use1.5 count and0.6 size; the glass tint stays uniform, including alpha.
    let amount = ((r.count as f32 * 2. * count)
        .round_ties_even()
        .clamp(8., 40.)
        * 0.25)
        .round_ties_even()
        .max(1.) as usize;
    let color = Vec4::from_array(tint.to_linear().to_f32_array());
    Spec {
        amount,
        life: 1.2_f32.max(r.life[1] * 1.2),
        one_shot: true,
        emission_box: Some(extents),
        burst_timing: Some(BurstTiming {
            explosiveness: 0.9,
            randomness: 0.5,
        }),
        cpu_spread: true,
        direction,
        spread: 85.,
        speed: Vec2::new(r.speed[0] * 0.5, r.speed[1] * 0.7),
        gravity: Vec3::new(0., -7. * r.gravity, 0.),
        size: Vec2::new(r.size[0] * 0.45, r.size[1] * 0.55) * (0.25 * size),
        angle: Vec2::new(-180., 180.),
        spin: Vec2::new(-400., 400.),
        colors: vec![(0., color), (1., color)],
        ..default()
    }
}
pub(super) fn cleanup(
    mut commands: Commands,
    epoch: Option<Res<WorldSessionEpoch>>,
    model: Option<Res<crate::ui::UiModel>>,
    nodes: Query<(Entity, &GlassTransient)>,
) {
    let Some(epoch) = epoch else {
        return;
    };
    for (entity, lifetime) in &nodes {
        if lifetime.0 != epoch.0 || model.as_ref().is_some_and(|m| !m.connected) {
            commands.entity(entity).despawn();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn actual_retail_glass_uses_source_count_box_size_and_uniform_half_alpha() {
        let mut cache = Cache::default();
        cache.load(&WorldConfig::default().content_root);
        let r = cache.rubble.as_ref().unwrap();
        assert_eq!(cache.sound.as_deref(), Some("effects/rubble_snd/64.wav"));
        let spec = specification(
            r,
            Vec3::NEG_Z,
            Vec3::new(0.12, 0.2, 0.06),
            Color::srgba(0.94, 0.89, 0.73, 0.5),
            1.5,
            0.6,
        );
        assert_eq!(spec.amount, 6);
        assert_eq!(spec.life, 1.2);
        assert_eq!(spec.speed, Vec2::new(2.5, 5.6));
        assert!((spec.size - Vec2::new(0.050625, 0.103125)).length() < 0.000001);
        assert_eq!(spec.gravity, Vec3::new(0., -7., 0.));
        assert_eq!(spec.colors[0].1.w, 0.5);
        assert_eq!(
            spec.colors[0].1, spec.colors[1].1,
            "source does not fade/shrink shards"
        );
        assert_eq!(spec.emission_box, Some(Vec3::new(0.12, 0.2, 0.06)));
    }
    #[test]
    fn burst_and_voice_retire_together_on_session_change_or_disconnect() {
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .insert_resource(WorldSessionEpoch(2))
            .insert_resource(crate::ui::UiModel::default())
            .add_systems(Update, cleanup);
        app.world_mut()
            .resource_mut::<crate::ui::UiModel>()
            .connected = true;
        let old = app.world_mut().spawn(GlassTransient(1)).id();
        let current = app.world_mut().spawn(GlassTransient(2)).id();
        app.update();
        assert!(app.world().get_entity(old).is_err());
        assert!(app.world().get_entity(current).is_ok());
        app.world_mut()
            .resource_mut::<crate::ui::UiModel>()
            .connected = false;
        app.update();
        assert!(app.world().get_entity(current).is_err());
    }
}
