use super::{VehicleBody, data::VehicleDefinition};
use crate::content::ContentCache;
use bevy::{
    asset::RenderAssetUsages,
    image::{CompressedImageFormats, ImageSampler, ImageType},
    prelude::*,
};
use bevy_rapier3d::prelude::*;
use std::path::Path;
pub mod aircraft;

#[derive(Component)]
pub struct WheelVisual {
    pub body: Entity,
    pub index: usize,
    pub left: bool,
}
#[derive(Component)]
pub struct SteeringWheel(pub Entity);
pub fn build(
    commands: &mut Commands,
    entity: Entity,
    d: &VehicleDefinition,
    variant: u8,
    root: &Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    images: &mut Assets<Image>,
    assets: &AssetServer,
) {
    let s = &d.spec;
    let paint = d.paints[variant as usize];
    let paint_key = format!("vehicle:{}:{variant}", d.key);
    let body_material = if let Some(m) = cache.materials.get(&paint_key) {
        m.clone()
    } else {
        let texture = s
            .palette
            .as_ref()
            .and_then(|name| std::fs::read(root.join(name)).ok())
            .and_then(|bytes| {
                Image::from_buffer(
                    &bytes,
                    ImageType::Extension("png"),
                    CompressedImageFormats::NONE,
                    true,
                    ImageSampler::Default,
                    RenderAssetUsages::default(),
                )
                .ok()
            })
            .map(|mut image| {
                if let Some(bytes) = &mut image.data {
                    for pixel in bytes.chunks_exact_mut(4) {
                        if pixel[3] < 128 {
                            for i in 0..3 {
                                pixel[i] = (paint[i].clamp(0., 1.) * 255.).round() as u8;
                            }
                            pixel[3] = 255;
                        }
                    }
                }
                images.add(image)
            });
        let m = materials.add(StandardMaterial {
            base_color: if texture.is_some() {
                Color::WHITE
            } else {
                Color::srgba(paint[0], paint[1], paint[2], paint[3])
            },
            base_color_texture: texture,
            perceptual_roughness: 0.85,
            ..default()
        });
        cache.materials.insert(paint_key, m.clone());
        m
    };
    let (body_override, lighting) =
        super::lighting::geometry::build(commands, entity, d, root, cache, meshes, materials);
    let door = super::bifold::build(
        commands,
        entity,
        d,
        root,
        cache,
        meshes,
        body_material.clone(),
    );
    let body_override = body_override.or_else(|| door.as_ref().map(|(mesh, _)| mesh.clone()));
    let body_names = s.body.iter().chain(s.heli_body_meshes.iter().flatten());
    for name in body_names {
        if s.body.as_ref() == Some(name)
            && let Some(mesh) = &body_override
        {
            commands.spawn((
                Name::new(name.clone()),
                Mesh3d(mesh.clone()),
                MeshMaterial3d(body_material.clone()),
                Transform::IDENTITY,
                Visibility::Inherited,
                ChildOf(entity),
            ));
            continue;
        }
        spawn_mesh(
            commands,
            entity,
            name,
            root,
            cache,
            meshes,
            body_material.clone(),
            Transform::default(),
        );
    }
    let steering = Vec3::from_array(s.steer_axis).try_normalize().map(|_| {
        commands
            .spawn((
                Name::new("Steering wheel pivot"),
                SteeringWheel(entity),
                Transform::from_translation(Vec3::from_array(s.steer_pivot)),
                Visibility::default(),
                ChildOf(entity),
            ))
            .id()
    });
    if let Some(parts) = &s.parts {
        for (name, c) in parts {
            if super::lighting::geometry::consumes(name) {
                continue;
            }
            let m = materials.add(StandardMaterial {
                base_color: Color::srgba(c[0], c[1], c[2], c[3]),
                perceptual_roughness: 0.85,
                ..default()
            });
            let wheel = name.contains("steer") && steering.is_some();
            spawn_mesh(
                commands,
                if wheel { steering.unwrap() } else { entity },
                name,
                root,
                cache,
                meshes,
                m,
                if wheel {
                    Transform::from_translation(-Vec3::from_array(s.steer_pivot))
                } else {
                    Transform::default()
                },
            );
        }
    }
    for (name, target, color, parent) in [
        (
            s.seat_model_file.as_deref(),
            Vec3::from_array(s.seat_model),
            Color::srgb(0.22, 0.22, 0.24),
            entity,
        ),
        (
            s.steer_model.as_deref(),
            Vec3::ZERO,
            Color::srgb(0.13, 0.11, 0.08),
            steering.unwrap_or(entity),
        ),
    ] {
        if let Some(name) = name {
            let centre = cache
                .load_mesh(&root.join(name), meshes)
                .ok()
                .map(|(_, m)| (m.min + m.max) * 0.5)
                .unwrap_or_default();
            let material = materials.add(StandardMaterial {
                base_color: color,
                perceptual_roughness: 0.85,
                ..default()
            });
            spawn_mesh(
                commands,
                parent,
                name,
                root,
                cache,
                meshes,
                material,
                Transform::from_translation(target - centre),
            );
        }
    }
    super::glazing::build(
        commands,
        entity,
        d,
        root,
        cache,
        meshes,
        materials,
        door.as_ref().map(|(_, door)| door),
    );
    if let Some((_, door)) = door {
        commands.entity(entity).insert(door);
    }
    let wheel_mat = cache.material(s.wheel_tex.as_deref(), false, assets, materials);
    if let Some(name) = &s.wheel {
        let split = if !s.tracked && !s.plane {
            super::tires::load(&root.join(name), cache, meshes)
        } else {
            None
        };
        for (index, w) in s.wheels().iter().enumerate() {
            let left = w.0 < 0.;
            if s.retract_gear {
                continue;
            }
            let pose = Transform::from_xyz(w.0, w.1 - 0.25, w.2)
                .with_scale(Vec3::splat(s.wheel_radius(index) / s.wheel_radius))
                .with_rotation(if left {
                    Quat::from_rotation_y(std::f32::consts::PI)
                } else {
                    Quat::IDENTITY
                });
            if let Some(wheel) = spawn_mesh(
                commands,
                entity,
                name,
                root,
                cache,
                meshes,
                wheel_mat.clone(),
                pose,
            ) {
                commands.entity(wheel).insert(WheelVisual {
                    body: entity,
                    index,
                    left,
                });
                if let Some((tire, rim)) = &split {
                    commands.entity(wheel).insert(Mesh3d(rim.clone()));
                    commands.spawn((
                        Name::new(format!("Tire {index}")),
                        Mesh3d(tire.clone()),
                        MeshMaterial3d(wheel_mat.clone()),
                        super::tires::TireVisual {
                            body: entity,
                            index,
                        },
                        Transform::default(),
                        Visibility::Inherited,
                        ChildOf(wheel),
                    ));
                }
            }
        }
    }
    commands.entity(entity).insert(lighting);
    super::turrets::build(
        commands,
        entity,
        d,
        root,
        cache,
        meshes,
        materials,
        body_material.clone(),
    );
    if s.heli || s.plane {
        aircraft::build(
            commands,
            entity,
            d,
            root,
            cache,
            meshes,
            materials,
            body_material,
            wheel_mat,
        );
    }
    super::collision::hull_mesh::build(commands, entity, d, root, cache);
    if s.body.is_some()
        && s.hull_bands.is_none()
        && s.hull_boxes.is_none()
        && std::env::var("UG_BOXHULL").as_deref() != Ok("1")
    {
        commands
            .entity(entity)
            .insert(super::collision::PendingHull);
    }
    let mut shapes = Vec::new();
    let mut box_shape = |size: [f32; 3], center: [f32; 3], yaw: f32| {
        if size.iter().all(|n| *n > 0.) {
            shapes.push((
                Vec3::from_array(center),
                Quat::from_rotation_y(yaw),
                Collider::cuboid(size[0] * 0.5, size[1] * 0.5, size[2] * 0.5),
            ));
        }
    };
    box_shape(s.box_size, s.box_center, 0.);
    if let Some((size, center)) = d.roof {
        box_shape(size, center, 0.);
    }
    for (size, center) in s.extra_boxes.iter().flatten() {
        box_shape(*size, *center, 0.);
    }
    for (size, center, yaw) in s.hull_boxes.iter().flatten() {
        box_shape(*size, *center, yaw.to_radians());
    }
    commands.entity(entity).insert((
        Collider::compound(shapes),
        ColliderMassProperties::Density(0.0),
    ));
}

