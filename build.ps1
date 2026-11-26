param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'), [switch]$IncludeSymbols)
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))) { $taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$taskCompiler = Join-Path $taskFramework 'csc.exe'
$taskWpf = Join-Path $taskFramework 'WPF'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskOut = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'SwitchEnv.exe'
$taskReferences = @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $taskWpf $_) }
$taskArguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/langversion:5',('/out:' + $taskOut),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/win32icon:' + (Join-Path $PSScriptRoot 'app.ico')),('/resource:' + (Join-Path $PSScriptRoot 'Theme.xaml') + ',SwitchEnv.Theme.xaml'),('/resource:' + (Join-Path $PSScriptRoot 'MainWindow.xaml') + ',SwitchEnv.MainWindow.xaml'),('/resource:' + (Join-Path $PSScriptRoot 'app.ico') + ',SwitchEnv.app.ico'),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Xaml.dll','/reference:System.Runtime.Serialization.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll')
$taskArguments += @('/reference:Microsoft.CSharp.dll','/reference:System.Xml.dll','/reference:System.Xml.Linq.dll')
$taskSources = @('AssemblyInfo.cs','Core.cs','Descriptions.cs','Groups.cs','Moves.cs','Store.cs','WindowsEnvironment.cs','AdminBootstrap.cs','WindowsStartup.cs','InstanceActivation.cs','Dialogs.cs','Controller.cs','App.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
if ($IncludeSymbols) { $taskArguments += '/debug:pdbonly' }
& $taskCompiler @taskArguments @taskReferences @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SwitchEnv.exe.config') -Destination ($taskOut + '.config') -Force
Write-Output "Built: $taskOut"
