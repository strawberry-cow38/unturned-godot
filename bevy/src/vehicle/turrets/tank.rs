use super::*;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Data {
    mg_box: [[f32; 3]; 2],
    mg_barrel: [[f32; 3]; 2],
    top_hatch: [[f32; 3]; 2],
    driver_hatch: [[f32; 3]; 2],
    top_hinge: [f32; 3],
    driver_hinge: [f32; 3],
    glacis_down: [f32; 3],
    top_closed: f32,
    driver_closed: f32,
    driver_lift: f32,
    driver_slide: f32,
    swing_seconds: f32,
    turret_size: [f32; 3],
    turret_center: [f32; 3],
    barrel_size: [f32; 3],
    barrel_center: [f32; 3],
}
pub(super) static DATA: LazyLock<Data> = LazyLock::new(|| {
    #[derive(Deserialize)]
    struct Export {
        tank: Data,
    }
    serde_json::from_str::<Export>(include_str!("../../../data/vehicles.json"))
        .unwrap()
        .tank
});
fn zone(v: [[f32; 3]; 2]) -> (Vec3, Vec3) {
    (Vec3::from_array(v[0]), Vec3::from_array(v[1]))
}
pub(super) struct Hatches {
    top: Option<Entity>,
    driver: Option<Entity>,
    pub top_phase: f32,
    pub driver_phase: f32,
    open_curve: Vec<[f32; 2]>,
    close_curve: Vec<[f32; 2]>,
}
impl Hatches {
    pub fn animate(
        &mut self,
        driver_closed: bool,
        top_closed: bool,
        dt: f32,
        transforms: &mut Query<&mut Transform>,
    ) {
        let d = &*DATA;
        let step = dt / d.swing_seconds;
        self.top_phase = approach(self.top_phase, if top_closed { 0. } else { 1. }, step);
        self.driver_phase = approach(self.driver_phase, if driver_closed { 0. } else { 1. }, step);
        let sample = |phase, closed| {
            let curve = if closed {
                &self.close_curve
            } else {
                &self.open_curve
            };
            let value = if curve.is_empty() {
                // Source TankHatchSwing fallback uses the clip's direction too.
                let at = if closed { 1. - phase } else { phase };
                at * at * (3. - 2. * at)
            } else {
                crate::world::interactables::easing(phase, !closed, Some(curve))
            };
            if closed { value.clamp(0., 1.) } else { value }
        };
        let top = sample(self.top_phase, top_closed);
        if let Some(entity) = self.top {
            if let Ok(mut pose) = transforms.get_mut(entity) {
                pose.rotation = Quat::from_rotation_x((d.top_closed * (1. - top)).to_radians());
            }
        }
        let driver = sample(self.driver_phase, driver_closed);
        if let Some(entity) = self.driver {
            if let Ok(mut pose) = transforms.get_mut(entity) {
                let closing = 1. - driver.clamp(0., 1.);
                pose.rotation =
                    Quat::from_rotation_x((d.driver_closed * (1. - driver)).to_radians());
                pose.translation = Vec3::from_array(d.driver_hinge)
                    + Vec3::Y * (d.driver_lift * closing)
                    + Vec3::from_array(d.glacis_down) * (d.driver_slide * closing);
            }
        }
    }
}
fn curve(root: &Path, name: &str) -> Vec<[f32; 2]> {
    std::fs::read_to_string(root.join(format!("objects/door_curves/Fridge_0_{name}.txt")))
        .ok()
        .into_iter()
        .flat_map(|s| {
            s.lines()
                .filter_map(|line| {
                    let mut v = line
                        .split_whitespace()
                        .filter_map(|v| v.parse::<f32>().ok());
                    Some([v.next()?, v.next()?])
                })
                .collect::<Vec<_>>()
        })
        .collect()
}
#[allow(clippy::too_many_arguments)]
pub(super) fn build(
    commands: &mut Commands,
    entity: Entity,
    def: &VehicleDefinition,
    root: &Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    body: Handle<StandardMaterial>,
    frames: &[Frame],
) -> Option<Hatches> {
    let (Some(cannon), Some(mg)) = (frames.first(), frames.get(1)) else {
        return None;
    };
    let d = &*DATA;
    let spec = &def.spec;
    if let Some(treads) = &spec.treads {
        let dark = materials.add(StandardMaterial {
            base_color: Color::srgb(0.14, 0.14, 0.15),
            perceptual_roughness: 1.,
            ..default()
        });
        spawn_mesh(
            commands,
            entity,
            treads,
            root,
            cache,
            meshes,
            dark,
            Transform::default(),
        );
    }
    let mut hatches = Hatches {
        top: None,
        driver: None,
        top_phase: 1.,
        driver_phase: 1.,
        open_curve: curve(root, "open"),
        close_curve: curve(root, "close"),
    };
    let mg_pivot = Vec3::from_array(spec.turrets.as_ref().unwrap()[1].pivot);
    for file in spec.turret_meshes.iter().flatten() {
        if file != "tank_turret.txt" {
            spawn_mesh(
                commands,
                cannon.yaw,
                file,
                root,
                cache,
                meshes,
                body.clone(),
                Transform::default(),
            );
            continue;
        }
        if let Some((mesh, _)) = split(
            cache,
            meshes,
            root,
            file,
            "turret-rest",
            &[zone(d.mg_box), zone(d.mg_barrel), zone(d.top_hatch)],
            false,
        ) {
            piece(
                commands,
                cannon.yaw,
                "tank_turret",
                mesh,
                body.clone(),
                Transform::default(),
            );
        }
        for (name, parent, z) in [
            ("tank_mg_box", mg.yaw, d.mg_box),
            ("tank_mg_barrel", mg.pitch, d.mg_barrel),
        ] {
            if let Some((mesh, _)) = split(cache, meshes, root, file, name, &[zone(z)], true) {
                piece(
                    commands,
                    parent,
                    name,
                    mesh,
                    body.clone(),
                    Transform::from_translation(-mg_pivot),
                );
            }
        }
        if let Some((mesh, _)) = split(
            cache,
            meshes,
            root,
            file,
            "top-lid",
            &[zone(d.top_hatch)],
            true,
        ) {
            let hinge = Vec3::from_array(d.top_hinge);
            let parent = commands
                .spawn((
                    Name::new("Tank turret hatch"),
                    Transform::from_translation(hinge),
                    Visibility::Inherited,
                    ChildOf(cannon.yaw),
                ))
                .id();
            piece(
                commands,
                parent,
                "tank_top_hatch",
                mesh,
                body.clone(),
                Transform::from_translation(-hinge),
            );
            hatches.top = Some(parent);
        }
    }
    if let Some(file) = &spec.gun_mesh {
        spawn_mesh(
            commands,
            cannon.pitch,
            file,
            root,
            cache,
            meshes,
            body.clone(),
            Transform::default(),
        );
    }
    // The tank's two source collision boxes are separate moving bodies: neither
    // can lift or destabilize the hull that carries it. Players still collide.
    for (name, parent, size, center) in [
        (
            "Tank turret collider",
            cannon.yaw,
            d.turret_size,
            d.turret_center,
        ),
        (
            "Tank barrel collider",
            cannon.pitch,
            d.barrel_size,
            d.barrel_center,
        ),
    ] {
        commands.spawn((
            Name::new(name),
            Transform::from_translation(Vec3::from_array(center)),
            ChildOf(parent),
            RigidBody::KinematicPositionBased,
            Collider::cuboid(size[0] * 0.5, size[1] * 0.5, size[2] * 0.5),
            CollisionGroups::new(Group::GROUP_4, Group::GROUP_2),
        ));
    }
    // Body replacement is deferred with the initial builder commands, preserving
    // the source's peeled visor instead of drawing it twice.
    if spec.body.as_deref() == Some("tank_hull.txt") {
        if let (Some((rest, _)), Some((visor, data))) = (
            split(
                cache,
                meshes,
                root,
                "tank_hull.txt",
                "hull-rest",
                &[zone(d.driver_hatch)],
                false,
            ),
            split(
                cache,
                meshes,
                root,
                "tank_hull.txt",
                "driver-lid",
                &[zone(d.driver_hatch)],
                true,
            ),
        ) {
            commands.queue(move |world: &mut World| {
                let children = world
                    .get::<Children>(entity)
                    .map(|c| c.iter().collect::<Vec<_>>())
                    .unwrap_or_default();
                for child in children {
                    if world
                        .get::<Name>(child)
                        .is_some_and(|n| n.as_str() == "tank_hull.txt")
                    {
                        world.entity_mut(child).insert(Mesh3d(rest.clone()));
                    }
                }
            });
            let hinge = Vec3::from_array(d.driver_hinge);
            let parent = commands
                .spawn((
                    Name::new("Tank driver hatch"),
                    Transform::from_translation(hinge),
                    Visibility::Inherited,
                    ChildOf(entity),
                ))
                .id();
            let pose = Transform::from_translation(-hinge).with_scale(Vec3::new(1., 1.02, 1.02));
            piece(
                commands,
                parent,
                "tank_driver_hatch",
                visor,
                body.clone(),
                pose,
            );
            let cap = cap(&data);
            piece(
                commands,
                parent,
                "tank_driver_hatch_cap",
                meshes.add(cap.mesh()),
                body,
                pose,
            );
            hatches.driver = Some(parent);
        }
    }
    Some(hatches)
}
fn cap(source: &ObjData) -> ObjData {
    let corners = [
        Vec3::new(-0.849, 1.903, -3.390),
        Vec3::new(0.849, 1.903, -3.390),
        Vec3::new(0.849, 1.856, -3.408),
        Vec3::new(-0.849, 1.856, -3.408),
    ];
    let normal = (corners[1] - corners[0])
        .cross(corners[2] - corners[0])
        .normalize_or_zero();
    let mut out = ObjData::default();
    for (indices, n) in [
        ([0, 1, 2], normal),
        ([0, 2, 3], normal),
        ([0, 2, 1], -normal),
        ([0, 3, 2], -normal),
    ] {
        for index in indices {
            let p = corners[index];
            let nearest = source
                .positions
                .iter()
                .enumerate()
                .min_by(|(_, a), (_, b)| {
                    p.distance_squared(Vec3::from_array(**a))
                        .total_cmp(&p.distance_squared(Vec3::from_array(**b)))
                })
                .unwrap()
                .0;
            out.positions.push(p.to_array());
            out.normals.push(n.to_array());
            out.uvs.push(source.uvs[nearest]);
            out.colors.push(source.colors[nearest]);
        }
    }
    out.min = corners
        .into_iter()
        .fold(Vec3::splat(f32::INFINITY), Vec3::min);
    out.max = corners
        .into_iter()
        .fold(Vec3::splat(f32::NEG_INFINITY), Vec3::max);
    out
}
