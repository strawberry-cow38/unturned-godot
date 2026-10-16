//! MainMenuPlay.cs + MainMenuAdvanced.cs: the singleplayer map selector. Each
//! entry drives the same world entry the CLI uses (`--map`, `--new-island`,
//! `--island`, `--map Procedural --seed`); world rules (Zombies) travel to the
//! authority in the session `init`, as WorldBuilder.ZombiesOverride does.
use super::godot::{self, Ctx, GOLD, Skin, look};
use super::*;
use std::path::{Path, PathBuf};

/// (name, art key, playable, English.dat description) in MainMenuPlay.OfficialMaps order.
pub const OFFICIAL: [(&str, &str, bool, &str); 8] = [
    ("PEI", "pei", true, "Sunny island off the East coast of Canada. Several small civilian towns with minor military presence. Tourist attractions include beaches, castles and sailing. Recommended for new survivors."),
    ("Washington", "washington", true, "Rainy state South-West of Canada. Several large civilian towns with extensive military presence. Tourist attractions include Seattle, golf and racing. Recommended for intermediate survivors."),
    ("Russia", "russia", false, "Multi-biome country neighboring Canada. Huge diversity of civilian destinations with varying military presence. Tourist attractions include historical monuments, picturesque countrysides and rock climbing. Recommended for experienced survivors."),
    ("Germany", "germany", false, "Mountainous country North of Canada. Modernized cities with active military presence. Tourist attractions include breathtaking vistas, hiking the alpine trails and the local celebration of Oktoberfest. Recommended for intermediate survivors."),
    ("Yukon", "yukon", true, "Harsh, freezing territory in North-West Canada. Barren frozen wasteland scattered with camps and cabins. Tourist attractions include skiing, skating and train spotting. Recommended for experienced survivors."),
    ("Hawaii", "", false, "Not installed."),
    ("Greece", "", false, "Not installed."),
    ("A6 Polaris", "", false, "Not installed."),
];
pub const GENERATE: &str = "Generate Island";
const GENERATE_DESC: &str = "A procedurally generated island: coastline, hills, and a network of towns, military bases and construction sites joined by roads, trails and rail. The same seed always builds the same island.";
const PLAYGROUND_DESC: &str = "The gun range -- an open sandbox to test weapons and mechanics. No survival, no map: just spawn in with everything.";
const INFINITE_DESC: &str = "PROTOTYPE. An endless generated world -- continents, mountain belts, forests -- streamed in around you as you go. Same seed, same world. Singleplayer only, no zombies or loot yet.";
pub const DIFFICULTIES: [&str; 3] = ["Easy", "Normal", "Hard"];
pub const LOOT: [&str; 3] = ["Sparse", "Normal", "Abundant"];
pub const DAY: [&str; 4] = ["Short", "Default", "Long", "Endless Day"];
pub const COMBAT: [&str; 2] = ["PvE", "PvP"];
pub const ZOMBIES: [&str; 2] = ["Normal", "Off"];

