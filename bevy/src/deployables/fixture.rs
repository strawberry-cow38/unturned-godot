//! Server-placed world fixtures as power endpoints (`GridPowerSource.Materialize`,
//! `GasPump.Materialize`). The map already draws and collides the breaker box or
//! pump; the replica hangs its source port and look-at box off the accepted frame.
use super::*;
use crate::info_billboard::{Bar, Content};

/// GridPowerSource.GridColor / GasPump.PumpColor.
const GRID: Color = Color::srgb(0.40, 0.85, 1.);
const PUMP: Color = Color::srgb(0.95, 0.75, 0.30);

/// Is this def a fixture whose replica is a port-bearing endpoint?
pub(super) fn endpoint(def: &Definition) -> bool {
    matches!(def.fixture.as_str(), "GridSource" | "GasPump")
}
/// Both Materialize calls: yaw, then a -90 degree X stand-up of the flat-authored
/// prop, at the accepted point. Their `PortLocal` is authored in that frame.
pub(super) fn transform(pose: Pose) -> Transform {
    Transform::from_translation(pose.point).with_rotation(
        Quat::from_rotation_y(pose.yaw.to_radians())
            * Quat::from_rotation_x((-90_f32).to_radians()),
    )
}
/// `AddInteractionCollider`: a world-axis box (size, centre height) wrapping the
/// standing prop, slightly oversized so it wins the coincident map collider.
pub(super) fn hit_box(def: &Definition) -> Option<(Vec3, f32)> {
    match def.fixture.as_str() {
        "GasPump" => Some((Vec3::new(1.1, 2.6, 1.1), 1.2)),
        "GridSource" => Some((Vec3::new(1.3, 2.1, 1.3), 0.95)),
        _ => None,
    }
}
/// The map object owns the mesh and solid collision. The replica adds only the
/// source's look-layer box: solid to the player and look rays (layer 1<<6 in the
/// walking mask), never to vehicles, zombies or the authority geometry.
pub(super) fn spawn(
    commands: &mut Commands,
    def: &Definition,
    id: u32,
    transform: Transform,
) -> Entity {
    let root = commands
        .spawn((
            Name::new(format!("{} #{id}", def.name)),
            Deployed {
                net_id: id,
                def_id: def.id,
            },
            transform,
            Visibility::Inherited,
        ))
        .id();
    if let Some((size, lift)) = hit_box(def) {
        // TopLevel in the source: undo the stand-up basis so the box stays world-axis.
        let undo = transform.rotation.inverse();
        commands.spawn((
            Name::new(format!("{} #{id} look box", def.name)),
            ChildOf(root),
            Transform::from_translation(undo * (Vec3::Y * lift)).with_rotation(undo),
            Collider::cuboid(size.x * 0.5, size.y * 0.5, size.z * 0.5),
            CollisionGroups::new(Group::GROUP_1, Group::GROUP_2),
        ));
    }
    root
}

/// Source billboard height above the fixture origin (`HubProcess`).
pub(super) fn info_lift(def: &Definition) -> f32 {
    if def.fixture == "GasPump" { 2.6 } else { 2.4 }
}

/// `GasPump.IsPowered`: the mains (any accepted GridSource on) or its own wired
/// input. The server's extract gate reads only the wire (`IsPumpPowered`); the
/// original prompt shows this union, so a mains-only pump offers a fill it refuses.
pub(super) fn pump_powered(
    snapshot: &Value,
    catalog: &ItemCatalog,
    accepted: &wires::Accepted,
    id: u32,
) -> bool {
    let mains = snapshot["deployables"]
        .as_array()
        .into_iter()
        .flatten()
        .any(|r| {
            r["toggledOn"] == true
                && r["defId"].as_u64().is_some_and(|d| {
                    catalog
                        .deployables
                        .iter()
                        .any(|def| u64::from(def.id) == d && def.fixture == "GridSource")
                })
        });
    mains || accepted.powered(id, 0)
}

/// `GridPowerSource.HubProcess` / `GasPump.HubProcess` replica panels.
pub(super) fn readout(
    def: &Definition,
    row: &Value,
    accepted: &wires::Accepted,
    powered: bool,
) -> Content {
    let id = row["netId"]
        .as_u64()
        .and_then(|id| u32::try_from(id).ok())
        .unwrap_or(0);
    let (name, color, bar, prompt) = if def.fixture == "GasPump" {
        // entity.Fuel is the station's 0..100 fill percent; litres stay server-side.
        let percent = row["fuel"].as_f64().unwrap_or(0.) as f32;
        let state = if percent <= 0.1 {
            "empty"
        } else if powered {
            "[RMB] with a gas can to fill"
        } else {
            "no power"
        };
        let prompt = format!("{}% station fuel · {state}", percent.round() as i64);
        ("Gas Pump", PUMP, (percent / 100.).clamp(0., 1.), prompt)
    } else {
        let watts = def.ports.first().map_or(0., |p| p.watts);
        let draw = accepted.port(id, 0).map_or(0., |p| p.draw);
        let prompt = if row["toggledOn"] == true {
            format!("{} / {}W", draw.round() as i64, watts.round() as i64)
        } else {
            "mains OFF".into()
        };
        let load = if watts > 0. {
            (draw / watts).clamp(0., 1.)
        } else {
            0.
        };
        ("Grid Power", GRID, load, prompt)
    };
    Content {
        name: name.into(),
        name_color: color,
        prompt,
        prompt_color: color,
        bars: [Some(Bar { value: bar, color }), None, None, None, None],
    }
}
