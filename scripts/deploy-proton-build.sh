#!/usr/bin/env bash
set -euo pipefail

usage() {
	cat <<'USAGE'
Usage: scripts/deploy-proton-build.sh [--apply] [--steam-root PATH] [--dest PATH] [--font-source PATH] [--no-fonts] [--no-refs] [--full-native]

	Copies the Proton-friendly build artifacts from this checkout's game/ folder
	into the Steam s&box editor install. The script defaults to dry-run.
	Pass --apply to copy.

Options:
  --apply            Actually copy files. Without this, rsync runs in dry-run mode.
  --steam-root PATH  Steam root to scan. Defaults to common Linux Steam paths.
  --dest PATH        Explicit s&box install directory. Skips Steam manifest scan.
  --font-source PATH Copy additional .ttf/.otf fonts from PATH into game/fonts/proton.
  --no-fonts         Do not stage or deploy Proton fallback fonts.
  --no-refs          Do not stage or deploy .NET reference assemblies for the addon compiler.
  --full-native      Deploy all game/bin/win64 native artifacts. Default deploys a focused editor/tool set.
  --help            Show this help.

Synced content:
  game/.version
  game/*.exe, game/*.dll, game/*.json, game/*.runtimeconfig.json
  focused game/bin/win64 editor, Hammer, and resource compiler artifacts
  game/bin/managed/
  game/bin/managed/refs/
  game/bin/*.txt, game/bin/*.json
  game/fonts/
  game/addons/*/Code/, game/addons/*/code/, and game/editor/*/Code/
  compiled game assets matching game/{addons,core,config,editor,mount,samples,templates}/**/*_c
USAGE
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
apply=0
steam_root="${STEAM_ROOT:-}"
dest=""
stage_fonts=1
stage_refs=1
full_native=0
font_sources=()

while [[ $# -gt 0 ]]; do
	case "$1" in
		--apply)
			apply=1
			shift
			;;
		--steam-root)
			steam_root="${2:-}"
			shift 2
			;;
			--dest)
				dest="${2:-}"
				shift 2
				;;
			--font-source)
				font_sources+=("${2:-}")
				shift 2
				;;
			--no-fonts)
				stage_fonts=0
				shift
				;;
			--no-refs)
				stage_refs=0
				shift
				;;
			--full-native)
				full_native=1
				shift
				;;
		--help|-h)
			usage
			exit 0
			;;
		*)
			echo "Unknown argument: $1" >&2
			usage >&2
			exit 2
			;;
	esac
done

if ! command -v rsync >/dev/null 2>&1; then
	echo "rsync is required." >&2
	exit 1
fi

if [[ ! -d "$repo_root/game" ]]; then
	echo "Could not find game/ under repo root: $repo_root" >&2
	exit 1
fi

steam_roots=()
if [[ -n "$steam_root" ]]; then
	steam_roots+=("$steam_root")
else
	steam_roots+=("$HOME/.local/share/Steam")
	steam_roots+=("$HOME/.steam/steam")
	steam_roots+=("$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam")
fi

read_acf_value() {
	local key="$1"
	local file="$2"
	awk -v key="$key" '
		$0 ~ "\"" key "\"" {
			line = $0
			sub("^[^\"]*\"" key "\"[ \t]*\"", "", line)
			sub("\".*$", "", line)
			print line
			exit
		}
	' "$file"
}