/// map_settings.cfg [map]. Only Zombies changes the world; the rest are the
/// source's persisted placeholders.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct MapOptions {
    pub difficulty: usize,
    pub zombies: usize,
    pub loot: usize,
    pub day: usize,
    pub combat: usize,
    pub cheats: bool,
    pub permadeath: bool,
}
impl Default for MapOptions {
    fn default() -> Self {
        Self { difficulty: 1, zombies: 0, loot: 1, day: 1, combat: 0, cheats: false, permadeath: false }
    }
}
use serde::Serialize;
impl MapOptions {
    pub fn path() -> PathBuf {
        #[cfg(test)]
        return Settings::path().parent().unwrap_or(Path::new(".")).join("map_settings.json");
        #[allow(unreachable_code)]
        if let Some(path) = std::env::var_os("UG_BEVY_MAP_SETTINGS") {
            return path.into();
        }
        Settings::path().parent().unwrap_or(Path::new(".")).join("map_settings.json")
    }
    /// LoadMapSettings + OptionRow's clamp (an older, longer value list is safe).
    pub fn load() -> Self {
        let mut options: Self = std::fs::read_to_string(Self::path())
            .ok()
            .and_then(|json| serde_json::from_str(&json).ok())
            .unwrap_or_default();
        options.difficulty = options.difficulty.min(DIFFICULTIES.len() - 1);
        options.zombies = options.zombies.min(ZOMBIES.len() - 1);
        options.loot = options.loot.min(LOOT.len() - 1);
        options.day = options.day.min(DAY.len() - 1);
        options.combat = options.combat.min(COMBAT.len() - 1);
        options
    }
    pub fn save(&self) -> Result<(), String> {
        let path = Self::path();
        if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let temp = path.with_extension("json.tmp");
        std::fs::write(&temp, serde_json::to_vec_pretty(self).map_err(|e| e.to_string())?)
            .and_then(|_| std::fs::rename(&temp, &path))
            .map_err(|e| e.to_string())
    }
    /// WorldBuilder.ZombiesDisabled as decided by the menu row.
    pub fn zombies_enabled(&self) -> bool {
        self.zombies == 0
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Entry {
    Official(usize),
    Generate,
    Playground,
    Infinite,
}
impl Entry {
    fn official(self) -> Option<usize> {
        match self {
            Self::Official(i) => Some(i),
            _ => None,
        }
    }
}

#[derive(Debug)]
pub struct Selector {
    pub loaded: bool,
    pub options: MapOptions,
    pub entry: Entry,
    /// SelectedMapFolder: only retail rows change it (Reset Data targets it).
    pub folder: String,
    pub category: u8,
    pub config_open: bool,
    pub advanced: bool,
    pub seed: String,
    pub seed_focus: bool,
    pub lakes: bool,
    pub reset_armed: bool,
    pub delete_armed: Option<String>,
    /// Replaces the description, as PlaySelected/OnResetPressed write `_descLabel`.
    pub note: Option<String>,
}
impl Default for Selector {
    fn default() -> Self {
        Self {
            loaded: false,
            options: MapOptions::default(),
            entry: Entry::Official(0),
            folder: "PEI".into(),
            category: 0,
            config_open: false,
            advanced: false,
            seed: random_seed().to_string(),
            seed_focus: false,
            lakes: false,
            reset_armed: false,
            delete_armed: None,
            note: None,
        }
    }
}
/// `GD.Randi() & 0x7FFFFFFF`: the first island is never a fixed default.
pub fn random_seed() -> i32 {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.as_nanos());
    let mut x = (nanos as u64) ^ ((std::process::id() as u64) << 32) ^ 0x9E37_79B9_7F4A_7C15;
    x ^= x >> 33;
    x = x.wrapping_mul(0xff51_afd7_ed55_8ccd);
    x ^= x >> 33;
    (x & 0x7FFF_FFFF) as i32
}
impl Selector {
    pub fn ensure_loaded(&mut self) {
        if !self.loaded {
            self.loaded = true;
            self.options = MapOptions::load();
        }
    }
    fn select(&mut self, entry: Entry) {
        self.entry = entry;
        if let Some(i) = entry.official() {
            self.folder = OFFICIAL[i].0.into();
        }
        self.note = None;
        // DisarmReset: an armed delete never survives a selection change.
        self.reset_armed = false;
    }
    pub fn name(&self) -> &str {
        match self.entry {
            Entry::Generate => GENERATE,
            Entry::Playground => "Playground",
            Entry::Infinite => "Infinite World",
            e => OFFICIAL[e.official().unwrap_or(0)].0,
        }
    }
    fn description(&self) -> &str {
        if let Some(note) = &self.note {
            return note;
        }
        match self.entry {
            Entry::Generate => GENERATE_DESC,
            Entry::Playground => PLAYGROUND_DESC,
            Entry::Infinite => INFINITE_DESC,
            e => OFFICIAL[e.official().unwrap_or(0)].3,
        }
    }
    fn generator_row(&self) -> bool {
        self.category == 0 && matches!(self.entry, Entry::Generate | Entry::Infinite)
    }
}

/// Saved generated islands live here, one original editor-map directory each.
pub fn maps_root() -> PathBuf {
    #[cfg(test)]
    return Settings::path().parent().unwrap_or(Path::new(".")).join("maps");
    #[allow(unreachable_code)]
    if let Some(path) = std::env::var_os("UG_BEVY_MAPS") {
        return path.into();
    }
    Settings::path().parent().unwrap_or(Path::new(".")).join("maps")
}
/// EditorMaps.Sanitise: letters, digits, space, dash, underscore; 40 max.
pub fn sanitise(raw: &str) -> Option<String> {
    let mut out = String::new();
    for c in raw.trim().chars() {
        if c.is_alphanumeric() || c == '-' || c == '_' || c == ' ' {
            out.push(c);
        }
        if out.chars().count() >= 40 {
            break;
        }
    }
    let out = out.trim().to_owned();
    (!out.is_empty()).then_some(out)
}
/// EditorMaps.Unique: "Island 7", "Island 7 2", ... never reusing a directory.
pub fn unique(root: &Path, want: &str) -> String {
    let base = sanitise(want).unwrap_or_else(|| "New Map".into());
    let taken = |name: &str| {
        std::fs::read_dir(root).is_ok_and(|dir| {
            dir.flatten()
                .any(|e| e.file_name().to_string_lossy().eq_ignore_ascii_case(name))
        })
    };
    if !taken(&base) {
        return base;
    }
    (2..1000)
        .map(|i| format!("{base} {i}"))
        .find(|c| !taken(c))
        .unwrap_or(base)
}
fn proc_marker(dir: &Path) -> Option<(String, PathBuf)> {
    let manifest: Value =
        serde_json::from_str(&std::fs::read_to_string(dir.join("finite.json")).ok()?).ok()?;
    let name = manifest["name"].as_str()?.to_owned();
    let marker = dir.join("objects").join(format!("editor_{name}_proc.txt"));
    marker.is_file().then_some((name, marker))
}
/// EditorMaps.ListProc: generated maps (by their seed marker), newest first.
pub fn list_islands(root: &Path) -> Vec<(String, i32, PathBuf)> {
    let mut found = Vec::new();
    for entry in std::fs::read_dir(root).into_iter().flatten().flatten() {
        let dir = entry.path();
        let Some((name, marker)) = proc_marker(&dir) else { continue };
        let Some(seed) = std::fs::read_to_string(&marker).ok().and_then(|s| s.trim().parse::<i32>().ok()) else {
            continue;
        };
        let when = std::fs::metadata(&marker).and_then(|m| m.modified()).ok();
        found.push((name, seed, dir, when));
    }
    found.sort_by(|a, b| b.3.cmp(&a.3));
    found.into_iter().map(|(n, s, d, _)| (n, s, d)).collect()
}
/// EditorMaps.Delete for a generated map: only a directory carrying the seed
/// marker is removed, so a hand-built map cannot be erased by this row.
pub fn delete_island(dir: &Path) -> Result<(), String> {
    if proc_marker(dir).is_none() {
        return Err("not a generated island".into());
    }
    std::fs::remove_dir_all(dir).map_err(|e| e.to_string())
}

/// World entry requested by the selector, performed after one rendered frame
/// so the original loading text is visible during blocking generation.
#[derive(Resource, Default, Debug)]
pub struct Pending {
    pub request: Option<FiniteRequest>,
    frames: u8,
}
#[derive(Clone, Debug, PartialEq)]
pub struct FiniteRequest {
    pub directory: PathBuf,
    pub create: bool,
    pub name: String,
    pub seed: i32,
    pub lakes: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Opt {
    Difficulty,
    Zombies,
    Loot,
    Day,
    Combat,
    Cheats,
    Permadeath,
}
#[derive(Component, Clone, Debug)]
pub enum SpAction {
    Select(Entry),
    Category(u8),
    Play,
    Step(Opt, i32),
    Toggle(Opt),
    Config,
    Advanced,
    Reset,
    Roll,
    Lakes,
    SeedFocus,
    OpenIsland(PathBuf),
    DeleteIsland(String, PathBuf),
    Back,
}

pub(super) struct SingleplayerPlugin;
impl Plugin for SingleplayerPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Pending>()
            .add_observer(activate)
            .add_systems(Update, perform.after(UiUpdate));
    }
}

