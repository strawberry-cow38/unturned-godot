use super::*;
use crate::bridge::CoreBridge;
use serde_json::{Value, json};

mod lifecycle;

fn u32_(out: &mut Vec<u8>, v: u32) {
    out.extend(v.to_le_bytes());
}
fn f32_(out: &mut Vec<u8>, v: f32) {
    out.extend(v.to_le_bytes());
}

#[test]
fn transport_reverses_winding_once_and_rejects_partial_invalid_or_trailing_data() {
    let mut bytes = Vec::new();
    for v in [0x43464755, 1, 3, 3] {
        u32_(&mut bytes, v);
    }
    for p in [[0., 0., 0.], [0., 0., -4.], [4., 0., 0.]] {
        for v in p {
            f32_(&mut bytes, v);
        }
    }
    for _ in 0..3 {
        for v in [0., 1., 0.] {
            f32_(&mut bytes, v);
        }
    }
    for _ in 0..3 {
        for v in [0., 0.] {
            f32_(&mut bytes, v);
        }
    }
    for _ in 0..3 {
        for v in [1.; 4] {
            f32_(&mut bytes, v);
        }
    }
    for v in [0, 1, 2] {
        u32_(&mut bytes, v);
    }
    let mesh = decode_chunk(&bytes).unwrap();
    let p: Vec<_> = mesh
        .positions
        .iter()
        .map(|p| Vec3::from_array(*p))
        .collect();
    assert!((p[1] - p[0]).cross(p[2] - p[0]).dot(Vec3::Y) > 0.);
    for end in [0, 7, 15, bytes.len() - 1] {
        assert!(decode_chunk(&bytes[..end]).is_err());
    }
    let mut bad = bytes.clone();
    bad.extend([0]);
    assert!(decode_chunk(&bad).is_err());
    let end = bytes.len() - 4;
    bytes[end..].copy_from_slice(&3_u32.to_le_bytes());
    assert!(decode_chunk(&bytes).is_err());
    bytes[12..16].copy_from_slice(&0_u32.to_le_bytes());
    bytes.truncate(end - 8);
    assert!(
        decode_chunk(&bytes).unwrap().positions.is_empty(),
        "fully dug source chunk has no geometry"
    );
}

struct Directory(PathBuf);
impl Drop for Directory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

#[test]
fn actual_source_generate_reopen_river_sampling_stream_bounds_and_decoder() {
    let project = Path::new(env!("CARGO_MANIFEST_DIR"));
    let temp = Directory(std::env::temp_dir().join(format!(
            "bevy-finite-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        )));
    fs::create_dir_all(&temp.0).unwrap();
    fs::copy(
        project.join("../tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"),
        temp.0.join("Roads.dat"),
    )
    .unwrap();
    let root = temp.0.join("map");
    let bridge = CoreBridge::launch(project).unwrap();
    let request = json!({"directory":root,"contentRoot":project.join("../game/content"),"environmentRoot":temp.0,"name":"Native Island","seed":1234,"tilesX":1,"tilesZ":1,"create":true});
    bridge.request("finiteMap", request).unwrap();
    let first = FiniteWorld::load(&root).unwrap();
    assert_eq!(first.surface.width, 257);
    assert_eq!(first.descriptor.seed, 1234);
    assert!(!first.players().unwrap().is_empty());
    let near = first.chunks_near(Vec3::new(32., 0., -32.), 10.);
    assert_eq!(near, vec![(0, 0)]);
    assert_eq!(
        first.chunks_near(Vec3::new(1024., 0., -1024.), 0.),
        vec![(5, 5)]
    );
    assert!(
        first
            .chunks_near(Vec3::new(2048., 0., -2048.), 10.)
            .is_empty()
    );
    let mesh = first.chunk(0, 0).unwrap();
    assert_eq!(mesh.positions.len(), 48 * 48 * 6);
    for tri in mesh.positions.chunks_exact(3) {
        let a = Vec3::from_array(tri[0]);
        let b = Vec3::from_array(tri[1]);
        let c = Vec3::from_array(tri[2]);
        assert!((b - a).cross(c - a).y > 0., "native faces must point up");
    }
    // Author a source v3 river and probe fractional points in its channel,
    // approach, untouched ground and clamped border through real player files.
    let mut river = Vec::new();
    for v in [3, 1, 3] {
        u32_(&mut river, v);
    }
    for p in [[40., 30., -40.], [44., 30., -75.], [35., 30., -130.]] {
        for v in p {
            f32_(&mut river, v);
        }
    }
    for v in [8., 4.] {
        f32_(&mut river, v);
    }
    u32_(&mut river, 5);
    fs::write(
        root.join("terrain/editor_Native Island_heightmap.bin.rivers"),
        river,
    )
    .unwrap();
    let mut players = String::new();
    for z in 0..9 {
        for x in 0..9 {
            players.push_str(&format!(
                "{} 30 {} 15 0 0\n",
                12.375 + x as f32 * 7.25,
                -40.125 - z as f32 * 8.125
            ));
        }
    }
    players.push_str("-2.25 30 4.125 0 0 0\n1025.75 30 -1027.25 0 0 0\n");
    fs::write(root.join("spawns/editor_Native Island_player.txt"), players).unwrap();
    bridge
        .request(
            "finiteMap",
            json!({"directory":root,"contentRoot":project.join("../game/content")}),
        )
        .unwrap();
    let reopened = FiniteWorld::load(&root).unwrap();
    assert_eq!(reopened.surface.rivers.len(), 1);
    assert!(reopened.chunk(0, 0).unwrap().positions.len() > mesh.positions.len());
    let samples: Vec<Value> =
        serde_json::from_slice(&fs::read(root.join(".native/players.json")).unwrap()).unwrap();
    for sample in samples {
        let p = &sample["position"];
        let x = p[0].as_f64().unwrap() as f32;
        let z = p[2].as_f64().unwrap() as f32;
        let expected = sample["ground"].as_f64().unwrap() as f32;
        assert!(
            (reopened.surface.sample(x, z) - expected).abs() < 0.0002,
            "original analytic height at {x},{z}: {} vs {expected}",
            reopened.surface.sample(x, z)
        );
    }
    let surface = fs::read(root.join(".native/surface.bin")).unwrap();
    assert!(Surface::decode(&surface[..surface.len() - 1]).is_err());
    let mut corrupt = surface;
    corrupt[16..20].copy_from_slice(&f32::NAN.to_le_bytes());
    assert!(Surface::decode(&corrupt).is_err());
    fs::write(root.join(".native/chunk_0_0.bin"), [0_u8; 8]).unwrap();
    assert!(
        reopened.chunk(0, 0).is_err(),
        "partial cache cannot become a visible/colliding chunk"
    );
}
