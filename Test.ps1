$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot 'Tests\MediaFlattener.Tests.csproj') -c Release -- (Join-Path $PSScriptRoot 'preview.png')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
