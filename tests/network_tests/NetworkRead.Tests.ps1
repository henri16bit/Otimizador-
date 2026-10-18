param([string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path)
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $Repository 'src/PCAnalyzer.Network/NetworkInspector.cs') -Raw
$match = [regex]::Match($source, '(?s)internal const string NetAdapterScript = """\r?\n(.*?)\r?\n\s*""";')
if (-not $match.Success) { throw 'Production NetAdapter script not found.' }
$directory = Join-Path ([IO.Path]::GetTempPath()) ('pcanalyzer-network-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$scriptPath = Join-Path $directory 'fixture.ps1'
$fixture = @'
param([string]$Scenario)
$fixtureInterfaceId = 'd9016897-a8ca-4275-abaa-5c2a1e439366'
function Get-NetAdapter {
  param([switch]$IncludeHidden)
  $guid = '{' + $fixtureInterfaceId.ToUpperInvariant() + '}'
  if ($Scenario -in @('fallback', 'missing-legacy')) { $guid = $null }
  if ($Scenario -eq 'contradictory-guid') { $guid = '7102bd1b-9888-4f80-a590-291695c39253' }
  $adapter = [pscustomobject]@{ InterfaceGuid=$guid; Name='Ethernet [Intel]*'; ifIndex=7; PnPDeviceID='PCI\VEN_8086&DEV_15FA';
    InterfaceDescription='Intel fixture'; DriverVersion='fixture-1'; HardwareInterface=$true; LinkSpeed='1 Gbps' }
  $adapter
  if ($Scenario -eq 'duplicate-guid') { $adapter }
  if ($Scenario -eq 'duplicate-name') {
    [pscustomobject]@{ InterfaceGuid='7102bd1b-9888-4f80-a590-291695c39253'; Name=$adapter.Name; ifIndex=8; PnPDeviceID='OTHER' }
  }
}
function Get-CimInstance {
  param([string]$ClassName)
  if ($ClassName -ne 'Win32_NetworkAdapter') { throw 'Unexpected CIM class.' }
  if ($Scenario -ne 'missing-legacy') {
    [pscustomobject]@{ GUID=$fixtureInterfaceId; InterfaceIndex=7; PNPDeviceID='PCI\VEN_8086&DEV_15FA' }
  }
}
function Assert-LiteralName([string]$Name) {
  if ($Name -cne [System.Management.Automation.WildcardPattern]::Escape('Ethernet [Intel]*')) { throw 'Adapter wildcard was not escaped.' }
}
function Get-NetAdapterAdvancedProperty {
  param([string]$Name, [switch]$IncludeHidden, [switch]$AllProperties)
  Assert-LiteralName $Name
  if ($Scenario -eq 'partial') { throw [System.UnauthorizedAccessException]::new('Access denied in fixture.') }
  [pscustomobject]@{ RegistryKeyword='*RSS'; DisplayName='Receive Side Scaling'; DisplayValue='Enabled'; RegistryValue=@('1');
    ValidRegistryValues=@('0','1'); ValidDisplayValues=@('Disabled','Enabled') }
}
function Get-NetAdapterRss {
  param([string]$Name, [switch]$IncludeHidden)
  Assert-LiteralName $Name
  [pscustomobject]@{ Enabled=$false; NumberOfReceiveQueues=2; BaseProcessorGroup=0; BaseProcessorNumber=2; RssProcessorArray=@('0:2') }
}
function Get-NetAdapterStatistics {
  param([string]$Name, [switch]$IncludeHidden)
  Assert-LiteralName $Name
  [pscustomobject]@{ ReceivedBytes=0; SentBytes=12; ReceivedPacketErrors=$null; OutboundPacketErrors=0 }
}
function Get-NetAdapterPowerManagement {
  param([string]$Name, [switch]$IncludeHidden)
  Assert-LiteralName $Name
  [pscustomobject]@{ SelectiveSuspend='Unsupported'; WakeOnMagicPacket='Enabled' }
}
function Get-NetTCPSetting {
  [pscustomobject]@{ SettingName='Internet'; AutoTuningLevelLocal='Normal'; CongestionProvider='CUBIC'; EcnCapability='Disabled' }
}
'@
try {
  [IO.File]::WriteAllText($scriptPath, $fixture + [Environment]::NewLine + $match.Groups[1].Value)
  $executable = (Get-Process -Id $PID).Path
  $entryAssembly = [Reflection.Assembly]::GetEntryAssembly().Location
  function Invoke-Fixture([string]$Scenario, [string]$Action = 'Inspect') {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    if ([IO.Path]::GetFileNameWithoutExtension($executable) -eq 'dotnet') { $info.ArgumentList.Add($entryAssembly) }
    foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',$scriptPath,$Scenario)) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
      $stdout = $process.StandardOutput.ReadToEndAsync()
      $stderr = $process.StandardError.ReadToEndAsync()
      $process.StandardInput.WriteLine((@{ Action=$Action; InterfaceGuid='d9016897-a8ca-4275-abaa-5c2a1e439366' } | ConvertTo-Json -Compress))
      $process.StandardInput.Close()
      if (-not $process.WaitForExit(15000)) { $process.Kill($true); throw 'Fixture timed out.' }
      @{ ExitCode=$process.ExitCode; Output=$stdout.GetAwaiter().GetResult(); Error=$stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
  }
  $checks = 0
  foreach ($scenario in @('normal', 'fallback', 'partial')) {
    $result = Invoke-Fixture $scenario
    if ($result.ExitCode -ne 0) { throw "${scenario}: $($result.Error)" }
    $data = $result.Output | ConvertFrom-Json
    if ($data.Identity.InterfaceGuid -ne 'd9016897-a8ca-4275-abaa-5c2a1e439366') { throw 'Wrong identity returned.' }
    if ($data.Statistics.ReceivedBytes -ne 0 -or $null -ne $data.Statistics.ReceivedPacketErrors) { throw 'Missing and zero counters were conflated.' }
    if ($scenario -eq 'fallback' -and $data.Identity.Mapping -ne 'GUID Win32 + índice + PnP verificados') { throw 'Fallback did not validate its identities.' }
    if ($scenario -eq 'partial') {
      if ($data.Statuses.Properties -ne 'PermissionRequired' -or $data.Properties.Count -ne 0 -or -not $data.Limitations) { throw 'Partial-query failure was hidden.' }
    } elseif ($data.Properties[0].RegistryKeyword -ne '*RSS') { throw 'Driver property was lost.' }
    $checks++
  }
  foreach ($scenario in @('contradictory-guid', 'duplicate-guid', 'duplicate-name', 'missing-legacy')) {
    $result = Invoke-Fixture $scenario
    if ($result.ExitCode -eq 0 -or $result.Output.Trim().Length -gt 0) { throw "$scenario must reject ambiguous identity without returning properties." }
    $checks++
  }
  $result = Invoke-Fixture 'tcp' 'Tcp'
  if ($result.ExitCode -ne 0 -or ($result.Output | ConvertFrom-Json).Tcp[0].SettingName -ne 'Internet') { throw 'TCP profile query failed.' }
  $checks++
  Write-Output "$checks network script fixture scenarios passed. No Windows cmdlets or configuration writes executed."
} finally { Remove-Item -LiteralPath $directory -Recurse -Force }
