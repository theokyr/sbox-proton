# sbox-proton

This fork carries experimental Linux/Proton support for building and running the Windows s&box editor from a Linux checkout. It keeps Facepunch's upstream source intact while adding path normalization, a Proton-targeted build pipeline, deployment helpers, fallback fonts, runtime compiler references, and Hammer/cloud asset fixes needed by the editor under Proton.

The intended workflow is:

1. Keep `upstream` pointed at Facepunch's repository.
2. Build Windows-managed artifacts on Linux with the local .NET SDK.
3. Reuse the native Windows artifacts downloaded by the build pipeline.
4. Deploy only the changed editor/runtime artifacts into the Steam s&box install.
5. Launch through Steam/Proton so Steam API, workshop/package auth, and the editor context are correct.

## Proton Fork Requirements

This workflow has been tested on Linux with Steam and Proton. You need:

* Steam with s&box installed and validated.
* A Proton compatibility tool for the s&box editor app.
* .NET 10 SDK on the Linux host.
* Windows .NET Desktop Runtime installed in the editor Wine prefix.
* `rsync`.
* `git`.
* Fallback fonts for editor icons/emoji, such as `NotoColorEmoji` and Material Symbols or Material Icons. The Proton titlebar/window-control icons also depend on this staged Material Icons path.

The scripts default to a persistent .NET CLI home under your user directory so the build does not depend on Wine's `Z:/.local` path behavior.

## Proton Prefix .NET Runtime Setup

The Linux host SDK is used for building, but the editor process itself runs inside Steam's Proton prefix and needs the Windows .NET Desktop Runtime there too. The editor may ask to install .NET, but under Proton it will not reliably install it for you.

Install the Windows x64 .NET 10 Desktop Runtime into the editor prefix. This fork has been tested with editor app id `2129370`; adjust the app id if your Steam install uses a different one.

Using `protontricks`:

```bash
protontricks 2129370 --gui
```

Then run the downloaded Windows Desktop Runtime installer from the protontricks file picker.

Or run the installer directly with Wine against the Steam compatdata prefix:

```bash
export STEAM_COMPAT_DATA_PATH="$HOME/.local/share/Steam/steamapps/compatdata/2129370"
WINEPREFIX="$STEAM_COMPAT_DATA_PATH/pfx" wine "$HOME/Downloads/windowsdesktop-runtime-10.0.0-win-x64.exe"
```

You can verify the prefix contains the runtime with:

```bash
WINEPREFIX="$STEAM_COMPAT_DATA_PATH/pfx" wine dotnet --list-runtimes
```

The output should include `Microsoft.WindowsDesktop.App 10.0.x`.

## Proton Fork Usage

Build and deploy to the detected Steam s&box install:

```bash
./build-and-deploy.sh --apply
```

Force a fresh download of public/native artifacts:

```bash
./build-and-deploy.sh --refresh-artifacts --apply
```

Use `--refresh-artifacts` after pulling or merging upstream Facepunch changes, or any time the editor reports an interop hash mismatch between managed and native code. The wrapper can reuse existing `game/bin/win64` artifacts for faster local rebuilds; after upstream moves, stale Source 2/native DLLs can leave freshly built managed assemblies out of sync with the deployed engine.

Preview deployment without copying files:

```bash
scripts/deploy-proton-build.sh
```

Deploy only, after a build has already produced artifacts:

```bash
scripts/deploy-proton-build.sh --apply
```

If Steam is installed somewhere nonstandard, pass an explicit destination:

```bash
./build-and-deploy.sh --apply --dest "$HOME/.local/share/Steam/steamapps/common/sbox"
```

The deploy script stages .NET reference assemblies for the in-editor compiler and copies discovered fallback fonts into `game/fonts/proton`. Hammer cloud materials are staged into each project's generated library at `Libraries/CloudAssets/Assets` so native Source 2 resource loading can resolve package material and texture paths under Proton.

## Proton Path Troubleshooting

When debugging editor launch or Hammer/resource-compiler failures under Proton, treat every path crossing from managed code into Source 2 native code as a Windows-facing contract:

* Normalize Wine drive paths such as `Z:\home\...` and nonstandard drive aliases before using them as host filesystem paths.
* Prefer Proton-visible native search paths for the resource compiler. Raw Linux paths can be reinterpreted into invalid mixed paths by Windows-side Source 2 code.
* Keep project-owned `.sbox/cloud` mounted. Compiled cloud package resources, including shader resources referenced by project materials, can live there.
* Normalize map references before native VPK lookup so `maps/example.vmap` resolves to `example.vpk`, not a doubled `maps/maps/...` path.

## Proton Font/Icon Troubleshooting

Editor and tool UI icons should render through bundled or staged fallback fonts under `game/fonts/`, especially `game/fonts/proton/MaterialIcons-Regular.ttf`. If toolbar or titlebar icons show as boxes, stray letters, or private-use glyphs under Proton, first check whether the control is drawing from a Windows-only font such as Segoe Fluent Icons or Segoe MDL2 Assets. Prefer routing shared editor icon controls through the staged Material Icons path when a matching ligature exists, then rebuild and deploy with:

```bash
./build-and-deploy.sh --apply
```

When verifying a deployed UI fix, compare the local and Steam install managed DLLs before assuming Proton is still using the new code.

## Public Fork Hygiene

Before pushing this fork to a public remote:

* Use synthetic paths and project identifiers in tests and docs.
* Leave local project caches, Steam install paths, and generated menu transient assets out of commits.
* Review `git status --short` after `./build-and-deploy.sh --apply`; deployment can leave tracked runtime config files or generated assets dirty in the working tree.
* Keep fork-specific notes above this section and leave the upstream README body intact.

## Upstream README

<div align="center">
  <img src="https://sbox.game/img/sbox-logo-square.svg" width="80px" alt="s&box logo">

  [Website] | [Getting Started] | [Forums] | [Documentation] | [Contributing]
</div>

[Website]: https://sbox.game/
[Getting Started]: https://sbox.game/dev/doc/about/getting-started/first-steps/
[Forums]: https://sbox.game/f/
[Documentation]: https://sbox.game/dev/doc/
[Contributing]: CONTRIBUTING.md

# s&box

s&box is a modern game engine, built on Valve's Source 2 and the latest .NET technology, it provides a modern intuitive editor for creating games.

![s&box editor](https://files.facepunch.com/matt/1b2211b1/sbox-dev_FoZ5NNZQTi.jpg)

If your goal is to create games using s&box, please start with the [getting started guide](https://sbox.game/dev/doc/about/getting-started/first-steps/).
This repository is for building the engine from source for those who want to contribute to the development of the engine.

## Getting the Engine

### Steam

You can download and install the s&box editor directly from [Steam](https://sbox.game/give-me-that).

### Compiling from Source

If you want to build from source, this repository includes all the necessary files to compile the engine yourself.

#### Prerequisites

* [Git](https://git-scm.com/install/windows)
* [Visual Studio 2026](https://visualstudio.microsoft.com/)
* [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download)

#### Building

```bash
# Clone the repo
git clone https://github.com/Facepunch/sbox-public.git
```

Once you've cloned the repo simply run `Bootstrap.bat` which will download dependencies and build the engine.

The game and editor can be run from the binaries in the game folder.

## Contributing

If you would like to contribute to the engine, please see the [contributing guide](CONTRIBUTING.md).

If you want to report bugs or request new features, see [sbox-issues](https://github.com/Facepunch/sbox-public/issues/).

## Documentation

Full documentation, tutorials, and API references are available at [sbox.game/dev/](https://sbox.game/dev/).

## License

The s&box engine source code is licensed under the [MIT License](LICENSE.md).

Certain native binaries in `game/bin` are not covered by the MIT license. These binaries are distributed under the s&box EULA. You must agree to the terms of the EULA to use them.

This project includes third-party components that are separately licensed.
Those components are not covered by the MIT license above and remain subject
to their original licenses as indicated in `game/thirdpartylegalnotices`.
