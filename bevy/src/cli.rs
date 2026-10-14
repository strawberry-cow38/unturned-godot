use anyhow::{Result, bail};
use std::path::PathBuf;

#[derive(Debug, Clone)]
pub struct Options {
    pub content: PathBuf,
    pub unturned: Option<PathBuf>,
    pub map: String,
    pub seed: i32,
    pub play: bool,
    pub headless: bool,
    pub ticks: u32,
    pub screenshot: Option<PathBuf>,
    pub screenshot_frame: u32,
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
            play: false,
            headless: false,
            ticks: 250,
            screenshot: None,
            screenshot_frame: 90,
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
                "--content" => options.content = value()?.into(),
                "--unturned" => options.unturned = Some(value()?.into()),
                "--ticks" => options.ticks = value()?.parse()?,
                "--shot" => options.screenshot = Some(value()?.into()),
                "--shot-frame" => options.screenshot_frame = value()?.parse()?,
                "--" => {}
                _ => bail!("Unknown option {arg}. Use --help."),
            }
        }
        if options.ticks == 0 {
            bail!("--ticks must be greater than zero");
        }
        if options.screenshot_frame == 0 {
            bail!("--shot-frame must be greater than zero");
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
  --headless --ticks 250   Exercise the core bridge without a renderer\n\
  --shot PATH              Capture the rendered window and exit\n\
  --shot-frame 90          Wait this many rendered frames before capture\n\
  --stay                   Keep running after capture\n\
  --help                   Show this help";

#[cfg(test)]
mod tests {
    use super::*;
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
        for args in [vec!["--unknown"], vec!["--map"], vec!["--ticks=0"]] {
            assert!(Options::parse(args.into_iter().map(str::to_owned)).is_err());
        }
    }
}
