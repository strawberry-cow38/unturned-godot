//! Feed the authority the same moving triangles that the native client renders.
use super::*;
use bevy::render::render_resource::PrimitiveTopology;

#[derive(Clone)]
struct Stamp {
    position: Vec3,
    rotation: Quat,
    articulation: [f32; 4],
    damage: u16,
    mounts: Vec<f32>,
    glass: Vec<f32>,
}
impl Stamp {
    fn of(
        body: &VehicleBody,
        pose: &Transform,
        mounts: Option<&turrets::Mounts>,
        glazing: Option<&glazing::Glazing>,
    ) -> Self {
        Self {
            position: pose.translation,
            rotation: pose.rotation,
            mounts: mounts.map_or_else(Vec::new, turrets::Mounts::signature),
            glass: glazing.map_or_else(Vec::new, |g| g.signature.clone()),
            damage: (body.popped_tire_mask as u16) | ((body.flags as u16) << 8),
            articulation: [
                body.flight.main_phase,
                body.flight.tail_phase,
                body.flight.prop_phase,
                body.flight.gear_deploy,
            ],
        }
    }
    fn changed(&self, other: &Self) -> bool {
        self.damage != other.damage
            || self.mounts != other.mounts
            || self.glass.len() != other.glass.len()
            || self
                .glass
                .iter()
                .zip(&other.glass)
                .any(|(a, b)| (a - b).abs() > 0.00001)
            || self.position.distance_squared(other.position) > 0.000001
            || self.rotation.angle_between(other.rotation).abs() > 0.001
            || self
                .articulation
                .iter()
                .zip(other.articulation)
                .any(|(a, b)| (a - b).abs() > 0.001)
    }
}
#[derive(Resource, Default)]
pub struct GeometryUploads {
    previous: HashMap<u32, Stamp>,
    revision: u64,
    tick: u64,
    map: String,
    epoch: u64,
}
pub(super) fn shift_origin(world: &mut World) {
    if let Some(mut uploads) = world.get_resource_mut::<GeometryUploads>() {
        uploads.previous.clear();
    }
}
pub fn upload(
    bridge: Res<CoreBridge>,
    model: Res<UiModel>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<crate::player::WorldSessionEpoch>,
    world: Res<WorldState>,
    config: Res<WorldConfig>,
    geometry: Res<WorldGeometry>,
    bodies: Query<(
        Entity,
        &VehicleBody,
        &Transform,
        Option<&turrets::Mounts>,
        Option<&glazing::Glazing>,
    )>,
    hierarchy: Query<(
        Option<&ChildOf>,
        Option<&Visibility>,
        Has<turrets::crew::Crew>,
    )>,
    parts: Query<
        (
            Entity,
            &Mesh3d,
            &GlobalTransform,
            &Visibility,
            Option<&render::WheelVisual>,
            Option<&tires::TireVisual>,
            Option<&glazing::Pane>,
        ),
        Without<crate::particles::Emitter>,
    >,
    meshes: Res<Assets<Mesh>>,
    mut uploaded: ResMut<GeometryUploads>,
) {
    if !world.ready || !model.connected || !model.network.owns_world {
        uploaded.previous.clear();
        return;
    }
    let tick = snapshot.0["tick"].as_u64().unwrap_or(0);
    if uploaded.revision != geometry.revision
        || uploaded.map != config.map_name
        || tick < uploaded.tick
        || uploaded.epoch != epoch.0
    {
        // The static world upload clears the managed BVH's dynamic subtrees too.
        uploaded.previous.clear();
        uploaded.revision = geometry.revision;
        uploaded.map = config.map_name.clone();
        uploaded.epoch = epoch.0;
    }
    uploaded.tick = tick;
    let mut changed = HashMap::new();
    let mut live = HashSet::new();
    let roots: HashMap<_, _> = bodies
        .iter()
        .map(|(entity, body, pose, mounts, glazing)| {
            live.insert(body.net_id);
            let stamp = Stamp::of(body, pose, mounts, glazing);
            if uploaded
                .previous
                .get(&body.net_id)
                .is_none_or(|last| last.changed(&stamp))
            {
                changed.insert(body.net_id, (stamp, Vec::new()));
            }
            (entity, body.net_id)
        })
        .collect();
    let vanished: Vec<_> = uploaded
        .previous
        .keys()
        .filter(|id| !live.contains(id))
        .copied()
        .collect();
    for id in vanished {
        if bridge
            .request(
                "worldDynamicGeometry",
                json!({"key":format!("vehicle:{id}"),"meshes":[]}),
            )
            .is_ok()
        {
            uploaded.previous.remove(&id);
        }
    }
    for (entity, mesh, pose, visibility, wheel, tire, pane) in &parts {
        if *visibility == Visibility::Hidden {
            continue;
        }
        // Source fallback canopy is visual-only: no standalone ray shape.
        // Its registered bounds may still classify a nearby real hull contact.
        if pane.is_some_and(|p| !p.has_triangles()) {
            continue;
        }
        // Mirror Vehicle.Owning's bounded ancestry lookup; a child pivot is still one vehicle.
        let mut owner = entity;
        let mut net_id = None;
        for _ in 0..5 {
            if let Some(id) = roots.get(&owner) {
                net_id = Some(*id);
                break;
            }
            let Ok((parent, visibility, crew)) = hierarchy.get(owner) else {
                break;
            };
            // TargetDummy's explicit source box is the only crew hit region.
            // Bind-pose skin, arms and attached gun are not vehicle hulls.
            if crew || visibility == Some(&Visibility::Hidden) {
                break;
            }
            let Some(parent) = parent else {
                break;
            };
            owner = parent.parent();
        }
        let Some(id) = net_id else {
            continue;
        };
        let Some((_, output)) = changed.get_mut(&id) else {
            continue;
        };
        let Some(mesh) = meshes.get(&mesh.0) else {
            continue;
        };
        // Rotor bullets resolve against the tiny source hub regions, not the entire blade.
        // Aircraft/tracked wheels likewise have no separately shootable tire in the source.
        let part = if let Some(pane) = pane {
            format!("glass:{}", pane.index)
        } else if let Some(tire) = tire {
            format!("tire:{}", tire.index)
        } else if let Some(wheel) = wheel.filter(|wheel| {
            bodies.get(wheel.body).is_ok_and(|(_, body, _, _, _)| {
                let spec = &data::definition(body.type_id).unwrap().spec;
                !spec.plane && !spec.tracked
            })
        }) {
            format!("tire:{}", wheel.index)
        } else {
            "hull".into()
        };
        if let Some(row) = mesh_request(mesh, pose, id, &part) {
            output.push(row);
        }
    }
    for (id, (stamp, meshes)) in changed {
        if meshes.is_empty() {
            continue;
        } // Deferred native children have not reached propagation yet.
        match bridge.request(
            "worldDynamicGeometry",
            json!({"key":format!("vehicle:{id}"),"meshes":meshes}),
        ) {
            Ok(_) => {
                uploaded.previous.insert(id, stamp);
            }
            Err(error) => warn!("Vehicle hit geometry #{id}: {error}"),
        }
    }
}
fn mesh_request(
    mesh: &Mesh,
    pose: &GlobalTransform,
    net_id: u32,
    part: &str,
) -> Option<serde_json::Value> {
    if mesh.primitive_topology() != PrimitiveTopology::TriangleList {
        return None;
    }
    let points = mesh.attribute(Mesh::ATTRIBUTE_POSITION)?.as_float3()?;
    let vertices: Vec<_> = points
        .iter()
        .map(|p| pose.transform_point(Vec3::from_array(*p)).to_array())
        .collect();
    let indices: Vec<_> = if let Some(indices) = mesh.indices() {
        indices.iter().collect()
    } else {
        (0..points.len()).collect()
    };
    if !indices.len().is_multiple_of(3) {
        return None;
    }
    Some(json!({"vertices":vertices,"indices":indices,"vehicleNetId":net_id,"bodyPart":part}))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn moving_render_mesh_upload_keeps_actual_triangles_and_authority_tag() {
        let mesh = Mesh::from(Cuboid::new(2., 1., 4.));
        let pose = GlobalTransform::from(
            Transform::from_xyz(12., 3., -8.)
                .with_rotation(Quat::from_rotation_y(std::f32::consts::FRAC_PI_2)),
        );
        let row = mesh_request(&mesh, &pose, 77, "hull").unwrap();
        assert_eq!(row["vehicleNetId"], 77);
        assert_eq!(row["indices"].as_array().unwrap().len(), 36);
        let points: Vec<[f32; 3]> = serde_json::from_value(row["vertices"].clone()).unwrap();
        let min = points.iter().fold(Vec3::splat(f32::INFINITY), |a, p| {
            a.min(Vec3::from_array(*p))
        });
        let max = points.iter().fold(Vec3::splat(f32::NEG_INFINITY), |a, p| {
            a.max(Vec3::from_array(*p))
        });
        assert!(
            (min - Vec3::new(10., 2.5, -9.)).length() < 0.001
                && (max - Vec3::new(14., 3.5, -7.)).length() < 0.001
        );
    }
}
