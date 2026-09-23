[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sourceDirectory = Join-Path $projectRoot 'src\WifiAP'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Instale o .NET Framework 4.8 em um Windows de 64 bits.' }
[void](New-Item -ItemType Directory -Path $output -Force)
$executable = Join-Path $output 'WiFi-AP.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ /codepage:65001 /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Configuration.dll "/win32manifest:$sourceDirectory\app.manifest" "/out:$executable" "$sourceDirectory\WifiAP.cs"
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação.' }
$config = Join-Path $sourceDirectory 'App.local.config'
if (-not (Test-Path -LiteralPath $config)) { $config = Join-Path $sourceDirectory 'App.config' }
Copy-Item -LiteralPath $config -Destination ($executable + '.config') -Force
Write-Output "Compilado: $executable"
