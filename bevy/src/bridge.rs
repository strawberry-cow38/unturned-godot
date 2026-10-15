//! Engine-independent game host. The child owns the original authoritative core;
//! Bevy owns presentation and the local collision body.
use anyhow::{Context, Result, anyhow, bail};
use bevy::prelude::Resource;
use serde_json::{Value, json};
use std::{
    io::{BufRead, BufReader, Read, Write},
    path::{Path, PathBuf},
    process::{Child, ChildStdin, Command, Stdio},
    sync::{Mutex, mpsc},
    thread,
    time::Duration,
};

const MAX_REPLY_BYTES: u64 = 32 * 1024 * 1024;

#[derive(Resource)]
pub struct CoreBridge(Mutex<BridgeProcess>);

impl CoreBridge {
    /// Acknowledge only events already delivered to the Bevy event stream/UI.
    pub fn ack_events(&self, sequence: u64) {
        if let Ok(mut process) = self.0.lock() {
            process.ack_events = process.ack_events.max(sequence);
        }
    }
    /// Only the exclusive native origin barrier may acknowledge a new frame.
    pub fn adopt_origin(&self, epoch: u64) {
        if let Ok(mut process) = self.0.lock() {
            process.origin_epoch = epoch;
        }
    }
    pub fn launch(manifest: &Path) -> Result<Self> {
        let dll = manifest.join("bridge/bin/Debug/net8.0/UnturnedBevy.Bridge.dll");
        if !dll.is_file() {
            bail!(
                "Game bridge is missing at {}. Run bevy/run.sh to build it.",
                dll.display()
            );
        }
        Self::launch_command(Command::new(dotnet_path()).arg(dll))
    }

    fn launch_command(command: &mut Command) -> Result<Self> {
        let mut child = command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .context("launching the .NET game core")?;
        let stdin = child.stdin.take().context("opening core input")?;
        let stdout = child.stdout.take().context("opening core output")?;
        let (tx, rx) = mpsc::sync_channel(4);
        thread::Builder::new()
            .name("game-core-replies".into())
            .spawn(move || {
                let mut reader = BufReader::new(stdout);
                loop {
                    let reply = read_reply(&mut reader);
                    let failed = reply.is_err();
                    if tx.send(reply).is_err() || failed {
                        break;
                    }
                }
            })?;
        Ok(Self(Mutex::new(BridgeProcess {
            child,
            stdin,
            replies: rx,
            next_id: 1,
            failed: false,
            ack_events: 0,
            origin_epoch: 0,
        })))
    }

    pub fn request(&self, operation: &str, fields: Value) -> Result<Value> {
        self.0
            .lock()
            .map_err(|_| anyhow!("core transport mutex poisoned"))?
            .request(operation, fields)
    }
}

struct BridgeProcess {
    child: Child,
    stdin: ChildStdin,
    replies: mpsc::Receiver<Result<Value>>,
    next_id: u64,
    failed: bool,
    ack_events: u64,
    origin_epoch: u64,
}

impl BridgeProcess {
    fn request(&mut self, operation: &str, fields: Value) -> Result<Value> {
        if self.failed {
            bail!("core transport is closed after an earlier failure");
        }
        let mut request = fields.as_object().cloned().unwrap_or_default();
        let id = self.next_id;
        self.next_id += 1;
        request.insert("id".into(), json!(id));
        request.insert("op".into(), json!(operation));
        if operation == "init" {
            self.ack_events = 0;
            self.origin_epoch = 0;
        }
        request.insert("ackEvents".into(), json!(self.ack_events));
        if matches!(
            operation,
            "tick"
                | "action"
                | "worldGeometry"
                | "worldDynamicGeometry"
                | "worldCosmeticGeometry"
                | "worldVehicleState"
                | "worldZombieState"
                | "worldZombieNoise"
                | "worldVehicleDamage"
                | "worldServices"
                | "worldLoot"
                | "worldVehicles"
        ) {
            request
                .entry("originEpoch")
                .or_insert_with(|| json!(self.origin_epoch));
        }
        let result = (|| {
            serde_json::to_writer(&mut self.stdin, &request)?;
            self.stdin.write_all(b"\n")?;
            self.stdin.flush()?;
            let response = self
                .replies
                .recv_timeout(Duration::from_secs(60))
                .context("waiting for game core reply")??;
            if response.get("id").and_then(Value::as_u64) != Some(id) {
                bail!(
                    "core response id mismatch: expected {id}, received {:?}",
                    response.get("id")
                );
            }
            Ok(response)
        })();
        let response: Value = match result {
            Ok(value) => value,
            Err(error) => {
                self.failed = true;
                let _ = self.child.kill();
                return Err(error);
            }
        };
        if response.get("ok").and_then(Value::as_bool) != Some(true) {
            bail!(
                "{}",
                response
                    .get("error")
                    .and_then(Value::as_str)
                    .unwrap_or("game core rejected operation")
            );
        }
        Ok(response)
    }
}

