//! Persisted player preferences. Action names stay stable across renderer changes.
use bevy::prelude::*;
use serde::{Deserialize, Serialize};
use std::{collections::BTreeMap, fs, io, path::PathBuf};

#[derive(Clone, Copy, Debug, Eq, PartialEq, Ord, PartialOrd, Serialize, Deserialize)]
pub enum GameAction {
    MoveForward,
    MoveBack,
    MoveLeft,
    MoveRight,
    Jump,
    Sprint,
    Crouch,
    CrouchToggle,
    Prone,
    LeanLeft,
    LeanRight,
    Fire,
    Aim,
    Reload,
    Firemode,
    Melee,
    Grenade,
    Interact,
    AttachMenu,
    ToggleFirstPerson,
    Flashlight,
    QuickTransfer,
    Inventory,
    Map,
    Craft,
    Skills,
    Console,
    Chat,
    Hotbar1,
    Hotbar2,
    Hotbar3,
    Hotbar4,
    Hotbar5,
    Hotbar6,
    Hotbar7,
    Hotbar8,
    Hotbar9,
    Hotbar10,
    VehicleHandbrake,
    VehicleDoor,
    Surrender,
    GestureWave,
    GestureSalute,
    GesturePoint,
    GestureFacepalm,
    GestureRest,
    BugReport,
    FreeLook,
    VehicleLights,
    VehicleIgnition,
    LandingGear,
    BuildCycleType,
    BuildSalvage,
    BuildUpgrade,
    ItemUses,
    ItemRecipes,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum Binding {
    Key(String),
    Mouse(u16),
    Unbound,
}

impl Binding {
    /// Match the configured physical input against one ordered OS event.
    /// The caller handles press/release/repeat and window ownership; final
    /// ButtonInput edge flags cannot represent a press and release in one batch.
    pub(crate) fn matches_window_event(&self, event: &bevy::window::WindowEvent) -> bool {
        match (self, event) {
            (Self::Key(name), bevy::window::WindowEvent::KeyboardInput(e)) => {
                *name == format!("{:?}", e.key_code)
            }
            (Self::Mouse(number), bevy::window::WindowEvent::MouseButtonInput(e)) => {
                *number == mouse_number(e.button)
            }
            _ => false,
        }
    }
    pub fn label(&self) -> String {
        match self {
            Self::Key(key) => key.strip_prefix("Key").unwrap_or(key).to_owned(),
            Self::Mouse(button) => format!("Mouse {button}"),
            Self::Unbound => "Unbound".into(),
        }
    }

    pub fn pressed(&self, keys: &ButtonInput<KeyCode>, mouse: &ButtonInput<MouseButton>) -> bool {
        match self {
            Self::Key(name) => keys.get_pressed().any(|key| format!("{key:?}") == *name),
            Self::Mouse(number) => mouse
                .get_pressed()
                .any(|button| mouse_number(*button) == *number),
            Self::Unbound => false,
        }
    }

