use super::*;

#[test]
fn shared_shader_is_readable_before_later_character_plugin_matches_main_build_order() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default(), InfoBillboardPlugin));
    let read = |app: &App| {
        let ShaderRef::Path(path) = Face::vertex_shader() else {
            panic!("expected shared shader path")
        };
        let server = app.world().resource::<AssetServer>();
        let source = server.get_source(path.source().clone()).unwrap();
        bevy::tasks::block_on(async {
            let mut reader = source
                .reader()
                .read(path.path())
                .await
                .expect("Info material can request this shader BEFORE CharacterPlugin builds");
            let mut bytes = Vec::new();
            reader.read_to_end(&mut bytes).await.unwrap();
            bytes
        })
    };
    assert_eq!(
        read(&app),
        include_bytes!("../character/nameplate/billboard.wesl")
    );
    app.add_plugins(crate::character::CharacterPlugin);
    assert_eq!(
        read(&app),
        include_bytes!("../character/nameplate/billboard.wesl"),
        "later nameplate setup must retain the same shared asset"
    );
}
