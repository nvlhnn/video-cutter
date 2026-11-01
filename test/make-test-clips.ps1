# Generates the synthetic test clips used by verify.ps1 and verify-loop.ps1 (needs ffmpeg with libx264/libx265/libopus).
# Run with PowerShell 7:  pwsh -File test\make-test-clips.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
function Run([string[]]$a) { & ffmpeg -v error -y @a; if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed: $a" } }

# 1. H.264 High, 29.97 fps, keyframe every 2 s, B-frames, 2 audio tracks (AAC mono + AC-3 5.1), Unicode + spaces in the name
Run @('-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=30000/1001:duration=30',
      '-f', 'lavfi', '-i', 'sine=f=440:d=30', '-f', 'lavfi', '-i', 'sine=f=660:d=30',
      '-filter_complex', '[2:a]aformat=channel_layouts=5.1[a2]',
      '-map', '0:v', '-map', '1:a', '-map', '[a2]',
      '-c:v', 'libx264', '-profile:v', 'high', '-pix_fmt', 'yuv420p', '-b:v', '6M', '-g', '60', '-bf', '2',
      '-c:a:0', 'aac', '-b:a:0', '192k', '-c:a:1', 'ac3', '-b:a:1', '384k',
      '-metadata:s:a:0', 'language=eng', '-metadata:s:a:1', 'language=ind', 'clip test ünï 日本.mp4')

# 2. HEVC Main 10 in MKV, 25 fps, Opus audio, SRT subtitle
"1`n00:00:01,000 --> 00:00:20,000`nHello subtitle`n" | Set-Content -Encoding utf8 sub.srt
Run @('-i', 'clip test ünï 日本.mp4', '-i', 'sub.srt', '-map', '0:v', '-map', '0:a:0', '-map', '1',
      '-c:v', 'libx265', '-preset', 'fast', '-pix_fmt', 'yuv420p10le', '-r', '25', '-b:v', '3M', '-x265-params', 'keyint=50:log-level=error',
      '-c:a', 'libopus', '-b:a', '128k', '-c:s', 'srt', 'hevc 10bit test.mkv')
Remove-Item sub.srt

# 3. Only ONE keyframe in the whole file (24 fps, B-frames, no audio) - the hardest case for copy cuts and seeking
Run @('-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=24:duration=9', '-c:v', 'libx264', '-profile:v', 'main',
      '-pix_fmt', 'yuv420p', '-b:v', '2M', '-g', '1000', '-bf', '2', 'one keyframe 24fps.mp4')

# 4. A/V sync clips: white flash + 1 kHz beep every 1.001 s, two audio tracks
Run @('-f', 'lavfi', '-i', 'color=c=black:s=1280x720:r=30000/1001:d=20',
      '-f', 'lavfi', '-i', "aevalsrc='if(lt(mod(t\,1.001)\,0.05)\,0.8*sin(2*PI*1000*t)\,0)':s=48000:d=20",
      '-vf', "drawbox=w=iw:h=ih:c=white:t=fill:enable='eq(mod(n\,30)\,0)'",
      '-map', '0', '-map', '1', '-map', '1', '-c:v', 'libx264', '-bf', '2', '-g', '60', '-b:v', '4M', '-pix_fmt', 'yuv420p',
      '-c:a:0', 'aac', '-b:a:0', '160k', '-c:a:1', 'ac3', 'sync test.mp4')
Run @('-i', 'sync test.mp4', '-map', '0', '-c:v', 'copy', '-c:a:0', 'libopus', '-c:a:1', 'copy', 'sync test.mkv')

# 5. 10-bit H.264 (most GPUs can't encode it: tests the automatic CPU fallback)
Run @('-i', 'one keyframe 24fps.mp4', '-c:v', 'libx264', '-pix_fmt', 'yuv420p10le', '-profile:v', 'high10', '-b:v', '3M', 'h264 10bit.mp4')

Get-ChildItem *.mp4, *.mkv | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