fn values(opt: Opt) -> &'static [&'static str] {
    match opt {
        Opt::Difficulty => &DIFFICULTIES,
        Opt::Zombies => &ZOMBIES,
        Opt::Loot => &LOOT,
        Opt::Day => &DAY,
        Opt::Combat => &COMBAT,
        _ => &[],
    }
}
fn index(o: &mut MapOptions, opt: Opt) -> &mut usize {
    match opt {
        Opt::Difficulty => &mut o.difficulty,
        Opt::Zombies => &mut o.zombies,
        Opt::Loot => &mut o.loot,
        Opt::Day => &mut o.day,
        _ => &mut o.combat,
    }
}

pub(super) fn activate(
    event: On<Activate>,
    actions: Query<&SpAction>,
    mut state: ResMut<UiState>,
    mut options: ResMut<crate::LaunchOptions>,
    mut intents: MessageWriter<UiIntent>,
    mut pending: ResMut<Pending>,
    mut model: ResMut<UiModel>,
    world: Option<ResMut<crate::world::WorldState>>,
) {
    let Ok(action) = actions.get(event.entity) else {
        return;
    };
    let ui = &mut *state;
    let sp = &mut ui.menu.sp;
    sp.ensure_loaded();
    if !matches!(action, SpAction::SeedFocus) {
        sp.seed_focus = false;
    }
    if !matches!(action, SpAction::DeleteIsland(..)) {
        sp.delete_armed = None;
    }
    match action {
        SpAction::Select(entry) => {
            sp.select(*entry);
            if let Some(i) = entry.official() {
                ui.map = OFFICIAL[i].0.into();
            }
        }
        SpAction::Category(category) => {
            sp.category = *category;
        }
        SpAction::Step(opt, delta) => {
            let n = values(*opt).len() as i32;
            let value = index(&mut sp.options, *opt);
            *value = ((*value as i32 + delta).rem_euclid(n)) as usize;
            let _ = sp.options.save();
        }
        SpAction::Toggle(opt) => {
            match opt {
                Opt::Cheats => sp.options.cheats = !sp.options.cheats,
                _ => sp.options.permadeath = !sp.options.permadeath,
            }
            let _ = sp.options.save();
        }
        SpAction::Config => sp.config_open = !sp.config_open,
        SpAction::Advanced => sp.advanced = !sp.advanced,
        SpAction::Roll => sp.seed = random_seed().to_string(),
        SpAction::Lakes => sp.lakes = !sp.lakes,
        SpAction::SeedFocus => sp.seed_focus = true,
        SpAction::Reset => {
            let path = crate::player::save_path(&sp.folder);
            if !path.is_file() {
                sp.reset_armed = false;
            } else if !sp.reset_armed {
                sp.reset_armed = true;
            } else {
                sp.reset_armed = false;
                sp.note = Some(match std::fs::remove_file(&path) {
                    Ok(()) => format!("Deleted the saved world for {}.", sp.name()),
                    Err(_) => format!("Could not delete the save at {}.", path.display()),
                });
            }
        }
        SpAction::DeleteIsland(name, dir) => {
            if sp.delete_armed.as_deref() == Some(name.as_str()) {
                sp.delete_armed = None;
                if let Err(error) = delete_island(dir) {
                    sp.note = Some(format!("Could not delete {name}: {error}"));
                }
            } else {
                sp.delete_armed = Some(name.clone());
            }
        }
        SpAction::OpenIsland(dir) => {
            options.0.zombies = sp.options.zombies_enabled();
            pending.request = Some(FiniteRequest {
                directory: dir.clone(),
                create: false,
                name: String::new(),
                seed: 0,
                lakes: false,
            });
            pending.frames = 0;
            model.status = format!("Loading {}", dir.file_name().map_or_else(String::new, |n| n.to_string_lossy().into_owned()));
            ui.screen = Screen::Connecting;
        }
        SpAction::Back => {
            sp.advanced = false;
            ui.menu.page = super::main_menu::Page::Dashboard;
        }
        SpAction::Play => {
            // The one real gameplay option applies before any world build.
            options.0.zombies = sp.options.zombies_enabled();
            match sp.entry {
                Entry::Playground => {
                    sp.note = Some("Playground (the gun range) is not ported to the native build yet.".into());
                }
                Entry::Infinite | Entry::Generate => {
                    // Read the field, never a stale last-good seed.
                    let Ok(seed) = sp.seed.trim().parse::<i32>() else {
                        sp.note = Some("Seed must be a whole number.".into());
                        return;
                    };
                    if sp.entry == Entry::Infinite {
                        // The Play handler keys rebuilds on the map name; a new
                        // seed under the same "Procedural" name must rebuild too.
                        if options.0.seed != seed || options.0.map != "Procedural" {
                            if let Some(mut world) = world {
                                world.ready = false;
                            }
                        }
                        options.0.seed = seed;
                        intents.write(UiIntent::Play { map: "Procedural".into() });
                    } else {
                        let root = maps_root();
                        let name = unique(&root, &format!("Island {seed}"));
                        model.status = format!("Generating {name}");
                        pending.request = Some(FiniteRequest {
                            directory: root.join(&name),
                            create: true,
                            name,
                            seed,
                            lakes: sp.lakes,
                        });
                        pending.frames = 0;
                        ui.screen = Screen::Connecting;
                    }
                }
                entry => {
                    let (name, _, playable, _) = OFFICIAL[entry.official().unwrap_or(0)];
                    if !playable {
                        sp.note = Some(format!(
                            "{name} isn't ported yet \u{2014} only PEI is playable right now."
                        ));
                    } else {
                        intents.write(UiIntent::Play { map: name.into() });
                    }
                }
            }
        }
    }
}

