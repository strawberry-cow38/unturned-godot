use anyhow::{Result, bail};
use std::path::PathBuf;

#[derive(Debug, Clone)]
pub struct Options {
    pub content: PathBuf,
    pub unturned: Option<PathBuf>,
    pub map: String,
    pub seed: i32,
    pub vehicle: Option<String>,
    pub play: bool,
    pub headless: bool,
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
            content: PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../game/content"),
            unturned: detect_retail_install(),
            map: "PEI".into(),
            seed: 1337,
            vehicle: None,
            play: false,
            headless: false,
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
                "--stay" => options.exit_after_capture = false,
                "--map" => options.map = value()?,
                "--seed" => options.seed = value()?.parse()?,
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
Usage: bevy/run.sh [options]\n\
  --play                   Enter the world immediately\n\
  --map PEI                Map (PEI, Washington, Yukon, or Procedural)\n\
  --unturned PATH          Retail install root (also UG_UNTURNED_DIR)\n\
  --content PATH           Bundled game/content directory\n\
  --seed 1337              Procedural world seed\n\
  --vehicle jeep          Spawn an original vehicle beside the player for testing\n\
  --headless --ticks 250   Exercise the core bridge without a renderer\n\
  --shot PATH              Capture the rendered window and exit\n\
  --shot-frame 90          Wait this many rendered frames before capture\n\
  --shot-event NAME        Capture when an authoritative event is rendered\n\
  --fixed-fps N            Deterministic render diagnostic (source movie mode)\n\
  --stay                   Keep running; F12 repeats the capture\n\
  --help                   Show this help";

#[cfg(test)]
mod tests {
    use super::*;
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
