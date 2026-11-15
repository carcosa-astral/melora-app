//! Compiles the UI, gives the exe Melora's icon, and puts the BASS DLLs next to the exe, where
//! Melora loads them from.

use std::path::Path;

fn main() {
    let config = slint_build::CompilerConfiguration::new().with_style("fluent-dark".into());
    if let Err(e) = slint_build::compile_with_config("ui/app.slint", config) {
        panic!("UI failed to compile: {e}"); // a build script failing loudly is the right behavior
    }

    // The icon Explorer and a pinned taskbar button show (the same .ico as 2.x).
    println!("cargo:rerun-if-changed=melora.ico");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let mut res = winresource::WindowsResource::new();
        res.set_icon("melora.ico");
        if let Err(e) = res.compile() {
            println!("cargo:warning=the exe gets no icon: {e}");
        }
    }

    // The exe's folder is the one named after the profile, somewhere above OUT_DIR
    // (how deep depends on the cargo version).
    let out = std::env::var("OUT_DIR").expect("cargo always sets OUT_DIR");
    let profile = std::env::var("PROFILE").expect("cargo always sets PROFILE");
    let Some(exe_dir) = Path::new(&out).ancestors().find(|p| p.ends_with(&profile)) else {
        println!("cargo:warning=couldn't find the {profile} folder above {out}");
        return;
    };
    for dll in [
        "bass.dll",
        "bassmix.dll",
        "bassflac.dll",
        "bassopus.dll",
        "bass_aac.dll",
        "basshls.dll",
    ] {
        let from = Path::new("../lib/bass").join(dll);
        println!("cargo:rerun-if-changed={}", from.display());
        let to = exe_dir.join(dll);
        // Already there (and maybe in use by a running Melora): nothing to do.
        let same = |p: &Path| std::fs::metadata(p).map(|m| m.len()).ok();
        if same(&from).is_some() && same(&from) == same(&to) {
            continue;
        }
        if let Err(e) = std::fs::copy(&from, &to) {
            println!("cargo:warning=couldn't copy {dll}: {e}");
        }
    }
}
