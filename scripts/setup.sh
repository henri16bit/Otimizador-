#!/usr/bin/env bash
set -euo pipefail
# Setup da tarefa cloud Linux. Não altera arquivos de código nem valores de HOME.
task_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
task_sdk_root=/workspace/.dotnet
export DOTNET_ROOT="$task_sdk_root"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME=/workspace/.dotnet-cli
export NUGET_PACKAGES=/workspace/.nuget/packages
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
if [[ ! -x "$DOTNET_ROOT/dotnet" ]] || ! dotnet --list-sdks | rg -q '^10\.0\.402 ' ; then
  python3 - "$DOTNET_ROOT" <<'PY'
import hashlib, json, pathlib, sys, tarfile, tempfile, urllib.request
version = '10.0.402'
manifest = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
with urllib.request.urlopen(manifest, timeout=60) as response:
    releases = json.load(response)['releases']
sdk = next(sdk for release in releases for sdk in release.get('sdks', [release['sdk']]) if sdk['version'] == version)
artifact = next(file for file in sdk['files'] if file['rid'] == 'linux-x64' and file['name'].endswith('.tar.gz'))
destination = pathlib.Path(sys.argv[1]); destination.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='pcanalyzer-sdk-') as staging:
    archive = pathlib.Path(staging) / 'sdk.tar.gz'
    with urllib.request.urlopen(artifact['url'], timeout=120) as response, archive.open('wb') as output:
        while chunk := response.read(1024 * 1024): output.write(chunk)
    with archive.open('rb') as file:
        actual = hashlib.file_digest(file, 'sha512').hexdigest()
    if actual.lower() != artifact['hash'].lower(): raise RuntimeError('SHA512 do SDK diverge do manifesto oficial')
    with tarfile.open(archive) as source: source.extractall(destination, filter='data')
print('SDK .NET', version, 'instalado com SHA512 verificado')
PY
fi
cd -- "$task_root"
dotnet restore PCAnalyzer.sln --locked-mode
dotnet build PCAnalyzer.sln -c Release --no-restore -m:2
dotnet test tests/PCAnalyzer.Tests/PCAnalyzer.Tests.csproj -c Release --no-build --no-restore
