# Checks loop exports (plain + crossfade): spec, length, frames at the loop joins, and A/V drift across loops.
# Run with PowerShell 7:  pwsh -File test\verify-loop.ps1
param([string]$Exe = (Join-Path $PSScriptRoot '..\VideoCutter.exe'))
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
function P([string]$s) { [double]::Parse($s, $inv) }
function F([double]$v, [string]$f = '0.000') { $v.ToString($f, $inv) }
function Probe($f) { & ffprobe -v error -count_frames -print_format json -show_format -show_streams $f 2>$null | ConvertFrom-Json }
function Sig($j) {
    $v = $j.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
    $a = ($j.streams | Where-Object codec_type -eq 'audio' | ForEach-Object { "$($_.codec_name)/$($_.channels)ch/$($_.sample_rate)" }) -join ' + '
    "$($v.codec_name) $($v.profile) $($v.width)x$($v.height) $($v.r_frame_rate) $($v.pix_fmt) | audio: $a"
}
function Rate($r) { $p = $r.Split('/'); (P $p[0]) / (P $p[1]) }

# SSIM between output frame #k and source frame #m
function FrameSsim($out, $k, $src, $m) {
    $o = & ffmpeg -hide_banner -nostdin -i $out -i $src -filter_complex "[0:v]select='eq(n\,$k)',setpts=PTS-STARTPTS[a];[1:v]select='eq(n\,$m)',setpts=PTS-STARTPTS[b];[a][b]ssim" -frames:v 1 -f null - 2>&1 | Select-String 'All:([0-9.]+)'
    P $o.Matches[0].Groups[1].Value
}

# For every full white flash: flash time minus nearest beep onset (per audio track)
function SyncDrift($f, $track) {
    $flashes = @(); $t = $null
    foreach ($l in (& ffmpeg -hide_banner -nostdin -i $f -map 0:v:0 -vf "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-" -f null - 2>$null)) {
        if ($l -match 'pts_time:([-0-9.]+)') { $t = P $Matches[1] }
        elseif ($l -match 'YAVG=([0-9.]+)' -and (P $Matches[1]) -gt 200) { $flashes += $t }
    }
    $beeps = @(& ffmpeg -hide_banner -nostdin -i $f -map "0:a:$track" -af "silencedetect=n=-30dB:d=0.1" -f null - 2>&1 |
        Select-String 'silence_end: ([-0-9.]+)' | ForEach-Object { P $_.Matches[0].Groups[1].Value })
    $offs = foreach ($fl in $flashes) { if ($fl -lt 0.3) { continue }; $b = $beeps | Sort-Object { [math]::Abs($_ - $fl) } | Select-Object -First 1; $fl - $b }
    return , @($offs)
}

$fail = 0
function Check($name, [bool]$ok, $detail) {
    if (-not $ok) { $script:fail++ }
    Write-Host ("   " + $(if ($ok) { 'ok  ' } else { 'FAIL' }) + " $name  $detail")
}

