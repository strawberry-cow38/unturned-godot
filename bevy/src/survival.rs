//! Survival feedback the owner's client produces: the two body-temperature probes
//! that need this client's physics world (ShelterProbe and ThermalField), the
//! DeadzoneOverlay grain and the GeigerCounter clicks. The bridge's client shell
//! runs PlayerTemperatureSim and streams only its band; every dose and status
//! read here is the accepted owner replica.
pub(crate) mod deadzone;
mod geiger;
#[cfg(test)]
mod visual;
use crate::{
    player::{GameSnapshot, PlayerBody, PublishPlayer},
    sound::shelter::Shelter,
    ui::UiModel,
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde_json::{Value, json};

/// PlayerController LMB/RMB with a fluid container in hand: TryDrinkContainer and
/// TryFillAtTap through the existing accepted commands. The returned item id asks
/// the viewmodel for PlayConsumeUse; only the server's echo changes the bottle.
pub(crate) fn fluid_hand(
    bridge: &crate::bridge::CoreBridge,
    snapshot: &Value,
    held: Option<&crate::entities::ItemVisual>,
    press: bool,
    secondary: bool,
    feedback: &mut crate::ui::feedback::Feedback,
) -> anyhow::Result<Option<u16>> {
    let Some(cell) = crate::use_items::Cell::held(snapshot) else {
        return Ok(None);
    };
    if !held.is_some_and(|h| h.id == cell.id && h.hand_kind == "fluid") || !(press || secondary) {
        return Ok(None);
    }
    let name = if press { "drinkHeldFluid" } else { "fillHeldFluidAtTap" };
    let response = bridge.request(
        "action",
        json!({"name":name,"args":{"page":cell.page,"x":cell.x,"y":cell.y,"id":cell.id}}),
    )?;
    let result = &response["result"];
    if let Some(toast) = result["toast"].as_str() {
        feedback.fluid_toast(toast);
    }
    Ok((press && result["queued"].as_bool() == Some(true)).then_some(cell.id))
}

/// StoreShelf: a lit appliance is a ThermalSource of 25 C over 5 m, SelfRadius 0.9 m.
pub(crate) const COOKER_HEAT_C: f32 = 25.;
pub(crate) const COOKER_HEAT_RADIUS: f32 = 5.;
pub(crate) const COOKER_SELF_RADIUS: f32 = 0.9;
/// ThermalFalloff.MaxCombinedC.
pub(crate) const MAX_COMBINED_C: f32 = 40.;

/// Latest client-world probes, carried on every authority tick as
/// `temperatureProbe`; the bridge latches them on its 4 Hz probe like the source.
#[derive(Resource, Default, Clone, Copy, Debug, PartialEq)]
pub struct TemperatureProbe {
    pub sheltered: bool,
    pub source_c: f32,
    /// PlayerController._lookEnd (LookReach 2.6 m), for WaterTap.AimedAt.
    pub look_end: Option<Vec3>,
}
/// PlayerController.LookReach.
pub(crate) const LOOK_REACH: f32 = 2.6;
impl TemperatureProbe {
    pub fn json(&self) -> Value {
        json!({"sheltered":self.sheltered,"sourceC":self.source_c})
    }
}

pub struct SurvivalPlugin;
impl Plugin for SurvivalPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<TemperatureProbe>()
            .add_systems(FixedUpdate, probe.before(PublishPlayer))
            .add_plugins((deadzone::DeadzonePlugin, geiger::GeigerPlugin));
    }
}

/// ThermalFalloff.Strength: smoothstep from 1 at the centre to 0 at the radius.
pub(crate) fn strength(distance: f32, radius: f32) -> f32 {
    if radius <= 0. {
        return 0.;
    }
    if distance <= 0. {
        return 1.;
    }
    if distance >= radius {
        return 0.;
    }
    let t = 1. - distance / radius;
    t * t * (3. - 2. * t)
}

/// ThermalField.NetC over lit cookers: range first, then line of sight, summed
/// and clamped. `blocked(from, to, self_radius)` is the world-layer ray.
pub(crate) fn net_c(
    at: Vec3,
    sources: impl IntoIterator<Item = Vec3>,
    mut blocked: impl FnMut(Vec3, Vec3, f32) -> bool,
) -> f32 {
    let mut sum = 0.;
    for p in sources {
        let d = at.distance(p);
        if d >= COOKER_HEAT_RADIUS {
            continue;
        }
        if blocked(at, p, COOKER_SELF_RADIUS) {
            continue;
        }
        sum += COOKER_HEAT_C * strength(d, COOKER_HEAT_RADIUS);
    }
    sum.clamp(-MAX_COMBINED_C, MAX_COMBINED_C)
}

