//! Original InventoryUI dashboard and gestures. Inventory pages are accepted
//! replicas; only the cursor, selection, scroll and operation timers live here.
//!
mod api;
mod badge;
mod commands;
mod context;
mod controller;
mod expression;
mod feedback;
mod gesture;
mod hotbar;
mod input;
mod layout;
mod magazines;
mod model;
mod money;
mod paperdoll;
mod render;
mod scrollbar;
mod spin;
mod symbols;
mod tiles;
mod tooltip;
mod transfer;

use bevy::prelude::*;
#[derive(Resource, Default)]
pub(super) struct Typing(pub bool);
pub(super) struct InventoryPlugin;
impl Plugin for InventoryPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<controller::Menu>()
            .init_resource::<Typing>()
            .init_resource::<spin::capture::Lease>()
            .add_systems(PostUpdate, spin::capture::apply)
            .init_resource::<render::Art>()
            .init_resource::<badge::Badge>()
            .init_resource::<symbols::Symbols>()
            .init_resource::<tooltip::Hover>()
            .init_resource::<crate::character::nameplate::OriginalFont>()
            .add_plugins((
                paperdoll::PaperdollPlugin,
                paperdoll::stage::PortraitViewPlugin,
                feedback::FeedbackPlugin,
            ))
            .add_observer(controller::activate)
            .add_observer(context::slider_change)
            .add_observer(context::number_key)
            .add_systems(
                Update,
                context::focus
                    .in_set(super::UiUpdate)
                    .before(super::keyboard_ui),
            )
            .add_systems(
                Update,
                hotbar::input
                    .in_set(super::UiUpdate)
                    .after(super::keyboard_ui),
            )
            .add_systems(
                Update,
                (
                    controller::update,
                    badge::refresh,
                    render::build,
                    context::synchronize,
                    context::preview,
                    context::slider_appearance,
                    spin::appearance,
                    symbols::apply,
                    tooltip::update,
                    scrollbar::appearance,
                    render::appearance,
                    render::ghost,
                )
                    .chain()
                    .in_set(super::UiUpdate)
                    .after(super::keyboard_ui)
                    .before(super::rebuild_overlay)
                    .before(paperdoll::PortraitUpdate),
            );
    }
}

#[cfg(test)]
mod command_tests;
#[cfg(test)]
mod runtime_tests;
#[cfg(test)]
mod tests;

#[cfg(test)]
mod visual;
