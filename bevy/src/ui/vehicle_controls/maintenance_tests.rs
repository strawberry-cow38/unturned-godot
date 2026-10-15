use super::*;

#[test]
fn original_sections_use_canonical_sparse_masks_and_keep_repair_buttons_until_acceptance() {
    let mut app = setup();
    app.world_mut()
        .resource_mut::<ItemCatalog>()
        .vehicle_maintenance = Some(crate::vehicle::maintenance::Definition {
        glass_names: (0..18).map(|i| format!("pane {i}")).collect(),
    });
    let mut snapshot = app.world_mut().resource_mut::<GameSnapshot>();
    let accepted = &mut snapshot.0["vehicles"][0];
    accepted["glassMask"] = json!((1 << 1) | (1 << 17));
    accepted["brokenGlassMask"] = json!(1 << 17);
    accepted["tireMask"] = json!((1 << 1) | (1 << 3));
    accepted["poppedTireMask"] = json!(1 << 3);
    drop(snapshot);
    render(&mut app);
    let buttons: Vec<_> = app
        .world_mut()
        .query::<(Entity, &Fix)>()
        .iter(app.world())
        .map(|(e, f)| (e, f.1, f.2))
        .collect();
    assert_eq!(buttons.len(), 6);
    assert_eq!(
        buttons
            .iter()
            .filter(|(_, p, _)| *p == Part::Glass)
            .map(|(_, _, i)| *i)
            .collect::<Vec<_>>(),
        [1, 17]
    );
    assert_eq!(
        buttons
            .iter()
            .filter(|(_, p, _)| *p == Part::Tire)
            .map(|(_, _, i)| *i)
            .collect::<Vec<_>>(),
        [1, 3]
    );
    for kind in [Part::Glass, Part::Tire] {
        let active = if kind == Part::Glass { 17 } else { 3 };
        let intact = buttons
            .iter()
            .find(|(_, p, i)| *p == kind && *i == 1)
            .unwrap()
            .0;
        app.world_mut().trigger(Activate { entity: intact });
        assert!(super::super::super::tests::events(&mut app).is_empty());
        let target = buttons
            .iter()
            .find(|(_, p, i)| *p == kind && *i == active)
            .unwrap()
            .0;
        app.world_mut().trigger(Activate { entity: target });
        let events = super::super::super::tests::events(&mut app);
        assert!(
            matches!(&events[..],[UiIntent::Action{name,args}]if name=="vehicleMaintenance"&&args["index"]==active&&args["action"]==if kind==Part::Glass{"RepairGlass"}else{"RepairTire"})
        );
        assert_ne!(
            kind.broken(&app.world().resource::<GameSnapshot>().0["vehicles"][0]),
            0,
            "no optimistic local repair"
        );
        app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"][0][if kind == Part::Glass {
            "brokenGlassMask"
        } else {
            "poppedTireMask"
        }] = json!(0);
        render(&mut app);
        assert!(
            app.world()
                .entity(target)
                .contains::<bevy::ui::InteractionDisabled>()
        );
        assert_eq!(
            app.world().get::<Fix>(target).unwrap().2,
            active,
            "retain pressed entity and canonical index"
        );
    }
    let labels: Vec<_> = app
        .world_mut()
        .query::<(&PartText, &Text)>()
        .iter(app.world())
        .map(|(p, t)| (p.0, p.1, t.0.clone()))
        .collect();
    assert!(labels.iter().any(|(p, i, t)| *p == Part::Glass
        && *i == 17
        && t.contains("pane 17")
        && t.ends_with("intact")));
    assert!(
        labels
            .iter()
            .any(|(p, i, t)| *p == Part::Tire && *i == 3 && t.starts_with("wheel 4")),
        "sparse canonical slots never renumber repair identity"
    );
    let text = app
        .world_mut()
        .query_filtered::<&Text, With<BodyText>>()
        .single(app.world())
        .unwrap()
        .0
        .clone();
    assert!(
        text.contains("glass       2 / 2 intact") && text.contains("tires       2 / 2 inflated")
    );
}

#[test]
fn original_glass_names_wait_for_catalog_and_late_masks_rebuild_once() {
    let mut app = setup();
    app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"][0]["glassMask"] = json!(1 << 17);
    render(&mut app);
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<Root>>()
            .iter(app.world())
            .count(),
        0
    );
    app.world_mut()
        .resource_mut::<ItemCatalog>()
        .vehicle_maintenance = Some(crate::vehicle::maintenance::Definition {
        glass_names: (0..18)
            .map(|i| {
                if i == 17 {
                    "canopy".into()
                } else {
                    format!("pane{i}")
                }
            })
            .collect(),
    });
    render(&mut app);
    let buttons: Vec<_> = app
        .world_mut()
        .query::<(Entity, &Fix)>()
        .iter(app.world())
        .map(|(e, f)| (e, f.1, f.2))
        .collect();
    assert_eq!(buttons.len(), 3);
    render(&mut app);
    assert!(
        buttons
            .iter()
            .all(|(e, _, _)| app.world().get::<Fix>(*e).is_some())
    );
}
