//! Session ownership shared by native UI, movement and world publishers. The
//! authority advertises these facts; clients never infer ownership from NetIds.
use serde::Deserialize;
use serde_json::{Value, json};

pub const DEFAULT_PORT: u16 = 47872;
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub enum Target {
    #[default]
    Local,
    Listen {
        port: u16,
        name: String,
        max_players: u8,
    },
    Client {
        host: String,
        port: u16,
    },
    /// Headless dedicated host: the bridge serves UDP with no local player.
    /// Unset name/capacity fall back to the source UG_NAME/UG_MAXPLAYERS.
    Dedicated {
        port: u16,
        name: Option<String>,
        max_players: Option<u8>,
    },
}
impl Target {
    pub fn networked(&self) -> bool {
        !matches!(self, Self::Local)
    }
    pub fn owns_world(&self) -> bool {
        !matches!(self, Self::Client { .. })
    }
    pub fn dedicated(&self) -> bool {
        matches!(self, Self::Dedicated { .. })
    }
    pub fn configure(&self, request: &mut Value) {
        match self {
            Self::Local => {}
            Self::Listen {
                port,
                name,
                max_players,
            } => {
                request["listenPort"] = json!(port);
                request["serverName"] = json!(name);
                request["maxPlayers"] = json!(max_players);
            }
            Self::Client { host, port } => {
                request["connectHost"] = json!(host);
                request["connectPort"] = json!(port);
            }
            Self::Dedicated {
                port,
                name,
                max_players,
            } => {
                request["dedicated"] = json!(true);
                request["listenPort"] = json!(port);
                if let Some(name) = name {
                    request["serverName"] = json!(name);
                }
                if let Some(max_players) = max_players {
                    request["maxPlayers"] = json!(max_players);
                }
            }
        }
    }
}
#[derive(Clone, Debug, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct State {
    pub mode: String,
    pub owns_world: bool,
    pub state: String,
    pub reason: String,
    pub reject_reason: String,
    pub reject_text: String,
    pub retryable: bool,
    pub awaiting_spawn: bool,
    pub host: Option<String>,
    pub port: u16,
    pub server_name: Option<String>,
    pub wire_gaps: Vec<String>,
}
impl Default for State {
    fn default() -> Self {
        Self {
            mode: "local".into(),
            owns_world: true,
            state: String::new(),
            reason: String::new(),
            reject_reason: String::new(),
            reject_text: String::new(),
            retryable: false,
            awaiting_spawn: false,
            host: None,
            port: 0,
            server_name: None,
            wire_gaps: vec![],
        }
    }
}
impl State {
    pub fn networked(&self) -> bool {
        matches!(self.mode.as_str(), "client" | "listen" | "dedicated")
    }
    pub fn read(snapshot: &Value) -> Self {
        serde_json::from_value(snapshot["network"].clone()).unwrap_or_default()
    }
}
pub fn owns_world(snapshot: &Value) -> bool {
    snapshot["network"]["ownsWorld"].as_bool().unwrap_or(true)
}
pub fn is_networked(snapshot: &Value) -> bool {
    matches!(
        snapshot["network"]["mode"].as_str(),
        Some("client" | "listen" | "dedicated")
    )
}
pub fn parse_address(host: &str, port: &str) -> Result<(String, u16), String> {
    let host = host.trim();
    if host.is_empty() {
        return Err("Enter a host or IP address.".into());
    }
    if host.chars().any(char::is_whitespace) {
        return Err("Host cannot contain spaces.".into());
    }
    let port = if port.trim().is_empty() {
        DEFAULT_PORT
    } else {
        port.trim()
            .parse::<u16>()
            .ok()
            .filter(|p| *p != 0)
            .ok_or_else(|| format!("\"{}\" is not a valid port (1-65535).", port.trim()))?
    };
    Ok((host.into(), port))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn address_and_authority_boundaries_are_explicit() {
        assert_eq!(
            parse_address(" localhost ", "").unwrap(),
            ("localhost".into(), 47872)
        );
        for (host, port) in [("", ""), ("a b", "12"), ("a", "0"), ("a", "65536")] {
            assert!(parse_address(host, port).is_err())
        }
        let mut r = json!({});
        Target::Client {
            host: "localhost".into(),
            port: 47872,
        }
        .configure(&mut r);
        assert_eq!(r, json!({"connectHost":"localhost","connectPort":47872}));
        let remote = json!({"network":{"mode":"client","ownsWorld":false}});
        assert!(!owns_world(&remote));
        assert!(is_networked(&remote));
        assert!(owns_world(&json!({})));
        assert!(!is_networked(&json!({})));
    }
}
