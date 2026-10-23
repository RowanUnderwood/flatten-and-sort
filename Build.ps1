$ErrorActionPreference = 'Stop'
dotnet publish (Join-Path $PSScriptRoot 'MediaFlattener.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $PSScriptRoot 'App') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host 'Ready: App\MediaFlattener.exe'
