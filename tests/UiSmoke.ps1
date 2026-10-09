#requires -Version 5.1
<#
Run the actual published GUI in a new data directory for each scenario, then
reopen that directory through the published CLI to verify saved drafts.
Failed evidence is retained; successful evidence is removed after path checks.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Executable,
    [ValidateRange(5, 60)]
    [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$exe = (Resolve-Path -LiteralPath $Executable).Path
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Executable not found: $exe" }
$root = Join-Path ([IO.Path]::GetTempPath()) ('PinCC-ui-smoke-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)

function Invoke-SmokeProcess([string] $DataHome, [string] $Arguments, [string] $Scenario) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $exe
    $start.Arguments = $Arguments
    $start.WorkingDirectory = Split-Path -Parent $exe
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    # Do not inherit diagnostic scripts, a skill's undo group, or a user's home.
    foreach ($name in @($start.EnvironmentVariables.Keys)) {
        if ($name -like 'PINCC_*') { $start.EnvironmentVariables.Remove($name) }
    }
    $start.EnvironmentVariables['PINCC_HOME'] = $DataHome
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $root 'bundle'
    if ($Scenario) { $start.EnvironmentVariables['PINCC_UI_SMOKE'] = $Scenario }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not start $exe" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            # This handle belongs to the diagnostic child we just created.
            # Never enumerate or terminate existing PinCC instances.
            $process.Kill()
            $process.WaitForExit(5000) | Out-Null
            throw "Diagnostic child exceeded $($TimeoutSeconds)s: $Scenario; evidence: $DataHome"
        }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw "Diagnostic output did not close: $DataHome" }
        [pscustomobject]@{ Code = $process.ExitCode; Out = $stdout.Result; Err = $stderr.Result }
    }
    finally { $process.Dispose() }
}

Write-Host "GUI smoke: $exe"
Write-Host "Temporary evidence (kept on failure): $root"
$scenarios = @(
    @{ Name = 'edit-exit'; Code = 0; Handlers = 0; Fault = '' },
    @{ Name = 'layout-fault'; Code = 1; Handlers = 1; Fault = 'UI smoke layout fault' },
    @{ Name = 'notify-fault'; Code = 1; Handlers = 1; Fault = 'UI smoke notify fault' },
    @{ Name = 'exit-fault'; Code = 0; Handlers = 0; Fault = 'UI smoke exit fault' },
    @{ Name = 'save-failure'; Code = 0; Handlers = 0; Fault = 'UI smoke save failure' }
)
foreach ($scenario in $scenarios) {
    $name = $scenario.Name
    $scenarioHome = Join-Path $root $name
    [IO.Directory]::CreateDirectory($scenarioHome) | Out-Null
    $run = Invoke-SmokeProcess $scenarioHome '' $name
    [IO.File]::WriteAllText((Join-Path $scenarioHome 'stdout.txt'), $run.Out, $utf8)
    [IO.File]::WriteAllText((Join-Path $scenarioHome 'stderr.txt'), $run.Err, $utf8)
    if ($run.Code -ne $scenario.Code) { throw "$name exit code $($run.Code), expected $($scenario.Code); evidence: $scenarioHome" }
    $resultPath = Join-Path $scenarioHome 'ui-smoke-result.json'
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) { throw "$name did not complete the GUI scenario; evidence: $scenarioHome" }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.scenario -ne $name -or $result.exitCode -ne $scenario.Code -or
        $result.handlerCount -ne $scenario.Handlers -or $result.shutdownCount -ne 1 -or
        $result.draftCount -ne 2 -or $result.savedCount -ne 2 -or $result.recoveryPending) {
        throw "$name lifecycle or draft recovery failed; evidence: $scenarioHome"
    }
    $expected = @('UI smoke editor draft', ('UI smoke checklist' + [char]10 + '- [ ] UI smoke quick add draft'))
    $draftBodies = @($result.draftBodies)
    if ($draftBodies.Count -ne $expected.Count -or @($expected | Where-Object { $_ -cnotin $draftBodies }).Count -ne 0) {
        throw "$name did not exercise both editor and quick-add drafts; evidence: $scenarioHome"
    }
    # An independent process reopens the DB; in-memory success is insufficient.
    $read = Invoke-SmokeProcess $scenarioHome 'list --all --json' ''
    [IO.File]::WriteAllText((Join-Path $scenarioHome 'saved-cards.json'), $read.Out, $utf8)
    if ($read.Code -ne 0) { throw "$name persisted database could not be reopened: $($read.Err); evidence: $scenarioHome" }
    $parsedCards = ConvertFrom-Json -InputObject $read.Out
    $cards = @($parsedCards)
    if ($cards.Count -ne 2 -or @($expected | Where-Object { $_ -cnotin $cards.body }).Count -ne 0) {
        throw "$name saved draft bodies do not match; evidence: $scenarioHome"
    }
    if ($name -eq 'save-failure' -and @($cards | Where-Object { $_.done_at -or $_.deleted_at }).Count -gt 0) {
        throw "save-failure lost the active draft card; evidence: $scenarioHome"
    }
    $errorPath = Join-Path $scenarioHome 'error.log'
    $errors = if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath -Raw } else { '' }
    if ($errors -match 'System\.Diagnostics\.Tracing|ControlsTraceLogger') {
        throw "$name WPF shutdown assembly failure recurred; evidence: $scenarioHome"
    }
    if ($scenario.Fault) {
        if (-not $errors -or -not $errors.Contains($scenario.Fault)) { throw "$name injected fault was not logged; evidence: $scenarioHome" }
    }
    elseif ($errors) { throw "$name logged an unexpected exception; evidence: $scenarioHome" }
    Write-Host "PASS $name (exit $($run.Code), both drafts persisted)"
}
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$resolvedRoot = (Resolve-Path -LiteralPath $root).Path
if (-not $resolvedRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
    -not $resolvedRoot.Equals([IO.Path]::GetFullPath($root), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing cleanup outside this smoke test's temporary directory: $resolvedRoot"
}
Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
Write-Host 'GUI smoke passed.'
