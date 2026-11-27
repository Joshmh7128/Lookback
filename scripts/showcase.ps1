<#
.SYNOPSIS
    Lookback showcase: build once, then run any component's demo by name.

.DESCRIPTION
    ./scripts/showcase.ps1 list              what can be demoed, in pipeline order
    ./scripts/showcase.ps1 <demo> [options]  run one demo
    ./scripts/showcase.ps1 all               run every unattended demo in order

    Footage is generated with ffmpeg test patterns (never third-party footage).
    Everything a demo writes (reports, clips, ratings, models) goes under -Out,
    a temp folder by default. Nothing is written into the repository.
    The walkthrough with expected output is docs/wiki/Showcase.md.

.PARAMETER Demo
    The demo to run. `list` prints them all.
.PARAMETER Out
    Output folder. Default: %TEMP%\lookback-showcase.
.PARAMETER Seconds
    How long a live demo runs. Each demo has its own default.
.PARAMETER GameProfile
    Profile for demos that take one (report, clipper, profiler). Default: generic,
    or overwatch where the demo is about HUD regions.
.PARAMETER Monitor
    Capture this monitor instead of the generated test-pattern window
    (capture, encode, watchdog). This captures whatever is on that screen.
.PARAMETER Mic
    Audio demo: also capture and encode the default microphone.
.PARAMETER NoBuild
    Skip `dotnet build Lookback.slnx -c Release` (use the last build).
.PARAMETER NoOpen
    Do not open reports and pages in the browser.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string] $Demo = 'list',
    [string] $Out = (Join-Path ([IO.Path]::GetTempPath()) 'lookback-showcase'),
    [int] $Seconds = 0,
    [string] $GameProfile = '',
    [int] $Monitor = -1,
    [switch] $Mic,
    [switch] $NoBuild,
    [switch] $NoOpen
)

# Cmdlet failures stop the script. Functions that run native tools relax this
# locally, because Windows PowerShell 5.1 turns their stderr into error records
# when output is redirected.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$Repo = Split-Path -Parent $PSScriptRoot
$PatternTitle = 'Lookback showcase pattern'
$ShowcasePipe = 'lookback.showcase'
$script:Built = $false

# ------------------------------------------------------------------ helpers