    pub fn just_pressed(
        &self,
        keys: &ButtonInput<KeyCode>,
        mouse: &ButtonInput<MouseButton>,
    ) -> bool {
        match self {
            Self::Key(name) => keys
                .get_just_pressed()
                .any(|key| format!("{key:?}") == *name),
            Self::Mouse(number) => mouse
                .get_just_pressed()
                .any(|button| mouse_number(*button) == *number),
            Self::Unbound => false,
        }
    }
}

impl Binding {
    /// Original `Bind.Label` for the rebind list: Godot's keycode string for the
    /// physical key on a US layout, `Mouse N`, or an em dash when unbound.
    pub fn source_label(&self) -> String {
        match self {
            Self::Unbound => "\u{2014}".into(),
            Self::Mouse(n) => format!("Mouse {n}"),
            Self::Key(name) => {
                let name = name.as_str();
                if let Some(letter) = name.strip_prefix("Key") {
                    return letter.into();
                }
                if let Some(digit) = name.strip_prefix("Digit") {
                    return digit.into();
                }
                if let Some(pad) = name.strip_prefix("Numpad") {
                    return format!("Kp {pad}");
                }
                if let Some(arrow) = name.strip_prefix("Arrow") {
                    return arrow.into();
                }
                match name {
                    "ShiftLeft" | "ShiftRight" => "Shift",
                    "ControlLeft" | "ControlRight" => "Ctrl",
                    "AltLeft" | "AltRight" => "Alt",
                    "SuperLeft" | "SuperRight" => "Meta",
                    "Backquote" => "QuoteLeft",
                    "Quote" => "Apostrophe",
                    other => other,
                }
                .into()
            }
        }
    }
}

impl GameAction {
    /// Keybinds.DisplayName: split the stable enum name before each capital that
    /// does not follow a digit ("Hotbar10", "Gesture Facepalm").
    pub fn display_name(self) -> String {
        let name = format!("{self:?}");
        let mut out = String::with_capacity(name.len() + 4);
        let mut previous: Option<char> = None;
        for c in name.chars() {
            if previous.is_some_and(|p| !p.is_ascii_digit()) && c.is_ascii_uppercase() {
                out.push(' ');
            }
            out.push(c);
            previous = Some(c);
        }
        out
    }
    /// Keybinds.ContextLabel; empty for Anywhere actions.
    pub fn context_label(self) -> &'static str {
        match context(self) {
            BindContext::OnFoot => "on foot",
            BindContext::Driving => "in vehicle",
            BindContext::Building => "while building",
            BindContext::Bag => "in inventory",
            BindContext::Anywhere => "",
        }
    }
}

pub fn mouse_number(button: MouseButton) -> u16 {
    match button {
        MouseButton::Left => 1,
        MouseButton::Right => 2,
        MouseButton::Middle => 3,
        MouseButton::Back => 4,
        MouseButton::Forward => 5,
        MouseButton::Other(number) => number.saturating_add(6),
    }
}

fn default_bindings() -> BTreeMap<GameAction, Binding> {
    use GameAction::*;
    let mut bindings = BTreeMap::new();
    for (action, key) in [
        (MoveForward, "KeyW"),
        (MoveBack, "KeyS"),
        (MoveLeft, "KeyA"),
        (MoveRight, "KeyD"),
        (Jump, "Space"),
        (Sprint, "ShiftLeft"),
        (Crouch, "KeyC"),
        (CrouchToggle, "KeyX"),
        (Prone, "KeyZ"),
        (LeanLeft, "KeyQ"),
        (LeanRight, "KeyE"),
        (Reload, "KeyR"),
        (Firemode, "KeyV"),
        (Interact, "KeyF"),
        (AttachMenu, "KeyT"),
        (ToggleFirstPerson, "KeyH"),
        (Flashlight, "KeyN"),
        (QuickTransfer, "KeyH"),
        (Inventory, "KeyG"),
        (Map, "KeyM"),
        (Craft, "KeyY"),
        (Skills, "KeyJ"),
        (Console, "Backquote"),
        (Chat, "Enter"),
        (Hotbar1, "Digit1"),
        (Hotbar2, "Digit2"),
        (Hotbar3, "Digit3"),
        (Hotbar4, "Digit4"),
        (Hotbar5, "Digit5"),
        (Hotbar6, "Digit6"),
        (Hotbar7, "Digit7"),
        (Hotbar8, "Digit8"),
        (Hotbar9, "Digit9"),
        (Hotbar10, "Digit0"),
        (VehicleHandbrake, "Space"),
        (VehicleDoor, "ControlLeft"),
        (Surrender, "KeyB"),
        (BugReport, "Backslash"),
        (FreeLook, "AltLeft"),
        (VehicleLights, "KeyL"),
        (VehicleIgnition, "KeyN"),
        (LandingGear, "KeyG"),
        (BuildCycleType, "KeyC"),
        (BuildSalvage, "KeyR"),
        (BuildUpgrade, "KeyY"),
        (ItemUses, "KeyU"),
        (ItemRecipes, "KeyR"),
    ] {
        bindings.insert(action, Binding::Key(key.into()));
    }
    bindings.insert(Fire, Binding::Mouse(1));
    bindings.insert(Aim, Binding::Mouse(2));
    for action in [
        Melee,
        Grenade,
        GestureWave,
        GestureSalute,
        GesturePoint,
        GestureFacepalm,
        GestureRest,
    ] {
        bindings.insert(action, Binding::Unbound);
    }
    bindings
}

