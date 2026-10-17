//! Where a running client finds its game bridge, bundled content and writable
//! cache, so a release can live anywhere. Checked in this order:
//!
//! 1. explicit environment overrides: `UG_BRIDGE_DLL`, `UG_CONTENT_DIR`,
//!    `UG_DATA_DIR` per path and `UG_PACKAGE_ROOT` for the whole layout
//!    (`--content` on the command line still beats `UG_CONTENT_DIR`);
//! 2. a relocatable package: the executable's own directory holds
//!    [`PACKAGE_MARKER`] (written by `bevy/tools/package.sh`) with `bridge/` and
//!    `content/` beside it;
//! 3. the source checkout this binary was compiled from (`cargo run`), whose
//!    paths are exactly the ones the dev workflow has always used.
//!
//! The overrides apply to every run, including `cargo run` and the tests: a
//! stray `UG_CONTENT_DIR`, `UG_DATA_DIR` or `UG_PACKAGE_ROOT` in the shell also
//! redirects the checkout (as `UG_BRIDGE_DLL` always did).
//!
//! A package never falls back to the checkout: a missing packaged bridge is an
//! error naming the package path, not a silent launch of whatever the compiling
//! machine left in `bevy/bridge/bin`.
//!
//! This module is std-only on purpose (plus `crate::settings` for the user
//! directory), so its tests do not need Bevy.
use std::{
    ffi::OsString,
    path::{Path, PathBuf},
    sync::OnceLock,
};

/// Written beside the executable by `bevy/tools/package.sh`.
pub const PACKAGE_MARKER: &str = "unturned-bevy-package.json";
const BRIDGE_DLL: &str = "UnturnedBevy.Bridge.dll";
/// `bevy/run.sh` and `bevy/check.sh` build here.
const DEV_BRIDGE: &str = "bridge/bin/Debug/net8.0";
const PACKAGE_BRIDGE: &str = "bridge";
const DEV_CONTENT: &str = "../game/content";
const PACKAGE_CONTENT: &str = "content";
const DEV_DATA: &str = ".local";

type Env<'a> = &'a dyn Fn(&str) -> Option<OsString>;

