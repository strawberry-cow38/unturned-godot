//! HUD.cs pain ColorRect followed by the original LowHealthOverlay screen copy.
//! Only the owning viewer's accepted health drives the smoothed fraction.
mod render;
#[cfg(test)]
mod tests;
use super::{
    UiModel, UiState,
    hurt::{HurtFeedback, HurtUpdate},
};
use crate::player::WorldSessionEpoch;
use bevy::{
    asset::embedded_asset,
    prelude::*,
    render::{
        RenderApp,
        extract_component::{ExtractComponent, ExtractComponentPlugin, UniformComponentPlugin},
        render_resource::ShaderType,
    },
};

#[derive(Resource, Default)]
struct State {
    strength: f32,
    epoch: Option<u64>,
}
impl State {
    fn step(&mut self, fraction: f32, dt: f32) {
        let fraction = fraction.clamp(0., 1.);
        let target = ((0.5 - fraction) / (0.5 - 0.15)).clamp(0., 1.);
        self.strength += (target - self.strength) * (1. - (-6. * dt).exp());
    }
}
#[derive(Component, Clone, Copy, ExtractComponent, ShaderType)]
#[extract_app(RenderApp)]
struct Feedback {
    /// Source low-health strength, pain alpha, reserved.
    parameters: Vec4,
}
pub(super) struct LowHealthPlugin;
impl Plugin for LowHealthPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "low_health/overlay.wesl");
        app.init_resource::<State>()
            .add_plugins((
                ExtractComponentPlugin::<Feedback>::default(),
                UniformComponentPlugin::<Feedback>::default(),
            ))
            .add_systems(Update, update.after(HurtUpdate).after(super::UiUpdate));
        render::install(app);
    }
}
fn update(
    mut commands: Commands,
    mut state: ResMut<State>,
    model: Res<UiModel>,
    ui: Res<UiState>,
    epoch: Res<WorldSessionEpoch>,
    hurt: Res<HurtFeedback>,
    time: Res<Time>,
    cameras: Query<Entity, With<IsDefaultUiCamera>>,
) {
    if !model.connected || state.epoch != Some(epoch.0) {
        state.strength = 0.;
    }
    state.epoch = Some(epoch.0);
    if model.connected && !crate::player::simulation_paused(&ui) {
        // Source AdoptReplicatedVitals fixes MaxHealth at100 (PlayerController6064).
        // Death does not hide/reset this pass; recovery is the same6/s smoothing.
        state.step(model.health / 100., time.delta_secs());
    }
    let parameters = if model.connected && !ui.menu_camera() {
        Vec4::new(state.strength, hurt.pain_alpha, 0., 0.)
    } else {
        Vec4::ZERO
    };
    for camera in &cameras {
        commands.entity(camera).insert(Feedback { parameters });
    }
}

#[cfg(test)]
mod visual;
