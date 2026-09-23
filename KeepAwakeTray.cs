using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// KeepAwakeTray v2: system-tray icon that keeps the Win11 session awake by
// injecting a (0,0) mouse move every ~50s (resets the inactivity timer WITHOUT
// moving the cursor and WITHOUT keystrokes -- unlike Caffeine's F15 '~' leak).
//
// Mechanism notes:
//  - The mouse "nudge" defeats input-idle locks (secure screensaver, Machine
//    inactivity limit, Dynamic Lock). It does NOT defeat presence-sensor
//    "lock on leave".
//  - SetThreadExecutionState(SYSTEM|DISPLAY) is added to robustly block sleep /
//    display-off while pulsing. It does NOT prevent the lock screen, so it is
//    complementary to the nudge.
//
// v2 fixes over v1 (high + medium issues):
//  - MakeIcon no longer leaks the HICON/Bitmap (DestroyIcon + using + Clone).
//  - DPI aware: crisp tray icon + Schedule dialog on scaled laptops.
//  - start == end is now treated as "all day (24h)" instead of "never".
//  - SendInput uses SetLastError + return-value check + MOVE_NOCOALESCE.
//  - SetThreadExecutionState keeps sleep/display-off blocked while pulsing.
//  - Single-instance guard (per-session Mutex).
//  - Config file location is configurable:  -config <dir|file.ini>  (default ./).
//  - WeekdaysOnly hand-edit parsing no longer silently flips to false.
//  - tray / timer / icons / menu are disposed deterministically.
//
// Usage:  KeepAwakeTray.exe [-keepon] [-config <dir|file.ini>]
//   -keepon          start in "Keep Active" (schedule bypass), not persisted.
//   -config <path>   ini location; a directory or a *.ini file; env vars allowed
//                    (e.g. -config "%APPDATA%\KeepAwakeTray"). Default: next to exe.
//
// Build (.NET Framework, no project needed):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe ^
//     /out:KeepAwakeTray.exe ^
//     /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll ^
//     KeepAwakeTray.cs

namespace KeepAwakeTray {
    static class Program {
        static System.Threading.Mutex mutex;   // kept alive for process lifetime

        [STAThread]
        static void Main(string[] args) {
            bool createdNew;
            mutex = new System.Threading.Mutex(true, @"Local\KeepAwakeTray.SingleInstance", out createdNew);
            if (!createdNew) return;   // another instance already runs in this session

            TrySetDpiAware();          // must precede any window/GDI to take effect
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool keepOn = Array.IndexOf(args, "-keepon") >= 0;
            string iniPath = ResolveConfigPath(args);

            Application.Run(new TrayApp(keepOn, iniPath));
            GC.KeepAlive(mutex);
        }

        // ---- config path resolution -------------------------------------------
        // Default: KeepAwakeTray.ini next to the exe ("./").
        // Override:  -config <dir|file.ini>   (env vars expanded; relative -> exe dir)
        static string ResolveConfigPath(string[] args) {
            string baseDir = Application.StartupPath;
            string custom = GetArgValue(args, "-config");
            if (string.IsNullOrEmpty(custom))
                return Path.Combine(baseDir, "KeepAwakeTray.ini");

            string p = Environment.ExpandEnvironmentVariables(custom.Trim().Trim('"'));
            if (!Path.IsPathRooted(p)) p = Path.Combine(baseDir, p);
            if (p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) return p;   // explicit file
            return Path.Combine(p, "KeepAwakeTray.ini");                            // directory
        }

        static string GetArgValue(string[] args, string key) {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        // ---- DPI awareness (best effort, newest API first) --------------------
        static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value); // 2 = per-monitor
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        static void TrySetDpiAware() {
            try { if (SetProcessDpiAwarenessContext(DPI_PER_MONITOR_AWARE_V2)) return; } catch { }
            try { SetProcessDpiAwareness(2); return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }
    }

    public class TrayApp : ApplicationContext {
        // ---- Win32 interop ----------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT {
            public int dx; public int dy; public uint mouseData;
            public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint esFlags);
        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        const uint INPUT_MOUSE = 0;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_MOVE_NOCOALESCE = 0x2000;
        const uint ES_CONTINUOUS = 0x80000000;
        const uint ES_SYSTEM_REQUIRED = 0x00000001;
        const uint ES_DISPLAY_REQUIRED = 0x00000002;
        const int  IntervalMs = 50000;        // pulse every 50 seconds

        static readonly int InputSize = Marshal.SizeOf(typeof(INPUT));

        // ---- UI / state -------------------------------------------------------
        NotifyIcon tray;
        ContextMenuStrip menu;
        Timer timer;
        Icon iconOn, iconWait, iconOff;
        ToolStripMenuItem miToggle, miKeep, miResumeNow, miSkins;
        readonly List<Image> skinThumbnails = new List<Image>();
        Timer snoozeTimer;
        DateTime? snoozeUntil;

