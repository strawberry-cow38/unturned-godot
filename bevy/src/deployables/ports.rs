//! Source ConnectionPort geometry and metadata. Ports are a separate query layer;
//! they never become world/vehicle obstacles or move into the mesh's Euler frame.
use super::*;
use crate::{player::PlayerCamera, ui::UiState};

mod art;
#[cfg(test)]
mod tests;
mod tool;

pub(super) const LAYER: Group = Group::GROUP_18;
#[derive(Clone, Copy, Debug, Hash, PartialEq, Eq)]
struct Address {
    device: u32,
    index: u8,
}
#[derive(Component)]
struct Socket {
    address: Address,
    body: Entity,
    definition: wires::Port,
    name: String,
    has_health: bool,
    local: Vec3,
    arrow: Entity,
    quad: Entity,
    cube_material: Handle<StandardMaterial>,
    arrow_material: Handle<StandardMaterial>,
}
#[derive(Clone)]
struct Endpoint {
    address: Address,
    position: Vec3,
    definition: wires::Port,
    name: String,
    usable: bool,
    wire: Option<u32>,
    live: f32,
    powered: bool,
    draw: f32,
}
#[derive(Clone, Copy, PartialEq, Eq, Default)]
enum Highlight {
    #[default]
    None,
    Focus,
    Good,
    Bad,
}
#[derive(Resource, Default)]
struct Presentation {
    roots: HashSet<Entity>,
    art: Option<art::Art>,
}
#[derive(Resource, Default)]
struct Frame {
    endpoints: HashMap<Address, Endpoint>,
    hovered: Option<Address>,
    shown: bool,
    active: bool,
    camera: Option<Transform>,
    free_end: Vec3,
}

pub(super) fn install(app: &mut App) {
    app.init_resource::<Presentation>()
        .init_resource::<Frame>()
        .init_resource::<tool::Gesture>()
        .add_systems(
            Update,
            (sync, gather, tool::input, tool::preview, paint)
                .chain()
                .after(wires::refresh)
                .after(super::sync)
                .after(crate::player::PlayerCameraUpdate)
                .before(crate::entities::ApplyInteractions),
        );
    tool::install_hud(app);
}

fn held(snapshot: &Value, catalog: &ItemCatalog) -> bool {
    crate::use_items::Cell::held(snapshot)
        .and_then(|cell| catalog.items.get(&cell.id))
        .is_some_and(|item| item.tool["kind"] == "Wire")
}
/// ConnectionPort.Usable: a burning or destroyed owner retires its ports. A world
/// fixture has no health (Health 0 by definition) and only burning retires it.
fn dead(snapshot: &Value, id: u32, has_health: bool) -> bool {
    snapshot["deployables"]
        .as_array()
        .into_iter()
        .flatten()
        .find(|r| r["netId"] == id)
        .is_none_or(|r| {
            r["onFire"] == true || (has_health && r["health"].as_f64().unwrap_or(0.) <= 0.)
        })
}

