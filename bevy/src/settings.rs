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
            vsync: true,
            bindings: default_bindings(),
        }
    }
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
