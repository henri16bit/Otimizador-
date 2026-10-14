#!/usr/bin/env bash
set -euo pipefail
task_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd -- "$task_root"
if [[ -x /workspace/.dotnet/dotnet ]]; then source scripts/cloud-env.sh; fi
task_release=${1:-"$task_root/artifacts/windows-x64"}
dotnet restore PCAnalyzer.sln --locked-mode
dotnet publish src/PCAnalyzer.App/PCAnalyzer.App.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:RestoreLockedMode=true -o "$task_release"
dotnet publish src/PCAnalyzer.Worker/PCAnalyzer.Worker.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:RestoreLockedMode=true -o "$task_release/worker"
dotnet publish src/PCAnalyzer.Verify/PCAnalyzer.Verify.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:RestoreLockedMode=true -o "$task_release/verify"
cp README.md "$task_release/README.md"
cp THIRD-PARTY-NOTICES.md "$task_release/THIRD-PARTY-NOTICES.md"
cp -R docs "$task_release/"
python3 scripts/package.py "$task_release" --source
printf '%s\n' "Publicação Windows x64 gerada em $task_release/PCAnalyzer.exe"