#[derive(Clone, Debug, Resource, Serialize, Deserialize)]
#[serde(default)]
pub struct Settings {
    pub mouse_sensitivity: f32,
    pub invert_look_y: bool,
    pub field_of_view: f32,
    pub master_volume: f32,
    pub invert_heli_pitch: bool,
    pub invert_plane_pitch: bool,
    pub heli_sensitivity: f32,
    pub vsync: bool,
    /// GraphicsOptions rows; `vsync` above keeps its original native key.
    pub graphics: Graphics,
    pub bindings: BTreeMap<GameAction, Binding>,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            mouse_sensitivity: 0.12,
            invert_look_y: false,
            field_of_view: 75.0,
            master_volume: 1.0,
            invert_heli_pitch: false,
            invert_plane_pitch: false,
            heli_sensitivity: 1.0,
            // GraphicsOptions.VSync defaults false; ApplyWindow presents uncapped.
            vsync: false,
            graphics: Graphics::default(),
            bindings: default_bindings(),
        }
    }
}

/// GraphicsOptions.AAMode in AAOrder.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum AntiAliasing {
    Off,
    #[default]
    Fxaa,
    Msaa2x,
    Msaa4x,
    Msaa8x,
    Taa,
}
impl AntiAliasing {
    pub const ORDER: [Self; 6] = [
        Self::Off,
        Self::Fxaa,
        Self::Msaa2x,
        Self::Msaa4x,
        Self::Msaa8x,
        Self::Taa,
    ];
    pub fn label(self) -> &'static str {
        match self {
            Self::Off => "Off",
            Self::Fxaa => "FXAA",
            Self::Msaa2x => "MSAA 2x",
            Self::Msaa4x => "MSAA 4x",
            Self::Msaa8x => "MSAA 8x",
            Self::Taa => "TAA",
        }
    }
    pub fn samples(self) -> u32 {
        match self {
            Self::Msaa2x => 2,
            Self::Msaa4x => 4,
            Self::Msaa8x => 8,
            _ => 1,
        }
    }
}

/// GraphicsOptions.ShadowQuality and GfxQuality share these five names.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum Quality {
    Off,
    Low,
    Medium,
    #[default]
    High,
    Ultra,
}
impl Quality {
    pub const ORDER: [Self; 5] = [Self::Off, Self::Low, Self::Medium, Self::High, Self::Ultra];
    pub const ORDER_NO_OFF: [Self; 4] = [Self::Low, Self::Medium, Self::High, Self::Ultra];
    pub fn label(self) -> &'static str {
        match self {
            Self::Off => "Off",
            Self::Low => "Low",
            Self::Medium => "Medium",
            Self::High => "High",
            Self::Ultra => "Ultra",
        }
    }
    /// ApplyShadows atlas size; Off is a real state with no shadow pass.
    pub fn shadow_map_size(self) -> usize {
        match self {
            Self::Off => 0,
            Self::Low => 1024,
            Self::Medium => 2048,
            Self::High => 4096,
            Self::Ultra => 8192,
        }
    }
    /// Godot splits one directional atlas into four PSSM quadrants; Bevy
    /// allocates its size per cascade. Half the atlas edge per cascade keeps
    /// the same texels per split (and the same memory).
    pub fn cascade_map_size(self) -> usize {
        self.shadow_map_size() / 2
    }
    /// GraphicsOptions.EffectMul (particle count/size, read when emitters build).
    pub fn effect_multiplier(self) -> f32 {
        match self {
            Self::Off => 0.05,
            Self::Low => 0.5,
            Self::Medium => 0.75,
            Self::High => 1.,
            Self::Ultra => 1.5,
        }
    }
    /// GraphicsOptions.ScopeSize: the square optic target, applied at next mount.
    pub fn scope_size(self) -> u32 {
        match self {
            Self::Off | Self::Low => 360,
            Self::Medium => 540,
            Self::High => 720,
            Self::Ultra => 1080,
        }
    }
    /// GraphicsOptions.PlanarEvery; Off disables the mirror camera entirely.
    pub fn planar_every(self) -> u32 {
        match self {
            Self::Low => 4,
            Self::Medium => 2,
            _ => 1,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum Fullscreen {
    #[default]
    Windowed,
    Borderless,
    Exclusive,
}
impl Fullscreen {
    pub const ORDER: [Self; 3] = [Self::Windowed, Self::Borderless, Self::Exclusive];
    pub fn label(self) -> &'static str {
        match self {
            Self::Windowed => "Windowed",
            Self::Borderless => "Borderless",
            Self::Exclusive => "Exclusive",
        }
    }
}

/// GraphicsOptions persisted state (graphics.cfg [graphics]). Every value keeps
/// the original default and cycle order; see `docs/SETTINGS_MENU_PORT.md` for
/// which rows reach a native renderer hook and which are explicitly pending.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct Graphics {
    pub anti_aliasing: AntiAliasing,
    pub anisotropy: u32,
    /// [0, 0] is "Native": the window is left as sized.
    pub resolution: [u32; 2],
    pub shadows: Quality,
    pub shadow_distance: f32,
    pub render_scale: f32,
    pub draw_distance: f32,
    pub fullscreen: Fullscreen,
    pub target_fps: u32,
    pub ui_scale: f32,
    pub effect_quality: Quality,
    pub ambient_occlusion: bool,
    pub bloom: bool,
    pub chromatic_aberration: bool,
    pub chromatic_amount: f32,
    pub sun_shafts: bool,
    /// 0 Low, 1 Medium, 2 High; boot-time like the original override.cfg key.
    pub fog_quality: u8,
    pub screen_space_reflections: bool,
    pub planar_reflection: Quality,
    pub scope_quality: Quality,
    pub outline: bool,
    pub grass_displacement: bool,
    pub wind: bool,
    pub ragdolls: bool,
    /// Pipelined (render-thread) rendering; read once at launch.
    pub render_thread: bool,
}
impl Default for Graphics {
    fn default() -> Self {
        Self {
            anti_aliasing: AntiAliasing::Fxaa,
            anisotropy: 4,
            resolution: [0, 0],
            shadows: Quality::High,
            shadow_distance: 120.,
            render_scale: 1.,
            draw_distance: 1.,
            fullscreen: Fullscreen::Windowed,
            target_fps: 0,
            ui_scale: 1.,
            effect_quality: Quality::High,
            ambient_occlusion: false,
            bloom: true,
            chromatic_aberration: false,
            chromatic_amount: 0.35,
            sun_shafts: false,
            fog_quality: 1,
            screen_space_reflections: false,
            planar_reflection: Quality::Medium,
            scope_quality: Quality::High,
            outline: true,
            grass_displacement: true,
            wind: true,
            ragdolls: true,
            render_thread: true,
        }
    }
}
impl Graphics {
    pub const ANISOTROPY: [u32; 5] = [1, 2, 4, 8, 16];
    pub const RESOLUTIONS: [[u32; 2]; 6] = [
        [0, 0],
        [1280, 720],
        [1600, 900],
        [1920, 1080],
        [2560, 1440],
        [3840, 2160],
    ];
    pub const SHADOW_DISTANCES: [f32; 5] = [40., 80., 120., 200., 300.];
    pub const RENDER_SCALES: [f32; 6] = [1., 0.9, 0.85, 0.75, 0.7, 0.5];
    pub const DRAW_DISTANCES: [f32; 4] = [0.25, 0.5, 0.75, 1.];
    pub const TARGET_FPS: [u32; 5] = [0, 60, 120, 144, 240];
    pub const UI_SCALES: [f32; 5] = [0.75, 0.85, 1., 1.15, 1.3];
    pub const CHROMATIC: [f32; 4] = [0.2, 0.35, 0.6, 1.];
    pub const FOG_QUALITY: [&'static str; 3] = ["Low", "Medium", "High"];

    /// GraphicsOptions.Load's clamps; unknown values keep their row's order.
    fn normalize(&mut self) {
        fn sane(value: f32, min: f32, max: f32, fallback: f32) -> f32 {
            if value.is_finite() { value.clamp(min, max) } else { fallback }
        }
        let fallback = Self::default();
        self.draw_distance = sane(self.draw_distance, 0.25, 1., fallback.draw_distance);
        self.shadow_distance = sane(self.shadow_distance, 40., 300., fallback.shadow_distance);
        self.render_scale = sane(self.render_scale, 0.25, 1., fallback.render_scale);
        self.ui_scale = sane(self.ui_scale, 0.5, 2., fallback.ui_scale);
        self.target_fps = self.target_fps.min(1000);
        self.fog_quality = self.fog_quality.min(2);
        if !self.chromatic_amount.is_finite() {
            self.chromatic_amount = fallback.chromatic_amount;
        }
        if self.resolution.iter().any(|v| *v > 16384) {
            self.resolution = [0, 0];
        }
    }
    pub fn resolution_label(&self) -> String {
        match self.resolution {
            [0, 0] => "Native".into(),
            [w, h] => format!("{w} x {h}"),
        }
    }
    pub fn draw_label(&self) -> &'static str {
        let d = self.draw_distance;
        if d <= 0.25 {
            "Low"
        } else if d <= 0.5 {
            "Medium"
        } else if d <= 0.75 {
            "High"
        } else {
            "Ultra"
        }
    }
    pub fn render_scale_label(&self) -> String {
        if self.render_scale >= 0.999 {
            "Native".into()
        } else {
            format!("{:.0}%", self.render_scale * 100.)
        }
    }
}

