//! TryToggleHitch precedes normal vehicle access after the original focus scan.
use super::super::*;

pub(in crate::vehicle) fn offer<'a>(
    focus: &mut crate::entities::InteractionFocus,
    body: &VehicleBody, pose: &Transform,
    mut bodies: impl Iterator<Item = (&'a VehicleBody, &'a Transform)>,
    feet: Vec3, distance: f32, eye: Vec3, direction: Vec3,
) -> bool {
    let spec = &data::definition(body.type_id).unwrap().spec;
    if spec.kingpin == [0.; 3] || body.flags & 32 != 0 { return false; }
    let pin = pose.transform_point(spec.kingpin.into());
    if feet.distance_squared(pin) > 3.5_f32.powi(2) { return false; }
    let linked = body.coupling.cab != 0;
    if !linked && !bodies.any(|(cab, pose)| {
        let spec = &data::definition(cab.type_id).unwrap().spec;
        spec.fifth_wheel != [0.; 3] && cab.coupling.trailer == 0 && cab.flags & 32 == 0
            && pose.transform_point(spec.fifth_wheel.into()).distance_squared(pin) <= 1.6_f32.powi(2)
    }) { return false; }
    focus.offer(distance, if linked { "disconnect trailer" } else { "connect trailer" },
        "vehicleHitch", json!({"trailerNetId":body.net_id,"origin":eye.to_array(),"direction":direction.to_array()}),false);
    true
}
