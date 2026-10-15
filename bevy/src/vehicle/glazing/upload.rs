use super::*;
use crate::{bridge::CoreBridge, player::WorldSessionEpoch, ui::UiModel, world::WorldState};
use serde_json::json;

pub(super) fn publish(
    bridge: Res<CoreBridge>,
    model: Res<UiModel>,
    world: Res<WorldState>,
    epoch: Res<WorldSessionEpoch>,
    snapshot: Res<GameSnapshot>,
    mut bodies: Query<(Entity, &VehicleBody, &GlobalTransform, &mut Glazing)>,
    panes: Query<(&Pane, &GlobalTransform)>,
    tires: Query<&super::super::tires::TireVisual>,
) {
    for (entity, body, hull, mut glazing) in &mut bodies {
        // Source only registers rubber when SplitWheelRadial succeeds. A wheel
        // count would invent repairable tires for missing/unsplittable art.
        let tire_mask = tire_mask(entity, tires.iter());
        let mut signature = vec![glazing.broken as f32, tire_mask as f32];
        let mut rows = Vec::new();
        for entity in &glazing.panes {
            let Ok((pane, pose)) = panes.get(*entity) else {
                continue;
            };
            let local = hull.affine().inverse() * pose.affine();
            let (scale, rotation, _) = local.to_scale_rotation_translation();
            let center = local.transform_point3(pane.center);
            signature.extend(center.to_array());
            signature.extend(rotation.to_array());
            signature.extend(scale.to_array());
            rows.push(json!({"label":pane.label(),"center":center.to_array(),"size":pane.size.to_array(),"rotation":rotation.to_array(),"scale":scale.to_array()}));
        }
        glazing.signature = signature.clone();
        if !model.connected || !model.network.owns_world || !world.ready {
            glazing.uploaded = None;
            continue;
        }
        // Optional fields keep older immutable bridge fixtures compatible while
        // protocol69 advertises this authority seam on every vehicle replica.
        if !snapshot.0["vehicles"].as_array().is_some_and(|rows| {
            rows.iter().any(|r| {
                r["netId"].as_u64() == Some(body.net_id as u64) && r["glassMask"].as_u64().is_some()
            })
        }) {
            continue;
        }
        // Damage belongs to authority; it invalidates triangles but never causes
        // re-registration. Only actual moving pane transforms change metadata.
        signature[0] = 0.;
        let key = (epoch.0, body.net_id, signature);
        if glazing.uploaded.as_ref().is_some_and(|old| {
            old.0 == key.0
                && old.1 == key.1
                && old.2.len() == key.2.len()
                && old
                    .2
                    .iter()
                    .zip(&key.2)
                    .all(|(a, b)| (a - b).abs() < 0.00001)
        }) {
            continue;
        }
        match bridge.request(
            "worldVehicleGlazing",
            json!({"netId":body.net_id,"panes":rows,"tireMask":tire_mask}),
        ) {
            Ok(_) => glazing.uploaded = Some(key),
            Err(error) => warn!("Vehicle glazing geometry {}: {error}", body.net_id),
        }
    }
}

fn tire_mask<'a>(
    entity: Entity,
    tires: impl Iterator<Item = &'a super::super::tires::TireVisual>,
) -> u8 {
    tires
        .filter(|t| t.body == entity && t.index < 8)
        .fold(0, |mask, t| mask | (1 << t.index))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn repair_mask_requires_actual_split_rubber_for_this_body_even_when_hidden() {
        let a = Entity::from_bits(1);
        let b = Entity::from_bits(2);
        let tires = [
            super::super::super::tires::TireVisual { body: a, index: 0 },
            super::super::super::tires::TireVisual { body: a, index: 3 },
            super::super::super::tires::TireVisual { body: b, index: 1 },
            super::super::super::tires::TireVisual { body: a, index: 8 },
        ];
        assert_eq!(tire_mask(a, tires.iter()), 9);
        assert_eq!(tire_mask(a, std::iter::empty()), 0);
    }
}
