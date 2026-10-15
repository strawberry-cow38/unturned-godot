//! RemotePlayers.ApplyHeld consumes the shared original body-hook builder.
use super::*;
use crate::{avatar::held_item, content::ContentCache};
pub(super) use held_item::{Held, Library};

#[derive(Component)]
pub(crate) struct RemoteMuzzle {
    pub player_id: u16,
    pub point: Vec3,
}

#[allow(clippy::too_many_arguments)]
pub(super) fn sync(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut library: ResMut<Library>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut bodies: Query<(&RigPlayback, &RemotePlayer, &mut Held)>,
) {
    let rows: HashMap<_, _> = replicas(&snapshot.0).collect();
    for (rig, player, mut held) in &mut bodies {
        let Some(row) = rows.get(&player.id) else {
            continue;
        };
        let id = row["heldItemId"].as_u64().unwrap_or(0) as u16;
        let previous = held.root;
        held_item::sync(
            &mut commands,
            &mut held,
            rig,
            id,
            &row["attachments"],
            &RenderLayers::layer(0),
            "Remote",
            &catalog,
            &config,
            &assets,
            &mut library,
            &mut cache,
            &mut meshes,
            &mut materials,
        );
        if held.root != previous {
            if let (Some(root), Some(point)) = (held.root, held.muzzle) {
                commands.entity(root).insert(RemoteMuzzle {
                    player_id: player.id,
                    point,
                });
            }
        }
    }
}
