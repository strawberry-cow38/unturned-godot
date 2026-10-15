//! Original local Die/Respawn presentation, driven only by the owner's alive fact.
use crate::{
    character::{Appearance, ragdoll::StartRagdoll},
    player::{
        GameSnapshot, PlayerBody, PlayerCamera, PlayerInput, WorldSessionEpoch, simulation_paused,
    },
    rig::{RigLibrary, spawn_rig},
    ui::UiState,
    world::WorldConfig,
};
use bevy::{mesh::skinning::SkinnedMeshInverseBindposes, prelude::*};
#[derive(Resource)]
pub(super) struct Death {
    corpse: Option<Entity>,
    epoch: u64,
    orbit: f32,
    random: u64,
}
impl Default for Death {
    fn default() -> Self {
        Self {
            corpse: None,
            epoch: 0,
            orbit: 2.2_f32.atan2(2.8),
            random: 0x128348239,
        }
    }
}
#[derive(Component)]
pub(super) struct PlayerCorpse;
#[allow(clippy::too_many_arguments)]
pub(super) fn update(
    mut commands: Commands,
    mut death: ResMut<Death>,
    epoch: Res<WorldSessionEpoch>,
    snapshot: Res<GameSnapshot>,
    input: Res<PlayerInput>,
    config: Res<WorldConfig>,
    time: Res<Time>,
    ui: Res<UiState>,
    bodies: Query<&Transform, (With<PlayerBody>, Without<PlayerCamera>)>,
    corpses: Query<&Transform, (With<PlayerCorpse>, Without<PlayerCamera>)>,
    mut camera: Query<
        &mut Transform,
        (
            With<PlayerCamera>,
            Without<PlayerBody>,
            Without<PlayerCorpse>,
        ),
    >,
    mut rigs: ResMut<RigLibrary>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let dead = snapshot.0["connected"].as_bool().unwrap_or(false)
        && snapshot.0["player"]["alive"].as_bool() == Some(false);
    if death.epoch != epoch.0 || !dead {
        if let Some(corpse) = death.corpse.take() {
            commands.entity(corpse).try_despawn();
        }
        death.epoch = epoch.0;
    }
    if !dead {
        return;
    }
    let Ok(body) = bodies.single() else {
        return;
    };
    let yaw = Quat::from_rotation_y(input.yaw().to_radians());
    if death.corpse.is_none() {
        let at = body.translation - Vec3::Y * 0.9;
        let face = snapshot.0["player"]["profile"]["face"]
            .as_u64()
            .unwrap_or(0)
            .min(31) as u8;
        if let Ok(corpse) = spawn_rig(
            &mut commands,
            &mut rigs,
            &mut meshes,
            &mut binds,
            &config.content_root.join("rig.json"),
            materials.add(StandardMaterial::default()),
            Transform::from_translation(at).with_rotation(yaw),
            false,
            "Idle_Stand",
        ) {
            let noise = Vec3::new(
                crate::particles::random(&mut death.random) * 32. - 16.,
                0.,
                crate::particles::random(&mut death.random) * 32. - 16.,
            );
            let impulse = (yaw * Vec3::NEG_Z * 5. + Vec3::Y * 8. + noise) * 0.64;
            commands.entity(corpse).insert((
                Name::new("Owner death body"),
                PlayerCorpse,
                Appearance {
                    face: Some(face),
                    ..default()
                },
                StartRagdoll(impulse),
            ));
            death.corpse = Some(corpse);
            death.orbit = 2.2_f32.atan2(2.8);
        }
    }
    if !simulation_paused(&ui) {
        death.orbit += time.delta_secs() * 0.22;
    }
    let focus = death
        .corpse
        .and_then(|e| corpses.get(e).ok())
        .map_or(body.translation, |t| t.translation)
        + Vec3::Y * 0.6;
    let eye = focus + Vec3::new(death.orbit.sin() * 3.6, 2., death.orbit.cos() * 3.6);
    for mut camera in &mut camera {
        *camera = Transform::from_translation(eye).looking_at(focus, Vec3::Y);
    }
}
#[cfg(test)]
impl Death {
    pub(super) fn corpse_for_test(&self) -> Option<Entity> {
        self.corpse
    }
}
