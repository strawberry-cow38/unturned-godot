//! Vehicle.MakeSmoke and UpdateRotorFx, using the original textures and thresholds.
use super::*;
use crate::particles::{self, Emitter, ParticleAssets, Spec};
use crate::projectiles::effects::light_unit;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
enum Kind {
    LightSmoke,
    HeavySmoke,
    Fire,
    MainSmoke,
    MainFire,
    TailSmoke,
    TailFire,
    Exhaust,
    MainStrike,
    TailStrike,
    Bonk,
}
const KINDS: [Kind; 8] = [
    Kind::LightSmoke,
    Kind::HeavySmoke,
    Kind::Fire,
    Kind::MainSmoke,
    Kind::MainFire,
    Kind::TailSmoke,
    Kind::TailFire,
    Kind::Exhaust,
];
#[derive(Default)]
struct VehicleEffects {
    nodes: HashMap<Kind, Entity>,
    light: Option<Entity>,
}
#[derive(Resource, Default)]
pub struct Effects {
    bodies: HashMap<Entity, VehicleEffects>,
    materials: HashMap<&'static str, Handle<StandardMaterial>>,
}

fn values(kind: Kind) -> (&'static str, Vec4, f32, f32, usize, Vec2, bool) {
    match kind {
        Kind::LightSmoke => (
            "veh_smoke_1.png",
            Vec4::new(0.55, 0.55, 0.55, 1.),
            2.2,
            2.2,
            20,
            Vec2::new(2., 4.),
            false,
        ),
        Kind::HeavySmoke => (
            "veh_smoke_0.png",
            Vec4::new(0.30, 0.29, 0.27, 1.),
            2.9,
            2.9,
            28,
            Vec2::new(2., 4.),
            false,
        ),
        Kind::Fire => (
            "veh_fire.png",
            Vec4::new(1., 0.72, 0.32, 1.),
            0.7,
            4.5,
            30,
            Vec2::new(1., 2.),
            true,
        ),
        Kind::MainSmoke => (
            "veh_smoke_1.png",
            Vec4::new(0.42, 0.42, 0.42, 1.),
            1.5,
            2.,
            16,
            Vec2::new(0.5, 1.1),
            false,
        ),
        Kind::MainFire => (
            "veh_fire.png",
            Vec4::new(1., 0.72, 0.32, 1.),
            0.6,
            2.6,
            22,
            Vec2::new(0.4, 0.9),
            true,
        ),
        Kind::TailSmoke => (
            "veh_smoke_1.png",
            Vec4::new(0.42, 0.42, 0.42, 1.),
            1.2,
            1.6,
            12,
            Vec2::new(0.3, 0.7),
            false,
        ),
        Kind::TailFire => (
            "veh_fire.png",
            Vec4::new(1., 0.72, 0.32, 1.),
            0.5,
            2.1,
            16,
            Vec2::new(0.25, 0.6),
            true,
        ),
        Kind::Exhaust => (
            "veh_smoke_1.png",
            Vec4::new(0.66, 0.66, 0.64, 0.8),
            1.3,
            1.3,
            14,
            Vec2::new(0.14, 0.34),
            false,
        ),
        Kind::MainStrike => (
            "veh_fire.png",
            Vec4::new(1., 0.85, 0.45, 1.),
            0.30,
            5.5,
            14,
            Vec2::new(0.10, 0.28),
            true,
        ),
        Kind::TailStrike => (
            "veh_fire.png",
            Vec4::new(1., 0.85, 0.45, 1.),
            0.26,
            4.5,
            10,
            Vec2::new(0.08, 0.22),
            true,
        ),
        Kind::Bonk => (
            "veh_smoke_1.png",
            Vec4::new(0.62, 0.60, 0.55, 1.),
            0.55,
            3.2,
            18,
            Vec2::new(0.20, 0.60),
            false,
        ),
    }
}
fn spec(kind: Kind, material: Handle<StandardMaterial>) -> Spec {
    let (_, color, life, speed, amount, size, fire) = values(kind);
    Spec {
        amount: ((amount as f32 * 0.25).round() as usize).max(1),
        life,
        duration: f32::INFINITY,
        spread: if kind == Kind::Exhaust { 16. } else { 25. },
        speed: Vec2::new(speed * 0.6, speed),
        gravity: Vec3::Y * if kind == Kind::Exhaust { 0.7 } else { 1.5 },
        size: size * 0.25,
        spin: if fire {
            Vec2::ZERO
        } else {
            Vec2::new(-35., 35.)
        },
        angle: if fire {
            Vec2::ZERO
        } else {
            Vec2::new(-180., 180.)
        },
        colors: vec![(0., color), (1., color)],
        growth: vec![(0., 1.), (1., 1.)],
        atlas: if fire { UVec2::new(4, 1) } else { UVec2::ONE },
        animation_offset: if fire { Vec2::new(0., 1.) } else { Vec2::ZERO },
        animation_speed: if fire { Vec2::new(5., 9.) } else { Vec2::ZERO },
        animation_loop: fire,
        material: Some(material),
        ..default()
    }
}
fn wanted(kind: Kind, body: &VehicleBody, spec: &data::VehicleSpec) -> bool {
    let exploded = body.flags & 32 != 0;
    let cold = exploded && body.burn_seconds >= 60.;
    if cold {
        return false;
    }
    match kind {
        Kind::LightSmoke => exploded || body.health < 200.,
        Kind::HeavySmoke => exploded || body.health < 100.,
        Kind::Fire => body.on_fire,
        Kind::MainSmoke => spec.heli && body.flight.main_health < 0.7,
        Kind::MainFire => spec.heli && body.flight.main_health <= 0.,
        Kind::TailSmoke => spec.heli && body.flight.tail_health < 0.7,
        Kind::TailFire => spec.heli && body.flight.tail_health <= 0.,
        Kind::Exhaust => {
            !spec.heli
                && !spec.plane
                && spec.box_size != [0.; 3]
                && body.flags & 1 != 0
                && !exploded
        }
        Kind::MainStrike | Kind::TailStrike | Kind::Bonk => false,
    }
}
fn local_position(kind: Kind, spec: &data::VehicleSpec) -> Vec3 {
    match kind {
        Kind::MainSmoke | Kind::MainFire | Kind::MainStrike => Vec3::from_array(spec.rotor_hub),
        Kind::TailSmoke | Kind::TailFire | Kind::TailStrike => {
            Vec3::from_array(spec.tail_rotor_hub)
        }
        Kind::Bonk => Vec3::from_array(spec.box_center),
        Kind::Exhaust => spec.exhaust_pos.map(Vec3::from_array).unwrap_or(Vec3::new(
            spec.box_size[0] * 0.5 - 0.3,
            (spec.box_center[1] - spec.box_size[1] * 0.5 + 0.18).max(0.22),
            spec.box_center[2] + spec.box_size[2] * 0.5 - 0.05,
        )),
        _ => Vec3::new(0., 1.24, -1.70),
    }
}
fn exhaust_amount(puff: f32, rpm: f32) -> usize {
    // Vehicle._Process changes this pool directly after MakeSmoke's initial scale.
    let revs = (rpm / 6000.).clamp(0., 1.);
    if puff > 0. {
        24
    } else if revs > 0.6 {
        16
    } else if revs > 0.25 {
        10
    } else {
        6
    }
}
fn material(
    kind: Kind,
    effects: &mut Effects,
    cache: &mut ContentCache,
    assets: &AssetServer,
    materials: &mut Assets<StandardMaterial>,
) -> Handle<StandardMaterial> {
    let (texture, _, _, _, _, _, fire) = values(kind);
    if let Some(material) = effects.materials.get(texture) {
        return material.clone();
    }
    let original = cache.material(Some(texture), false, assets, materials);
    let texture_handle = materials
        .get(&original)
        .and_then(|m| m.base_color_texture.clone());
    let material = materials.add(StandardMaterial {
        base_color: Color::WHITE.with_alpha(if fire { 0.95 } else { 0.7 }),
        base_color_texture: texture_handle,
        alpha_mode: if fire {
            AlphaMode::Add
        } else {
            AlphaMode::Blend
        },
        unlit: true,
        cull_mode: None,
        emissive: if fire {
            LinearRgba::new(2.5, 1., 0.125, 1.)
        } else {
            LinearRgba::BLACK
        },
        ..default()
    });
    effects.materials.insert(texture, material.clone());
    material
}
pub fn update(
    mut commands: Commands,
    bodies: Query<(Entity, &VehicleBody, &Transform)>,
    mut emitters: Query<&mut Emitter>,
    mut lights: Query<&mut PointLight>,
    mut effects: ResMut<Effects>,
    particles: Res<ParticleAssets>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    mut cache: ResMut<ContentCache>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut meshes: ResMut<Assets<Mesh>>,
) {
    effects.bodies.retain(|body, _| bodies.contains(*body));
    for (entity, body, pose) in &bodies {
        let source = &data::definition(body.type_id).unwrap().spec;
        let exploded = body.flags & 32 != 0;
        let fade = if exploded {
            1. - ((body.burn_seconds - 40.) / 20.).clamp(0., 1.)
        } else {
            1.
        };
        let mut state = effects.bodies.remove(&entity).unwrap_or_default();
        for kind in KINDS {
            if !wanted(kind, body, source) {
                if let Some(node) = state.nodes.remove(&kind) {
                    if let Ok(mut emitter) = emitters.get_mut(node) {
                        emitter.stop();
                    }
                }
                continue;
            }
            let node = if let Some(node) = state.nodes.get(&kind).filter(|e| emitters.contains(**e))
            {
                *node
            } else {
                let (texture, _, _, _, _, _, _) = values(kind);
                if !config.content_root.join(texture).is_file() {
                    continue;
                }
                let material = material(kind, &mut effects, &mut cache, &assets, &mut materials);
                let mut emitter_spec = spec(kind, material);
                if kind == Kind::Exhaust {
                    emitter_spec.direction = pose.rotation * Vec3::new(0., 0.35, 1.);
                }
                let node = particles::spawn(
                    &mut commands,
                    &mut meshes,
                    &particles,
                    emitter_spec,
                    local_position(kind, source),
                    Some(entity),
                    body.net_id as u64 * 41 + kind as u64 + 1,
                );
                commands.queue(move |world: &mut World| {
                    if let Some(mut emitter) = world.get_mut::<Emitter>(node) {
                        emitter.set_opacity(fade);
                    }
                });
                state.nodes.insert(kind, node);
                node
            };
            if let Ok(mut emitter) = emitters.get_mut(node) {
                emitter.set_opacity(fade);
                emitter.set_direction(if kind == Kind::Exhaust {
                    pose.rotation * Vec3::new(0., 0.35, 1.)
                } else {
                    Vec3::Y
                });
                let rotor = match kind {
                    Kind::MainSmoke => Some((body.flight.main_health, 16.)),
                    Kind::TailSmoke => Some((body.flight.tail_health, 12.)),
                    _ => None,
                };
                if let Some((health, max)) = rotor {
                    let t = (1. - health / 0.7).clamp(0., 1.);
                    emitter.set_amount(((max * 0.25).lerp(max, t) * 0.25).round().max(1.) as usize);
                }
                if kind == Kind::Exhaust {
                    emitter.set_amount(exhaust_amount(
                        body.engine.exhaust_puff,
                        body.drivetrain.rpm,
                    ));
                }
            }
        }
        let energy = if body.on_fire {
            if exploded { 3. * fade } else { 1.2 }
        } else {
            0.
        };
        if energy > 0. && state.light.is_none() {
            state.light = Some(
                commands
                    .spawn((
                        Name::new("Vehicle fire light"),
                        PointLight {
                            color: Color::srgb(1., 0.55, 0.2),
                            range: 8.,
                            intensity: energy * light_unit(),
                            ..default()
                        },
                        Transform::from_xyz(0., 1.24, -1.70),
                        ChildOf(entity),
                    ))
                    .id(),
            );
        }
        if let Some(light) = state.light {
            if let Ok(mut light) = lights.get_mut(light) {
                light.intensity = energy * light_unit();
            }
        }
        effects.bodies.insert(entity, state);
    }
}

