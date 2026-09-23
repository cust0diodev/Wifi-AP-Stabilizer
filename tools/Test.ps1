[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$executable = Join-Path $output 'WiFi-AP.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Compile com tools\Build.ps1 primeiro.' }
$report = Join-Path $output 'test-results.txt'
$process = Start-Process -FilePath $executable -ArgumentList @('--self-test', ('"' + $report + '"')) -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(30000)) { throw 'Os testes ultrapassaram 30 segundos.' }
if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
if ($process.ExitCode -ne 0) { throw "Testes falharam: código $($process.ExitCode)." }
if (-not (Test-Path -LiteralPath $report)) { throw 'Relatório de testes ausente.' }
if (-not ((Get-Content -LiteralPath $report -Raw).StartsWith('PASS:'))) { throw 'O relatório não confirmou sucesso.' }
