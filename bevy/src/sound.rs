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