/// Generate or reopen the finite map, then enter it through the Play intent.
fn perform(
    mut pending: ResMut<Pending>,
    bridge: Res<crate::bridge::CoreBridge>,
    mut options: ResMut<crate::LaunchOptions>,
    mut config: ResMut<crate::world::WorldConfig>,
    mut state: ResMut<UiState>,
    mut model: ResMut<UiModel>,
    mut intents: MessageWriter<UiIntent>,
    mut world: ResMut<crate::world::WorldState>,
) {
    let Some(request) = pending.request.clone() else {
        return;
    };
    if pending.frames < 2 {
        pending.frames += 1;
        return;
    }
    pending.request = None;
    let entry = crate::world::finite::bootstrap::Entry {
        directory: request.directory.clone(),
        create: request.create,
        name: request.name.clone(),
        seed: request.seed,
        tiles: [3, 3],
        lakes: request.lakes,
    };
    let result = crate::world::finite::bootstrap::open(
        &bridge,
        &options.0.content,
        options.0.unturned.as_deref(),
        &entry,
        &mut config,
    );
    match result {
        Ok((map, seed)) => {
            // A regenerated island can reuse a deleted island's name: rebuild
            // from the files just written instead of trusting the map name.
            if request.create || options.0.island.as_ref() != Some(&request.directory) {
                world.ready = false;
            }
            options.0.island = Some(request.directory);
            options.0.new_island = None;
            options.0.seed = seed;
            intents.write(UiIntent::Play { map });
        }
        Err(error) => {
            let message = error.to_string();
            model.status = message.clone();
            state.screen = Screen::MainMenu;
            state.menu.sp.note = Some(message);
        }
    }
}

/// Seed LineEdit typing while focused; Enter/Escape release focus.
pub(super) fn typed(state: &mut UiState, characters: &mut MessageReader<KeyboardInput>) {
    for input in characters.read() {
        if !input.state.is_pressed() {
            continue;
        }
        let sp = &mut state.menu.sp;
        match &input.logical_key {
            Key::Backspace => {
                sp.seed.pop();
            }
            Key::Enter | Key::Escape => sp.seed_focus = false,
            Key::Character(value) => {
                if sp.seed.chars().count() + value.chars().count() <= 24
                    && !value.chars().any(char::is_control)
                {
                    sp.seed.push_str(value);
                }
            }
            _ => {}
        }
    }
}

fn option_row(p: &mut ChildSpawnerCommands, c: &Ctx, label: &str, opt: Opt, value: usize) {
    let values = values(opt);
    let value = values[value.min(values.len() - 1)];
    p.spawn(Node { width: c.px(340.), height: c.px(32.), column_gap: c.px(6.), align_items: AlignItems::Center, ..default() })
        .with_children(|row| {
            row.spawn((c.text(label, 15., Color::WHITE), Node { width: c.px(110.), ..default() }));
            let small = || Node { width: c.px(30.), height: c.px(30.), ..default() };
            godot::label_button(row, c, look(Skin::Default), small(), "<", 16., Some(SpAction::Step(opt, -1)));
            row.spawn((
                c.text(value, 15., Color::srgb(0.95, 0.95, 0.9)),
                TextLayout::justify(Justify::Center),
                Node { width: c.px(160.), ..default() },
            ));
            godot::label_button(row, c, look(Skin::Default), small(), ">", 16., Some(SpAction::Step(opt, 1)));
        });
}
fn toggle_row(p: &mut ChildSpawnerCommands, c: &Ctx, label: &str, opt: Opt, on: bool) {
    p.spawn(Node { width: c.px(340.), height: c.px(32.), column_gap: c.px(6.), align_items: AlignItems::Center, ..default() })
        .with_children(|row| {
            row.spawn((c.text(label, 15., Color::WHITE), Node { width: c.px(110.), ..default() }));
            godot::button(
                row,
                c,
                look(Skin::Flat),
                Node { width: c.px(44.), height: c.px(24.), align_items: AlignItems::Center, ..default() },
                Some(SpAction::Toggle(opt)),
                |b, _| godot::switch(b, c, on),
            );
        });
}
fn wide(c: &Ctx, height: f32) -> Node {
    Node {
        width: c.px(340.),
        height: c.px(height),
        align_items: AlignItems::Center,
        padding: UiRect::horizontal(c.px(4.)),
        ..default()
    }
}

/// BuildMapSelector: preview/Play/options | tabs + list | name + description.
pub(super) fn panel(p: &mut ChildSpawnerCommands, c: &Ctx, state: &UiState, assets: &AssetServer) {
    let sp = &state.menu.sp;
    p.spawn((Node { padding: UiRect::all(c.px(14.)), column_gap: c.px(16.), ..default() }, godot::panel_color()))
        .with_children(|cols| {
            left(cols, c, sp, assets);
            centre(cols, c, sp, assets);
            cols.spawn(Node { width: c.px(260.), flex_direction: FlexDirection::Column, row_gap: c.px(7.), ..default() })
                .with_children(|r| {
                    r.spawn(c.text(sp.name(), 20., Color::srgb(0.95, 0.94, 0.9)));
                    r.spawn(c.wrapped(sp.description(), 14., Color::srgb(0.78, 0.78, 0.78), 260.));
                });
        });
    if sp.advanced {
        advanced(p, c);
    }
}

fn preview_key(sp: &Selector) -> Option<&'static str> {
    match sp.entry {
        Entry::Generate | Entry::Infinite => None,
        // SetPreview("playground"): the thumbnail is absent from content.
        Entry::Playground => None,
        e => Some(OFFICIAL[e.official().unwrap_or(0)].1).filter(|k| !k.is_empty()),
    }
}

