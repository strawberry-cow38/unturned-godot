//! Original GeigerCounter: Poisson clicks whose mean rate follows the accepted
//! owner's radiation dose, silent below the floor, four overlapping voices with
//! a little level jitter per click.
use crate::{player::simulation_paused, settings::Settings, ui::{UiModel, UiState}};
use bevy::{audio::Volume, prelude::*};

/// GeigerCounter.SilentBelow / MinRate / MaxRate.
pub(crate) const SILENT_BELOW: f32 = 0.02;
pub(crate) const MIN_RATE: f32 = 1.2;
pub(crate) const MAX_RATE: f32 = 30.;
const CLIPS: [&str; 2] = [
    "audio/geiger/geiger_click_1.wav",
    "audio/geiger/geiger_click_2.wav",
];
const VOICES: usize = 4;

/// GeigerCounter.RateFor: squared so the dangerous end is the audible gradient.
pub(crate) fn rate_for(dose: f32) -> f32 {
    if dose < SILENT_BELOW {
        return 0.;
    }
    let t = ((dose - SILENT_BELOW) / (1. - SILENT_BELOW).max(0.001)).clamp(0., 1.);
    MIN_RATE + (MAX_RATE - MIN_RATE) * t * t
}

#[derive(Resource)]
pub(crate) struct Geiger {
    next_in: f64,
    voice: usize,
    voices: [Option<Entity>; VOICES],
    rng: u64,
    forced: Option<f32>,
    pub(crate) clicks: u64,
}
impl Default for Geiger {
    fn default() -> Self {
        Self {
            next_in: 1.,
            voice: 0,
            voices: [None; VOICES],
            rng: 0x9E37_79B9_7F4A_7C15,
            forced: None,
            clicks: 0,
        }
    }
}
/// One scheduled click: which clip and the jittered level in dB.
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct Click {
    pub clip: usize,
    pub db: f32,
}
impl Geiger {
    fn uniform(&mut self) -> f32 {
        // xorshift64*: deterministic and seedable for tests.
        self.rng ^= self.rng >> 12;
        self.rng ^= self.rng << 25;
        self.rng ^= self.rng >> 27;
        ((self.rng.wrapping_mul(0x2545_F491_4F6C_DD1D) >> 40) as f32) / (1u64 << 24) as f32
    }
    /// GeigerCounter._Process for one frame.
    pub(crate) fn step(&mut self, dose: f32, dt: f64) -> Option<Click> {
        let rate = rate_for(dose);
        if rate <= 0. {
            // Leaving does not bank a click.
            self.next_in = 1.;
            return None;
        }
        self.next_in -= dt;
        if self.next_in > 0. {
            return None;
        }
        // Exponential inter-arrival -ln(U)/rate, clamped away from zero.
        let u = self.uniform().max(1e-4);
        self.next_in = ((1. / MAX_RATE * 0.25).max(-u.ln() / rate)) as f64;
        let clip = (self.uniform() * CLIPS.len() as f32).min(1.) as usize;
        let db = -6. + (-2.5 + self.uniform() * 4.);
        Some(Click { clip, db })
    }
}

pub(crate) struct GeigerPlugin;
impl Plugin for GeigerPlugin {
    fn build(&self, app: &mut App) {
        app.insert_resource(Geiger {
            forced: std::env::var("UG_GEIGER").ok().and_then(|s| s.parse().ok()),
            ..default()
        })
        .add_systems(Update, play);
    }
}
fn play(
    mut commands: Commands,
    mut geiger: ResMut<Geiger>,
    model: Res<UiModel>,
    ui: Res<UiState>,
    settings: Res<Settings>,
    assets: Res<AssetServer>,
    time: Res<Time>,
) {
    // A Godot Node processes with the tree: singleplayer pause freezes it.
    if simulation_paused(&ui) {
        return;
    }
    let dose = geiger
        .forced
        .unwrap_or(if model.connected { model.radiation } else { 0. });
    let Some(click) = geiger.step(dose, time.delta_secs_f64()) else {
        return;
    };
    geiger.clicks += 1;
    let slot = geiger.voice;
    geiger.voice = (slot + 1) % VOICES;
    // Reusing a voice restarts it, like AudioStreamPlayer.Play on a busy player.
    if let Some(old) = geiger.voices[slot].take() {
        commands.entity(old).try_despawn();
    }
    let gain = 10_f32.powf(click.db / 20.) * settings.master_volume;
    geiger.voices[slot] = Some(
        commands
            .spawn((
                Name::new("Geiger click"),
                AudioPlayer::new(assets.load(CLIPS[click.clip])),
                PlaybackSettings::DESPAWN.with_volume(Volume::Linear(gain)),
            ))
            .id(),
    );
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_rate_curve_poisson_schedule_and_jittered_levels() {
        assert_eq!(rate_for(0.), 0.);
        assert_eq!(rate_for(0.0199), 0.);
        assert_eq!(rate_for(0.02), MIN_RATE);
        assert_eq!(rate_for(1.), MAX_RATE);
        assert_eq!(rate_for(4.), MAX_RATE);
        let half = (0.51 - 0.02) / 0.98_f32;
        assert!((rate_for(0.51) - (1.2 + 28.8 * half * half)).abs() < 1e-4);
        // Silent and reset while clear: entering waits the source's first second.
        let mut g = Geiger::default();
        g.next_in = 0.2;
        assert!(g.step(0., 10.).is_none());
        assert_eq!(g.next_in, 1.);
        assert!(g.step(0.5, 0.99).is_none());
        let click = g.step(0.5, 0.02).expect("first click after the initial second");
        assert!(click.clip < 2);
        assert!((-8.5..=-4.5).contains(&click.db));
        // Mean rate over a long run follows RateFor (Poisson, not a metronome).
        // Like _Process, a click lands on the first frame past its wait, so the
        // 30/s ceiling reads slightly slower at a 5 ms frame.
        let run = |dose: f32| {
            let mut g = Geiger::default();
            let (mut clicks, mut gaps, mut since) = (0, Vec::new(), 0.);
            for _ in 0..400_000 {
                since += 0.005;
                if g.step(dose, 0.005).is_some() {
                    clicks += 1;
                    gaps.push(since);
                    since = 0.;
                }
            }
            (clicks as f64 / (400_000. * 0.005), gaps)
        };
        let (low, _) = run(0.3);
        assert!((low / rate_for(0.3) as f64 - 1.).abs() < 0.05, "mean rate {low}");
        let (high, gaps) = run(1.);
        assert!((25.5..30.).contains(&high), "mean rate {high}");
        let distinct = gaps
            .iter()
            .map(|g| (g * 1000.) as i64)
            .collect::<std::collections::HashSet<_>>();
        assert!(distinct.len() > 10, "inter-arrival times vary");
    }
}