function Write-Step([string] $Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Write-Note([string] $Text) {
    Write-Host $Text -ForegroundColor DarkGray
}

function Write-Look([string[]] $Lines) {
    Write-Host ''
    Write-Host 'What to look at:' -ForegroundColor Yellow
    foreach ($l in $Lines) { Write-Host "  - $l" -ForegroundColor Yellow }
}

function Write-Command([string] $Exe, [string[]] $Arguments) {
    $name = Split-Path -Leaf $Exe
    $shown = foreach ($a in $Arguments) { if ($a -match '\s') { "`"$a`"" } else { $a } }
    Write-Host "> $name $($shown -join ' ')" -ForegroundColor Green
}

function Get-Seconds([int] $Default) {
    if ($Seconds -gt 0) { return $Seconds }
    return $Default
}

function Get-GameProfile([string] $Default) {
    if ($GameProfile) { return $GameProfile }
    return $Default
}

function ConvertTo-ArgumentLine([string[]] $Arguments) {
    $quoted = foreach ($a in $Arguments) {
        if ($a -eq '' -or $a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
    }
    return ($quoted -join ' ')
}

# Runs a tool on this console: its stdout, stderr and prompts go straight to
# the terminal (so `rate` stays interactive and progress lines redraw in place).
function Invoke-Tool([string] $Exe, [string[]] $Arguments, [switch] $AllowFailure) {
    Write-Command $Exe $Arguments
    $p = Start-Process -FilePath $Exe -ArgumentList (ConvertTo-ArgumentLine $Arguments) -NoNewWindow -Wait -PassThru
    $code = $p.ExitCode
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "$(Split-Path -Leaf $Exe) exited with code $code"
    }
    return $code
}

function Test-Command([string] $Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Assert-Ffmpeg {
    foreach ($t in 'ffmpeg', 'ffprobe', 'ffplay') {
        if (-not (Test-Command $t)) {
            throw "$t was not found on PATH. Lookback's demos need ffmpeg, ffprobe and ffplay (a gyan.dev essentials build has all three)."
        }
    }
}

function Initialize-Build {
    $ErrorActionPreference = 'Continue'  # native tools write progress to stderr; PS 5.1 would turn it into errors
    if ($script:Built) { return }
    if (-not $NoBuild) {
        Write-Step 'Building Lookback (Release)'
        Write-Command 'dotnet' @('build', 'Lookback.slnx', '-c', 'Release', '-v', 'q', '-nologo')
        Push-Location $Repo
        try {
            & dotnet build Lookback.slnx -c Release -v q -nologo
            if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
        }
        finally { Pop-Location }
    }
    $script:Built = $true
}

function Get-Exe([string] $Pattern) {
    $hit = Get-ChildItem -Path (Join-Path $Repo $Pattern) -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $hit) { throw "Not built: $Pattern. Run without -NoBuild." }
    return $hit.FullName
}

function Get-Testbench { Get-Exe 'src\Lookback.Testbench\bin\Release\net10.0\lookback-testbench.exe' }
function Get-Recorder { Get-Exe 'src\Lookback.Recorder\bin\Release\net10.0\Lookback.Recorder.exe' }
function Get-CaptureBench { Get-Exe 'src\Lookback.Capture.Cli\bin\Release\net10.0-windows*\lookback-capture.exe' }
function Get-DetectBench { Get-Exe 'src\Lookback.Detection.Cli\bin\Release\net10.0-windows*\lookback-detect.exe' }
function Get-TestWindow { Get-Exe 'tests\Lookback.Detection.TestWindow\bin\Release\net10.0-windows*\lookback-testwindow.exe' }

function Get-OutDir([string] $Name) {
    $dir = Join-Path $Out $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}

function Open-Page([string] $Target) {
    if ($NoOpen) {
        Write-Note "open: $Target"
        return
    }
    Write-Note "opening $Target"
    Start-Process $Target | Out-Null
}

# A 60 s 1280x720 test pattern with a 440 Hz tone. Two "moments" (a white
# flash with a low thump, at 12 s and 32 s) and a third thump at 52 s over
# black, frozen video, so the clipper finds two clean candidates and one that
# fails QA. Generated once into -Out; never committed.
function Get-Footage {
    $ErrorActionPreference = 'Continue'  # native tools write progress to stderr; PS 5.1 would turn it into errors
    Assert-Ffmpeg
    $dir = Get-OutDir 'footage'
    $file = Join-Path $dir 'showcase-pattern.mp4'
    if (Test-Path $file) { return $file }
    Write-Step 'Generating test footage (ffmpeg lavfi)'
    $video = "testsrc2=size=1280x720:rate=30," +
        "drawbox=x=0:y=0:w=iw:h=ih:color=white@1:t=fill:enable='between(mod(t,20),12,12.3)*lt(t,45)'," +
        "drawbox=x=0:y=0:w=iw:h=ih:color=black@1:t=fill:enable='gte(t,45)'"
    $audio = "aevalsrc='0.05*sin(2*PI*440*t)+if(between(mod(t,20),12,12.6),0.6*sin(2*PI*90*t)*exp(-6*(mod(t,20)-12)),0)':s=48000"
    $ffArgs = @('-v', 'error', '-y', '-f', 'lavfi', '-i', $video, '-f', 'lavfi', '-i', $audio,
        '-t', '60', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', $file)
    & ffmpeg @ffArgs
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg could not generate the test footage' }
    Write-Note "footage $file"
    return $file
}

# A live test-pattern window for the capture demos, so they never capture
# your desktop. Returns the ffplay process.
function Start-PatternWindow {
    Assert-Ffmpeg
    $ffArgs = @('-loglevel', 'error', '-window_title', "`"$PatternTitle`"", '-x', '1280', '-y', '720',
        '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=60')
    $p = Start-Process -FilePath 'ffplay' -ArgumentList $ffArgs -PassThru -WindowStyle Normal
    Start-Sleep -Milliseconds 1500
    return $p
}

# Beeps on the default speakers for the audio demo, quietly. Returns the process.
function Start-Tone([int] $ForSeconds) {
    $src = "aevalsrc='0.3*sin(2*PI*660*t)*lt(mod(t,1),0.25)':s=48000:d=$ForSeconds"
    $p = Start-Process -FilePath 'ffplay' -ArgumentList @('-loglevel', 'error', '-nodisp', '-autoexit', '-volume', '40', '-f', 'lavfi', '-i', "`"$src`"") -PassThru -WindowStyle Hidden
    return $p
}

function Stop-Quietly($Process) {
    if ($Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
}

# Proto3 JSON leaves out fields at their default (false, 0, empty), so read
# them with a default instead of failing under strict mode.
function Get-Prop($Object, [string] $Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $prop = $Object.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    return $Default
}

function Hide-Token([string] $Text) {
    return [regex]::Replace($Text, '(token["=:]+"?)([0-9A-Fa-f]{4})[0-9A-Fa-f]+', '$1$2...')
}

function Show-Probe([string] $File) {
    $ErrorActionPreference = 'Continue'
    Write-Command 'ffprobe' @('-count_packets', '-show_streams', $File)
    $info = (& ffprobe -v error -count_packets -show_entries 'stream=codec_type,codec_name,width,height,sample_rate,channels,avg_frame_rate,nb_read_packets,duration' -of json $File) -join "`n" | ConvertFrom-Json
    foreach ($st in $info.streams) {
        if ($st.codec_type -eq 'video') {
            $parts = ([string]$st.avg_frame_rate).Split('/')
            $fps = if ($parts.Count -eq 2 -and [double]$parts[1] -ne 0) { [double]$parts[0] / [double]$parts[1] } else { 0 }
            Write-Host ("  video {0} {1}x{2}, {3:N3} fps average, {4} frames" -f $st.codec_name, $st.width, $st.height, $fps, $st.nb_read_packets)
        }
        else {
            Write-Host ("  {0} {1} {2} Hz, {3} channels, {4} packets" -f $st.codec_type, $st.codec_name, $st.sample_rate, $st.channels, $st.nb_read_packets)
        }
    }
    Write-Command 'ffmpeg' @('-v', 'error', '-i', $File, '-f', 'null', '-')
    $errors = @(& ffmpeg -v error -i $File -f null - 2>&1)
    if ($errors.Count -eq 0) { Write-Host '  decodes with no errors' } else { $errors | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" } }
}

function Find-MlPython {
    if ($env:LOOKBACK_ML_PYTHON -and (Test-Path $env:LOOKBACK_ML_PYTHON)) { return $env:LOOKBACK_ML_PYTHON }
    $venv = Join-Path $Repo 'ml\.venv\Scripts\python.exe'
    if (Test-Path $venv) { return $venv }
    return $null
}

# Waits for a line starting with $Prefix in a redirected stdout file.
function Wait-Line([string] $File, [string] $Prefix, [int] $TimeoutSeconds, $Process) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $File) {
            $line = Get-Content -Path $File -ErrorAction SilentlyContinue | Where-Object { $_.StartsWith($Prefix) } | Select-Object -First 1
            if ($line) { return $line }
        }
        if ($Process -and $Process.HasExited) { return $null }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

# Connects to the recorder's WebSocket like the shell does and prints the
# stream: one line per session, profile, game event, candidate and QA report,
# and a per-second count of signal frames (30 fps of them would flood the screen).
function Watch-EventStream([string] $WsUrl, [string] $Token, [int] $ForSeconds, $StopWhenExited) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $uri = [Uri]("$WsUrl" + '?token=' + [Uri]::EscapeDataString($Token))
    $ws.ConnectAsync($uri, [Threading.CancellationToken]::None).Wait()
    Write-Note "connected to $WsUrl (token in the query string, like the shell)"
    $buffer = New-Object byte[] 65536
    $segment = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $buffer)
    $message = New-Object System.IO.MemoryStream
    $counts = @{}
    $total = 0
    $lastTick = [DateTime]::UtcNow
    $deadline = [DateTime]::UtcNow.AddSeconds($ForSeconds)
    $exitedAt = $null
    try {
        while ([DateTime]::UtcNow -lt $deadline) {
            $task = $ws.ReceiveAsync($segment, [Threading.CancellationToken]::None)
            while (-not $task.Wait(250)) {
                if ([DateTime]::UtcNow -ge $deadline) { break }
                if ($StopWhenExited -and $StopWhenExited.HasExited) {
                    if (-not $exitedAt) { $exitedAt = [DateTime]::UtcNow }
                    if (([DateTime]::UtcNow - $exitedAt).TotalSeconds -gt 2) { $deadline = [DateTime]::UtcNow }
                }
            }
            if (-not $task.IsCompleted) { break }
            $r = $task.Result
            if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { break }
            $message.Write($buffer, 0, $r.Count)
            if (-not $r.EndOfMessage) { continue }
            $json = [Text.Encoding]::UTF8.GetString($message.ToArray())
            $message.SetLength(0)
            $total++
            $e = $json | ConvertFrom-Json
            $topic = [string](Get-Prop $e 'topic' '')
            if ($topic.StartsWith('signals.')) {
                if ($counts.ContainsKey($topic)) { $counts[$topic]++ } else { $counts[$topic] = 1 }
            }
            else {
                $detail = ''
                $s = Get-Prop $e 'sessionStarted'
                $g = Get-Prop $e 'gameEvent'
                $c = Get-Prop $e 'clipCandidate'
                $q = Get-Prop $e 'qaReport'
                $pd = Get-Prop $e 'profileDetected'
                $se = Get-Prop $e 'sessionEnded'
                $gl = Get-Prop $e 'gameLifecycle'
                if ($s) { $detail = "SessionStarted $(Get-Prop $s 'sourceKind') $(Get-Prop $s 'retention') profile=$(Get-Prop $s 'gameProfile')" }
                elseif ($se) { $detail = "SessionEnded $(Get-Prop $se 'reason')" }
                elseif ($pd) { $detail = "ProfileDetected $(Get-Prop $pd 'profileId') $(Get-Prop $pd 'method')" }
                elseif ($g) { $detail = "GameEvent $(Get-Prop $g 'name') confidence $([Math]::Round([double](Get-Prop $g 'confidence' 0), 2))" }
                elseif ($c) { $detail = "ClipCandidate $(Get-Prop $c 'candidateId') score $([Math]::Round([double](Get-Prop $c 'heuristicScore' 0), 2))" }
                elseif ($q) { $detail = "QaReport $(Get-Prop $q 'candidateId') passed=$(Get-Prop $q 'passed' $false)" }
                elseif ($gl) { $detail = "GameLifecycle $(Get-Prop $gl 'kind')" }
                else { $detail = '(other payload)' }
                Write-Host ("  {0,-15} {1,-16} {2}" -f $topic, (Get-Prop $e 'sourceId' ''), $detail)
            }
            if (([DateTime]::UtcNow - $lastTick).TotalSeconds -ge 5 -and $counts.Count -gt 0) {
                $summary = ($counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', '
                Write-Note "  (last 5 s: $summary)"
                $counts.Clear()
                $lastTick = [DateTime]::UtcNow
            }
        }
    }
    finally {
        $ws.Abort()
        $ws.Dispose()
    }
    Write-Note "  $total envelopes received"
}

# ------------------------------------------------------------------ demos

function Show-Recorder {
    Initialize-Build
    $rec = Get-Recorder
    $tb = Get-Testbench
    $footage = Get-Footage
    $dir = Get-OutDir 'recorder'
    $stdout = Join-Path $dir 'recorder.stdout.txt'
    $stderr = Join-Path $dir 'recorder.stderr.txt'
    Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue

    Write-Step 'Recorder: start-up handshake'
    $recArgs = @('--Recorder:Profiler=true', "--Recorder:PipeName=$ShowcasePipe")
    Write-Command $rec $recArgs
    $p = Start-Process -FilePath $rec -ArgumentList $recArgs -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    $live = $null
    try {
        $ready = Wait-Line $stdout 'LOOKBACK_READY ' 30 $p
        if (-not $ready) { throw "The recorder did not print LOOKBACK_READY. See $stderr" }
        $profilerLine = Wait-Line $stdout 'LOOKBACK_PROFILER ' 5 $p
        Write-Host (Hide-Token $ready)
        if ($profilerLine) { Write-Host (Hide-Token $profilerLine) }
        $hs = $ready.Substring('LOOKBACK_READY '.Length) | ConvertFrom-Json
        if ($profilerLine) { Open-Page ($profilerLine.Substring('LOOKBACK_PROFILER '.Length)) }

        Write-Step "Feeding the generated clip into the recorder's pipe at 4x"
        $tbLog = Join-Path $dir 'testbench-live.txt'
        $liveArgs = @('live', $footage, '--profile', (Get-GameProfile 'generic'), '--pipe', $ShowcasePipe, '--speed', '4')
        Write-Command $tb $liveArgs
        $live = Start-Process -FilePath $tb -ArgumentList ($liveArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -RedirectStandardOutput $tbLog -RedirectStandardError (Join-Path $dir 'testbench-live.err.txt') -PassThru -WindowStyle Hidden

        Write-Step 'Event stream on the WebSocket (what the shell sees)'
        Watch-EventStream $hs.ws $hs.token (Get-Seconds 40) $live
    }
    finally {
        Stop-Quietly $live
        Stop-Quietly $p
    }
    Write-Look @(
        'Stdout carries only the LOOKBACK_READY (and LOOKBACK_PROFILER) lines; logs go to stderr.',
        'session first: SessionStarted SOURCE_KIND_FILE RETENTION_OWNED, then profile, game.event, clip.candidate and qa.report as the clip plays.',
        'signals.* frames arrive at the analysis rate and are only counted here; the profiler page draws them.',
        "Recorder log: $stderr")
}

function Show-Report {
    Initialize-Build
    $tb = Get-Testbench
    $footage = Get-Footage
    $dir = Get-OutDir 'reports'
    Write-Step 'Footage signals and the timeline report'
    $before = @(Get-ChildItem $dir -Filter '*.html' -ErrorAction SilentlyContinue | ForEach-Object FullName)
    Invoke-Tool $tb @('analyze', $footage, '--out', $dir, '--profile', (Get-GameProfile 'generic')) | Out-Null
    $report = Get-ChildItem $dir -Filter '*.html' | Where-Object { $before -notcontains $_.FullName } | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($report) { Open-Page $report.FullName }
    Write-Look @(
        '1800 video frames for a 60 s clip: signals always run at 384x216, 30 fps.',
        'In the report, flash.brighten and audio.rms_db spike at 12 s and 32 s; at 45 s the picture goes black and frozen.',
        'Hover a lane for values; click to seek. The clip candidates and QA are drawn under the signals.')
}

function Show-Profiles {
    Initialize-Build
    $tb = Get-Testbench
    Write-Step 'The profile tree'
    Invoke-Tool $tb @('profiles') | Out-Null
    Write-Step 'Profiles by tag'
    Invoke-Tool $tb @('profiles', '--tag', 'shooter') | Out-Null

    $id = Get-GameProfile 'overwatch'
    $game = $id.Split('.')[0]
    $file = Join-Path $Repo "profiles\$game\$id.json"
    if (Test-Path $file) {
        Write-Step "One profile: $id ($([IO.Path]::GetFileName($file)))"
        $text = (Get-Content $file | Where-Object { -not $_.TrimStart().StartsWith('//') }) -join "`n"
        $p = $text | ConvertFrom-Json
        Write-Host "executables  $($p.executables -join ', ')"
        Write-Host "policy       presentHook=$($p.capturePolicy.allowPresentHook) memoryReading=$($p.capturePolicy.allowMemoryReading)"
        if ($p.PSObject.Properties['regions']) {
            Write-Host 'HUD regions (fractions of the frame):'
            foreach ($r in $p.regions) { Write-Host ("  {0,-20} x {1,-5} y {2,-5} w {3,-5} h {4,-5}" -f $r.name, $r.x, $r.y, $r.w, $r.h) }
        }
        if ($p.PSObject.Properties['events']) {
            Write-Host 'Event rules:'
            foreach ($ev in $p.events) {
                $conds = @()
                foreach ($k in 'when', 'whenAny') {
                    if ($ev.PSObject.Properties[$k]) {
                        foreach ($c in $ev.$k) {
                            $test = ($c.PSObject.Properties | Where-Object { $_.Name -ne 'signal' } | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ' '
                            $conds += "$k $($c.signal) $test"
                        }
                    }
                }
                Write-Host ("  {0,-20} weight {1,-4} {2}" -f $ev.name, $ev.weight, ($conds -join '; '))
            }
        }
    }

    Write-Step "The generated clip with --profile $id (report with the calibration overlay)"
    $footage = Get-Footage
    $dir = Get-OutDir 'reports'
    $before = @(Get-ChildItem $dir -Filter '*.html' -ErrorAction SilentlyContinue | ForEach-Object FullName)
    Invoke-Tool $tb @('analyze', $footage, '--out', $dir, '--profile', $id) | Out-Null
    $report = Get-ChildItem $dir -Filter '*.html' | Where-Object { $before -notcontains $_.FullName } | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($report) { Open-Page $report.FullName }
    Write-Look @(
        'The tree: game, sub-game, mode; only games list executables.',
        "In the report: an events lane, one lane per rule condition (region.<name>.activity against its threshold) and the calibration overlay with $id's HUD regions drawn on the video.",
        'A test pattern trips HUD rules (it moves everywhere). That is expected; judge a profile on real footage.',
        'The browser demo (showcase.ps1 browse) shows the same profiles as a page.')
}

function Show-Browse {
    Initialize-Build
    $tb = Get-Testbench
    $dir = Get-OutDir 'browse'
    $page = Join-Path $dir 'browser.html'
    Write-Step 'The dataset and profile browser'
    Invoke-Tool $tb @('browse', '--out', $page) | Out-Null
    Open-Page $page
    Write-Look @(
        'Left: the profile tree. Pick a profile to see what it identifies, its key signals, its HUD regions drawn on a frame, and how it is detected.',
        'Dataset coverage comes from datasets/<game>/manifest*.json; only example manifests are committed, so coverage is empty until you add your own.',
        'With --results <dataset --out folders> it also shows precision and recall per event.')
}

function Show-Profiler {
    Initialize-Build
    $tb = Get-Testbench
    $footage = Get-Footage
    $dir = Get-OutDir 'profiler'
    $log = Join-Path $dir 'live.txt'
    $secs = Get-Seconds 90
    Write-Step "Live profiler: the clip plays in real time, looping, for $secs s (Ctrl+C stops)"
    $liveArgs = @('live', $footage, '--profile', (Get-GameProfile 'overwatch'), '--loop')
    Write-Command $tb $liveArgs
    $p = Start-Process -FilePath $tb -ArgumentList ($liveArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -RedirectStandardOutput $log -RedirectStandardError (Join-Path $dir 'live.err.txt') -PassThru -WindowStyle Hidden
    try {
        $line = Wait-Line $log 'profiler ' 20 $p
        if (-not $line) { throw "No profiler link. See $log" }
        Write-Host (Hide-Token $line)
        Open-Page ($line.Substring('profiler '.Length).Trim())
        Write-Note 'The clip starts when the page connects.'
        $deadline = [DateTime]::UtcNow.AddSeconds($secs)
        while ([DateTime]::UtcNow -lt $deadline -and -not $p.HasExited) { Start-Sleep -Milliseconds 500 }
    }
    finally { Stop-Quietly $p }
    Write-Look @(
        'Event tiles flash as rules fire; the rule panel shows each condition against its threshold.',
        'Signal sparklines scroll live; candidates and QA appear as the clipper finishes them.',
        'Nothing is written to disk by `live`.')
}

function Show-Detect {
    $ErrorActionPreference = 'Continue'  # native tools write progress to stderr; PS 5.1 would turn it into errors
    Initialize-Build
    $det = Get-DetectBench
    $win = Get-TestWindow
    $dir = Get-OutDir 'detect'
    $settings = Join-Path $dir 'detection.json'
    Remove-Item $settings -ErrorAction SilentlyContinue
    $title = 'Lookback showcase game'
    Write-Step 'Game detection: opt a stand-in "game" in, in a throwaway settings file'
    Write-Note 'Your own %LOCALAPPDATA%\Lookback\detection.json is not touched.'
    Invoke-Tool $det @('allow', 'lookback-testwindow.exe', '--profile', 'generic', '--settings', $settings) | Out-Null

    $windowSeconds = 10
    $watch = [Math]::Max((Get-Seconds 20), $windowSeconds + 8)
    Write-Step "Watching for $watch s; the stand-in window opens now and closes after $windowSeconds s"
    # The stand-in closes when its stdin closes, so hold a pipe open; no console window.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $win
    $psi.Arguments = ConvertTo-ArgumentLine @($title, "$windowSeconds")
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $w = [System.Diagnostics.Process]::Start($psi)
    try {
        Start-Sleep -Milliseconds 800
        Write-Step 'One look at that window (lookback-detect windows, filtered to it)'
        Write-Command $det @('windows', '--settings', $settings)
        $rows = @(& $det windows --settings $settings)
        if ($rows.Count -gt 0) { Write-Host $rows[0] }
        $rows | Select-String -SimpleMatch 'lookback-testwindow.exe' -Context 0, 1 | ForEach-Object {
            Write-Host $_.Line
            foreach ($next in $_.Context.PostContext) { Write-Host $next }
        }
        Write-Step 'Lifecycle'
        Invoke-Tool $det @('--seconds', "$watch", '--settings', $settings) | Out-Null
    }
    finally { Stop-Quietly $w }
    Write-Look @(
        'DETECTED about 3 s after the window appears (the start hold), with the capture target and policy (external capture only: the user opted it in).',
        'No FOCUSED: the stand-in window never takes focus. Click it while it is open to see FOCUSED/UNFOCUSED.',
        'EXITED about 5 s after it closes (the exit grace, in case it relaunches).',
        'Detection reads the process list and top-level windows only: no hooks, no memory reading.')
}

function Show-Clipper {
    Initialize-Build
    $tb = Get-Testbench
    $footage = Get-Footage
    $dir = Get-OutDir 'reports'
    Write-Step 'Clipper v1: candidates and QA'
    $before = @(Get-ChildItem $dir -Filter '*.html' -ErrorAction SilentlyContinue | ForEach-Object FullName)
    Invoke-Tool $tb @('analyze', $footage, '--out', $dir, '--profile', (Get-GameProfile 'generic')) | Out-Null
    $report = Get-ChildItem $dir -Filter '*.html' | Where-Object { $before -notcontains $_.FullName } | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($report) { Open-Page $report.FullName }
    Write-Look @(
        'clips: 3 candidates, 2 pass QA. The first two are the flash-and-thump moments; each reason lists the events and the curve peak with its top z-scores.',
        'The third (around 37-55 s) is a thump over black, frozen video: QA fail (black, frozen) and a low score.',
        'In the report, the excitement curve and its high/low hysteresis lines sit under the signals, with each candidate window and its QA.')
}

function Show-Capture {
    Initialize-Build
    $cap = Get-CaptureBench
    $secs = Get-Seconds 15
    Write-Step 'Capture targets on this machine'
    Invoke-Tool $cap @('--list') | Out-Null
    $pattern = $null
    try {
        if ($Monitor -ge 0) {
            Write-Note "Capturing monitor ${Monitor}: whatever is on that screen."
            $target = @('--monitor', "$Monitor")
        }
        else {
            $pattern = Start-PatternWindow
            $target = @('--window', $PatternTitle)
        }
        Write-Step "Live capture and analysis for $secs s, with the live profiler"
        Write-Note 'Open the printed profiler link to watch the signals live.'
        Invoke-Tool $cap ($target + @('--seconds', "$secs", '--profiler')) | Out-Null
    }
    finally { Stop-Quietly $pattern }
    Write-Look @(
        'The first line names the backend (wgc), the adapter that owns the window''s monitor, and persist: True/False.',
        'Every second: capture fps (WGC sends a frame only when the window changes; the pattern changes every frame), analysis fps (thinned to 30) and dropped frames (should be 0).',
        'Nothing is written to disk without --encode.')
}

function Show-Encode {
    $ErrorActionPreference = 'Continue'  # native tools write progress to stderr; PS 5.1 would turn it into errors
    Initialize-Build
    $cap = Get-CaptureBench
    $secs = Get-Seconds 12
    $dir = Get-OutDir 'clips'
    $clip = Join-Path $dir 'showcase-encode.mp4'
    Remove-Item $clip -ErrorAction SilentlyContinue
    $pattern = $null
    try {
        if ($Monitor -ge 0) { $target = @('--monitor', "$Monitor") }
        else {
            $pattern = Start-PatternWindow
            $target = @('--window', $PatternTitle)
        }
        Write-Step "Live encode for $secs s, then save the last 8 s from the replay ring"
        Invoke-Tool $cap ($target + @('--seconds', "$secs", '--encode', '8', '--out', $clip, '--codec', 'hevc', '--fps', '60', '--size', '1280x720', '--mbps', '10')) | Out-Null
    }
    finally { Stop-Quietly $pattern }
    if (Test-Path $clip) {
        Write-Step 'The saved clip'
        Show-Probe $clip
        Write-Command 'ffprobe' @('-skip_frame', 'nokey', '-show_entries', 'frame=pts_time', $clip)
        $keys = & ffprobe -v error -select_streams v:0 -skip_frame nokey -show_entries frame=pts_time -of csv=p=0 $clip
        Write-Host "keyframes at $((@($keys) | ForEach-Object { [Math]::Round([double]$_, 3) }) -join ', ') s"
        Open-Page $dir
    }
    Write-Look @(
        '"encoding Hevc 1280x720@60 ... with <encoder> (hardware)": the MFT picked by the capture adapter''s LUID (NVENC on an NVIDIA card, AMF on AMD; software H.264 if there is none).',
        'encode ~60 fps with repeated/skipped/busy near 0: the constant-rate ticker holds 60 fps whatever the capture rate.',
        'The ticker summary line, then the clip: hevc 1280x720, avg_frame_rate 60/1, keyframes every 2 s from 0.',
        "Clip: $clip (plays in any player).")
}

function Show-Audio {
    Initialize-Build
    $cap = Get-CaptureBench
    $secs = Get-Seconds 10
    $dir = Get-OutDir 'clips'
    $clip = Join-Path $dir 'showcase-audio.mp4'
    Remove-Item $clip -ErrorAction SilentlyContinue
    Write-Step 'Audio endpoints'
    Invoke-Tool $cap @('audio', '--list') | Out-Null
    Write-Step "Live audio for $secs s while a quiet beep plays on the default speakers"
    Write-Note 'Endpoint loopback records everything the speakers play, not only the beep.'
    $tone = Start-Tone ($secs + 3)
    try {
        $audioArgs = @('audio', '--seconds', "$secs", '--out', $clip)
        if ($Mic) { $audioArgs += '--mic' }
        Invoke-Tool $cap $audioArgs | Out-Null
    }
    finally { Stop-Quietly $tone }
    if (Test-Path $clip) {
        Write-Step 'The saved audio clip'
        Show-Probe $clip
    }
    Write-Look @(
        'The plan line says what this session gets and why (endpoint loopback on Windows 10; process loopback needs build 20348+).',
        'Every second: level (the beep shows as a level well above silence), packets, 10 ms hops, the measured device rate (about 48000 Hz), clock resets, glitches and bytes allocated on the capture thread (0).',
        'The clip: aac 48000 Hz stereo (a second aac stream with -Mic).')
}

function Show-GovernorProbe {
    Initialize-Build
    $cap = Get-CaptureBench
    Write-Step 'Governor probe: the machine profile and the recording plan'
    $probeArgs = @('probe', '--store', (Get-OutDir 'store'))
    if ($Monitor -ge 0) { $probeArgs += @('--monitor', "$Monitor") }
    Invoke-Tool $cap $probeArgs | Out-Null
    Write-Look @(
        'RAM, the store volume (free space, SSD or HDD), every adapter with its monitors and hardware encoders.',
        'The plan for the captured monitor: tier (for example 2160p60-hevc), ring size and disk budget, plus any notes and unknowns.',
        'It reads only what a normal user can and never throws.')
}

function Show-Watchdog {
    Initialize-Build
    Write-Step 'Fault injection: encoders that hang, never output, or refuse input'
    $capTests = Get-Exe 'tests\Lookback.Capture.Tests\bin\Release\net10.0-windows*\Lookback.Capture.Tests.exe'
    Invoke-Tool $capTests @('-class', 'Lookback.Capture.Tests.EncodeTickerFaultTests', '-reporter', 'verbose', '-noColor') | Out-Null
    Write-Step 'The watchdog itself'
    $coreTests = Get-Exe 'tests\Lookback.Core.Tests\bin\Release\net10.0\Lookback.Core.Tests.exe'
    Invoke-Tool $coreTests @('-class', 'Lookback.Core.Tests.StallWatchdogTests', '-reporter', 'verbose', '-noColor') | Out-Null

    $cap = Get-CaptureBench
    $pattern = $null
    try {
        if ($Monitor -ge 0) { $target = @('--monitor', "$Monitor") }
        else {
            $pattern = Start-PatternWindow
            $target = @('--window', $PatternTitle)
        }
        $clip = Join-Path (Get-OutDir 'clips') 'showcase-watchdog.mp4'
        Write-Step 'A live encode with a tight watchdog, the encoder on its own device, and no blocklist writes'
        Invoke-Tool $cap ($target + @('--seconds', "$(Get-Seconds 10)", '--encode', '6', '--out', $clip, '--codec', 'h264', '--fps', '60', '--size', '1280x720', '--stall-ms', '750', '--isolate', '--no-blocklist')) | Out-Null
    }
    finally { Stop-Quietly $pattern }

    $blocklist = Join-Path $env:LOCALAPPDATA 'Lookback\encoder-blocklist.json'
    Write-Step 'The encoder blocklist on this machine (read only)'
    if (Test-Path $blocklist) { Get-Content $blocklist | Write-Host }
    else { Write-Host "none: $blocklist does not exist (no encoder has stalled here)" }
    Write-Look @(
        'Each fault test names what it proves: a hung encoder is abandoned and the next takes over on the same tick grid; when every encoder stalls, or too many swaps happen, the process ends instead of hanging; Dispose returns promptly; the blocklist survives a restart.',
        'In the live run, a healthy encoder never trips the 750 ms watchdog: no STALLED line, no swaps. If one did, the bench would print STALLED: with every thread''s phase and swap to the next encoder.',
        '--no-blocklist keeps a demo with a tight threshold from blocklisting a healthy encoder on this machine.')
}

function Show-Rate {
    Initialize-Build
    $tb = Get-Testbench
    $footage = Get-Footage
    $datasets = Get-OutDir 'datasets'
    $gameDir = Join-Path $datasets 'generic'
    $media = Join-Path $gameDir 'media'
    New-Item -ItemType Directory -Force -Path $media | Out-Null
    Copy-Item $footage (Join-Path $media 'showcase-pattern.mp4') -Force
    $manifest = Join-Path $gameDir 'manifest.json'
    @'
{
  "game": "generic",
  "entries": [
    {
      "file": "media/showcase-pattern.mp4",
      "source": "generated test pattern (ffmpeg lavfi), made by scripts/showcase.ps1",
      "retention": "owned",
      "profile": "generic",
      "clips": [ { "start": 8, "end": 15 }, { "start": 28, "end": 35 } ],
      "labels": [ { "event": "impact", "at": 12.0 }, { "event": "impact", "at": 32.0 } ]
    }
  ]
}
'@ | Set-Content -Path $manifest -Encoding ascii
    Write-Step 'Rating candidates in the terminal (writes to the showcase folder, not datasets/)'
    Write-Note 'Type 1-5 and Enter per candidate; 0 clears, Enter skips, p plays it with ffplay, q stops.'
    $rateArgs = @('rate', $manifest, '--datasets', $datasets, '--out', (Get-OutDir 'rate-out'), '--again')
    $model = Join-Path $Out 'ml\model'
    if (Test-Path (Join-Path $model 'model.json')) { $rateArgs += @('--model', $model) }
    Invoke-Tool $tb $rateArgs | Out-Null
    $ratings = Join-Path $gameDir 'ratings.jsonl'
    if (Test-Path $ratings) {
        Write-Step 'What was stored'
        Get-Content $ratings | Select-Object -Last 3 | ForEach-Object {
            $r = $_ | ConvertFrom-Json
            Write-Host ("  {0}  stars {1}  label {2}  heuristic {3}  schema {4}, {5} features" -f $r.candidateId, $r.stars, $r.label, [Math]::Round([double]$r.heuristicScore, 2), $r.featureSchema, @($r.features).Count)
        }
        Write-Note "  $ratings"
    }
    Write-Look @(
        'Each candidate shows its window, profile, heuristic score and reasons; ratings are about the moment, not video quality.',
        'Each rating is one line in <datasets>/<game>/ratings.jsonl, gated by RetentionRules (an ephemeral or official-player session is refused).',
        'Session ids are stable per file, so a second pass without --again asks only about new candidates.')
}

function Show-Train {
    $py = Find-MlPython
    if (-not $py) {
        Write-Host 'No Python environment for ml/. Set it up once (inside the repo, git-ignored):' -ForegroundColor Yellow
        Write-Host '  cd ml; python -m venv .venv; .venv\Scripts\python -m pip install -r requirements.txt'
        Write-Host 'or point LOOKBACK_ML_PYTHON at a python.exe that has ml/requirements.txt installed.'
        exit 1
    }
    $synth = Join-Path $Out 'ml\synth'
    $model = Join-Path $Out 'ml\model'
    Push-Location (Join-Path $Repo 'ml')
    try {
        Write-Step 'Synthetic ratings (three made-up games), in the showcase folder'
        Invoke-Tool $py @('-m', 'lookback_ml.synth', '--out', $synth) | Out-Null
        Write-Step 'Training: logistic regression, 5-fold cross-validation by session, Platt calibration per game, ONNX export'
        Invoke-Tool $py @('-m', 'lookback_ml.train', '--datasets', $synth, '--out', $model) | Out-Null
    }
    finally { Pop-Location }
    Write-Look @(
        'The tables are held-out numbers: the learned model against the heuristic score (AUC, log loss, Brier, ECE, precision@3), overall and per game.',
        '"calibrated" is the model after per-game Platt scaling: same AUC, lower log loss and ECE.',
        "Model: $model (model.onnx + model.json + report.md). Synthetic data proves the pipeline, not the model.")
}

function Show-Scorer {
    Initialize-Build
    Write-Step 'The ONNX scorer in C#: fixture parity with Python, calibration and the heuristic fallback'
    $tests = Get-Exe 'tests\Lookback.Scoring.Tests\bin\Release\net10.0\Lookback.Scoring.Tests.exe'
    Invoke-Tool $tests @('-class', 'Lookback.Scoring.Tests.LearnedClipScorerTests', '-class', 'Lookback.Scoring.Tests.ClipperFeatureMapTests', '-reporter', 'verbose', '-noColor') | Out-Null
    $model = Join-Path $Out 'ml\model\model.json'
    if (Test-Path $model) {
        Write-Step 'The model from the train demo'
        $m = Get-Content $model -Raw | ConvertFrom-Json
        $m.PSObject.Properties | Where-Object { $_.Value -isnot [array] -and $_.Value -isnot [pscustomobject] } | ForEach-Object { Write-Host ("  {0,-20} {1}" -f $_.Name, $_.Value) }
    }
    Write-Look @(
        'The fixture test runs a tiny committed model and must match ONNX Runtime in Python within 1e-5 (raw and calibrated).',
        'Fallback tests: no model, a corrupt model, a game the model does not cover, or missing features all leave the heuristic score standing. A broken model never stops clipping.')
}

function Show-Shell {
    $ErrorActionPreference = 'Continue'  # native tools write progress to stderr; PS 5.1 would turn it into errors
    Initialize-Build
    if (-not (Test-Command 'node')) { throw 'Node.js 24 is needed for the desktop shell (node was not found).' }
    $app = Join-Path $Repo 'app'
    $footage = Get-Footage
    Push-Location $app
    try {
        if (-not (Test-Path (Join-Path $app 'node_modules'))) {
            Write-Step 'Installing the shell''s packages into app/node_modules (npm ci, once)'
            & npm ci
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        }
        Write-Step 'The desktop shell'
        Write-Host 'In the app:'
        Write-Host '  1. The toolbar shows "recorder ready" and "stream open" (the shell started the recorder).'
        Write-Host "  2. Library > Open footage... > $footage, profile generic. Candidates appear as it is analysed at 8x."
        Write-Host '  3. Select a candidate: its preview loops. Rate with 1-5 (0 clears, Up/Down moves).'
        Write-Host '  4. Timeline: the session''s events and candidates, coloured by stars.'
        Write-Host '  5. Open viewer: the official-player viewer and its calibration panel (any YouTube or Twitch link; nothing about the video is stored).'
        Write-Host 'Close the window to finish.'
        Write-Command 'npm' @('start')
        & npm start
    }
    finally { Pop-Location }
}

function Show-ShellSmoke {
    $ErrorActionPreference = 'Continue'  # native tools write progress to stderr; PS 5.1 would turn it into errors
    Initialize-Build
    $app = Join-Path $Repo 'app'
    $footage = Get-Footage
    Push-Location $app
    try {
        if (-not (Test-Path (Join-Path $app 'node_modules'))) { & npm ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' } }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'npm run build failed' }
        $env:LOOKBACK_USER_DATA = Get-OutDir 'shell-userdata'
        Write-Step 'Shell smoke: start-up, then the whole Open footage > rate > preview flow, unattended'
        Write-Command 'npx' @('electron', '.', '--smoke', "--smoke-footage=$footage", '--smoke-profile=generic')
        & npx electron . --smoke "--smoke-footage=$footage" --smoke-profile=generic
        $code = $LASTEXITCODE
        Write-Step 'Viewer smoke: viewer capture into /ingest on a synthetic player'
        & npx electron . --smoke-viewer
        if ($code -eq 0) { $code = $LASTEXITCODE }
    }
    finally {
        Remove-Item Env:\LOOKBACK_USER_DATA -ErrorAction SilentlyContinue
        Pop-Location
    }
    Write-Look @('Each run prints one LOOKBACK_SMOKE ok|fail {json} line: recorder, stream, ingest and panel status, candidates, the rating and the preview.')
    if ($code -ne 0) { exit $code }
}

function Show-NotYet([string] $What, [string] $Why) {
    Write-Host "$What cannot be demoed yet." -ForegroundColor Yellow
    Write-Host $Why
}

# ------------------------------------------------------------------ catalogue

$Demos = [ordered]@{
    'recorder'      = @{ Run = { Show-Recorder };      Kind = 'auto';        What = 'Recorder handshake (LOOKBACK_READY) and the live event stream on its WebSocket, fed by the testbench' }
    'report'        = @{ Run = { Show-Report };        Kind = 'auto';        What = 'Footage signals on generated footage and the HTML timeline report' }
    'profiles'      = @{ Run = { Show-Profiles };      Kind = 'auto';        What = 'The profile tree, one profile''s HUD regions and rules, and its calibration overlay in a report' }
    'browse'        = @{ Run = { Show-Browse };        Kind = 'auto';        What = 'The dataset and profile browser page' }
    'profiler'      = @{ Run = { Show-Profiler };      Kind = 'interactive'; What = 'The live profiler: events fire in a browser page as the clip plays (-Seconds, default 90)' }
    'detect'        = @{ Run = { Show-Detect };        Kind = 'auto';        What = 'Game detection (lookback-detect) on a stand-in game window: DETECTED and EXITED' }
    'clipper'       = @{ Run = { Show-Clipper };       Kind = 'auto';        What = 'Clipper v1 candidates and QA (two pass, one fails on black/frozen video)' }
    'capture'       = @{ Run = { Show-Capture };       Kind = 'auto';        What = 'Live screen capture and analysis (lookback-capture) of a test-pattern window' }
    'encode'        = @{ Run = { Show-Encode };        Kind = 'auto';        What = 'Live hardware encode and a saved clip from the replay ring (--encode), checked with ffprobe' }
    'audio'         = @{ Run = { Show-Audio };         Kind = 'auto';        What = 'Live audio (lookback-capture audio): loopback + AAC clip; -Mic adds the microphone' }
    'probe'         = @{ Run = { Show-GovernorProbe }; Kind = 'auto';        What = 'Governor probe: machine profile and the recording plan' }
    'watchdog'      = @{ Run = { Show-Watchdog };      Kind = 'auto';        What = 'Encode watchdog: fault-injection tests, then a live encode with --stall-ms/--isolate/--no-blocklist' }
    'rate'          = @{ Run = { Show-Rate };          Kind = 'interactive'; What = 'M4 ratings in the terminal (testbench rate) into the showcase folder' }
    'train'         = @{ Run = { Show-Train };         Kind = 'auto';        What = 'M4 training on synthetic ratings (ml/): metrics and an ONNX model' }
    'scorer'        = @{ Run = { Show-Scorer };        Kind = 'auto';        What = 'The C# ONNX scorer: Python parity and the heuristic fallback' }
    'shell'         = @{ Run = { Show-Shell };         Kind = 'interactive'; What = 'The Electron shell: library, rating with previews, Open footage, timeline, official-player viewer' }
    'shell-smoke'   = @{ Run = { Show-ShellSmoke };    Kind = 'auto';        What = 'The shell''s unattended smoke runs (start-up, Open footage > rate > preview, viewer ingest)' }
    'live-recorder' = @{ Run = { Show-NotYet 'The recorder hosting live capture' 'The recorder does not host capture, detection or the clipper yet; that wiring is in progress. Today live capture runs in the lookback-capture bench (capture, encode, audio demos) and the recorder hosts the event stream (recorder demo).' }; Kind = 'not yet'; What = 'The recorder running detection, capture and the clipper on a real game' }
    'present-hook'  = @{ Run = { Show-NotYet 'The present hook' 'The D3D11/D3D12 present-hook backend is in an open pull request (Joshmh7128/Lookback#24) and not on main. Profiles for anti-cheat titles never allow it.' }; Kind = 'not yet'; What = 'Present-hook capture backend (open PR #24)' }
}

function Show-List {
    Write-Host 'Lookback showcase. Usage: ./scripts/showcase.ps1 <demo> [-Seconds n] [-GameProfile id] [-Monitor n] [-Mic] [-NoBuild] [-NoOpen] [-Out dir]'
    Write-Host "Output goes to $Out. Walkthrough: docs/wiki/Showcase.md"
    Write-Host ''
    foreach ($k in $Demos.Keys) {
        $d = $Demos[$k]
        $kind = $d.Kind
        $color = 'Gray'
        if ($kind -eq 'not yet') { $color = 'DarkYellow' }
        Write-Host ("  {0,-14} {1,-12} {2}" -f $k, $kind, $d.What) -ForegroundColor $color
    }
    Write-Host ''
    Write-Host '  all            runs every "auto" demo in order (opens nothing with -NoOpen)'
}

# ------------------------------------------------------------------ main

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$name = $Demo.ToLowerInvariant()
if ($name -eq 'list') { Show-List; return }
if ($name -eq 'build') { Initialize-Build; return }
if ($name -eq 'all') {
    $failed = @()
    foreach ($k in $Demos.Keys) {
        if ($Demos[$k].Kind -ne 'auto') { continue }
        Write-Host ''
        Write-Host "########## $k" -ForegroundColor Magenta
        try { & $Demos[$k].Run }
        catch { Write-Host "$k failed: $_" -ForegroundColor Red; $failed += $k }
    }
    Write-Host ''
    if ($failed.Count -gt 0) { Write-Host "failed: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
    Write-Host 'all demos ran' -ForegroundColor Green
    return
}
if (-not $Demos.Contains($name)) {
    Write-Host "Unknown demo '$Demo'." -ForegroundColor Red
    Show-List
    exit 64
}
& $Demos[$name].Run