pub fn feedback(
    mut commands: Commands,
    mut facts: MessageReader<contacts::Feedback>,
    bodies: Query<&VehicleBody>,
    mut effects: ResMut<Effects>,
    particles: Res<ParticleAssets>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Res<Settings>,
    mut cache: ResMut<ContentCache>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut meshes: ResMut<Assets<Mesh>>,
) {
    for fact in facts.read() {
        let Ok(body) = bodies.get(fact.vehicle) else {
            continue;
        };
        let source = &data::definition(body.type_id).unwrap().spec;
        // Source constructs this feedback kit only for helicopters.
        if !source.heli {
            continue;
        }
        let kind = match fact.kind {
            contacts::FeedbackKind::Bonk => Kind::Bonk,
            contacts::FeedbackKind::MainStrike => Kind::MainStrike,
            contacts::FeedbackKind::TailStrike => Kind::TailStrike,
        };
        if !config.content_root.join(values(kind).0).is_file() {
            continue;
        }
        let material = material(kind, &mut effects, &mut cache, &assets, &mut materials);
        let mut spec = spec(kind, material);
        spec.one_shot = true;
        spec.duration = spec.life;
        let emitter = particles::spawn(
            &mut commands,
            &mut meshes,
            &particles,
            spec,
            local_position(kind, source),
            Some(fact.vehicle),
            fact.seed,
        );
        let state = effects.bodies.entry(fact.vehicle).or_default();
        if let Some(old) = state.nodes.insert(kind, emitter) {
            commands.entity(old).try_despawn();
        }
        if config.content_root.join("impact_metal.wav").is_file() {
            audio::spawn_sound(
                &mut commands,
                &assets,
                fact.vehicle,
                "impact_metal.wav",
                false,
                10_f32.powf(2. / 20.),
                1.,
                12.,
                90.,
                settings.master_volume,
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn authoritative_damage_repair_and_cooling_drive_real_emitters_without_charring_smoke() {
        let mut app = App::new();
        app.add_plugins((
            MinimalPlugins,
            TransformPlugin,
            bevy::asset::AssetPlugin::default(),
        ))
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .init_asset::<AudioSource>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldConfig>()
        .init_resource::<Settings>()
        .init_resource::<damage::WreckMaterial>()
        .init_resource::<Effects>()
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ))
        .add_message::<crate::entities::CoreEvent>()
        .add_message::<contacts::Feedback>()
        .add_plugins(crate::particles::ParticlePlugin)
        .add_systems(Update, (damage::update, update, feedback).chain());
        app.world_mut()
            .spawn((PlayerCamera, Transform::from_xyz(0., 3., 8.)));
        let car = super::super::tests::body(&mut app, "jeep", Vec3::ZERO);
        {
            let mut b = app.world_mut().get_mut::<VehicleBody>(car).unwrap();
            b.flags = 0;
            b.health = 150.;
        }
        for _ in 0..30 {
            app.update();
        }
        let smoke = app.world().resource::<Effects>().bodies[&car].nodes[&Kind::LightSmoke];
        assert!(
            !app.world().resource::<Effects>().bodies[&car]
                .nodes
                .contains_key(&Kind::HeavySmoke)
        );
        let smoke_material = app
            .world()
            .get::<MeshMaterial3d<StandardMaterial>>(smoke)
            .unwrap()
            .0
            .clone();
        let mesh = &app.world().get::<Mesh3d>(smoke).unwrap().0;
        assert!(
            app.world()
                .resource::<Assets<Mesh>>()
                .get(mesh)
                .unwrap()
                .count_vertices()
                > 0,
            "accepted damage must generate actual render geometry"
        );
        {
            let mut b = app.world_mut().get_mut::<VehicleBody>(car).unwrap();
            b.flags = 32;
            b.health = 0.;
            b.on_fire = true;
            b.burn_seconds = 50.;
        }
        for _ in 0..30 {
            app.update();
        }
        assert_eq!(
            app.world()
                .get::<MeshMaterial3d<StandardMaterial>>(smoke)
                .unwrap()
                .0,
            smoke_material,
            "charred hull material must never replace smoke's texture"
        );
        let fire = app.world().resource::<Effects>().bodies[&car].nodes[&Kind::Fire];
        let mesh = &app.world().get::<Mesh3d>(fire).unwrap().0;
        let colors = app
            .world()
            .resource::<Assets<Mesh>>()
            .get(mesh)
            .unwrap()
            .attribute(Mesh::ATTRIBUTE_COLOR)
            .unwrap();
        let bevy::mesh::VertexAttributeValues::Float32x4(colors) = colors else {
            panic!("particle colors")
        };
        assert!(
            !colors.is_empty() && colors.iter().all(|c| c[3] <= 0.50001),
            "loaded 50-second wreck must begin at its current fade"
        );
        {
            let mut b = app.world_mut().get_mut::<VehicleBody>(car).unwrap();
            b.burn_seconds = 60.;
            b.on_fire = false;
        }
        for _ in 0..160 {
            app.update();
        }
        assert!(
            app.world().resource::<Effects>().bodies[&car]
                .nodes
                .is_empty()
        );
        assert!(
            app.world().get_entity(smoke).is_err() && app.world().get_entity(fire).is_err(),
            "cooled wreck stops births, then the last particles dissipate"
        );
        let helicopter = super::super::tests::body(&mut app, "huey", Vec3::X * 8.);
        app.world_mut().write_message(contacts::Feedback {
            vehicle: helicopter,
            kind: contacts::FeedbackKind::MainStrike,
            seed: 18,
        });
        app.update();
        let strike = app.world().resource::<Effects>().bodies[&helicopter].nodes[&Kind::MainStrike];
        assert_eq!(
            app.world().get::<Emitter>(strike).unwrap().debug_state().1,
            4,
            "accepted blade strike bursts the source quarter-scaled14-particle pool"
        );
        app.world_mut().write_message(contacts::Feedback {
            vehicle: helicopter,
            kind: contacts::FeedbackKind::MainStrike,
            seed: 19,
        });
        app.update();
        let restarted =
            app.world().resource::<Effects>().bodies[&helicopter].nodes[&Kind::MainStrike];
        assert_ne!(strike, restarted);
        assert!(
            app.world().get_entity(strike).is_err(),
            "repeated strikes restart the original burst instead of accumulating infinite emitters"
        );
    }
    #[test]
    fn cold_start_exhaust_then_idle_and_revs_keep_source_pool_steps() {
        let source = &data::named("jeep").unwrap().spec;
        let mut engine = audio::Engine::new(2., false);
        engine.step(source, true, 0.02);
        assert_eq!(exhaust_amount(engine.exhaust_puff, 1000.), 24);
        for _ in 0..51 {
            engine.step(source, true, 0.02);
        }
        assert_eq!(exhaust_amount(engine.exhaust_puff, 1000.), 6);
        assert_eq!(exhaust_amount(engine.exhaust_puff, 3000.), 10);
        assert_eq!(exhaust_amount(engine.exhaust_puff, 5000.), 16);
    }
    #[test]
    fn original_flame_uses_four_real_frames_and_global_quarter_scale() {
        let fire = spec(Kind::Fire, Handle::default());
        assert_eq!(fire.atlas, UVec2::new(4, 1));
        assert_eq!(fire.amount, 8);
        assert_eq!(fire.size, Vec2::new(0.25, 0.5));
        assert_eq!(fire.animation_speed, Vec2::new(5., 9.));
        let smoke = spec(Kind::LightSmoke, Handle::default());
        assert_eq!(smoke.amount, 5);
        assert_eq!(smoke.life, 2.2);
        assert_eq!(smoke.size, Vec2::new(0.5, 1.));
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        for texture in ["veh_fire.png", "veh_smoke_0.png", "veh_smoke_1.png"] {
            assert!(root.join(texture).is_file());
        }
    }
}
