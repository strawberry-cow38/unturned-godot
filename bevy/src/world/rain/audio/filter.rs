//! Godot AudioFilterSW low-pass / AudioEffectFilterInstance stage routing.
//! Adapted from Godot 4.6 (MIT; see bevy/licenses/GODOT_AUDIO_FILTER.txt).
use bevy::{
    audio::{ChannelCount, Decodable, SampleRate, Source},
    prelude::*,
};
use std::{
    sync::{
        Arc,
        atomic::{AtomicU32, Ordering},
    },
    time::Duration,
};

#[derive(Clone)]
pub struct Pcm {
    pub samples: Arc<[f32]>,
    pub channels: ChannelCount,
    pub rate: SampleRate,
}
impl Pcm {
    pub fn decode(source: &AudioSource) -> Self {
        let decoder = source.decoder();
        Self {
            channels: decoder.channels(),
            rate: decoder.sample_rate(),
            samples: decoder.collect::<Vec<_>>().into(),
        }
    }
}
/// The source owns a looping PCM view, so a random initial offset wraps back to
/// the full file. `skip_duration(...).repeat_infinite()` would repeat only its tail.
#[derive(Asset, TypePath, Clone)]
pub struct FilteredClip {
    pub pcm: Pcm,
    pub cutoff: Arc<AtomicU32>,
    pub start_frame: usize,
}
impl Decodable for FilteredClip {
    type Decoder = FilteredDecoder;
    fn decoder(&self) -> Self::Decoder {
        let channels = usize::from(self.pcm.channels.get());
        FilteredDecoder {
            pcm: self.pcm.clone(),
            cutoff: self.cutoff.clone(),
            position: (self.start_frame * channels) % self.pcm.samples.len().max(1),
            coefficients: Coefficients::new(
                f32::from_bits(self.cutoff.load(Ordering::Relaxed)),
                self.pcm.rate.get(),
            ),
            last_cutoff: self.cutoff.load(Ordering::Relaxed),
            history: vec![[History::default(); 4]; channels],
        }
    }
}
#[derive(Clone, Copy)]
struct Coefficients {
    b0: f64,
    b1: f64,
    b2: f64,
    a1: f64,
    a2: f64,
}
impl Coefficients {
    fn new(cutoff: f32, rate: u32) -> Self {
        // Godot db=3 selects FOUR biquad processors. Retain its actual routing,
        // rather than infer a stage count from the inspector's "24 dB" label.
        let omega = std::f64::consts::TAU * f64::from(cutoff.clamp(1., (rate / 2 + 512) as f32))
            / f64::from(rate);
        let (sin, cos) = omega.sin_cos();
        let alpha = sin / (2. * 0.25); // RainAudio's original resonance.
        let a0 = 1. + alpha;
        Self {
            b0: (1. - cos) / 2. / a0,
            b1: (1. - cos) / a0,
            b2: (1. - cos) / 2. / a0,
            a1: 2. * cos / a0,
            a2: -(1. - alpha) / a0,
        }
    }
}
#[derive(Clone, Copy, Default)]
struct History {
    x1: f32,
    x2: f32,
    y1: f32,
    y2: f32,
}
impl History {
    fn step(&mut self, input: f32, c: Coefficients) -> f32 {
        let output = (f64::from(input) * c.b0
            + f64::from(self.x1) * c.b1
            + f64::from(self.x2) * c.b2
            + f64::from(self.y1) * c.a1
            + f64::from(self.y2) * c.a2) as f32;
        self.x2 = self.x1;
        self.x1 = input;
        self.y2 = self.y1;
        self.y1 = output;
        output
    }
}
pub struct FilteredDecoder {
    pcm: Pcm,
    cutoff: Arc<AtomicU32>,
    position: usize,
    coefficients: Coefficients,
    last_cutoff: u32,
    history: Vec<[History; 4]>,
}
impl Iterator for FilteredDecoder {
    type Item = f32;
    fn next(&mut self) -> Option<f32> {
        let input = *self.pcm.samples.get(self.position)?;
        let channel = self.position % self.history.len();
        if channel == 0 {
            let cutoff = self.cutoff.load(Ordering::Relaxed);
            if cutoff != self.last_cutoff {
                self.last_cutoff = cutoff;
                self.coefficients = Coefficients::new(f32::from_bits(cutoff), self.pcm.rate.get());
            }
        }
        self.position = (self.position + 1) % self.pcm.samples.len();
        let mut output = input;
        for stage in &mut self.history[channel] {
            output = stage.step(output, self.coefficients);
        }
        Some(output)
    }
}
impl Source for FilteredDecoder {
    fn current_span_len(&self) -> Option<usize> {
        None
    }
    fn channels(&self) -> ChannelCount {
        self.pcm.channels
    }
    fn sample_rate(&self) -> SampleRate {
        self.pcm.rate
    }
    fn total_duration(&self) -> Option<Duration> {
        None
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn tone(frequency: f32, cutoff: f32) -> f32 {
        let samples: Vec<_> = (0..44100)
            .map(|i| (std::f32::consts::TAU * frequency * i as f32 / 44100.).sin())
            .collect();
        let clip = FilteredClip {
            pcm: Pcm {
                samples: samples.into(),
                channels: ChannelCount::new(1).unwrap(),
                rate: SampleRate::new(44100).unwrap(),
            },
            cutoff: Arc::new(AtomicU32::new(cutoff.to_bits())),
            start_frame: 0,
        };
        let mut d = clip.decoder();
        for _ in 0..4096 {
            d.next();
        }
        (d.take(22050).map(|v| v * v).sum::<f32>() / 22050.).sqrt()
    }
    #[test]
    fn actual_four_stage_filter_preserves_low_rain_and_muffles_high_hiss() {
        let low = tone(200., 6000.);
        let high = tone(8000., 6000.);
        let outside = tone(8000., 20500.);
        assert!(low > 0.6, "low frequencies remain audible: {low}");
        assert!(outside > 0.45, "open filter preserves the hiss: {outside}");
        assert!(
            high < outside * 0.1,
            "the source four-stage filter muffles the top: {high}/{outside}"
        );
    }
    #[test]
    fn random_offset_wraps_full_pcm_and_channels_have_independent_filter_history() {
        let clip = FilteredClip {
            pcm: Pcm {
                samples: Arc::from([1., 0., 0., 0., 0., 0., 0., 0.]),
                channels: ChannelCount::new(2).unwrap(),
                rate: SampleRate::new(44100).unwrap(),
            },
            cutoff: Arc::new(AtomicU32::new(20500_f32.to_bits())),
            start_frame: 2,
        };
        let mut d = clip.decoder();
        let samples: Vec<_> = d.by_ref().take(32).collect();
        assert!(
            samples.iter().step_by(2).skip(2).any(|s| s.abs() > 0.001),
            "the initial skipped impulse returns on wrap"
        );
        assert!(
            samples.iter().skip(1).step_by(2).all(|s| *s == 0.),
            "left filter state must not leak into right"
        );
        clip.cutoff.store(6000_f32.to_bits(), Ordering::Relaxed);
        assert!(d.take(1000).all(f32::is_finite));
    }
}
