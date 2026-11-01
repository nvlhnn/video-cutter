// Video Cutter - FFmpeg engine (probe, keyframes, export).
// C# 5 / .NET Framework 4.x so it builds with the csc.exe that ships with Windows.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VideoCutter
{
    public class StreamInfo
    {
        public int Index;
        public string Type = "";      // video / audio / subtitle / data / attachment
        public string Codec = "";
        public string Profile = "";
        public int Width, Height;
        public string PixFmt = "";
        public string RFrameRate = "", AvgFrameRate = "";
        public long BitRate;
        public string ColorPrimaries = "", ColorTrc = "", ColorSpace = "", ColorRange = "";
        public string CodecTag = "";
        public bool AttachedPic;
        public int Channels;
        public int SampleRate;
        public string Language = "";
    }

    public class MediaInfo
    {
        public string Path;
        public double Duration;
        public double StartTime;
        public long FormatBitRate;
        public long Size;
        public string FormatName = "";
        public List<StreamInfo> Streams = new List<StreamInfo>();
        public StreamInfo Video;      // main video stream (not cover art)
        public int VideoOrdinal;      // position of Video among video streams (for -c:v:N)
        public double Fps;

        public IEnumerable<StreamInfo> Audio { get { return Streams.Where(s => s.Type == "audio"); } }

        /// Video bitrate in bit/s. Uses the stream value, else estimates from container minus audio.
        public long VideoBitRate
        {
            get
            {
                if (Video != null && Video.BitRate > 0) return Video.BitRate;
                long total = FormatBitRate;
                if (total <= 0 && Duration > 0 && Size > 0) total = (long)(Size * 8 / Duration);
                long audio = Audio.Sum(a => a.BitRate > 0 ? a.BitRate : 192000);
                long v = total - audio;
                return v > 100000 ? v : Math.Max(total, 2000000);
            }
        }
    }

    public static class Tools
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string ffmpeg, ffprobe;

        public static string FFmpeg { get { if (ffmpeg == null) ffmpeg = Find("ffmpeg.exe"); return ffmpeg; } }
        public static string FFprobe { get { if (ffprobe == null) ffprobe = Find("ffprobe.exe"); return ffprobe; } }

        /// Looks next to the app first, then in PATH. Chocolatey "shims" are resolved to the real exe
        /// so that cancelling an export really stops ffmpeg.
        static string Find(string exe)
        {
            var dirs = new List<string>();
            dirs.Add(AppDomain.CurrentDomain.BaseDirectory);
            dirs.Add(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg"));
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            dirs.AddRange(path.Split(';').Where(d => d.Trim().Length > 0));
            foreach (var d in dirs)
            {
                try
                {
                    string p = System.IO.Path.Combine(d.Trim().Trim('"'), exe);
                    if (!File.Exists(p)) continue;
                    if (p.IndexOf("chocolatey\\bin", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string lib = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(p)), "lib");
                        if (Directory.Exists(lib))
                            foreach (var pkg in Directory.GetDirectories(lib, "ffmpeg*"))
                            {
                                var real = Directory.GetFiles(pkg, exe, SearchOption.AllDirectories).FirstOrDefault();
                                if (real != null) return real;
                            }
                    }
                    return p;
                }
                catch { }
            }
            return null;
        }

        public static string Q(string s) { return "\"" + s + "\""; }
        public static string N(double v) { return v.ToString("0.######", Inv); }

        public static string Fmt(double t)
        {
            if (double.IsNaN(t) || t < 0) t = 0;
            long ms = (long)Math.Round(t * 1000);
            return string.Format(Inv, "{0:00}:{1:00}:{2:00}.{3:000}", ms / 3600000, ms / 60000 % 60, ms / 1000 % 60, ms % 1000);
        }

        /// Accepts "83.5", "1:23.5", "00:01:23.500" (comma also allowed as decimal mark).
        public static bool TryParseTime(string s, out double t)
        {
            t = 0;
            if (s == null) return false;
            s = s.Trim().Replace(',', '.');
            if (s.Length == 0) return false;
            var parts = s.Split(':');
            if (parts.Length > 3) return false;
            double total = 0;
            foreach (var p in parts)
            {
                double v;
                if (!double.TryParse(p.Length == 0 ? "0" : p, NumberStyles.Float, Inv, out v) || v < 0) return false;
                total = total * 60 + v;
            }
            t = total;
            return true;
        }

        public static double ParseRate(string r)
        {
            if (string.IsNullOrEmpty(r)) return 0;
            var p = r.Split('/');
            double a, b = 1;
            if (!double.TryParse(p[0], NumberStyles.Float, Inv, out a)) return 0;
            if (p.Length > 1 && (!double.TryParse(p[1], NumberStyles.Float, Inv, out b) || b == 0)) return 0;
            return a / b;
        }

        public static ProcessStartInfo Psi(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            return psi;
        }

        /// Runs a tool and returns stdout as text. Throws on failure.
        public static string RunText(string exe, string args)
        {
            using (var p = Process.Start(Psi(exe, args)))
            {
                var err = p.StandardError.ReadToEndAsync();
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception(err.Result.Trim());
                return o;
            }
        }

        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.00", Inv) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", Inv) + " MB";
            return (bytes / 1024.0).ToString("0", Inv) + " KB";
        }

        public static string Rate(long bps)
        {
            return bps >= 1000000 ? (bps / 1e6).ToString("0.0#", Inv) + " Mb/s" : (bps / 1e3).ToString("0", Inv) + " kb/s";
        }
    }

    public static class Probe
    {
        static string S(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, Tools.Inv) : "";
        }
        static long L(Dictionary<string, object> d, string k)
        {
            long v; return long.TryParse(S(d, k), NumberStyles.Integer, Tools.Inv, out v) ? v : 0;
        }
        static double D(Dictionary<string, object> d, string k)
        {
            double v; return double.TryParse(S(d, k), NumberStyles.Float, Tools.Inv, out v) ? v : 0;
        }
        static Dictionary<string, object> Sub(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }

        public static MediaInfo Run(string path)
        {
            if (Tools.FFprobe == null) throw new Exception("ffprobe.exe was not found. Put FFmpeg in PATH or next to VideoCutter.exe.");
            string json = Tools.RunText(Tools.FFprobe, "-v error -print_format json -show_format -show_streams " + Tools.Q(path));
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            var root = js.Deserialize<Dictionary<string, object>>(json);
            var info = new MediaInfo();
            info.Path = path;
            var fmt = Sub(root, "format");
            info.Duration = D(fmt, "duration");
            info.StartTime = D(fmt, "start_time");
            info.FormatBitRate = L(fmt, "bit_rate");
            info.Size = L(fmt, "size");
            info.FormatName = S(fmt, "format_long_name");

            object arr;
            if (root.TryGetValue("streams", out arr) && arr is System.Collections.IEnumerable)
            {
                foreach (var o in (System.Collections.IEnumerable)arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var s = new StreamInfo();
                    s.Index = (int)L(d, "index");
                    s.Type = S(d, "codec_type");
                    s.Codec = S(d, "codec_name");
                    s.Profile = S(d, "profile");
                    s.Width = (int)L(d, "width");
                    s.Height = (int)L(d, "height");
                    s.PixFmt = S(d, "pix_fmt");
                    s.RFrameRate = S(d, "r_frame_rate");
                    s.AvgFrameRate = S(d, "avg_frame_rate");
                    s.BitRate = L(d, "bit_rate");
                    s.ColorPrimaries = S(d, "color_primaries");
                    s.ColorTrc = S(d, "color_transfer");
                    s.ColorSpace = S(d, "color_space");
                    s.ColorRange = S(d, "color_range");
                    s.CodecTag = S(d, "codec_tag_string");
                    s.Channels = (int)L(d, "channels");
                    s.SampleRate = (int)L(d, "sample_rate");
                    var tags = Sub(d, "tags");
                    s.Language = S(tags, "language");
                    if (s.BitRate <= 0 && tags != null)
                    {   // Matroska stores bitrate as a tag
                        foreach (var kv in tags)
                            if (kv.Key.StartsWith("BPS", StringComparison.OrdinalIgnoreCase))
                            { long b; if (long.TryParse(Convert.ToString(kv.Value, Tools.Inv), out b)) { s.BitRate = b; break; } }
                    }
                    s.AttachedPic = L(Sub(d, "disposition"), "attached_pic") == 1;
                    info.Streams.Add(s);
                }
            }
            int ord = 0;
            foreach (var s in info.Streams.Where(x => x.Type == "video"))
            {
                if (!s.AttachedPic && info.Video == null) { info.Video = s; info.VideoOrdinal = ord; }
                ord++;
            }
            if (info.Video == null) throw new Exception("This file has no video stream.");
            double r = Tools.ParseRate(info.Video.RFrameRate), a = Tools.ParseRate(info.Video.AvgFrameRate);
            info.Fps = (r > 0 && r <= 240) ? r : (a > 0 ? a : 25);
            if (info.Duration <= 0) throw new Exception("Could not read the video length.");
            return info;
        }

        /// Keyframe times (seconds from file start) from packet flags only - no decoding, so it's fast.
        public static double[] Keyframes(MediaInfo info, CancellationToken ct)
        {
            var list = new List<double>();
            var psi = Tools.Psi(Tools.FFprobe, "-v error -select_streams " + info.Video.Index +
                " -show_entries packet=pts_time,dts_time,flags -of csv=p=0 " + Tools.Q(info.Path));
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += delegate { };
                p.BeginErrorReadLine();
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } return null; }
                    if (line.IndexOf('K') < 0) continue;
                    foreach (var f in line.Split(','))
                    {
                        double t;
                        if (double.TryParse(f, NumberStyles.Float, Tools.Inv, out t)) { list.Add(t - info.StartTime); break; }
                    }
                }
                p.WaitForExit();
            }
            list.Sort();
            return list.ToArray();
        }

        /// One decoded frame as BMP bytes (used when Windows can't play the file).
        public static byte[] Frame(string path, double t, int maxWidth)
        {
            string args = "-v error -nostdin -ss " + Tools.N(Math.Max(0, t)) + " -i " + Tools.Q(path) +
                " -frames:v 1 -an -sn -dn -vf \"scale='min(" + maxWidth + ",iw)':-2\" -f image2pipe -c:v bmp -";
            using (var p = Process.Start(Tools.Psi(Tools.FFmpeg, args)))
            {
                p.ErrorDataReceived += delegate { };
                p.BeginErrorReadLine();
                var ms = new MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(ms);
                p.WaitForExit();
                return ms.Length > 0 ? ms.ToArray() : null;
            }
        }
    }

    public class ExportJob
    {
        public MediaInfo Info;
        public string Output;
        public double In, Out;
        public bool Exact;
        public double[] Keyframes;    // needed for copy mode

        /// Copy mode starts on the keyframe at or before In.
        public double CopyStart
        {
            get
            {
                double k = 0;
                if (Keyframes != null)
                    foreach (var t in Keyframes) { if (t <= In + 0.0005) k = Math.Max(0, t); else break; }
                return k;
            }
        }

        double NextKeyAfter(double k)
        {
            if (Keyframes != null) foreach (var t in Keyframes) if (t > k + 0.0005) return t;
            return Info.Duration;
        }

        public double OutputLength { get { return Loop ? LoopLength : Exact ? (Out - In) / Speed : Out - CopyStart; } }

        const string Streams = " -map 0 -dn -ignore_unknown -map_metadata 0";

        public string CopyArgs(string output)
        {
            double k = CopyStart;
            // FFmpeg seeks up to ~0.13 s before the requested time for some formats (MKV, TS...),
            // so aim a bit past the keyframe (but before the next one) to land exactly on it.
            // Everything from the keyframe on is kept when copying.
            double target = k + Math.Min(0.2, (NextKeyAfter(k) - k) / 2);
            return "-hide_banner -nostdin -y -ss " + Tools.N(target) + " -i " + Tools.Q(Info.Path) +
                   " -t " + Tools.N(Out - target) + Streams +
                   " -c copy -avoid_negative_ts make_zero -progress pipe:1 -nostats " + Tools.Q(output);
        }

        /// Fast input-seek point: the keyframe before In (or 10 s before In if keyframes aren't known yet).
        double Coarse
        {
            get
            {
                double c = Keyframes != null && Keyframes.Length > 0 ? CopyStart : In - 10;
                return Math.Max(0, c - 0.001);
            }
        }

        /// Re-encodes only the main video stream; audio/subtitles are copied untouched.
        public string ExactArgs(string output, bool gpu)
        {
            var a = new StringBuilder();
            // Two-step seek: fast input seek to the keyframe before In, then an exact output seek.
            // The output seek also trims the copied audio/subtitle packets (an input seek alone doesn't).
            // -0.5 ms: the frame exactly at In is kept, the one before it is not.
            double coarse = Coarse;
            double fine = Math.Max(0, In - coarse - 0.0005);
            a.Append("-hide_banner -nostdin -y -ss " + Tools.N(coarse) + " -i " + Tools.Q(Info.Path) +
                     " -ss " + Tools.N(fine) + " -t " + Tools.N(Out - In) + Streams);
            a.Append(" -c copy");
            a.Append(VideoEncodeOpts(":v:" + Info.VideoOrdinal, gpu, true));
            // Audio is re-encoded (same codec/bitrate/sample rate) so it starts at 0 and is exactly as long as
            // the video - copied audio can only be cut on ~21 ms packets, which leaves gaps when files are joined.
            var audio = Info.Audio.ToList();
            for (int i = 0; i < audio.Count; i++) a.Append(AudioEncodeOpts(audio[i], ":a:" + i));
            // no -avoid_negative_ts make_zero: that shifted B-frame video to start at ~0.08 s instead of 0
            a.Append(" -progress pipe:1 -nostats " + Tools.Q(output));
            return a.ToString();
        }

        // ---------- Loop export ----------
        // Step 1 renders ONE loop cycle (frame-accurate; crossfade blended in) with the final video settings
        // and lossless PCM audio. Step 2 repeats that cycle to the requested length: the video is
        // stream-copied (no second generation), the audio is encoded once as one continuous track,
        // so there are no gaps or drift at the joins.

        public bool Loop;
        public double LoopLength;     // seconds of output
        public int Cycles;            // >0: output is exactly this many whole cycles (length taken from the rendered cycle)
        public double Fade;           // crossfade seconds (0 = plain loop)
        public bool Mirror;           // loop plays forward, then backward (first and last frame meet)
        public double Speed = 1;      // playback speed of the output (0.25 .. 4)
        public bool Preview;          // build a light preview (≤1280 wide, fast encoder, AAC audio in MP4)

        int FramesOf(double sec) { return (int)Math.Round(sec * Info.Fps); }
        bool Sped { get { return Math.Abs(Speed - 1) > 0.0001; } }
        public bool HasSpeed { get { return Sped; } }
        int SpeedFrames(int sourceFrames) { return Sped ? Math.Max(1, (int)Math.Round(sourceFrames / Speed)) : sourceFrames; }

        /// Source frames in one loop cycle (before speed).
        int CycleSourceFrames
        {
            get
            {
                int n = FramesOf(Out - In);
                if (Mirror) return Math.Max(1, 2 * n - 2);
                int nf = Fade > 0 ? Math.Max(1, Math.Min(FramesOf(Fade), n / 2)) : 0;
                return n - nf;
            }
        }

        /// Length of one loop cycle in the output (after speed).
        public double CycleLength { get { return SpeedFrames(CycleSourceFrames) / Info.Fps; } }

        string FpsExpr { get { return Tools.ParseRate(Info.Video.RFrameRate) > 0 ? Info.Video.RFrameRate : Tools.N(Info.Fps); } }

        /// Video speed change keeping the source frame rate, cut to exactly `frames` output frames.
        string VSpeed(int frames)
        {
            if (!Sped) return "";
            return ",setpts=PTS/" + Tools.N(Speed) + ",fps=" + FpsExpr + ",tpad=stop_mode=clone:stop=4,trim=end_frame=" + frames + ",setpts=PTS-STARTPTS";
        }

        /// Audio tempo change without pitch change (atempo takes 0.5..2 per stage).
        string ASpeed()
        {
            if (!Sped) return "";
            var r = new StringBuilder();
            double s = Speed;
            while (s > 2.0001) { r.Append(",atempo=2"); s /= 2; }
            while (s < 0.4999) { r.Append(",atempo=0.5"); s /= 0.5; }
            r.Append(",atempo=" + Tools.N(s));
            return r.ToString();
        }

        string VideoOut(bool gpu, bool allowTag)
        {
            if (!Preview) return VideoEncodeOpts(":v:0", gpu, allowTag);
            return gpu ? " -c:v h264_nvenc -preset p1 -rc vbr -cq 19 -b:v 0" : " -c:v libx264 -preset ultrafast -crf 18";
        }
        string PreviewScale { get { return Preview ? ",scale='min(1280,iw)':-2,format=yuv420p" : ""; } }

        /// Filter graph for one plain/crossfade cycle (with speed), starting at In. Outputs [v] and [a0..].
        string CycleFilter(double coarse, out int outFrames)
        {
            double vs = Math.Max(0, In - coarse - 0.0005);     // video start (relative to the input seek)
            double au = Math.Max(0, In - coarse);              // audio start
            int n = FramesOf(Out - In);
            int nf = Fade > 0 ? Math.Max(1, Math.Min(FramesOf(Fade), n / 2)) : 0;
            double lx = n / Info.Fps, fx = nf / Info.Fps;
            outFrames = SpeedFrames(n - nf);
            double ol = outFrames / Info.Fps;                  // output length of the cycle
            var audio = Info.Audio.ToList();
            var f = new StringBuilder();
            f.Append("[0:v:" + Info.VideoOrdinal + "]trim=start=" + Tools.N(vs) + ",setpts=PTS-STARTPTS,trim=end_frame=" + n + ",setpts=PTS-STARTPTS");
            if (nf > 0)
                f.Append(",split[s1][s2];[s1]trim=start_frame=" + nf + ",setpts=PTS-STARTPTS[tl];[s2]trim=end_frame=" + nf +
                         ",setpts=PTS-STARTPTS[hd];[tl][hd]xfade=transition=fade:duration=" + Tools.N(fx) + ":offset=" + Tools.N(lx - 2 * fx));
            f.Append(VSpeed(outFrames) + PreviewScale + "[v]");
            for (int i = 0; i < audio.Count && (!Preview || i == 0); i++)
            {
                // trim to exactly the video length (pad if the audio is short) so the cycles line up
                f.Append(";[0:a:" + i + "]atrim=start=" + Tools.N(au) + ",asetpts=PTS-STARTPTS,apad,atrim=end=" + Tools.N(lx) + ",asetpts=PTS-STARTPTS");
                if (nf > 0)
                    f.Append(",asplit[x" + i + "][y" + i + "];[x" + i + "]atrim=start=" + Tools.N(fx) + ",asetpts=PTS-STARTPTS[at" + i + "];[y" + i +
                             "]atrim=end=" + Tools.N(fx) + ",asetpts=PTS-STARTPTS[ah" + i + "];[at" + i + "][ah" + i + "]acrossfade=d=" + Tools.N(fx) + ":c1=tri:c2=tri");
                f.Append(ASpeed() + ",apad,atrim=end=" + Tools.N(ol) + ",asetpts=PTS-STARTPTS[a" + i + "]");
            }
            return f.ToString();
        }

        int AudioOutCount { get { int c = Info.Audio.Count(); return Preview ? Math.Min(1, c) : c; } }

        string MapAudio()
        {
            var r = new StringBuilder();
            for (int i = 0; i < AudioOutCount; i++) r.Append(" -map [a" + i + "]");
            return r.ToString();
        }

        /// Audio codec for the loop cycle: lossless PCM (MKV) for exports, AAC (MP4) for previews.
        string UnitAudioCodec { get { return AudioOutCount == 0 ? "" : Preview ? " -c:a aac -b:a 192k" : " -c:a pcm_s24le"; } }

        /// Frame-accurate cut with a speed change (re-encodes video and audio; subtitles are left out).
        public string ExactSpeedArgs(string output, bool gpu)
        {
            double coarse = Coarse;
            int frames;
            string f = CycleFilterPlain(coarse, out frames);
            var a = new StringBuilder();
            a.Append("-hide_banner -nostdin -y -ss " + Tools.N(coarse) + " -i " + Tools.Q(Info.Path));
            a.Append(" -filter_complex " + Tools.Q(f) + " -map [v]" + MapAudio());
            a.Append(VideoEncodeOpts(":v:0", gpu, true));
            var audio = Info.Audio.ToList();
            for (int i = 0; i < audio.Count; i++) a.Append(AudioEncodeOpts(audio[i], ":a:" + i));
            a.Append(" -map_metadata 0 -progress pipe:1 -nostats " + Tools.Q(output));
            return a.ToString();
        }

        string CycleFilterPlain(double coarse, out int frames)
        {
            double saveFade = Fade; bool saveMirror = Mirror;
            Fade = 0; Mirror = false;
            try { return CycleFilter(coarse, out frames); }
            finally { Fade = saveFade; Mirror = saveMirror; }
        }

        /// The ffmpeg runs that build one loop cycle into `unit`. Each entry: (arguments, seconds of output for progress).
        /// Mirror cycles are built from small chunks (the backward part is reversed 2 s at a time), so memory stays low
        /// even for long selections; the chunks are then joined without re-encoding.
        public List<KeyValuePair<string, double>> UnitSteps(string unit, bool gpu, List<string> temps)
        {
            var steps = new List<KeyValuePair<string, double>>();
            if (!Mirror)
            {
                double coarse = Coarse;
                int frames;
                string f = CycleFilter(coarse, out frames);
                var a = new StringBuilder();
                a.Append("-hide_banner -nostdin -y -ss " + Tools.N(coarse) + " -i " + Tools.Q(Info.Path));
                a.Append(" -filter_complex " + Tools.Q(f) + " -map [v]" + MapAudio());
                a.Append(VideoOut(gpu, false) + UnitAudioCodec);
                a.Append(" -map_metadata -1 -progress pipe:1 -nostats " + Tools.Q(unit));
                steps.Add(new KeyValuePair<string, double>(a.ToString(), frames / Info.Fps));
                return steps;
            }

            int n = Math.Max(3, FramesOf(Out - In));
            string ext = Path.GetExtension(unit);
            string dir = Path.GetDirectoryName(unit), stem = Path.GetFileNameWithoutExtension(unit);
            var parts = new List<string>();
            // forward: frames 0 .. n-1
            parts.Add(ChunkStep(steps, Path.Combine(dir, stem + ".part0" + ext), 0, n, false, gpu));
            // backward: frames n-2 down to 1, in chunks of ~2 s
            int chunk = Math.Max(8, FramesOf(2));
            int k = 1;
            for (int hi = n - 1; hi > 1; hi -= chunk, k++)
            {
                int lo = Math.Max(1, hi - chunk);
                parts.Add(ChunkStep(steps, Path.Combine(dir, stem + ".part" + k + ext), lo, hi - lo, true, gpu));
            }
            temps.AddRange(parts);
            string list = Path.Combine(dir, stem + ".parts.txt");
            temps.Add(list);
            File.WriteAllLines(list, parts.Select(p => "file '" + p.Replace("'", "'\\''") + "'").ToArray(), new UTF8Encoding(false));
            steps.Add(new KeyValuePair<string, double>("-hide_banner -nostdin -y -f concat -safe 0 -i " + Tools.Q(list) +
                " -map 0 -c copy -map_metadata -1 -progress pipe:1 -nostats " + Tools.Q(unit), 0));
            return steps;
        }

        /// One mirror chunk: `count` source frames starting `first` frames after In, optionally reversed.
        string ChunkStep(List<KeyValuePair<string, double>> steps, string file, int first, int count, bool reverse, bool gpu)
        {
            double fps = Info.Fps;
            double t0 = In + first / fps;
            double coarse = CoarseFor(t0);
            double vs = Math.Max(0, t0 - coarse - 0.0005), au = Math.Max(0, t0 - coarse);
            int outFrames = SpeedFrames(count);
            double len = count / fps, ol = outFrames / fps;
            var f = new StringBuilder();
            f.Append("[0:v:" + Info.VideoOrdinal + "]trim=start=" + Tools.N(vs) + ",setpts=PTS-STARTPTS,trim=end_frame=" + count + ",setpts=PTS-STARTPTS");
            if (reverse) f.Append(",reverse");
            f.Append(VSpeed(outFrames) + PreviewScale + "[v]");
            for (int i = 0; i < AudioOutCount; i++)
            {
                f.Append(";[0:a:" + i + "]atrim=start=" + Tools.N(au) + ",asetpts=PTS-STARTPTS,apad,atrim=end=" + Tools.N(len) + ",asetpts=PTS-STARTPTS");
                if (reverse) f.Append(",areverse");
                f.Append(ASpeed() + ",apad,atrim=end=" + Tools.N(ol) + ",asetpts=PTS-STARTPTS[a" + i + "]");
            }
            var a = new StringBuilder();
            a.Append("-hide_banner -nostdin -y -ss " + Tools.N(coarse) + " -i " + Tools.Q(Info.Path));
            a.Append(" -filter_complex " + Tools.Q(f.ToString()) + " -map [v]" + MapAudio());
            a.Append(VideoOut(gpu, false) + UnitAudioCodec);
            a.Append(" -map_metadata -1 -progress pipe:1 -nostats " + Tools.Q(file));
            steps.Add(new KeyValuePair<string, double>(a.ToString(), ol));
            return file;
        }

        /// Frames in a rendered file (fast: counts packets, no decoding).
        public static int CountFrames(string file)
        {
            try
            {
                string o = Tools.RunText(Tools.FFprobe, "-v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 " + Tools.Q(file));
                int c; return int.TryParse(o.Trim().TrimEnd(','), out c) ? c : 0;
            }
            catch { return 0; }
        }

        /// Fast input-seek point for any time t (the keyframe before it).
        double CoarseFor(double t)
        {
            double k = t - 10;
            if (Keyframes != null && Keyframes.Length > 0)
            {
                k = 0;
                foreach (var x in Keyframes) { if (x <= t + 0.0005) k = x; else break; }
            }
            return Math.Max(0, k - 0.001);
        }

        /// A short, frame-exact clip of the loop seam: `lead` seconds before END, then the jump (or crossfade)
        /// into START, then `lead` seconds after - built exactly like the loop export. Preview quality, H.264 8-bit.
        public string SeamPreviewArgs(string output, double lead, bool gpu)
        {
            int n = FramesOf(Out - In);
            int nf = Fade > 0 ? Math.Max(1, Math.Min(FramesOf(Fade), n / 2)) : 0;
            int nl = Math.Max(1, Math.Min(FramesOf(lead), n - nf));
            int nt = nl + nf, nh = nf + nl;                       // tail frames (before the seam), head frames
            double fps = Info.Fps;
            double tailStart = Out - nt / fps;
            double ct = CoarseFor(tailStart), ch = CoarseFor(In);
            string ts = Tools.N(Math.Max(0, tailStart - ct - 0.0005)), hs = Tools.N(Math.Max(0, In - ch - 0.0005));
            string tsa = Tools.N(Math.Max(0, tailStart - ct)), hsa = Tools.N(Math.Max(0, In - ch));
            var f = new StringBuilder();
            string v = "[0:v:" + Info.VideoOrdinal + "]", v1 = "[1:v:" + Info.VideoOrdinal + "]";
            f.Append(v + "trim=start=" + ts + ",setpts=PTS-STARTPTS,trim=end_frame=" + nt + ",setpts=PTS-STARTPTS[t];");
            f.Append(v1 + "trim=start=" + hs + ",setpts=PTS-STARTPTS,trim=end_frame=" + nh + ",setpts=PTS-STARTPTS[h];");
            if (nf == 0) f.Append("[t][h]concat=n=2:v=1:a=0");
            else f.Append("[t][h]xfade=transition=fade:duration=" + Tools.N(nf / fps) + ":offset=" + Tools.N(nl / fps));
            f.Append(",scale='min(1280,iw)':-2,format=yuv420p[v]");
            bool audio = Info.Audio.Any();
            if (audio)
            {
                f.Append(";[0:a:0]atrim=start=" + tsa + ",asetpts=PTS-STARTPTS,apad,atrim=end=" + Tools.N(nt / fps) + ",asetpts=PTS-STARTPTS[ta]");
                f.Append(";[1:a:0]atrim=start=" + hsa + ",asetpts=PTS-STARTPTS,apad,atrim=end=" + Tools.N(nh / fps) + ",asetpts=PTS-STARTPTS[ha]");
                if (nf == 0) f.Append(";[ta][ha]concat=n=2:v=0:a=1[a]");
                else f.Append(";[ta][ha]acrossfade=d=" + Tools.N(nf / fps) + ":c1=tri:c2=tri[a]");
            }
            var a = new StringBuilder();
            a.Append("-hide_banner -nostdin -y -ss " + Tools.N(ct) + " -i " + Tools.Q(Info.Path) + " -ss " + Tools.N(ch) + " -i " + Tools.Q(Info.Path));
            a.Append(" -filter_complex " + Tools.Q(f.ToString()) + " -map [v]" + (audio ? " -map [a] -c:a aac -b:a 192k" : ""));
            a.Append(gpu ? " -c:v h264_nvenc -preset p1 -rc vbr -cq 19 -b:v 0" : " -c:v libx264 -preset ultrafast -crf 18");
            a.Append(" -map_metadata -1 " + Tools.Q(output));
            return a.ToString();
        }

        /// Seam preview layout (seconds): length of the part before the seam, and where START+fade lands.
        public void SeamLayout(double lead, out double tailLen, out double headFrom)
        {
            int n = FramesOf(Out - In);
            int nf = Fade > 0 ? Math.Max(1, Math.Min(FramesOf(Fade), n / 2)) : 0;
            int nl = Math.Max(1, Math.Min(FramesOf(lead), n - nf));
            tailLen = (nl + nf) / Info.Fps;     // clip time where the tail ends (blend ends)
            headFrom = nf / Info.Fps;            // source offset after START where the clip continues
        }

        public string LoopFinalArgs(string unit, string output)
        {
            var v = Info.Video;
            var audio = Info.Audio.ToList();
            var a = new StringBuilder();
            a.Append("-hide_banner -nostdin -y -stream_loop -1 -i " + Tools.Q(unit) + " -t " + Tools.N(LoopLength));
            // -frames:v: copied B-frame video would otherwise run a few frames past the length
            a.Append(" -map 0:v:0 -map 0:a? -map_metadata -1 -c:v copy -frames:v " + FramesOf(LoopLength));
            if (v.Codec == "hevc" && (v.CodecTag == "hvc1" || v.CodecTag == "hev1")) a.Append(" -tag:v " + v.CodecTag);
            for (int i = 0; i < audio.Count; i++)
            {
                a.Append(AudioEncodeOpts(audio[i], ":a:" + i));
                if (audio[i].Language.Length > 0) a.Append(" -metadata:s:a:" + i + " language=" + audio[i].Language);
            }
            a.Append(" -progress pipe:1 -nostats " + Tools.Q(output));
            return a.ToString();
        }

        static string AudioEncodeOpts(StreamInfo s, string sp)
        {
            string enc; bool lossy = true; long defBr = 192000;
            switch (s.Codec)
            {
                case "aac": enc = "aac"; break;
                case "mp3": enc = "libmp3lame"; break;
                case "ac3": enc = "ac3"; defBr = 384000; break;
                case "eac3": enc = "eac3"; defBr = 384000; break;
                case "opus": enc = "libopus"; defBr = 128000; break;
                case "vorbis": enc = "libvorbis"; break;
                case "flac": case "alac": enc = s.Codec; lossy = false; break;
                default:
                    if (s.Codec.StartsWith("pcm_")) { enc = s.Codec; lossy = false; }
                    else enc = "aac";
                    break;
            }
            string r = " -c" + sp + " " + enc;
            if (lossy) r += " -b" + sp + " " + (s.BitRate > 0 ? s.BitRate : defBr);
            if (s.SampleRate > 0) r += " -ar" + sp + " " + s.SampleRate;
            return r;
        }

        /// Video encoder options matching the source (codec, bitrate, pix_fmt, profile, colour tags).
        string VideoEncodeOpts(string sp, bool gpu, bool allowTag)
        {
            var v = Info.Video;
            long br = Info.VideoBitRate;
            long target = (long)(br * 1.1);           // a little above source to avoid visible generation loss
            long maxrate = (long)(br * 1.6);
            long buf = br * 2;
            string enc = Encoder(v.Codec, gpu);
            var a = new StringBuilder();
            a.Append(" -c" + sp + " " + enc);

            string pix = v.PixFmt;
            string range = v.ColorRange;
            if (pix.StartsWith("yuvj")) { pix = "yuv" + pix.Substring(4); range = "pc"; }
            if (enc.EndsWith("_nvenc"))
            {
                if (pix == "yuv420p10le") pix = "p010le";   // same 10-bit 4:2:0 layout NVENC wants
                a.Append(" -preset" + sp + " p5 -tune" + sp + " hq -rc" + sp + " vbr -spatial-aq" + sp + " 1");
            }
            else if (enc == "libx264" || enc == "libx265") a.Append(" -preset" + sp + " medium");
            else if (enc == "libaom-av1") a.Append(" -cpu-used" + sp + " 6 -row-mt" + sp + " 1");
            else if (enc == "libvpx-vp9") a.Append(" -deadline" + sp + " good -cpu-used" + sp + " 2 -row-mt" + sp + " 1");

            if (enc != "prores_ks")
                a.Append(" -b" + sp + " " + target + " -maxrate" + sp + " " + maxrate + " -bufsize" + sp + " " + buf);
            else
                a.Append(" -profile" + sp + " " + ProresProfile(v.Profile));
            if (pix.Length > 0) a.Append(" -pix_fmt" + sp + " " + pix);
            string prof = ProfileFor(enc, v.Profile);
            if (prof != null) a.Append(" -profile" + sp + " " + prof);
            if (Good(v.ColorPrimaries)) a.Append(" -color_primaries" + sp + " " + v.ColorPrimaries);
            if (Good(v.ColorTrc)) a.Append(" -color_trc" + sp + " " + v.ColorTrc);
            if (Good(v.ColorSpace)) a.Append(" -colorspace" + sp + " " + v.ColorSpace);
            if (Good(range)) a.Append(" -color_range" + sp + " " + range);
            if (allowTag && v.Codec == "hevc" && (v.CodecTag == "hvc1" || v.CodecTag == "hev1")) a.Append(" -tag" + sp + " " + v.CodecTag);
            return a.ToString();
        }

        static bool Good(string s) { return !string.IsNullOrEmpty(s) && s != "unknown" && s != "reserved"; }

        public static bool HasGpuEncoder(string codec) { return codec == "h264" || codec == "hevc"; }

        public static string Encoder(string codec, bool gpu)
        {
            if (gpu && codec == "h264") return "h264_nvenc";
            if (gpu && codec == "hevc") return "hevc_nvenc";
            switch (codec)
            {
                case "h264": return "libx264";
                case "hevc": return "libx265";
                case "vp9": return "libvpx-vp9";
                case "vp8": return "libvpx";
                case "av1": return "libaom-av1";
                case "prores": return "prores_ks";
                default: return codec;   // mpeg4, mpeg2video, mjpeg, ...
            }
        }

        static string ProfileFor(string enc, string p)
        {
            p = (p ?? "").ToLowerInvariant();
            if (enc == "h264_nvenc")
            {
                if (p == "high") return "high";
                if (p == "main") return "main";
                if (p.Contains("baseline")) return "baseline";
                if (p.Contains("4:4:4")) return "high444p";
                return null;
            }
            if (enc == "libx264")
            {
                if (p == "high") return "high";
                if (p == "main") return "main";
                if (p.Contains("baseline")) return "baseline";
                if (p == "high 10") return "high10";
                if (p.Contains("4:2:2")) return "high422";
                if (p.Contains("4:4:4")) return "high444";
                return null;
            }
            if (enc == "hevc_nvenc")
            {
                if (p == "main") return "main";
                if (p == "main 10") return "main10";
                if (p == "rext") return "rext";
            }
            return null;
        }

        static string ProresProfile(string p)
        {
            p = (p ?? "").ToLowerInvariant();
            if (p.Contains("proxy")) return "0";
            if (p.Contains("lt")) return "1";
            if (p.Contains("4444") && p.Contains("xq")) return "5";
            if (p.Contains("4444")) return "4";
            if (p.Contains("hq")) return "3";
            return "2";
        }

        public string Describe()
        {
            var v = Info.Video;
            if (!Exact) return "lossless stream copy";
            return Encoder(v.Codec, true) + " @ ~" + Tools.Rate((long)(Info.VideoBitRate * 1.1));
        }
    }

    /// Runs one ffmpeg export and reports progress (0..1). Writes to a temp file first,
    /// then renames, so a failed/cancelled export never destroys an existing file.
    public class Exporter
    {
        public event Action<double, string> Progress;   // fraction, speed text
        public event Action<string> Status;
        Process proc;
        volatile bool cancelled;
        public string LastError = "";
        public string UsedEncoder = "";

        public void Cancel()
        {
            cancelled = true;
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
        }

        public bool Run(ExportJob job)
        {
            if (Tools.FFmpeg == null) { LastError = "ffmpeg.exe was not found."; return false; }
            string dir = Path.GetDirectoryName(job.Output);
            string tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(job.Output) + ".cutting" + Path.GetExtension(job.Output));
            try
            {
                bool ok;
                if (job.Loop) ok = RunLoop(job, tmp);
                else if (!job.Exact)
                {
                    UsedEncoder = "copy";
                    ok = RunOne(job.CopyArgs(tmp), job.OutputLength);
                }
                else
                {
                    bool gpu = ExportJob.HasGpuEncoder(job.Info.Video.Codec);
                    UsedEncoder = ExportJob.Encoder(job.Info.Video.Codec, gpu);
                    Func<bool, string> args = g => job.HasSpeed ? job.ExactSpeedArgs(tmp, g) : job.ExactArgs(tmp, g);
                    ok = RunOne(args(gpu), job.OutputLength);
                    if (!ok && gpu && !cancelled)
                    {
                        if (Status != null) Status("GPU encoder failed - retrying on CPU...");
                        UsedEncoder = ExportJob.Encoder(job.Info.Video.Codec, false);
                        ok = RunOne(args(false), job.OutputLength);
                    }
                }
                if (!ok || cancelled) { TryDelete(tmp); if (cancelled) LastError = "Cancelled."; return false; }
                if (File.Exists(job.Output)) File.Delete(job.Output);
                File.Move(tmp, job.Output);
                return true;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                LastError = ex.Message;
                return false;
            }
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        double progBase = 0, progScale = 1;

        bool RunLoop(ExportJob job, string tmp)
        {
            string unit = Path.Combine(Path.GetDirectoryName(tmp), Path.GetFileNameWithoutExtension(job.Output) + ".loop-cycle.mkv");
            var temps = new List<string>();
            try
            {
                double cycle = job.CycleLength;
                // rough weights: encoding the cycle is slow, repeating it (copy + audio encode) is fast
                double w1 = cycle / (cycle + job.LoopLength / 15);
                if (Status != null) Status("Rendering one loop cycle...");
                bool gpu = ExportJob.HasGpuEncoder(job.Info.Video.Codec);
                UsedEncoder = ExportJob.Encoder(job.Info.Video.Codec, gpu);
                bool ok = RunSteps(job.UnitSteps(unit, gpu, temps), 0, w1);
                if (!ok && gpu && !cancelled)
                {
                    if (Status != null) Status("GPU encoder failed - retrying on CPU...");
                    UsedEncoder = ExportJob.Encoder(job.Info.Video.Codec, false);
                    ok = RunSteps(job.UnitSteps(unit, false, temps), 0, w1);
                }
                if (!ok || cancelled) return false;
                if (job.Cycles > 0)
                {   // whole cycles: take the exact length from the rendered cycle
                    int u = ExportJob.CountFrames(unit);
                    if (u > 0) job.LoopLength = job.Cycles * u / job.Info.Fps;
                }
                if (Status != null) Status("Repeating to full length...");
                progBase = w1; progScale = 1 - w1;
                return RunOne(job.LoopFinalArgs(unit, tmp), job.LoopLength);
            }
            finally
            {
                progBase = 0; progScale = 1;
                TryDelete(unit);
                foreach (var t in temps) TryDelete(t);
            }
        }

        /// Runs several ffmpeg steps, spreading their progress over [from, to].
        bool RunSteps(List<KeyValuePair<string, double>> steps, double from, double to)
        {
            double total = Math.Max(0.001, steps.Sum(s => Math.Max(0.05, s.Value)));
            double done = 0;
            foreach (var s in steps)
            {
                double w = Math.Max(0.05, s.Value);
                progBase = from + (to - from) * done / total;
                progScale = (to - from) * w / total;
                if (!RunOne(s.Key, Math.Max(0.05, s.Value)) || cancelled) return false;
                done += w;
            }
            return true;
        }

        bool RunOne(string args, double length)
        {
            var errLines = new Queue<string>();
            var psi = Tools.Psi(Tools.FFmpeg, args);
            using (proc = Process.Start(psi))
            {
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (errLines) { errLines.Enqueue(e.Data); while (errLines.Count > 25) errLines.Dequeue(); }
                };
                proc.BeginErrorReadLine();
                string line, speed = "";
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("speed=")) speed = line.Substring(6).Trim();
                    else if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                    {
                        long us;
                        if (long.TryParse(line.Substring(12), out us) && length > 0 && Progress != null)
                            Progress(progBase + progScale * Math.Max(0, Math.Min(1, us / 1e6 / length)), speed);
                    }
                }
                proc.WaitForExit();
                lock (errLines) LastError = string.Join("\n", errLines.Where(l => l.Trim().Length > 0).ToArray());
                return proc.ExitCode == 0;
            }
        }
    }
}
