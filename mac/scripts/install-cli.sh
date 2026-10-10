#!/bin/bash
# Link the bundled helper; never duplicate an executable or modify the signed app.
set -euo pipefail
usage() {
    printf '%s\n' 'Usage: bash scripts/install-cli.sh [--app /Applications/PinCC.app] [--bin-dir "$HOME/.local/bin"] [--remove]'
}
app="/Applications/PinCC.app"
bin_dir="$HOME/.local/bin"
remove=0
while [[ "$#" -gt 0 ]]; do
    case "$1" in
        --app) [[ "$#" -ge 2 ]] || { usage >&2; exit 2; }; app="$2"; shift 2 ;;
        --bin-dir) [[ "$#" -ge 2 ]] || { usage >&2; exit 2; }; bin_dir="$2"; shift 2 ;;
        --remove) remove=1; shift ;;
        --help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
done
[[ "$(uname -s)" == "Darwin" ]] || { printf '%s\n' 'Install the CLI on macOS.' >&2; exit 1; }
case "$app" in /*.app) ;; *) printf '%s\n' '--app must be an absolute .app path.' >&2; exit 2 ;; esac
case "$bin_dir" in /*) ;; *) printf '%s\n' '--bin-dir must be absolute.' >&2; exit 2 ;; esac
helper="$app/Contents/Helpers/pincc"
link="$bin_dir/pincc"
if [[ "$remove" -eq 1 ]]; then
    if [[ -L "$link" && "$(readlink "$link")" == "$helper" ]]; then
        rm -- "$link"
        printf 'Removed: %s\n' "$link"
    elif [[ -e "$link" || -L "$link" ]]; then
        printf 'Refusing to remove an unrelated file: %s\n' "$link" >&2
        exit 1
    fi
    exit 0
fi
[[ -x "$helper" ]] || { printf 'Missing bundled CLI: %s\n' "$helper" >&2; exit 1; }
if [[ -L "$link" && "$(readlink "$link")" == "$helper" ]]; then
    printf 'Already linked: %s\n' "$link"
    exit 0
fi
[[ ! -e "$link" && ! -L "$link" ]] || { printf 'Refusing to overwrite: %s\n' "$link" >&2; exit 1; }
mkdir -p "$bin_dir"
ln -s "$helper" "$link"
printf 'Installed: %s -> %s\n' "$link" "$helper"
printf 'Add %s to PATH if necessary.\n' "$bin_dir"
