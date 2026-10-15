//! Presentation of accepted vehicle destruction; health and blast damage stay in the server.
use super::*;
use crate::entities::CoreEvent;

#[derive(Default)]
pub struct Wreck {
    pub impulse: Option<ExternalImpulse>,
    pub charred: bool,
}
#[derive(Component)]
pub(super) struct WheelDebris {
    remaining: f32,
    material: Handle<StandardMaterial>,
}
#[derive(Resource, Default)]
pub struct WreckMaterial(Option<Handle<StandardMaterial>>);
impl WreckMaterial {
    pub(in crate::vehicle) fn handle(
        &mut self,
        materials: &mut Assets<StandardMaterial>,
    ) -> Handle<StandardMaterial> {
        self.0
            .get_or_insert_with(|| {
                materials.add(StandardMaterial {
                    base_color: Color::srgb(0.05, 0.05, 0.05),
                    perceptual_roughness: 1.,
                    cull_mode: None,
                    ..default()
                })
            })
            .clone()
    }
}

pub fn update(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    mut bodies: Query<(Entity, &mut VehicleBody, &Transform)>,
    hierarchy: Query<&ChildOf>,
    parts: Query<(Entity, &MeshMaterial3d<StandardMaterial>), Without<crate::particles::Emitter>>,
    wheels: Query<(&render::WheelVisual, &Transform)>,
    aircraft: Query<(Entity, &render::aircraft::AircraftPart)>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Res<Settings>,
    mut wreck_material: ResMut<WreckMaterial>,
) {
    let charred = wreck_material.handle(&mut materials);
    for event in events.read().filter(|e| e.kind == "vehicleExploded") {
        let Some(id) = event.data["netId"].as_u64() else {
            continue;
        };
        let Some((entity, mut body, pose)) =
            bodies.iter_mut().find(|(_, b, _)| b.net_id == id as u32)
        else {
            continue;
        };
        body.wreck.impulse = Some(ExternalImpulse {
            impulse: crate::player::vector(&event.data["impulse"]).unwrap_or(Vec3::Y * 18000.),
            torque_impulse: crate::player::vector(&event.data["torqueImpulse"])
                .unwrap_or(Vec3::X * 2800.),
        });
        let spec = &data::definition(body.type_id).unwrap().spec;
        if config.content_root.join("explosion.ogg").is_file() {
            super::audio::spawn_sound(
                &mut commands,
                &assets,
                entity,
                "explosion.ogg",
                false,
                10_f32.powf(6. / 20.),
                1.,
                20.,
                200.,
                settings.master_volume,
            );
        }
        if let Some(name) = &spec.wheel {
            let Ok((mesh, _)) = cache.load_mesh(&config.content_root.join(name), &mut meshes)
            else {
                continue;
            };
            let original =
                cache.material(spec.wheel_tex.as_deref(), false, &assets, &mut materials);
            for (wheel, local) in &wheels {
                if wheel.body != entity {
                    continue;
                }
                let position = pose.transform_point(local.translation);
                let outward = (position - pose.translation)
                    .with_y(0.)
                    .try_normalize()
                    .unwrap_or(Vec3::X);
                // The source uses an independently randomized wheel kick. The stable event
                // seed makes replayed accepted facts reproducible without changing its ranges.
                let mut random =
                    (event.sequence.wrapping_mul(0x9e3779b97f4a7c15) ^ wheel.index as u64).max(1);
                let mut range =
                    |extent: f32| (crate::particles::random(&mut random) * 2. - 1.) * extent;
                let linear =
                    (outward * 45. + Vec3::Y * 55. + Vec3::new(range(15.), 0., range(15.))) / 18.;
                let angular = Vec3::new(range(12.), range(12.), range(12.));
                let copy = materials.get(&original).cloned().unwrap_or_default();
                let material = materials.add(copy);
                let debris = commands
                    .spawn((
                        Name::new("Detached vehicle wheel"),
                        WheelDebris {
                            remaining: 10.,
                            material: material.clone(),
                        },
                        Transform::from_translation(position),
                        Visibility::default(),
                        RigidBody::Dynamic,
                        Collider::ball(spec.wheel_radius),
                        ColliderMassProperties::Mass(18.),
                        CollisionGroups::new(Group::GROUP_3, Group::GROUP_1),
                        Velocity { linear, angular },
                        Damping {
                            linear_damping: 0.1,
                            angular_damping: 0.1,
                        },
                    ))
                    .id();
                commands.spawn((
                    Mesh3d(mesh.clone()),
                    MeshMaterial3d(material),
                    Transform::from_scale(local.scale),
                    ChildOf(debris),
                ));
            }
        }
    }
    for (entity, mut body, _) in &mut bodies {
        if body.flags & 32 == 0 {
            continue;
        }
        body.flags &= !(1 | 2 | 4 | 8 | 64 | 128);
        if !body.wreck.charred {
            for (part, _) in &parts {
                let mut ancestor = part;
                for _ in 0..6 {
                    if ancestor == entity {
                        commands
                            .entity(part)
                            .insert(MeshMaterial3d(charred.clone()));
                        break;
                    }
                    let Ok(parent) = hierarchy.get(ancestor) else {
                        break;
                    };
                    ancestor = parent.parent();
                }
            }
            body.wreck.charred = true;
        }
        for (part, aircraft) in &aircraft {
            if aircraft.body == entity
                && matches!(
                    aircraft.kind,
                    render::aircraft::PartKind::MainRotor
                        | render::aircraft::PartKind::TailRotor
                        | render::aircraft::PartKind::Gear(_)
                )
            {
                commands.entity(part).insert(Visibility::Hidden);
            }
        }
    }
}
pub fn hide_wheels(
    bodies: Query<&VehicleBody>,
    mut wheels: Query<(&render::WheelVisual, &mut Visibility)>,
) {
    for (wheel, mut visible) in &mut wheels {
        if bodies.get(wheel.body).is_ok_and(|b| b.flags & 32 != 0) {
            *visible = Visibility::Hidden;
        }
    }
}
pub fn fade_debris(
    mut commands: Commands,
    time: Res<Time>,
    ui: Res<UiState>,
    mut debris: Query<(Entity, &mut WheelDebris)>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    if crate::player::simulation_paused(&ui) {
        return;
    }
    for (entity, mut wheel) in &mut debris {
        wheel.remaining -= time.delta_secs();
        if wheel.remaining <= 0. {
            commands.entity(entity).despawn();
        } else if wheel.remaining <= 1. {
            if let Some(mut material) = materials.get_mut(&wheel.material) {
                material.alpha_mode = AlphaMode::Blend;
                material.base_color = material.base_color.with_alpha(wheel.remaining);
            }
        }
    }
}
