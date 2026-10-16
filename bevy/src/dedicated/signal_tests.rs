//! The real signal path: the built `unturned-bevy --dedicated` binary, SIGTERM
//! to its process group (what systemd sends), and the final save on disk.
//! Opt-in (`--ignored`): it needs the built binary and the retail map.
//!
//! ```text
//! cargo build --bin unturned-bevy
//! UG_UNTURNED_DIR=... cargo test --bin unturned-bevy sigterm -- --ignored
//! ```
use std::{
    io::Read,
    os::unix::process::CommandExt,
    path::Path,
    process::{Command, Stdio},
    time::{Duration, Instant},
};

#[test]
#[ignore = "spawns the built binary against the retail map (UG_UNTURNED_DIR)"]
fn sigterm_to_the_process_group_writes_the_final_save_and_exits_cleanly() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let binary = root.join("target/debug/unturned-bevy");
    assert!(binary.is_file(), "build the binary first: {}", binary.display());
    let retail = std::env::var_os("UG_UNTURNED_DIR").expect("UG_UNTURNED_DIR names the retail install");
    let dir = std::env::temp_dir().join(format!("ug-dedicated-signal-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let log = dir.join("server.log");
    let port = std::net::UdpSocket::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
    let mut child = Command::new(&binary)
        .args(["--dedicated", "--port", &port.to_string()])
        .current_dir(root.parent().unwrap())
        .env("UG_UNTURNED_DIR", retail)
        .env("UG_BEVY_SAVES", &dir)
        .env("UG_BEVY_SETTINGS", dir.join("settings.json"))
        .env("UG_DEDICATED_RADIUS", "150")
        .env_remove("DISPLAY")
        .env_remove("WAYLAND_DISPLAY")
        .stdout(std::fs::File::create(&log).unwrap())
        .stderr(Stdio::from(std::fs::File::options().append(true).open(&log).unwrap()))
        .stdin(Stdio::null())
        // Its own group, like the systemd unit's control group: the signal
        // reaches the native host and the bridge child together.
        .process_group(0)
        .spawn()
        .unwrap();
    let text = |log: &Path| {
        let mut s = String::new();
        let _ = std::fs::File::open(log).and_then(|mut f| f.read_to_string(&mut s));
        s
    };
    let started = Instant::now();
    while !text(&log).contains("serving on UDP") {
        assert!(child.try_wait().unwrap().is_none(), "server exited early:\n{}", text(&log));
        assert!(started.elapsed() < Duration::from_secs(300), "server never served:\n{}", text(&log));
        std::thread::sleep(Duration::from_millis(250));
    }
    let group = format!("-{}", child.id());
    assert!(Command::new("kill").args(["-TERM", "--", &group]).status().unwrap().success());
    let stopping = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        assert!(stopping.elapsed() < Duration::from_secs(30), "SIGTERM did not stop the server (systemd would SIGKILL)");
        std::thread::sleep(Duration::from_millis(100));
    };
    let output = text(&log);
    assert!(status.success(), "a signal stop exits 0 ({status}):\n{output}");
    assert!(output.contains("[SAVE] shutdown save written"), "{output}");
    assert!(dir.join("pei.json").is_file(), "the final save is on disk");
    let _ = std::fs::remove_dir_all(&dir);
}
