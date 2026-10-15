//! Last completed render-frame facts for screenshot diagnosis (not switch intent).
use super::*;
use bevy::camera::visibility::{InheritedVisibility, RenderLayers, ViewVisibility};

#[derive(Resource, Default)]
pub(crate) struct LightDiagnostics(pub Value);

pub(super) fn install(app: &mut App) {
    app.init_resource::<LightDiagnostics>()
        .add_systems(Last, collect);
}
fn collect(
    mut state: ResMut<LightDiagnostics>,
    mut enabled: Local<Option<bool>>,
    lights: Query<
        (
            Entity,
            Option<&Name>,
            Option<&PointLight>,
            Option<&SpotLight>,
            &Visibility,
            &InheritedVisibility,
            &ViewVisibility,
            &GlobalTransform,
            Option<&RenderLayers>,
        ),
        Or<(With<PointLight>, With<SpotLight>)>,
    >,
) {
    if !*enabled.get_or_insert_with(|| std::env::var("UG_LIGHT_DIAGNOSTICS").as_deref() == Ok("1"))
    {
        return;
    }
    state.0 = Value::Array(lights.iter().map(|(entity,name,point,spot,visibility,inherited,view,pose,layers)| {
        serde_json::json!({
            "entity":entity.to_bits(),"name":name.map(Name::as_str),
            "type":if point.is_some(){"point"}else{"spot"},
            "intensity":point.map(|p|p.intensity).or_else(||spot.map(|p|p.intensity)),
            "range":point.map(|p|p.range).or_else(||spot.map(|p|p.range)),
            "visibility":format!("{visibility:?}"),"inherited":inherited.get(),"view":view.get(),
            "position":pose.translation().to_array(),"layers":format!("{:?}",layers.cloned().unwrap_or_default())
        })
    }).collect());
}
