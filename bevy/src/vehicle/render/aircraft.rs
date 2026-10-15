//! Authored rotors/propellers and the source scoutcopter's procedural pod.
use super::*;

#[derive(Clone, Copy)]
pub enum PartKind {
    MainRotor,
    TailRotor,
    Propeller,
    PropBlades,
    PropDisc,
    Gear(usize),
}
#[derive(Component)]
pub struct AircraftPart {
    pub body: Entity,
    pub kind: PartKind,
}

pub fn build(
    commands: &mut Commands,
    body: Entity,
    d: &VehicleDefinition,
    root: &Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    body_material: Handle<StandardMaterial>,
    wheel_material: Handle<StandardMaterial>,
) {
    let s = &d.spec;
    let frame = materials.add(StandardMaterial {
        base_color: Color::srgb(0.16, 0.16, 0.18),
        perceptual_roughness: 0.85,
        ..default()
    });
    let blade = materials.add(StandardMaterial {
        base_color: Color::srgb(0.10, 0.10, 0.11),
        perceptual_roughness: 0.85,
        cull_mode: None,
        ..default()
    });
    if s.heli {
        if s.heli_body_meshes.is_none() {
            pod(
                commands,
                body,
                meshes,
                materials,
                body_material,
                frame.clone(),
            );
        }
        let prefix = s.heli_rotor_mesh_prefix.as_deref().unwrap_or("huey");
        for (kind, name, hub, radius, fallback_span) in [
            (
                PartKind::MainRotor,
                "main",
                s.rotor_hub,
                s.rotor_radius,
                11.14,
            ),
            (
                PartKind::TailRotor,
                "tail",
                s.tail_rotor_hub,
                s.tail_rotor_radius,
                2.56,
            ),
        ] {
            let pivot = commands
                .spawn((
                    Name::new(format!("Rotor {name}")),
                    Transform::from_translation(Vec3::from_array(hub)),
                    Visibility::default(),
                    ChildOf(body),
                    AircraftPart { body, kind },
                ))
                .id();
            let filename = format!("{prefix}_rotor_{name}_blades.txt");
            let span = cache
                .load_mesh(&root.join(&filename), meshes)
                .ok()
                .map(|(_, obj)| obj.max.x - obj.min.x)
                .filter(|s| *s > 0.01)
                .unwrap_or(fallback_span);
            let loaded = spawn_mesh(
                commands,
                pivot,
                &filename,
                root,
                cache,
                meshes,
                blade.clone(),
                Transform::from_scale(Vec3::splat(radius * 2. / span)),
            );
            if loaded.is_none() {
                let (height, width) = if name == "main" {
                    (0.035, 0.20)
                } else {
                    (0.03, 0.10)
                };
                primitive(
                    commands,
                    pivot,
                    "Rotor blades",
                    meshes.add(Cuboid::new(radius * 2., height, width)),
                    blade.clone(),
                    Vec3::ZERO,
                    Vec3::ZERO,
                );
            }
            if name == "main" {
                primitive(
                    commands,
                    pivot,
                    "Rotor hub",
                    meshes.add(ConicalFrustum {
                        radius_top: 0.11,
                        radius_bottom: 0.13,
                        height: 0.16,
                    }),
                    frame.clone(),
                    Vec3::ZERO,
                    Vec3::ZERO,
                );
            }
        }
    }
    if s.plane {
        if let Some(name) = &s.missile_mesh {
            let material = materials.add(StandardMaterial {
                base_color: Color::srgb(0.34, 0.34, 0.36),
                metallic: 0.15,
                perceptual_roughness: 0.65,
                cull_mode: None,
                ..default()
            });
            spawn_mesh(
                commands,
                body,
                name,
                root,
                cache,
                meshes,
                material,
                Transform::default(),
            );
        }
        if let Some(name) = &s.steer_mesh {
            let material = materials.add(StandardMaterial {
                base_color: Color::srgb(0.14, 0.14, 0.16),
                metallic: 0.2,
                perceptual_roughness: 0.7,
                cull_mode: None,
                ..default()
            });
            spawn_mesh(
                commands,
                body,
                name,
                root,
                cache,
                meshes,
                material,
                Transform::from_xyz(0., 0.30, 0.),
            );
        }
        if let Some(prefix) = &s.prop_mesh_prefix {
            let filename = format!("{prefix}_prop.txt");
            let hub = cache
                .load_mesh(&root.join(&filename), meshes)
                .ok()
                .map(|(_, obj)| (obj.min + obj.max) * 0.5)
                .unwrap_or(Vec3::from_array(s.prop_hub));
            let pivot = commands
                .spawn((
                    Name::new("Propeller"),
                    Transform::from_translation(hub),
                    Visibility::default(),
                    ChildOf(body),
                    AircraftPart {
                        body,
                        kind: PartKind::Propeller,
                    },
                ))
                .id();
            if let Some(blades) = spawn_mesh(
                commands,
                pivot,
                &filename,
                root,
                cache,
                meshes,
                blade,
                Transform::from_translation(-hub),
            ) {
                if root.join(format!("{prefix}_prop_disc.txt")).is_file() {
                    commands.entity(blades).insert(AircraftPart {
                        body,
                        kind: PartKind::PropBlades,
                    });
                    let disc_material = materials.add(StandardMaterial {
                        base_color: Color::srgba(0.12, 0.12, 0.13, 0.28),
                        alpha_mode: AlphaMode::Blend,
                        cull_mode: None,
                        unlit: true,
                        ..default()
                    });
                    if let Some(disc) = spawn_mesh(
                        commands,
                        pivot,
                        &format!("{prefix}_prop_disc.txt"),
                        root,
                        cache,
                        meshes,
                        disc_material,
                        Transform::from_translation(-hub),
                    ) {
                        commands.entity(disc).insert((
                            AircraftPart {
                                body,
                                kind: PartKind::PropDisc,
                            },
                            Visibility::Hidden,
                            bevy::light::NotShadowCaster,
                        ));
                    }
                }
            }
        }
        if s.retract_gear {
            let strut = materials.add(StandardMaterial {
                base_color: Color::srgb(0.72, 0.72, 0.74),
                metallic: 0.1,
                perceptual_roughness: 0.6,
                cull_mode: None,
                ..default()
            });
            for (i, wheel) in s.wheels().iter().enumerate() {
                let pivot = commands
                    .spawn((
                        Name::new(format!("Landing gear {i}")),
                        Transform::from_xyz(wheel.0, 0.55, wheel.2),
                        Visibility::default(),
                        ChildOf(body),
                        AircraftPart {
                            body,
                            kind: PartKind::Gear(i),
                        },
                    ))
                    .id();
                let name = if wheel.0.abs() < 1. {
                    "fighterjet_gear_nose.txt"
                } else if wheel.0 < 0. {
                    "fighterjet_gear_mainL.txt"
                } else {
                    "fighterjet_gear_mainR.txt"
                };
                if let Some(strut) = spawn_mesh(
                    commands,
                    pivot,
                    name,
                    root,
                    cache,
                    meshes,
                    strut.clone(),
                    Transform::default(),
                ) {
                    commands.entity(strut).insert(bevy::light::NotShadowCaster);
                }
                if let Some(name) = &s.wheel {
                    let scale = s.wheel_radius(i) / s.wheel_radius;
                    spawn_mesh(
                        commands,
                        pivot,
                        name,
                        root,
                        cache,
                        meshes,
                        wheel_material.clone(),
                        Transform::from_xyz(0., wheel.1 - 0.55, 0.)
                            .with_scale(Vec3::splat(scale))
                            .with_rotation(if wheel.0 < 0. {
                                Quat::from_rotation_y(std::f32::consts::PI)
                            } else {
                                Quat::IDENTITY
                            }),
                    );
                }
            }
        }
    }
}
fn primitive(
    commands: &mut Commands,
    body: Entity,
    name: &str,
    mesh: Handle<Mesh>,
    material: Handle<StandardMaterial>,
    at: Vec3,
    rotation: Vec3,
) {
    commands.spawn((
        Name::new(name.to_owned()),
        Mesh3d(mesh),
        MeshMaterial3d(material),
        Transform::from_translation(at).with_rotation(Quat::from_euler(
            EulerRot::YXZ,
            rotation.y.to_radians(),
            rotation.x.to_radians(),
            rotation.z.to_radians(),
        )),
        ChildOf(body),
    ));
}
fn pod(
    commands: &mut Commands,
    body: Entity,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    paint: Handle<StandardMaterial>,
    frame: Handle<StandardMaterial>,
) {
    // BuildHeliModel's Pod branch. These are its actual dimensions, not inferred collision boxes.
    let glass = materials.add(StandardMaterial {
        base_color: Color::srgba(0.55, 0.70, 0.78, 0.35),
        alpha_mode: AlphaMode::Blend,
        metallic: 0.3,
        perceptual_roughness: 0.1,
        cull_mode: None,
        ..default()
    });
    for (name, size, at, material) in [
        ("Body", [1.05, 0.80, 1.35], [0., 0.10, 0.05], paint.clone()),
        (
            "Engine",
            [0.72, 0.52, 0.62],
            [0., 0.34, 0.62],
            frame.clone(),
        ),
        (
            "Seat L",
            [0.42, 0.10, 0.44],
            [-0.26, 0.18, 0.02],
            frame.clone(),
        ),
        (
            "Seat R",
            [0.42, 0.10, 0.44],
            [0.26, 0.18, 0.02],
            frame.clone(),
        ),
        (
            "Seat back",
            [1., 0.46, 0.09],
            [0., 0.42, 0.27],
            frame.clone(),
        ),
        (
            "Tail fin",
            [0.05, 0.62, 0.42],
            [0., 0.62, 2.98],
            frame.clone(),
        ),
        (
            "Stabiliser",
            [0.86, 0.04, 0.26],
            [0., 0.34, 2.82],
            frame.clone(),
        ),
    ] {
        primitive(
            commands,
            body,
            name,
            meshes.add(Cuboid::from_size(Vec3::from_array(size))),
            material,
            Vec3::from_array(at),
            Vec3::ZERO,
        );
    }
    for (name, radius, sectors, rings, at, material) in [
        ("Nose", 0.52, 14, 8, [0., 0.10, -0.60], paint),
        ("Canopy", 0.46, 14, 8, [0., 0.30, -0.42], glass),
        ("Fuel tank", 0.30, 12, 7, [0., 0.30, 1.02], frame.clone()),
    ] {
        primitive(
            commands,
            body,
            name,
            meshes.add(Sphere::new(radius).mesh().uv(sectors, rings)),
            material,
            Vec3::from_array(at),
            Vec3::ZERO,
        );
    }
    for side in [-1., 1.] {
        primitive(
            commands,
            body,
            "Skid",
            meshes.add(Cylinder::new(0.055, 2.30).mesh().resolution(10)),
            frame.clone(),
            Vec3::new(side * 0.52, -0.72, 0.10),
            Vec3::new(90., 0., 0.),
        );
        primitive(
            commands,
            body,
            "Skid tip",
            meshes.add(Cuboid::new(0.09, 0.09, 0.22)),
            frame.clone(),
            Vec3::new(side * 0.52, -0.66, -1.10),
            Vec3::new(-22., 0., 0.),
        );
        for z in [-0.45, 0.62] {
            primitive(
                commands,
                body,
                "Strut",
                meshes.add(Cylinder::new(0.045, 0.66).mesh().resolution(10)),
                frame.clone(),
                Vec3::new(side * 0.38, -0.40, z),
                Vec3::new(0., 0., side * 22.),
            );
        }
    }
    primitive(
        commands,
        body,
        "Tail boom",
        meshes.add(Cylinder::new(0.075, 2.70).mesh().resolution(10)),
        frame.clone(),
        Vec3::new(0., 0.34, 1.85),
        Vec3::new(90., 0., 0.),
    );
    primitive(
        commands,
        body,
        "Mast",
        meshes.add(Cylinder::new(0.06, 0.60).mesh().resolution(10)),
        frame,
        Vec3::new(0., 0.80, 0.20),
        Vec3::ZERO,
    );
}
pub fn animate(
    bodies: Query<&VehicleBody>,
    mut parts: Query<(&AircraftPart, &mut Transform, &mut Visibility)>,
) {
    for (part, mut transform, mut visible) in &mut parts {
        let Ok(body) = bodies.get(part.body) else {
            continue;
        };
        match part.kind {
            PartKind::MainRotor => {
                transform.rotation = Quat::from_rotation_y(body.flight.main_phase)
            }
            PartKind::TailRotor => {
                transform.rotation = Quat::from_rotation_z(std::f32::consts::FRAC_PI_2)
                    * Quat::from_rotation_y(body.flight.tail_phase)
            }
            PartKind::Propeller => {
                transform.rotation = Quat::from_rotation_z(body.flight.prop_phase)
            }
            PartKind::PropBlades => {
                *visible = if body.flight.spool > 0.35 {
                    Visibility::Hidden
                } else {
                    Visibility::Inherited
                }
            }
            PartKind::PropDisc => {
                *visible = if body.flight.spool > 0.35 {
                    Visibility::Inherited
                } else {
                    Visibility::Hidden
                }
            }
            PartKind::Gear(i) => {
                let spec = &crate::vehicle::data::definition(body.type_id).unwrap().spec;
                let angle = if spec.wheels()[i].2 < 0. {
                    -85f32
                } else {
                    95f32
                };
                transform.rotation =
                    Quat::from_rotation_x(angle.to_radians() * (1. - body.flight.gear_deploy));
                *visible = if body.flight.gear_deploy > 0.01 {
                    Visibility::Inherited
                } else {
                    Visibility::Hidden
                };
            }
        }
    }
}
