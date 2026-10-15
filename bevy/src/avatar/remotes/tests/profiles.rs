use super::*;
use crate::{bridge::CoreBridge, character::nameplate::Nameplate};

#[test]
fn accepted_name_uses_original_sanitizer_and_absent_avatar_stays_name_only_until_disconnect() {
    let mut app = app();
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    // Source sanitation is intentionally available without constructing a host.
    let raw = "  [img] Ålice\n ";
    let expected = bridge.request("profileRules", json!({"name":raw})).unwrap()["result"]["name"]
        .as_str()
        .unwrap()
        .to_owned();
    app.insert_resource(bridge);
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["profile"]["name"] = json!(raw);
    app.update();
    let (entity, name) = app
        .world_mut()
        .query::<(Entity, &Nameplate)>()
        .single(app.world())
        .unwrap();
    assert_eq!(name.name, expected);
    assert!(name.picture.is_none());
    let entity = entity;
    assert_eq!(
        app.world().get::<ChildOf>(entity).unwrap().parent(),
        body(&app)
    );
    app.update();
    assert_eq!(
        app.world_mut()
            .query::<&Nameplate>()
            .iter(app.world())
            .count(),
        1
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["profile"]["name"] =
        json!("Changed");
    app.update();
    assert_eq!(
        app.world().get::<Nameplate>(entity).unwrap().name,
        "Changed"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["connected"] = json!(false);
    app.update();
    assert!(app.world().get_entity(entity).is_err());
}
