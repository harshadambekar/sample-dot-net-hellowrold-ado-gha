<#
.SYNOPSIS
  Local developer loop: restore, build and test the solution the way CI does.
.DESCRIPTION
  CI (.github/workflows/_dotnet-windows-ci.yml) uses MSBuild + vstest.console for parity
  with the old Azure Pipeline. Locally the dotnet CLI is enough:
    ./build.ps1                 # AnyCPU, runs anywhere with the .NET 8 SDK
    ./build.ps1 -Platform x86   # x86, needs Windows + an x86 .NET 8 runtime
#>
[CmdletBinding()]
param(
  [ValidateSet('AnyCPU', 'x86')] [string] $Platform = 'AnyCPU',
  [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
  $props = @("-p:Platform=$Platform", "-c", $Configuration)
  dotnet restore HelloWorld.sln --configfile nuget.config
  if ($LASTEXITCODE) { throw 'restore failed' }
  dotnet build HelloWorld.sln --no-restore @props
  if ($LASTEXITCODE) { throw 'build failed' }
  $testArgs = @('--no-build', '--settings', 'coverlet.runsettings', '--results-directory', 'TestResults', '--logger', 'trx') + $props
  if ($Platform -eq 'x86') { $testArgs += @('--arch', 'x86') }
  dotnet test HelloWorld.sln @testArgs
  if ($LASTEXITCODE) { throw 'tests failed' }
}
finally { Pop-Location }