/// GraphicsOptions.Next: advance and wrap; an unlisted current value starts the order.
pub fn next<T: PartialEq + Copy>(order: &[T], current: T) -> T {
    let index = order.iter().position(|value| *value == current);
    order[index.map_or(0, |i| (i + 1) % order.len())]
}

impl Settings {
    /// Mirrors the original keybind conflict contexts. Runtime input still has to
    /// enforce these modal boundaries; this only prevents an accidental double bind.
    pub fn conflict_with(&self, action: GameAction, binding: &Binding) -> Option<GameAction> {
        if *binding == Binding::Unbound {
            return None;
        }
        self.bindings.iter().find_map(|(&other, used)| {
            (other != action && used == binding && !exclusive(action, other)).then_some(other)
        })
    }

    pub fn path() -> PathBuf {
        // Rows persist on every change; a unit test must never write the
        // developer's real preferences (or the saves/maps beside them), even
        // when a dedicated box exports the override variables.
        #[cfg(test)]
        return std::env::temp_dir()
            .join(format!("unturned-bevy-test-{}", std::process::id()))
            .join("settings.json");
        #[allow(unreachable_code)]
        if let Some(path) = std::env::var_os("UG_BEVY_SETTINGS") {
            return path.into();
        }
        let base = std::env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .or_else(|| std::env::var_os("APPDATA").map(PathBuf::from))
            .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".config")))
            .unwrap_or_else(|| PathBuf::from("."));
        base.join("unturned-bevy").join("settings.json")
    }

    pub fn load() -> Result<Self, String> {
        match fs::read_to_string(Self::path()) {
            Ok(json) => Self::from_json(&json),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(Self::default()),
            Err(error) => Err(error.to_string()),
        }
    }

    pub fn from_json(json: &str) -> Result<Self, String> {
        let mut settings: Self = serde_json::from_str(json).map_err(|error| error.to_string())?;
        settings.normalize();
        Ok(settings)
    }

    pub fn normalize(&mut self) {
        fn sane(value: f32, min: f32, max: f32, fallback: f32) -> f32 {
            if value.is_finite() {
                value.clamp(min, max)
            } else {
                fallback
            }
        }
        self.mouse_sensitivity = sane(self.mouse_sensitivity, 0.02, 0.5, 0.12);
        self.field_of_view = sane(self.field_of_view, 50.0, 110.0, 75.0);
        self.master_volume = sane(self.master_volume, 0.0, 1.0, 1.0);
        self.heli_sensitivity = sane(self.heli_sensitivity, 0.25, 2.5, 1.0);
        self.graphics.normalize();
        for (action, binding) in default_bindings() {
            let current = self.bindings.entry(action).or_insert(binding.clone());
            if let Binding::Key(name) = current {
                if let bevy::reflect::TypeInfo::Enum(keys) =
                    <KeyCode as bevy::reflect::Typed>::type_info()
                {
                    if keys.variant(name).is_none() {
                        *current = binding;
                    }
                }
            }
        }
    }

    pub fn save(&self) -> Result<(), String> {
        let path = Self::path();
        if let Some(parent) = path
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
        {
            fs::create_dir_all(parent).map_err(|error| error.to_string())?;
        }
        let json = serde_json::to_vec_pretty(self).map_err(|error| error.to_string())?;
        // A completed temporary write is renamed over the prior file, so a crash cannot truncate it.
        let temp = path.with_extension("json.tmp");
        fs::write(&temp, json).map_err(|error| error.to_string())?;
        fs::rename(&temp, &path).map_err(|error| error.to_string())
    }

    pub fn pressed(
        &self,
        action: GameAction,
        keys: &ButtonInput<KeyCode>,
        mouse: &ButtonInput<MouseButton>,
    ) -> bool {
        self.bindings
            .get(&action)
            .is_some_and(|binding| binding.pressed(keys, mouse))
    }

    pub fn just_pressed(
        &self,
        action: GameAction,
        keys: &ButtonInput<KeyCode>,
        mouse: &ButtonInput<MouseButton>,
    ) -> bool {
        self.bindings
            .get(&action)
            .is_some_and(|binding| binding.just_pressed(keys, mouse))
    }
}

