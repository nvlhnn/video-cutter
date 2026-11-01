// Video Cutter - main window logic and entry point.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("Video Cutter")]
[assembly: AssemblyProduct("Video Cutter")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace VideoCutter
{
    public static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--export") return Cli(args);
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var ui = new MainUI();
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.Message, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            if (args.Length > 0 && File.Exists(args[0])) ui.Window.Loaded += delegate { ui.Open(args[0]); };
            return app.Run(ui.Window);
        }

        // Test/automation mode: VideoCutter.exe --export <in> <out> <start> <end> <copy|exact>
        static int Cli(string[] a)
        {
            AttachConsole(-1);
            try
            {
                if (a.Length < 6) { Console.WriteLine("usage: --export <in> <out> <start> <end> <copy|exact|loop=LEN|loopxf=LEN,FADE>"); return 2; }
                var info = Probe.Run(a[1]);
                double s, e;
                if (!Tools.TryParseTime(a[3], out s) || !Tools.TryParseTime(a[4], out e)) { Console.WriteLine("bad time"); return 2; }
                var job = new ExportJob { Info = info, Output = Path.GetFullPath(a[2]), In = s, Out = Math.Min(e, info.Duration), Exact = a[5] == "exact" };
                // loop modes:  loop=<length>   or   loopxf=<length>,<fade seconds>
                if (a[5].StartsWith("loop"))
                {
                    var p = a[5].Substring(a[5].IndexOf('=') + 1).Split(',');
                    double len, fd = 0;
                    if (!Tools.TryParseTime(p[0], out len)) { Console.WriteLine("bad loop length"); return 2; }
                    if (a[5].StartsWith("loopxf") && (p.Length < 2 || !double.TryParse(p[1], System.Globalization.NumberStyles.Float, Tools.Inv, out fd))) { Console.WriteLine("bad fade"); return 2; }
                    job.Loop = true; job.LoopLength = len; job.Fade = fd;
                }
                // extra options after the mode: speed=0.5  mirror  cycles=N
                for (int i = 6; i < a.Length; i++)
                {
                    double sp; int cy;
                    if (a[i].StartsWith("speed=") && double.TryParse(a[i].Substring(6), System.Globalization.NumberStyles.Float, Tools.Inv, out sp)) job.Speed = Math.Max(0.25, Math.Min(4, sp));
                    else if (a[i] == "mirror") { job.Mirror = true; job.Fade = 0; }
                    else if (a[i].StartsWith("cycles=") && int.TryParse(a[i].Substring(7), out cy)) job.Cycles = cy;
                }
                if (job.Loop && job.Cycles > 0) job.LoopLength = job.Cycles * job.CycleLength;
                job.Keyframes = Probe.Keyframes(info, CancellationToken.None);
                var ex = new Exporter();
                ex.Status += m => Console.WriteLine(m);
                bool ok = ex.Run(job);
                Console.WriteLine(ok ? "OK " + ex.UsedEncoder + " start=" + Tools.N(job.Exact ? job.In : job.CopyStart) + " len=" + Tools.N(job.OutputLength)
                                     : "FAILED: " + ex.LastError);
                return ok ? 0 : 1;
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); return 1; }
        }
    }

    public class MainUI
    {
        public Window Window;
        MediaElement player;            // the visible/active player
        MediaElement standby;           // loop preview: waits paused at START
        MediaElement p1, p2;
        Image frameImage;
        RadioButton rbLoopOff, rbLoopPlain, rbLoopFade, rbLoopMirror, rbLoopExport;
        Slider speedSlider;
        TextBlock txtSpeed;
        static readonly double[] Speeds = { 0.25, 1.0 / 3, 0.5, 0.75, 1, 1.25, 1.5, 2, 2.5, 3, 4 };
        double speed = 1;
        TextBox tbFade, tbLoopLen;
        double loopLen = 600;
        int loopMode;                   // 0 off, 1 plain loop, 2 crossfade loop
        double fade = 1.0;
        bool crossfading, loopHooked;
        FrameworkElement dropHint, previewBadge;
        TextBlock txtFile, txtInfo, txtPos, txtDur, txtLen, txtModeNote, txtStatus, badgeText;
        TextBox tbIn, tbOut;
        Button btnPlay, btnExport, btnCancel, btnShow, btnMute;
        RadioButton rbCopy, rbExact;
        ProgressBar progress;
        Timeline timeline;

        MediaInfo info;
        double[] keyframes;
        CancellationTokenSource keyCts;
        double pos, inT, outT, frameDur = 1 / 25.0;
        bool stillMode, playing, playSel, wasPlayingBeforeDrag;
        int openToken;
        string lastExport;

        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        readonly DispatcherTimer seekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        double? pendingSeek;
        bool grabbing; double? pendingGrab;
        Exporter exporter;

        T F<T>(string name) where T : class { return Window.FindName(name) as T; }

        public MainUI()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(s);
            p1 = F<MediaElement>("Player"); p2 = F<MediaElement>("Player2");
            player = p1; standby = p2;
            frameImage = F<Image>("FrameImage");
            rbLoopOff = F<RadioButton>("RbLoopOff"); rbLoopPlain = F<RadioButton>("RbLoopPlain"); rbLoopFade = F<RadioButton>("RbLoopFade");
            tbFade = F<TextBox>("TbFade");
            holdImage = F<Image>("HoldImage");
            seamPlayer = F<MediaElement>("SeamPlayer");
            seamTick.Tick += (s, e) => SeamTick();
            seamHoldTimer.Tick += (s, e) => SeamRestart();
            seamPlayer.MediaEnded += (s, e) => SeamToEnd();
            seamPlayer.MediaFailed += (s, e) => { EndSeam(); txtStatus.Text = "Couldn't play the seam preview."; };
            ClearCache();
            rbLoopExport = F<RadioButton>("RbLoopExport"); tbLoopLen = F<TextBox>("TbLoopLen");
            rbLoopMirror = F<RadioButton>("RbLoopMirror");
            rbLoopMirror.Checked += (s, e) => SetLoopMode(3);
            speedSlider = F<Slider>("SpeedSlider"); txtSpeed = F<TextBlock>("TxtSpeed");
            speedSlider.ValueChanged += (s, e) => SetSpeed(Speeds[Math.Max(0, Math.Min(Speeds.Length - 1, (int)Math.Round(speedSlider.Value)))]);
            speedSlider.MouseDoubleClick += (s, e) => speedSlider.Value = 4;
            cbWholeCycles = F<CheckBox>("CbWholeCycles");
            F<Button>("BtnOneCycle").Click += (s, e) => SetOneCycle();
            cbWholeCycles.Checked += (s, e) => UpdateNote();
            cbWholeCycles.Unchecked += (s, e) => UpdateNote();
            dropHint = F<FrameworkElement>("DropHint"); previewBadge = F<FrameworkElement>("PreviewBadge");
            badgeText = F<TextBlock>("PreviewBadgeText");
            txtFile = F<TextBlock>("TxtFile"); txtInfo = F<TextBlock>("TxtInfo"); txtPos = F<TextBlock>("TxtPos");
            txtDur = F<TextBlock>("TxtDur"); txtLen = F<TextBlock>("TxtLen"); txtModeNote = F<TextBlock>("TxtModeNote");
            txtStatus = F<TextBlock>("TxtStatus");
            tbIn = F<TextBox>("TbIn"); tbOut = F<TextBox>("TbOut");
            btnPlay = F<Button>("BtnPlay"); btnExport = F<Button>("BtnExport"); btnCancel = F<Button>("BtnCancel");
            btnShow = F<Button>("BtnShow"); btnMute = F<Button>("BtnMute");
            rbCopy = F<RadioButton>("RbCopy"); rbExact = F<RadioButton>("RbExact");
            progress = F<ProgressBar>("Progress");

            timeline = new Timeline();
            F<Border>("TimelineHost").Child = timeline;
            timeline.Scrub += t => Seek(t);
            timeline.InMoved += t => SetIn(t);
            timeline.OutMoved += t => SetOut(t);
            timeline.Wheel += d => Step(d);
            timeline.DragStarted += () => { wasPlayingBeforeDrag = playing; if (playing) Pause(); };
            timeline.DragEnded += () => { if (wasPlayingBeforeDrag) Play(false); };

            foreach (var me in new[] { p1, p2 })
            {
                me.Volume = 1;
                me.MediaOpened += OnMediaOpened;
                me.MediaFailed += (s, e) =>
                {
                    if (s == player) EnterStillMode("Windows can't play this format");
                    else { rbLoopOff.IsChecked = true; txtStatus.Text = "Loop preview isn't available for this file."; }
                };
                me.MediaEnded += OnMediaEnded;
            }
            Panel.SetZIndex(p1, 1); Panel.SetZIndex(p2, 0);

            rbLoopOff.Checked += (s, e) => SetLoopMode(0);
            rbLoopPlain.Checked += (s, e) => SetLoopMode(1);
            rbLoopFade.Checked += (s, e) => SetLoopMode(2);
            tbFade.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { CommitFade(); Window.Focus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { tbFade.Text = Tools.N(fade); Window.Focus(); e.Handled = true; }
            };
            tbFade.LostKeyboardFocus += (s, e) => CommitFade();
            F<Button>("BtnSeam").Click += (s, e) => PlaySeam();

            tick.Tick += OnTick;
            seekTimer.Tick += (s, e) =>
            {
                seekTimer.Stop();
                if (pendingSeek.HasValue) { player.Position = TimeSpan.FromSeconds(pendingSeek.Value); pendingSeek = null; }
            };

            F<Button>("BtnOpen").Click += (s, e) => OpenDialog();
            btnPlay.Click += (s, e) => TogglePlay();
            F<Button>("BtnPrevFrame").Click += (s, e) => Step(-1);
            F<Button>("BtnNextFrame").Click += (s, e) => Step(1);
            F<Button>("BtnPrevKey").Click += (s, e) => JumpKey(-1);
            F<Button>("BtnNextKey").Click += (s, e) => JumpKey(1);
            F<Button>("BtnPlaySel").Click += (s, e) => PlaySelection();
            F<Button>("BtnSetIn").Click += (s, e) => SetIn(pos);
            F<Button>("BtnSetOut").Click += (s, e) => SetOut(pos);
            F<Button>("BtnGoIn").Click += (s, e) => { Pause(); Seek(inT); };
            F<Button>("BtnGoOut").Click += (s, e) => { Pause(); Seek(outT); };
            btnMute.Click += (s, e) => ToggleMute();
            btnExport.Click += (s, e) => Export();
            btnCancel.Click += (s, e) => { if (exporter != null) exporter.Cancel(); };
            btnShow.Click += (s, e) => { if (lastExport != null) Process.Start("explorer.exe", "/select,\"" + lastExport + "\""); };
            rbCopy.Checked += (s, e) => UpdateNote();
            rbExact.Checked += (s, e) => UpdateNote();
            rbLoopExport.Checked += (s, e) => UpdateNote();
            tbLoopLen.GotKeyboardFocus += (s, e) => rbLoopExport.IsChecked = true;
            tbLoopLen.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { CommitLoopLen(); Window.Focus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { tbLoopLen.Text = Tools.Fmt(loopLen).Substring(0, 8); Window.Focus(); e.Handled = true; }
            };
            tbLoopLen.LostKeyboardFocus += (s, e) => CommitLoopLen();
            tbLoopLen.PreviewTextInput += (s, e) => oneCycle = false;   // typing a length leaves "1 cycle" mode

            tbIn.KeyDown += (s, e) => TimeBoxKey(tbIn, e, true);
            tbOut.KeyDown += (s, e) => TimeBoxKey(tbOut, e, false);
            tbIn.LostKeyboardFocus += (s, e) => CommitBox(tbIn, true);
            tbOut.LostKeyboardFocus += (s, e) => CommitBox(tbOut, false);

            Window.PreviewKeyDown += OnKey;
            Window.Drop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) Open(files[0]);
            };
            Window.Closing += (s, e) =>
            {
                if (exporter != null)
                {
                    if (MessageBox.Show(Window, "An export is running. Stop it and quit?", "Video Cutter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    { e.Cancel = true; return; }
                    exporter.Cancel();
                }
                if (keyCts != null) keyCts.Cancel();
                p1.Close(); p2.Close();
                EndSeam(); seamPlayer.Close();
                ClearCache();
            };
            SetEnabled(false);
            UpdateNote();
        }

        // ---------- Opening ----------

        void OpenDialog()
        {
            var d = new Microsoft.Win32.OpenFileDialog();
            d.Title = "Open video";
            d.Filter = "Video files|*.mp4;*.mkv;*.mov;*.m4v;*.avi;*.webm;*.ts;*.mts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg;*.3gp|All files|*.*";
            if (d.ShowDialog(Window) == true) Open(d.FileName);
        }

        public async void Open(string path)
        {
            if (exporter != null) { MessageBox.Show(Window, "Please wait for the export to finish.", "Video Cutter"); return; }
            int token = ++openToken;
            EndSeam();
            Pause();
            txtStatus.Text = "Reading file...";
            MediaInfo mi;
            try { mi = await Task.Run(() => Probe.Run(path)); }
            catch (Exception ex)
            {
                if (token != openToken) return;
                txtStatus.Text = "Ready";
                MessageBox.Show(Window, "Can't open this file:\n\n" + ex.Message, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (token != openToken) return;

            info = mi;
            frameDur = 1 / info.Fps;
            keyframes = null;
            inT = 0; outT = info.Duration; pos = 0;
            stillMode = false;
            lastExport = null; btnShow.Visibility = Visibility.Collapsed;
            progress.Value = 0;
            txtStatus.Text = "Ready";
            Window.Title = Path.GetFileName(path) + " - Video Cutter";
            txtFile.Text = Path.GetFileName(path);
            txtInfo.Text = Describe(info);
            txtDur.Text = " / " + Tools.Fmt(info.Duration);
            dropHint.Visibility = Visibility.Collapsed;
            previewBadge.Visibility = Visibility.Collapsed;
            frameImage.Visibility = Visibility.Collapsed;
            frameImage.Source = null;
            timeline.Duration = info.Duration;
            timeline.Keyframes = null;
            SetEnabled(true);
            RefreshTimes();

            // Preview through Windows' media engine (hardware decoding, light on CPU).
            ResetPlayers(true);
            player.Source = new Uri(path);
            player.Play();
            player.Pause();
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            guard.Tick += (s, e) =>
            {
                guard.Stop();
                if (token == openToken && !stillMode && !player.NaturalDuration.HasTimeSpan) EnterStillMode("Windows can't play this format");
            };
            guard.Start();

            // Keyframe scan in the background (used by copy mode and the keyframe buttons).
            if (keyCts != null) keyCts.Cancel();
            keyCts = new CancellationTokenSource();
            var ct = keyCts.Token;
            UpdateNote();
            try
            {
                var k = await Task.Run(() => Probe.Keyframes(mi, ct));
                if (token != openToken || k == null) return;
                keyframes = k;
                timeline.Keyframes = k;
                timeline.InvalidateVisual();
                UpdateNote();
            }
            catch { }
        }

        void OnMediaOpened(object sender, RoutedEventArgs e)
        {
            if (info == null || sender != player) return;
            if (player.NaturalVideoWidth <= 0) { EnterStillMode("Windows can't decode this video codec"); return; }
            player.Pause();
            player.Position = TimeSpan.FromSeconds(pos);
        }

        void EnterStillMode(string why)
        {
            if (info == null || stillMode) return;
            stillMode = true;
            Pause();
            rbLoopOff.IsChecked = true;
            ResetPlayers(false);
            frameImage.Visibility = Visibility.Visible;
            badgeText.Text = why + " - showing still frames (export is not affected)";
            previewBadge.Visibility = Visibility.Visible;
            Grab(pos);
        }

        static string Describe(MediaInfo i)
        {
            var v = i.Video;
            string s = v.Codec.ToUpperInvariant() + (v.Profile.Length > 0 ? " (" + v.Profile + ")" : "") +
                       "  ·  " + v.Width + "×" + v.Height +
                       "  ·  " + i.Fps.ToString("0.###", Tools.Inv) + " fps" +
                       "  ·  " + v.PixFmt +
                       "  ·  " + Tools.Rate(i.VideoBitRate);
            var a = i.Audio.ToList();
            if (a.Count == 0) s += "  ·  no audio";
            else s += "  ·  " + a.Count + " audio: " + string.Join(", ", a.Select(x => x.Codec + " " + x.Channels + "ch" + (x.Language.Length > 0 && x.Language != "und" ? " " + x.Language : "")).ToArray());
            int subs = i.Streams.Count(x => x.Type == "subtitle");
            if (subs > 0) s += "  ·  " + subs + " subtitle";
            return s + "  ·  " + Tools.Size(i.Size);
        }

        void SetEnabled(bool on)
        {
            timeline.Enabled = on;
            foreach (var n in new[] { "BtnPrevKey", "BtnPrevFrame", "BtnPlay", "BtnNextFrame", "BtnNextKey", "BtnPlaySel", "BtnMute", "BtnSetIn", "BtnSetOut", "BtnGoIn", "BtnGoOut", "BtnSeam" })
                F<Button>(n).IsEnabled = on;
            tbIn.IsEnabled = tbOut.IsEnabled = on;
            rbLoopOff.IsEnabled = rbLoopPlain.IsEnabled = rbLoopFade.IsEnabled = rbLoopMirror.IsEnabled = on;
            tbFade.IsEnabled = on && loopMode == 2;
            btnExport.IsEnabled = on && exporter == null;
            timeline.InvalidateVisual();
        }

        // ---------- Playback / seeking ----------

        void TogglePlay() { if (seamActive) { EndSeam(); return; } if (playing) Pause(); else Play(false); }

        void Play(bool selection)
        {
            if (info == null) return;
            if (seamActive) EndSeam();
            if (stillMode) { txtStatus.Text = "Playback isn't available for this format - scrub to preview."; return; }
            playSel = selection;
            if (!selection && pos >= info.Duration - frameDur) Seek(0);
            if (pendingSeek.HasValue) { seekTimer.Stop(); player.Position = TimeSpan.FromSeconds(pendingSeek.Value); pendingSeek = null; }
            ApplySpeed();
            player.Play();
            playing = true;
            btnPlay.Content = "";
            tick.Start();
            if (selection && (loopMode == 1 || loopMode == 2)) StartLoop();
        }

        void Pause()
        {
            if (!playing) return;
            StopLoop();
            player.Pause();
            playing = false; playSel = false;
            btnPlay.Content = "";
            tick.Stop();
            pos = SnapFrame(player.Position.TotalSeconds);
            RefreshTimes();
        }

        void PlaySelection()
        {
            if (info == null) return;
            if (loopMode == 3) { PlayMirror(); return; }   // backward playback has to be rendered
            Pause();
            Seek(inT);
            Play(true);
        }

        void OnTick(object sender, EventArgs e)
        {
            pos = Math.Min(info.Duration, player.Position.TotalSeconds);
            if (playSel && loopMode == 0 && pos >= outT) { Pause(); Seek(outT); return; }
            RefreshTimes();
        }

        void Seek(double t)
        {
            if (info == null) return;
            if (seamActive) EndSeam();
            pos = Math.Max(0, Math.Min(info.Duration, t));
            if (stillMode) Grab(pos);
            else if (playing) player.Position = TimeSpan.FromSeconds(pos);
            else { pendingSeek = pos; if (!seekTimer.IsEnabled) seekTimer.Start(); }   // throttled while scrubbing
            RefreshTimes();
        }

        double SnapFrame(double t)
        {
            if (info == null) return t;
            double s = Math.Round(t * info.Fps) / info.Fps;
            return Math.Max(0, Math.Min(info.Duration, s));
        }

        void Step(int frames)
        {
            if (info == null) return;
            if (playing) Pause();
            Seek(SnapFrame(SnapFrame(pos) + frames * frameDur));
        }

        void StepSeconds(double sec)
        {
            if (info == null) return;
            if (playing) Pause();
            Seek(SnapFrame(pos + sec));
        }

        void JumpKey(int dir)
        {
            if (info == null) return;
            if (keyframes == null || keyframes.Length == 0) { txtStatus.Text = "Still finding keyframes..."; return; }
            if (playing) Pause();
            double target = dir < 0 ? keyframes.Where(k => k < pos - 0.001).DefaultIfEmpty(0).Max()
                                    : keyframes.Where(k => k > pos + 0.001).DefaultIfEmpty(info.Duration).Min();
            Seek(target);
        }

        async void Grab(double t)
        {
            if (grabbing) { pendingGrab = t; return; }
            grabbing = true;
            int token = openToken;
            string path = info.Path;
            try
            {
                while (true)
                {
                    byte[] bmp = await Task.Run(() => Probe.Frame(path, t, 1280));
                    if (token != openToken) return;
                    if (bmp != null)
                    {
                        var img = new BitmapImage();
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;
                        img.StreamSource = new MemoryStream(bmp);
                        img.EndInit();
                        img.Freeze();
                        frameImage.Source = img;
                    }
                    if (!pendingGrab.HasValue) break;
                    t = pendingGrab.Value; pendingGrab = null;
                }
            }
            catch { }
            finally { grabbing = false; }
        }

        void ToggleMute()
        {
            p1.IsMuted = p2.IsMuted = !p1.IsMuted;
            btnMute.Content = p1.IsMuted ? "" : "";
        }

        void OnMediaEnded(object sender, RoutedEventArgs e)
        {
            if (sender != player) return;
            if (loopHooked)
            {   // selection runs to the end of the file: the last frame stays on screen until the next cycle is moving
                oldFrozen = true;
                endedPlayer = player;
                HoldFrame(player);
                DebugLog("ended " + (player == p1 ? "p1" : "p2") + " at " + player.Position.TotalSeconds.ToString("0.000"));
                if (!handing && !crossfading) BeginHandover(loopMode == 2);
                return;
            }
            Pause(); Seek(info != null ? info.Duration : 0);
        }

        // ---------- Loop preview (preview only; export is unchanged) ----------
        // Two players: the active one plays the selection, the standby one waits paused at START.
        // Plain loop: at END the standby takes over instantly (no seek stall).
        // Crossfade loop: the standby starts at END - fade and fades in on top, audio crossfades too.

        void ResetPlayers(bool visible)
        {
            StopLoop();
            p1.Close(); p2.Close();
            p2.Source = null;
            player = p1; standby = p2;
            p1.Opacity = 1; p2.Opacity = 0;
            p1.Volume = p2.Volume = 1;
            Panel.SetZIndex(p1, 1); Panel.SetZIndex(p2, 0);
            p1.Visibility = p2.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        void SetLoopMode(int m)
        {
            loopMode = m;
            tbFade.IsEnabled = info != null && m == 2;
            if (seamActive) EndSeam();
            if (playing && playSel)
            {
                if (m == 0) StopLoop();
                else if (m == 3) Pause();                       // mirror is previewed by rendering
                else { StopLoop(); StartLoop(); }
            }
            if (m == 1 || m == 2) ParkStandby();
            RefreshTimes(); UpdateNote();
        }

        void CycleLoop()
        {
            if (rbLoopOff.IsChecked == true) rbLoopPlain.IsChecked = true;
            else if (rbLoopPlain.IsChecked == true) rbLoopFade.IsChecked = true;
            else if (rbLoopFade.IsChecked == true) rbLoopMirror.IsChecked = true;
            else rbLoopOff.IsChecked = true;
        }

        // ---------- Speed ----------

        void SetSpeed(double s)
        {
            speed = s;
            txtSpeed.Text = (Math.Abs(s - 1.0 / 3) < 0.001 ? "0.33" : s.ToString("0.##", Tools.Inv)) + "×";
            ApplySpeed();
            UpdateNote();
        }

        void ApplySpeed()
        {
            p1.SpeedRatio = speed; p2.SpeedRatio = speed;
            if (seamPlayer != null) seamPlayer.SpeedRatio = speed;
        }

        void NudgeSpeed(int dir)
        {
            speedSlider.Value = Math.Max(speedSlider.Minimum, Math.Min(speedSlider.Maximum, Math.Round(speedSlider.Value) + dir));
        }

        void CommitFade()
        {
            double v;
            if (double.TryParse((tbFade.Text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, Tools.Inv, out v))
                fade = Math.Max(0.05, Math.Min(10, v));
            tbFade.Text = fade.ToString("0.0##", Tools.Inv);
            RefreshTimes(); UpdateNote();
        }

        void CommitLoopLen()
        {
            if (oneCycle) { ShowLoopLen(); UpdateNote(); return; }
            double v;
            if (Tools.TryParseTime(tbLoopLen.Text, out v) && v >= 0.04) loopLen = Math.Min(v, 24 * 3600);
            ShowLoopLen();
            UpdateNote();
        }

        // ---------- Loop cycles ----------
        // A file that is exactly a whole number of loop cycles repeats seamlessly (FFmpeg -stream_loop,
        // OBS "loop", editors). One cycle = selection length (plain) or selection minus fade (crossfade).

        CheckBox cbWholeCycles;
        bool oneCycle;          // "1 cycle" button: follows the selection/fade automatically

        ExportJob CycleJob() { return new ExportJob { Info = info, In = inT, Out = outT, Fade = loopMode == 2 ? EffFade : 0, Mirror = loopMode == 3, Speed = speed }; }

        int CycleFrames()
        {
            if (info == null) return 0;
            return Math.Max(1, (int)Math.Round(CycleJob().CycleLength * info.Fps));
        }

        /// Number of cycles and exact length the export will have.
        void LoopPlan(out int cycles, out double length)
        {
            cycles = 0; length = loopLen;
            if (info == null) return;
            int cf = CycleFrames();
            double cyc = cf / info.Fps;
            if (oneCycle) { cycles = 1; length = cyc; return; }
            if (cbWholeCycles.IsChecked == true)
            {
                cycles = Math.Max(1, (int)Math.Round(loopLen / cyc));
                length = cycles * cf / info.Fps;
            }
        }

        void SetOneCycle()
        {
            if (info == null) return;
            oneCycle = true;
            rbLoopExport.IsChecked = true;
            ShowLoopLen();
            UpdateNote();
        }

        void ShowLoopLen()
        {
            if (oneCycle && info != null) { int c; double l; LoopPlan(out c, out l); tbLoopLen.Text = Tools.Fmt(l); return; }
            string s = Tools.Fmt(loopLen);
            tbLoopLen.Text = s.EndsWith(".000") ? s.Substring(0, 8) : s;
        }

        /// Crossfade length actually used (never more than half the selection).
        double EffFade { get { return Math.Max(0.04, Math.Min(fade, (outT - inT) / 2)); } }

        /// Loads the standby player (first time) and parks it paused on the START frame.
        void ParkStandby()
        {
            if (info == null || stillMode || crossfading) return;
            if (standby.Source == null) standby.Source = new Uri(info.Path);
            standby.IsMuted = p1.IsMuted;
            standby.Opacity = 0;
            standby.Volume = 0;
            standby.Play();
            standby.Pause();
            standby.Position = TimeSpan.FromSeconds(inT);
        }

        void StartLoop()
        {
            if (stillMode) return;
            ParkStandby();
            handing = crossfading = oldFrozen = false;
            if (!loopHooked) { System.Windows.Media.CompositionTarget.Rendering += LoopFrame; loopHooked = true; }
        }

        void StopLoop()
        {
            if (loopHooked) { System.Windows.Media.CompositionTarget.Rendering -= LoopFrame; loopHooked = false; }
            ClearHold();
            if (handing || crossfading)
            {
                standby.Pause();
                standby.Opacity = 0;
                standby.Volume = 0;
                Panel.SetZIndex(standby, 0); Panel.SetZIndex(player, 1);
                handing = crossfading = oldFrozen = false;
                standby.Position = TimeSpan.FromSeconds(inT);
            }
            player.Volume = 1;
            player.Opacity = 1;
        }

        // Handover between the two players. A player needs ~50-150 ms after Play() before it shows
        // moving frames, so the next cycle is started a little early (learned delay) and only shown
        // once it is really moving. Until then the old player's last frame stays on screen - never black.
        bool handing, oldFrozen, latencyMeasured, xfDrawn;
        double xfBase, xfRefBright = 100;
        MediaElement endedPlayer;
        Image holdImage;
        bool holdActive;
        double holdBright;
        readonly Stopwatch holdSw = new Stopwatch();

        // Windows' player can run its clock and even draw a black picture before the real first frame
        // shows up (much longer on files with few keyframes). So at the handover a snapshot of the last
        // frame is laid ON TOP and only removed once the new player really shows a picture (max 0.6 s).
        static RenderTargetBitmap Snapshot(FrameworkElement src, double scale)
        {
            double w = src.ActualWidth, h = src.ActualHeight;
            if (w < 1 || h < 1) return null;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(src);
            double sx = dpi.DpiScaleX * scale, sy = dpi.DpiScaleY * scale;
            var rtb = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w * sx)), Math.Max(1, (int)Math.Ceiling(h * sy)),
                                             96 * sx, 96 * sy, System.Windows.Media.PixelFormats.Pbgra32);
            // draw through a VisualBrush: rendering the element directly would include its offset in the window
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(src), null, new Rect(0, 0, w, h));
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        void HoldFrame(MediaElement src)
        {
            try
            {
                var rtb = Snapshot(src, 1);
                if (rtb == null) return;
                holdImage.Source = rtb;
                holdImage.Visibility = Visibility.Visible;
                holdBright = AvgBright(rtb);
                holdActive = true;
                if (dbg != null)
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(dbg + ".hold.png")) enc.Save(fs);
                    DebugLog("hold " + rtb.PixelWidth + "x" + rtb.PixelHeight + " avg " + holdBright.ToString("0.0") + " element " + src.ActualWidth + "x" + src.ActualHeight);
                }
                holdSw.Reset();
            }
            catch { }
        }

        /// Called every frame after the handover: drop the snapshot once the new player shows a picture.
        void CheckHold()
        {
            if (!holdActive || !holdSw.IsRunning) return;
            bool drawn = false;
            try
            {
                var small = Snapshot(player, 0.15);
                drawn = small != null && AvgBright(small) > Math.Max(6, 0.35 * holdBright);
            }
            catch { drawn = true; }
            if (drawn || holdSw.Elapsed.TotalSeconds > 0.6)
            {
                DebugLog("hold released after " + holdSw.ElapsedMilliseconds + " ms" + (drawn ? "" : " (timeout)"));
                ClearHold();
            }
        }

        /// Average brightness (0-255) of a sparse grid of pixels.
        static double AvgBright(BitmapSource b)
        {
            int w = b.PixelWidth, h = b.PixelHeight;
            var px = new byte[4];
            double sum = 0; int n = 0;
            for (int y = h / 8; y < h; y += Math.Max(1, h / 8))
                for (int x = w / 8; x < w; x += Math.Max(1, w / 8))
                {
                    b.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                    sum += (px[0] + px[1] + px[2]) / 3.0; n++;
                }
            return n > 0 ? sum / n : 0;
        }

        void ClearHold()
        {
            holdActive = false;
            holdSw.Reset();
            if (holdImage == null) return;
            holdImage.Source = null;
            holdImage.Visibility = Visibility.Collapsed;
        }
        double startLatency = 0.1;
        readonly Stopwatch handSw = new Stopwatch();

        void BeginHandover(bool xf)
        {
            handing = !xf; crossfading = xf;
            oldFrozen = false; latencyMeasured = xf;   // crossfade learns its delay from the first real picture
            xfDrawn = false;
            if (xf) { try { var sn = Snapshot(player, 0.15); if (sn != null) xfRefBright = AvgBright(sn); } catch { } }
            standby.IsMuted = p1.IsMuted;
            standby.Volume = 0;
            // crossfade: wait fully opaque UNDER the old player (covered, so invisible but still capturable);
            // it moves on top and fades in from 0 once it shows a real picture
            if (xf) { standby.Opacity = 1; Panel.SetZIndex(standby, 0); Panel.SetZIndex(player, 1); }
            else { standby.Opacity = 1; Panel.SetZIndex(standby, 0); Panel.SetZIndex(player, 1); }  // waits underneath
            handSw.Restart();
            standby.Play();
        }

        // Runs every rendered frame while loop preview plays (only then - keeps idle CPU at zero).
        void LoopFrame(object sender, EventArgs e)
        {
            if (!playing || info == null) return;
            CheckHold();
            double t = player.Position.TotalSeconds;
            double half = frameDur * 0.5;
            bool moving = false;
            double nextT = 0;
            if (handing || crossfading)
            {
                nextT = standby.Position.TotalSeconds;
                // the clock starts a moment before the first picture is drawn: wait ~2 frames of real motion
                moving = nextT > inT + 2 * frameDur;
                if (moving && !latencyMeasured)
                {
                    latencyMeasured = true;
                    startLatency = Math.Max(0.02, Math.Min(0.3, 0.6 * startLatency + 0.4 * handSw.Elapsed.TotalSeconds));
                }
                // hold the old player on its last selected frame (just before the file's end, so it never "ends")
                if (!oldFrozen && t >= Math.Min(outT - half, info.Duration - frameDur))
                {
                    player.Pause(); oldFrozen = true;
                    if (!crossfading) HoldFrame(player);   // crossfade: the fade itself covers the handover
                    DebugLog("freeze at " + t.ToString("0.000"));
                }
            }

            if (loopMode == 1)
            {
                if (!handing && t >= outT - half - startLatency) BeginHandover(false);
                else if (handing && oldFrozen && (moving || handSw.Elapsed.TotalSeconds > 0.8)) Reveal();
                return;
            }

            double f = EffFade;
            if (!crossfading && t >= outT - f - startLatency) BeginHandover(true);
            if (crossfading)
            {
                // The incoming video stays invisible until it really shows a picture (its first ~100 ms can be
                // black), then the fade runs on its own clock - so the blend never mixes in black.
                if (!xfDrawn && moving)
                {
                    double nb = -1;
                    try { var sn = Snapshot(standby, 0.15); if (sn != null) nb = AvgBright(sn); } catch { }
                    if (nb > Math.Max(6, 0.35 * xfRefBright) || handSw.Elapsed.TotalSeconds > 0.6)
                    {
                        xfDrawn = true; xfBase = nextT;
                        startLatency = Math.Max(0.02, Math.Min(0.3, 0.6 * startLatency + 0.4 * handSw.Elapsed.TotalSeconds));
                        DebugLog("xf drawn after " + handSw.ElapsedMilliseconds + " ms, bright " + nb.ToString("0.0") + " ref " + xfRefBright.ToString("0.0"));
                    }
                }
                double a = xfDrawn ? Math.Max(0, Math.Min(1, (nextT - xfBase) / f)) : 0;
                if (xfDrawn)
                {
                    standby.Opacity = a;
                    if (Panel.GetZIndex(standby) != 2) Panel.SetZIndex(standby, 2);
                }
                standby.Volume = a;
                player.Volume = 1 - a;
                if (a >= 1) Reveal();
            }
        }

        static readonly string dbg = Environment.GetEnvironmentVariable("VC_DEBUG");
        static void DebugLog(string m) { if (dbg != null) try { File.AppendAllText(dbg, DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + "\r\n"); } catch { } }

        // The next cycle is moving: make it the active player.
        void Reveal()
        {
            DebugLog("reveal " + (standby == p1 ? "p1" : "p2") + " at " + standby.Position.TotalSeconds.ToString("0.000") + ", old " + player.Position.TotalSeconds.ToString("0.000") + (oldFrozen ? " frozen" : ""));
            var next = standby;
            if (holdActive) holdSw.Restart();     // start watching for the new player's first real picture
            next.Opacity = 1; next.Volume = 1;
            Panel.SetZIndex(next, 2);
            Retire(player);
            player = next;
            Panel.SetZIndex(player, 1);
            handing = crossfading = oldFrozen = false;
            pos = player.Position.TotalSeconds;
            RefreshTimes();
        }

        // The outgoing player becomes the new standby: hidden, silent, parked at START.
        void Retire(MediaElement old)
        {
            old.Opacity = 0;
            old.Volume = 0;
            Panel.SetZIndex(old, 0);
            // No Stop() here: it resets the shared video device and makes the visible player flash black.
            endedPlayer = null;
            old.Pause();
            old.Position = TimeSpan.FromSeconds(inT);
            standby = old;
        }

        // ---------- Frame-exact seam preview ----------
        // Renders "2 s before END -> jump/crossfade -> 2 s after START" with FFmpeg (same method as the loop
        // export) and plays that clip straight through - no player switching, so every frame is exact.

        const double SeamLead = 2.0;
        MediaElement seamPlayer;
        bool seamActive, seamRendering;
        double seamTailLen, seamHeadFrom;
        string seamBadgeText; Visibility seamBadgeVis;
        readonly DispatcherTimer seamTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        static readonly string CacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-cache");

        async void PlaySeam()
        {
            if (info == null || seamRendering) return;
            if (seamActive) EndSeam();
            if (loopMode == 3) { PlayMirror(); return; }
            if (loopMode == 0) rbLoopPlain.IsChecked = true;
            Pause();
            var job = new ExportJob { Info = info, In = inT, Out = outT, Keyframes = keyframes, Fade = loopMode == 2 ? EffFade : 0 };
            string key = string.Format(Tools.Inv, "{0}|{1}|{2:0.000000}|{3:0.000000}|{4:0.000}", info.Path, info.Size, inT, outT, job.Fade);
            string file = Path.Combine(CacheDir, "seam-" + ((uint)key.GetHashCode()).ToString("x8") + ".mp4");
            if (!File.Exists(file))
            {
                seamRendering = true;
                txtStatus.Text = "Rendering seam preview...";
                int token = openToken;
                bool ok = await Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        string tmp = file + ".part.mp4";
                        try { Tools.RunText(Tools.FFmpeg, job.SeamPreviewArgs(tmp, SeamLead, true)); }
                        catch { Tools.RunText(Tools.FFmpeg, job.SeamPreviewArgs(tmp, SeamLead, false)); }   // no GPU: CPU
                        File.Move(tmp, file);
                        return true;
                    }
                    catch { return false; }
                });
                seamRendering = false;
                if (token != openToken) return;
                if (!ok) { txtStatus.Text = "Couldn't render the seam preview."; return; }
                txtStatus.Text = "Ready";
            }
            job.SeamLayout(SeamLead, out seamTailLen, out seamHeadFrom);
            StartRendered(file, "Seam preview - exact frames, " + (job.Fade > 0 ? job.Fade.ToString("0.0##", Tools.Inv) + " s crossfade" : "plain jump") +
                                " (as exported) - repeats, Space to stop", false);
        }

        /// Plays a rendered preview clip on top of the normal preview, repeating until stopped.
        void StartRendered(string file, string badge, bool mirror)
        {
            seamMirror = mirror;
            seamActive = true;
            seamBadgeText = badgeText.Text; seamBadgeVis = previewBadge.Visibility;
            badgeText.Text = badge + (Math.Abs(speed - 1) > 0.001 ? "  ·  " + txtSpeed.Text : "");
            badgeText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0xB2, 0xFF));
            previewBadge.Visibility = Visibility.Visible;
            seamPlayer.IsMuted = p1.IsMuted;
            seamPlayer.Volume = 1;
            seamPlayer.Source = new Uri(file);
            seamPlayer.Visibility = Visibility.Visible;
            ApplySpeed();
            seamPlayer.Play();
            btnPlay.Content = "";
            seamTick.Start();
        }

        // ---------- Mirror preview ----------
        // Windows' player can't play backward, so the mirrored cycle (forward, then backward) is rendered
        // with the same steps as the export (preview quality) and played on repeat.

        bool seamMirror;
        int mirrorFrames;      // source frames in the selection (forward part)

        async void PlayMirror()
        {
            if (info == null || seamRendering) return;
            if (seamActive) EndSeam();
            Pause();
            int n = (int)Math.Round((outT - inT) * info.Fps);
            if (n < 3) { txtStatus.Text = "The selection is too short for a mirror loop."; return; }
            var job = new ExportJob { Info = info, In = inT, Out = outT, Keyframes = keyframes, Mirror = true, Preview = true };
            string key = string.Format(Tools.Inv, "mirror|{0}|{1}|{2:0.000000}|{3:0.000000}", info.Path, info.Size, inT, outT);
            string file = Path.Combine(CacheDir, "mirror-" + ((uint)key.GetHashCode()).ToString("x8") + ".mp4");
            if (!File.Exists(file))
            {
                seamRendering = true;
                txtStatus.Text = "Rendering mirror preview...";
                int token = openToken;
                bool ok = await Task.Run(() =>
                {
                    var temps = new List<string>();
                    string tmp = file + ".part.mp4";
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        try { foreach (var s in job.UnitSteps(tmp, true, temps)) Tools.RunText(Tools.FFmpeg, s.Key); }
                        catch { foreach (var s in job.UnitSteps(tmp, false, temps)) Tools.RunText(Tools.FFmpeg, s.Key); }   // no GPU: CPU
                        File.Move(tmp, file);
                        return true;
                    }
                    catch { return false; }
                    finally { foreach (var t in temps) try { File.Delete(t); } catch { } }
                });
                seamRendering = false;
                if (token != openToken) return;
                if (!ok) { txtStatus.Text = "Couldn't render the mirror preview."; return; }
                txtStatus.Text = "Ready";
            }
            mirrorFrames = n;
            StartRendered(file, "Mirror preview - forward, then backward (rendered) - repeats, Space to stop", true);
        }

        void SeamTick()
        {
            if (!seamActive || seamHolding) return;
            double c = seamPlayer.Position.TotalSeconds;
            double dur = seamPlayer.NaturalDuration.HasTimeSpan ? seamPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0;
            // the player doesn't always report "ended" - stop on our own at the last frame
            if (dur > 0 && c >= dur - (seamMirror ? 0.02 : 0.06)) { SeamToEnd(); return; }
            if (c > 0) pos = SeamMap(c);
            RefreshTimes();
        }

        /// Clip time -> source time (for the playhead).
        double SeamMap(double c)
        {
            double t;
            if (seamMirror)
            {   // forward through the selection, then back again
                double f = c * info.Fps;
                t = f < mirrorFrames ? inT + c : inT + (2 * mirrorFrames - 2 - f) / info.Fps;
            }
            else t = c < seamTailLen ? outT - seamTailLen + c : inT + seamHeadFrom + (c - seamTailLen);
            return Math.Max(0, Math.Min(info.Duration, t));
        }

        // At the end of the clip: hold the last frame for half a second, then play it again.
        // Repeats until the user pauses (or does anything else). The hold makes the restart
        // clearly different from the loop point itself.
        const double SeamHold = 0.5;
        bool seamHolding;
        readonly DispatcherTimer seamHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SeamHold) };

        void SeamToEnd()
        {
            if (!seamActive || seamHolding) return;
            seamHolding = true;
            bool ended = !seamPlayer.NaturalDuration.HasTimeSpan || seamPlayer.Position >= seamPlayer.NaturalDuration.TimeSpan;
            seamPlayer.Pause();
            seamEndedState = ended;
            if (seamPlayer.NaturalDuration.HasTimeSpan) pos = SnapFrame(SeamMap(seamPlayer.NaturalDuration.TimeSpan.TotalSeconds - frameDur));
            RefreshTimes();
            // mirror: the restart is just the turn at START, so no hold
            seamHoldTimer.Interval = seamMirror ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(SeamHold);
            seamHoldTimer.Start();
        }
        bool seamEndedState;

        void SeamRestart()
        {
            seamHoldTimer.Stop();
            if (!seamActive) return;
            if (seamEndedState) seamPlayer.Stop();   // a player that reached the very end needs a stop to rewind
            seamPlayer.Position = TimeSpan.Zero;
            ApplySpeed();
            seamPlayer.Play();
            seamHolding = false;
        }

        void EndSeam()
        {
            if (!seamActive) return;
            seamActive = false;
            seamHolding = false;
            seamHoldTimer.Stop();
            seamTick.Stop();
            seamPlayer.Stop();
            seamPlayer.Close();
            seamPlayer.Source = null;
            seamPlayer.Visibility = Visibility.Collapsed;
            badgeText.Text = seamBadgeText;
            badgeText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xB0, 0x50));
            previewBadge.Visibility = seamBadgeVis;
            btnPlay.Content = "";
            // leave the normal preview on the frame where the seam clip stopped
            if (!stillMode) { pendingSeek = pos; seekTimer.Start(); }
            else Grab(pos);
            RefreshTimes();
        }

        static void ClearCache()
        {
            try { if (Directory.Exists(CacheDir)) foreach (var f in Directory.GetFiles(CacheDir)) try { File.Delete(f); } catch { } } catch { }
        }

        // ---------- In / Out ----------

        void SetIn(double t)
        {
            if (info == null) return;
            t = SnapFrame(t);
            inT = Math.Min(t, Math.Max(0, outT - frameDur));
            if (loopMode > 0) ParkStandby();
            RefreshTimes(); UpdateNote();
        }

        void SetOut(double t)
        {
            if (info == null) return;
            t = SnapFrame(t);
            outT = Math.Max(t, Math.Min(info.Duration, inT + frameDur));
            RefreshTimes(); UpdateNote();
        }

        void TimeBoxKey(TextBox box, KeyEventArgs e, bool isIn)
        {
            if (e.Key == Key.Enter) { CommitBox(box, isIn); Window.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { RefreshTimes(); Window.Focus(); e.Handled = true; }
        }

        void CommitBox(TextBox box, bool isIn)
        {
            if (info == null) return;
            double t;
            if (Tools.TryParseTime(box.Text, out t))
            {
                t = Math.Min(t, info.Duration);
                // unchanged text (e.g. focus just moved away): do nothing, don't interrupt playback
                if (Math.Abs(SnapFrame(t) - (isIn ? inT : outT)) < 0.0005 || Tools.Fmt(isIn ? inT : outT) == box.Text.Trim()) { RefreshTimes(); return; }
                if (isIn) SetIn(t); else SetOut(t);
                Pause(); Seek(isIn ? inT : outT);
            }
            RefreshTimes();
        }

        void RefreshTimes()
        {
            txtPos.Text = Tools.Fmt(pos);
            if (!tbIn.IsKeyboardFocused) tbIn.Text = Tools.Fmt(inT);
            if (!tbOut.IsKeyboardFocused) tbOut.Text = Tools.Fmt(outT);
            txtLen.Text = Tools.Fmt(outT - inT);
            timeline.Position = pos; timeline.In = inT; timeline.Out = outT;
            timeline.Fade = loopMode == 2 ? EffFade : 0;
            timeline.InvalidateVisual();
        }

        void UpdateNote()
        {
            if (txtModeNote == null) return;
            if (rbCopy.IsChecked == true)
            {
                string s = "Instant, 100% original quality. A copy cut can only start on a keyframe, so the start snaps back to the nearest keyframe before START. Not for loops or joining clips (the end can drop or add a frame or two) - use Frame-accurate or Looped to → 1 cycle for that.";
                if (info != null)
                {
                    if (keyframes == null) s += "  (Finding keyframes...)";
                    else
                    {
                        var j = new ExportJob { Info = info, In = inT, Out = outT, Keyframes = keyframes };
                        double extra = inT - j.CopyStart;
                        s += extra < 0.001 ? "  Your START is on a keyframe - the cut will be exact."
                                           : "  This cut will start at " + Tools.Fmt(j.CopyStart) + " (" + extra.ToString("0.###", Tools.Inv) + " s earlier).";
                    }
                }
                if (Math.Abs(speed - 1) > 0.001) s = "Speed " + txtSpeed.Text + " needs re-encoding - use Frame-accurate or Looped to (you'll be asked to switch). " + s;
                txtModeNote.Text = s;
            }
            else if (rbLoopExport.IsChecked == true)
            {
                if (oneCycle) ShowLoopLen();
                int cycles; double length;
                LoopPlan(out cycles, out length);
                string style = loopMode == 2 ? EffFade.ToString("0.0##", Tools.Inv) + " s crossfade at each join (video starts at START + fade)"
                             : loopMode == 3 ? "mirror: forward, then backward - the end turns back into the start, no jump"
                                             : "frame-accurate jump END → START (pick Crossfade loop or Mirror above to hide it)";
                if (Math.Abs(speed - 1) > 0.001) style += ", at " + txtSpeed.Text + " speed (audio keeps its pitch)";
                string s;
                if (info == null) s = "Repeats the selection to the length you type.";
                else if (cycles == 1)
                    s = "Exactly ONE loop cycle (" + Tools.Fmt(length) + "), " + style + ". Repeat this file (FFmpeg -stream_loop -1, OBS loop, an editor) and it loops seamlessly.";
                else if (cycles > 1)
                    s = cycles + " whole loop cycles = " + Tools.Fmt(length) + ", " + style + ". Ends on a full cycle, so the file itself also loops seamlessly when repeated.";
                else
                    s = "Repeats until " + Tools.Fmt(length) + ", " + style + ". Ends mid-cycle: tick Whole cycles if you'll repeat this file.";
                if (info != null)
                {
                    bool gpu = ExportJob.HasGpuEncoder(info.Video.Codec);
                    s += " Video: one cycle encoded as " + info.Video.Codec.ToUpperInvariant() + (gpu ? " on the GPU" : "") + " matching the source, then repeated without re-encoding." +
                         " Audio encoded once (same codec/bitrate). Subtitles left out.";
                }
                txtModeNote.Text = s;
            }
            else
            {
                string s = "Cuts exactly on the START and END frames. Video is re-encoded";
                if (info != null)
                {
                    bool gpu = ExportJob.HasGpuEncoder(info.Video.Codec);
                    s += " as " + info.Video.Codec.ToUpperInvariant() + (gpu ? " on the NVIDIA GPU" : "") +
                         " at ~" + Tools.Rate((long)(info.VideoBitRate * 1.1)) + " with the same resolution, fps and pixel format";
                }
                s += ". Audio is re-encoded in the same codec and bitrate so the file starts at 0 and audio matches the video exactly - clean for joining.";
                if (Math.Abs(speed - 1) > 0.001) s += " Speed " + txtSpeed.Text + ": same frame rate, audio keeps its pitch; subtitles are left out.";
                if (loopMode >= 2) s += " This is one plain cut - the " + (loopMode == 2 ? "crossfade" : "mirror") + " style applies to Looped to.";
                txtModeNote.Text = s;
            }
        }

        // ---------- Export ----------

        async void Export()
        {
            if (info == null || exporter != null) return;
            EndSeam();
            Pause();
            if (outT - inT < frameDur / 2) { MessageBox.Show(Window, "The selection is empty. Set START before END.", "Video Cutter"); return; }
            bool exact = rbExact.IsChecked == true;
            bool loop = rbLoopExport.IsChecked == true;
            if (!loop && ((!exact && loopMode > 0) || (exact && loopMode >= 2)))
            {
                string why = !exact
                    ? "Fast lossless cuts aren't loop-ready: they can start a little late and drop or add a couple of frames at the end, so repeating the file shows a jump."
                    : "Frame-accurate exports one plain cut, without the " + (loopMode == 2 ? "crossfade" : "mirror (backward part)") + " you're previewing.";
                var r = MessageBox.Show(Window,
                    "You're previewing a loop. " + why + "\n\n" +
                    "Export exactly ONE loop cycle instead? It repeats seamlessly, just like the preview.\n\n" +
                    "Yes = one loop cycle      No = " + (exact ? "plain Frame-accurate cut" : "Fast lossless cut") + " anyway",
                    "Video Cutter", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes) { SetOneCycle(); loop = true; }
            }
            if (!exact && !loop && Math.Abs(speed - 1) > 0.001)
            {
                var r = MessageBox.Show(Window,
                    "Speed " + txtSpeed.Text + " needs re-encoding, which Fast lossless can't do.\n\nExport Frame-accurate at " + txtSpeed.Text + " instead?\n\n" +
                    "Yes = Frame-accurate at " + txtSpeed.Text + "      No = Fast lossless at normal speed",
                    "Video Cutter", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes) { exact = true; rbExact.IsChecked = true; }
            }
            if (!exact && !loop && keyframes == null) { MessageBox.Show(Window, "Still finding keyframes - try again in a moment.", "Video Cutter"); return; }
            if (loop) CommitLoopLen();

            string ext = Path.GetExtension(info.Path);
            string dir = Path.GetDirectoryName(info.Path);
            string baseName = Path.GetFileNameWithoutExtension(info.Path) + (loop ? "_loop" : "_cut");
            string name = baseName + ext;
            for (int n = 2; File.Exists(Path.Combine(dir, name)); n++) name = baseName + " (" + n + ")" + ext;

            var d = new Microsoft.Win32.SaveFileDialog();
            d.Title = "Save cut video";
            d.InitialDirectory = dir;
            d.FileName = name;
            d.DefaultExt = ext;
            d.Filter = ext.TrimStart('.').ToUpperInvariant() + " video|*" + ext;
            d.OverwritePrompt = true;   // never overwrite silently
            if (d.ShowDialog(Window) != true) return;
            string output = d.FileName;
            if (!string.Equals(Path.GetExtension(output), ext, StringComparison.OrdinalIgnoreCase)) output += ext;
            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(info.Path), StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(Window, "Choose a different name - the source file can't be overwritten.", "Video Cutter"); return; }

            var job = new ExportJob { Info = info, Output = output, In = inT, Out = outT, Exact = exact, Keyframes = keyframes };
            if (exact || loop) job.Speed = speed;
            if (loop)
            {
                int cycles; double length;
                LoopPlan(out cycles, out length);
                job.Loop = true; job.LoopLength = length; job.Cycles = cycles;
                job.Fade = loopMode == 2 ? EffFade : 0;
                job.Mirror = loopMode == 3;
            }
            var ex = new Exporter();
            exporter = ex;
            btnExport.IsEnabled = false; rbCopy.IsEnabled = rbExact.IsEnabled = rbLoopExport.IsEnabled = tbLoopLen.IsEnabled = false;
            btnCancel.Visibility = Visibility.Visible; btnShow.Visibility = Visibility.Collapsed;
            progress.Value = 0;
            txtStatus.Text = loop ? "Rendering loop..." : exact ? "Encoding..." : "Copying...";
            var sw = Stopwatch.StartNew();
            ex.Progress += (f, rate) => Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                progress.Value = f;
                txtStatus.Text = (f * 100).ToString("0", Tools.Inv) + "%" + (rate.Length > 0 && rate != "N/A" ? "  ·  " + rate : "");
            }));
            ex.Status += m => Window.Dispatcher.BeginInvoke(new Action(() => txtStatus.Text = m));

            bool ok = await Task.Run(() => ex.Run(job));

            exporter = null;
            btnExport.IsEnabled = true; rbCopy.IsEnabled = rbExact.IsEnabled = rbLoopExport.IsEnabled = tbLoopLen.IsEnabled = true;
            btnCancel.Visibility = Visibility.Collapsed;
            if (ok)
            {
                progress.Value = 1;
                lastExport = output;
                btnShow.Visibility = Visibility.Visible;
                long size = new FileInfo(output).Length;
                txtStatus.Text = "Done in " + sw.Elapsed.TotalSeconds.ToString("0.0", Tools.Inv) + " s  ·  " + Tools.Size(size) +
                                 (exact || loop ? "  ·  " + ex.UsedEncoder : "");
            }
            else
            {
                progress.Value = 0;
                txtStatus.Text = ex.LastError == "Cancelled." ? "Cancelled" : "Export failed";
                if (ex.LastError != "Cancelled.")
                    MessageBox.Show(Window, "Export failed:\n\n" + ex.LastError, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- Keyboard ----------

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (ctrl && e.Key == Key.O) { OpenDialog(); e.Handled = true; return; }
            if (info == null) return;
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Left: if (ctrl) JumpKey(-1); else if (shift) StepSeconds(-1); else Step(-1); break;
                case Key.Right: if (ctrl) JumpKey(1); else if (shift) StepSeconds(1); else Step(1); break;
                case Key.I: SetIn(pos); break;
                case Key.O: SetOut(pos); break;
                case Key.P: PlaySelection(); break;
                case Key.L: CycleLoop(); break;
                case Key.S: PlaySeam(); break;
                case Key.OemOpenBrackets: NudgeSpeed(-1); break;
                case Key.OemCloseBrackets: NudgeSpeed(1); break;
                case Key.M: ToggleMute(); break;
                case Key.Home: Pause(); Seek(0); break;
                case Key.End: Pause(); Seek(info.Duration); break;
                case Key.E: if (ctrl) Export(); else return; break;
                default: return;
            }
            e.Handled = true;
        }
    }
}
