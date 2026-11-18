#Requires -Version 7.4
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))

$ErrorActionPreference = 'Stop'
$publishOptions = @(
    '-c', 'Release', '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true'
)

$app = Join-Path $PSScriptRoot 'Bd2Achievement.csproj'
$workerProject = Join-Path $PSScriptRoot 'ExporterWorker/ExporterWorker.csproj'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$workerOutput = Join-Path $output 'worker-x64'

dotnet publish $app @publishOptions -r win-x64 -o (Join-Path $output 'x64')
if ($LASTEXITCODE) { throw 'x64 build failed' }

dotnet publish $workerProject @publishOptions -r win-x64 -o $workerOutput
if ($LASTEXITCODE) { throw 'Exporter build failed' }

$worker = Join-Path $workerOutput 'ExporterWorker.exe'
dotnet publish $app @publishOptions -r win-arm64 "-p:X64Worker=$worker" -o (Join-Path $output 'arm64')
if ($LASTEXITCODE) { throw 'ARM64 build failed' }