        readonly string iniPath;
        readonly string skinsDir;
        string currentSkin = "Classic";
        bool active = true;
        bool bypassSchedule = false;          // "Keep Active": session override, not persisted
        TimeSpan startT = new TimeSpan(8, 0, 0);
        TimeSpan endT   = new TimeSpan(19, 0, 0);
        bool weekdaysOnly = true;

        bool keepAwakeAsserted = false;       // tracks SetThreadExecutionState state
        bool disposed = false;

        public TrayApp(bool keepOn, string configPath) {
            iniPath = configPath;
            string configDir = Path.GetDirectoryName(iniPath);
            skinsDir = string.IsNullOrEmpty(configDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skins")
                : Path.Combine(configDir, "skins");

            LoadConfig();
            if (keepOn) { bypassSchedule = true; active = true; }

            LoadSkin(currentSkin);

            miToggle = new ToolStripMenuItem("Active", null, OnToggle); miToggle.Checked = active;
            miKeep   = new ToolStripMenuItem("Keep Active (ignore schedule)", null, OnKeepActive); miKeep.Checked = bypassSchedule;

            ToolStripMenuItem miSnooze = new ToolStripMenuItem("Pause for...");
            miSnooze.DropDownItems.Add(new ToolStripMenuItem("5 minutes", null, delegate { StartSnooze(TimeSpan.FromMinutes(5)); }));
            miSnooze.DropDownItems.Add(new ToolStripMenuItem("10 minutes", null, delegate { StartSnooze(TimeSpan.FromMinutes(10)); }));
            miSnooze.DropDownItems.Add(new ToolStripMenuItem("30 minutes", null, delegate { StartSnooze(TimeSpan.FromMinutes(30)); }));
            miSnooze.DropDownItems.Add(new ToolStripSeparator());
            miSnooze.DropDownItems.Add(new ToolStripMenuItem("Custom (hours)...", null, OnCustomSnoozeHours));

            miResumeNow = new ToolStripMenuItem("Resume now", null, OnResumeNow);
            miResumeNow.Visible = false;

            miSkins = new ToolStripMenuItem("Skins");
            ToolStripDropDownMenu skinsDrop = (ToolStripDropDownMenu)miSkins.DropDown;
            skinsDrop.ShowCheckMargin = true;
            skinsDrop.ShowImageMargin = true;
            PopulateSkinsMenu();

            ToolStripMenuItem miConfig = new ToolStripMenuItem("Schedule...", null, OnConfig);
            ToolStripMenuItem miExit   = new ToolStripMenuItem("Exit", null, OnExit);

            menu = new ContextMenuStrip();
            menu.ShowCheckMargin = true;
            menu.ShowImageMargin = false;
            menu.Items.AddRange(new ToolStripItem[] {
                miToggle, miKeep, miSnooze, miResumeNow, new ToolStripSeparator(),
                miSkins, miConfig, new ToolStripSeparator(), miExit
            });
            menu.Opening += OnMenuOpening;

            ApplyThemeToMenus();

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += OnToggle;

            timer = new Timer();
            timer.Interval = IntervalMs;
            timer.Tick += OnTick;
            timer.Start();

            Reevaluate();   // initial pulse / keep-awake / icon
        }

        bool InWindow() {
            DateTime now = DateTime.Now;
            if (weekdaysOnly && (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday))
                return false;
            if (startT == endT) return true;              // equal start/end = all day (24h)
            TimeSpan t = now.TimeOfDay;
            if (startT < endT) return t >= startT && t < endT;   // normal window
            return t >= startT || t < endT;                      // window crossing midnight
        }

        bool ShouldPulse() { return active && (bypassSchedule || InWindow()); }

        // Single place that applies current state: pulse, keep-awake hint, icon.
        void Reevaluate() {
            bool pulse = ShouldPulse();
            ApplyKeepAwake(pulse);
            if (pulse) Nudge();
            UpdateIcon();
        }

        void OnTick(object sender, EventArgs e) { Reevaluate(); }

        void OnToggle(object sender, EventArgs e) {
            active = !active;
            if (!active) { bypassSchedule = false; }  // pausing clears bypass
            CancelSnooze();
            Reevaluate();
            UpdateMenuStates();
        }

        void OnKeepActive(object sender, EventArgs e) {
            bypassSchedule = !bypassSchedule;
            if (bypassSchedule) { active = true; }    // "Keep Active" implies active
            CancelSnooze();
            Reevaluate();
            UpdateMenuStates();
        }

        void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e) {
            ApplyThemeToMenus();
            UpdateMenuStates();
            PopulateSkinsMenu();
        }

        void UpdateMenuStates() {
            if (miToggle != null) miToggle.Checked = active;
            if (miKeep != null) miKeep.Checked = bypassSchedule;
            if (miResumeNow != null) {
                if (snoozeUntil.HasValue) {
                    miResumeNow.Visible = true;
                    string untilStr = (snoozeUntil.Value.Date == DateTime.Today)
                        ? snoozeUntil.Value.ToString("HH:mm")
                        : snoozeUntil.Value.ToString("dd/MM HH:mm");
                    miResumeNow.Text = "Resume now (paused until " + untilStr + ")";
                } else {
                    miResumeNow.Visible = false;
                }
            }
        }

