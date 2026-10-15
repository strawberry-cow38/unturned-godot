use super::*;
use crate::{
    entities::CoreEvent,
    particles::{Emitter, ParticlePlugin},
    player::WorldSessionEpoch,
    settings::Settings,
    ui::UiModel,
};

#[test]
fn accepted_glass_fact_uses_original_shader_fallback_and_never_replays_snapshot_or_repair() {
    let mut app = app();
    app.init_asset::<AudioSource>()
        .init_resource::<Settings>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<UiModel>()
        .insert_resource(crate::ui::UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .add_message::<CoreEvent>()
        .add_plugins(ParticlePlugin)
        .add_systems(Update, super::super::effects::receive.after(adopt));
    app.world_mut().resource_mut::<UiModel>().connected = true;
    build(
        &mut app,
        "sedan",
        Transform::from_xyz(2., 3., 4.).with_rotation(Quat::from_rotation_y(1.2)),
    );
    let bursts = |app: &mut App| -> Vec<Entity> {
        app.world_mut()
            .query_filtered::<Entity, With<Emitter>>()
            .iter(app.world())
            .collect()
    };
    mask(&mut app, 1);
    assert!(
        bursts(&mut app).is_empty(),
        "late damaged snapshot is not a break fact"
    );
    let fact =
        |app: &mut App, sequence, kind: &str| {
            app.world_mut().write_message(CoreEvent {sequence,kind:kind.into(),
            data:json!({"netId":1,"label":"windshield","position":[9.,8.,7.],"normal":[0.,1.,0.],
                "halfSize":[0.8,0.2,0.5],"acceptedTick":4000})});
        };
    fact(&mut app, 9, "vehicleGlassBroken");
    app.update();
    let emitted = bursts(&mut app);
    assert_eq!(emitted.len(), 1);
    let e = emitted[0];
    let spec = app.world().get::<Emitter>(e).unwrap().source_spec();
    assert_eq!(spec.amount, 6);
    assert_eq!(
        spec.direction,
        Vec3::Y,
        "accepted frame remains correct if the vehicle moved before delivery"
    );
    assert_eq!(spec.emission_box, Some(Vec3::new(0.8, 0.2, 0.5)));
    assert!((spec.size - Vec2::new(0.0675, 0.1375)).length() < 0.00001);
    assert_eq!(
        spec.colors[0].1,
        Vec4::from_array(
            Color::srgba(0.753, 0.8245, 0.857, 0.5)
                .to_linear()
                .to_f32_array()
        )
    );
    assert_eq!(
        app.world().get::<Transform>(e).unwrap().translation,
        Vec3::new(9., 8., 7.)
    );
    assert!(app.world().get::<ChildOf>(e).is_none());
    fact(&mut app, 9, "vehicleGlassBroken");
    fact(&mut app, 10, "vehicleGlassRepaired");
    mask(&mut app, 0);
    assert_eq!(bursts(&mut app).len(), 1);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(bursts(&mut app).is_empty());
}
