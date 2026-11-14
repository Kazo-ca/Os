<#
.SYNOPSIS
Assembles the independently cached static releases into one GitHub Pages artifact.
.DESCRIPTION
WASM owns the root, legacy owns /legacy/ and /beta/ redirects to the beta site
(ADR 0271 §3, build/pages/beta-redirect.html). Sources are copied verbatim, including
the hashed service-worker asset manifest. The destination must be empty so stale
hashed assets cannot accidentally survive from a previous release.
#>
[CmdletBinding()]
param(
    [string]$WasmDirectory = (Join-Path $PSScriptRoot '../artifacts/pages-publish/wwwroot'),
    [string]$LegacyDirectory = (Join-Path $PSScriptRoot '../../../dist'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/pages')
)

$ErrorActionPreference = 'Stop'
$wasmRoot = (Resolve-Path -LiteralPath $WasmDirectory).Path
$legacyRoot = (Resolve-Path -LiteralPath $LegacyDirectory).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($required in @('index.html', 'service-worker.js', 'service-worker-assets.js', '_framework')) {
    if (-not (Test-Path -LiteralPath (Join-Path $wasmRoot $required))) {
        throw "Incomplete WASM publish: $required is missing."
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $legacyRoot 'index.html'))) {
    throw 'Incomplete legacy publish: index.html is missing.'
}
foreach ($reserved in @('legacy', 'beta')) {
    if (Test-Path -LiteralPath (Join-Path $wasmRoot $reserved)) {
        throw "The WASM publish cannot occupy the reserved $reserved directory."
    }
}
if ((Test-Path -LiteralPath $outputRoot) -and (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
    throw 'Use an empty output directory to assemble a Pages release.'
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Get-ChildItem -LiteralPath $wasmRoot -Force | Copy-Item -Destination $outputRoot -Recurse -Force
$legacyOutput = Join-Path $outputRoot 'legacy'
New-Item -ItemType Directory -Path $legacyOutput | Out-Null
Get-ChildItem -LiteralPath $legacyRoot -Force | Copy-Item -Destination $legacyOutput -Recurse -Force
$betaOutput = Join-Path $outputRoot 'beta'
New-Item -ItemType Directory -Path $betaOutput | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pages/beta-redirect.html') -Destination (Join-Path $betaOutput 'index.html')
[IO.File]::WriteAllText((Join-Path $outputRoot '.nojekyll'), '')
[IO.File]::WriteAllText((Join-Path $outputRoot 'CNAME'), "os.kazo.ca`n")
Write-Output "Pages artifact: $outputRoot (WASM /, legacy /legacy/, beta redirection /beta/)"
