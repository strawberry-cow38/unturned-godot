//! Deployable.UpdateSwitchLight: the source indicator follows the accepted
//! switch bit. Its cube sits on the body root, outside the mesh rotation.
use super::*;

pub(super) struct Art {
    mesh: Handle<Mesh>,
    on: Handle<StandardMaterial>,
    off: Handle<StandardMaterial>,
}
#[derive(Component)]
pub(super) struct Indicator {
    pub net_id: u32,
    on: Handle<StandardMaterial>,
    off: Handle<StandardMaterial>,
}
impl Art {
    pub fn load(meshes: &mut Assets<Mesh>, materials: &mut Assets<StandardMaterial>) -> Self {
        let mut material = |on: bool| {
            let color = if on {
                Color::srgb(0.15, 0.9, 0.2)
            } else {
                Color::srgb(0.9, 0.15, 0.15)
            };
            materials.add(StandardMaterial {
                base_color: color,
                emissive: color.to_linear() * if on { 1.6 } else { 0.8 },
                perceptual_roughness: 1.,
                ..default()
            })
        };
        Self {
            mesh: meshes.add(Cuboid::new(0.12, 0.06, 0.12)),
            on: material(true),
            off: material(false),
        }
    }
    pub fn spawn(&self, commands: &mut Commands, root: Entity, net_id: u32, on: bool) {
        commands.spawn((
            Name::new("SwitchStateLight"),
            Indicator {
                net_id,
                on: self.on.clone(),
                off: self.off.clone(),
            },
            Mesh3d(self.mesh.clone()),
            MeshMaterial3d(if on {
                self.on.clone()
            } else {
                self.off.clone()
            }),
            Transform::from_translation(geometry::env_vector(
                "UG_SWLP",
                Vec3::new(0., 0.20, -0.14),
            )),
            ChildOf(root),
        ));
    }
}
pub(super) fn update(
    snapshot: Res<GameSnapshot>,
    mut indicators: Query<(&Indicator, &mut MeshMaterial3d<StandardMaterial>)>,
) {
    for (indicator, mut material) in &mut indicators {
        let Some(row) = snapshot.0["deployables"]
            .as_array()
            .into_iter()
            .flatten()
            .find(|r| r["netId"].as_u64() == Some(u64::from(indicator.net_id)))
        else {
            continue;
        };
        let expected = if row["toggledOn"] == true {
            &indicator.on
        } else {
            &indicator.off
        };
        if material.0 != *expected {
            material.0 = expected.clone();
        }
    }
}
