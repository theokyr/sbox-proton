#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet_home="${DOTNET_CLI_HOME:-$HOME/.local/share/sbox-public/dotnet-cli-home}"
refresh_artifacts=0
deploy_args=()

while [[ $# -gt 0 ]]; do
	case "$1" in
		--refresh-artifacts)
			refresh_artifacts=1
			shift
			;;
		*)
			deploy_args+=("$1")
			shift
			;;
	esac
done

mkdir -p "$dotnet_home"

export DOTNET_CLI_HOME="$dotnet_home"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

cd "$repo_root"

build_args=( build-proton --target-platform win64 --runtime win-x64 )
if [[ "$refresh_artifacts" -eq 0 && -f "$repo_root/game/bin/win64/engine2.dll" && -f "$repo_root/game/bin/win64/resourcecompiler.dll" ]]; then
	build_args+=( --skip-artifacts )
fi

sboxbuild_dll="$repo_root/engine/Tools/SboxBuild/bin/net10.0/sboxbuild.dll"
if [[ ! -f "$sboxbuild_dll" ]]; then
	env -u SHELLOPTS -u BASHOPTS dotnet build engine/Tools/SboxBuild/SboxBuild.csproj -v:minimal
fi

build_command=( dotnet "$sboxbuild_dll" "${build_args[@]}" )
env SBOX_REPO_ROOT="$repo_root" zsh -lc 'cd "$SBOX_REPO_ROOT" && "$@"' zsh "${build_command[@]}"

scripts/deploy-proton-build.sh --apply "${deploy_args[@]}"
