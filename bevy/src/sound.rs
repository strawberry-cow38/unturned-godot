//! Original GameAudio variation banks. Exact clips should be loaded directly.
pub(crate) mod shelter;
use std::path::Path;
#[derive(Default)]
pub struct Bank {
    pub(crate) clips: Vec<String>,
    last: usize,
}
impl Bank {
    pub fn load(root: &Path, folder: &str, prefix: &str) -> Self {
        let prefix = format!("{prefix}_");
        let mut clips = std::fs::read_dir(root.join("audio").join(folder))
            .into_iter()
            .flatten()
            .filter_map(Result::ok)
            .filter_map(|entry| {
                let name = entry.file_name().to_string_lossy().into_owned();
                (name.starts_with(&prefix) && (name.ends_with(".wav") || name.ends_with(".ogg")))
                    .then(|| format!("audio/{folder}/{name}"))
            })
            .collect::<Vec<_>>();
        clips.sort_unstable();
        Self { clips, last: 0 }
    }
    pub fn pick(&mut self, seed: u64) -> Option<&str> {
        if self.clips.is_empty() {
            return None;
        }
        if self.clips.len() > 1 {
            let mut i = seed as usize % (self.clips.len() - 1);
            if i >= self.last {
                i += 1;
            }
            self.last = i;
        }
        Some(&self.clips[self.last])
    }
}

// Godot 4.6 AudioStreamPlayer3D::_get_attenuation_db/_update_panning:
// inverse distance, default +3 dB cap, then linear fade to MaxDistance.
// https://github.com/godotengine/godot/blob/4.6-stable/scene/3d/audio_stream_player_3d.cpp
pub(crate) fn distance_gain(db: f32, unit: f32, range: f32, distance: f32) -> f32 {
    (10_f32.powf(db / 20.) / (distance / unit + 0.00001)).min(10_f32.powf(3. / 20.))
        * (1. - distance / range).max(0.)
}