        void StartSnooze(TimeSpan duration) {
            CancelSnoozeTimer();
            snoozeUntil = DateTime.Now.Add(duration);
            active = false;
            bypassSchedule = false;

            snoozeTimer = new Timer();
            long ms = (long)duration.TotalMilliseconds;
            if (ms > int.MaxValue) ms = int.MaxValue;
            if (ms < 1000) ms = 1000;
            snoozeTimer.Interval = (int)ms;
            snoozeTimer.Tick += OnSnoozeExpired;
            snoozeTimer.Start();

            Reevaluate();
            UpdateMenuStates();

            string untilStr = (snoozeUntil.Value.Date == DateTime.Today)
                ? snoozeUntil.Value.ToString("HH:mm")
                : snoozeUntil.Value.ToString("dd/MM HH:mm");
            if (tray != null)
                tray.ShowBalloonTip(3000, "KeepAwake",
                    "Paused for " + FormatDuration(duration) + " (until " + untilStr + ")",
                    ToolTipIcon.Info);
        }

        void OnSnoozeExpired(object sender, EventArgs e) {
            if (snoozeUntil.HasValue && DateTime.Now < snoozeUntil.Value) {
                TimeSpan rem = snoozeUntil.Value - DateTime.Now;
                long ms = (long)rem.TotalMilliseconds;
                if (ms > 1000) {
                    snoozeTimer.Interval = (int)Math.Min(int.MaxValue, ms);
                    return;
                }
            }
            CancelSnoozeTimer();
            snoozeUntil = null;
            active = true;
            Reevaluate();
            UpdateMenuStates();

            if (tray != null)
                tray.ShowBalloonTip(3000, "KeepAwake",
                    "Pause ended. Keep-awake resumed.",
                    ToolTipIcon.Info);
        }

        void OnResumeNow(object sender, EventArgs e) {
            CancelSnooze();
            active = true;
            Reevaluate();
            UpdateMenuStates();
        }

        void CancelSnooze() {
            CancelSnoozeTimer();
            snoozeUntil = null;
        }

        void CancelSnoozeTimer() {
            if (snoozeTimer != null) {
                snoozeTimer.Stop();
                snoozeTimer.Dispose();
                snoozeTimer = null;
            }
        }

        static string FormatDuration(TimeSpan ts) {
            if (ts.TotalHours >= 1) {
                double h = Math.Round(ts.TotalHours, 1);
                return h.ToString(CultureInfo.InvariantCulture) + (h == 1 ? " hour" : " hours");
            }
            return ((int)ts.TotalMinutes) + " min";
        }

        void UpdateIcon() {
            string win = (startT == endT) ? "24h"
                : string.Format("{0:00}:{1:00}-{2:00}:{3:00}", startT.Hours, startT.Minutes, endT.Hours, endT.Minutes);
            Icon targetIcon;
            string targetText;

            if (!active) {
                targetIcon = iconOff;
                if (snoozeUntil.HasValue) {
                    string until = (snoozeUntil.Value.Date == DateTime.Today)
                        ? snoozeUntil.Value.ToString("HH:mm")
                        : snoozeUntil.Value.ToString("dd/MM HH:mm");
                    targetText = "KeepAwake: paused until " + until;
                } else {
                    targetText = "KeepAwake: paused";
                }
            } else if (bypassSchedule) {
                targetIcon = iconOn;   targetText = "KeepAwake: active (Keep Active)";
            } else if (InWindow()) {
                targetIcon = iconOn;   targetText = "KeepAwake: active (" + win + ")";
            } else {
                targetIcon = iconWait; targetText = "KeepAwake: waiting, outside " + win;
            }

            if (tray != null) {
                if (tray.Icon != targetIcon) tray.Icon = targetIcon;
                if (tray.Text != targetText) tray.Text = targetText;
            }
        }

