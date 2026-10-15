//! The shared MenuSwoop.cs clock. UI state/input closes immediately; only the
//! outgoing pixels remain until the original 85 ms transition finishes.
use super::{Screen, UiButton, UiModel, UiState, backdrop::Backdrop};
use bevy::{picking::Pickable, prelude::*, ui_widgets::Button};

const SECONDS: f32 = 0.085;
const RISE: f32 = 54.;
const SCREENS: [Screen; 3] = [Screen::Inventory, Screen::Crafting, Screen::Information];

#[derive(Component, Clone, Copy)]
pub(super) struct Swoop(pub Screen);
#[derive(Component)]
struct Retiring;
#[derive(Component)]
struct Settled;

#[derive(Clone, Copy, Default)]
struct Clock {
    t: f32,
    direction: i8,
    open: bool,
}
impl Clock {
    fn step(&mut self, open: bool, delta: f32) {
        if self.open != open {
            self.direction = if open { 1 } else { -1 };
            self.open = open;
        }
        if self.direction != 0 {
            self.t = (self.t + self.direction as f32 * delta / SECONDS).clamp(0., 1.);
            if self.t == 0. || self.t == 1. {
                self.direction = 0;
            }
        }
    }
    fn alpha(self) -> f32 {
        // At zero/one both source curves agree, including the idle endpoints.
        if self.direction < 0 {
            self.t * self.t
        } else {
            1. - (1. - self.t).powi(3)
        }
    }
}
#[derive(Resource, Default)]
struct Clocks([Clock; 3], bool);

pub(super) struct SwoopPlugin;
impl Plugin for SwoopPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Clocks>()
            .add_systems(
                Update,
                advance
                    .in_set(super::UiUpdate)
                    .after(super::rebuild_overlay),
            )
            .add_systems(
                PostUpdate,
                animate
                    .before(super::backdrop::BackdropRouting)
                    .before(bevy::ui::UiSystems::Layout),
            );
    }
}

fn advance(
    time: Res<Time>,
    model: Res<UiModel>,
    mut state: ResMut<UiState>,
    mut clocks: ResMut<Clocks>,
) {
    if !model.connected || !model.alive {
        *clocks = default();
    } else {
        // Source MapUI.Close(captureMouse:false) is used for tab navigation;
        // its eventual Closed callback must not recapture another menu's mouse.
        if clocks.0[2].open && state.screen != Screen::Information {
            clocks.1 = state.screen == Screen::Playing;
        } else if state.screen == Screen::Information {
            clocks.1 = false;
        }
        for (clock, screen) in clocks.0.iter_mut().zip(SCREENS) {
            clock.step(screen == state.screen, time.delta_secs());
        }
    }
    state.map_closing = model.connected
        && model.alive
        && clocks.1
        && state.screen == Screen::Playing
        && clocks.0[2].t > 0.;
}

/// Builders may replace widgets for changed snapshots without restarting their
/// screen's clock. On a tab change remove the builder marker, retaining only a
/// presentation root that cannot receive further actions.
pub(super) fn replace<T: Component>(
    commands: &mut Commands,
    entity: Entity,
    swoop: Option<&Swoop>,
    state: &UiState,
    model: &UiModel,
) {
    if swoop.is_some_and(|s| s.0 != state.screen) && model.connected && model.alive {
        commands.entity(entity).remove::<T>().insert(Retiring);
    } else {
        commands.entity(entity).despawn();
    }
}

