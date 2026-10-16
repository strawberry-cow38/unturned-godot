//! Deployable.BuildTurbine: the procedural upright tower, nacelle and three-blade
//! hub. The hub spins with the server-accepted wind factor, never a local sample.
use super::*;
use geometry::Bounds;

/// Source tower height `H`.
const HEIGHT: f32 = 2.6;

pub(super) struct Art {
    nacelle: Handle<Mesh>,
    cap: Handle<Mesh>,
    blade: Handle<Mesh>,
    gray: Handle<StandardMaterial>,
    white: Handle<StandardMaterial>,
}
/// The source `BladeHub` node of one accepted turbine.
#[derive(Component)]
pub(super) struct Hub {
    pub net_id: u32,
}

/// Tower mesh, its gray material, the source's hand-written local AABB and the
/// remaining parts. The AABB (not the meshes) seats and collides the body.
pub(super) fn load(
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
) -> (Handle<Mesh>, Handle<StandardMaterial>, Bounds, Art) {
    let gray = materials.add(StandardMaterial {
        base_color: Color::srgb(0.80, 0.81, 0.83),
        metallic: 0.1,
        perceptual_roughness: 0.55,
        ..default()
    });
    let white = materials.add(StandardMaterial {
        base_color: Color::srgb(0.94, 0.94, 0.96),
        perceptual_roughness: 0.5,
        double_sided: true,
        cull_mode: None,
        ..default()
    });
    // Godot CylinderMesh(top 0.06, bottom 0.10, 10 radial segments, 4 rings).
    let tower = meshes.add(
        ConicalFrustum {
            radius_top: 0.06,
            radius_bottom: 0.10,
            height: HEIGHT,
        }
        .mesh()
        .resolution(10)
        .segments(5),
    );
    let art = Art {
        nacelle: meshes.add(Cuboid::new(0.20, 0.18, 0.40)),
        // Godot SphereMesh(radius 0.08, 8 radial segments, 4 rings = 5 bands).
        cap: meshes.add(Sphere::new(0.08).mesh().uv(8, 5)),
        blade: meshes.add(Cuboid::new(0.07, 1.05, 0.02)),
        gray: gray.clone(),
        white,
    };
    let bounds = Bounds {
        min: Vec3::new(-1.15, 0., -0.15),
        max: Vec3::new(-1.15, 0., -0.15) + Vec3::new(2.3, HEIGHT + 1.15, 1.2),
    };
    (tower, gray, bounds, art)
}

impl Art {
    /// Children of the tower, which itself sits at (0, H/2, 0) in the body.
    /// `hub` names the accepted body whose wind spins it; a placement ghost's
    /// hub (`None`) stays still, as the source ghost's `_bladeHub` is never set.
    pub(super) fn spawn(&self, commands: &mut Commands, tower: Entity, hub: Option<u32>) {
        commands.spawn((
            Mesh3d(self.nacelle.clone()),
            MeshMaterial3d(self.gray.clone()),
            Transform::from_xyz(0., HEIGHT * 0.5, 0.05),
            ChildOf(tower),
        ));
        let net_id = hub;
        let hub = commands
            .spawn((
                Name::new("BladeHub"),
                Transform::from_xyz(0., HEIGHT * 0.5, 0.28),
                Visibility::Inherited,
                ChildOf(tower),
            ))
            .id();
        if let Some(net_id) = net_id {
            commands.entity(hub).insert(Hub { net_id });
        }
        commands.spawn((
            Mesh3d(self.cap.clone()),
            MeshMaterial3d(self.gray.clone()),
            Transform::default(),
            ChildOf(hub),
        ));
        for i in 0..3 {
            let arm = commands
                .spawn((
                    Transform::from_rotation(Quat::from_rotation_z((i as f32 * 120.).to_radians())),
                    Visibility::Inherited,
                    ChildOf(hub),
                ))
                .id();
            commands.spawn((
                Mesh3d(self.blade.clone()),
                MeshMaterial3d(self.white.clone()),
                Transform::from_xyz(0., 0.58, 0.),
                ChildOf(arm),
            ));
        }
    }
}

/// Source `_Process`: `RotateZ(delta * (0.25 + 5.5 * windFactor))`, with the
/// accepted wind. Singleplayer pause stops it with the rest of the scene.
pub(super) fn spin(
    time: Res<Time>,
    snapshot: Res<GameSnapshot>,
    ui: Option<Res<crate::ui::UiState>>,
    mut hubs: Query<(&Hub, &mut Transform)>,
) {
    if ui.is_some_and(|ui| crate::player::simulation_paused(&ui)) {
        return;
    }
    let dt = time.delta_secs();
    for (hub, mut pose) in &mut hubs {
        let wind = snapshot.0["deployables"]
            .as_array()
            .into_iter()
            .flatten()
            .find(|r| r["netId"].as_u64() == Some(u64::from(hub.net_id)))
            .and_then(|r| r["wind"].as_f64())
            .unwrap_or(0.) as f32;
        pose.rotate_z(dt * (0.25 + 5.5 * wind));
    }
}
