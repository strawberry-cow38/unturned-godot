//! Original NightVision.cs tube modes; its screen pass follows world + hands,
//! while HUD/console remain above it. Input and goggles ownership live in lights.
mod render;
use bevy::{
    asset::embedded_asset,
    prelude::*,
    render::{
        RenderApp,
        extract_component::{ExtractComponent, ExtractComponentPlugin, UniformComponentPlugin},
        render_resource::ShaderType,
    },
};
#[derive(Resource, Default, Clone, Copy, Debug)]
pub struct NightVision {
    pub active: bool,
    pub military: bool,
}
impl NightVision {
    pub fn fog_scale(self) -> f32 {
        if !self.active {
            1.
        } else if self.military {
            0.2
        } else {
            0.4
        }
    }
    pub fn overlay_dim(self) -> f32 {
        if !self.active {
            1.
        } else if self.military {
            0.32
        } else {
            0.42
        }
    }
    pub fn overlay_tint(self) -> Color {
        if !self.active {
            Color::WHITE
        } else if self.military {
            Color::srgb(0.34, 0.95, 0.40)
        } else {
            Color::srgb(0.88, 0.90, 0.88)
        }
    }
}
#[derive(Component, Clone, Copy, ExtractComponent, ShaderType)]
#[extract_app(RenderApp)]
struct TubeUniform {
    tint_gain: Vec4,
    // grain, vignette, hot, active
    optics: Vec4,
    time: Vec4,
}
impl TubeUniform {
    fn new(nv: NightVision, elapsed: f32) -> Self {
        let (tint, gain, grain, vignette, hot) = if nv.military {
            (Vec3::new(0.22, 1., 0.32), 2.6, 0.11, 0.35, 0.6)
        } else {
            (Vec3::new(0.88, 0.90, 0.88), 1.9, 0.19, 0.45, 0.4)
        };
        Self {
            tint_gain: tint.extend(gain),
            optics: Vec4::new(grain, vignette, hot, u8::from(nv.active) as f32),
            time: Vec4::new(elapsed, 0., 0., 0.),
        }
    }
}
#[derive(Resource, Default)]
struct Diagnostic(Option<bool>);
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct NightVisionUpdate;
pub struct NightVisionPlugin;
impl Plugin for NightVisionPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "nightvision.wesl");
        app.add_plugins((
            ExtractComponentPlugin::<TubeUniform>::default(),
            UniformComponentPlugin::<TubeUniform>::default(),
        ))
        .init_resource::<NightVision>()
        .insert_resource(Diagnostic(
            std::env::var("UG_NIGHTVISION")
                .ok()
                .filter(|s| !s.is_empty())
                .map(|s| s == "military"),
        ))
        .add_systems(
            Update,
            update
                .after(crate::viewmodel::lights::LightInput)
                .in_set(NightVisionUpdate),
        );
        render::install(app);
    }
}
fn update(
    mut commands: Commands,
    mut nv: ResMut<NightVision>,
    switches: Res<crate::viewmodel::lights::Switches>,
    debug: Res<Diagnostic>,
    time: Res<Time<Real>>,
    cameras: Query<Entity, With<IsDefaultUiCamera>>,
) {
    *nv = debug.0.map_or(
        NightVision {
            active: switches.nightvision,
            military: switches.military,
        },
        |military| NightVision {
            active: true,
            military,
        },
    );
    let settings = TubeUniform::new(*nv, time.elapsed_secs() % 3600.);
    for entity in &cameras {
        commands.entity(entity).insert(settings);
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn accepted_switch_mode_only_targets_world_and_hands_camera_and_recovers_fog() {
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .init_resource::<NightVision>()
            .init_resource::<Diagnostic>()
            .init_resource::<crate::viewmodel::lights::Switches>()
            .add_systems(Update, update);
        let world_composite = app.world_mut().spawn(IsDefaultUiCamera).id();
        let console = app
            .world_mut()
            .spawn(crate::world::rain::lightning::ConsoleCamera)
            .id();
        {
            let mut s = app
                .world_mut()
                .resource_mut::<crate::viewmodel::lights::Switches>();
            s.nightvision = true;
            s.military = true;
        }
        app.update();
        let nv = *app.world().resource::<NightVision>();
        assert_eq!(nv.fog_scale(), 0.2);
        assert_eq!(nv.overlay_dim(), 0.32);
        let tube = app.world().get::<TubeUniform>(world_composite).unwrap();
        assert_eq!(tube.tint_gain, Vec4::new(0.22, 1., 0.32, 2.6));
        assert_eq!(tube.optics, Vec4::new(0.11, 0.35, 0.6, 1.));
        assert!(app.world().get::<TubeUniform>(console).is_none());
        app.world_mut()
            .resource_mut::<crate::viewmodel::lights::Switches>()
            .military = false;
        app.update();
        let nv = *app.world().resource::<NightVision>();
        assert_eq!(nv.fog_scale(), 0.4);
        assert_eq!(nv.overlay_dim(), 0.42);
        assert_eq!(
            app.world()
                .get::<TubeUniform>(world_composite)
                .unwrap()
                .optics,
            Vec4::new(0.19, 0.45, 0.4, 1.)
        );
        app.world_mut()
            .resource_mut::<crate::viewmodel::lights::Switches>()
            .nightvision = false;
        app.update();
        assert_eq!(app.world().resource::<NightVision>().fog_scale(), 1.);
        assert_eq!(
            app.world()
                .get::<TubeUniform>(world_composite)
                .unwrap()
                .optics
                .w,
            0.
        );
    }
}