        void OnConfig(object sender, EventArgs e) {
            float s = DpiScale();
            Func<int, int> P = v => (int)Math.Round(v * s);

            using (Form f = new Form()) {
                f.Text = "Schedule";
                f.Font = SystemFonts.MessageBoxFont;       // native look; scales by DPI (point-based)
                f.AutoScaleMode = AutoScaleMode.None;      // we scale manually -> no double scaling
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.Manual;   // open near the tray / cursor, not center-screen
                f.ClientSize = new Size(P(250), P(170));
                f.ShowInTaskbar = false;
                Point cur = Cursor.Position;                  // where the tray menu was just clicked
                Rectangle wa = Screen.FromPoint(cur).WorkingArea;
                int dx = P(-304);                             // horizontal offset from cursor anchor (px@96dpi): + = left, - = right
                int dy = P(19);                               // vertical offset: + = down, - = up
                int fx = Math.Min(Math.Max(wa.Left, cur.X - f.Width  - dx), wa.Right  - f.Width);
                int fy = Math.Min(Math.Max(wa.Top,  cur.Y - f.Height + dy), wa.Bottom - f.Height);
                f.Location = new Point(fx, fy);

                Label l1 = new Label(); l1.Text = "Start time:"; l1.SetBounds(P(20), P(24), P(90), P(20));
                Label l2 = new Label(); l2.Text = "End time:";   l2.SetBounds(P(20), P(58), P(90), P(20));

                DateTimePicker dtpStart = NewTimePicker(startT); dtpStart.SetBounds(P(120), P(20), P(100), P(24));
                DateTimePicker dtpEnd   = NewTimePicker(endT);   dtpEnd.SetBounds(P(120), P(54), P(100), P(24));

                CheckBox chk = new CheckBox();
                chk.Text = "Weekdays only (Mon-Fri)"; chk.Checked = weekdaysOnly;
                chk.SetBounds(P(20), P(92), P(210), P(22));

                Button ok = new Button(); ok.Text = "Save"; ok.SetBounds(P(50), P(128), P(80), P(28));
                ok.DialogResult = DialogResult.OK;
                Button cancel = new Button(); cancel.Text = "Cancel"; cancel.SetBounds(P(140), P(128), P(80), P(28));
                cancel.DialogResult = DialogResult.Cancel;

                f.Controls.AddRange(new Control[] { l1, l2, dtpStart, dtpEnd, chk, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                ApplyModernDialogTheme(f);

                if (f.ShowDialog() == DialogResult.OK) {
                    startT = new TimeSpan(dtpStart.Value.Hour, dtpStart.Value.Minute, 0);
                    endT   = new TimeSpan(dtpEnd.Value.Hour, dtpEnd.Value.Minute, 0);
                    weekdaysOnly = chk.Checked;
                    SaveConfig();
                    Reevaluate();
                }
            }   // Form (and its child controls / pickers) disposed here
        }

        void OnCustomSnoozeHours(object sender, EventArgs e) {
            float s = DpiScale();
            Func<int, int> P = v => (int)Math.Round(v * s);

            using (Form f = new Form()) {
                f.Text = "Pause Duration";
                f.Font = SystemFonts.MessageBoxFont;
                f.AutoScaleMode = AutoScaleMode.None;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.Manual;
                f.ClientSize = new Size(P(250), P(120));
                f.ShowInTaskbar = false;
                Point cur = Cursor.Position;
                Rectangle wa = Screen.FromPoint(cur).WorkingArea;
                int dx = P(-304);
                int dy = P(19);
                int fx = Math.Min(Math.Max(wa.Left, cur.X - f.Width  - dx), wa.Right  - f.Width);
                int fy = Math.Min(Math.Max(wa.Top,  cur.Y - f.Height + dy), wa.Bottom - f.Height);
                f.Location = new Point(fx, fy);

                Label lbl = new Label();
                lbl.Text = "Pause for (hours):";
                lbl.SetBounds(P(20), P(24), P(110), P(20));

                NumericUpDown nud = new NumericUpDown();
                nud.DecimalPlaces = 1;
                nud.Increment = 0.5M;
                nud.Minimum = 0.1M;
                nud.Maximum = 72M;
                nud.Value = 1.0M;
                nud.SetBounds(P(135), P(22), P(85), P(24));

                Button ok = new Button();
                ok.Text = "Pause";
                ok.SetBounds(P(50), P(68), P(80), P(28));
                ok.DialogResult = DialogResult.OK;

                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.SetBounds(P(140), P(68), P(80), P(28));
                cancel.DialogResult = DialogResult.Cancel;

                f.Controls.AddRange(new Control[] { lbl, nud, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                ApplyModernDialogTheme(f);

                if (f.ShowDialog() == DialogResult.OK) {
                    double hours = (double)nud.Value;
                    StartSnooze(TimeSpan.FromHours(hours));
                }
            }
        }

        static DateTimePicker NewTimePicker(TimeSpan t) {
            DateTimePicker d = new DateTimePicker();
            d.Format = DateTimePickerFormat.Custom;
            d.CustomFormat = "HH:mm";
            d.ShowUpDown = true;
            d.Value = DateTime.Today.Add(t);
            return d;
        }

        void OnExit(object sender, EventArgs e) {
            if (tray != null) tray.Visible = false;   // hide immediately
            ExitThread();                              // ends loop; Run() disposes this context
        }

        static void Nudge() {
            INPUT[] inp = new INPUT[1];
            inp[0].type = INPUT_MOUSE;
            inp[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_MOVE_NOCOALESCE; // (0,0) move, not coalesced
            uint sent = SendInput(1, inp, InputSize);
            if (sent != 1)
                Debug.WriteLine("KeepAwake: SendInput inserted " + sent + " (err=" + Marshal.GetLastWin32Error() + ")");
        }

        void ApplyKeepAwake(bool on) {
            if (on == keepAwakeAsserted) return;        // only on transitions
            SetThreadExecutionState(on
                ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)   // block sleep + display-off
                : ES_CONTINUOUS);                                             // release
            keepAwakeAsserted = on;
        }

        // ---- config I/O -------------------------------------------------------
        void LoadConfig() {
            try {
                if (!File.Exists(iniPath)) { SaveConfig(); return; }   // create default
                foreach (string raw in File.ReadAllLines(iniPath)) {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    if (key == "starttime") startT = ParseTime(val, startT);
                    else if (key == "endtime") endT = ParseTime(val, endT);
                    else if (key == "weekdaysonly") {
                        bool? b = ParseBool(val);
                        if (b.HasValue) weekdaysOnly = b.Value;   // unknown token -> keep current
                    }
                    else if (key == "skin" && !string.IsNullOrEmpty(val)) {
                        currentSkin = val;
                    }
                }
                if (currentSkin == "Classic" && (Directory.Exists(Path.Combine(skinsDir, "Pixel Office")) || File.Exists(Path.Combine(skinsDir, "Pixel Office.zip")))) {
                    currentSkin = "Pixel Office";
                }
            } catch (Exception ex) {
                Debug.WriteLine("KeepAwake: LoadConfig failed: " + ex.Message);   // fall back to defaults
            }
        }

        void SaveConfig() {
            try {
                string dir = Path.GetDirectoryName(iniPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(iniPath, new string[] {
                    "# KeepAwakeTray configuration (edit by hand or from the tray icon menu)",
                    "# File location is set with the -config <dir|file.ini> option (default: next to the exe).",
                    "StartTime="    + string.Format("{0:00}:{1:00}", startT.Hours, startT.Minutes),
                    "EndTime="      + string.Format("{0:00}:{1:00}", endT.Hours, endT.Minutes),
                    "WeekdaysOnly=" + (weekdaysOnly ? "1" : "0"),
                    "Skin="         + currentSkin
                });
            } catch (Exception ex) {
                Debug.WriteLine("KeepAwake: SaveConfig failed: " + ex.Message);
                if (tray != null)
                    tray.ShowBalloonTip(5000, "KeepAwake",
                        "Could not save settings to:\n" + iniPath + "\n(" + ex.Message + ")",
                        ToolTipIcon.Warning);
            }
        }

        static TimeSpan ParseTime(string s, TimeSpan fallback) {
            DateTime dt;
            if (DateTime.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return new TimeSpan(dt.Hour, dt.Minute, 0);
            TimeSpan ts;
            if (TimeSpan.TryParse(s, out ts)) return new TimeSpan(ts.Hours, ts.Minutes, 0);
            return fallback;
        }

        static bool? ParseBool(string s) {
            switch (s.Trim().ToLowerInvariant()) {
                case "1": case "true": case "yes": case "on": case "y": return true;
                case "0": case "false": case "no": case "off": case "n": return false;
                default: return null;   // unrecognized -> caller keeps current value
            }
        }

        // ---- DPI / icon rendering --------------------------------------------
        static float DpiScale() {
            try { uint d = GetDpiForSystem(); if (d > 0) return d / 96f; } catch { }
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; } catch { }
            return 1f;
        }

        // ---- skin management --------------------------------------------------
        static readonly string[] ActiveFilePatterns  = new string[] { "active", "icon-active", "on" };
        static readonly string[] WaitingFilePatterns = new string[] { "waiting", "icon-waiting", "wait", "idle" };
        static readonly string[] PausedFilePatterns  = new string[] { "paused", "icon-paused", "pause", "off", "sleep" };

        void ClearSkinThumbnails() {
            foreach (Image img in skinThumbnails) {
                try { img.Dispose(); } catch { }
            }
            skinThumbnails.Clear();
        }

        void PopulateSkinsMenu() {
            if (miSkins == null) return;
            ClearSkinThumbnails();
            miSkins.DropDownItems.Clear();

            int thumbSize = Math.Max(16, (int)Math.Round(16 * DpiScale()));

            Bitmap classicThumb = MakeDotBitmap(Color.FromArgb(40, 180, 70), thumbSize);
            skinThumbnails.Add(classicThumb);
            ToolStripMenuItem miClassic = new ToolStripMenuItem("Classic (Dots)", classicThumb, delegate { SwitchSkin("Classic"); });
            miClassic.Checked = string.Equals(currentSkin, "Classic", StringComparison.OrdinalIgnoreCase);
            miSkins.DropDownItems.Add(miClassic);

            List<string> available = GetAvailableSkins();
            if (available.Count > 0) {
                miSkins.DropDownItems.Add(new ToolStripSeparator());
                foreach (string skin in available) {
                    string sName = skin;
                    Bitmap thumb = LoadThumbnail(sName, thumbSize);
                    if (thumb != null) skinThumbnails.Add(thumb);
                    ToolStripMenuItem item = new ToolStripMenuItem(sName, thumb, delegate { SwitchSkin(sName); });
                    item.Checked = string.Equals(currentSkin, sName, StringComparison.OrdinalIgnoreCase);
                    miSkins.DropDownItems.Add(item);
                }
            }

            miSkins.DropDownItems.Add(new ToolStripSeparator());
            miSkins.DropDownItems.Add(new ToolStripMenuItem("Open skins folder...", null, delegate { OpenSkinsFolder(); }));
        }

        Bitmap LoadThumbnail(string skinName, int size) {
            if (string.Equals(skinName, "Classic", StringComparison.OrdinalIgnoreCase)) {
                return MakeDotBitmap(Color.FromArgb(40, 180, 70), size);
            }
            // 1. Try folder
            string folder = Path.Combine(skinsDir, skinName);
            if (Directory.Exists(folder)) {
                foreach (string pattern in ActiveFilePatterns) {
                    foreach (string ext in new string[] { ".png", ".ico", ".jpg", ".jpeg", ".bmp" }) {
                        string file = Path.Combine(folder, pattern + ext);
                        if (File.Exists(file)) {
                            try {
                                byte[] bytes = File.ReadAllBytes(file);
                                using (MemoryStream ms = new MemoryStream(bytes))
                                using (Image img = Image.FromStream(ms)) {
                                    return MakeBitmapImage(img, size);
                                }
                            } catch { }
                        }
                    }
                }
            }
            // 2. Try zip
            string zipPath = Path.Combine(skinsDir, skinName + ".zip");
            if (File.Exists(zipPath)) {
                try {
                    using (FileStream fs = File.OpenRead(zipPath))
                    using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read)) {
                        foreach (ZipArchiveEntry entry in zip.Entries) {
                            string nameNoExt = Path.GetFileNameWithoutExtension(entry.Name).ToLowerInvariant();
                            string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                            if (ext != ".png" && ext != ".ico" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp") continue;
                            foreach (string p in ActiveFilePatterns) {
                                if (nameNoExt == p.ToLowerInvariant()) {
                                    using (Stream s = entry.Open())
                                    using (MemoryStream ms = new MemoryStream()) {
                                        s.CopyTo(ms);
                                        ms.Position = 0;
                                        using (Image img = Image.FromStream(ms)) {
                                            return MakeBitmapImage(img, size);
                                        }
                                    }
                                }
                            }
                        }
                    }
                } catch { }
            }
            return MakeDotBitmap(Color.FromArgb(40, 180, 70), size);
        }

        List<string> GetAvailableSkins() {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                if (Directory.Exists(skinsDir)) {
                    foreach (string d in Directory.GetDirectories(skinsDir)) {
                        string name = Path.GetFileName(d);
                        if (!string.IsNullOrEmpty(name)) set.Add(name);
                    }
                    foreach (string f in Directory.GetFiles(skinsDir, "*.zip")) {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (!string.IsNullOrEmpty(name)) set.Add(name);
                    }
                }
            } catch (Exception ex) {
                Debug.WriteLine("KeepAwake: GetAvailableSkins error: " + ex.Message);
            }
            List<string> list = new List<string>(set);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        void OpenSkinsFolder() {
            try {
                if (!Directory.Exists(skinsDir)) Directory.CreateDirectory(skinsDir);
                Process.Start("explorer.exe", skinsDir);
            } catch (Exception ex) {
                Debug.WriteLine("KeepAwake: OpenSkinsFolder failed: " + ex.Message);
            }
        }

        void SwitchSkin(string skinName) {
            if (string.Equals(currentSkin, skinName, StringComparison.OrdinalIgnoreCase)) return;
            currentSkin = skinName;
            SaveConfig();
            LoadSkin(currentSkin);
            UpdateIcon();
            PopulateSkinsMenu();
        }

        void LoadSkin(string skinName) {
            int sz = Math.Max(16, SystemInformation.SmallIconSize.Width);
            Icon newOn = null, newWait = null, newOff = null;

            if (!string.Equals(skinName, "Classic", StringComparison.OrdinalIgnoreCase)) {
                // 1. Try folder
                string folderPath = Path.Combine(skinsDir, skinName);
                if (Directory.Exists(folderPath)) {
                    newOn   = LoadIconFromFolder(folderPath, ActiveFilePatterns, sz);
                    newWait = LoadIconFromFolder(folderPath, WaitingFilePatterns, sz);
                    newOff  = LoadIconFromFolder(folderPath, PausedFilePatterns, sz);
                }

                // 2. Try zip
                string zipPath = Path.Combine(skinsDir, skinName + ".zip");
                if ((newOn == null || newWait == null || newOff == null) && File.Exists(zipPath)) {
                    try {
                        using (FileStream fs = File.OpenRead(zipPath))
                        using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read)) {
                            if (newOn   == null) newOn   = LoadIconFromZip(zip, ActiveFilePatterns, sz);
                            if (newWait == null) newWait = LoadIconFromZip(zip, WaitingFilePatterns, sz);
                            if (newOff  == null) newOff  = LoadIconFromZip(zip, PausedFilePatterns, sz);
                        }
                    } catch (Exception ex) {
                        Debug.WriteLine("KeepAwake: Failed to read skin zip: " + ex.Message);
                    }
                }
            }

            // Fallback for missing state
            if (newOn   == null) newOn   = MakeDotIcon(Color.FromArgb(40, 180, 70), sz);
            if (newWait == null) newWait = MakeDotIcon(Color.FromArgb(225, 185, 40), sz);
            if (newOff  == null) newOff  = MakeDotIcon(Color.FromArgb(130, 130, 130), sz);

            Icon oldOn = iconOn, oldWait = iconWait, oldOff = iconOff;
            iconOn = newOn;
            iconWait = newWait;
            iconOff = newOff;

            if (oldOn != null) oldOn.Dispose();
            if (oldWait != null) oldWait.Dispose();
            if (oldOff != null) oldOff.Dispose();
        }

        static Icon LoadIconFromFolder(string folder, string[] patterns, int size) {
            try {
                foreach (string pattern in patterns) {
                    foreach (string ext in new string[] { ".png", ".ico", ".jpg", ".jpeg", ".bmp" }) {
                        string file = Path.Combine(folder, pattern + ext);
                        if (File.Exists(file)) {
                            byte[] bytes = File.ReadAllBytes(file);
                            using (MemoryStream ms = new MemoryStream(bytes))
                            using (Image img = Image.FromStream(ms)) {
                                return MakeBitmapIcon(img, size);
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                Debug.WriteLine("KeepAwake: LoadIconFromFolder error: " + ex.Message);
            }
            return null;
        }

        static Icon LoadIconFromZip(ZipArchive zip, string[] patterns, int size) {
            try {
                foreach (ZipArchiveEntry entry in zip.Entries) {
                    string nameNoExt = Path.GetFileNameWithoutExtension(entry.Name).ToLowerInvariant();
                    string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext != ".png" && ext != ".ico" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp")
                        continue;

                    foreach (string p in patterns) {
                        if (nameNoExt == p.ToLowerInvariant()) {
                            using (Stream s = entry.Open())
                            using (MemoryStream ms = new MemoryStream()) {
                                s.CopyTo(ms);
                                ms.Position = 0;
                                using (Image img = Image.FromStream(ms)) {
                                    return MakeBitmapIcon(img, size);
                                }
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                Debug.WriteLine("KeepAwake: LoadIconFromZip error: " + ex.Message);
            }
            return null;
        }

        static Icon MakeBitmapIcon(Image img, int size) {
            using (Bitmap bmp = new Bitmap(size, size)) {
                using (Graphics g = Graphics.FromImage(bmp)) {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(img, 0, 0, size, size);
                }
                IntPtr h = bmp.GetHicon();
                try {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                } finally {
                    DestroyIcon(h);
                }
            }
        }

        static Icon MakeDotIcon(Color c, int size) {
            using (Bitmap bmp = new Bitmap(size, size)) {
                using (Graphics g = Graphics.FromImage(bmp)) {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    int inset = Math.Max(1, size / 8);
                    int d = size - 2 * inset;
                    using (Brush b = new SolidBrush(c)) g.FillEllipse(b, inset, inset, d, d);
                    using (Pen p = new Pen(Color.White, Math.Max(1f, size / 16f))) g.DrawEllipse(p, inset, inset, d, d);
                }
                IntPtr h = bmp.GetHicon();
                try {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();   // Clone owns its handle -> safe to Dispose later
                } finally {
                    DestroyIcon(h);                 // release the HICON returned by GetHicon()
                }
            }
        }

        static Bitmap MakeBitmapImage(Image img, int size) {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(img, 0, 0, size, size);
            }
            return bmp;
        }

        static Bitmap MakeDotBitmap(Color c, int size) {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                int inset = Math.Max(1, size / 8);
                int d = size - 2 * inset;
                using (Brush b = new SolidBrush(c)) g.FillEllipse(b, inset, inset, d, d);
                using (Pen p = new Pen(Color.White, Math.Max(1f, size / 16f))) g.DrawEllipse(p, inset, inset, d, d);
            }
            return bmp;
        }

        static bool IsWindowsDarkMode() {
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                    if (key != null) {
                        object val = key.GetValue("AppsUseLightTheme");
                        if (val != null && val is int) return ((int)val == 0);
                    }
                }
            } catch { }
            return false;
        }

        void ApplyThemeToMenus() {
            bool dark = IsWindowsDarkMode();
            ModernMenuRenderer renderer = new ModernMenuRenderer(dark);
            if (menu != null) {
                ApplyRendererRecursive(menu, renderer);
            }
        }

        static void ApplyRendererRecursive(ToolStripDropDown drop, ToolStripRenderer renderer) {
            if (drop == null) return;
            drop.Renderer = renderer;
            drop.Font = SystemFonts.MessageBoxFont;
            foreach (ToolStripItem item in drop.Items) {
                if (item is ToolStripDropDownItem) {
                    ApplyRendererRecursive(((ToolStripDropDownItem)item).DropDown, renderer);
                }
            }
        }

        static void ApplyModernDialogTheme(Form f) {
            try {
                f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            } catch { }

            bool dark = IsWindowsDarkMode();
            if (dark) {
                try {
                    int v = 1;
                    if (DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)) != 0) {
                        DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref v, sizeof(int));
                    }
                } catch { }

                Color bg = Color.FromArgb(32, 32, 34);
                Color fg = Color.FromArgb(240, 240, 240);
                f.BackColor = bg;
                f.ForeColor = fg;
                foreach (Control c in f.Controls) {
                    c.ForeColor = fg;
                    if (c is Button) {
                        Button btn = (Button)c;
                        btn.FlatStyle = FlatStyle.Flat;
                        btn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 76);
                        btn.BackColor = Color.FromArgb(48, 48, 54);
                    } else if (c is CheckBox) {
                        ((CheckBox)c).ForeColor = fg;
                    } else if (c is Label) {
                        ((Label)c).ForeColor = fg;
                    }
                }
            }
        }

        // ---- cleanup ----------------------------------------------------------
        protected override void Dispose(bool disposing) {
            if (!disposed && disposing) {
                disposed = true;
                CancelSnoozeTimer();
                ApplyKeepAwake(false);              // release SetThreadExecutionState
                if (timer != null) { timer.Stop(); timer.Dispose(); }
                if (tray  != null) { tray.Visible = false; tray.Dispose(); }
                ClearSkinThumbnails();
                if (menu  != null) menu.Dispose();  // disposes its MenuItems too
                if (iconOn   != null) iconOn.Dispose();
                if (iconWait != null) iconWait.Dispose();
                if (iconOff  != null) iconOff.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    sealed class ModernColorTable : ProfessionalColorTable {
        readonly bool dark;
        public ModernColorTable(bool dark) { this.dark = dark; }

        public override Color MenuItemSelected { get { return dark ? Color.FromArgb(55, 55, 60) : Color.FromArgb(232, 234, 238); } }
        public override Color MenuItemSelectedGradientBegin { get { return MenuItemSelected; } }
        public override Color MenuItemSelectedGradientEnd { get { return MenuItemSelected; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }

        public override Color ToolStripDropDownBackground { get { return dark ? Color.FromArgb(32, 32, 34) : Color.FromArgb(250, 250, 252); } }
        public override Color ImageMarginGradientBegin { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginGradientMiddle { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginGradientEnd { get { return ToolStripDropDownBackground; } }

        public override Color MenuBorder { get { return dark ? Color.FromArgb(60, 60, 66) : Color.FromArgb(218, 220, 224); } }
        public override Color MenuItemPressedGradientBegin { get { return dark ? Color.FromArgb(45, 45, 50) : Color.FromArgb(220, 222, 226); } }
        public override Color MenuItemPressedGradientEnd { get { return MenuItemPressedGradientBegin; } }

        public override Color SeparatorDark { get { return dark ? Color.FromArgb(52, 52, 58) : Color.FromArgb(228, 230, 234); } }
        public override Color SeparatorLight { get { return Color.Transparent; } }

        public override Color CheckBackground { get { return dark ? Color.FromArgb(52, 52, 58) : Color.FromArgb(220, 225, 235); } }
        public override Color CheckSelectedBackground { get { return dark ? Color.FromArgb(68, 68, 76) : Color.FromArgb(205, 215, 230); } }
        public override Color CheckPressedBackground { get { return CheckSelectedBackground; } }
    }

    sealed class ModernMenuRenderer : ToolStripProfessionalRenderer {
        readonly bool dark;
        public ModernMenuRenderer(bool dark) : base(new ModernColorTable(dark)) {
            this.dark = dark;
            this.RoundedEdges = true;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
            if (!e.Item.Enabled) {
                e.TextColor = dark ? Color.FromArgb(120, 120, 125) : Color.FromArgb(150, 150, 155);
            } else {
                e.TextColor = dark ? Color.FromArgb(242, 242, 242) : Color.FromArgb(30, 30, 30);
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
            e.ArrowColor = dark ? Color.FromArgb(200, 200, 205) : Color.FromArgb(70, 70, 75);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(e.ImageRectangle.X, e.ImageRectangle.Y, e.ImageRectangle.Width, e.ImageRectangle.Height);
            r.Inflate(-1, -1);
            Color bg = dark ? Color.FromArgb(52, 52, 58) : Color.FromArgb(220, 225, 235);
            Color border = dark ? Color.FromArgb(85, 85, 92) : Color.FromArgb(180, 190, 205);
            Color checkColor = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(30, 30, 30);
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, r);
            using (Pen p = new Pen(border)) g.DrawRectangle(p, r);
            using (Pen cp = new Pen(checkColor, 1.8f)) {
                Point p1 = new Point(r.Left + r.Width / 4, r.Top + r.Height / 2);
                Point p2 = new Point(r.Left + r.Width * 4 / 9, r.Bottom - r.Height / 4);
                Point p3 = new Point(r.Right - r.Width / 4, r.Top + r.Height / 3);
                g.DrawLine(cp, p1, p2);
                g.DrawLine(cp, p2, p3);
            }
        }
    }
}
