//! Installed parts follow the accepted item state. No UI click changes a mesh.
use super::*;
use serde_json::Value;
#[derive(Resource, Default)]
pub(super) struct Mounted {
    owner: Option<Entity>,
    signature: Value,
    parts: Vec<Entity>,
}
#[derive(Component)]
pub(super) struct Reticle;
#[derive(Clone, Copy)]
struct Dot {
    position: Vec3,
    relief: f32,
    size: f32,
}
fn dot(mesh: &str) -> Option<Dot> {
    let (position, relief, size) = match mesh {
        "red_dot_sight.txt" => (Vec3::new(0., -0.1884, -0.0655), -0.3611, 0.005),
        "red_halo_sight.txt" => (Vec3::new(0., -0.297, -0.0813), -0.2918, 0.016),
        "red_kobra_sight.txt" => (Vec3::new(0., -0.1884, -0.0655), -0.3611, 0.02),
        _ => return None,
    };
    Some(Dot {
        position,
        relief,
        size,
    })
}
#[derive(Debug)]
struct Part {
    slot: &'static str,
    mesh: String,
    texture: Option<String>,
    position: Vec3,
    color: Color,
    id: i64,
}
fn parts(visual: &GunVisual, ids: &Value, catalog: &ItemCatalog) -> Vec<Part> {
    let mut result = vec![];
    for (slot, key) in [
        ("Sight", "sightId"),
        ("Magazine", "magazineId"),
        ("Barrel", "barrelId"),
        ("Tactical", "tacticalId"),
    ] {
        let id = ids[key].as_i64().unwrap_or(-1);
        let item = (id > 0).then(|| catalog.items.get(&(id as u16))).flatten();
        let own = item.and_then(|i| i.attachment_mesh.as_ref()).cloned();
        let mesh = own.or_else(|| match slot {
            "Sight" if id < 0 => visual.sight.clone(),
            "Magazine" if id < 0 => visual.magazine.clone(),
            _ => None,
        });
        let Some(mesh) = mesh else {
            continue;
        };
        let iron = visual.sight.as_ref() == Some(&mesh) || mesh.contains("iron_sights");
        let texture = item.and_then(|i| i.attachment_texture.clone()).or_else(|| {
            (slot == "Sight" && iron)
                .then(|| visual.sight_texture.clone())
                .flatten()
        });
        let position = match slot {
            "Sight" if !iron => visual.sight_hook.map_or(visual.sight_position, |hook| {
                hook + Vec3::new(0., 0.371, 0.0206)
            }),
            "Sight" => visual.sight_position,
            "Magazine" => visual.magazine_position,
            "Tactical" => Vec3::new(-0.0601, 0.3815, -0.0851),
            _ => Vec3::new(0., 0.7307, -0.0818),
        };
        let color = if texture.is_some() {
            Color::WHITE
        } else {
            match slot {
                "Sight" if iron => visual.sight_color,
                "Sight" => {
                    scopes::calibration(&mesh).map_or(Color::srgb(0.06, 0.065, 0.075), |c| c.color)
                }
                "Magazine" => Color::srgb(0.07, 0.07, 0.08),
                "Tactical" => Color::srgb(0.06, 0.06, 0.065),
                _ => Color::srgb(0.05, 0.05, 0.055),
            }
        };
        result.push(Part {
            slot,
            mesh,
            texture,
            position,
            color,
            id,
        });
    }
    result
}
/// AttachmentFit.BottleFit: seat against actual bore geometry, grow only to
/// cover a larger muzzle face. The bottle mesh's neck remains at the origin.
fn bottle_fit(visual: &GunVisual, vertices: &[[f32; 3]]) -> (Vec3, f32) {
    let m = visual.muzzle;
    if m == Vec3::ZERO {
        return (Vec3::new(0., 0.7307, -0.0818), 1.);
    }
    let band: Vec<Vec3> = vertices
        .iter()
        .copied()
        .map(Vec3::from_array)
        .filter(|v| (v.x - m.x).abs() <= 0.12 && (v.z - m.z).abs() <= 0.05 && v.y <= m.y + 0.01)
        .collect();
    let tip = band.iter().map(|v| v.y).fold(f32::NEG_INFINITY, f32::max);
    if !tip.is_finite() {
        return (m, 1.);
    }
    let seat_y = if m.y - tip > 0.08 { m.y } else { m.y.min(tip) };
    let face = |behind: f32, ahead: f32| {
        band.iter()
            .filter(|v| v.y >= seat_y - behind && v.y <= seat_y + ahead)
            .map(|v| (v.x - m.x).abs().max((v.z - m.z).abs()))
            .fold(0., f32::max)
    };
    let mut half = face(0.015, 0.005);
    if half <= 0. {
        half = face(0.05, 0.01);
    }
    let scale = ((half * 1.05 / (0.12727 * 0.42)).clamp(1., 3.) * 100.).round() / 100.;
    (Vec3::new(m.x, seat_y, m.z), scale)
}
#[allow(clippy::too_many_arguments)]
pub(super) fn sync(
    mut commands: Commands,
    mut mounted: ResMut<Mounted>,
    mut state: ResMut<ViewmodelState>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let owner = state.gun.filter(|_| state.is_gun);
    let ids = &snapshot.0["weapon"]["attachments"];
    if mounted.owner == owner && mounted.signature == *ids {
        return;
    }
    for entity in mounted.parts.drain(..) {
        commands.entity(entity).try_despawn();
    }
    mounted.owner = owner;
    mounted.signature = ids.clone();
    state.scope_zoom = 0.;
    let Some(owner) = owner else {
        return;
    };
    let Some(visual) = state.visuals.get(&state.asset).cloned() else {
        return;
    };
    state.aim_hook = visual.aim;
    for mut part in parts(&visual, ids, &catalog) {
        let Ok((mesh, _)) = cache.load_mesh(&config.content_root.join(&part.mesh), &mut meshes)
        else {
            continue;
        };
        let mut scale = 1.;
        if part.id == 9400 && part.slot == "Barrel" {
            if let Ok((_, data)) =
                cache.load_mesh(&config.content_root.join(&visual.mesh), &mut meshes)
            {
                (part.position, scale) = bottle_fit(&visual, &data.positions);
            }
        }
        if part.slot == "Magazine" && part.texture.is_none() {
            let candidate = part.mesh.replace(".txt", "_albedo.png");
            if config.content_root.join(&candidate).is_file() {
                part.texture = Some(candidate);
                part.color = Color::WHITE;
            }
        }
        let material = materials.add(StandardMaterial {
            base_color: part.color,
            base_color_texture: part.texture.map(|path| assets.load(path)),
            perceptual_roughness: if part.id == 9400 { 0.35 } else { 1. },
            reflectance: 0.,
            cull_mode: None,
            ..default()
        });
        let entity = commands
            .spawn((
                Name::new(format!("{} attachment {}", part.slot, part.id)),
                Mesh3d(mesh),
                MeshMaterial3d(material),
                Transform::from_translation(part.position).with_scale(Vec3::splat(scale)),
                RenderLayers::layer(LAYER),
                ChildOf(owner),
            ))
            .id();
        mounted.parts.push(entity);
        if part.slot == "Tactical" {
            commands
                .entity(entity)
                .insert((lights::RailEmitter, lights::LightSurface(part.id as u16)));
        }
        if part.slot == "Sight" {
            if let Some(dot) = dot(&part.mesh) {
                state.aim_hook = part.position + dot.position + Vec3::Y * dot.relief;
                let texture = assets.load(part.mesh.replace("_sight.txt", "_reticle.png"));
                let material = materials.add(StandardMaterial {
                    unlit: true,
                    base_color: Color::srgb(1., 0., 0.),
                    base_color_texture: Some(texture.clone()),
                    emissive_texture: Some(texture),
                    emissive: LinearRgba::new(2.5, 0., 0., 1.),
                    alpha_mode: AlphaMode::Blend,
                    cull_mode: None,
                    ..default()
                });
                commands.spawn((
                    Name::new("Optic reticle"),
                    Reticle,
                    Mesh3d(meshes.add(Rectangle::new(dot.size, dot.size))),
                    MeshMaterial3d(material),
                    Transform::from_translation(dot.position),
                    RenderLayers::layer(LAYER),
                    ChildOf(entity),
                    bevy::light::NotShadowCaster,
                ));
            } else if let Some(calibration) = scopes::calibration(&part.mesh) {
                if !calibration.integral {
                    state.aim_hook = part.position + calibration.aim;
                }
                state.scope_zoom = 90. / calibration.fov;
                commands.entity(entity).insert(scopes::ScopeMount {
                    calibration,
                    reticle: Some(part.mesh.replace("_sight.txt", "_reticle.png")),
                });
            }
        }
    }
}
pub(super) fn billboard(
    cameras: Query<&GlobalTransform, (With<ViewmodelCamera>, Without<Reticle>)>,
    parents: Query<&GlobalTransform, Without<Reticle>>,
    mut reticles: Query<(&ChildOf, &mut Transform, &mut GlobalTransform), With<Reticle>>,
) {
    let Ok(camera) = cameras.single() else {
        return;
    };
    for (parent, mut local, mut world) in &mut reticles {
        if let Ok(pose) = parents.get(parent.parent()) {
            local.rotation = pose.rotation().inverse() * camera.rotation();
            *world = pose.mul_transform(*local);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    fn catalog() -> ItemCatalog {
        let mut catalog = ItemCatalog::default();
        for (id, mesh, texture) in [
            (5, "eaglefire_sight.txt", None),
            (6, "mag_military_30.txt", None),
            (7, "military_suppressor_barrel.txt", None),
            (22, "red_dot_sight.txt", None),
            (
                9400,
                "water_bottle_silencer.txt",
                Some("bottled_water_albedo.png"),
            ),
        ] {
            catalog.items.insert(id,serde_json::from_value(json!({"id":id,"name":"Part","type":"sight","attachmentMesh":mesh,"attachmentTexture":texture})).unwrap());
        }
        catalog
    }
    #[test]
    fn source_roster_bottle_fit_keeps_size_and_seats_on_the_actual_barrel() {
        let root = WorldConfig::default().content_root;
        let visuals = load_gun_visuals(&root).unwrap();
        let mut normal = 0;
        for visual in visuals.values() {
            let data = crate::content::ObjData::load(&root.join(&visual.mesh)).unwrap();
            let (seat, scale) = bottle_fit(visual, &data.positions);
            if scale == 1. {
                normal += 1;
            }
            assert!((1. ..=3.).contains(&scale));
            if visual.name == "launcher_rocket" || visual.name == "sawed_off" {
                assert!(scale > 1.5, "{}={scale}", visual.name);
            }
            if visual.name == "eaglefire" {
                assert!(seat.y > 0.7 && seat.y < visual.muzzle.y - 0.03);
            }
        }
        assert!(normal >= 40);
    }
    #[test]
    fn accepted_parts_replace_exact_slot_and_restore_irons_after_optic_removal() {
        let root = WorldConfig::default().content_root;
        let visuals = load_gun_visuals(&root).unwrap();
        let cat = catalog();
        let mut ids = json!({"sightId":22,"magazineId":0,"barrelId":7,"tacticalId":-1});
        for name in ["eaglefire", "nightraider"] {
            let visual = &visuals[name];
            let p = parts(visual, &ids, &cat);
            assert_eq!(p.len(), 2);
            assert!(!p.iter().any(|p| p.slot == "Magazine"));
            let sight = p.iter().find(|p| p.slot == "Sight").unwrap();
            assert_eq!(
                sight.position,
                visual.sight_hook.unwrap() + Vec3::new(0., 0.371, 0.0206)
            );
            let dot = dot(&sight.mesh).unwrap();
            let eye = sight.position + dot.position + Vec3::Y * dot.relief;
            assert_eq!(eye.x, sight.position.x + dot.position.x);
            assert_eq!(eye.z, sight.position.z + dot.position.z);
            assert!((eye.y - (sight.position.y + dot.position.y) - dot.relief).abs() < 1e-6);
            assert_eq!(
                p.iter().find(|p| p.slot == "Barrel").unwrap().position,
                Vec3::new(0., 0.7307, -0.0818)
            );
        }
        let visual = &visuals["eaglefire"];
        // The real catalog names the Eaglefire's factory mesh from sights.tsv.
        let mut cat = cat;
        cat.items.get_mut(&5).unwrap().attachment_mesh = visual.sight.clone();
        ids["sightId"] = json!(5);
        ids["barrelId"] = json!(-1);
        ids["magazineId"] = json!(6);
        let p = parts(visual, &ids, &cat);
        assert_eq!(p.len(), 2);
        let sight = p.iter().find(|p| p.slot == "Sight").unwrap();
        assert_eq!(Some(&sight.mesh), visual.sight.as_ref());
        assert_eq!(sight.position, visual.sight_position);
        assert_eq!(sight.color, visual.sight_color);
    }
}
