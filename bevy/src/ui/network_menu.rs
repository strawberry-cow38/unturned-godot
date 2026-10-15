//! Original direct-connect probe-before-load and authored server browser.
use super::*;
use crate::{
    bridge::CoreBridge,
    network::{self, Target},
};
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Field {
    Host,
    Port,
    Name,
    ServerName,
    MaxPlayers,
}
#[derive(Clone, Debug)]
pub enum Action {
    Focus(Field),
    Connect,
    Host,
    Refresh,
    Select,
    Sort(String),
}
#[derive(Debug)]
pub struct Menu {
    pub host: String,
    pub port: String,
    pub name: String,
    pub server_name: String,
    pub max_players: String,
    pub focus: Option<Field>,
    pub status: String,
    pub busy: bool,
    pub selected: bool,
    pub sort: String,
    pub descending: bool,
    pub live: Option<Status>,
    pub refresh_after: f64,
    pub generation: u64,
    pub request: Option<bool>,
    pub probe: Option<Probe>,
    pub cancelled_tokens: Vec<u64>,
    pub submit_host: bool,
}
impl Default for Menu {
    fn default() -> Self {
        Self {
            host: "127.0.0.1".into(),
            port: network::DEFAULT_PORT.to_string(),
            name: "Survivor".into(),
            server_name: "Bevy world".into(),
            max_players: "24".into(),
            focus: None,
            status: String::new(),
            busy: false,
            selected: true,
            sort: "Name".into(),
            descending: false,
            live: None,
            refresh_after: 0.,
            generation: 0,
            request: None,
            probe: None,
            cancelled_tokens: Vec::new(),
            submit_host: false,
        }
    }
}
#[derive(Clone, Debug, Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
pub struct Status {
    pub ok: bool,
    pub ping: i32,
    pub players: u16,
    pub max: u16,
    pub protocol: u8,
    pub pvp: bool,
    pub passworded: bool,
    pub has_flags: bool,
    pub max_ping: i32,
    pub map: String,
    pub gamemode: String,
    pub motd: String,
    pub icon_bytes: u32,
    pub compatible: bool,
    pub refusal: Option<String>,
    pub ping_refusal: Option<String>,
}
#[derive(Debug)]
pub struct Probe {
    token: u64,
    generation: u64,
    attempt: u8,
    join: bool,
    host: String,
    port: u16,
}
impl Menu {
    pub fn cancel(&mut self) {
        self.generation = self.generation.wrapping_add(1);
        self.busy = false;
        self.status.clear();
        if let Some(probe) = self.probe.take() {
            self.cancelled_tokens.push(probe.token);
        }
        self.request = None;
        self.submit_host = false;
    }
    fn field(&mut self, f: Field) -> &mut String {
        match f {
            Field::Host => &mut self.host,
            Field::Port => &mut self.port,
            Field::Name => &mut self.name,
            Field::ServerName => &mut self.server_name,
            Field::MaxPlayers => &mut self.max_players,
        }
    }
    pub fn action(&mut self, action: &Action, map: &str, now: f64) -> Option<UiIntent> {
        match action {
            Action::Focus(f) => self.focus = Some(*f),
            Action::Connect => {
                if !self.busy {
                    self.request = Some(true);
                }
            }
            Action::Refresh => {
                if !self.busy && now >= self.refresh_after {
                    self.host = "claw.bitvox.me".into();
                    self.port = network::DEFAULT_PORT.to_string();
                    self.request = Some(false);
                    self.refresh_after = now + 5.;
                }
            }
            Action::Select => {
                self.selected = true;
                self.host = "claw.bitvox.me".into();
                self.port = network::DEFAULT_PORT.to_string();
            }
            Action::Sort(key) => {
                if self.sort == *key {
                    self.descending = !self.descending;
                } else {
                    self.sort = key.clone();
                    self.descending = false;
                }
            }
            Action::Host => {
                if map.eq_ignore_ascii_case("Procedural") {
                    self.status="UDP hosting requires a retail map within the shared world-coordinate range.".into();
                    return None;
                }
                let (_, port) = match network::parse_address("localhost", &self.port) {
                    Ok(v) => v,
                    Err(e) => {
                        self.status = e;
                        return None;
                    }
                };
                let max = match self
                    .max_players
                    .parse::<u8>()
                    .ok()
                    .filter(|n| (1..=32).contains(n))
                {
                    Some(n) => n,
                    None => {
                        self.status = "Max players must be 1-32, including you.".into();
                        return None;
                    }
                };
                return Some(UiIntent::StartSession {
                    map: map.into(),
                    target: Target::Listen {
                        port,
                        name: self.server_name.clone(),
                        max_players: max,
                    },
                });
            }
        }
        None
    }
}
pub(super) struct NetworkMenuPlugin;
impl Plugin for NetworkMenuPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Update, poll.before(UiUpdate));
    }
}
pub(super) fn typed(state: &mut UiState, events: &mut MessageReader<KeyboardInput>) {
    for event in events.read() {
        if !event.state.is_pressed() {
            continue;
        }
        let Some(field) = state.network_menu.focus else {
            continue;
        };
        let value = state.network_menu.field(field);
        match &event.logical_key {
            Key::Backspace => {
                value.pop();
            }
            Key::Space => {
                if value.len() < 128 {
                    value.push(' ');
                }
            }
            Key::Character(chars) => {
                if value.len() + chars.len() <= 128 {
                    value.push_str(chars);
                }
            }
            Key::Enter if !event.repeat => {
                if state.screen == Screen::Host {
                    state.network_menu.submit_host = true;
                } else {
                    state.network_menu.request = Some(true);
                }
            }
            _ => {}
        }
    }
}
fn start(bridge: &CoreBridge, menu: &mut Menu, join: bool, host: String, port: u16, attempt: u8) {
    match bridge.request("serverProbeStart", json!({"host":host,"port":port})) {
        Ok(reply) => {
            if let Some(token) = reply["result"]["token"].as_u64() {
                menu.probe = Some(Probe {
                    token,
                    generation: menu.generation,
                    attempt,
                    join,
                    host: host.clone(),
                    port,
                });
                menu.busy = true;
                menu.status = format!("Connecting to {host}:{port} - attempt {attempt} of 4");
            } else {
                menu.busy = false;
                menu.status = "Invalid server probe reply".into();
            }
        }
        Err(error) => {
            menu.busy = false;
            menu.status = error.to_string();
        }
    }
}
fn poll(
    bridge: Res<CoreBridge>,
    mut ui: ResMut<UiState>,
    mut options: ResMut<crate::LaunchOptions>,
    mut intents: MessageWriter<UiIntent>,
    mut boot: Local<bool>,
) {
    if !*boot {
        *boot = true;
        ui.network_menu.name = options.0.player_name.clone();
        if let Target::Client { host, port } = &options.0.network {
            ui.network_menu.host = host.clone();
            ui.network_menu.port = port.to_string();
            ui.network_menu.request = Some(true);
            ui.screen = Screen::DirectConnect;
            options.0.play = false;
        }
    }
    for token in std::mem::take(&mut ui.network_menu.cancelled_tokens) {
        let _ = bridge.request("serverProbeCancel", json!({"token":token}));
    }
    if std::mem::take(&mut ui.network_menu.submit_host) {
        let map = ui.map.clone();
        if let Some(intent) = ui.network_menu.action(&Action::Host, &map, 0.) {
            options.0.player_name = ui.network_menu.name.clone();
            intents.write(intent);
        }
    }
    if let Some(join) = ui.network_menu.request.take() {
        let address = network::parse_address(&ui.network_menu.host, &ui.network_menu.port);
        match address {
            Ok((host, port)) => {
                ui.network_menu.cancel();
                start(&bridge, &mut ui.network_menu, join, host, port, 1);
            }
            Err(error) => ui.network_menu.status = error,
        }
    }
    let Some(probe) = ui.network_menu.probe.take() else {
        return;
    };
    if probe.generation != ui.network_menu.generation {
        return;
    }
    let response = match bridge.request("serverProbePoll", json!({"token":probe.token})) {
        Ok(r) => r,
        Err(error) => {
            ui.network_menu.busy = false;
            ui.network_menu.status = error.to_string();
            return;
        }
    };
    if response["result"]["pending"] == true {
        ui.network_menu.probe = Some(probe);
        return;
    }
    ui.network_menu.busy = false;
    let status: Status =
        serde_json::from_value(response["result"]["status"].clone()).unwrap_or_default();
    if !status.ok {
        if probe.join && probe.attempt < 4 {
            start(
                &bridge,
                &mut ui.network_menu,
                true,
                probe.host,
                probe.port,
                probe.attempt + 1,
            );
        } else {
            ui.network_menu.status = if probe.join {
                "No response from server (tried 4 times)."
            } else {
                "No response from server."
            }
            .into();
            ui.network_menu.live = None;
        }
        return;
    }
    ui.network_menu.status = format!("Reached {}:{} ({} ms)", probe.host, probe.port, status.ping);
    if probe.join {
        if let Some(refusal) = &status.refusal {
            ui.network_menu.status = refusal.clone();
        } else if !status.compatible {
            ui.network_menu.status = "Server has different game content.".into();
        } else if status.max_ping > 0 && status.ping > status.max_ping {
            ui.network_menu.status = format!(
                "Ping too high: server allows {} ms, measured {} ms.",
                status.max_ping, status.ping
            );
        } else if status.passworded {
            ui.network_menu.status = "This protocol cannot submit a server password.".into();
        } else if !matches!(status.map.as_str(), "PEI" | "Washington" | "Yukon") {
            ui.network_menu.status =
                format!("Server map '{}' is unavailable in this client.", status.map);
        } else {
            options.0.player_name = ui.network_menu.name.clone();
            intents.write(UiIntent::StartSession {
                map: status.map.clone(),
                target: Target::Client {
                    host: probe.host,
                    port: probe.port,
                },
            });
        }
    }
    ui.network_menu.live = Some(status);
}
fn field(p: &mut ChildSpawnerCommands, menu: &Menu, label: &str, f: Field, value: &str) {
    button(
        p,
        format!(
            "{}{}: {}",
            if menu.focus == Some(f) { "> " } else { "" },
            label,
            value
        ),
        UiButton::Network(Action::Focus(f)),
    );
}
pub(super) fn panel(p: &mut ChildSpawnerCommands, state: &UiState) {
    let m = &state.network_menu;
    p.spawn(text(
        match state.screen {
            Screen::Servers => "MULTIPLAYER",
            Screen::Host => "HOST WORLD",
            _ => "DIRECT CONNECT",
        },
        24.,
        TEXT,
    ));
    if state.screen == Screen::Servers {
        p.spawn(text(
            "Official   |   Favorites / History / LAN unavailable",
            13.,
            DIM,
        ));
        p.spawn(row()).with_children(|r| {
            for key in ["Name", "Map", "Mode", "Gamemode", "Players", "Ping"] {
                button(r, key, UiButton::Network(Action::Sort(key.into())));
            }
        });
        button(
            p,
            format!(
                "VoX Official - PEI | PEI | PvP | Survival | {}",
                m.live.as_ref().map_or("No response".into(), |s| format!(
                    "{}/{} | {} ms",
                    s.players, s.max, s.ping
                ))
            ),
            UiButton::Network(Action::Select),
        );
        if let Some(s) = &m.live {
            p.spawn(text(&s.motd, 14., DIM));
        }
        p.spawn(row()).with_children(|r| {
            button(r, "Refresh", UiButton::Network(Action::Refresh));
            button(r, "Join", UiButton::Network(Action::Connect));
        });
    } else {
        field(p, m, "Player name", Field::Name, &m.name);
        if state.screen == Screen::Host {
            field(p, m, "Server name", Field::ServerName, &m.server_name);
            field(p, m, "Max players", Field::MaxPlayers, &m.max_players);
        } else {
            field(p, m, "Host / IP", Field::Host, &m.host);
        }
        field(p, m, "Port", Field::Port, &m.port);
        p.spawn(text(
            "Password unavailable in the current server protocol",
            13.,
            DIM,
        ));
        button(
            p,
            if m.busy {
                "Connecting..."
            } else if state.screen == Screen::Host {
                "Host"
            } else {
                "Connect"
            },
            UiButton::Network(if state.screen == Screen::Host {
                Action::Host
            } else {
                Action::Connect
            }),
        );
    }
    p.spawn(text(&m.status, 14., ACCENT));
    button(p, "Back", UiButton::Open(Screen::MainMenu));
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cancellation_and_host_validation_do_not_create_an_optimistic_session() {
        let mut menu = Menu::default();
        menu.probe = Some(Probe {
            token: 9,
            generation: 0,
            attempt: 1,
            join: true,
            host: "localhost".into(),
            port: 47872,
        });
        menu.busy = true;
        menu.status = "Reached an old server".into();
        menu.cancel();
        assert!(
            menu.status.is_empty(),
            "a different form must not display a stale probe success"
        );
        assert!(!menu.busy);
        assert!(menu.probe.is_none());
        assert_eq!(menu.generation, 1);
        assert!(menu.action(&Action::Host, "Procedural", 0.).is_none());
        menu.max_players = "33".into();
        assert!(menu.action(&Action::Host, "PEI", 0.).is_none());
        menu.max_players = "4".into();
        assert!(matches!(
            menu.action(&Action::Host, "PEI", 0.),
            Some(UiIntent::StartSession {
                target: Target::Listen { max_players: 4, .. },
                ..
            })
        ));
        menu.action(&Action::Refresh, "PEI", 1.);
        assert_eq!(menu.request, Some(false));
        menu.request = None;
        menu.action(&Action::Refresh, "PEI", 2.);
        assert!(menu.request.is_none());
    }
    #[test]
    fn actual_status_probe_selects_advertised_map_before_emitting_join() {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
        let host = CoreBridge::launch(root).unwrap();
        let port = std::net::UdpSocket::bind("127.0.0.1:0")
            .unwrap()
            .local_addr()
            .unwrap()
            .port();
        host.request("init",json!({"contentRoot":crate::world::WorldConfig::default().content_root,"listenPort":port,"mapId":"Yukon","spawn":[0,0,0]})).unwrap();
        let mut app = App::new();
        let mut state = UiState {
            screen: Screen::DirectConnect,
            ..default()
        };
        state.network_menu.port = port.to_string();
        state.network_menu.request = Some(true);
        app.add_plugins(MinimalPlugins)
            .insert_resource(CoreBridge::launch(root).unwrap())
            .insert_resource(crate::LaunchOptions(crate::cli::Options::default()))
            .insert_resource(state)
            .add_message::<UiIntent>()
            .add_systems(Update, poll);
        app.update();
        assert!(!app.world().resource::<crate::LaunchOptions>().0.play);
        assert!(app.world().resource::<UiState>().network_menu.busy);
        for _ in 0..160 {
            host.request("tick", json!({})).unwrap();
            std::thread::sleep(std::time::Duration::from_millis(10));
            app.update();
            if !app.world().resource::<UiState>().network_menu.busy {
                break;
            }
        }
        let messages: Vec<_> = app
            .world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .collect();
        assert!(
            matches!(&messages[..],[UiIntent::StartSession{map,target:Target::Client{host,port:p}}] if map=="Yukon"&&host=="127.0.0.1"&&*p==port),
            "actual probe result: {:?}; messages {messages:?}",
            app.world().resource::<UiState>().network_menu
        );
        assert!(
            !app.world().resource::<crate::LaunchOptions>().0.play,
            "only the lifecycle accepts the join intent and starts world load"
        );
    }
}
