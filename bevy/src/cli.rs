use anyhow::{Result, bail};
use std::path::PathBuf;

#[derive(Debug, Clone)]
pub struct Options {
    pub content: PathBuf,
    pub unturned: Option<PathBuf>,
    pub map: String,
    pub seed: i32,
    pub island: Option<PathBuf>,
    pub new_island: Option<PathBuf>,
    pub island_name: String,
    pub island_tiles: [usize; 2],
    /// Generate Island's Lakes box (`--lakes`).
    pub lakes: bool,
    /// MainMenuPlay Zombies row; false matches `UG_NOZOMBIES=1` for this session.
    pub zombies: bool,
    pub network: crate::network::Target,
    pub player_name: String,
    pub network_ready: bool,
    pub vehicle: Option<String>,
    pub hitch: Option<String>,
    pub demo_furniture: bool,
    pub play: bool,
    pub headless: bool,
    /// `--netlog`: route core NetLog to stderr (source flag; also UG_NETLOG=1).
    pub netlog: bool,
    pub ticks: u32,
    pub screenshot: Option<PathBuf>,
    pub screenshot_frame: u32,
    pub screenshot_event: Option<String>,
    pub fixed_fps: Option<u32>,
    pub exit_after_capture: bool,
    pub help: bool,
}

impl Default for Options {
    fn default() -> Self {
        Self {
            content: crate::install::Layout::current().content(),
            unturned: detect_retail_install(),
            map: "PEI".into(),
            seed: 1337,
            island: None,
            new_island: None,
            island_name: "Island".into(),
            island_tiles: [3, 3],
            lakes: false,
            zombies: !matches!(std::env::var("UG_NOZOMBIES").as_deref(), Ok("1"))
                && !matches!(std::env::var("UG_DEDICATED_NOZOMBIES").as_deref(), Ok("1")),
            network: crate::network::Target::Local,
            player_name: std::env::var("UG_USERNAME").unwrap_or_else(|_| "Survivor".into()),
            network_ready: false,
            vehicle: None,
            hitch: None,
            demo_furniture: false,
            play: false,
            headless: false,
            netlog: false,
            ticks: 250,
            screenshot: None,
            screenshot_frame: 90,
            screenshot_event: None,
            fixed_fps: None,
            exit_after_capture: true,
            help: false,
        }
    }
}

