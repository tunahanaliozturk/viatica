# Installs the SDK pinned in global.json into .dotnet/, leaving any system-wide .NET untouched.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$version = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
$script = Join-Path $env:TEMP "dotnet-install-$([guid]::NewGuid()).ps1"
try {
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script
    & $script -Version $version -InstallDir .dotnet
}
finally {
    Remove-Item -LiteralPath $script -Force -ErrorAction SilentlyContinue
}
