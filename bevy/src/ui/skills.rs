//! Original SkillsUI graph. All cost/prerequisite verdicts come from the source
//! managed read model over the accepted owner snapshot; Learn is only an intent.
use super::*;
use crate::player::{GameSnapshot, WorldSessionEpoch};
use bevy::window::PrimaryWindow;
mod geometry;
mod render;
#[cfg(test)]
mod tests;
#[cfg(test)]
mod visual;

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct Tree {
    experience: u32,
    pillars: Vec<Pillar>,
    nodes: Vec<SkillNode>,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct Pillar {
    id: String,
    title: String,
    blurb: String,
    advanced: bool,
    column: u32,
    requires: Vec<String>,
    open: bool,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct SkillNode {
    id: String,
    title: String,
    blurb: String,
    pillar: String,
    tier: u32,
    cost: u32,
    requires: Vec<String>,
    grants: Vec<String>,
    learned: bool,
    can_take: bool,
    reason: String,
}
#[derive(Resource, Default)]
struct Menu {
    epoch: Option<u64>,
    tree: Tree,
    selected: Option<String>,
    pan: Vec2,
    dragging: bool,
    cursor: Option<Vec2>,
    viewport: Vec2,
}
#[derive(Component)]
enum Action {
    Select(String),
    Learn,
}
#[derive(Resource)]
struct Symbols(Handle<Font>);
impl FromWorld for Symbols {
    fn from_world(world: &mut World) -> Self {
        Self(world.resource_mut::<Assets<Font>>().add(Font::from_bytes(
            include_bytes!("../../assets/fonts/DejaVuSans_SkillSymbols.ttf").to_vec(),
        )))
    }
}
pub(super) struct SkillsPlugin;
impl Plugin for SkillsPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Menu>()
            .init_resource::<crate::character::nameplate::OriginalFont>()
            .init_resource::<Symbols>()
            .add_observer(activate)
            .add_systems(
                Update,
                (sync, input, render::build, render::appearance)
                    .chain()
                    .in_set(UiUpdate)
                    .before(rebuild_overlay),
            );
    }
}
fn sync(
    mut menu: ResMut<Menu>,
    model: Res<UiModel>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    windows: Query<&Window, With<PrimaryWindow>>,
) {
    if !model.connected || menu.epoch != Some(epoch.0) {
        *menu = Menu {
            epoch: Some(epoch.0),
            ..default()
        };
    }
    if let Ok(window) = windows.single() {
        let viewport = Vec2::new(window.width(), window.height());
        if menu.viewport != viewport {
            menu.viewport = viewport;
            menu.pan = geometry::Graph::new(&menu.tree)
                .center_if_small(menu.pan, geometry::Layout::new(viewport).clip.size());
        }
    }
    if model.connected {
        let tree = serde_json::from_value::<Tree>(snapshot.0["skills"]["tree"].clone())
            .unwrap_or_default();
        if menu.tree != tree {
            let graph = geometry::Graph::new(&tree);
            let layout_changed = graph != geometry::Graph::new(&menu.tree);
            menu.tree = tree;
            // Source Refresh repaints XP/learned state without moving the graph.
            // Only Layout/resize recenters a graph narrower than its viewport.
            if layout_changed {
                menu.pan = graph
                    .center_if_small(menu.pan, geometry::Layout::new(menu.viewport).clip.size());
            }
            if menu
                .selected
                .as_ref()
                .is_some_and(|id| !menu.tree.nodes.iter().any(|n| n.id == *id))
            {
                menu.selected = None;
            }
        }
    }
}
fn input(
    mut menu: ResMut<Menu>,
    state: Res<UiState>,
    mouse: Res<ButtonInput<MouseButton>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    roots: Query<&UiTransform, With<render::Root>>,
) {
    if state.screen != Screen::Skills {
        menu.dragging = false;
        menu.cursor = None;
        return;
    }
    let Ok(window) = windows.single() else {
        return;
    };
    let layout = geometry::Layout::new(menu.viewport);
    let graph = geometry::Graph::new(&menu.tree);
    let slide = roots.single().ok().map_or(0., |t| match t.translation.y {
        Val::Px(value) => value,
        _ => 0.,
    });
    let cursor = window.cursor_position();
    if !mouse.pressed(MouseButton::Left) {
        menu.dragging = false;
    }
    if let Some(at) = cursor {
        let panel_at = at - Vec2::Y * slide - layout.panel.min;
        let in_graph = panel_at - layout.clip.min - menu.pan;
        // Source node Buttons stop left-clicks; only exposed graph canvas drags.
        let node_hit = graph
            .nodes
            .values()
            .any(|pos| Rect::from_corners(*pos, *pos + geometry::NODE).contains(in_graph));
        if mouse.just_pressed(MouseButton::Left) && !node_hit && layout.clip.contains(panel_at) {
            menu.dragging = true;
            menu.cursor = Some(at);
        }
        if menu.dragging {
            if let Some(previous) = menu.cursor {
                menu.pan += at - previous;
            }
            menu.pan = graph.clamp(menu.pan, layout.clip.size());
        }
    }
    menu.cursor = cursor;
}
fn activate(
    event: On<Activate>,
    actions: Query<&Action>,
    state: Res<UiState>,
    model: Res<UiModel>,
    mut menu: ResMut<Menu>,
    mut intents: MessageWriter<UiIntent>,
) {
    if state.screen != Screen::Skills || !model.connected || !model.alive {
        return;
    }
    let Ok(action) = actions.get(event.entity) else {
        return;
    };
    match action {
        Action::Select(id) => menu.selected = Some(id.clone()),
        Action::Learn => {
            if let Some(node) = menu
                .tree
                .nodes
                .iter()
                .find(|n| Some(&n.id) == menu.selected.as_ref() && n.can_take)
            {
                intents.write(UiIntent::Action {
                    name: "learnSkillNode".into(),
                    args: json!({"nodeId":node.id}),
                });
            }
        }
    }
}
