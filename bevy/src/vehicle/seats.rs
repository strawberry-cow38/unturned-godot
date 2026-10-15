//! Literal source F1..F12 seat selection, routed through the same accepted entry
//! command. Occupancy, driver authority and camera state change only on replicas.
use super::*;

pub(crate) fn pressed(keys: &ButtonInput<KeyCode>) -> Option<u8> {
    [
        KeyCode::F1,
        KeyCode::F2,
        KeyCode::F3,
        KeyCode::F4,
        KeyCode::F5,
        KeyCode::F6,
        KeyCode::F7,
        KeyCode::F8,
        KeyCode::F9,
        KeyCode::F10,
        KeyCode::F11,
        KeyCode::F12,
    ]
    .iter()
    .position(|key| keys.just_pressed(*key))
    .map(|n| n as u8)
}

pub(super) fn input(
    keys: Res<ButtonInput<KeyCode>>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    occupancy: Res<Occupancy>,
    bodies: Query<&VehicleBody>,
    mut intents: MessageWriter<crate::ui::UiIntent>,
) {
    if ui.screen != crate::ui::Screen::Playing || !model.connected || !model.alive {
        return;
    }
    let (Some(current), Some(want)) = (occupancy.seat, pressed(&keys)) else {
        return;
    };
    if want == current.seat {
        return;
    }
    let Ok(body) = bodies.get(current.vehicle) else {
        return;
    };
    let Some(definition) = data::definition(body.type_id) else {
        return;
    };
    if want as usize >= definition.seats.len() {
        return;
    }
    let occupant = if want == 0 {
        body.driver_player_id
    } else {
        body.passengers.get(want as usize - 1).copied().unwrap_or(0)
    };
    if occupant != 0 {
        return;
    }
    intents.write(crate::ui::UiIntent::Action {
        name: "enterVehicle".into(),
        args: json!({"netId":current.net_id,"seat":want}),
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn literal_function_keys_map_to_all_twelve_source_seats() {
        for (index, key) in [
            KeyCode::F1,
            KeyCode::F2,
            KeyCode::F3,
            KeyCode::F4,
            KeyCode::F5,
            KeyCode::F6,
            KeyCode::F7,
            KeyCode::F8,
            KeyCode::F9,
            KeyCode::F10,
            KeyCode::F11,
            KeyCode::F12,
        ]
        .into_iter()
        .enumerate()
        {
            let mut keys = ButtonInput::default();
            keys.press(key);
            assert_eq!(pressed(&keys), Some(index as u8));
            keys.clear();
            assert_eq!(pressed(&keys), None, "held keys are not repeat intents");
        }
    }
    #[test]
    fn real_key_dispatch_sends_intent_without_optimistic_occupancy_and_respects_source_gates() {
        let mut app = App::new();
        let mut model = UiModel::default();
        model.connected = true;
        model.alive = true;
        app.init_resource::<ButtonInput<KeyCode>>()
            .insert_resource(model)
            .insert_resource(UiState {
                screen: crate::ui::Screen::Playing,
                ..default()
            })
            .add_message::<crate::ui::UiIntent>()
            .add_systems(Update, input);
        let vehicle = super::super::tests::body(&mut app, "jeep", Vec3::ZERO);
        let original = OccupiedSeat {
            vehicle,
            net_id: 1,
            seat: 0,
            driver: true,
            aircraft: false,
            velocity: Vec3::ZERO,
            eye_pose: default(),
            body_pose: default(),
        };
        app.insert_resource(Occupancy {
            seat: Some(original),
        });
        let dispatch = |app: &mut App, key| {
            app.world_mut()
                .resource_mut::<ButtonInput<KeyCode>>()
                .reset_all();
            app.world_mut()
                .resource_mut::<ButtonInput<KeyCode>>()
                .press(key);
            app.update();
            app.world_mut()
                .resource_mut::<Messages<crate::ui::UiIntent>>()
                .drain()
                .collect::<Vec<_>>()
        };
        let intents = dispatch(&mut app, KeyCode::F2);
        assert!(
            matches!(&intents[..],[crate::ui::UiIntent::Action {name,args}] if name=="enterVehicle" && args==&json!({"netId":1,"seat":1}))
        );
        assert_eq!(app.world().resource::<Occupancy>().seat.unwrap().seat, 0);
        assert_eq!(
            app.world()
                .get::<VehicleBody>(vehicle)
                .unwrap()
                .driver_player_id,
            1
        );
        assert!(
            dispatch(&mut app, KeyCode::F1).is_empty(),
            "same-seat request is a source no-op"
        );
        assert!(
            dispatch(&mut app, KeyCode::F12).is_empty(),
            "out-of-range must not become AnySeat"
        );
        app.world_mut()
            .get_mut::<VehicleBody>(vehicle)
            .unwrap()
            .passengers = vec![2];
        assert!(
            dispatch(&mut app, KeyCode::F2).is_empty(),
            "occupied source seat cannot be selected"
        );
        app.world_mut()
            .get_mut::<VehicleBody>(vehicle)
            .unwrap()
            .passengers
            .clear();
        app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Inventory;
        assert!(dispatch(&mut app, KeyCode::F2).is_empty());
        app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Playing;
        app.world_mut().resource_mut::<UiModel>().alive = false;
        assert!(dispatch(&mut app, KeyCode::F2).is_empty());
    }
}
