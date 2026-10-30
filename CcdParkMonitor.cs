// CCD Park Monitor - checks that the non-V-Cache CCD is parked while a game is in the foreground.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQuery(string src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufSize, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetLogicalProcessorInformationEx(int relType, IntPtr buffer, ref uint len);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

    // Effective power mode is what amd3dvcacheSvc keys off: GameMode (5) => prefer cache => park the other CCD.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void PowerModeCallback(int mode, IntPtr context);
    [DllImport("powrprof.dll")] public static extern int PowerRegisterForEffectivePowerModeNotifications(uint version, PowerModeCallback callback, IntPtr context, out IntPtr handle);
    [DllImport("powrprof.dll")] public static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr handle);
    public const uint EFFECTIVE_POWER_MODE_V2 = 2;

    public const uint PDH_FMT_DOUBLE = 0x200;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
}

class Ccd
{
    public string Name;
    public long Mask;      // logical processors in processor group 0
    public uint L3Bytes;
    public bool VCache;
    public List<int> Cpus = new List<int>();
}

// One PDH query, wildcard counters, sampled once per tick. Cheap.
class Sampler : IDisposable
{
    IntPtr query, parkCounter, loadCounter;
    public double[] Parked = new double[64];
    public double[] Load = new double[64];

    public Sampler()
    {
        Check(Native.PdhOpenQuery(null, IntPtr.Zero, out query), "PdhOpenQuery");
        Check(Native.PdhAddEnglishCounter(query, @"\Processor Information(*)\Parking Status", IntPtr.Zero, out parkCounter), "Parking Status counter");
        Check(Native.PdhAddEnglishCounter(query, @"\Processor Information(*)\% Processor Time", IntPtr.Zero, out loadCounter), "% Processor Time counter");
        Native.PdhCollectQueryData(query);
    }

    static void Check(uint rc, string what)
    {
        if (rc != 0) throw new Exception(what + " failed: 0x" + rc.ToString("X8"));
    }

    public void Sample()
    {
        if (Native.PdhCollectQueryData(query) != 0) return;
        Read(parkCounter, Parked);
        Read(loadCounter, Load);
    }

    static void Read(IntPtr counter, double[] dest)
    {
        uint size = 0, count;
        uint rc = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
        if (rc != Native.PDH_MORE_DATA) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, buf) != 0) return;
            int itemSize = IntPtr.Size + 16; // szName + PDH_FMT_COUNTERVALUE {CStatus, pad, double}
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buf + i * itemSize;
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                // instances look like "0,17" (group,cpu); skip "_Total" / "0,_Total"
                int comma = name.IndexOf(',');
                int cpu;
                if (comma < 0 || name.Substring(0, comma) != "0" || !int.TryParse(name.Substring(comma + 1), out cpu)) continue;
                if (cpu >= dest.Length) continue;
                dest[cpu] = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose() { Native.PdhCloseQuery(query); }
}

static class Topology
{
    // Each CCD has its own L3, so L3 cache groups == CCDs. The bigger L3 is the V-Cache die.
    public static List<Ccd> Detect()
    {
        var ccds = new List<Ccd>();
        uint len = 0;
        Native.GetLogicalProcessorInformationEx(2 /*RelationCache*/, IntPtr.Zero, ref len);
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(2, buf, ref len)) throw new Exception("GetLogicalProcessorInformationEx failed");
            int off = 0;
            while (off < len)
            {
                IntPtr rec = buf + off;
                int size = Marshal.ReadInt32(rec, 4);
                byte level = Marshal.ReadByte(rec, 8);
                uint cacheSize = (uint)Marshal.ReadInt32(rec, 12);
                int type = Marshal.ReadInt32(rec, 16);
                long mask = Marshal.ReadInt64(rec, 40);
                short group = Marshal.ReadInt16(rec, 48);
                if (level == 3 && (type == 0 /*Unified*/) && group == 0)
                {
                    var c = new Ccd { Mask = mask, L3Bytes = cacheSize };
                    for (int i = 0; i < 64; i++) if ((mask & (1L << i)) != 0) c.Cpus.Add(i);
                    ccds.Add(c);
                }
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        ccds.Sort((a, b) => a.Cpus[0].CompareTo(b.Cpus[0]));
        uint maxL3 = 0;
        foreach (var c in ccds) maxL3 = Math.Max(maxL3, c.L3Bytes);
        for (int i = 0; i < ccds.Count; i++)
        {
            ccds[i].VCache = ccds.Count > 1 && ccds[i].L3Bytes == maxL3;
            ccds[i].Name = "CCD" + i;
        }
        return ccds;
    }
}