fn process_env(key: &str) -> Option<OsString> {
    std::env::var_os(key).filter(|value| !value.is_empty())
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Layout {
    /// A relocatable package rooted at this directory.
    Package(PathBuf),
    /// The `bevy/` directory of the source checkout (`CARGO_MANIFEST_DIR`).
    Dev(PathBuf),
}

impl Layout {
    /// The running process's layout, resolved once.
    pub fn current() -> &'static Layout {
        static LAYOUT: OnceLock<Layout> = OnceLock::new();
        LAYOUT.get_or_init(|| {
            Self::resolve(
                &process_env,
                std::env::current_exe().ok().as_deref(),
                Path::new(env!("CARGO_MANIFEST_DIR")),
            )
        })
    }

    fn resolve(env: Env, exe: Option<&Path>, dev: &Path) -> Self {
        if let Some(root) = env("UG_PACKAGE_ROOT") {
            return Self::Package(root.into());
        }
        // current_exe resolves symlinks on Linux, so a launcher shortcut or a
        // ~/bin link to the packaged binary still finds its siblings.
        if let Some(directory) = exe.and_then(Path::parent)
            && directory.join(PACKAGE_MARKER).is_file()
        {
            return Self::Package(directory.to_path_buf());
        }
        Self::Dev(dev.to_path_buf())
    }

    pub fn root(&self) -> &Path {
        match self {
            Self::Package(root) | Self::Dev(root) => root,
        }
    }

    pub fn bridge_dll(&self) -> PathBuf {
        self.bridge_dll_in(&process_env)
    }

    fn bridge_dll_in(&self, env: Env) -> PathBuf {
        if let Some(dll) = env("UG_BRIDGE_DLL") {
            return dll.into();
        }
        match self {
            Self::Package(root) => root.join(PACKAGE_BRIDGE).join(BRIDGE_DLL),
            Self::Dev(root) => root.join(DEV_BRIDGE).join(BRIDGE_DLL),
        }
    }

    /// The bundled `game/content` tree (meshes, textures, audio, `.dat`s).
    pub fn content(&self) -> PathBuf {
        self.content_in(&process_env)
    }

    fn content_in(&self, env: Env) -> PathBuf {
        if let Some(content) = env("UG_CONTENT_DIR") {
            return content.into();
        }
        match self {
            Self::Package(root) => root.join(PACKAGE_CONTENT),
            Self::Dev(root) => root.join(DEV_CONTENT),
        }
    }

    /// Writable scratch for generated files (procedural terrain JSON). A package
    /// may be installed read-only, so it writes beside the user's settings.
    pub fn data_dir(&self) -> PathBuf {
        self.data_dir_in(&process_env, || {
            crate::settings::Settings::path()
                .parent()
                .map_or_else(|| PathBuf::from("."), Path::to_path_buf)
        })
    }

    fn data_dir_in(&self, env: Env, user: impl FnOnce() -> PathBuf) -> PathBuf {
        if let Some(directory) = env("UG_DATA_DIR") {
            return directory.into();
        }
        match self {
            Self::Package(_) => user().join("cache"),
            Self::Dev(root) => root.join(DEV_DATA),
        }
    }

    /// Where BugReportCore's BuildStamp looks for `.git` in a checkout run. A
    /// package sends its [`Self::package_marker`] commit instead and is never
    /// searched upward (it may sit inside an unrelated repository).
    pub fn project_dir(&self) -> &Path {
        match self {
            Self::Package(root) => root,
            Self::Dev(root) => root.parent().unwrap_or(root),
        }
    }

    /// The marker recording what was packaged (`commit`, `dirty`), if a package.
    pub fn package_marker(&self) -> Option<PathBuf> {
        match self {
            Self::Package(root) => Some(root.join(PACKAGE_MARKER)),
            Self::Dev(_) => None,
        }
    }

    /// Whether the bridge must run from a private copy. The checkout's build
    /// output is rebuilt in place while games run, so it is copied (see
    /// `bridge::runtime`). A package's own bridge is immutable: copying it
    /// would put the whole .NET runtime in the temp dir on every launch, fail
    /// on a `noexec` temp mount and leak after a SIGKILL. An override pointing
    /// outside the package is treated like a checkout build.
    pub fn needs_private_copy(&self, dll: &Path) -> bool {
        match self {
            Self::Package(root) => !dll.starts_with(root),
            Self::Dev(_) => true,
        }
    }

    /// What to tell a player whose bridge is missing.
    pub fn bridge_hint(&self) -> &'static str {
        self.bridge_hint_in(&process_env)
    }

    fn bridge_hint_in(&self, env: Env) -> &'static str {
        if env("UG_BRIDGE_DLL").is_some() {
            return "UG_BRIDGE_DLL names a file that does not exist; fix or unset it.";
        }
        match self {
            Self::Package(_) => "The package is incomplete; reinstall it or rerun bevy/tools/package.sh.",
            Self::Dev(_) => "Run bevy/run.sh to build it.",
        }
    }
}

