//! An owned, immutable assembly directory for one live game-core process.
use anyhow::{Context, Result, bail};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT_COPY: AtomicU64 = AtomicU64::new(0);

pub(super) struct RuntimeCopy {
    directory: PathBuf,
    dll: PathBuf,
}

impl RuntimeCopy {
    pub(super) fn from_dll(dll: &Path) -> Result<Self> {
        let source = dll.canonicalize().context("locating game-core assembly")?;
        if !source.is_file() {
            bail!("game-core assembly is not a file: {}", source.display());
        }
        let parent = source.parent().context("locating game-core dependencies")?;
        let filename = source.file_name().context("reading game-core filename")?;
        let temporary = std::env::temp_dir()
            .canonicalize()
            .context("locating temporary directory")?;
        if temporary.starts_with(parent) {
            bail!(
                "game-core output must be in a dedicated directory outside the temporary-directory root"
            );
        }
        let directory = loop {
            let sequence = NEXT_COPY.fetch_add(1, Ordering::Relaxed);
            let candidate = temporary.join(format!(
                "unturned-bevy-core-{}-{sequence}",
                std::process::id()
            ));
            match fs::create_dir(&candidate) {
                Ok(()) => break candidate,
                Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
                Err(error) => return Err(error).context("creating private game-core directory"),
            }
        };
        // Construct the cleanup guard before copying, including partial failures.
        let copy = Self {
            dll: directory.join(filename),
            directory,
        };
        copy_directory(parent, &copy.directory).context("copying game-core runtime")?;
        Ok(copy)
    }

    pub(super) fn dll(&self) -> &Path {
        &self.dll
    }
}

impl Drop for RuntimeCopy {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.directory);
    }
}

fn copy_directory(source: &Path, destination: &Path) -> Result<()> {
    for entry in fs::read_dir(source)? {
        let entry = entry?;
        let target = destination.join(entry.file_name());
        let kind = entry.file_type()?;
        if kind.is_dir() {
            fs::create_dir(&target)?;
            copy_directory(&entry.path(), &target)?;
        } else if kind.is_file() {
            // A hard link would still expose the running process to overwrites.
            fs::copy(entry.path(), target)?;
        } else {
            bail!(
                "unsupported runtime dependency at {}",
                entry.path().display()
            );
        }
    }
    Ok(())
}
