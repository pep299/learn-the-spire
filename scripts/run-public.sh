#!/usr/bin/env bash
set -euo pipefail
task_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
task_dotnet="${LEARN_DOTNET:-dotnet}"
task_binary="$task_root/addons/public-observation/Sts2DllRecorder/bin/Release/net9.0/Sts2DllRecorder.dll"
if [[ ! -f "$task_binary" ]]; then
  printf '%s\n' '先に dotnet build addons/public-observation/public-observation.sln -c Release を実行してください。' >&2
  exit 2
fi
mkdir -p "$task_root/.runtime"
export TMPDIR="$task_root/.runtime"
exec "$task_dotnet" "$task_binary" "$@"