/// A self-contained `dotnet publish` carries its own runtime and starts through
/// its apphost; a framework-dependent build (the dev `bin/Debug` output also has
/// an apphost) runs on the installed `dotnet`. The runtimeconfig decides:
/// only a self-contained one lists `includedFrameworks`.
pub fn self_contained_apphost(dll: &Path) -> Option<PathBuf> {
    let config = std::fs::read_to_string(dll.with_extension("runtimeconfig.json")).ok()?;
    if !config.contains("\"includedFrameworks\"") {
        return None;
    }
    let apphost = dll.with_extension(std::env::consts::EXE_EXTENSION);
    apphost.is_file().then_some(apphost)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn scratch(name: &str) -> PathBuf {
        let directory = std::env::temp_dir().join(format!(
            "unturned-bevy-install-{}-{name}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&directory);
        fs::create_dir_all(&directory).unwrap();
        directory
    }

    fn no_env(_: &str) -> Option<OsString> {
        None
    }

    fn package(name: &str) -> (PathBuf, PathBuf) {
        let root = scratch(name);
        fs::write(root.join(PACKAGE_MARKER), "{}").unwrap();
        let exe = root.join("unturned-bevy");
        fs::write(&exe, "").unwrap();
        (root, exe)
    }

    #[test]
    fn executable_beside_the_marker_is_a_package_and_finds_its_siblings() {
        let (root, exe) = package("exe-relative");
        let dev = scratch("exe-relative-dev");
        let layout = Layout::resolve(&no_env, Some(&exe), &dev);
        assert_eq!(layout, Layout::Package(root.clone()));
        assert_eq!(
            layout.bridge_dll_in(&no_env),
            root.join("bridge/UnturnedBevy.Bridge.dll")
        );
        assert_eq!(layout.content_in(&no_env), root.join("content"));
        let user = root.join("user");
        assert_eq!(layout.data_dir_in(&no_env, || user.clone()), user.join("cache"));
        assert_eq!(layout.project_dir(), root);
    }

    #[test]
    fn a_package_never_falls_back_to_the_checkout_that_compiled_it() {
        // The dev bridge and content exist; the packaged ones do not.
        let (root, exe) = package("no-fallback");
        let dev = scratch("no-fallback-dev");
        fs::create_dir_all(dev.join(DEV_BRIDGE)).unwrap();
        fs::write(dev.join(DEV_BRIDGE).join(BRIDGE_DLL), "").unwrap();
        let layout = Layout::resolve(&no_env, Some(&exe), &dev);
        let bridge = layout.bridge_dll_in(&no_env);
        assert!(bridge.starts_with(&root), "{}", bridge.display());
        assert!(!bridge.is_file());
        assert!(layout.content_in(&no_env).starts_with(&root));
    }

    #[test]
    fn without_a_marker_the_checkout_layout_is_unchanged() {
        let directory = scratch("dev");
        let exe = directory.join("unturned-bevy");
        let dev = Path::new("/checkout/bevy");
        let layout = Layout::resolve(&no_env, Some(&exe), dev);
        assert_eq!(layout, Layout::Dev(dev.into()));
        assert_eq!(
            layout.bridge_dll_in(&no_env),
            dev.join("bridge/bin/Debug/net8.0/UnturnedBevy.Bridge.dll")
        );
        assert_eq!(layout.content_in(&no_env), dev.join("../game/content"));
        assert_eq!(
            layout.data_dir_in(&no_env, || unreachable!()),
            dev.join(".local")
        );
        assert_eq!(layout.project_dir(), Path::new("/checkout"));
        // No executable path at all (exotic platforms) is also the checkout.
        assert_eq!(Layout::resolve(&no_env, None, dev), Layout::Dev(dev.into()));
    }

    #[test]
    fn this_test_binary_runs_from_the_checkout() {
        let exe = std::env::current_exe().unwrap();
        let manifest = Path::new(env!("CARGO_MANIFEST_DIR"));
        let layout = Layout::resolve(&no_env, Some(&exe), manifest);
        assert_eq!(layout, Layout::Dev(manifest.into()));
        assert!(layout.bridge_dll_in(&no_env).ends_with("net8.0/UnturnedBevy.Bridge.dll"));
        assert!(layout.content_in(&no_env).join("CREDITS.md").is_file());
    }

    #[test]
    fn environment_overrides_beat_both_layouts() {
        let (root, exe) = package("overrides");
        let env = |key: &str| -> Option<OsString> {
            match key {
                "UG_BRIDGE_DLL" => Some("/frozen/UnturnedBevy.Bridge.dll".into()),
                "UG_CONTENT_DIR" => Some("/shared/content".into()),
                "UG_DATA_DIR" => Some("/scratch/data".into()),
                _ => None,
            }
        };
        for layout in [
            Layout::resolve(&no_env, Some(&exe), Path::new("/checkout/bevy")),
            Layout::Dev("/checkout/bevy".into()),
        ] {
            assert_eq!(
                layout.bridge_dll_in(&env),
                Path::new("/frozen/UnturnedBevy.Bridge.dll")
            );
            assert_eq!(layout.content_in(&env), Path::new("/shared/content"));
            assert_eq!(
                layout.data_dir_in(&env, || unreachable!()),
                Path::new("/scratch/data")
            );
        }
        // UG_PACKAGE_ROOT selects a package even for a binary with no marker.
        let moved = |key: &str| (key == "UG_PACKAGE_ROOT").then(|| root.clone().into_os_string());
        let layout = Layout::resolve(&moved, Some(Path::new("/usr/bin/x")), Path::new("/c/bevy"));
        assert_eq!(layout, Layout::Package(root.clone()));
        assert_eq!(layout.content_in(&no_env), root.join("content"));
    }

    #[test]
    fn only_a_package_runs_its_own_bridge_in_place() {
        let (root, exe) = package("in-place");
        let package = Layout::resolve(&no_env, Some(&exe), Path::new("/checkout/bevy"));
        assert!(!package.needs_private_copy(&package.bridge_dll_in(&no_env)));
        // An override outside the package may be a build output being rebuilt.
        assert!(package.needs_private_copy(Path::new("/checkout/bevy/bridge/bin/Debug/net8.0/UnturnedBevy.Bridge.dll")));
        assert!(package.needs_private_copy(&root.with_extension("other").join("UnturnedBevy.Bridge.dll")));
        let dev = Layout::Dev("/checkout/bevy".into());
        assert!(dev.needs_private_copy(&dev.bridge_dll_in(&no_env)));
        assert_eq!(package.package_marker(), Some(root.join(PACKAGE_MARKER)));
        assert_eq!(dev.package_marker(), None);
    }

    #[test]
    fn a_missing_override_bridge_names_the_variable_not_a_reinstall() {
        let overridden = |key: &str| (key == "UG_BRIDGE_DLL").then(|| OsString::from("/nope.dll"));
        for layout in [Layout::Package("/pkg".into()), Layout::Dev("/checkout/bevy".into())] {
            assert!(layout.bridge_hint_in(&overridden).contains("UG_BRIDGE_DLL"));
            assert!(!layout.bridge_hint_in(&no_env).contains("UG_BRIDGE_DLL"));
        }
        assert!(Layout::Package("/pkg".into()).bridge_hint_in(&no_env).contains("reinstall"));
    }

    #[test]
    fn only_a_self_contained_publish_starts_through_its_apphost() {
        let directory = scratch("apphost");
        let dll = directory.join(BRIDGE_DLL);
        let config = directory.join("UnturnedBevy.Bridge.runtimeconfig.json");
        let apphost = dll.with_extension(std::env::consts::EXE_EXTENSION);
        fs::write(&dll, "").unwrap();
        fs::write(&apphost, "").unwrap();
        // Framework-dependent (dev bin/Debug): run on the installed dotnet.
        fs::write(
            &config,
            r#"{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}"#,
        )
        .unwrap();
        assert_eq!(self_contained_apphost(&dll), None);
        fs::write(
            &config,
            r#"{"runtimeOptions":{"tfm":"net8.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"8.0.21"}]}}"#,
        )
        .unwrap();
        assert_eq!(self_contained_apphost(&dll), Some(apphost.clone()));
        fs::remove_file(&apphost).unwrap();
        assert_eq!(self_contained_apphost(&dll), None);
        fs::remove_file(&config).unwrap();
        assert_eq!(self_contained_apphost(&dll), None);
    }
}