/// Accepted container replicas whose appliance is lit (StoreShelf.SetCookerOn).
pub(crate) fn lit_cookers(snapshot: &Value) -> impl Iterator<Item = Vec3> + '_ {
    snapshot["containers"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|c| c["cookerOn"].as_bool() == Some(true))
        .filter_map(|c| crate::player::vector(&c["position"]))
}

#[allow(clippy::too_many_arguments)]
fn probe(
    model: Res<UiModel>,
    snapshot: Res<GameSnapshot>,
    shelter: Shelter,
    context: ReadRapierContext,
    input: Res<crate::player::PlayerInput>,
    lean: Option<Res<crate::player::lean::LeanState>>,
    player: Query<(Entity, &Transform, &PlayerBody)>,
    mut out: ResMut<TemperatureProbe>,
) {
    if !model.connected {
        *out = default();
        return;
    }
    let Ok((entity, transform, body)) = player.single() else {
        return;
    };
    let at = transform.translation;
    // The first-person look ray: world, props and vehicle surfaces stop it.
    let (eye, forward) = crate::player::aim_eye(at, body, &input, lean.as_deref());
    let look_end = context.single().ok().map(|context| {
        context
            .cast_ray(
                eye,
                forward,
                LOOK_REACH,
                true,
                QueryFilter::default()
                    .exclude_collider(entity)
                    .exclude_sensors()
                    .predicate(&crate::player::world_or_prop)
                    .groups(CollisionGroups::new(Group::ALL, Group::GROUP_1 | Group::GROUP_4)),
            )
            .map_or(eye + forward * LOOK_REACH, |(_, toi)| eye + forward * toi)
    });
    // ShelterProbe casts from the body origin; the native feet rest exactly on
    // the floor surface, so lift past it as the shared footstep probe does.
    let sheltered = shelter.covered(at + Vec3::Y * 0.2);
    let source_c = match context.single() {
        Ok(context) => net_c(at, lit_cookers(&snapshot.0), |from, to, self_radius| {
            // ThermalField.Blocked: world geometry only (BlockerMask bit 0), cast
            // FROM the player; a hit inside the source's own body is the source.
            let delta = to - from;
            let length = delta.length();
            if length <= f32::EPSILON {
                return false;
            }
            context
                .cast_ray(
                    from,
                    delta / length,
                    length,
                    true,
                    QueryFilter::default()
                        .exclude_sensors()
                        .groups(CollisionGroups::new(Group::ALL, Group::GROUP_1)),
                )
                .is_some_and(|(_, toi)| {
                    (from + delta / length * toi).distance(to) > self_radius.max(0.05)
                })
        }),
        Err(_) => 0.,
    };
    *out = TemperatureProbe {
        sheltered,
        source_c,
        look_end,
    };
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn thermal_falloff_matches_source_smoothstep_range_and_clamp() {
        assert_eq!(strength(0., 5.), 1.);
        assert_eq!(strength(5., 5.), 0.);
        assert_eq!(strength(7., 5.), 0.);
        assert_eq!(strength(2.5, 5.), 0.5);
        assert_eq!(strength(1., 0.), 0.);
        // 25 C at 1 m of a 5 m cooker: t=.8 -> .896.
        let one = net_c(Vec3::ZERO, [Vec3::X], |_, _, _| false);
        assert!((one - 25. * 0.896).abs() < 1e-4);
        // Blocked sources contribute nothing; out-of-range ones are never ray-tested.
        let mut rays = 0;
        assert_eq!(
            net_c(Vec3::ZERO, [Vec3::X, Vec3::X * 9.], |_, _, r| {
                rays += 1;
                assert_eq!(r, COOKER_SELF_RADIUS);
                true
            }),
            0.
        );
        assert_eq!(rays, 1);
        // Two fires are warmer than one, a pile never more than the clamp.
        assert_eq!(net_c(Vec3::ZERO, [Vec3::ZERO; 3], |_, _, _| false), 40.);
    }
    #[test]
    fn only_lit_accepted_cookers_are_sources() {
        let snapshot = json!({"containers":[
            {"position":[1,0,0],"cookerOn":true},
            {"position":[2,0,0],"cookerOn":false},
            {"position":[3,0,0]}]});
        assert_eq!(lit_cookers(&snapshot).collect::<Vec<_>>(), vec![Vec3::X]);
        assert_eq!(
            TemperatureProbe {
                sheltered: true,
                source_c: 12.5,
                look_end: None,
            }
            .json(),
            json!({"sheltered":true,"sourceC":12.5})
        );
    }
}
