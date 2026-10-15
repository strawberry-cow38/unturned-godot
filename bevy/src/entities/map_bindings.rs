//! Remote authored interactions bind to accepted registry IDs. The same manifest
//! parser as the host supplies source ordering; this path never registers a host.
use super::*;
use crate::{player::WorldSessionEpoch, world::WorldState};
use std::hash::{Hash, Hasher};

#[derive(Resource, Default)]
pub(super) struct Bindings {
    key: Option<(u64, String, String)>,
    signature: Option<u64>,
    revision: u64,
    next_check: f64,
    last_error: Option<String>,
}

pub(super) fn adopt(
    bridge: Res<CoreBridge>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    time: Res<Time>,
    mut config: ResMut<WorldConfig>,
    world: Res<WorldState>,
    geometry: Res<WorldGeometry>,
    mut model: ResMut<UiModel>,
    mut services: ResMut<ServiceManifest>,
    mut bindings: ResMut<Bindings>,
) {
    if !model.connected || snapshot.0.is_null() {
        *bindings = default();
        if !services.map.is_empty() {
            services.clear();
        }
        return;
    }
    // An accepted handshake (or restored host save) owns this decision. Keep
    // the existing streaming frame and player pose while rebuilding its props.
    if let Some(holiday) = snapshot.0["network"]["holiday"].as_str() {
        if config.holiday != holiday {
            config.holiday = holiday.into();
            *bindings = default();
            return;
        }
    }
    if model.network.owns_world
        || !world.ready
        || config.procedural_path.is_some()
        || geometry
            .holiday
            .as_deref()
            .is_some_and(|h| h != config.holiday)
        || model
            .network
            .wire_gaps
            .iter()
            .any(|gap| gap == "mapServiceBindings")
    {
        return;
    }
    let Some(root) = config.map_root.as_ref() else {
        return;
    };
    let key = (epoch.0, config.map_name.clone(), config.holiday.clone());
    if bindings.key.as_ref() != Some(&key) {
        *bindings = Bindings {
            key: Some(key),
            ..default()
        };
    }
    if time.elapsed_secs_f64() < bindings.next_check {
        return;
    }
    // Replica membership/identity is cheap to inspect. Door animation, fuel and
    // other changing values do not require re-parsing thousands of placements.
    bindings.next_check = time.elapsed_secs_f64() + 0.25;
    let signature = registry_signature(&snapshot.0);
    if bindings.signature == Some(signature) {
        return;
    }
    let reply = bridge.request(
        "mapBindings",
        json!({
            "mapRoot":root,"mapName":config.map_name,"contentRoot":config.content_root
        }),
    );
    match reply {
        Ok(reply) => {
            let result = &reply["result"];
            let revision = result["revision"].as_u64().unwrap_or(0);
            // A zero/absent revision is not a valid binding response. Do not
            // erase an adopted manifest when an incompatible host is selected.
            if result["readOnly"] != true || revision == 0 {
                return;
            }
            if bindings.revision != revision || services.map != config.map_name {
                services.adopt(&config.map_name, &config.holiday, result);
                bindings.revision = revision;
                info!(
                    "Remote map bindings revision {revision}: {} unbound containers, {} unbound fixtures",
                    result["unboundContainers"], result["unboundFixtures"]
                );
            }
            bindings.signature = Some(signature);
            bindings.last_error = None;
        }
        Err(error) => {
            let text = format!("Remote map bindings: {error}");
            if bindings.last_error.as_ref() != Some(&text) {
                warn!("{text}");
                model.notice(text.clone());
                bindings.last_error = Some(text);
            }
        }
    }
}

fn registry_signature(snapshot: &Value) -> u64 {
    let mut hash = std::collections::hash_map::DefaultHasher::new();
    for (registry, kind) in [("containers", "kindId"), ("deployables", "defId")] {
        registry.hash(&mut hash);
        for row in snapshot[registry].as_array().into_iter().flatten() {
            for field in ["netId", kind, "position", "yaw"] {
                row[field].to_string().hash(&mut hash);
            }
        }
    }
    hash.finish()
}

#[cfg(test)]
mod tests;