static class Games
{
    // Exes Windows considers games: Game Bar's "Remember this is a game" ends up in GameConfigStore.
    public static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var root = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children"))
            {
                if (root == null) return set;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using (var k = root.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        var p = k.GetValue("MatchedExeFullPath") as string;
                        if (!string.IsNullOrEmpty(p)) set.Add(p);
                    }
                }
            }
        }
        catch { }
        string extra = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "games.txt");
        if (File.Exists(extra))
            foreach (string line in File.ReadAllLines(extra))
                if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("#")) set.Add(line.Trim());
        return set;
    }

    public static bool IsGame(HashSet<string> games, string path)
    {
        if (path == null) return false;
        if (games.Contains(path)) return true;
        return games.Contains(Path.GetFileName(path)); // games.txt may list bare exe names
    }

    public static string ForegroundExe()
    {
        uint pid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        if (pid == 0) return null;
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint n = (uint)sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }
}

// Per-user settings live in the registry so the app folder stays just the exe (+ log).
static class Settings
{
    const string KeyPath = @"Software\CcdParkMonitor";

    public static bool GetBool(string name)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                return k != null && Convert.ToInt32(k.GetValue(name, 0)) != 0;
        }
        catch { return false; }
    }

    public static void SetBool(string name, bool value)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                k.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
    }
}

enum Verdict { Idle, Waiting, Pass, Fail }

class MonitorForm : Form
{
    const int GraceSeconds = 10;          // time the AMD driver gets to park after a game takes focus
    const int GameListRefreshSeconds = 30;

    readonly List<Ccd> ccds;
    readonly Ccd target;                  // the non-V-Cache CCD that should park
    readonly Sampler sampler;
    readonly Timer timer = new Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Icon[] icons = new Icon[4];
    readonly Panel grid = new DoubleBufferedPanel();
    readonly Label status = new Label();
    readonly Label details = new Label();
    readonly CheckBox forceGame = new CheckBox();
    readonly CheckBox stayOnTop = new CheckBox();
    readonly ToolStripMenuItem stayOnTopItem = new ToolStripMenuItem("Stay on top");
    readonly string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ccd_park_log.csv");

    HashSet<string> games;
    int tick;
    Verdict verdict = Verdict.Idle;
    string gameExe;
    DateTime gameSince;
    int gameSamples, gameParkedSamples, failEvents;
    double gameTargetLoadSum;
    bool alerted;
    volatile int powerMode = -1;          // written from a threadpool callback, read in OnTick
    int loggedPowerMode = -1;
    Native.PowerModeCallback powerModeCallback; // kept in a field so the GC doesn't collect it
    IntPtr powerModeHandle;

    static readonly string[] PowerModeNames = { "BatterySaver", "BetterBattery", "Balanced", "HighPerformance", "MaxPerformance", "GameMode", "MixedReality" };

    static string PowerModeName(int mode)
    {
        if (mode < 0) return "unknown";
        return mode < PowerModeNames.Length ? PowerModeNames[mode] : "mode " + mode;
    }

    public MonitorForm()
    {
        ccds = Topology.Detect();
        foreach (var c in ccds) if (!c.VCache) { target = c; break; }
        if (target == null || ccds.Count < 2) throw new Exception("Expected a dual-CCD X3D CPU (two L3 caches of different size). Found " + ccds.Count + " L3 group(s).");
        sampler = new Sampler();
        games = Games.Load();

        Text = "CCD Park Monitor";
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(560, 322);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.Gainsboro;

        status.SetBounds(12, 10, 536, 30);
        status.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        grid.SetBounds(12, 46, 536, 120);
        grid.Paint += PaintGrid;
        details.SetBounds(12, 172, 536, 96);
        forceGame.SetBounds(12, 270, 536, 22);
        forceGame.Text = "Treat any foreground app as a game (for testing an exe Game Bar hasn't flagged)";
        stayOnTop.SetBounds(12, 294, 536, 22);
        stayOnTop.Text = "Stay on top";
        stayOnTop.CheckedChanged += delegate { SetStayOnTop(stayOnTop.Checked); };
        Controls.AddRange(new Control[] { status, grid, details, forceGame, stayOnTop });

        for (int i = 0; i < 4; i++) icons[i] = MakeIcon(VerdictColor((Verdict)i));
        tray.Icon = icons[0];
        tray.Visible = true;
        tray.Text = "CCD Park Monitor";
        tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, delegate { Show(); Activate(); });
        stayOnTopItem.Click += delegate { SetStayOnTop(!TopMost); };
        menu.Items.Add(stayOnTopItem);
        menu.Items.Add("Open log", null, delegate { if (File.Exists(logPath)) System.Diagnostics.Process.Start(logPath); });
        menu.Items.Add("Exit", null, delegate { tray.Visible = false; Application.Exit(); });
        tray.ContextMenuStrip = menu;

