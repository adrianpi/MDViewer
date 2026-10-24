$ErrorActionPreference = "Stop"

$installerDirectory = $PSScriptRoot
$repositoryDirectory = (Resolve-Path (Join-Path $installerDirectory "..")).Path
$publishDirectory = Join-Path $installerDirectory "publish"
$installerScript = Join-Path $installerDirectory "MDViewer.iss"

$compiler = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($null -eq $compiler) {
    $possiblePaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $compilerPath = $possiblePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($null -eq $compilerPath) {
        throw "Inno Setup 6 or 7 was not found. Install it from https://jrsoftware.org/isinfo.php and try again."
    }

    $compilerPath = (Resolve-Path -LiteralPath $compilerPath).Path
} else {
    $compilerPath = $compiler.Source
}

& dotnet publish (Join-Path $repositoryDirectory "MDViewer.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "The MDViewer release publish failed with exit code $LASTEXITCODE."
}

Push-Location $installerDirectory
try {
    & $compilerPath $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}
