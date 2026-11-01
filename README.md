# Video Cutter

A small, fast Windows app for cutting a piece out of a video, keeping the source quality.
`VideoCutter.exe` is about 75 KB. It runs on the .NET Framework that comes with Windows 10/11 (nothing to install) and uses FFmpeg for the actual cutting.

## Use it

1. Double-click **VideoCutter.exe**.
2. Click **Open video** or drag a video onto the window.
3. Pick the part you want:
   - Drag on the timeline to scrub. Drag the **green** handle to set the start and the **red** handle to set the end.
   - Or move the playhead and click **Set to playhead** (keys **I** and **O**).
   - Or type exact times in the START / END boxes (`1:23.5` or `00:01:23.500`) and press Enter.
4. Choose a mode:
   - **Fast lossless** (default): copies the video and audio as they are. It's instant and keeps 100% of the original quality. A copy cut can only *start* on a keyframe, so the start jumps back to the nearest keyframe before your START (the app tells you exactly where). The yellow ticks on the timeline are keyframes; **Ctrl+←/→** jumps between them. On files with B-frames, the end can also run 1–4 frames long or skip a frame, and the file may start a few hundredths of a second late. So copy cuts are **not for loops or for joining clips**. If a loop preview mode is on when you export with this mode, the app warns you and offers a one-cycle loop export instead.
   - **Frame-accurate**: starts and ends on exactly the frames you chose. The video is re-encoded with the same codec, resolution, frame rate, pixel format, profile and colour info, at about the source bitrate (×1.1). It uses the NVIDIA GPU (h264_nvenc / hevc_nvenc) and falls back to the CPU automatically if the GPU can't do it, e.g. for 10-bit H.264. Audio tracks are re-encoded in the same codec and bitrate, so the file starts at exactly 0 and the audio is exactly as long as the video. That way two cuts join without a gap; copied audio can only be cut in ~21 ms blocks. Subtitle tracks are copied.
   - **Looped to [length]**: repeats the selection until the length you type, e.g. `10:00` or `1:00:00`. Use it for a background loop for a music video. The loop style follows the **LOOP PREVIEW** setting, so what you preview is what you get:
     - **Off** or **Loop**: plain loop. END jumps straight back to START, frame-accurate.
     - **Crossfade loop**: every join blends, picture and sound, over the fade length. The video starts at START + fade, because the first fade is already blended into the end of each cycle.

     **1 cycle** sets the length to exactly one loop cycle: the selection length for a plain loop, or the selection minus the fade for a crossfade. That makes a file that repeats seamlessly when you loop it, for example with FFmpeg's `-stream_loop -1`, OBS "loop", or an editor timeline. **Whole cycles** (on by default) rounds any length you type to whole cycles, so a long export also repeats seamlessly.

     How it works: one cycle is encoded once, matching the source (GPU when possible), then repeated without re-encoding. That makes it fast; a 1-hour loop took about 13 seconds in testing. Audio is encoded once as one continuous track in the source's codec and bitrate, so the joins have no gaps and sync doesn't drift. Subtitles are left out.
5. Click **Export cut**. It asks where to save (default: `name_cut.mp4` next to the source) and warns before replacing an existing file. The app writes to a temp file first, so a failed or cancelled export never damages an existing file.

All audio tracks and subtitles are kept. The output uses the same container as the source (mp4 → mp4, mkv → mkv, …).

### Loop preview

Check how your selection looks when it repeats, for example a background loop for a music video. **LOOP PREVIEW** has three choices:
- **Off**: *Play selection* stops at END.
- **Loop**: END jumps straight back to START, with nothing added. Use this to see whether the cut loops seamlessly on its own.
- **Crossfade loop**: the last part of the selection fades into the start, both picture and sound. Set the fade length in seconds next to it. It's never more than half the selection. The green ramp on the timeline shows the fade zone.

Press **Play selection** (P) and it keeps looping until you pause. **L** switches between the loop modes.