        SetStayOnTop(Settings.GetBool("StayOnTop"));

        powerModeCallback = delegate(int mode, IntPtr ctx) { powerMode = mode; };
        Native.PowerRegisterForEffectivePowerModeNotifications(Native.EFFECTIVE_POWER_MODE_V2, powerModeCallback, IntPtr.Zero, out powerModeHandle);

        if (!File.Exists(logPath)) File.AppendAllText(logPath, "time,event,exe,detail\r\n");
        Log("start", "", string.Format("target={0} cpus {1}-{2}, vcache L3={3} MB", target.Name, target.Cpus[0], target.Cpus[target.Cpus.Count - 1], VCacheCcd().L3Bytes >> 20));

        timer.Interval = 1000;
        timer.Tick += OnTick;
        timer.Start();
        OnTick(null, null);
    }

    void SetStayOnTop(bool on)
    {
        TopMost = on;
        stayOnTop.Checked = on;       // no-op re-entry when already equal
        stayOnTopItem.Checked = on;
        Settings.SetBool("StayOnTop", on);
    }

    Ccd VCacheCcd() { foreach (var c in ccds) if (c.VCache) return c; return null; }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide(); // to tray, stops repainting
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && tray.Visible) { e.Cancel = true; Hide(); return; }
        EndGame();
        tray.Visible = false;
        if (powerModeHandle != IntPtr.Zero) Native.PowerUnregisterFromEffectivePowerModeNotifications(powerModeHandle);
        sampler.Dispose();
        base.OnFormClosing(e);
    }

    int ParkedCount(Ccd c)
    {
        int n = 0;
        foreach (int cpu in c.Cpus) if (sampler.Parked[cpu] >= 0.5) n++;
        return n;
    }

    double AvgLoad(Ccd c)
    {
        double s = 0;
        foreach (int cpu in c.Cpus) s += sampler.Load[cpu];
        return s / c.Cpus.Count;
    }

    void OnTick(object sender, EventArgs e)
    {
        tick++;
        if (tick % GameListRefreshSeconds == 0) games = Games.Load();
        sampler.Sample();

        int mode = powerMode;
        if (mode != loggedPowerMode)
        {
            loggedPowerMode = mode;
            Log("power_mode", gameExe ?? "", "");
        }

        string fg = Games.ForegroundExe();
        bool isGame = fg != null && (forceGame.Checked ? !fg.EndsWith("CcdParkMonitor.exe", StringComparison.OrdinalIgnoreCase) : Games.IsGame(games, fg));

        if (isGame && !string.Equals(fg, gameExe, StringComparison.OrdinalIgnoreCase)) { EndGame(); StartGame(fg); }
        else if (!isGame && gameExe != null) EndGame();

        int parked = ParkedCount(target);
        bool fullyParked = parked == target.Cpus.Count;
        Verdict v;
        if (gameExe == null) v = Verdict.Idle;
        else
        {
            gameSamples++;
            gameTargetLoadSum += AvgLoad(target);
            if (fullyParked) gameParkedSamples++;
            if (fullyParked) v = Verdict.Pass;
            else v = (DateTime.Now - gameSince).TotalSeconds < GraceSeconds ? Verdict.Waiting : Verdict.Fail;
        }

        if (v != verdict)
        {
            if (v == Verdict.Pass || v == Verdict.Fail)
                Log(v == Verdict.Pass ? "parked" : "NOT_parked", gameExe, string.Format("{0}/{1} {2} cores parked", parked, target.Cpus.Count, target.Name));
            if (v == Verdict.Fail) failEvents++;
            if (v == Verdict.Fail && !alerted)
            {
                alerted = true;
                tray.ShowBalloonTip(5000, "CCD not parked", string.Format("{0} has focus but only {1}/{2} {3} cores are parked.", Path.GetFileName(gameExe), parked, target.Cpus.Count, target.Name), ToolTipIcon.Warning);
            }
            verdict = v;
            tray.Icon = icons[(int)v];
        }

        string summary = StatusText(v, parked);
        tray.Text = summary.Length > 63 ? summary.Substring(0, 63) : summary;
        if (!Visible) return; // nothing to draw

        status.Text = summary;
        status.ForeColor = VerdictColor(v);
        var sb = new StringBuilder();
        foreach (var c in ccds)
            sb.AppendFormat("{0}{1}: L3 {2} MB, {3}/{4} parked, load {5:0}%\n", c.Name, c.VCache ? " (V-Cache)" : "", c.L3Bytes >> 20, ParkedCount(c), c.Cpus.Count, AvgLoad(c));
        sb.AppendFormat("Windows power mode: {0}{1}\n", PowerModeName(mode), mode == 5 ? "  (AMD driver should prefer the V-Cache CCD)" : "");
        sb.AppendFormat("Foreground: {0}{1}\n",fg == null ? "(unknown / protected)" : Path.GetFileName(fg), isGame ? "  [game]" : "");
        if (gameExe != null)
            sb.AppendFormat("This session: {0:0}s, {1} cores parked {2:0}% of the time, {1} avg load {3:0.0}%, not-parked events {4}",
                (DateTime.Now - gameSince).TotalSeconds, target.Name, 100.0 * gameParkedSamples / Math.Max(1, gameSamples), gameTargetLoadSum / Math.Max(1, gameSamples), failEvents);
        details.Text = sb.ToString();
        grid.Invalidate();
    }

    string StatusText(Verdict v, int parked)
    {
        switch (v)
        {
            case Verdict.Pass: return string.Format("PASS - {0} parked ({1}/{2})", target.Name, parked, target.Cpus.Count);
            case Verdict.Fail: return string.Format("FAIL - {0} not parked ({1}/{2})", target.Name, parked, target.Cpus.Count);
            case Verdict.Waiting: return string.Format("Game detected, waiting for park... ({0}/{1})", parked, target.Cpus.Count);
            default: return string.Format("No game in focus - {0} {1}/{2} parked", target.Name, parked, target.Cpus.Count);
        }
    }

    void StartGame(string exe)
    {
        gameExe = exe;
        gameSince = DateTime.Now;
        gameSamples = gameParkedSamples = failEvents = 0;
        gameTargetLoadSum = 0;
        alerted = false;
        Log("game_focus", exe, "");
    }

    void EndGame()
    {
        if (gameExe == null) return;
        Log("game_blur", gameExe, string.Format("{0:0}s, {1} parked {2:0.0}% of samples, avg {1} load {3:0.0}%, not-parked events {4}",
            (DateTime.Now - gameSince).TotalSeconds, target.Name, 100.0 * gameParkedSamples / Math.Max(1, gameSamples), gameTargetLoadSum / Math.Max(1, gameSamples), failEvents));
        gameExe = null;
    }

    void Log(string evt, string exe, string detail)
    {
        detail = (detail.Length > 0 ? detail + "; " : "") + "power mode " + PowerModeName(powerMode);
        try
        {
            File.AppendAllText(logPath, string.Format("{0:yyyy-MM-dd HH:mm:ss},{1},\"{2}\",\"{3}\"\r\n", DateTime.Now, evt, exe, detail));
        }
        catch { }
    }

    void PaintGrid(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int cellW = 54, cellH = 16, gap = 3, y = 0;
        using (var font = new Font("Segoe UI", 7.5f))
        using (var text = new SolidBrush(Color.White))
        {
            foreach (var c in ccds)
            {
                g.DrawString(c.Name + (c.VCache ? " V$" : ""), Font, text, 0, y + 14);
                for (int i = 0; i < c.Cpus.Count; i++)
                {
                    int cpu = c.Cpus[i];
                    int col = i / 2, row = i % 2; // SMT siblings stacked
                    var r = new Rectangle(70 + col * (cellW + gap + 1), y + row * (cellH + gap), cellW, cellH);
                    bool parked = sampler.Parked[cpu] >= 0.5;
                    double load = Math.Max(0, Math.Min(100, sampler.Load[cpu]));
                    Color bg = parked ? Color.FromArgb(60, 60, 66) : Color.FromArgb(30 + (int)(load * 1.6), 110 + (int)(load * 1.2), 60);
                    using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
                    g.DrawString(parked ? cpu + " P" : cpu + " " + load.ToString("0") + "%", font, text, r.X + 2, r.Y + 1);
                }
                y += 2 * (cellH + gap) + 20;
            }
        }
    }

    static Color VerdictColor(Verdict v)
    {
        switch (v)
        {
            case Verdict.Pass: return Color.FromArgb(70, 200, 90);
            case Verdict.Fail: return Color.FromArgb(230, 70, 60);
            case Verdict.Waiting: return Color.FromArgb(240, 180, 40);
            default: return Color.FromArgb(140, 140, 150);
        }
    }

    static Icon MakeIcon(Color c)
    {
        using (var bmp = new Bitmap(16, 16))
        {
            using (var g = Graphics.FromImage(bmp))
            using (var b = new SolidBrush(c))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(b, 1, 1, 14, 14);
            }
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            Native.DestroyIcon(h);
            return icon;
        }
    }

    class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() { DoubleBuffered = true; }
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        try
        {
            var f = new MonitorForm();
            bool tray = Array.IndexOf(args, "--tray") >= 0;
            if (tray) { f.WindowState = FormWindowState.Minimized; f.ShowInTaskbar = true; }
            Application.Run(f);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "CCD Park Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