// Godot parent Modulate multiplies the original child alpha. Bevy UI paints
// children independently, so retain each source color and the last applied
// value. A live hover/status color change becomes the new source color; the
// animation never repeatedly multiplies its own previous result.
#[derive(Clone, Copy)]
struct Tint {
    source: Color,
    applied: Color,
}
fn tint(slot: &mut Option<Tint>, color: &mut Color, alpha: f32) {
    let value = slot.get_or_insert(Tint {
        source: *color,
        applied: *color,
    });
    if *color != value.applied {
        value.source = *color;
    }
    *color = value.source.with_alpha(value.source.alpha() * alpha);
    value.applied = *color;
}
#[derive(Component, Default)]
struct Paint {
    background: Option<Tint>,
    border: [Option<Tint>; 4],
    text: Option<Tint>,
    image: Option<Tint>,
    shadow: Vec<Option<Tint>>,
}
fn paint(world: &mut World, entity: Entity, alpha: f32, closing: bool) {
    let mut entity = world.entity_mut(entity);
    if closing {
        entity
            .insert(Pickable::IGNORE)
            .remove::<(Button, UiButton)>();
    }
    // Fully-open widgets need no persistent color override.
    if alpha == 1. && !entity.contains::<Paint>() {
        return;
    }
    let mut base = entity.take::<Paint>().unwrap_or_default();
    if let Some(mut color) = entity.get_mut::<BackgroundColor>() {
        tint(&mut base.background, &mut color.0, alpha);
    }
    if let Some(mut color) = entity.get_mut::<BorderColor>() {
        tint(&mut base.border[0], &mut color.top, alpha);
        tint(&mut base.border[1], &mut color.right, alpha);
        tint(&mut base.border[2], &mut color.bottom, alpha);
        tint(&mut base.border[3], &mut color.left, alpha);
    }
    if let Some(mut color) = entity.get_mut::<TextColor>() {
        tint(&mut base.text, &mut color.0, alpha);
    }
    if let Some(mut image) = entity.get_mut::<ImageNode>() {
        tint(&mut base.image, &mut image.color, alpha);
    }
    if let Some(mut shadows) = entity.get_mut::<BoxShadow>() {
        base.shadow.resize(shadows.0.len(), None);
        for (color, slot) in shadows.0.iter_mut().zip(&mut base.shadow) {
            tint(slot, &mut color.color, alpha);
        }
    }
    if alpha != 1. {
        entity.insert(base);
    }
}

fn animate(world: &mut World) {
    let selected = world.resource::<UiState>().screen;
    let model = world.resource::<UiModel>();
    let live = model.connected && model.alive;
    let clocks = world.resource::<Clocks>().0;
    let roots: Vec<_> = world
        .query::<(Entity, &Swoop, Has<Retiring>)>()
        .iter(world)
        .map(|(entity, swoop, retiring)| (entity, swoop.0, retiring))
        .collect();
    for (root, screen, retiring) in roots {
        let Some(index) = SCREENS.iter().position(|s| *s == screen) else {
            continue;
        };
        let clock = clocks[index];
        if !live || (retiring && (clock.t == 0. || selected == screen)) {
            world.despawn(root);
            continue;
        }
        let alpha = clock.alpha();
        let closing = retiring || selected != screen;
        if alpha == 1. && !closing && world.get::<Settled>(root).is_some() {
            continue;
        }
        {
            let mut entity = world.entity_mut(root);
            if let Some(mut transform) = entity.get_mut::<UiTransform>() {
                transform.translation.y = px((1. - alpha) * RISE);
            }
            if closing && screen == Screen::Information {
                // MapUI.Close ducks its departing canvas under HUD layer10.
                entity.remove::<Backdrop>().insert(GlobalZIndex(9));
            } else {
                // Out hides the backdrop immediately; retain layer11 routing
                // for Inventory/Crafting while their panels finish fading.
                entity.insert(Backdrop::dashboard(if closing { 0. } else { alpha }));
            }
        }
        let mut nodes = vec![root];
        let mut at = 0;
        while at < nodes.len() {
            let entity = nodes[at];
            if let Some(children) = world.get::<Children>(entity) {
                nodes.extend(children.iter());
            }
            paint(world, entity, alpha, closing);
            at += 1;
        }
        if alpha == 1. && !closing {
            world.entity_mut(root).insert(Settled);
        } else {
            world.entity_mut(root).remove::<Settled>();
        }
    }
}

#[cfg(test)]
mod tests;
#[cfg(test)]
mod visual;
