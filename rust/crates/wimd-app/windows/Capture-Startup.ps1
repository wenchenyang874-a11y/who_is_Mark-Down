[CmdletBinding()]
param([switch]$Recorder, [string]$RunId, [switch]$VerifyOnly, [string]$DocumentPath)

# Bug fix: application logging cannot observe time before Main, nor a kernel
# wait stack. Start WPR BEFORE launching the editor, never after the slow event.
# No persistent ETW session, cache clearing, process termination or network use.
$ErrorActionPreference = 'Stop'
$wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$application = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\WIMD.exe'))
$traceRoot = Join-Path $env:LOCALAPPDATA 'WIMD\Logs\Traces'
$utf8 = New-Object Text.UTF8Encoding($false)

function Write-Record([string]$Name, [object]$Value) {
    # Immutable files make partial/failing capture stages auditable. Never
    # overwrite an existing path, follow a selected user document, or log args.
    $bytes = $utf8.GetBytes(($Value | ConvertTo-Json -Depth 5))
    $stream = New-Object IO.FileStream((Join-Path $runDirectory $Name), [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function Assert-NormalDirectory([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Diagnostic output directory must not be a reparse point'
    }
}

if ($VerifyOnly) {
    # Packaging/tests can inspect the plan without UAC, recording, launching an
    # application, reading settings, or touching the personal trace directory.
    [pscustomobject]@{ recorder='Windows WPR'; duration_seconds=30; mode='file'; raw_stop_threshold_bytes=2GB;
        profiles=@('GeneralProfile','FileIO'); application_exists=(Test-Path -LiteralPath $application);
        wpr_exists=(Test-Path -LiteralPath $wpr); closes_windows=$false; clears_cache=$false } | ConvertTo-Json
    exit 0
}

if (-not $Recorder) { $RunId = [Guid]::NewGuid().ToString('N') }
if ($RunId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid capture identity' }
$runDirectory = Join-Path $traceRoot $RunId
$instance = 'WIMD-Startup-' + $RunId

if ($Recorder) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 5 }
    $owned = $false
    $failure = $null
    $startCode = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $stopReason = 'SetupFailed'
    try {
        Assert-NormalDirectory $traceRoot
        Assert-NormalDirectory $runDirectory
        Write-Record 'recorder-started.json' @{ stage='Starting'; utc=[DateTimeOffset]::UtcNow.ToString('o');
            instance=$instance; recorder_pid=$PID; duration_seconds=30; mode='file'; raw_stop_threshold_bytes=2GB }
        # Circular memory capture can overwrite the very process-start events
        # we need. File mode preserves the beginning; time/size trigger stop.
        # Merging/rundown has extra space/time cost, separate from recording.
        $temporaryTrace = Join-Path $runDirectory 'raw'
        [void][IO.Directory]::CreateDirectory($temporaryTrace)
        $output = & $wpr -start GeneralProfile -start FileIO -filemode -recordtempto $temporaryTrace -instancename $instance 2>&1
        $startCode = $LASTEXITCODE
        if ($startCode -ne 0) {
            Write-Record 'wpr-start-error.json' @{ exit_code=$startCode; output=($output | Out-String) }
            throw ('WPR start code ' + $startCode)
        }
        $owned = $true
        Write-Record 'recording-ready.json' @{ stage='Ready'; utc=[DateTimeOffset]::UtcNow.ToString('o'); instance=$instance }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $stopReason = 'Deadline'
        while ($clock.Elapsed.TotalSeconds -lt 30) {
            if (Test-Path -LiteralPath (Join-Path $runDirectory 'stop.request')) { $stopReason='ApplicationReady'; break }
            $rawBytes = (Get-ChildItem -LiteralPath $temporaryTrace -File -Recurse | Measure-Object -Property Length -Sum).Sum
            if ($rawBytes -ge 2GB) { $stopReason='RawSizeThreshold'; break }
            Start-Sleep -Milliseconds 250
        }
    }
    catch { $failure = $_.Exception.GetType().FullName }
    finally {
        if ($owned) {
            # A full/unwritable disk must not prevent stopping the owned session.
            try {
                Write-Record 'recording-saving.json' @{ stage='Saving'; utc=[DateTimeOffset]::UtcNow.ToString('o');
                    recorded_ms=$clock.Elapsed.TotalMilliseconds; stop_reason=$stopReason; instance=$instance }
            } catch { }
            $stopCode = -1
            try {
                $output = & $wpr -stop (Join-Path $runDirectory 'startup.etl') -instancename $instance 2>&1
                $stopCode = $LASTEXITCODE
            } catch {
                # PowerShell 5.1 can promote native stderr to a terminating
                # error. Keep the stop failure visible instead of losing state.
                $output = $_ | Out-String
                if ($LASTEXITCODE) { $stopCode=$LASTEXITCODE }
            }
            if ($stopCode -ne 0) {
                Write-Record 'wpr-stop-error.json' @{ exit_code=$stopCode; output=($output | Out-String) }
            }
            Write-Record 'recording-finished.json' @{ stage=$(if ($stopCode -eq 0) { 'Saved' } else { 'StopFailed' });
                utc=[DateTimeOffset]::UtcNow.ToString('o'); exit_code=$stopCode; instance=$instance; error_type=$failure }
        } else {
            Write-Record 'recording-failed.json' @{ stage='NotStarted'; utc=[DateTimeOffset]::UtcNow.ToString('o');
                start_exit_code=$startCode; error_type=$failure; instance=$instance }
        }
    }
    exit
}

$recorderProcess = $null
try {
    # The app launched this coordinator without elevation. An administrator-run
    # script must not accidentally create a privileged editing process.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run startup capture from a normal, non-administrator WIMD window'
    }
    if (-not (Test-Path -LiteralPath $application)) { throw 'Packaged WIMD executable not found' }
    [void][IO.Directory]::CreateDirectory($traceRoot)
    Assert-NormalDirectory $traceRoot
    [void][IO.Directory]::CreateDirectory($runDirectory)
    Assert-NormalDirectory $runDirectory
    Write-Record 'capture-request.json' @{ stage='Requested'; utc=[DateTimeOffset]::UtcNow.ToString('o'); launcher_pid=$PID }
    $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($runDirectory))
    if ($drive.AvailableFreeSpace -lt 6GB) {
        Write-Record 'insufficient-space.json' @{ required_bytes=6GB; available_bytes=$drive.AvailableFreeSpace }
        throw 'Insufficient diagnostic capture space'
    }
    $start = New-Object Diagnostics.ProcessStartInfo($powershell)
    $start.UseShellExecute = $true
    $start.Verb = 'runas'
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Recorder -RunId {1}' -f $PSCommandPath, $RunId
    $recorderProcess = [Diagnostics.Process]::Start($start)
    $readyClock = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'recording-ready.json'))) {
        if ($recorderProcess.HasExited) { throw 'Recorder exited before readiness' }
        if ($readyClock.Elapsed.TotalSeconds -gt 90) { throw 'Recorder readiness timed out; editor not launched' }
        Start-Sleep -Milliseconds 100
    }
    $editorStart = New-Object Diagnostics.ProcessStartInfo($application)
    $editorStart.UseShellExecute = $false
    $editorStart.WorkingDirectory = [IO.Path]::GetDirectoryName($application)
    if ($DocumentPath) {
        if (-not [IO.Path]::IsPathRooted($DocumentPath) -or $DocumentPath.Contains('"')) { throw 'Invalid document path argument' }
        $editorStart.Arguments = '"{0}"' -f $DocumentPath
    }
    $launchUtc = [DateTimeOffset]::UtcNow
    $editor = [Diagnostics.Process]::Start($editorStart)
    $targetPid = $editor.Id
    Write-Record 'target-process.json' @{ pid=$targetPid; launch_request_utc=$launchUtc.ToString('o') }
    # The recorder owns its automatic stop even if this coordinator exits.
    # Waiting here does not block a WPF dispatcher or terminate either editor.
    $saveClock = [Diagnostics.Stopwatch]::StartNew()
    $exitRecorded = $false
    $readyAt = $null
    $progressIndex = 0
    while (-not $recorderProcess.HasExited -and $saveClock.Elapsed.TotalSeconds -lt 180) {
        if ($editor.HasExited -and -not $exitRecorded) {
            Write-Record 'target-exited.json' @{ pid=$targetPid; exit_code=$editor.ExitCode; utc=[DateTimeOffset]::UtcNow.ToString('o') }
            $exitRecorded = $true
        }
        if (-not $readyAt) {
            foreach ($log in Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($traceRoot)) -Filter ('wimd-*-{0}-*.jsonl' -f $targetPid) -File) {
                if ($log.CreationTimeUtc -lt $launchUtc.UtcDateTime.AddSeconds(-1) -or $log.Length -gt 4MB) { continue }
                $logInput = New-Object IO.FileStream($log.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                $reader = New-Object IO.StreamReader($logInput)
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                if ($text.Contains('"stage":"StartupPreviewReady"') -or $text.Contains('"stage":"StartupPreviewDeferred"')) {
                    $readyAt = $saveClock.Elapsed.TotalSeconds
                }
            }
        }
        if ($readyAt -and $saveClock.Elapsed.TotalSeconds - $readyAt -ge 2 -and -not (Test-Path -LiteralPath (Join-Path $runDirectory 'stop.request'))) {
            Write-Record 'stop.request' @{ reason='ApplicationReady'; utc=[DateTimeOffset]::UtcNow.ToString('o') }
        }
        if ($saveClock.Elapsed.TotalSeconds -ge ($progressIndex + 1) * 10) {
            Write-Record ('coordinator-progress-{0:D3}.json' -f $progressIndex) @{ waited_ms=$saveClock.Elapsed.TotalMilliseconds;
                recorder_pid=$recorderProcess.Id; target_exited=$exitRecorded; utc=[DateTimeOffset]::UtcNow.ToString('o') }
            $progressIndex++
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $recorderProcess.HasExited) {
        Write-Record 'save-wait-exceeded.json' @{ stage='RecorderStillSaving'; recorder_pid=$recorderProcess.Id;
            utc=[DateTimeOffset]::UtcNow.ToString('o') }
    }
    # Keep this launch's normal JSONL next to its ETL so later log rotation does
    # not separate the timestamps from the native evidence. Do not copy other
    # processes' logs, settings, recovery snapshots or documents.
    $copied = 0
    $logRoot = [IO.Path]::GetDirectoryName($traceRoot)
    foreach ($log in Get-ChildItem -LiteralPath $logRoot -Filter ('wimd-*-{0}-*.jsonl' -f $targetPid) -File) {
        if ($log.CreationTimeUtc -lt $launchUtc.UtcDateTime.AddSeconds(-1)) { continue }
        if ($log.Length -gt 4MB) { continue }
        $logInput = New-Object IO.FileStream($log.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $destination = Join-Path $runDirectory ('application-{0:D4}.jsonl' -f $copied)
            $output = New-Object IO.FileStream($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try { $logInput.CopyTo($output) } finally { $output.Dispose() }
        } finally { $logInput.Dispose() }
        $copied++
    }
    Write-Record 'capture-complete.json' @{ stage=$(if ($recorderProcess.HasExited) { 'CoordinatorCompleted' } else { 'RecorderStillSaving' });
        application_logs_copied=$copied; target_exited=$exitRecorded;
        utc=[DateTimeOffset]::UtcNow.ToString('o'); recorder_exit_code=$(if ($recorderProcess.HasExited) { $recorderProcess.ExitCode } else { $null }) }
}
catch {
    if (Test-Path -LiteralPath $runDirectory) {
        Write-Record 'coordinator-failed.json' @{ stage='Failed'; utc=[DateTimeOffset]::UtcNow.ToString('o');
            error_type=$_.Exception.GetType().FullName; hresult=$_.Exception.HResult }
    }
    Add-Type -AssemblyName PresentationFramework
    [void][Windows.MessageBox]::Show('启动诊断未完成。未关闭任何 WIMD 窗口；请检查结果目录中的状态文件。', 'WIMD 启动诊断')
}
finally {
    if ($null -ne $editor) { $editor.Dispose() }
    if ($null -ne $recorderProcess) { $recorderProcess.Dispose() }
    if (Test-Path -LiteralPath $runDirectory) {
        $explorer = New-Object Diagnostics.ProcessStartInfo('explorer.exe')
        $explorer.UseShellExecute = $true
        $explorer.Arguments = '"{0}"' -f $runDirectory
        $opened = [Diagnostics.Process]::Start($explorer)
        if ($null -ne $opened) { $opened.Dispose() }
    }
}