$cases = @(
    @{ src = 'clip test ünï 日本.mp4'; a = 153; b = 303; len = 40; fade = 0 },
    @{ src = 'clip test ünï 日本.mp4'; a = 153; b = 303; len = 40; fade = 1.0 },
    @{ src = 'hevc 10bit test.mkv'; a = 137; b = 262; len = 30; fade = 0.6 },
    @{ src = 'sync test.mp4'; a = 100; b = 250; len = 60; fade = 0; sync = $true },
    @{ src = 'sync test.mp4'; a = 100; b = 250; len = 60; fade = 1.0; sync = $true }
)
foreach ($c in $cases) {
    $src = Join-Path $PSScriptRoot $c.src
    $sj = Probe $src
    $sv = $sj.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
    $fps = Rate $sv.r_frame_rate
    $n = $c.b - $c.a
    $nf = [int][math]::Round($c.fade * $fps)
    $cyc = $n - $nf
    $mode = if ($c.fade -gt 0) { "loopxf=$($c.len),$(F $c.fade '0.###')" } else { "loop=$($c.len)" }
    $out = Join-Path $PSScriptRoot ("out " + $mode.Replace('=', ' ').Replace(',', ' ') + " " + [IO.Path]::GetFileNameWithoutExtension($src) + [IO.Path]::GetExtension($src))
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out }
    Write-Host "`n=== $($c.src)  $mode  (selection $n frames, cycle $cyc frames)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Exe -ArgumentList '--export', "`"$src`"", "`"$out`"", (F ($c.a / $fps) '0.######'), (F ($c.b / $fps) '0.######'), $mode -Wait -PassThru -NoNewWindow
    Write-Host "   export took $(F $sw.Elapsed.TotalSeconds '0.0') s"
    if ($p.ExitCode -ne 0) { Check 'export' $false ''; continue }
    $oj = Probe $out
    $ov = $oj.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
    Check 'same spec as source' ((Sig $oj) -eq (Sig $sj)) (Sig $oj)
    $want = [math]::Round($c.len * $fps)
    Check 'frame count' ([math]::Abs([int]$ov.nb_read_frames - $want) -le 1) "$($ov.nb_read_frames) (expected $want)"
    foreach ($as in ($oj.streams | Where-Object codec_type -eq 'audio')) {
        $ad = if ($as.duration) { P $as.duration } else { P $oj.format.duration }
        Check "audio #$($as.index) length" ([math]::Abs($ad - $c.len) -le 0.05) "$(F $ad) s"
    }
    if (-not $c.sync) {
        # first frame of each cycle must be source frame a+nf (plain: a); the frame before it the end of the cycle
        $s1 = FrameSsim $out 0 $src ($c.a + $nf)
        $s2 = FrameSsim $out $cyc $src ($c.a + $nf)
        $s3 = FrameSsim $out (5 * $cyc) $src ($c.a + $nf)
        Check 'cycle start frames (loop 1, 2, 6)' (($s1, $s2, $s3 | Measure-Object -Minimum).Minimum -ge 0.97) "SSIM $(F $s1 '0.000'), $(F $s2 '0.000'), $(F $s3 '0.000')"
        if ($nf -eq 0) {
            $s4 = FrameSsim $out ($cyc - 1) $src ($c.b - 1)
            Check 'frame before the join = last selected frame' ($s4 -ge 0.97) "SSIM $(F $s4 '0.000')"
        } else {
            # middle of the crossfade: a mix, so it must not fully match either the tail or the head frame
            $jm = [int][math]::Floor($nf / 2)                 # frame j of the fade, in the 2nd cycle
            $k = $cyc + ($cyc - $nf) + $jm
            $mt = $c.b - $nf + $jm; $mh = $c.a + $jm
            $tail = FrameSsim $out $k $src $mt
            $head = FrameSsim $out $k $src $mh
            $o = & ffmpeg -hide_banner -nostdin -i $out -i $src -i $src -filter_complex "[1:v]select='eq(n\,$mt)',setpts=PTS-STARTPTS[t];[2:v]select='eq(n\,$mh)',setpts=PTS-STARTPTS[h];[t][h]blend=all_mode=average[m];[0:v]select='eq(n\,$k)',setpts=PTS-STARTPTS[o];[o][m]ssim" -frames:v 1 -f null - 2>&1 | Select-String 'All:([0-9.]+)'
            $mix = P $o.Matches[0].Groups[1].Value
            Check 'mid-crossfade frame is a 50/50 blend of end + start' ($mix -gt $tail -and $mix -gt $head -and $mix -ge 0.97) "SSIM vs 50/50 mix $(F $mix '0.000'), vs end frame $(F $tail '0.000'), vs start frame $(F $head '0.000')"
        }
    } else {
        $nA = @($oj.streams | Where-Object codec_type -eq 'audio').Count
        for ($i = 0; $i -lt $nA; $i++) {
            $d = SyncDrift $out $i
            $spread = ($d | Measure-Object -Maximum).Maximum - ($d | Measure-Object -Minimum).Minimum
            Check "A/V sync track $i over $($d.Count) flashes" ($d.Count -ge 5 -and ($d | ForEach-Object { [math]::Abs($_) } | Measure-Object -Maximum).Maximum -le 0.034) "offset first $(F ($d[0]*1000) '0') ms, last $(F ($d[-1]*1000) '0') ms, spread $(F ($spread*1000) '0') ms"
        }
    }
}
Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'out *' | Remove-Item
Write-Host "`nFailures: $fail"
exit $fail