fn left(p: &mut ChildSpawnerCommands, c: &Ctx, sp: &Selector, assets: &AssetServer) {
    p.spawn(Node { width: c.px(340.), flex_direction: FlexDirection::Column, row_gap: c.px(7.), ..default() })
        .with_children(|l| {
            // Preview: 340x200, KeepAspectCovered over a dark bordered panel.
            l.spawn((
                Node {
                    width: c.px(340.),
                    height: c.px(200.),
                    border: UiRect::all(c.px(1.)),
                    overflow: Overflow::clip(),
                    justify_content: JustifyContent::Center,
                    align_items: AlignItems::Center,
                    ..default()
                },
                BackgroundColor(Color::srgb(0.10, 0.11, 0.10)),
                BorderColor::all(Color::srgba(0., 0., 0., 0.5)),
            ))
            .with_children(|frame| {
                if let Some(key) = preview_key(sp) {
                    // mappreview_*.png are 320x180: cover scale max(340/320, 200/180).
                    let s = (340. / 320f32).max(200. / 180.);
                    frame.spawn((
                        ImageNode::new(assets.load(format!("menu/mappreview_{key}.png"))),
                        Node { width: c.px(320. * s), height: c.px(180. * s), flex_shrink: 0., ..default() },
                        Pickable::IGNORE,
                    ));
                }
            });
            godot::label_button(l, c, look(Skin::Default), Node { width: c.px(340.), height: c.px(46.), ..default() }, "PLAY", 22., Some(SpAction::Play));
            option_row(l, c, "Difficulty", Opt::Difficulty, sp.options.difficulty);
            option_row(l, c, "Zombies", Opt::Zombies, sp.options.zombies);
            // Reset Data: two presses, disarmed on any selection change.
            let exists = crate::player::save_path(&sp.folder).is_file();
            let mut reset = look(Skin::Default);
            reset.disabled = !exists;
            let text = if !exists {
                "  Reset Data  (no save)".to_owned()
            } else if sp.reset_armed {
                format!("  Delete {}'s world?  Click again", sp.name())
            } else {
                "  Reset Data".to_owned()
            };
            godot::button(l, c, reset, wide(c, 34.), Some(SpAction::Reset), |b, color| {
                b.spawn((c.text(text, 15., color), Pickable::IGNORE));
            });
            let mut config = look(Skin::Default);
            config.toggled = sp.config_open;
            godot::button(l, c, config, wide(c, 34.), Some(SpAction::Config), |b, color| {
                b.spawn((c.text("  Config", 15., color), Pickable::IGNORE));
            });
            if sp.config_open {
                l.spawn(Node { flex_direction: FlexDirection::Column, row_gap: c.px(5.), ..default() })
                    .with_children(|cfg| {
                        option_row(cfg, c, "Loot", Opt::Loot, sp.options.loot);
                        option_row(cfg, c, "Day Cycle", Opt::Day, sp.options.day);
                        option_row(cfg, c, "Combat", Opt::Combat, sp.options.combat);
                        toggle_row(cfg, c, "Cheats", Opt::Cheats, sp.options.cheats);
                        toggle_row(cfg, c, "Permadeath", Opt::Permadeath, sp.options.permadeath);
                        godot::label_button(
                            cfg,
                            c,
                            look(Skin::Default),
                            Node { width: c.px(340.), height: c.px(34.), ..default() },
                            "\u{2699}  Advanced  (full vanilla config)",
                            14.,
                            Some(SpAction::Advanced),
                        );
                    });
            }
            if sp.generator_row() {
                l.spawn(Node { width: c.px(340.), height: c.px(32.), column_gap: c.px(6.), align_items: AlignItems::Center, ..default() })
                    .with_children(|row| {
                        row.spawn((c.text("Seed", 15., Color::WHITE), Node { width: c.px(110.), ..default() }));
                        let mut field = look(Skin::Default);
                        field.toggled = false;
                        godot::button(
                            row,
                            c,
                            field,
                            Node {
                                width: c.px(160.),
                                height: c.px(30.),
                                justify_content: JustifyContent::Center,
                                align_items: AlignItems::Center,
                                border: UiRect::all(c.px(if sp.seed_focus { 1. } else { 0. })),
                                ..default()
                            },
                            Some(SpAction::SeedFocus),
                            |b, _| {
                                let caret = if sp.seed_focus { "|" } else { "" };
                                b.spawn((c.text(format!("{}{caret}", sp.seed), 16., godot::FONT), Pickable::IGNORE));
                            },
                        );
                        godot::label_button(row, c, look(Skin::Default), Node { width: c.px(36.), height: c.px(30.), ..default() }, "\u{2684}", 16., Some(SpAction::Roll));
                        // Lakes lives in the seed row and hides for Infinite World.
                        if sp.entry == Entry::Generate {
                            godot::button(row, c, look(Skin::Flat), Node { column_gap: c.px(4.), align_items: AlignItems::Center, ..default() }, Some(SpAction::Lakes), |b, color| {
                                godot::checkbox(b, c, sp.lakes);
                                b.spawn((c.text("Lakes", 14., color), Pickable::IGNORE));
                            });
                        }
                    });
            }
            godot::button(l, c, look(Skin::Default), wide(c, 36.), Some(SpAction::Back), |b, color| {
                b.spawn((c.text("\u{25c4}  Back", 16., color), Pickable::IGNORE));
            });
        });
}

fn list_button(p: &mut ChildSpawnerCommands, c: &Ctx, text: String, width: f32, gold: bool, action: SpAction, icon: Option<(Handle<Image>, Vec2)>) {
    let mut l = look(Skin::Default);
    if gold {
        l.font = Some(GOLD);
    }
    godot::button(
        p,
        c,
        l,
        Node { width: c.px(width), height: c.px(if gold { 46. } else { 48. }), align_items: AlignItems::Center, padding: UiRect::horizontal(c.px(4.)), column_gap: c.px(4.), ..default() },
        Some(action),
        |b, color| {
            if let Some((image, size)) = icon {
                b.spawn((ImageNode::new(image), Node { width: c.px(size.x), height: c.px(size.y), flex_shrink: 0., ..default() }, Pickable::IGNORE));
            }
            b.spawn((c.text(text, 16., color), Pickable::IGNORE));
        },
    );
}