pub(super) fn spawn_mesh(
    commands: &mut Commands,
    parent: Entity,
    name: &str,
    root: &Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    material: Handle<StandardMaterial>,
    pose: Transform,
) -> Option<Entity> {
    match cache.load_mesh(&root.join(name), meshes) {
        Ok((mesh, _)) => Some(
            commands
                .spawn((
                    Name::new(name.to_owned()),
                    Mesh3d(mesh),
                    MeshMaterial3d(material),
                    pose,
                    ChildOf(parent),
                ))
                .id(),
        ),
        Err(e) => {
            warn!("Vehicle mesh {name}: {e}");
            None
        }
    }
}
pub fn animate_wheels(
    bodies: Query<(&VehicleBody, &RayCastVehicleController)>,
    mut visuals: Query<(&WheelVisual, &mut Transform)>,
) {
    for (visual, mut transform) in &mut visuals {
        let Ok((_, controller)) = bodies.get(visual.body) else {
            continue;
        };
        let Some(wheel) = controller.wheels.get(visual.index) else {
            continue;
        };
        transform.translation =
            wheel.chassis_connection_point + wheel.direction * wheel.state.suspension_length;
        // Rapier owns the physics phase; the mesh receives it once, like VehicleWheel3D's child.
        transform.rotation = Quat::from_axis_angle(-wheel.direction, wheel.steering)
            * Quat::from_rotation_x(wheel.state.rotation)
            * if visual.left {
                Quat::from_rotation_y(std::f32::consts::PI)
            } else {
                Quat::IDENTITY
            };
    }
}
pub fn animate_steering(
    bodies: Query<&VehicleBody>,
    mut wheels: Query<(&SteeringWheel, &mut Transform)>,
) {
    for (wheel, mut transform) in &mut wheels {
        if let Ok(body) = bodies.get(wheel.0) {
            if let Some((_, axis, angle)) = body.steering_wheel() {
                transform.rotation = Quat::from_axis_angle(axis, angle);
            }
        }
    }
}