fn sync(
    mut commands: Commands,
    deployed: Query<(Entity, &Deployed)>,
    catalog: Res<ItemCatalog>,
    mut presentation: ResMut<Presentation>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
) {
    let mut live = HashSet::new();
    for (body, deployed) in &deployed {
        live.insert(body);
        if !presentation.roots.insert(body) {
            continue;
        }
        let Some(def) = catalog.deployables.iter().find(|d| d.id == deployed.def_id) else {
            continue;
        };
        if def.ports.is_empty() {
            continue;
        }
        let art = presentation
            .art
            .get_or_insert_with(|| art::Art::load(&mut meshes, &mut images));
        for (index, p) in def.ports.iter().enumerate() {
            let Ok(index) = u8::try_from(index) else {
                break;
            };
            let local = p.position(def.id);
            let cube_material = materials.add(art::cube(p, false, false, Highlight::None));
            let arrow_material = materials.add(art::arrow(p, false));
            let socket = commands
                .spawn((
                    Name::new(format!("{} socket{index}", def.name)),
                    Transform::from_translation(local),
                    Visibility::Hidden,
                    ChildOf(body),
                    Mesh3d(art.cube.clone()),
                    MeshMaterial3d(cube_material.clone()),
                    Collider::cuboid(0.065, 0.065, 0.065),
                    // Source PortLayer bit8 with CollisionMask 0: a sensor on
                    // its own layer, outside contacts and the ordinary look ray.
                    Sensor,
                    CollisionGroups::new(Group::NONE, Group::NONE),
                ))
                .id();
            let arrow_pose = art::arrow_pose(p, local);
            let arrow = commands
                .spawn((arrow_pose, Visibility::Hidden, ChildOf(socket)))
                .id();
            let quad = commands
                .spawn((
                    Mesh3d(art.quad.clone()),
                    MeshMaterial3d(arrow_material.clone()),
                    Transform::default(),
                    Visibility::Inherited,
                    ChildOf(arrow),
                    bevy::light::NotShadowCaster,
                ))
                .id();
            materials
                .get_mut(&arrow_material)
                .unwrap()
                .base_color_texture = Some(art.texture.clone());
            commands.entity(socket).insert(Socket {
                address: Address {
                    device: deployed.net_id,
                    index,
                },
                body,
                definition: p.clone(),
                name: def.name.clone(),
                has_health: def.health > 0.,
                local,
                arrow,
                quad,
                cube_material,
                arrow_material,
            });
        }
    }
    presentation.roots.retain(|entity| live.contains(entity));
}

fn gather(
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    model: Res<UiModel>,
    ui: Option<Res<UiState>>,
    cameras: Query<&Transform, With<PlayerCamera>>,
    windows: Query<(&Window, &bevy::window::CursorOptions), With<bevy::window::PrimaryWindow>>,
    sockets: Query<(Entity, &Socket)>,
    bodies: Query<&Transform, With<Deployed>>,
    player: Query<Entity, With<crate::player::PlayerBody>>,
    parents: Query<&ChildOf>,
    context: ReadRapierContext,
    accepted: Res<wires::Accepted>,
    mut frame: ResMut<Frame>,
) {
    *frame = Frame::default();
    frame.shown = model.connected && held(&snapshot.0, &catalog);
    frame.camera = cameras.single().ok().copied();
    frame.active = frame.shown
        && ui.is_some_and(|u| !u.captures_input())
        && snapshot.0["player"]["alive"] == true
        && snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) == 0
        && windows.single().is_ok_and(|(w, c)| {
            w.focused && !c.visible && c.grab_mode != bevy::window::CursorGrabMode::None
        });
    for (_, socket) in &sockets {
        let Ok(body) = bodies.get(socket.body) else {
            continue;
        };
        let mut endpoint = Endpoint {
            address: socket.address,
            position: body.transform_point(socket.local),
            definition: socket.definition.clone(),
            name: socket.name.clone(),
            usable: !dead(&snapshot.0, socket.address.device, socket.has_health),
            wire: None,
            live: 0.,
            powered: false,
            draw: 0.,
        };
        if let Some(graph) = &accepted.graph {
            endpoint.wire = graph
                .wires
                .iter()
                .find(|w| {
                    (w.source_id == socket.address.device
                        && w.source_port == socket.address.index as usize)
                        || (w.consumer_id == socket.address.device
                            && w.consumer_port == socket.address.index as usize)
                })
                .map(|w| w.net_id);
            if let Some(result) = graph
                .devices
                .iter()
                .find(|d| d.net_id == socket.address.device)
                .and_then(|d| {
                    d.ports
                        .iter()
                        .find(|p| p.index == socket.address.index as usize)
                })
            {
                endpoint.live = result.live;
                endpoint.powered = result.powered;
                endpoint.draw = result.draw;
            }
        }
        frame.endpoints.insert(socket.address, endpoint);
    }
    if !frame.shown {
        return;
    }
    let (Some(camera), Ok(context)) = (frame.camera, context.single()) else {
        return;
    };
    let owner = player.single().ok();
    let place = |entity, collider: &bevy_rapier3d::rapier::geometry::Collider| {
        crate::player::world_or_prop(entity, collider)
            && !std::iter::once(entity)
                .chain(parents.iter_ancestors(entity))
                .any(|e| Some(e) == owner || bodies.get(e).is_ok())
    };
    frame.free_end = camera.translation + camera.forward() * 6.;
    if let Some((_, distance)) = context.cast_ray(
        camera.translation,
        *camera.forward(),
        6.,
        true,
        QueryFilter::default()
            .exclude_sensors()
            .groups(CollisionGroups::new(
                Group::ALL,
                Group::GROUP_1 | Group::GROUP_4,
            ))
            .predicate(&place),
    ) {
        frame.free_end = camera.translation + camera.forward() * distance;
    }
    if !frame.active {
        return;
    }
    let alive = |entity, _: &bevy_rapier3d::rapier::geometry::Collider| {
        sockets
            .get(entity)
            .is_ok_and(|(_, p)| frame.endpoints.get(&p.address).is_some_and(|p| p.usable))
    };
    if let Some((hit, _)) = context.cast_ray(
        camera.translation,
        *camera.forward(),
        5.5,
        true,
        QueryFilter::default()
            .groups(CollisionGroups::new(Group::ALL, LAYER))
            .predicate(&alive),
    ) {
        frame.hovered = sockets.get(hit).ok().map(|(_, p)| p.address);
    }
}