fn centre(p: &mut ChildSpawnerCommands, c: &Ctx, sp: &Selector, assets: &AssetServer) {
    p.spawn(Node { width: c.px(420.), flex_direction: FlexDirection::Column, row_gap: c.px(6.), ..default() })
        .with_children(|col| {
            col.spawn(Node { column_gap: c.px(3.), ..default() }).with_children(|tabs| {
                for (i, name) in ["Official", "Proc", "Curated", "Workshop", "Misc"].into_iter().enumerate() {
                    let mut tab = look(Skin::Default);
                    tab.toggled = sp.category as usize == i;
                    tab.disabled = i > 1;
                    godot::label_button(tabs, c, tab, Node { min_width: c.px(84.), height: c.px(40.), padding: UiRect::horizontal(c.px(4.)), ..default() }, name, 13., Some(SpAction::Category(i as u8)));
                }
            });
            godot::scroll_box(col, c, "maps", c.px(420.), c.px(330.), |scroll| {
                scroll.spawn(Node { min_width: c.px(400.), flex_direction: FlexDirection::Column, row_gap: c.px(3.), ..default() })
                    .with_children(|list| {
                        if sp.category == 1 {
                            islands(list, c, sp);
                            return;
                        }
                        list_button(list, c, format!("  \u{2699}  {GENERATE}"), 400., true, SpAction::Select(Entry::Generate), None);
                        list_button(list, c, "  \u{25ce}  Playground".into(), 400., true, SpAction::Select(Entry::Playground), None);
                        list_button(list, c, "  \u{221e}  Infinite World (prototype)".into(), 400., true, SpAction::Select(Entry::Infinite), None);
                        godot::separator(list, c);
                        for (i, (name, key, playable, _)) in OFFICIAL.iter().enumerate() {
                            // LoadTex(maxSize 36): mapicon_*.png is 380x80.
                            let icon = (!key.is_empty()).then(|| (assets.load(format!("menu/mapicon_{key}.png")), Vec2::new(36., 36. * 80. / 380.)));
                            let suffix = if *playable { "" } else { "   \u{b7}  not ported" };
                            let entry = Entry::Official(i);
                            list_button(list, c, format!("  {name}{suffix}"), 342., false, SpAction::Select(entry), icon);
                        }
                    });
            });
        });
}

/// RebuildProcList: one row per saved generated island, Play or two-press delete.
fn islands(list: &mut ChildSpawnerCommands, c: &Ctx, sp: &Selector) {
    let maps = list_islands(&maps_root());
    if maps.is_empty() {
        list.spawn((
            c.text("  No generated islands yet -- make one with Generate Island.", 13., Color::srgb(0.7, 0.7, 0.72)),
            Node { width: c.px(400.), height: c.px(46.), align_items: AlignItems::Center, ..default() },
        ));
        return;
    }
    for (name, seed, dir) in maps {
        list.spawn(Node { width: c.px(400.), height: c.px(46.), column_gap: c.px(4.), ..default() })
            .with_children(|row| {
                godot::button(row, c, look(Skin::Default), Node { width: c.px(346.), height: c.px(46.), align_items: AlignItems::Center, padding: UiRect::horizontal(c.px(4.)), ..default() }, Some(SpAction::OpenIsland(dir.clone())), |b, color| {
                    b.spawn((c.text(format!("  \u{2691}  {name}   (seed {seed})"), 15., color), Pickable::IGNORE));
                });
                let armed = sp.delete_armed.as_deref() == Some(name.as_str());
                godot::label_button(row, c, look(Skin::Default), Node { width: c.px(46.), height: c.px(46.), ..default() }, if armed { "?" } else { "X" }, 16., Some(SpAction::DeleteIsland(name.clone(), dir)));
            });
    }
}

