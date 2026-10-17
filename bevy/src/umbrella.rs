//! Source umbrellas (retail `Cloud`, `Umbrellas.cs`, PlayerController.EquipHeldUmbrella and
//! the PhysicsTick line `_move.GravityMultiplier = held umbrella && IsEquipComplete ? Gravity : 1`).
//! The hold is EmptyHands (`Melee_Equip`, no carry model), so the native viewmodel already shows
//! it; what the umbrella changes is the owner's fall. The client-authoritative movement step
//! takes the multiplier as an input (the bridge `simulate` hands it to `PlayerMovementSim`), and
//! the server-derived fall damage reads the same rule from the replicated hand
//! (`bridge/UmbrellaServices.cs`).
use crate::entities::ItemVisual;

/// The fall gravity multiplier this frame: the held umbrella's `Gravity` once its equip
/// clip has finished (`Viewmodel.IsEquipComplete`, the native `ViewmodelStatus.ready`), else 1.
/// Recomputed every tick from the hand, so there is no reset to forget.
pub(crate) fn gravity(held: Option<&ItemVisual>, equip_complete: bool) -> f32 {
    held.map(|i| i.gravity_multiplier)
        .filter(|g| *g > 0. && equip_complete)
        .unwrap_or(1.)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    fn item(id: u16, gravity: Option<f32>) -> ItemVisual {
        serde_json::from_value(json!({"id":id,"name":"x","type":"cloud","asset":null,"mesh":null,"texture":null,"icon":null,
            "gravityMultiplier":gravity})).unwrap_or_else(|_| {
            serde_json::from_value(json!({"id":id,"name":"x","type":"cloud","asset":null,"mesh":null,"texture":null,"icon":null})).unwrap()
        })
    }
    #[test]
    fn an_umbrella_slows_the_fall_only_once_its_equip_has_finished() {
        let umbrella = item(1103, Some(0.25));
        assert_eq!(gravity(Some(&umbrella), true), 0.25);
        assert_eq!(gravity(Some(&umbrella), false), 1., "UseableCloud waits for the equip animation");
        assert_eq!(gravity(Some(&item(13, None)), true), 1.);
        assert_eq!(gravity(None, true), 1.);
    }
}