add_steam_libraries() {
	local root="$1"
	local libraries=()

	[[ -d "$root/steamapps" ]] && libraries+=("$root")

	local libraryfolders="$root/steamapps/libraryfolders.vdf"
	if [[ -f "$libraryfolders" ]]; then
		while IFS= read -r path; do
			[[ -n "$path" && -d "$path/steamapps" ]] && libraries+=("$path")
		done < <(awk '
			/"path"/ {
				line = $0
				sub(/^.*"path"[ \t]*"/, "", line)
				sub(/".*$/, "", line)
				gsub(/\\\\/, "/", line)
				print line
			}
		' "$libraryfolders")
	fi

	printf '%s\n' "${libraries[@]}" | awk '!seen[$0]++'
}

find_sbox_install() {
	local root library manifest name installdir appid

	for root in "${steam_roots[@]}"; do
		[[ -d "$root" ]] || continue

		while IFS= read -r library; do
			[[ -n "$library" ]] || continue

			for manifest in "$library"/steamapps/appmanifest_*.acf; do
				[[ -f "$manifest" ]] || continue

				name="$(read_acf_value "name" "$manifest" | tr '[:upper:]' '[:lower:]')"
				appid="$(read_acf_value "appid" "$manifest")"
				installdir="$(read_acf_value "installdir" "$manifest")"

				if [[ "$name" == *"s&box"* || "$name" == *"sbox"* ]]; then
					if [[ -n "$installdir" && -d "$library/steamapps/common/$installdir" ]]; then
						echo "$library/steamapps/common/$installdir"
						return 0
					fi
				fi
			done
		done < <(add_steam_libraries "$root")
	done

	return 1
}

if [[ -z "$dest" ]]; then
	if ! dest="$(find_sbox_install)"; then
		echo "Could not find the Steam s&box install." >&2
		echo "Pass --dest /path/to/steamapps/common/<sbox folder>." >&2
		exit 1
	fi
fi

if [[ ! -d "$dest" ]]; then
	echo "Destination does not exist: $dest" >&2
	exit 1
fi

remove_obsolete_deployed_files() {
	if [[ "$apply" -eq 0 ]]; then
		return
	fi

	local obsolete_files=(
		"$dest/addons/menu/Code/MenuUI/Components/New/PackageCard.razor"
	)
	local obsolete_dirs=(
		"$dest/addons/menu/code"
	)

	local obsolete
	for obsolete in "${obsolete_files[@]}"; do
		if [[ -f "$obsolete" ]]; then
			rm -f "$obsolete"
			echo "Removed obsolete deployed file: $obsolete"
		fi
	done

	for obsolete in "${obsolete_dirs[@]}"; do
		if [[ -d "$obsolete" ]] && ! find "$repo_root/game/addons/menu/code" -type f -print -quit 2>/dev/null | grep -q .; then
			rm -rf "$obsolete"
			echo "Removed obsolete deployed directory: $obsolete"
		fi
	done
}

copy_font_if_present() {
	local source="$1"
	local target_dir="$2"

	[[ -f "$source" ]] || return 1

	case "${source,,}" in
		*.ttf|*.otf)
			mkdir -p "$target_dir"
			cp -f "$source" "$target_dir/"
			return 0
			;;
	esac

	return 1
}

stage_proton_fonts() {
	local target_dir="$repo_root/game/fonts/proton"
	local copied=0
	local has_emoji=0
	local has_material_icons=0
	local source

	mkdir -p "$target_dir"

	local known_fonts=(
		"/usr/share/fonts/noto/NotoColorEmoji.ttf"
		"/usr/share/fonts/TTF/NotoColorEmoji.ttf"
		"/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf"
		"/usr/share/fonts/google-noto-emoji/NotoColorEmoji.ttf"
		"/usr/share/fonts/MaterialIcons-Regular.ttf"
		"/usr/share/fonts/TTF/MaterialIcons-Regular.ttf"
		"/usr/share/fonts/truetype/material-design-icons/MaterialIcons-Regular.ttf"
		"/usr/share/fonts/TTF/MaterialSymbolsOutlined.ttf"
		"/usr/share/fonts/TTF/MaterialSymbolsRounded.ttf"
		"/usr/share/fonts/TTF/MaterialSymbolsSharp.ttf"
		"/usr/share/fonts/TTF/MaterialSymbolsOutlined[FILL,GRAD,opsz,wght].ttf"
		"/usr/share/fonts/TTF/MaterialSymbolsRounded[FILL,GRAD,opsz,wght].ttf"
		"/usr/share/fonts/TTF/MaterialSymbolsSharp[FILL,GRAD,opsz,wght].ttf"
		"/usr/share/fonts/TTF/SymbolsNerdFont-Regular.ttf"
		"/usr/share/fonts/TTF/SymbolsNerdFontMono-Regular.ttf"
	)

	while IFS= read -r source; do
		known_fonts+=("$source")
	done < <(find /usr/share/fonts -type f \( -iname 'MaterialIcons*.ttf' -o -iname 'MaterialSymbols*.ttf' -o -iname 'NotoColorEmoji*.ttf' \) 2>/dev/null)

	for source in "${known_fonts[@]}"; do
		if copy_font_if_present "$source" "$target_dir"; then
			copied=$((copied + 1))
			[[ "$(basename "$source")" == "NotoColorEmoji.ttf" ]] && has_emoji=1
			[[ "$(basename "$source")" == MaterialIcons-Regular.ttf || "$(basename "$source")" == MaterialSymbols*.ttf ]] && has_material_icons=1
		fi
	done

	for source in "${font_sources[@]}"; do
		if [[ -d "$source" ]]; then
			while IFS= read -r font; do
				if copy_font_if_present "$font" "$target_dir"; then
					copied=$((copied + 1))
					[[ "$(basename "$font")" == "NotoColorEmoji.ttf" ]] && has_emoji=1
					[[ "$(basename "$font")" == MaterialIcons-Regular.ttf || "$(basename "$font")" == MaterialSymbols*.ttf ]] && has_material_icons=1
				fi
			done < <(find "$source" -type f \( -iname '*.ttf' -o -iname '*.otf' \))
		elif copy_font_if_present "$source" "$target_dir"; then
			copied=$((copied + 1))
			[[ "$(basename "$source")" == "NotoColorEmoji.ttf" ]] && has_emoji=1
			[[ "$(basename "$source")" == MaterialIcons-Regular.ttf || "$(basename "$source")" == MaterialSymbols*.ttf ]] && has_material_icons=1
		else
			echo "Warning: font source not found or not a font: $source" >&2
		fi
	done

	if [[ "$copied" -eq 0 ]]; then
		echo "Warning: no Proton fallback fonts were found. Install Noto Color Emoji and Material Icons, or pass --font-source PATH." >&2
	else
		echo "Staged Proton fallback fonts in: $target_dir"
	fi

	if [[ "$has_emoji" -eq 0 ]]; then
		echo "Warning: NotoColorEmoji.ttf was not found; emoji fallback may be missing." >&2
	fi

	if [[ "$has_material_icons" -eq 0 ]]; then
		echo "Warning: Material Icons/Symbols font was not found; editor icon ligatures may still render as text. Pass --font-source PATH if you have MaterialIcons-Regular.ttf." >&2
	fi
}

if [[ "$stage_fonts" -eq 1 ]]; then
	stage_proton_fonts
fi

stage_dotnet_refs() {
	local target_dir="$repo_root/game/bin/managed/refs/net10.0"
	local ref_root="${DOTNET_REF_ROOT:-}"

	if [[ -z "$ref_root" ]]; then
		ref_root="$(find /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref -path '*/ref/net10.0' -type d 2>/dev/null | sort -V | tail -1 || true)"
	fi

	if [[ -z "$ref_root" || ! -d "$ref_root" ]]; then
		echo "Warning: .NET reference assemblies were not found. Install the dotnet 10 SDK/ref pack or set DOTNET_REF_ROOT." >&2
		return
	fi

	mkdir -p "$target_dir"
	cp -f "$ref_root"/*.dll "$target_dir/"
	echo "Staged .NET reference assemblies in: $target_dir"
}

if [[ "$stage_refs" -eq 1 ]]; then
	stage_dotnet_refs
fi

remove_obsolete_deployed_files

rsync_args=(-av --prune-empty-dirs)
if [[ "$apply" -eq 0 ]]; then
	rsync_args+=(--dry-run)
fi

includes=(
	"*/"
	"/.version"
	"/*.exe"
	"/*.dll"
	"/*.json"
	"/*.runtimeconfig.json"
	"/bin/*.txt"
	"/bin/*.json"
	"/bin/managed/***"
	"/fonts/***"
	"/addons/*/Code/***"
	"/addons/*/code/***"
	"/editor/*/Code/***"
	"/addons/**/*_c"
	"/core/**/*_c"
	"/config/**/*_c"
	"/editor/**/*_c"
	"/mount/**/*_c"
	"/samples/**/*_c"
	"/templates/**/*_c"
)

native_includes=(
	"/bin/win64/*.txt"
	"/bin/win64/*.json"
	"/bin/win64/config/***"
	"/bin/win64/phonemeextractors/***"
	"/bin/win64/qt5_plugins/imageformats/***"
	"/bin/win64/qt5_plugins/platforms/qwindows.dll"
	"/bin/win64/tools/tools.txt"
	"/bin/win64/tools/hammer.dll"
	"/bin/win64/tools/met.dll"
	"/bin/win64/tools/modeldoc_editor.dll"
	"/bin/win64/tools/animgraph_editor.dll"
	"/bin/win64/contentbuilder.exe"
	"/bin/win64/resourcecompiler.exe"
	"/bin/win64/resourcecompiler.dll"
	"/bin/win64/vrad2.exe"
	"/bin/win64/vrad3.exe"
	"/bin/win64/vsopen.exe"
	"/bin/win64/vswhere.exe"
	"/bin/win64/hammer.dll"
	"/bin/win64/engine2.dll"
	"/bin/win64/filesystem_stdio.dll"
	"/bin/win64/assetsystem.dll"
	"/bin/win64/materialsystem2.dll"
	"/bin/win64/meshsystem.dll"
	"/bin/win64/modeldoc_utils.dll"
	"/bin/win64/physicsbuilder.dll"
	"/bin/win64/propertyeditor.dll"
	"/bin/win64/rendersystemempty.dll"
	"/bin/win64/rendersystemvulkan.dll"
	"/bin/win64/schemasystem.dll"
	"/bin/win64/toolframework2.dll"
	"/bin/win64/toolscenenodes.dll"
	"/bin/win64/vfx_vulkan.dll"
	"/bin/win64/visbuilder.dll"
	"/bin/win64/animationsystem.dll"
	"/bin/win64/bakedlodbuilder.dll"
	"/bin/win64/localize.dll"
	"/bin/win64/helpsystem.dll"
	"/bin/win64/tier0.dll"
	"/bin/win64/tier0_s64.dll"
	"/bin/win64/vstdlib_s64.dll"
	"/bin/win64/steam_api64.dll"
	"/bin/win64/steamclient64.dll"
	"/bin/win64/SDL3.dll"
	"/bin/win64/Qt5Core.dll"
	"/bin/win64/Qt5Gui.dll"
	"/bin/win64/Qt5Widgets.dll"
	"/bin/win64/Qt5Concurrent.dll"
	"/bin/win64/tbb.dll"
	"/bin/win64/tbbmalloc.dll"
	"/bin/win64/openvr_api.dll"
	"/bin/win64/openxr_loader.dll"
	"/bin/win64/dxcompiler.dll"
	"/bin/win64/libHarfBuzzSharp.dll"
	"/bin/win64/libSkiaSharp.dll"
	"/bin/win64/libfbxsdk.dll"
	"/bin/win64/ati_compress_wrapper.dll"
	"/bin/win64/bc7enc.dll"
	"/bin/win64/compressonator.dll"
	"/bin/win64/ispc_texcomp.dll"
	"/bin/win64/lame_enc.dll"
	"/bin/win64/avdevice-62.dll"
	"/bin/win64/avfilter-11.dll"
	"/bin/win64/embree3.dll"
	"/bin/win64/OpenImageDenoise.dll"
	"/bin/win64/OVRLipSync.dll"
	"/bin/win64/PerformanceAPI.dll"
	"/bin/win64/GFSDK_Aftermath_Lib.x64.dll"
	"/bin/win64/sentry.dll"
	"/bin/win64/crashpad_handler.exe"
	"/bin/win64/crashpad_wer.dll"
)

if [[ "$full_native" -eq 1 ]]; then
	includes+=( "/bin/win64/***" )
else
	includes+=( "${native_includes[@]}" )
fi

filter_args=()
for include in "${includes[@]}"; do
	filter_args+=(--include "$include")
done
filter_args+=(--exclude '*')

echo "Source:      $repo_root/game"
echo "Destination: $dest"
if [[ "$apply" -eq 0 ]]; then
	echo "Mode:        dry-run (pass --apply to copy)"
else
	echo "Mode:        apply"
fi

cd "$repo_root"
rsync "${rsync_args[@]}" "${filter_args[@]}" game/ "$dest/"