/// MainMenuAdvanced: every vanilla ModeConfigData field. Source controls are
/// placeholders, so these are drawn but deliberately inert.
const ADVANCED: [(&str, &[(&str, char)]); 10] = [
    ("Items", &[("Spawn_Chance",'f'),("Despawn_Dropped_Time",'f'),("Despawn_Natural_Time",'f'),("Respawn_Time",'f'),("Quality_Full_Chance",'f'),("Quality_Multiplier",'f'),("Gun_Bullets_Full_Chance",'f'),("Gun_Bullets_Multiplier",'f'),("Magazine_Bullets_Full_Chance",'f'),("Magazine_Bullets_Multiplier",'f'),("Crate_Bullets_Full_Chance",'f'),("Crate_Bullets_Multiplier",'f'),("Has_Durability",'b'),("Food_Spawns_At_Full_Quality",'b'),("Water_Spawns_At_Full_Quality",'b'),("Clothing_Spawns_At_Full_Quality",'b'),("Weapons_Spawn_At_Full_Quality",'b'),("Default_Spawns_At_Full_Quality",'b'),("Clothing_Has_Durability",'b'),("Weapons_Have_Durability",'b')]),
    ("Vehicles", &[("Decay_Time",'f'),("Decay_Damage_Per_Second",'f'),("Has_Battery_Chance",'f'),("Min_Battery_Charge",'f'),("Max_Battery_Charge",'f'),("Has_Tire_Chance",'f'),("Respawn_Time",'f'),("Unlocked_After_Seconds_In_Safezone",'f'),("Armor_Multiplier",'f'),("Child_Explosion_Armor_Multiplier",'f'),("Gun_Lowcal_Damage_Multiplier",'f'),("Gun_Highcal_Damage_Multiplier",'f'),("Melee_Damage_Multiplier",'f'),("Melee_Repair_Multiplier",'f'),("Max_Instances_Tiny",'i'),("Max_Instances_Small",'i'),("Max_Instances_Medium",'i'),("Max_Instances_Large",'i'),("Max_Instances_Insane",'i'),("Min_Natural_Vehicles",'i')]),
    ("Zombies", &[("Spawn_Chance",'f'),("Loot_Chance",'f'),("Crawler_Chance",'f'),("Sprinter_Chance",'f'),("Flanker_Chance",'f'),("Burner_Chance",'f'),("Acid_Chance",'f'),("Boss_Electric_Chance",'f'),("Boss_Wind_Chance",'f'),("Boss_Fire_Chance",'f'),("Spirit_Chance",'f'),("DL_Red_Volatile_Chance",'f'),("DL_Blue_Volatile_Chance",'f'),("Boss_Elver_Stomper_Chance",'f'),("Boss_Kuwait_Chance",'f'),("Respawn_Day_Time",'f'),("Respawn_Night_Time",'f'),("Respawn_Beacon_Time",'f'),("Quest_Boss_Respawn_Interval",'f'),("Damage_Multiplier",'f'),("Armor_Multiplier",'f'),("Backstab_Multiplier",'f'),("NonHeadshot_Armor_Multiplier",'f'),("Beacon_Experience_Multiplier",'f'),("Full_Moon_Experience_Multiplier",'f'),("Min_Drops",'i'),("Max_Drops",'i'),("Min_Mega_Drops",'i'),("Max_Mega_Drops",'i'),("Min_Boss_Drops",'i'),("Max_Boss_Drops",'i'),("Slow_Movement",'b'),("Can_Stun",'b'),("Only_Critical_Stuns",'b'),("Weapons_Use_Player_Damage",'b'),("Can_Target_Barricades",'b'),("Can_Target_Structures",'b'),("Can_Target_Vehicles",'b'),("Can_Target_Objects",'b'),("Beacon_Max_Rewards",'i'),("Beacon_Max_Participants",'i'),("Beacon_Rewards_Multiplier",'f')]),
    ("Animals", &[("Respawn_Time",'f'),("Damage_Multiplier",'f'),("Armor_Multiplier",'f'),("Max_Instances_Tiny",'i'),("Max_Instances_Small",'i'),("Max_Instances_Medium",'i'),("Max_Instances_Large",'i'),("Max_Instances_Insane",'i'),("Weapons_Use_Player_Damage",'b')]),
    ("Barricades", &[("Decay_Time",'i'),("Armor_Lowtier_Multiplier",'f'),("Armor_Hightier_Multiplier",'f'),("Gun_Lowcal_Damage_Multiplier",'f'),("Gun_Highcal_Damage_Multiplier",'f'),("Melee_Damage_Multiplier",'f'),("Melee_Repair_Multiplier",'f'),("Allow_Item_Placement_On_Vehicle",'b'),("Allow_Trap_Placement_On_Vehicle",'b'),("Max_Item_Distance_From_Hull",'f'),("Max_Trap_Distance_From_Hull",'f')]),
    ("Structures", &[("Decay_Time",'i'),("Armor_Lowtier_Multiplier",'f'),("Armor_Hightier_Multiplier",'f'),("Gun_Lowcal_Damage_Multiplier",'f'),("Gun_Highcal_Damage_Multiplier",'f'),("Melee_Damage_Multiplier",'f'),("Melee_Repair_Multiplier",'f')]),
    ("Players", &[("Health_Default",'i'),("Health_Regen_Min_Food",'i'),("Health_Regen_Min_Water",'i'),("Health_Regen_Ticks",'i'),("Food_Default",'i'),("Food_Use_Ticks",'i'),("Food_Damage_Ticks",'i'),("Water_Default",'i'),("Water_Use_Ticks",'i'),("Water_Damage_Ticks",'i'),("Virus_Default",'i'),("Virus_Infect",'i'),("Virus_Use_Ticks",'i'),("Virus_Damage_Ticks",'i'),("Leg_Regen_Ticks",'i'),("Bleed_Damage_Ticks",'i'),("Bleed_Regen_Ticks",'i'),("Armor_Multiplier",'f'),("Experience_Multiplier",'f'),("Detect_Radius_Multiplier",'f'),("Ray_Aggressor_Distance",'f'),("Lose_Skills_PvP",'f'),("Lose_Skills_PvE",'f'),("Lose_Skill_Levels_PvP",'i'),("Lose_Skill_Levels_PvE",'i'),("Lose_Experience_PvP",'f'),("Lose_Experience_PvE",'f'),("Skill_Cost_Multiplier",'f'),("Lose_Items_PvP",'f'),("Lose_Items_PvE",'f'),("Lose_Clothes_PvP",'b'),("Lose_Clothes_PvE",'b'),("Lose_Weapons_PvP",'b'),("Lose_Weapons_PvE",'b'),("Can_Hurt_Legs",'b'),("Can_Break_Legs",'b'),("Can_Fix_Legs",'b'),("Can_Start_Bleeding",'b'),("Can_Stop_Bleeding",'b'),("Spawn_With_Max_Skills",'b'),("Spawn_With_Stamina_Skills",'b'),("Prevent_Level_Skill_Overrides",'b'),("Allow_Instakill_Headshots",'b'),("Allow_Per_Character_Saves",'b')]),
    ("Objects", &[("Binary_State_Reset_Multiplier",'f'),("Fuel_Reset_Multiplier",'f'),("Water_Reset_Multiplier",'f'),("Resource_Reset_Multiplier",'f'),("Resource_Drops_Multiplier",'f'),("Rubble_Reset_Multiplier",'f'),("Allow_Holiday_Drops",'b'),("Items_Obstruct_Tree_Respawns",'b')]),
    ("Events", &[("Rain_Frequency_Min",'f'),("Rain_Frequency_Max",'f'),("Rain_Duration_Min",'f'),("Rain_Duration_Max",'f'),("Snow_Frequency_Min",'f'),("Snow_Frequency_Max",'f'),("Snow_Duration_Min",'f'),("Snow_Duration_Max",'f'),("Weather_Frequency_Multiplier",'f'),("Weather_Duration_Multiplier",'f'),("Airdrop_Frequency_Min",'f'),("Airdrop_Frequency_Max",'f'),("Airdrop_Speed",'f'),("Airdrop_Force",'f'),("Arena_Min_Players",'i'),("Arena_Compactor_Damage",'i'),("Arena_Compactor_Extra_Damage_Per_Second",'f'),("Arena_Clear_Timer",'i'),("Arena_Finale_Timer",'i'),("Arena_Restart_Timer",'i'),("Arena_Compactor_Delay_Timer",'i'),("Arena_Compactor_Pause_Timer",'i'),("Use_Airdrops",'b'),("Arena_Use_Compactor_Pause",'b'),("Arena_Compactor_Speed_Tiny",'f'),("Arena_Compactor_Speed_Small",'f'),("Arena_Compactor_Speed_Medium",'f'),("Arena_Compactor_Speed_Large",'f'),("Arena_Compactor_Speed_Insane",'f'),("Arena_Compactor_Shrink_Factor",'f')]),
    ("Gameplay", &[("Repair_Level_Max",'i'),("Hitmarkers",'b'),("Crosshair",'b'),("Ballistics",'b'),("Chart",'b'),("Satellite",'b'),("Compass",'b'),("Group_Map",'b'),("Group_HUD",'b'),("Group_Player_List",'b'),("Allow_Static_Groups",'b'),("Allow_Dynamic_Groups",'b'),("Allow_Lobby_Groups",'b'),("Allow_Shoulder_Camera",'b'),("Can_Suicide",'b'),("Friendly_Fire",'b'),("Bypass_Buildable_Mobility",'b'),("Bypass_No_Building_Zones",'b'),("Bypass_Building_In_Safezones",'b'),("Allow_Freeform_Buildables",'b'),("Allow_Freeform_Buildables_On_Vehicles",'b'),("Enable_Damage_Flinch",'b'),("Enable_Explosion_Camera_Shake",'b')]),
];
pub fn advanced_field_count() -> usize {
    ADVANCED.iter().map(|(_, f)| f.len()).sum()
}

