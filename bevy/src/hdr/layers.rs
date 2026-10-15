//! Bevy 0.20 CPU clustering preserves each viewport's RenderLayers.
use bevy::{light::cluster::GlobalClusterSettings, prelude::*};

pub(crate) fn configure(settings: Option<ResMut<GlobalClusterSettings>>) {
    if let Some(mut settings) = settings {
        // The 0.20 GPU cluster object buffer has no per-view render-layer mask.
        // Its global light list would illuminate world geometry with layer1
        // viewmodel mirrors and layer18 shadowless reflection copies as well.
        // CPU assign_objects_to_clusters performs the required layer test.
        settings.gpu_clustering = None;
    }
}
