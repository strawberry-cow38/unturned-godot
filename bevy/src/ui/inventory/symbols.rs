//! Godot's actual Linux fallback for the frozen-food snowflake. Keep Open Sans
//! first for all ordinary text; the embedded subset supplies only U+2744.
use bevy::prelude::*;

#[derive(Component)]
pub(super) struct SnowGlyph;
#[derive(Resource)]
pub(super) struct Symbols(Handle<Font>);
impl FromWorld for Symbols {
    fn from_world(world: &mut World) -> Self {
        Self(world.resource_mut::<Assets<Font>>().add(Font::from_bytes(
            include_bytes!("../../../assets/fonts/DejaVuSans_InventorySnow.ttf").to_vec(),
        )))
    }
}
pub(super) fn apply(
    original: Res<crate::character::nameplate::OriginalFont>,
    symbols: Res<Symbols>,
    mut labels: Query<&mut TextFont, Added<SnowGlyph>>,
) {
    for mut font in &mut labels {
        font.font = bevy::text::FontSource::list([original.0.clone(), symbols.0.clone()]);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn frozen_food_preserves_original_font_and_uses_actual_snow_fallback() {
        let mut app = App::new();
        app.init_resource::<Assets<Font>>()
            .init_resource::<crate::character::nameplate::OriginalFont>()
            .init_resource::<Symbols>()
            .add_systems(Update, apply);
        let label = app
            .world_mut()
            .spawn((Text::new("❄63%"), TextFont::default(), SnowGlyph))
            .id();
        app.update();
        assert_eq!(
            app.world().get::<TextFont>(label).unwrap().font,
            bevy::text::FontSource::list([
                app.world()
                    .resource::<crate::character::nameplate::OriginalFont>()
                    .0
                    .clone(),
                app.world().resource::<Symbols>().0.clone(),
            ])
        );
        assert_eq!(app.world().get::<Text>(label).unwrap().0, "❄63%");
    }
}