fn advanced(p: &mut ChildSpawnerCommands, c: &Ctx) {
    // Fixed rect (240,100)-(1140,1015) in the 2560x1440 base.
    p.spawn((
        Node {
            position_type: PositionType::Absolute,
            left: c.px(240.),
            top: c.px(100.),
            width: c.px(900.),
            height: c.px(915.),
            border: UiRect::all(c.px(1.)),
            ..default()
        },
        BackgroundColor(Color::srgb(0.10, 0.11, 0.12)),
        BorderColor::all(Color::srgba(0., 0., 0., 0.7)),
        GlobalZIndex(21),
    ))
    .with_children(|panel| {
        panel.spawn((c.text("ADVANCED \u{2014} FULL GAMEPLAY CONFIG", 20., GOLD), Node { position_type: PositionType::Absolute, left: c.px(16.), top: c.px(12.), ..default() }));
        godot::label_button(
            panel,
            c,
            look(Skin::Flat),
            Node { position_type: PositionType::Absolute, right: c.px(12.), top: c.px(8.), width: c.px(40.), height: c.px(36.), ..default() },
            "\u{2715}",
            22.,
            Some(SpAction::Advanced),
        );
        panel.spawn((c.text("every vanilla ModeConfigData option (per-difficulty). controls are placeholders \u{2014} the live ones are in the map panel.", 12., Color::srgb(0.7, 0.7, 0.7)), Node { position_type: PositionType::Absolute, left: c.px(16.), top: c.px(40.), ..default() }));
        panel
            .spawn((
                bevy::ui_widgets::ScrollArea,
                super::godot::ScrollKey("advanced"),
                Node {
                    position_type: PositionType::Absolute,
                    left: c.px(16.),
                    right: c.px(16.),
                    top: c.px(62.),
                    bottom: c.px(14.),
                    flex_direction: FlexDirection::Column,
                    overflow: Overflow::scroll_y(),
                    ..default()
                },
            ))
            .with_children(|scroll| {
                scroll.spawn(Node { min_width: c.px(860.), flex_direction: FlexDirection::Column, row_gap: c.px(4.), ..default() })
                    .with_children(|body| {
                        for (cat, fields) in ADVANCED {
                            body.spawn(c.text(format!("\u{25b8} {}", cat.to_uppercase()), 15., GOLD));
                            body.spawn(Node { display: Display::Grid, grid_template_columns: vec![RepeatedGridTrack::auto(2)], column_gap: c.px(22.), row_gap: c.px(2.), ..default() })
                                .with_children(|grid| {
                                    for (name, kind) in fields.iter() {
                                        grid.spawn(Node { width: c.px(350.), height: c.px(26.), column_gap: c.px(6.), align_items: AlignItems::Center, ..default() })
                                            .with_children(|row| {
                                                row.spawn((c.text(name.replace('_', " "), 12., Color::srgb(0.82, 0.82, 0.82)), Node { width: c.px(250.), ..default() }));
                                                if *kind == 'b' {
                                                    godot::switch(row, c, true);
                                                } else {
                                                    row.spawn((Node { width: c.px(70.), height: c.px(24.), justify_content: JustifyContent::Center, align_items: AlignItems::Center, border_radius: BorderRadius::all(c.px(3.)), ..default() }, godot::panel_color()))
                                                        .with_children(|e| { e.spawn(c.text(if *kind == 'f' { "1.0" } else { "1" }, 12., godot::FONT)); });
                                                }
                                            });
                                    }
                                });
                        }
                    });
            });
    });
}
