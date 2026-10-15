//! Original muzzleflash.gdshader vertex stage. Billboard orientation belongs to
//! the current rendering view; a CPU pose tied to the main camera loses the
//! star in a side-facing scope or reflection view.
use bevy::{
    asset::embedded_asset,
    mesh::MeshTag,
    pbr::{ExtendedMaterial, MaterialExtension},
    prelude::*,
    render::render_resource::AsBindGroup,
    shader::ShaderRef,
};

#[derive(Asset, AsBindGroup, TypePath, Clone, Debug)]
pub(super) struct Billboard {}

impl MaterialExtension for Billboard {
    fn vertex_shader() -> ShaderRef {
        "embedded://unturned_bevy/vehicle/turrets/fx/flash.wesl".into()
    }
    fn enable_prepass() -> bool {
        false
    }
}

pub(super) type FlashMaterial = ExtendedMaterial<StandardMaterial, Billboard>;

pub(super) struct FlashMaterialPlugin;
impl Plugin for FlashMaterialPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "flash.wesl");
        app.add_plugins(MaterialPlugin::<FlashMaterial>::default());
    }
}

/// A mesh instance owns its source random roll. Bit-preserving transport in the
/// existing instance tag keeps one shared material for all accepted flashes.
pub(super) fn roll_tag(roll: f32) -> MeshTag {
    MeshTag::new(roll.to_bits())
}

pub(super) fn source_material(texture: Handle<Image>) -> FlashMaterial {
    ExtendedMaterial {
        base: StandardMaterial {
            base_color: Color::linear_rgba(2.6, 2.6, 2.6, 1.),
            base_color_texture: Some(texture),
            unlit: true,
            cull_mode: None,
            alpha_mode: AlphaMode::Add,
            ..default()
        },
        extension: Billboard {},
    }
}

#[cfg(test)]
mod tests;
