[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $repository 'oracle/mac-contracts/PinCC.MacContracts.csproj'
$output = Join-Path $repository 'mac/contracts/vectors.jsonl'
Push-Location -LiteralPath $repository
try {
    & dotnet run --project $project --configuration Release -- --output $output
    if ($LASTEXITCODE -ne 0) { throw "C# contract oracle failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }
