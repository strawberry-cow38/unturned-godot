use super::*;
use crate::{bridge::CoreBridge, cli::Options, world::WorldConfig};
use bevy::{
    asset::RenderAssetUsages,
    render::render_resource::{Extent3d, TextureDimension, TextureFormat},
};
use serde_json::json;

pub(crate) fn prepare(
    bridge: &CoreBridge,
    options: &mut Options,
    config: &mut WorldConfig,
) -> anyhow::Result<()> {
    let Some(path) = options.island.as_ref().or(options.new_island.as_ref()) else {
        return Ok(());
    };
    let entry = Entry {
        directory: path.clone(),
        create: options.new_island.is_some(),
        name: options.island_name.clone(),
        seed: options.seed,
        tiles: options.island_tiles,
        lakes: options.lakes,
    };
    let (map, seed) = open(bridge, &options.content, options.unturned.as_deref(), &entry, config)?;
    options.map = map;
    options.seed = seed;
    Ok(())
}

/// One finite editor-map entry: `--new-island`/Generate Island (create) or
/// `--island`/the Proc tab (reopen without regenerating authored files).
#[derive(Clone, Debug)]
pub(crate) struct Entry {
    pub directory: PathBuf,
    pub create: bool,
    pub name: String,
    pub seed: i32,
    pub tiles: [usize; 2],
    pub lakes: bool,
}

/// Generate or reopen through the bridge and point `config` at the result.
/// Returns the session map key and the descriptor's seed.
pub(crate) fn open(
    bridge: &CoreBridge,
    content: &Path,
    unturned: Option<&Path>,
    entry: &Entry,
    config: &mut WorldConfig,
) -> anyhow::Result<(String, i32)> {
    let environment = unturned.map(|p| p.join("Maps/PEI/Environment"));
    if entry.create
        && !environment
            .as_ref()
            .is_some_and(|p| p.join("Roads.dat").is_file())
    {
        anyhow::bail!(
            "Finite generation needs original PEI road definitions: supply --unturned PATH"
        );
    }
    let reply = bridge.request(
        "finiteMap",
        json!({"directory":entry.directory,"create":entry.create,
        "contentRoot":content,"environmentRoot":environment,
        "name":entry.name,"seed":entry.seed,
        "tilesX":entry.tiles[0],"tilesZ":entry.tiles[1],"lakes":entry.lakes}),
    )?;
    let root = PathBuf::from(
        reply["result"]["directory"]
            .as_str()
            .ok_or_else(|| anyhow::anyhow!("missing finite root"))?,
    );
    let finite = FiniteWorld::load(&root)?;
    if finite.descriptor.tiles_x > 3 || finite.descriptor.tiles_z > 3 {
        anyhow::bail!(
            "This native entry currently supports finite maps up to 3x3 tiles inside the shared wire frame"
        );
    }
    persist_chart(&root, finite.descriptor.seed)?;
    let map = format!("Island_{}", finite.descriptor.name);
    config.map_name = map.clone();
    config.map_root = None;
    config.procedural_path = None;
    config.finite_path = Some(root);
    config.finite_spawn = crate::player::vector(&reply["result"]["initialSpawn"]);
    config.finite_population = (!reply["result"]["population"].is_null())
        .then(|| reply["result"]["population"].clone());
    Ok((map, finite.descriptor.seed))
}

/// Source ProcIslandMap writes once and rebinds on reopen. Preserve an existing
/// PNG exactly; convert raw RGB bytes without color-space or filtering changes.
pub(super) fn persist_chart(root: &Path, seed: i32) -> anyhow::Result<()> {
    let image = root.join(format!("island_{seed}_map.png"));
    if image.is_file() {
        return Ok(());
    }
    let raw = root.join(format!("island_{seed}_map.rgb"));
    if !raw.is_file() {
        return Ok(());
    }
    let bytes = fs::read(raw)?;
    if bytes.len() != 1024 * 1024 * 3 {
        anyhow::bail!("invalid source chart RGB length");
    }
    let rgba = bytes
        .chunks_exact(3)
        .flat_map(|p| [p[0], p[1], p[2], 255])
        .collect();
    let png = Image::new(
        Extent3d {
            width: 1024,
            height: 1024,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        rgba,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::MAIN_WORLD,
    )
    .try_into_dynamic()?;
    let nonce = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)?
        .as_nanos();
    let temp = root.join(format!(".chart-{}-{nonce}.png", std::process::id()));
    let result = (|| -> anyhow::Result<()> {
        png.save(&temp)?;
        fs::rename(&temp, &image)?;
        Ok(())
    })();
    if temp.exists() {
        let _ = fs::remove_file(temp);
    }
    result
}