impl Options {
    pub fn parse(args: impl IntoIterator<Item = String>) -> Result<Self> {
        let mut options = Self::default();
        let mut args = args.into_iter();
        let mut listen = None;
        let mut connect = None;
        let mut dedicated = false;
        let mut port = None;
        let mut server_name = None;
        let mut max_players = None;
        while let Some(arg) = args.next() {
            let (key, inline) = arg
                .split_once('=')
                .map_or((arg.as_str(), None), |(k, v)| (k, Some(v)));
            let mut value = || {
                inline
                    .map(str::to_owned)
                    .or_else(|| args.next())
                    .ok_or_else(|| anyhow::anyhow!("{key} requires a value"))
            };
            match key {
                "--help" | "-h" => options.help = true,
                "--play" | "--peiplay" => options.play = true,
                "--headless" => options.headless = true,
                "--dedicated" => dedicated = true,
                "--netlog" => options.netlog = true,
                "--demo-furniture" => options.demo_furniture = true,
                "--lakes" => options.lakes = true,
                "--no-zombies" => options.zombies = false,
                "--hitch" => {
                    options.hitch = Some(inline.unwrap_or("trailer").to_ascii_lowercase())
                }
                "--stay" => options.exit_after_capture = false,
                "--map" => options.map = value()?,
                "--seed" => options.seed = value()?.parse()?,
                "--island" => options.island = Some(value()?.into()),
                "--new-island" => options.new_island = Some(value()?.into()),
                "--island-name" => options.island_name = value()?,
                "--island-tiles" => {
                    let text = value()?;
                    let (x, z) = text
                        .split_once('x')
                        .ok_or_else(|| anyhow::anyhow!("--island-tiles requires XxZ"))?;
                    options.island_tiles = [x.parse()?, z.parse()?];
                }
                "--listen" => listen = Some(value()?.parse::<u16>()?),
                "--connect" => connect = Some(value()?),
                "--port" => port = Some(value()?.parse::<u16>()?),
                "--name" => options.player_name = value()?,
                "--server-name" => server_name = Some(value()?),
                "--max-players" => max_players = Some(value()?.parse::<u8>()?),
                "--vehicle" => options.vehicle = Some(value()?.to_ascii_lowercase()),
                "--content" => options.content = value()?.into(),
                "--unturned" => options.unturned = Some(value()?.into()),
                "--ticks" => options.ticks = value()?.parse()?,
                "--shot" => options.screenshot = Some(value()?.into()),
                "--shot-frame" => options.screenshot_frame = value()?.parse()?,
                "--shot-event" => options.screenshot_event = Some(value()?),
                "--fixed-fps" => options.fixed_fps = Some(value()?.parse()?),
                "--" => {}
                _ => bail!("Unknown option {arg}. Use --help."),
            }
        }
        if [listen.is_some(), connect.is_some(), dedicated].iter().filter(|m| **m).count() > 1 {
            bail!("--listen, --connect and --dedicated are mutually exclusive");
        }
        if listen == Some(0) || port == Some(0) {
            bail!("Network port must be 1..65535");
        }
        if max_players.is_some_and(|n| !(1..=32).contains(&n)) {
            bail!("--max-players must be 1..32");
        }
        if dedicated {
            // Source Main.PortEnv: UG_PORT overrides the standard 47872.
            let port = match port {
                Some(port) => port,
                None => match std::env::var("UG_PORT").ok().filter(|p| !p.is_empty()) {
                    Some(text) => text
                        .parse::<u16>()
                        .ok()
                        .filter(|p| *p != 0)
                        .ok_or_else(|| anyhow::anyhow!("UG_PORT must be 1..65535"))?,
                    None => crate::network::DEFAULT_PORT,
                },
            };
            if options.headless || options.island.is_some() || options.new_island.is_some() {
                bail!("--dedicated hosts a retail map; it excludes --headless and finite islands");
            }
            if options.screenshot.is_some() || options.fixed_fps.is_some() {
                bail!("--dedicated has no renderer; --shot and --fixed-fps do not apply");
            }
            if options.vehicle.is_some() || options.hitch.is_some() || options.demo_furniture {
                bail!("--vehicle/--hitch/--demo-furniture stage props beside a local player; a dedicated server has none");
            }
            options.network = crate::network::Target::Dedicated {
                port,
                name: server_name.clone(),
                max_players,
            };
            options.play = true;
        }
        let port = port.unwrap_or(crate::network::DEFAULT_PORT);
        let server_name = server_name.unwrap_or_else(|| "Bevy world".to_owned());
        let max_players = max_players.unwrap_or(24);
        if let Some(host) = connect {
            let (host, port) = crate::network::parse_address(&host, &port.to_string())
                .map_err(anyhow::Error::msg)?;
            options.network = crate::network::Target::Client { host, port };
            options.play = true;
        } else if let Some(port) = listen {
            options.network = crate::network::Target::Listen {
                port,
                name: server_name,
                max_players,
            };
            options.play = true;
        }
        if options.network.networked() && options.map.eq_ignore_ascii_case("Procedural") {
            bail!("UDP sessions require a retail map inside the original shared coordinate range");
        }
        if options.island.is_some() && options.new_island.is_some() {
            bail!("--island and --new-island are mutually exclusive");
        }
        if options.island_tiles.iter().any(|n| !(1..=3).contains(n)) {
            bail!(
                "Native finite islands currently support 1..3 tiles per axis inside the shared wire frame"
            );
        }
        if options.island.is_some() || options.new_island.is_some() {
            if options.map.eq_ignore_ascii_case("Procedural") {
                bail!("Finite islands and the endless Procedural map are distinct entries");
            }
            if options.network.networked() {
                bail!(
                    "Finite UDP descriptor negotiation is not linked yet; use the local source map entry"
                );
            }
            if options.headless {
                bail!("Finite maps require the native world; --headless checks the core only");
            }
            options.play = true;
        }
        if options.headless && options.network.networked() {
            bail!(
                "--headless is a local simulation check; UDP host/client requires the game world"
            );
        }
        if options.fixed_fps == Some(0) {
            bail!("--fixed-fps must be greater than zero");
        }
        if options.ticks == 0 {
            bail!("--ticks must be greater than zero");
        }
        if options.screenshot_frame == 0 {
            bail!("--shot-frame must be greater than zero");
        }
        if let Some(vehicle) = &options.vehicle {
            if crate::vehicle::data::named(vehicle).is_none() {
                bail!(
                    "Unknown vehicle {vehicle}. Available vehicles: {}",
                    crate::vehicle::data::FLEET
                        .iter()
                        .map(|v| v.key.as_str())
                        .collect::<Vec<_>>()
                        .join(", ")
                );
            }
        }
        if let Some(trailer) = &options.hitch {
            let tows = options.vehicle.as_deref().and_then(crate::vehicle::data::named)
                .is_some_and(|d| d.spec.fifth_wheel != [0.; 3]);
            let towed = crate::vehicle::data::named(trailer).is_some_and(|d| d.spec.kingpin != [0.; 3]);
            if !tows || !towed {
                bail!("--hitch[=TRAILER] needs --vehicle with a hitch and a trailer key (e.g. --vehicle semi --hitch)");
            }
        }
        Ok(options)
    }
}

