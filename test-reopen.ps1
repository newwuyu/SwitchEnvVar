param([string]$Executable = (Join-Path $PSScriptRoot 'build\SwitchEnv.exe'))
$ErrorActionPreference = 'Stop'
$reopenExe = (Resolve-Path -LiteralPath $Executable).ProviderPath
$reopenData = Join-Path ([System.IO.Path]::GetTempPath()) ('SwitchEnv reopen test ' + [Guid]::NewGuid().ToString('N'))
$reopenState = Join-Path $reopenData 'state.json'
$reopenError = Join-Path $reopenData 'reopen-error.txt'
$reopenArgs = @('--demo', '--data-dir', ('"' + $reopenData + '"'), '--error-log', ('"' + $reopenError + '"'))
$reopenProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Start-DemoInstance([switch]$Startup) {
    $reopenLaunchArgs = $reopenArgs
    if ($Startup) { $reopenLaunchArgs += '--startup' }
    $reopenProcess = Start-Process -FilePath $reopenExe -ArgumentList $reopenLaunchArgs -WindowStyle Hidden -PassThru
    $reopenProcesses.Add($reopenProcess)
    return $reopenProcess
}

function Wait-Until([scriptblock]$Condition, [string]$Failure) {
    $reopenClock = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 50
    } while ($reopenClock.ElapsedMilliseconds -lt 6000)
    throw $Failure
}

function Assert-ManualReopen {
    $reopenLauncher = Start-DemoInstance
    Wait-Until {
        $reopenPrimary.Refresh()
        $reopenLauncher.Refresh()
        $reopenPrimary.MainWindowTitle -like 'SwitchEnv *' -or $reopenLauncher.MainWindowTitle -eq 'SwitchEnv' -or ($reopenLauncher.HasExited -and $reopenLauncher.ExitCode -ne 0)
    } 'Manual relaunch did not display a window.'
    if ($reopenPrimary.MainWindowTitle -notlike 'SwitchEnv *') {
        throw 'Manual relaunch did not display the original instance. The second launch only showed a prompt or failed.'
    }
    if (!$reopenLauncher.WaitForExit(6000) -or $reopenLauncher.ExitCode -ne 0) { throw 'Manual relaunch did not exit successfully.' }
}

try {
    $reopenPrimary = Start-DemoInstance -Startup
    Wait-Until { Test-Path -LiteralPath $reopenState } 'Demo startup did not initialize.'
    $reopenBefore = (Get-FileHash -LiteralPath $reopenState).Hash
    $reopenDuplicate = Start-DemoInstance -Startup
    if (!$reopenDuplicate.WaitForExit(6000) -or $reopenDuplicate.ExitCode -ne 0) { throw 'Repeated login launch did not exit.' }
    $reopenPrimary.Refresh()
    if ($reopenPrimary.HasExited -or $reopenPrimary.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Login launch should be alive with its main window hidden.' }
    Assert-ManualReopen
    if (!$reopenPrimary.CloseMainWindow()) { throw 'Could not close the original window.' }
    Wait-Until {
        $reopenPrimary.Refresh()
        $reopenPrimary.MainWindowHandle -eq [IntPtr]::Zero
    } 'Closing the original window did not hide it.'
    Assert-ManualReopen
    if ((Get-FileHash -LiteralPath $reopenState).Hash -ne $reopenBefore) { throw 'Reopening changed the demo data.' }
    Write-Output 'PASS Repeated login launch stayed hidden. Both manual launches reopened the same instance, including after closing to the tray.'
} finally {
    foreach ($reopenProcess in $reopenProcesses) {
        if (!$reopenProcess.HasExited) {
            $reopenProcess.Kill()
            $reopenProcess.WaitForExit()
        }
        $reopenProcess.Dispose()
    }
    # The single-use demo folder can only contain these two files.
    foreach ($reopenFile in @($reopenState, $reopenError)) {
        if (Test-Path -LiteralPath $reopenFile) { Remove-Item -LiteralPath $reopenFile }
    }
    if ((Test-Path -LiteralPath $reopenData) -and !(Get-ChildItem -LiteralPath $reopenData -Force)) { Remove-Item -LiteralPath $reopenData }
}
