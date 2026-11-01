# Cuts test clips with VideoCutter.exe in both modes and checks the results with ffprobe/ffmpeg.
# Run with PowerShell 7:  pwsh -File test\verify.ps1
param([string]$Exe = (Join-Path $PSScriptRoot '..\VideoCutter.exe'))
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
function P([string]$s) { [double]::Parse($s, $inv) }
function F([double]$v, [string]$f = '0.000') { $v.ToString($f, $inv) }

function Probe($f) { & ffprobe -v error -count_frames -print_format json -show_format -show_streams $f 2>$null | ConvertFrom-Json }
function Sig($j) {
    $v = $j.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
    $a = ($j.streams | Where-Object codec_type -eq 'audio' | ForEach-Object { "$($_.codec_name)/$($_.channels)ch/$($_.sample_rate)" }) -join ' + '
    $s = @($j.streams | Where-Object codec_type -eq 'subtitle').Count
    "$($v.codec_name) $($v.profile) $($v.width)x$($v.height) $($v.r_frame_rate) $($v.pix_fmt) | audio: $(if ($a) { $a } else { 'none' }) | subs: $s"
}
function Rate($r) { $p = $r.Split('/'); (P $p[0]) / (P $p[1]) }

# SSIM of output frame 0 against source frames start-1, start, start+1 (proves which frame the cut starts on)
function FirstFrameMatch($src, $out, $t, $fps) {
    foreach ($d in -1, 0, 1) {
        $ts = F ($t + $d / $fps) '0.######'
        $o = & ffmpeg -hide_banner -nostdin -i $out -ss $ts -i $src -filter_complex "[0:v]trim=end_frame=1,setpts=PTS-STARTPTS[a];[1:v]trim=end_frame=1,setpts=PTS-STARTPTS[b];[a][b]ssim" -f null - 2>&1 | Select-String 'All:([0-9.]+)'
        P $o.Matches[0].Groups[1].Value
    }
}

# A/V sync: time of first white flash minus time of first beep (per audio track), on the decoded timeline
function SyncOffsets($f) {
    # take the 2nd flash (skips a flash/beep that may be cut in half at the very start), then the nearest beep onset
    $flashes = @()
    $lines = & ffmpeg -hide_banner -nostdin -i $f -map 0:v:0 -vf "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-" -f null - 2>$null
    $t = $null
    foreach ($l in $lines) {
        if ($l -match 'pts_time:([-0-9.]+)') { $t = P $Matches[1] }
        elseif ($l -match 'YAVG=([0-9.]+)' -and (P $Matches[1]) -gt 128) { $flashes += $t; if ($flashes.Count -ge 2) { break } }
    }
    $flash = $flashes[1]
    $n = @((Probe $f).streams | Where-Object codec_type -eq 'audio').Count
    for ($i = 0; $i -lt $n; $i++) {
        $beeps = & ffmpeg -hide_banner -nostdin -i $f -map "0:a:$i" -af "silencedetect=n=-30dB:d=0.1" -f null - 2>&1 |
            Select-String 'silence_end: ([-0-9.]+)' | ForEach-Object { P $_.Matches[0].Groups[1].Value }
        $beep = $beeps | Sort-Object { [math]::Abs($_ - $flash) } | Select-Object -First 1
        $flash - $beep
    }
}

