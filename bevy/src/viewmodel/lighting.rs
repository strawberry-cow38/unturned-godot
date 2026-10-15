//! Viewmodel.MatchWorldLight: one camera-space sun and two readability fills.
mod world;
use super::{LAYER, PlayerCamera, ViewmodelCamera, ViewmodelFrame};
use bevy::{camera::visibility::RenderLayers, prelude::*};
pub(crate) use world::{NearbyLight, WorldLight};

#[derive(Component)]
struct ViewmodelLight(u8);

pub(super) struct LightingPlugin;
impl Plugin for LightingPlugin {
    fn build(&self, app: &mut App) {
        world::install(app);
        app.add_systems(
            PostUpdate,
            match_world
                .after(bevy::transform::TransformSystems::Propagate)
                .before(bevy::light::SimulationLightSystems::UpdateDirectionalLightCascades),
        );
    }
}

pub(super) fn spawn(commands: &mut Commands, frame: Entity, camera: Entity) {
    commands.entity(camera).insert(AmbientLight {
        color: Color::srgb(0.72, 0.72, 0.74),
        brightness: crate::hdr::ambient_brightness(1.),
        ..default()
    });
    for (index, (angles, energy)) in [
        (Vec3::new(-40., -25., 10.), 1.2),
        (Vec3::new(25., 165., 0.), 0.45),
        (Vec3::new(-65., 55., 0.), 0.35),
    ]
    .into_iter()
    .enumerate()
    {
        commands.spawn((
            Name::new(format!("Viewmodel light {index}")),
            ViewmodelLight(index as u8),
            DirectionalLight {
                illuminance: crate::hdr::directional_illuminance(energy),
                shadow_maps_enabled: false,
                ..default()
            },
            Transform::from_rotation(Quat::from_euler(
                EulerRot::YXZ,
                angles.y.to_radians(),
                angles.x.to_radians(),
                angles.z.to_radians(),
            )),
            RenderLayers::layer(LAYER),
            ChildOf(frame),
        ));
    }
}

fn match_world(
    main: Query<&GlobalTransform, (With<PlayerCamera>, Without<ViewmodelLight>)>,
    frames: Query<&GlobalTransform, (With<ViewmodelFrame>, Without<ViewmodelLight>)>,
    sun: Query<
        (&DirectionalLight, &GlobalTransform),
        (With<crate::environment::Sun>, Without<ViewmodelLight>),
    >,
    ambient: Option<Res<GlobalAmbientLight>>,
    mut cameras: Query<&mut AmbientLight, With<ViewmodelCamera>>,
    mut lights: Query<(
        &ViewmodelLight,
        &mut DirectionalLight,
        &mut Transform,
        &mut GlobalTransform,
    )>,
) {
    let (Ok(main), Ok(frame), Ok((sun, sun_pose))) = (main.single(), frames.single(), sun.single())
    else {
        return;
    };
    // The source calls MatchWorldLight last, overriding its older WorldSun path.
    let key = sun
        .illuminance
        .max(crate::hdr::directional_illuminance(0.22));
    let fill = (key / crate::hdr::directional_illuminance(1.2)).clamp(0.25, 1.);
    let direction = main.rotation().inverse() * *sun_pose.forward();
    let up = if direction.y.abs() > 0.995 {
        Vec3::NEG_Z
    } else {
        Vec3::Y
    };
    for (marker, mut light, mut local, mut global) in &mut lights {
        match marker.0 {
            0 => {
                light.illuminance = key;
                light.color = sun.color;
                local.rotation = Transform::IDENTITY.looking_to(direction, up).rotation;
            }
            1 => light.illuminance = crate::hdr::directional_illuminance(0.45 * fill),
            _ => light.illuminance = crate::hdr::directional_illuminance(0.35 * fill),
        }
        // Transform propagation already ran so the same frame's extraction sees
        // the new mapped sunlight, including head-look and overhead sun.
        *global = frame.mul_transform(*local);
    }
    if let Some(ambient) = ambient {
        for mut camera in &mut cameras {
            camera.color = ambient.color;
            camera.brightness = ambient.brightness.max(crate::hdr::ambient_brightness(0.18));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_key_fill_ambient_follow_world_and_keep_readability_at_night() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, TransformPlugin, LightingPlugin));
        app.insert_resource(GlobalAmbientLight {
            color: Color::srgb(0.1, 0.15, 0.25),
            brightness: crate::hdr::ambient_brightness(0.1),
            ..default()
        });
        let heading = Quat::from_rotation_y(1.1);
        let main = app
            .world_mut()
            .spawn((PlayerCamera, Transform::from_rotation(heading)))
            .id();
        let frame = app
            .world_mut()
            .spawn((ViewmodelFrame, Transform::IDENTITY, ChildOf(main)))
            .id();
        let camera = app
            .world_mut()
            .spawn((ViewmodelCamera, Camera3d::default()))
            .id();
        let sun = app
            .world_mut()
            .spawn((
                crate::environment::Sun,
                DirectionalLight {
                    color: Color::srgb(0.7, 0.8, 1.),
                    illuminance: crate::hdr::directional_illuminance(0.015),
                    ..default()
                },
                Transform::from_rotation(Quat::from_rotation_x(-0.8)),
            ))
            .id();
        spawn(&mut app.world_mut().commands(), frame, camera);
        app.update();
        let key = app
            .world_mut()
            .query::<(Entity, &ViewmodelLight)>()
            .iter(app.world())
            .find(|(_, light)| light.0 == 0)
            .unwrap()
            .0;
        let world_direction = *app.world().get::<GlobalTransform>(sun).unwrap().forward();
        let mapped = *app.world().get::<GlobalTransform>(key).unwrap().forward();
        assert!((world_direction - mapped).length() < 1e-6);
        let key_light = app.world().get::<DirectionalLight>(key).unwrap();
        assert_eq!(
            key_light.illuminance,
            crate::hdr::directional_illuminance(0.22)
        );
        assert!(!key_light.shadow_maps_enabled);
        assert_eq!(
            app.world().get::<AmbientLight>(camera).unwrap().brightness,
            crate::hdr::ambient_brightness(0.18)
        );
        for (marker, light, layers) in app
            .world_mut()
            .query::<(&ViewmodelLight, &DirectionalLight, &RenderLayers)>()
            .iter(app.world())
        {
            assert_eq!(*layers, RenderLayers::layer(LAYER));
            if marker.0 > 0 {
                let source = if marker.0 == 1 { 0.45 } else { 0.35 };
                assert_eq!(
                    light.illuminance,
                    crate::hdr::directional_illuminance(source * 0.25)
                );
            }
        }
        app.world_mut()
            .get_mut::<DirectionalLight>(sun)
            .unwrap()
            .illuminance = crate::hdr::directional_illuminance(1.35);
        app.world_mut().get_mut::<Transform>(sun).unwrap().rotation =
            Quat::from_rotation_x(-std::f32::consts::FRAC_PI_2);
        app.update();
        assert_eq!(
            app.world()
                .get::<DirectionalLight>(key)
                .unwrap()
                .illuminance,
            crate::hdr::directional_illuminance(1.35)
        );
        assert!(
            app.world()
                .get::<GlobalTransform>(key)
                .unwrap()
                .to_matrix()
                .is_finite()
        );
        assert!(
            (*app.world().get::<GlobalTransform>(key).unwrap().forward() - Vec3::NEG_Y).length()
                < 1e-6
        );
    }
}
