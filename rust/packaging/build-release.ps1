param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$rustRoot = Join-Path $repoRoot "rust"
$appRoot = Join-Path $rustRoot "crates\wimd-app"
$cargo = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe"
$version = "2.0.0"
$setupName = "WIMD_${version}_x64-setup.exe"
$sourceSetup = Join-Path $rustRoot "target\release\bundle\nsis\$setupName"
$artifactDirectory = Join-Path $repoRoot "artifacts\rust-installer"
$artifactSetup = Join-Path $artifactDirectory "WIMD-Setup-v$version-win-x64.exe"

if (-not (Test-Path -LiteralPath $cargo)) {
    throw "Rust Cargo not found at $cargo"
}

$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"

Push-Location $rustRoot
try {
    & $cargo fmt --all -- --check
    if ($LASTEXITCODE -ne 0) {
        throw "cargo fmt check failed."
    }

    & $cargo check --workspace --all-targets --offline
    if ($LASTEXITCODE -ne 0) {
        throw "cargo check failed."
    }

    if (-not $SkipTests) {
        & $cargo test --workspace --offline
        if ($LASTEXITCODE -ne 0) {
            throw "Rust tests failed."
        }
    }
}
finally {
    Pop-Location
}

Push-Location $appRoot
try {
    npx --yes @tauri-apps/cli@2.12.4 build --bundles nsis
    if ($LASTEXITCODE -ne 0) {
        throw "Tauri NSIS bundle failed."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $sourceSetup)) {
    throw "Expected installer was not generated: $sourceSetup"
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
Copy-Item -LiteralPath $sourceSetup -Destination $artifactSetup -Force
$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $artifactSetup
$size = (Get-Item -LiteralPath $artifactSetup).Length
$checksumPath = "$artifactSetup.sha256.txt"
"$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $artifactSetup)" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Output "Installer: $artifactSetup"
Write-Output "Bytes: $size"
Write-Output "SHA256: $($hash.Hash.ToLowerInvariant())"
