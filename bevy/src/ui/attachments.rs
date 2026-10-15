//! Original hold-T attachment rings. Options are authority-owned physical items;
//! widgets retain opaque gun/item tokens and never mutate a client inventory.
use super::*;
use crate::{
    bridge::CoreBridge,
    entities::ItemCatalog,
    player::{GameSnapshot, WorldSessionEpoch},
    viewmodel::{AttachmentAnchors, ViewmodelStatus},
    world::WorldConfig,
};
use bevy::window::PrimaryWindow;

pub struct AttachmentMenuPlugin;
impl Plugin for AttachmentMenuPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Menu>()
            .add_systems(
                Update,
                (input, refresh, build, orient_icons)
                    .chain()
                    .in_set(UiUpdate)
                    .after(keyboard_ui)
                    .before(rebuild_overlay),
            )
            .add_systems(
                PostUpdate,
                layout
                    .after(crate::viewmodel::ProjectAttachmentHooks)
                    .before(bevy::ui::UiSystems::Layout),
            )
            .add_observer(activate);
    }
}
#[derive(Resource, Default)]
struct Menu {
    options: Value,
    drawn: Value,
    pending: bool,
    drawn_pending: bool,
    drawn_busy: bool,
    selection: Value,
    epoch: u64,
    poll: f32,
}
#[derive(Component)]
struct Root;
#[derive(Component)]
struct Hook {
    slot: String,
    index: Option<usize>,
    count: usize,
    size: Vec2,
}
#[derive(Component)]
struct Action {
    name: String,
    args: Value,
}
#[derive(Component)]
struct Label(String);
#[derive(Component)]
struct Tooltip;
#[derive(Component)]
struct VerticalIcon;

fn input(
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    settings: Res<Settings>,
    mut state: ResMut<UiState>,
    snapshot: Res<GameSnapshot>,
    status: Res<ViewmodelStatus>,
    catalog: Res<ItemCatalog>,
    epoch: Res<WorldSessionEpoch>,
    mut menu: ResMut<Menu>,
) {
    let gun = catalog
        .items
        .get(&status.held)
        .is_some_and(|i| i.item_type == "gun");
    let weapon = &snapshot.0["weapon"];
    let held = settings.pressed(GameAction::AttachMenu, &keys, &mouse);
    if state.screen == Screen::Attachments {
        if !held
            || !gun
            || snapshot.0["player"]["alive"] == false
            || menu.epoch != epoch.0
            || menu.selection != weapon["selectionVersion"]
        {
            state.screen = Screen::Playing;
        }
    } else if state.screen == Screen::Playing
        && gun
        && status.ready
        && snapshot.0["connected"] == true
        && snapshot.0["player"]["alive"] == true
        && !weapon["reloading"].as_bool().unwrap_or(false)
        && !weapon["rechambering"].as_bool().unwrap_or(false)
        && settings.just_pressed(GameAction::AttachMenu, &keys, &mouse)
    {
        state.screen = Screen::Attachments;
        menu.selection = weapon["selectionVersion"].clone();
        menu.epoch = epoch.0;
        menu.options = Value::Null;
        menu.drawn = Value::Null;
        menu.pending = true;
        menu.poll = 0.;
    }
}
fn refresh(
    state: Res<UiState>,
    bridge: Res<CoreBridge>,
    time: Res<Time>,
    mut menu: ResMut<Menu>,
    mut model: ResMut<UiModel>,
) {
    if state.screen != Screen::Attachments {
        menu.options = Value::Null;
        menu.pending = false;
        return;
    }
    menu.poll -= time.delta_secs();
    if menu.poll > 0. {
        return;
    }
    menu.poll = 0.15;
    match bridge.request("attachmentOptions", json!({})) {
        Ok(reply) => {
            let options = &reply["result"];
            menu.pending = options["pending"] == true
                || (options["supported"] != false && options["selectionVersion"] != menu.selection);
            if !menu.pending {
                menu.options = options.clone();
            }
        }
        Err(e) => {
            menu.pending = true;
            model.notice(format!("Attachment options: {e}"));
        }
    }
}
fn weapon_busy(snapshot: &Value) -> bool {
    ["reloading", "unloading", "rechambering"]
        .iter()
        .any(|key| snapshot["weapon"][key] == true)
}
fn actions_busy(menu: &Menu, snapshot: &Value) -> bool {
    menu.pending || menu.options["busy"].as_bool().unwrap_or(true) || weapon_busy(snapshot)
}
fn request(options: &Value, name: &str, mut args: Value) -> Action {
    args["selectionVersion"] = options["selectionVersion"].clone();
    args["gunToken"] = options["gunToken"].clone();
    Action {
        name: name.into(),
        args,
    }
}
fn activate(
    event: On<Activate>,
    buttons: Query<&Action>,
    state: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    mut intents: MessageWriter<UiIntent>,
    mut menu: ResMut<Menu>,
) {
    if state.screen != Screen::Attachments
        || actions_busy(&menu, &snapshot.0)
        || snapshot.0["connected"] != true
        || snapshot.0["player"]["alive"] != true
        || menu.selection != snapshot.0["weapon"]["selectionVersion"]
    {
        return;
    }
    if let Ok(action) = buttons.get(event.entity) {
        if action.args["gunToken"] != menu.options["gunToken"]
            || action.args["selectionVersion"] != menu.selection
        {
            return;
        }
        intents.write(UiIntent::Action {
            name: action.name.clone(),
            args: action.args.clone(),
        });
        menu.poll = 0.;
    }
}
#[allow(clippy::too_many_arguments)]
fn build(
    mut commands: Commands,
    state: Res<UiState>,
    mut menu: ResMut<Menu>,
    roots: Query<Entity, With<Root>>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    catalog: Res<ItemCatalog>,
    snapshot: Res<GameSnapshot>,
    settings: Res<Settings>,
) {
    if state.screen != Screen::Attachments {
        for root in &roots {
            commands.entity(root).despawn();
        }
        menu.drawn = Value::Null;
        return;
    }
    let busy = actions_busy(&menu, &snapshot.0);
    if (menu.options.is_null() && !menu.pending)
        || (menu.options == menu.drawn
            && menu.pending == menu.drawn_pending
            && busy == menu.drawn_busy
            && !roots.is_empty())
    {
        return;
    }
    for root in &roots {
        commands.entity(root).despawn();
    }
    menu.drawn = menu.options.clone();
    menu.drawn_pending = menu.pending;
    menu.drawn_busy = busy;
    let options = &menu.options;
    commands.spawn((Root,Node{width:percent(100),height:percent(100),position_type:PositionType::Absolute,..default()},
        GlobalZIndex(58),BackgroundColor(Color::srgba(0.,0.,0.,0.35))))
        .with_children(|root| {
            root.spawn((text(format!("{}   |   Release {} to close", options["gunName"].as_str().unwrap_or("Attachments"),settings.bindings.get(&GameAction::AttachMenu).map_or_else(||"T".into(),Binding::label)),16.,TEXT),
                Node{position_type:PositionType::Absolute,left:px(24),top:px(84),..default()}));
            root.spawn((Tooltip,text("",14.,TEXT),Node{position_type:PositionType::Absolute,bottom:px(80),left:percent(30),..default()}));
            if menu.pending || options["supported"]==false {
                root.spawn((text(if menu.pending {"Loading attachments…"}else{"This server does not support attachment controls."},16.,TEXT),
                    Node{position_type:PositionType::Absolute,left:px(24),top:px(112),..default()}));
            }
            for slot in options["slots"].as_array().into_iter().flatten() {
                if slot["visible"]==false {continue;}
                let name=slot["name"].as_str().unwrap_or("");
                let rgba=slot["color"].as_array();
                let component=|i:usize|rgba.and_then(|v|v.get(i)).and_then(Value::as_f64).unwrap_or(1.) as f32;
                let tint=Color::srgba(component(0),component(1),component(2),component(3));
                let detachable=!busy&&slot["detachable"]==true;
                spawn_button(root,Hook{slot:name.into(),index:None,count:0,size:Vec2::splat(40.)},
                    &format!("{name}{}",if detachable {" — remove"}else{""}),
                    Some(format!("attach_{}.png",name.to_lowercase())),tint,None,false,
                    detachable.then(||request(options,"removeAttachment",json!({"slot":name}))),&assets,&config);
                let is_mag=slot["kind"]=="magazine"||slot["kind"]=="shell";
                let size=if is_mag {Vec2::new(44.*0.78,44.*1.34)}else{Vec2::splat(44.)};
                let mut choices=vec![];
                for c in slot["choices"].as_array().into_iter().flatten() {
                    let id=c["id"].as_u64().unwrap_or(0) as u16;
                    let item=catalog.items.get(&id);
                    let count=is_mag.then(||c["amount"].as_u64().unwrap_or(0));
                    let label=format!("{}{}",item.map_or("Attachment",|i|i.name.as_str()),count.map_or(String::new(),|n|format!(" — {n} rounds")));
                    let action=(!busy).then(||request(options,"installAttachment",json!({"slot":name,"page":c["page"],"x":c["x"],"y":c["y"],"instanceToken":c["instanceToken"]})));
                    choices.push((label,Some(format!("items/icons/{id}.png")),Color::WHITE,count,action));
                }
                for c in slot["shells"].as_array().into_iter().flatten() {
                    let id=c["id"].as_u64().unwrap_or(0) as u16;let count=c["count"].as_u64().unwrap_or(0);
                    let name=catalog.items.get(&id).map_or("Rounds",|i|i.name.as_str());
                    choices.push((format!("{name} — {count}"),Some(format!("items/icons/{id}.png")),
                        if c["selected"]==true {Color::srgb(0.4,0.85,0.45)}else{Color::WHITE},Some(count),
                        (!busy&&count>0).then(||request(options,"chooseShellType",json!({"itemId":id})))));
                }
                if slot["kind"]=="shell"&&snapshot.0["weapon"]["ammo"].as_i64().unwrap_or(0)>0 {
                    choices.push(("Unload".into(),None,Color::srgb(0.9,0.35,0.35),None,
                        (!busy).then(||request(options,"unloadShells",json!({})))));
                }
                let count=choices.len();
                for (index,(label,icon,tint,rounds,action)) in choices.into_iter().enumerate() {
                    spawn_button(root,Hook{slot:name.into(),index:Some(index),count,size},&label,icon,tint,rounds,is_mag,action,&assets,&config);
                }
            }
        });
}
#[allow(clippy::too_many_arguments)]
fn spawn_button(
    root: &mut ChildSpawnerCommands,
    hook: Hook,
    label: &str,
    icon: Option<String>,
    tint: Color,
    rounds: Option<u64>,
    vertical: bool,
    action: Option<Action>,
    assets: &AssetServer,
    config: &WorldConfig,
) {
    let size = hook.size;
    let enabled = action.is_some();
    let color = Color::srgba(0.08, 0.09, 0.11, if enabled { 0.88 } else { 0.55 });
    let mut button = root.spawn((
        hook,
        Label(label.into()),
        Hovered::default(),
        Node {
            position_type: PositionType::Absolute,
            width: px(size.x),
            height: px(size.y),
            justify_content: JustifyContent::Center,
            align_items: AlignItems::Center,
            border: UiRect::all(px(1)),
            border_radius: BorderRadius::all(px(4)),
            ..default()
        },
        BackgroundColor(color),
        ButtonBaseColor(color),
        BorderColor::all(EDGE),
    ));
    if let Some(action) = action {
        button.insert((Button, action));
    }
    button.with_children(|p| {
        if let Some(icon) = icon.filter(|path| config.content_root.join(path).is_file()) {
            let mut e = p.spawn((
                ImageNode {
                    color: tint,
                    ..ImageNode::new(assets.load(icon))
                },
                Node {
                    width: px(size.x - 6.),
                    height: px(size.x - 6.),
                    ..default()
                },
                Pickable::IGNORE,
            ));
            if vertical {
                e.insert((VerticalIcon, UiTransform::default()));
            }
        } else {
            p.spawn((text(label, 12., tint), Pickable::IGNORE));
        }
        if let Some(rounds) = rounds {
            p.spawn((
                text(rounds.to_string(), 11., Color::WHITE),
                Pickable::IGNORE,
                Node {
                    position_type: PositionType::Absolute,
                    bottom: px(0),
                    width: percent(100),
                    ..default()
                },
                TextLayout::justify(Justify::Center),
            ));
        }
    });
}
fn radius(count: usize) -> f32 {
    if count <= 1 {
        56.
    } else {
        56_f32.max(54. / (2. * (std::f32::consts::PI / count as f32).sin()))
    }
}
fn option_position(anchor: Vec2, size: Vec2, screen: Vec2, index: usize, count: usize) -> Vec2 {
    let radius = radius(count);
    let pad = radius + 28.;
    let centre = anchor.clamp(
        Vec2::splat(pad),
        (screen - Vec2::splat(pad)).max(Vec2::splat(pad)),
    );
    let theta =
        -std::f32::consts::FRAC_PI_2 + std::f32::consts::TAU * index as f32 / count.max(1) as f32;
    centre + Vec2::new(theta.cos(), theta.sin()) * radius - size * 0.5
}
fn layout(
    anchors: Res<AttachmentAnchors>,
    window: Query<&Window, With<PrimaryWindow>>,
    mut hooks: Query<(&Hook, &mut Node, &mut Visibility)>,
    hover: Query<(&Label, &Hovered)>,
    mut text: Query<&mut Text, With<Tooltip>>,
) {
    let Ok(window) = window.single() else {
        return;
    };
    for (hook, mut node, mut visibility) in &mut hooks {
        let Some(anchor) = anchors.0.get(&hook.slot) else {
            *visibility = Visibility::Hidden;
            continue;
        };
        *visibility = Visibility::Inherited;
        let p = hook.index.map_or(*anchor - hook.size * 0.5, |index| {
            option_position(
                *anchor,
                hook.size,
                window.resolution.size(),
                index,
                hook.count,
            )
        });
        node.left = px(p.x);
        node.top = px(p.y);
    }
    let label = hover
        .iter()
        .find(|(_, h)| h.0)
        .map_or("", |(l, _)| l.0.as_str());
    for mut t in &mut text {
        **t = label.into();
    }
}
pub(super) fn drawn_wide(image: &Image) -> bool {
    let mut lo = UVec2::splat(u32::MAX);
    let mut hi = UVec2::ZERO;
    for y in 0..image.height() {
        for x in 0..image.width() {
            if image.get_color_at(x, y).is_ok_and(|c| c.alpha() > 0.03) {
                lo = lo.min(UVec2::new(x, y));
                hi = hi.max(UVec2::new(x, y));
            }
        }
    }
    lo.x != u32::MAX && (hi.x - lo.x) as f32 > (hi.y - lo.y) as f32 * 1.65
}
fn orient_icons(
    mut commands: Commands,
    images: Res<Assets<Image>>,
    mut icons: Query<(Entity, &ImageNode, &mut UiTransform), With<VerticalIcon>>,
) {
    for (e, icon, mut transform) in &mut icons {
        if let Some(image) = images.get(&icon.image) {
            if drawn_wide(image) {
                // Source rotates CCW then flips horizontally: the combined
                // linear map is rotation +90 followed by a local X reflection.
                transform.rotation = Rot2::radians(std::f32::consts::FRAC_PI_2);
                transform.scale = Vec2::new(-1., 1.);
            }
            commands.entity(e).remove::<VerticalIcon>();
        }
    }
}

#[cfg(test)]
mod tests;
