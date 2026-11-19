param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$rustRoot = Join-Path $repoRoot "rust"
$appRoot = Join-Path $rustRoot "crates\wimd-app"
$cargo = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe"
$version = "2.0.0"
$bundleRoot = Join-Path $rustRoot "target\release\bundle\nsis"
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

    & $cargo build --package wimd --bin wimd-updater-helper --release --offline
    if ($LASTEXITCODE -ne 0) {
        throw "Updater close helper build failed."
    }
}
finally {
    Pop-Location
}

Push-Location $appRoot
try {
    $buildStarted = Get-Date
    npx --yes @tauri-apps/cli@2.12.4 build --bundles nsis
    if ($LASTEXITCODE -ne 0) {
        throw "Tauri NSIS bundle failed."
    }
}
finally {
    Pop-Location
}

$sourceSetup = Get-ChildItem -LiteralPath $bundleRoot -File -Filter "*_${version}_x64-setup.exe" |
    Where-Object { $_.LastWriteTime -ge $buildStarted.AddMinutes(-1) } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $sourceSetup -or -not (Test-Path -LiteralPath $sourceSetup)) {
    throw "Expected installer was not generated under: $bundleRoot"
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
