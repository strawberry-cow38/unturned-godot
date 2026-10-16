use super::*;
use bevy::{
    asset::RenderAssetUsages,
    image::ImageSampler,
    render::render_resource::{Extent3d, TextureDimension, TextureFormat},
};

pub(super) struct Art {
    pub cube: Handle<Mesh>,
    pub quad: Handle<Mesh>,
    pub texture: Handle<Image>,
}
impl Art {
    pub fn load(meshes: &mut Assets<Mesh>, images: &mut Assets<Image>) -> Self {
        let mut pixels = Vec::with_capacity(64 * 64 * 4);
        for row in 0i32..64 {
            for col in 0i32..64 {
                let inside = if (3..=30).contains(&row) {
                    (col - 32).abs() <= row - 3
                } else {
                    (31..=58).contains(&row) && (col - 32).abs() <= 9
                };
                pixels.extend_from_slice(&[255, 255, 255, if inside { 255 } else { 0 }]);
            }
        }
        let mut image = Image::new(
            Extent3d {
                width: 64,
                height: 64,
                depth_or_array_layers: 1,
            },
            TextureDimension::D2,
            pixels,
            TextureFormat::Rgba8UnormSrgb,
            RenderAssetUsages::default(),
        );
        image.sampler = ImageSampler::linear();
        Self {
            cube: meshes.add(Cuboid::from_length(0.13)),
            quad: meshes.add(Rectangle::new(0.17, 0.24)),
            texture: images.add(image),
        }
    }
}
fn base(p: &wires::Port, occupied: bool, data_live: bool) -> Color {
    if matches!(p.kind.as_str(), "DataOut" | "DataIn") {
        return if data_live {
            Color::srgb(0.35, 0.88, 1.)
        } else {
            Color::srgb(0.16, 0.42, 0.50)
        };
    }
    match p.role.as_str() {
        "TurnOn" => Color::srgb(0.30, 0.90, 0.40),
        "TurnOff" => Color::srgb(0.95, 0.30, 0.30),
        _ => {
            if occupied {
                Color::srgba(0.30, 0.30, 0.34, 0.74)
            } else {
                Color::srgba(0.80, 0.80, 0.84, 0.48)
            }
        }
    }
}
pub(super) fn cube(
    p: &wires::Port,
    occupied: bool,
    data_live: bool,
    highlight: Highlight,
) -> StandardMaterial {
    let mut color = base(p, occupied, data_live);
    let mut emission = LinearRgba::BLACK;
    match highlight {
        Highlight::None => {}
        Highlight::Focus => {
            let c = color.to_srgba();
            emission = Color::srgb(
                c.red + (1. - c.red) * 0.55,
                c.green + (1. - c.green) * 0.55,
                c.blue + (1. - c.blue) * 0.55,
            )
            .to_linear()
                * 1.15;
        }
        Highlight::Good | Highlight::Bad => {
            let c = if highlight == Highlight::Good {
                Color::srgb(0.30, 0.90, 0.42)
            } else {
                Color::srgb(0.95, 0.28, 0.28)
            };
            emission = c.to_linear() * 0.55;
            let c = c.to_srgba();
            color = Color::srgba(c.red, c.green, c.blue, 0.92);
        }
    }
    StandardMaterial {
        base_color: color,
        emissive: emission,
        metallic: 0.,
        perceptual_roughness: 0.6,
        alpha_mode: if p.role == "None" || p.role.is_empty() {
            AlphaMode::Blend
        } else {
            AlphaMode::Opaque
        },
        ..default()
    }
}
pub(super) fn arrow(p: &wires::Port, data_live: bool) -> StandardMaterial {
    let c = base(p, false, data_live).to_srgba();
    StandardMaterial {
        base_color: Color::srgba(c.red, c.green, c.blue, 0.95),
        unlit: true,
        alpha_mode: AlphaMode::Blend,
        double_sided: true,
        cull_mode: None,
        ..default()
    }
}
pub(super) fn arrow_pose(p: &wires::Port, position: Vec3) -> Transform {
    let outward = if position.x.abs() > 1e-3 || position.y.abs() > 1e-3 {
        if position.x.abs() >= position.y.abs() {
            Vec3::X * position.x.signum()
        } else {
            Vec3::Y * position.y.signum()
        }
    } else {
        Vec3::Y
    };
    let flow = if p.kind == "Consumer" {
        -outward
    } else {
        outward
    };
    Transform::from_translation(outward * 0.20)
        .with_rotation(wires::segment::pose(Vec3::ZERO, flow).unwrap().rotation)
}

/// AxisBillboard.Tick pins local +Y and keeps the previous bearing exactly on
/// that axis. The caller transforms the camera into this arrow's local frame.
pub(super) fn face_camera(quad: &mut Transform, camera: Vec3) {
    if camera.x * camera.x + camera.z * camera.z >= 1e-8 {
        quad.rotation = Quat::from_rotation_y(camera.x.atan2(camera.z));
    }
}