fn paint(
    frame: Res<Frame>,
    gesture: Res<tool::Gesture>,
    sockets: Query<(Entity, &Socket, &Visibility, &CollisionGroups)>,
    arrows: Query<&Visibility, Without<Socket>>,
    bodies: Query<&Transform, With<Deployed>>,
    mut commands: Commands,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut quad_poses: Query<&mut Transform, Without<Deployed>>,
) {
    for (entity, socket, visibility, groups) in &sockets {
        let Some(endpoint) = frame.endpoints.get(&socket.address) else {
            continue;
        };
        let shown = if frame.shown && endpoint.usable {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        if *visibility != shown {
            commands.entity(entity).insert(shown);
        }
        let arrow = if frame.active && endpoint.usable {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        if arrows.get(socket.arrow).is_ok_and(|v| *v != arrow) {
            commands.entity(socket.arrow).insert(arrow);
        }
        // A retired socket must leave the port-only ray even when held again.
        let layer = if endpoint.usable { LAYER } else { Group::NONE };
        if groups.memberships != layer {
            commands.entity(entity).insert(CollisionGroups::new(layer, layer));
        }
        let highlight = if frame.hovered == Some(endpoint.address) {
            match gesture.start {
                None => Highlight::Focus,
                Some(start) if start == endpoint.address => Highlight::Focus,
                Some(start) => {
                    if tool::complete(frame.endpoints.get(&start), Some(endpoint)) {
                        Highlight::Good
                    } else {
                        Highlight::Bad
                    }
                }
            }
        } else {
            Highlight::None
        };
        let cube = art::cube(
            &endpoint.definition,
            endpoint.wire.is_some(),
            false,
            highlight,
        );
        if materials
            .get(&socket.cube_material)
            .is_some_and(|m| m.base_color != cube.base_color || m.emissive != cube.emissive)
        {
            let mut m = materials.get_mut(&socket.cube_material).unwrap();
            m.base_color = cube.base_color;
            m.emissive = cube.emissive;
        }
        let mut arrow = art::arrow(&endpoint.definition, false);
        let c = cube.base_color.to_srgba();
        arrow.base_color = Color::srgba(c.red, c.green, c.blue, 0.95);
        if materials
            .get(&socket.arrow_material)
            .is_some_and(|m| m.base_color != arrow.base_color)
        {
            materials
                .get_mut(&socket.arrow_material)
                .unwrap()
                .base_color = arrow.base_color;
        }
        if frame.active
            && let (Some(camera), Ok(body), Ok(mut quad)) = (
                frame.camera,
                bodies.get(socket.body),
                quad_poses.get_mut(socket.quad),
            )
        {
            let arrow = body.compute_affine()
                * Transform::from_translation(socket.local).compute_affine()
                * art::arrow_pose(&socket.definition, socket.local).compute_affine();
            let camera = arrow.inverse().transform_point3(camera.translation);
            art::face_camera(&mut quad, camera);
        }
    }
}
