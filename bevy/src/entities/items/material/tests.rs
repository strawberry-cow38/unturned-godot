use super::*;
use bevy::render::render_resource::{
    ColorTargetState, CompareFunction, DepthStencilState, FragmentState, StencilState,
    TextureFormat,
};

fn descriptor(prepass: bool) -> RenderPipelineDescriptor {
    let mut descriptor = RenderPipelineDescriptor::default();
    if prepass {
        descriptor
            .vertex
            .shader_defs
            .push("PREPASS_PIPELINE".into());
    }
    descriptor.fragment = Some(FragmentState {
        targets: if prepass {
            vec![]
        } else {
            vec![
                Some(ColorTargetState {
                    format: TextureFormat::Rgba8Unorm,
                    blend: None,
                    write_mask: ColorWrites::ALL,
                }),
                None,
                Some(ColorTargetState {
                    format: TextureFormat::Rgba16Float,
                    blend: None,
                    write_mask: ColorWrites::ALL,
                }),
            ]
        },
        ..default()
    });
    descriptor.depth_stencil = Some(DepthStencilState {
        format: TextureFormat::Depth32Float,
        depth_write_enabled: Some(true),
        depth_compare: Some(CompareFunction::GreaterEqual),
        stencil: StencilState::default(),
        bias: default(),
    });
    descriptor
}
#[test]
fn actual_material_roles_preserve_prepass_and_shadow_depth_without_color_or_shadow_only_main_depth()
{
    assert!(DepthMaterial::enable_prepass());
    assert!(!DepthMaterial::enable_shadows());
    assert!(!ShadowMaterial::enable_prepass());
    assert!(ShadowMaterial::enable_shadows());
    for shadow_only in [false, true] {
        let mut main = descriptor(false);
        only_depth(&mut main, shadow_only);
        for target in main.fragment.as_ref().unwrap().targets.iter().flatten() {
            assert!(target.write_mask.is_empty());
        }
        assert_eq!(
            main.depth_stencil.unwrap().depth_write_enabled,
            Some(!shadow_only)
        );
        // Both point and directional shadows enter the same real prepass branch;
        // there is no assumption about projection or a color target at index0.
        let mut shadow = descriptor(true);
        only_depth(&mut shadow, shadow_only);
        assert_eq!(
            shadow.depth_stencil.unwrap().depth_write_enabled,
            Some(true)
        );
        assert!(shadow.fragment.unwrap().targets.is_empty());
    }
}
fn app() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .add_plugins(AlphaDepthPlugin);
    app
}
#[test]
fn original_alpha_thresholds_share_sampler_culling_and_shape_and_retire_with_parent() {
    let mut app = app();
    let mesh = app
        .world_mut()
        .resource_mut::<Assets<Mesh>>()
        .add(Cuboid::from_length(1.));
    let texture = app
        .world_mut()
        .resource_mut::<Assets<Image>>()
        .add(Image::default());
    let visible = StandardMaterial {
        base_color_texture: Some(texture.clone()),
        alpha_mode: AlphaMode::Blend,
        double_sided: true,
        cull_mode: None,
        ..default()
    };
    let material = app
        .world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .add(visible);
    let parent = app
        .world_mut()
        .spawn((
            AlphaDepth,
            Mesh3d(mesh.clone()),
            MeshMaterial3d(material.clone()),
            Transform::from_xyz(4., 5., 6.),
            RenderLayers::layer(15),
        ))
        .id();
    app.update();
    let pair = app.world().get::<Companions>(parent).unwrap();
    let depth = pair.depth;
    let shadow = pair.shadow;
    assert!(app.world().get::<NotShadowCaster>(parent).is_some());
    assert_eq!(
        app.world()
            .resource::<Assets<StandardMaterial>>()
            .get(&material)
            .unwrap()
            .alpha_mode,
        AlphaMode::Blend
    );
    for child in [depth, shadow] {
        assert_eq!(app.world().get::<ChildOf>(child).unwrap().parent(), parent);
        assert_eq!(
            app.world().get::<Transform>(child).unwrap().translation,
            Vec3::ZERO
        );
        assert_eq!(
            app.world().get::<RenderLayers>(child),
            Some(&RenderLayers::layer(15))
        );
        assert_eq!(app.world().get::<Mesh3d>(child).unwrap().0, mesh);
    }
    let dm = app
        .world()
        .resource::<Assets<DepthMaterial>>()
        .get(
            &app.world()
                .get::<MeshMaterial3d<DepthMaterial>>(depth)
                .unwrap()
                .0,
        )
        .unwrap();
    let sm = app
        .world()
        .resource::<Assets<ShadowMaterial>>()
        .get(
            &app.world()
                .get::<MeshMaterial3d<ShadowMaterial>>(shadow)
                .unwrap()
                .0,
        )
        .unwrap();
    assert_eq!(dm.base.alpha_mode, AlphaMode::Mask(0.99));
    assert_eq!(sm.base.alpha_mode, AlphaMode::Mask(0.1));
    assert_eq!(dm.base.base_color_texture, Some(texture.clone()));
    assert_eq!(sm.base.base_color_texture, Some(texture));
    assert!(dm.base.cull_mode.is_none() && sm.base.cull_mode.is_none());
    assert!(dm.base.double_sided && sm.base.double_sided);
    // The actual material thresholds retain their inclusive boundary. Alpha
    // 112/255 glass and204/255 ice cast shadows but never write main depth.
    let survives = |mat: &StandardMaterial, alpha: f32| match mat.alpha_mode {
        AlphaMode::Mask(cutoff) => alpha >= cutoff,
        _ => panic!("companion must alpha-test"),
    };
    for (alpha, main, shadow) in [
        (0.05, false, false),
        (0.099, false, false),
        (0.1, false, true),
        (0.989, false, true),
        (0.99, true, true),
        (1., true, true),
        (112. / 255., false, true),
        (204. / 255., false, true),
    ] {
        assert_eq!(survives(&dm.base, alpha), main);
        assert_eq!(survives(&sm.base, alpha), shadow);
    }
    let prefix = app
        .world_mut()
        .resource_mut::<Assets<Mesh>>()
        .add(Cuboid::from_length(0.5));
    app.world_mut()
        .entity_mut(parent)
        .insert((Mesh3d(prefix.clone()), RenderLayers::layer(3)));
    app.update();
    assert_eq!(app.world().get::<Companions>(parent).unwrap().depth, depth);
    for child in [depth, shadow] {
        assert_eq!(app.world().get::<Mesh3d>(child).unwrap().0, prefix);
        assert_eq!(
            app.world().get::<RenderLayers>(child),
            Some(&RenderLayers::layer(3))
        );
    }
    // A live material edit must rebuild the shared cached pair, rather than
    // leave invisible depth/shadow children sampling a stale texture/color.
    let old_depth_material = app
        .world()
        .get::<MeshMaterial3d<DepthMaterial>>(depth)
        .unwrap()
        .0
        .clone();
    app.world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .get_mut(&material)
        .unwrap()
        .base_color = Color::srgba(0.3, 0.7, 0.4, 0.8);
    app.update(); // AssetEvent flush follows Update.
    app.update();
    let replacement = &app
        .world()
        .get::<MeshMaterial3d<DepthMaterial>>(depth)
        .unwrap()
        .0;
    assert_ne!(replacement, &old_depth_material);
    let refreshed = app
        .world()
        .resource::<Assets<DepthMaterial>>()
        .get(replacement)
        .unwrap();
    assert_eq!(refreshed.base.base_color, Color::srgba(0.3, 0.7, 0.4, 0.8));
    assert_eq!(refreshed.base.alpha_mode, AlphaMode::Mask(0.99));
    app.world_mut().entity_mut(parent).remove::<RenderLayers>();
    app.update();
    assert_eq!(
        app.world().get::<RenderLayers>(depth),
        Some(&RenderLayers::default())
    );
    app.world_mut().entity_mut(parent).despawn();
    assert!(app.world().get_entity(depth).is_err() && app.world().get_entity(shadow).is_err());
}
#[test]
fn late_material_load_and_removing_translucency_leave_no_stale_companions_or_shadow_suppression() {
    let mut app = app();
    let mesh = app
        .world_mut()
        .resource_mut::<Assets<Mesh>>()
        .add(Cuboid::from_length(1.));
    let handle = app
        .world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .reserve_handle();
    let parent = app
        .world_mut()
        .spawn((
            AlphaDepth,
            Mesh3d(mesh),
            MeshMaterial3d(handle.clone()),
            Transform::IDENTITY,
        ))
        .id();
    app.update();
    assert!(app.world().get::<Companions>(parent).is_none());
    app.world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .insert(
            handle.id(),
            StandardMaterial {
                alpha_mode: AlphaMode::Blend,
                ..default()
            },
        )
        .unwrap();
    app.update();
    let pair = app.world().get::<Companions>(parent).unwrap();
    let (depth, shadow) = (pair.depth, pair.shadow);
    app.world_mut().entity_mut(parent).remove::<AlphaDepth>();
    app.update();
    assert!(app.world().get_entity(depth).is_err() && app.world().get_entity(shadow).is_err());
    assert!(app.world().get::<Companions>(parent).is_none());
    assert!(app.world().get::<NotShadowCaster>(parent).is_none());
}
