//! Original PlayerProfile preferences. Choosing a face changes the local
//! preference; rendered characters still read accepted profile replicas.
use crate::{bridge::CoreBridge, cli::Options, settings::Settings};
use bevy::prelude::*;
use serde::Deserialize;
use serde_json::json;
use std::path::{Path, PathBuf};

#[derive(Resource, Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub(crate) struct LocalProfile {
    #[serde(skip)]
    pub ready: bool,
    pub name: String,
    pub face: u8,
    pub face_count: u8,
    pub free_face_count: u8,
    pub has_avatar: bool,
    pub avatar_notice: Option<String>,
    pub save_notice: Option<String>,
}

impl LocalProfile {
    pub fn load(bridge: &CoreBridge, options: &mut Options) -> Self {
        let path = preference_path();
        match bridge
            .request(
                "localProfile",
                json!({
                    "path":path, "legacyPath":legacy_path(), "name":options.player_name,
                }),
            )
            .and_then(|reply| Ok(serde_json::from_value::<Self>(reply["result"].clone())?))
        {
            Ok(mut profile) => {
                profile.ready = true;
                options.player_name = profile.name.clone();
                profile
            }
            Err(error) => Self {
                save_notice: Some(format!("Appearance unavailable: {error}")),
                ..default()
            },
        }
    }

    pub fn select(&mut self, bridge: &CoreBridge, face: u8) {
        if !self.ready || face >= self.face_count {
            return;
        }
        match bridge
            .request("setLocalFace", json!({"face":face}))
            .and_then(|reply| {
                Ok(serde_json::from_value::<Self>(
                    reply["result"]["profile"].clone(),
                )?)
            }) {
            Ok(mut profile) => {
                profile.ready = true;
                *self = profile;
            }
            Err(error) => self.save_notice = Some(error.to_string()),
        }
    }
}

fn beside_settings(settings: &Path) -> PathBuf {
    settings
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join("profile.cfg")
}
fn preference_path() -> PathBuf {
    std::env::var_os("UG_BEVY_PROFILE")
        .map(PathBuf::from)
        .unwrap_or_else(|| beside_settings(&Settings::path()))
}

/// Import the existing Godot user:// profile once; subsequent writes belong to
/// Bevy's own preferences. The original application name is in project.godot.
fn legacy_path() -> Option<PathBuf> {
    #[cfg(target_os = "windows")]
    let root = std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .map(|p| p.join("Godot/app_userdata"));
    #[cfg(target_os = "macos")]
    let root = std::env::var_os("HOME")
        .map(PathBuf::from)
        .map(|p| p.join("Library/Application Support/Godot/app_userdata"));
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    let root = std::env::var_os("XDG_DATA_HOME")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|p| PathBuf::from(p).join(".local/share")))
        .map(|p| p.join("godot/app_userdata"));
    root.map(|p| p.join("Unturned Godot/profile.cfg"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn optional_notices_and_real_face_metadata_decode_without_optimistic_authority() {
        let p: LocalProfile = serde_json::from_value(
            json!({"name":"Survivor","face":31,"faceCount":32,"freeFaceCount":10,"hasAvatar":true}),
        )
        .unwrap();
        assert_eq!((p.face, p.face_count, p.free_face_count), (31, 32, 10));
        assert!(!p.ready);
        assert_eq!(p.avatar_notice, None);
        assert_eq!(
            beside_settings(Path::new("/tmp/custom/settings.json")),
            PathBuf::from("/tmp/custom/profile.cfg")
        );
    }
}