#[derive(PartialEq, Eq, Clone, Copy)]
enum BindContext {
    Anywhere,
    OnFoot,
    Driving,
    Bag,
    Building,
}

fn context(action: GameAction) -> BindContext {
    use GameAction::*;
    match action {
        Jump | Sprint | Crouch | CrouchToggle | Prone | LeanLeft | LeanRight | Flashlight => {
            BindContext::OnFoot
        }
        VehicleHandbrake | VehicleLights | VehicleIgnition | LandingGear | VehicleDoor => {
            BindContext::Driving
        }
        QuickTransfer | ItemUses | ItemRecipes => BindContext::Bag,
        BuildCycleType | BuildSalvage | BuildUpgrade => BindContext::Building,
        _ => BindContext::Anywhere,
    }
}

fn exclusive(a: GameAction, b: GameAction) -> bool {
    use GameAction::*;
    let (ca, cb) = (context(a), context(b));
    if ca != BindContext::Anywhere && cb != BindContext::Anywhere && ca != cb {
        return true;
    }
    if (ca == BindContext::Building) != (cb == BindContext::Building) {
        return true;
    }
    if (ca == BindContext::Bag) != (cb == BindContext::Bag) {
        let world = if ca == BindContext::Bag { b } else { a };
        if world != Inventory && world != Interact {
            return true;
        }
    }
    matches!((a, b), (LandingGear, Inventory) | (Inventory, LandingGear))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ordered_binding_matches_each_rebound_physical_edge_and_mouse_button() {
        use bevy::{
            input::{
                ButtonState,
                keyboard::{Key, KeyboardInput},
                mouse::MouseButtonInput,
            },
            window::WindowEvent,
        };
        let window = Entity::PLACEHOLDER;
        let binding = Binding::Key("KeyQ".into());
        for state in [ButtonState::Pressed, ButtonState::Released] {
            let event = WindowEvent::KeyboardInput(KeyboardInput {
                window,
                key_code: KeyCode::KeyQ,
                logical_key: Key::Character("a".into()),
                state,
                text: None,
                repeat: false,
            });
            assert!(
                binding.matches_window_event(&event),
                "physical binding survives a different logical keyboard layout"
            );
            assert!(!Binding::Key("KeyF".into()).matches_window_event(&event));
            assert!(!Binding::Unbound.matches_window_event(&event));
        }
        let event = WindowEvent::MouseButtonInput(MouseButtonInput {
            window,
            button: MouseButton::Back,
            state: ButtonState::Released,
        });
        assert!(Binding::Mouse(4).matches_window_event(&event));
        assert!(!Binding::Mouse(5).matches_window_event(&event));
        assert!(!binding.matches_window_event(&event));
    }

    #[test]
    fn partial_settings_preserve_defaults_and_explicit_unbound_actions() {
        let settings =
            Settings::from_json(r#"{"bindings":{"Jump":"Unbound"},"mouse_sensitivity":99.0}"#)
                .unwrap();
        assert_eq!(settings.bindings[&GameAction::Jump], Binding::Unbound);
        assert_eq!(
            settings.bindings[&GameAction::MoveForward],
            Binding::Key("KeyW".into())
        );
        assert_eq!(settings.mouse_sensitivity, 0.5);
    }

    #[test]
    fn serializing_preferences_keeps_all_bindings_and_inversions() {
        let original = Settings {
            invert_look_y: true,
            invert_heli_pitch: true,
            master_volume: 0.3,
            ..default()
        };
        let copy = Settings::from_json(&serde_json::to_string(&original).unwrap()).unwrap();
        assert_eq!(copy.bindings, original.bindings);
        assert!(copy.invert_look_y && copy.invert_heli_pitch);
        assert!(!copy.invert_plane_pitch);
        assert_eq!(copy.master_volume, 0.3);
    }

    #[test]
    fn unbound_action_never_consumes_keyboard_or_mouse_input() {
        let mut keys = ButtonInput::default();
        keys.press(KeyCode::KeyG);
        let mut mouse = ButtonInput::default();
        mouse.press(MouseButton::Left);
        assert!(!Binding::Unbound.pressed(&keys, &mouse));
        assert!(!Binding::Unbound.just_pressed(&keys, &mouse));
    }

    #[test]
    fn defaults_share_only_controls_with_exclusive_contexts() {
        let settings = Settings::default();
        for (&action, binding) in &settings.bindings {
            assert_eq!(settings.conflict_with(action, binding), None, "{action:?}");
        }
        assert_eq!(
            settings.conflict_with(GameAction::Reload, &Binding::Key("KeyW".into())),
            Some(GameAction::MoveForward)
        );
    }

    #[test]
    fn an_invalid_saved_key_does_not_disable_movement() {
        let settings =
            Settings::from_json(r#"{"bindings":{"MoveForward":{"Key":"TypoKeyW"}}}"#).unwrap();
        assert_eq!(
            settings.bindings[&GameAction::MoveForward],
            Binding::Key("KeyW".into())
        );
    }
}
