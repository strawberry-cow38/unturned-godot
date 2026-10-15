//! A respawn changes the source look once. Position recovery and ordinary owner
//! snapshots cannot keep imposing the old angle after the player looks around.
use super::*;

pub(super) fn adopt(input: &mut PlayerInput, session: &mut Session, state: &Value) {
    let fact = &state["player"]["respawnLook"];
    let version = fact["version"].as_u64().unwrap_or(0);
    if version == session.respawn_look_version {
        return;
    }
    session.respawn_look_version = version;
    if version == 0 {
        return;
    }
    input.pitch = 0.;
    if let Some(yaw) = fact["yaw"].as_f64().filter(|v| v.is_finite()) {
        input.yaw = yaw as f32;
    }
}

#[cfg(test)]
mod tests;