$cases = @(
    @{ src = 'clip test ünï 日本.mp4'; start = 153; end = 375 },
    @{ src = 'hevc 10bit test.mkv'; start = 137; end = 410 },
    @{ src = 'one keyframe 24fps.mp4'; start = 50; end = 170 },
    @{ src = 'sync test.mp4'; start = 100; end = 400; sync = $true },
    @{ src = 'sync test.mkv'; start = 100; end = 400; sync = $true }
)
$fail = 0
foreach ($c in $cases) {
    $src = if ([IO.Path]::IsPathRooted($c.src)) { $c.src } else { Join-Path $PSScriptRoot $c.src }
    $sj = Probe $src
    $sv = $sj.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
    $fps = Rate $sv.r_frame_rate
    $t0 = $c.start / $fps; $t1 = $c.end / $fps
    # B-frame reorder delay in frames (pts - dts of the first packet)
    $pk = (& ffprobe -v error -select_streams v:0 -read_intervals '%+#1' -show_entries packet=pts_time,dts_time -of csv=p=0 $src 2>$null | Select-Object -First 1).Split(',')
    $delay = if ($pk[1] -match '^[-0-9.]+$') { [math]::Round(((P $pk[0]) - (P $pk[1])) * $fps) } else { [int]$sv.has_b_frames }
    Write-Host "`n=== $([IO.Path]::GetFileName($src))   cut $(F $t0) -> $(F $t1)  ($($c.end - $c.start) frames)"
    Write-Host ("source : " + (Sig $sj) + " | " + (F ((P $sj.format.bit_rate) / 1e6) '0.00') + " Mb/s total")
    $srcSync = if ($c.sync) { @(SyncOffsets $src) } else { $null }
    foreach ($mode in 'copy', 'exact') {
        $ext = [IO.Path]::GetExtension($src)
        $out = Join-Path $PSScriptRoot ("out " + $mode + " " + [IO.Path]::GetFileNameWithoutExtension($src) + $ext)
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $Exe -ArgumentList '--export', "`"$src`"", "`"$out`"", (F $t0 '0.######'), (F $t1 '0.######'), $mode -Wait -PassThru -NoNewWindow
        $sw.Stop()
        if ($p.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $out)) { Write-Host "[$mode] EXPORT FAILED" -ForegroundColor Red; $fail++; continue }
        $oj = Probe $out
        $ov = $oj.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
        $same = (Sig $oj) -eq (Sig $sj)
        $frames = [int]$ov.nb_read_frames
        if ($mode -eq 'exact') { $want = $c.end - $c.start; $tStart = $t0; $tol = 0 }
        else {
            $keys = & ffprobe -v error -select_streams v:0 -show_entries packet=pts_time,flags -of csv=p=0 $src 2>$null |
                Where-Object { $_ -match 'K' } | ForEach-Object { (P $_.Split(',')[0]) - (P $sj.format.start_time) }
            $tStart = ($keys | Where-Object { $_ -le $t0 + 0.0005 } | Measure-Object -Maximum).Maximum
            $want = [math]::Round(($t1 - $tStart) * $fps)
            $tol = $delay + 1   # copy cuts end in decode order: B-frame files can run a few frames long
        }
        $m = @(FirstFrameMatch $src $out $tStart $fps)
        # target frame must match best (ties happen on identical frames, e.g. the black sync clip)
        $exactFrame = ($m[1] -ge $m[0]) -and ($m[1] -ge $m[2])
        $ok = $same -and ([math]::Abs($frames - $want) -le $tol) -and $exactFrame
        $syncTxt = ''
        if ($c.sync) {
            $os = @(SyncOffsets $out)
            for ($i = 0; $i -lt $os.Count; $i++) {
                $drift = $os[$i] - $srcSync[$i]
                if ([math]::Abs($drift) -gt 0.5 / $fps) { $ok = $false }
                $syncTxt += " a$i drift $(F ($drift*1000) '0') ms;"
            }
        }
        if (-not $ok) { $fail++ }
        Write-Host ("[$mode] " + $(if ($ok) { 'PASS' } else { 'FAIL' }) + "  ($(F $sw.Elapsed.TotalSeconds '0.0') s)")
        Write-Host ("   output : " + (Sig $oj) + " | " + (F ((P $oj.format.bit_rate) / 1e6) '0.00') + " Mb/s total")
        Write-Host ("   same spec as source: $same")
        Write-Host ("   starts at $(F $tStart) s, $frames frames (expected $want" + $(if ($tol) { " ±$tol" }) + "), file length $(F (P $oj.format.duration)) s")
        Write-Host ("   first-frame SSIM [prev, target, next] = " + (($m | ForEach-Object { F $_ '0.0000' }) -join ', '))
        if ($c.sync) { Write-Host ("   A/V sync vs source:" + $syncTxt) }
    }
}
Write-Host "`nFailures: $fail"
exit $fail
