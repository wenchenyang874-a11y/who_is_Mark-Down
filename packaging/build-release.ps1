[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Version,

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must use the numeric major.minor.patch format required by the Windows installer.'
}

# Keep every Windows version surface aligned. Passing only Version leaves an
# explicit FileVersion/AssemblyVersion from the project file unchanged, which
# made Explorer report an older application version inside a newer installer.
$binaryVersion = "$Version.0"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\WhoIsMarkdown.App\WhoIsMarkdown.App.csproj'
$nugetConfig = Join-Path $repositoryRoot 'NuGet.Config'
$publishDirectory = Join-Path $repositoryRoot "artifacts\publish\$Runtime"
$installerScript = Join-Path $PSScriptRoot 'WIMD.iss'
$innoCandidates = @(
    (Join-Path $repositoryRoot 'artifacts\tools\inno-setup-6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
)
$innoCompiler = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$resolvedPublishDirectory = [IO.Path]::GetFullPath($publishDirectory)
if (-not $resolvedPublishDirectory.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish directory escaped the repository artifacts directory.'
}
if (Test-Path -LiteralPath $resolvedPublishDirectory) {
    Remove-Item -LiteralPath $resolvedPublishDirectory -Recurse -Force
}

if (-not $innoCompiler) {
    throw 'Inno Setup compiler was not found.'
}

dotnet restore $projectPath `
    --runtime $Runtime `
    --locked-mode `
    --configfile $nugetConfig

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

dotnet publish $projectPath `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --no-restore `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:FileVersion=$binaryVersion `
    -p:AssemblyVersion=$binaryVersion `
    -p:InformationalVersion=$Version `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    --output $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

& $innoCompiler "/DMyAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Write-Host "WIMD $Version installer generated successfully."