**Play seam** (S) is the exact check. It renders a short clip with FFmpeg, the same way the loop export does, which takes about 2 seconds the first time and is instant on replays. The clip runs 2 seconds before END, then the jump or crossfade, then 2 seconds after START, and the app plays it straight through. Every frame is exactly what an exported loop will show. It repeats until you pause (Space). Between repeats it holds the last frame for half a second, so the restart can't be mistaken for the loop point. A badge says "Seam preview - exact frames". It also works for files Windows can't play. The clips are kept in `preview-cache` next to the exe and deleted when the app closes.

The continuous loop from *Play selection* switches between two of Windows' players. It can hold or skip a frame or two at the loop point, so use **Play seam** to judge the join. If the seam still shows a jump there, it's in the video itself: the last and first frames don't match. Use **Crossfade loop** to hide it.

To save the loop as a video, choose the **Looped to** export mode (see above). The preview uses a second player that waits at START, so the jump is instant. That player is only loaded while a loop mode is on. Loop preview isn't available for files that are in still-frame preview.

### Keys

| Key | Action |
|---|---|
| Space | Play / pause |
| ← / → | 1 frame back / forward (Shift = 1 second, Ctrl = keyframe) |
| Mouse wheel on timeline | 1 frame back / forward |
| I / O | Set start / end at the playhead |
| P | Play only the selection (loops if a loop mode is on) |
| L | Loop preview: Off → Loop → Crossfade loop |
| S | Play the loop seam |
| Home / End | Go to the beginning / end |
| M | Mute |
| Ctrl+O / Ctrl+E | Open / Export |

### Preview

The preview uses Windows' own video player engine, which is hardware-decoded and light on the CPU. If Windows can't play a format (for example 10-bit HEVC in MKV), the app switches to **still-frame preview** on its own: scrubbing still shows the exact frame (via FFmpeg) but there is no playback. Export works the same either way.

## Needs

- Windows 10/11 (.NET Framework 4.8 is built in).
- `ffmpeg.exe` and `ffprobe.exe` in PATH, or in the same folder as `VideoCutter.exe`.

## Rebuild

Double-click **build.bat**. It uses the C# compiler that ships with Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so no SDK or downloads are needed. Building takes about 2 seconds.

Source code:
- `src\App.cs`: window logic, keyboard shortcuts, and export flow. It also contains the test mode (see below).
- `src\Engine.cs`: ffprobe reading, keyframe scan, and the FFmpeg commands for both modes.
- `src\Timeline.cs`: the custom-drawn timeline.
- `src\MainWindow.xaml`: the layout and dark theme.
- `src\make-icon.ps1`: redraws `src\app.ico`. You only need it if you want a different icon.

## Testing

The exe has a command-line test mode that uses the same export code as the button:

```bash
VideoCutter.exe --export "in.mp4" "out.mp4" 5.1 12.5 exact
```

The last word can be `copy`, `exact`, `loop=1:00:00` (plain loop to 1 hour) or `loopxf=1:00:00,1.0` (crossfade loop with a 1 s fade).

First create the test clips. They're synthetic, made by FFmpeg, and not stored in the repo:

```bash
pwsh -File test\make-test-clips.ps1
```

`test\verify.ps1` cuts every clip in `test\` in both modes and checks the results with ffprobe/ffmpeg. It checks that:
- codec, profile, resolution, fps, pix_fmt and audio/subtitle tracks match the source,
- the frame count is right,
- the first frame is the right one (SSIM),
- audio stays in sync (the sync clips flash white and beep once per second).

Run it with PowerShell 7:

```bash
pwsh -File test\verify.ps1
```

`test\verify-loop.ps1` checks loop exports (plain and crossfade). It checks:
- the spec matches the source,
- the frame count and audio length are exact,
- every join lands on the right frames,
- the crossfade really is a 50/50 blend,
- A/V sync doesn't drift across the loops.

```bash
pwsh -File test\verify-loop.ps1
```