impl Drop for BridgeProcess {
    fn drop(&mut self) {
        // Always reap the owned child, including panic/failed-handshake paths.
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn read_reply(reader: &mut impl BufRead) -> Result<Value> {
    let mut bytes = Vec::new();
    let read = reader
        .take(MAX_REPLY_BYTES + 1)
        .read_until(b'\n', &mut bytes)?;
    if read == 0 {
        bail!("game core closed its output");
    }
    if read as u64 > MAX_REPLY_BYTES {
        bail!("game core response exceeds 32 MiB");
    }
    if bytes.last() != Some(&b'\n') {
        bail!("game core sent an incomplete response");
    }
    serde_json::from_slice(&bytes).context("decoding game core JSON response")
}

fn dotnet_path() -> PathBuf {
    if let Some(path) = std::env::var_os("DOTNET") {
        return path.into();
    }
    if let Some(home) = std::env::var_os("HOME") {
        let local = PathBuf::from(home).join(".dotnet/dotnet");
        if local.is_file() {
            return local;
        }
    }
    "dotnet".into()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn zombie_host_publications_reject_a_stale_origin_until_native_adoption() {
        let manifest = Path::new(env!("CARGO_MANIFEST_DIR"));
        let bridge = CoreBridge::launch(manifest).expect("build original core with bevy/run.sh");
        bridge
            .request(
                "init",
                json!({"contentRoot":manifest.join("../game/content"),
            "mapId":"Procedural","spawn":[1025.,40.,12.],"origin":[0.,0.]}),
            )
            .unwrap();
        let shifted = bridge
            .request("rebase", json!({"shift":[1024.,0.,0.],"expectedEpoch":0}))
            .unwrap();
        let epoch = shifted["state"]["origin"]["epoch"].as_u64().unwrap();
        for (operation, fields) in [
            ("worldZombieState", json!({"bodies":[]})),
            (
                "worldZombieNoise",
                json!({"position":[1.,40.,12.],"radius":32.}),
            ),
        ] {
            let error = bridge.request(operation, fields.clone()).unwrap_err();
            assert!(
                error.to_string().contains("Origin epoch mismatch"),
                "{operation}: {error}"
            );
            bridge.adopt_origin(epoch);
            bridge.request(operation, fields).unwrap();
            bridge.adopt_origin(0);
        }
    }

    #[test]
    fn consecutive_replies_remain_individually_framed() {
        let mut input = Cursor::new(b"{\"id\":1,\"ok\":true}\n{\"id\":2,\"ok\":true}\n");
        assert_eq!(read_reply(&mut input).unwrap()["id"], 1);
        assert_eq!(read_reply(&mut input).unwrap()["id"], 2);
        assert!(read_reply(&mut input).is_err());
    }

    #[test]
    fn truncated_and_non_json_output_fail_closed() {
        for bytes in [b"{\"ok\":true}".as_slice(), b"debug noise\n", b""] {
            assert!(read_reply(&mut Cursor::new(bytes)).is_err());
        }
    }
}
