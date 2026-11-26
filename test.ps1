param([string]$TestDirectory = (Join-Path $PSScriptRoot 'test-results'))
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))) { $taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
New-Item -ItemType Directory -Path $TestDirectory -Force | Out-Null
$taskTestPath = [System.IO.Path]::GetFullPath($TestDirectory)
$taskOut = Join-Path $taskTestPath 'SwitchEnv.Tests.exe'
$taskSources = @('Core.cs','Descriptions.cs','Groups.cs','Moves.cs','Store.cs','WindowsEnvironment.cs','AdminBootstrap.cs','WindowsStartup.cs','Tests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $taskFramework 'csc.exe') '/nologo' '/target:exe' '/langversion:5' "/out:$taskOut" '/reference:System.dll' '/reference:System.Core.dll' '/reference:System.Runtime.Serialization.dll' '/reference:Microsoft.CSharp.dll' '/reference:System.Xml.dll' '/reference:System.Xml.Linq.dll' @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $taskOut $taskTestPath
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
