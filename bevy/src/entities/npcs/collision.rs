//! A playable/listen scene owns the same opaque NPC capsules as source Godot.
//! Remote diagnostic characters remain local; they never upload host geometry.
use super::*;
use crate::player::GameSnapshot;

#[derive(Resource, Default)]
struct Published {
    bodies: HashMap<String, (Vec3, u64, u64)>,
    error: Option<String>,
}
pub(super) fn install(app: &mut App) {
    app.init_resource::<Published>()
        .add_systems(Update, publish.after(sync));
}
fn publish(
    registry: Res<Registry>,
    geometry: Res<WorldGeometry>,
    snapshot: Res<GameSnapshot>,
    bridge: Res<CoreBridge>,
    mut published: ResMut<Published>,
    mut model: ResMut<UiModel>,
) {
    if !model.connected || !crate::network::owns_world(&snapshot.0) {
        published.bodies.clear();
        published.error = None;
        return;
    }
    let result = (|| -> anyhow::Result<()> {
        let removed: Vec<_> = published
            .bodies
            .keys()
            .filter(|key| !registry.instances.contains_key(*key))
            .cloned()
            .collect();
        for key in removed {
            bridge.request(
                "worldDynamicGeometry",
                json!({"key":format!("source-npc:{key}"),"meshes":[]}),
            )?;
            published.bodies.remove(&key);
        }
        for (key, instance) in &registry.instances {
            let Some(position) = crate::player::vector(&instance.placement["position"]) else {
                continue;
            };
            let state = (position, registry.epoch, geometry.revision);
            if published.bodies.get(key) == Some(&state) {
                continue;
            }
            bridge.request(
                "worldDynamicGeometry",
                json!({
                    "key":format!("source-npc:{key}"),"meshes":[],
                    "capsules":[{"foot":position.to_array(),"radius":0.35,"height":1.8}]
                }),
            )?;
            published.bodies.insert(key.clone(), state);
        }
        Ok(())
    })();
    if let Err(error) = result {
        let message = format!("NPC collision: {error}");
        if published.error.as_ref() != Some(&message) {
            warn!("{message}");
            model.notice(message.clone());
            published.error = Some(message);
        }
    } else {
        published.error = None;
    }
}