fn detect_retail_install() -> Option<PathBuf> {
    if let Some(path) = std::env::var_os("UG_UNTURNED_DIR") {
        return Some(path.into());
    }
    let mut candidates = vec![
        PathBuf::from("/mnt/c/Program Files (x86)/Steam/steamapps/common/Unturned"),
        PathBuf::from(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"),
    ];
    if let Some(home) = std::env::var_os("HOME") {
        candidates.push(PathBuf::from(home).join(".steam/steam/steamapps/common/Unturned"));
    }
    candidates
        .into_iter()
        .find(|path| path.join("Maps").is_dir())
}

pub const HELP: &str = "Unturned Bevy\n\
Usage: bevy/run.sh [options]  (a package: ./unturned-bevy [options])\n\
  --play                   Enter the world immediately\n\
  --dedicated              Headless dedicated server (no window/GPU, no local player)\n\
                           on UDP --port (or UG_PORT, default 47872); see DEDICATED_SERVER_PORT.md\n\
  --netlog                 Network diagnostics on stderr (also UG_NETLOG=1)\n\
  --connect HOST --port 47872  Probe and join an actual UDP server\n\
  --listen 47872           Host a UDP world and play locally\n\
  --name Survivor          Local player profile name\n\
  --server-name NAME --max-players 24  Listen server settings\n\
  --map PEI                Map (PEI, Washington, Yukon, or Procedural)\n\
  --unturned PATH          Retail install root (also UG_UNTURNED_DIR)\n\
  --content PATH           Bundled game/content directory (also UG_CONTENT_DIR)\n\
  --seed 1337              Procedural world seed\n\
  --new-island DIR         Generate original finite editor map in a new directory\n\
  --island DIR             Reopen that map without regenerating authored files\n\
  --island-name NAME --island-tiles 3x3  Finite generation settings\n\
  --lakes                  Cut inland ponds into a newly generated island\n\
  --no-zombies             Host without the zombie field (menu Zombies: Off)\n\
  --vehicle jeep          Spawn an original vehicle beside the player for testing\n\
  --hitch[=trailer]        With --vehicle: park that trailer, kingpin under the hitch\n\
  --demo-furniture        Original fallback bed at source map anchor (diagnostic)\n\
  --headless --ticks 250   Exercise the core bridge without a renderer\n\
  --shot PATH              Capture the rendered window and exit\n\
  --shot-frame 90          Wait this many rendered frames before capture\n\
  --shot-event NAME        Capture when an authoritative event is rendered\n\
  --fixed-fps N            Deterministic render diagnostic (source movie mode)\n\
  --stay                   Keep running; F12 repeats the capture\n\
  --help                   Show this help\n\
Paths: a package finds bridge/ and content/ beside the executable; a checkout\n\
uses its own. Override with UG_BRIDGE_DLL, UG_CONTENT_DIR, UG_DATA_DIR or\n\
UG_PACKAGE_ROOT.";

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn finite_entry_preserves_authored_directory_and_rejects_ambiguous_worlds() {
        let options = Options::parse(
            [
                "--new-island=/tmp/My Island",
                "--island-name=My Island",
                "--island-tiles=2x3",
                "--seed=17",
            ]
            .map(str::to_owned),
        )
        .unwrap();
        assert_eq!(options.new_island, Some(PathBuf::from("/tmp/My Island")));
        assert_eq!(options.island_name, "My Island");
        assert_eq!(options.island_tiles, [2, 3]);
        assert_eq!(options.seed, 17);
        assert!(options.play);
        for args in [
            vec!["--island=a", "--new-island=b"],
            vec!["--island=a", "--map=Procedural"],
            vec!["--island=a", "--connect=localhost"],
            vec!["--island=a", "--headless"],
            vec!["--new-island=a", "--island-tiles=0x3"],
            vec!["--new-island=a", "--island-tiles=4x3"],
        ] {
            assert!(Options::parse(args.into_iter().map(str::to_owned)).is_err());
        }
    }
    #[test]
    fn render_fixed_fps_is_explicit_and_rejects_zero() {
        assert_eq!(
            Options::parse(Vec::<String>::new()).unwrap().fixed_fps,
            None
        );
        assert_eq!(
            Options::parse(["--fixed-fps=50".into()]).unwrap().fixed_fps,
            Some(50)
        );
        assert!(Options::parse(["--fixed-fps=0".into()]).is_err());
    }
    #[test]
    fn hitch_harness_requires_a_fifth_wheel_vehicle() {
        let options = Options::parse(["--vehicle=semi".into(), "--hitch".into()]).unwrap();
        assert_eq!(options.hitch.as_deref(), Some("trailer"));
        let car = Options::parse(["--vehicle=jeep".into(), "--hitch=Dinky_Trailer".into()]).unwrap();
        assert_eq!(car.hitch.as_deref(), Some("dinky_trailer"));
        assert!(Options::parse(["--hitch".into()]).is_err());
        assert!(Options::parse(["--vehicle=huey".into(), "--hitch".into()]).is_err());
        assert!(Options::parse(["--vehicle=semi".into(), "--hitch=jeep".into()]).is_err());
    }
    #[test]
    fn paths_with_spaces_and_both_option_forms_are_preserved() {
        let options = Options::parse(
            [
                "--unturned",
                "C:/Program Files (x86)/Steam/Unturned",
                "--map=Yukon",
                "--play",
            ]
            .map(str::to_owned),
        )
        .unwrap();
        assert_eq!(
            options.unturned.unwrap(),
            PathBuf::from("C:/Program Files (x86)/Steam/Unturned")
        );
        assert_eq!(options.map, "Yukon");
        assert!(options.play);
    }
    #[test]
    fn unknown_or_missing_values_fail_instead_of_silently_selecting_defaults() {
        for args in [
            vec!["--unknown"],
            vec!["--map"],
            vec!["--ticks=0"],
            vec!["--vehicle"],
            vec!["--vehicle=imaginary_car"],
        ] {
            assert!(Options::parse(args.into_iter().map(str::to_owned)).is_err());
        }
    }
    #[test]
    fn vehicle_harness_accepts_actual_fleet_keys_and_preserves_regular_default() {
        assert!(Options::parse([]).unwrap().vehicle.is_none());
        for args in [vec!["--vehicle", "Huey"], vec!["--vehicle=huey"]] {
            assert_eq!(
                Options::parse(args.into_iter().map(str::to_owned))
                    .unwrap()
                    .vehicle
                    .as_deref(),
                Some("huey")
            );
        }
        for vehicle in crate::vehicle::data::FLEET.iter() {
            assert!(Options::parse([format!("--vehicle={}", vehicle.key)]).is_ok());
        }
    }
}

#[cfg(test)]
mod network_tests {
    use super::*;
    #[test]
    fn explicit_modes_validate_ports_and_prevent_ambiguous_or_unbounded_udp_worlds() {
        for args in [
            vec!["--listen=0"],
            vec!["--connect=a", "--listen=47872"],
            vec!["--connect=a", "--port=0"],
            vec!["--listen=47872", "--map=Procedural"],
            vec!["--listen=47872", "--max-players=33"],
        ] {
            assert!(Options::parse(args.into_iter().map(str::to_owned)).is_err());
        }
        let c = Options::parse(["--connect=localhost".into(), "--port=49000".into()]).unwrap();
        assert!(c.play);
        assert!(matches!(
            c.network,
            crate::network::Target::Client { port: 49000, .. }
        ));
        let h = Options::parse(["--listen=49001".into(), "--server-name=Friends".into()]).unwrap();
        assert!(matches!(
            h.network,
            crate::network::Target::Listen { port: 49001, .. }
        ));
    }
}
