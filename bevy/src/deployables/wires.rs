//! Accepted Wire replicas: the unified source game retains two endpoints, not
//! the tool's temporary routing nodes. Ports belong to the body frame, outside
//! its optional mesh correction. Unsupported fixture bodies remain explicit.
use super::*;

pub(super) mod segment;
#[cfg(test)]
mod tests;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Port {
    pub(super) kind: String,
    pub(super) pos: SourceVector,
    #[serde(default)]
    pub(super) role: String,
    #[serde(default)]
    pub(super) watts: f32,
}
impl Port {
    pub(super) fn position(&self, def_id: u16) -> Vec3 {
        let p = Vec3::from(self.pos);
        match (def_id, self.kind.as_str(), self.role.as_str()) {
            (459, "Consumer", _) => geometry::env_vector("UG_SPC", p),
            (459, _, _) => geometry::env_vector("UG_SPP", p),
            (458, _, "TurnOn") => geometry::env_vector("UG_GTON", p),
            (458, _, "TurnOff") => geometry::env_vector("UG_GTOFF", p),
            _ => p,
        }
    }
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Wire {
    pub(super) net_id: u32,
    pub(super) source_id: u32,
    pub(super) source_port: usize,
    pub(super) consumer_id: u32,
    pub(super) consumer_port: usize,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct SolvedPort {
    pub(super) index: usize,
    pub(super) live: f32,
    pub(super) powered: bool,
    pub(super) draw: f32,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Device {
    pub(super) net_id: u32,
    pub(super) ports: Vec<SolvedPort>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Graph {
    version: u32,
    read_only: bool,
    state_hash: u64,
    pub(super) wires: Vec<Wire>,
    pub(super) devices: Vec<Device>,
}
#[derive(Resource, Default)]
pub(super) struct Accepted {
    epoch: u64,
    hash: Option<u64>,
    pub(super) graph: Option<Graph>,
    error: Option<String>,
}
#[derive(Component)]
struct WireSegment(u32);
#[derive(Resource, Default)]
pub(super) struct Visuals {
    epoch: u64,
    instances: HashMap<u32, Entity>,
    art: Option<(Handle<Mesh>, Handle<StandardMaterial>)>,
    bad: Option<Handle<StandardMaterial>>,
}
impl Visuals {
    pub(super) fn handles(
        &mut self,
        valid: bool,
        meshes: &mut Assets<Mesh>,
        materials: &mut Assets<StandardMaterial>,
    ) -> (Handle<Mesh>, Handle<StandardMaterial>) {
        let (mesh, material) = self.art.get_or_insert_with(|| {
            (
                meshes.add(Cylinder::new(0.018, 1.).mesh().resolution(6)),
                materials.add(StandardMaterial {
                    base_color: Color::srgb(0.06, 0.06, 0.07),
                    metallic: 0.2,
                    perceptual_roughness: 0.6,
                    ..default()
                }),
            )
        });
        if valid {
            return (mesh.clone(), material.clone());
        }
        let material = self.bad.get_or_insert_with(|| {
            materials.add(StandardMaterial {
                base_color: Color::srgb(0.90, 0.20, 0.15),
                metallic: 0.2,
                perceptual_roughness: 0.6,
                ..default()
            })
        });
        (mesh.clone(), material.clone())
    }
}

pub(super) fn install(app: &mut App) {
    app.init_resource::<Accepted>()
        .init_resource::<Visuals>()
        .add_systems(Update, (refresh, draw).chain().after(super::sync));
}

pub(super) fn refresh(
    bridge: Res<CoreBridge>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    model: Res<UiModel>,
    mut accepted: ResMut<Accepted>,
) {
    let hash = snapshot.0["powerStateHash"].as_u64();
    if accepted.epoch != epoch.0 || !model.connected {
        *accepted = Accepted {
            epoch: epoch.0,
            ..default()
        };
    }
    let Some(hash) = hash.filter(|_| model.connected) else {
        return;
    };
    if accepted.hash == Some(hash) {
        return;
    }
    let result = (|| -> anyhow::Result<Graph> {
        let response = bridge.request("powerView", json!({}))?;
        let graph: Graph = serde_json::from_value(response["result"].clone())?;
        anyhow::ensure!(
            graph.version == 1 && graph.read_only,
            "unsupported power view"
        );
        // A view is not allowed to silently use a later or earlier client graph.
        anyhow::ensure!(
            graph.state_hash == hash,
            "power view does not match accepted snapshot"
        );
        Ok(graph)
    })();
    match result {
        Ok(graph) => {
            accepted.graph = Some(graph);
            accepted.hash = Some(hash);
            accepted.error = None;
        }
        Err(error) => {
            accepted.graph = None;
            accepted.hash = None;
            let error = format!("Cannot read accepted wires: {error}");
            if accepted.error.as_ref() != Some(&error) {
                warn!("{error}");
            }
            accepted.error = Some(error);
        }
    }
}

fn draw(
    mut commands: Commands,
    accepted: Res<Accepted>,
    epoch: Res<WorldSessionEpoch>,
    catalog: Res<ItemCatalog>,
    deployed: Query<(&Deployed, &Transform)>,
    mut visuals: ResMut<Visuals>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    if visuals.epoch != epoch.0 {
        for (_, entity) in visuals.instances.drain() {
            commands.entity(entity).despawn();
        }
        visuals.epoch = epoch.0;
    }
    let bodies: HashMap<_, _> = deployed
        .iter()
        .map(|(d, t)| (d.net_id, (d.def_id, t)))
        .collect();
    let point = |id: u32, port: usize| -> Option<Vec3> {
        let (def_id, transform) = *bodies.get(&id)?;
        let def = catalog.deployables.iter().find(|d| d.id == def_id)?;
        let p = transform.transform_point(def.ports.get(port)?.position(def_id));
        p.is_finite().then_some(p)
    };
    let mut live = HashSet::new();
    for wire in accepted.graph.iter().flat_map(|g| &g.wires) {
        let (Some(a), Some(b)) = (
            point(wire.source_id, wire.source_port),
            point(wire.consumer_id, wire.consumer_port),
        ) else {
            continue;
        };
        if wire.net_id == 0 || !live.insert(wire.net_id) {
            continue;
        }
        let pose = segment::pose(a, b);
        let visibility = if pose.is_some() {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        if let Some(entity) = visuals.instances.get(&wire.net_id) {
            commands
                .entity(*entity)
                .insert((pose.unwrap_or_default(), visibility));
        } else {
            let (mesh, material) = visuals.handles(true, &mut meshes, &mut materials);
            let entity = commands
                .spawn((
                    Name::new(format!("Accepted wire #{}", wire.net_id)),
                    WireSegment(wire.net_id),
                    Mesh3d(mesh),
                    MeshMaterial3d(material),
                    pose.unwrap_or_default(),
                    visibility,
                ))
                .id();
            visuals.instances.insert(wire.net_id, entity);
        }
    }
    let gone: Vec<_> = visuals
        .instances
        .keys()
        .filter(|id| !live.contains(*id))
        .copied()
        .collect();
    for id in gone {
        commands
            .entity(visuals.instances.remove(&id).unwrap())
            .despawn();
    }
}
