// GamePowerPlan.cs
// Tray app: switches to your gaming power plan while a game is running, and to your idle plan otherwise.
// Everything is controlled from the tray icon. Settings are stored in GamePowerPlan.cfg next to the exe.
//
// Build (one line, no install needed):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:GamePowerPlan.exe /win32manifest:GamePowerPlan.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll GamePowerPlan.cs
// (or just run build.bat)
// Microsoft Store package: see the msix folder (run msix\build-msix.bat)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("GamePowerPlan")]
[assembly: AssemblyDescription("Tray app that switches the Windows power plan while a game is running")]
[assembly: AssemblyCompany("Nimbonk")]
[assembly: AssemblyProduct("GamePowerPlan")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Nimbonk")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

class PlanInfo
{
    public string Id;
    public string Name;
}

// Invisible window that receives the global hotkey message.
class HotkeyWindow : NativeWindow
{
    public event EventHandler Pressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && Pressed != null) Pressed(this, EventArgs.Empty);   // WM_HOTKEY
        base.WndProc(ref m);
    }
}

class GamePowerPlan : ApplicationContext
{
    enum Mode { Auto, ForceHigh, ForceBalanced }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "GamePowerPlan";
    const string StockHigh = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    const string StockBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    const int HotkeyId = 0x4750;

    static readonly string[] DefaultGames = new string[] {
        "\\steamapps\\common\\", "\\Epic Games\\", "\\GOG Galaxy\\Games\\", "\\GOG Games\\",
        "\\Riot Games\\", "\\XboxGames\\", "\\Ubisoft Game Launcher\\games\\", "\\EA Games\\", "\\Battle.net\\"
    };

    static readonly string[] DefaultIgnore = new string[] {
        "PalServer*", "valheim_server*", "wallpaper*", "SteamVR*", "vrserver*"
    };

    // ---- Windows API ------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);

    // True when running from the Microsoft Store (MSIX) package, false for the plain exe.
    // In the Store package the registry Run key is virtualised and would not start the app at login,
    // so "Start with Windows" opens Windows' own Startup apps page instead.
    static readonly bool packaged = IsPackaged();

    static bool IsPackaged()
    {
        try
        {
            int len = 0;
            return GetCurrentPackageFullName(ref len, null) != 15700;   // APPMODEL_ERROR_NO_PACKAGE
        }
        catch { return false; }
    }

    // ---- Settings (stored in the cfg) -------------------------------------

    int checkSeconds;
    int graceSeconds;
    Color balancedColour;
    Color gamingColour;
    string hotkeyText;
    string gamingPlanSetting;
    string idlePlanSetting;
    bool notifyOverride;
    bool holdPlan;
    List<string> games = new List<string>();
    List<string> ignores = new List<string>();
    List<Regex> ignoreRegexes = new List<Regex>();

    // ---- State ------------------------------------------------------------

    string cfgPath;
    List<PlanInfo> plans = new List<PlanInfo>();
    string gamingGuid, idleGuid;
    string lastSet, lastWanted, activeNow;
    Mode mode = Mode.Auto;
    int gamePid = 0;
    string gameName;
    DateTime gameSince = DateTime.MinValue;
    DateTime lastGame = DateTime.MinValue;
    DateTime lastNotice = DateTime.MinValue;
    DateTime flyoutClosed = DateTime.MinValue;
    Form flyout;

    NotifyIcon tray;
    ContextMenuStrip menu;
    System.Windows.Forms.Timer timer;
    HotkeyWindow hotkeyWindow;
    bool hotkeyActive;
    Icon iconIdle, iconGaming;
    IntPtr hIdle = IntPtr.Zero, hGaming = IntPtr.Zero;

    ToolStripMenuItem statusItem, autoItem, highItem, balItem;
    ToolStripMenuItem removeGameMenu, removeIgnoreMenu, ignoreDetectedItem;
    ToolStripMenuItem gamingPlanMenu, idlePlanMenu;
    ToolStripMenuItem graceItem, checkItem, hotkeyItem, notifyItem, holdItem, startupItem;

    internal bool failed = false;

    [STAThread]
    static void Main()
    {
        bool created;
        using (Mutex m = new Mutex(true, "GamePowerPlanTrayMutex", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            GamePowerPlan app = new GamePowerPlan();
            if (app.failed) return;
            Application.Run(app);
        }
    }

    GamePowerPlan()
    {
        cfgPath = ChooseConfigPath();
        LoadConfig();
        RefreshPlans();

        if (gamingGuid == null)
        {
            MessageBox.Show("Could not find a High Performance power plan. Run powercfg /list to see your plans.",
                "GamePowerPlan", MessageBoxButtons.OK, MessageBoxIcon.Error);
            failed = true;
            return;
        }

        RebuildIcons();
        BuildMenu();

        tray = new NotifyIcon();
        tray.ContextMenuStrip = menu;
        tray.Icon = iconIdle;
        tray.Text = "GamePowerPlan";
        tray.Visible = true;
        tray.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) ShowFlyout(); };

        timer = new System.Windows.Forms.Timer();
        timer.Interval = checkSeconds * 1000;
        timer.Tick += Tick;
        timer.Start();

        hotkeyWindow = new HotkeyWindow();
        hotkeyWindow.Pressed += (s, e) => CycleMode();
        if (!RegisterHotkeyFromSetting())
            Notify("Hotkey not set", "Could not register " + hotkeyText + ". It may be used by another program.");

        SetMode(Mode.Auto);
    }

    // ---- Menu -------------------------------------------------------------

    static ToolStripMenuItem Item(string text, EventHandler click)
    {
        ToolStripMenuItem it = new ToolStripMenuItem(text);
        if (click != null) it.Click += click;
        return it;
    }

    void BuildMenu()
    {
        menu = new ContextMenuStrip();

        statusItem = Item("Starting...", null);
        statusItem.Enabled = false;

        autoItem = Item("Auto (gaming plan while a game runs)", (s, e) => SetMode(Mode.Auto));
        highItem = Item("Force gaming plan", (s, e) => SetMode(Mode.ForceHigh));
        balItem = Item("Force idle plan (Balanced)", (s, e) => SetMode(Mode.ForceBalanced));

        // Games submenu
        ToolStripMenuItem gamesMenu = Item("Games", null);
        removeGameMenu = Item("Remove game folder", null);
        removeGameMenu.DropDownOpening += (s, e) => RebuildRemoveMenu(removeGameMenu, true);
        RebuildRemoveMenu(removeGameMenu, true);
        removeIgnoreMenu = Item("Remove ignored program", null);
        removeIgnoreMenu.DropDownOpening += (s, e) => RebuildRemoveMenu(removeIgnoreMenu, false);
        RebuildRemoveMenu(removeIgnoreMenu, false);
        ignoreDetectedItem = Item("Ignore detected program", (s, e) => IgnoreDetected());

        gamesMenu.DropDownItems.Add(Item("Add game (pick its .exe)...", (s, e) => AddGameFromExe()));
        gamesMenu.DropDownItems.Add(Item("Add game folder...", (s, e) => AddGameFromFolder()));
        gamesMenu.DropDownItems.Add(removeGameMenu);
        gamesMenu.DropDownItems.Add(new ToolStripSeparator());
        gamesMenu.DropDownItems.Add(ignoreDetectedItem);
        gamesMenu.DropDownItems.Add(Item("Add program to ignore (pick its .exe)...", (s, e) => AddIgnoreFromExe()));
        gamesMenu.DropDownItems.Add(removeIgnoreMenu);

        // Plans submenu
        ToolStripMenuItem plansMenu = Item("Power plans", null);
        gamingPlanMenu = Item("Gaming plan", null);
        idlePlanMenu = Item("Idle plan", null);
        plansMenu.DropDownItems.Add(gamingPlanMenu);
        plansMenu.DropDownItems.Add(idlePlanMenu);
        BuildPlanMenus();

        // Settings submenu
        ToolStripMenuItem settingsMenu = Item("Settings", null);
        graceItem = Item("Grace period...", (s, e) => ChangeGrace());
        checkItem = Item("Check interval...", (s, e) => ChangeInterval());
        hotkeyItem = Item("Hotkey...", (s, e) => ChangeHotkey());
        notifyItem = Item("Notify when plan is changed by something else", (s, e) => { notifyOverride = !notifyOverride; SaveConfig(); });
        holdItem = Item("Hold the plan if something else changes it", (s, e) => { holdPlan = !holdPlan; SaveConfig(); });
        startupItem = Item("Start with Windows", (s, e) => ToggleStartup());
        settingsMenu.DropDownItems.Add(graceItem);
        settingsMenu.DropDownItems.Add(checkItem);
        settingsMenu.DropDownItems.Add(hotkeyItem);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        settingsMenu.DropDownItems.Add(Item("Idle icon colour...", (s, e) => ChangeColour(false)));
        settingsMenu.DropDownItems.Add(Item("Gaming icon colour...", (s, e) => ChangeColour(true)));
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        settingsMenu.DropDownItems.Add(notifyItem);
        settingsMenu.DropDownItems.Add(holdItem);
        settingsMenu.DropDownItems.Add(startupItem);

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoItem);
        menu.Items.Add(highItem);
        menu.Items.Add(balItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(gamesMenu);
        menu.Items.Add(plansMenu);
        menu.Items.Add(settingsMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Reload settings", (s, e) => ReloadSettings()));
        menu.Items.Add(Item("Open settings file", (s, e) => OpenSettingsFile()));
        menu.Items.Add(Item("Exit", (s, e) => ExitApp()));

        menu.Opening += (s, e) => RefreshMenuText();
    }

    void RefreshMenuText()
    {
        graceItem.Text = "Grace period: " + graceSeconds + " s...";
        checkItem.Text = "Check interval: " + checkSeconds + " s...";
        hotkeyItem.Text = "Hotkey: " + (hotkeyText.Length == 0 ? "not set" : hotkeyText) + "...";
        notifyItem.Checked = notifyOverride;
        holdItem.Checked = holdPlan;
        startupItem.Text = packaged ? "Start with Windows (opens Windows Settings)..." : "Start with Windows";
        startupItem.Checked = !packaged && StartupEnabled();
        gamingPlanMenu.Text = "Gaming plan: " + PlanName(gamingGuid) + (gamingPlanSetting == "auto" ? " (auto)" : "");
        idlePlanMenu.Text = "Idle plan: " + PlanName(idleGuid) + (idlePlanSetting == "auto" ? " (auto)" : "");
        bool tracking = (gamePid != 0 && gameName != null);
        ignoreDetectedItem.Enabled = tracking;
        ignoreDetectedItem.Text = tracking ? "Ignore detected program (" + gameName + ")" : "Ignore detected program";
        autoItem.Checked = (mode == Mode.Auto);
        highItem.Checked = (mode == Mode.ForceHigh);
        balItem.Checked = (mode == Mode.ForceBalanced);
    }

    void BuildPlanMenus()
    {
        FillPlanMenu(gamingPlanMenu, true);
        FillPlanMenu(idlePlanMenu, false);
    }

    void FillPlanMenu(ToolStripMenuItem parent, bool gaming)
    {
        parent.DropDownItems.Clear();
        string setting = gaming ? gamingPlanSetting : idlePlanSetting;

        ToolStripMenuItem autoChoice = Item("Auto (recommended)", (s, e) => ChoosePlan(gaming, "auto"));
        autoChoice.Checked = (setting == "auto");
        parent.DropDownItems.Add(autoChoice);

        foreach (PlanInfo p in plans)
        {
            string id = p.Id;
            ToolStripMenuItem it = Item(p.Name, (s, e) => ChoosePlan(gaming, id));
            it.Checked = (setting == id);
            parent.DropDownItems.Add(it);
        }
    }

    void ChoosePlan(bool gaming, string setting)
    {
        if (gaming) gamingPlanSetting = setting; else idlePlanSetting = setting;
        string g = ResolveGaming();
        if (g != null) gamingGuid = g;
        idleGuid = ResolveIdle();
        SaveConfig();
        BuildPlanMenus();
        Tick(null, EventArgs.Empty);
    }

    void RebuildRemoveMenu(ToolStripMenuItem parent, bool isGames)
    {
        parent.DropDownItems.Clear();
        List<string> list = isGames ? games : ignores;
        if (list.Count == 0)
        {
            ToolStripMenuItem none = Item("(none)", null);
            none.Enabled = false;
            parent.DropDownItems.Add(none);
            return;
        }
        foreach (string entry in list.ToArray())
        {
            string text = entry;
            ToolStripMenuItem it = Item(text.Replace("&", "&&"), (s, e) =>
            {
                if (isGames) games.Remove(text); else { ignores.Remove(text); RebuildIgnoreRegexes(); }
                SaveConfig();
                gamePid = 0;
                Notify("Removed", text);
                Tick(null, EventArgs.Empty);
            });
            parent.DropDownItems.Add(it);
        }
    }

    // ---- Modes ------------------------------------------------------------

    void SetMode(Mode m)
    {
        mode = m;
        autoItem.Checked = (m == Mode.Auto);
        highItem.Checked = (m == Mode.ForceHigh);
        balItem.Checked = (m == Mode.ForceBalanced);
        Tick(null, EventArgs.Empty);
    }

    void CycleMode()
    {
        Mode next = mode == Mode.Auto ? Mode.ForceHigh : (mode == Mode.ForceHigh ? Mode.ForceBalanced : Mode.Auto);
        SetMode(next);
        Notify("Power mode", ModeText());
    }

    string ModeText()
    {
        if (mode == Mode.Auto) return "Auto";
        if (mode == Mode.ForceHigh) return "Forced: " + PlanName(gamingGuid);
        return "Forced: " + PlanName(idleGuid);
    }

    // ---- Core loop --------------------------------------------------------

    void Tick(object sender, EventArgs e)
    {
        try { DoTick(); } catch { }
        try { UpdateUi(); } catch { }
    }

    void DoTick()
    {
        bool wantGaming;
        if (mode == Mode.ForceHigh) wantGaming = true;
        else if (mode == Mode.ForceBalanced) wantGaming = false;
        else
        {
            bool gameNow = GameRunning();
            if (gameNow) lastGame = DateTime.Now;
            wantGaming = gameNow || (lastGame != DateTime.MinValue && (DateTime.Now - lastGame).TotalSeconds < graceSeconds);
        }

        string wanted = wantGaming ? gamingGuid : idleGuid;
        string active = GetActivePlan();
        string shown = active;
        if (active == null) active = lastSet;

        bool wantedChanged = (wanted != lastWanted);
        lastWanted = wanted;

        if (active == wanted)
        {
            lastSet = wanted;
        }
        else
        {
            bool overridden = (lastSet != null && active != null && active != lastSet);
            if (overridden) NoticeOverride(active, wanted);

            if (holdPlan || wantedChanged || lastSet == null)
            {
                Apply(wanted);
                shown = wanted;
            }
            else
            {
                lastSet = active;   // accepted the change made by something else
            }
        }
        activeNow = (shown != null) ? shown : lastSet;
    }

    void Apply(string guid)
    {
        Guid g = new Guid(guid);
        uint r = PowerSetActiveScheme(IntPtr.Zero, ref g);
        if (r != 0)
        {
            try { RunPowercfg("/setactive " + guid); } catch { }
        }
        lastSet = guid;
    }

    void NoticeOverride(string active, string wanted)
    {
        if (!notifyOverride) return;
        if ((DateTime.Now - lastNotice).TotalSeconds < 60) return;
        lastNotice = DateTime.Now;
        string text = "The power plan was changed to " + PlanName(active) + " by something else (a program, Windows or another tool). ";
        text += holdPlan ? "Restoring " + PlanName(wanted) + "." : "Leaving it as it is.";
        Notify("Power plan overridden", text);
    }

    void Notify(string title, string text)
    {
        try
        {
            if (tray != null) tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        }
        catch { }
    }

    void UpdateUi()
    {
        bool gaming = (activeNow != null && activeNow == gamingGuid);
        string plan = PlanName(activeNow);
        string modeText = (mode == Mode.Auto) ? "Auto" : "Forced";
        string tip = "Power: " + plan + " (" + modeText + ")";
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        tray.Icon = gaming ? iconGaming : iconIdle;
        statusItem.Text = "Plan: " + plan + " (" + modeText + ")";
    }

    // ---- Power plans ------------------------------------------------------

    static string RunPowercfg(string args)
    {
        ProcessStartInfo psi = new ProcessStartInfo("powercfg.exe", args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        using (Process p = Process.Start(psi))
        {
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o;
        }
    }

    static string GetActivePlan()
    {
        IntPtr p;
        if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return null;
        try
        {
            Guid g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            return g.ToString().ToLowerInvariant();
        }
        finally { LocalFree(p); }
    }

    static List<PlanInfo> ReadPlans()
    {
        List<PlanInfo> list = new List<PlanInfo>();
        string output;
        try { output = RunPowercfg("/list"); } catch { return list; }
        Regex line = new Regex(@"([0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s+\((.*?)\)");
        foreach (Match m in line.Matches(output))
        {
            PlanInfo p = new PlanInfo();
            p.Id = m.Groups[1].Value.ToLowerInvariant();
            string cleaned = Regex.Replace(m.Groups[3].Value, "[^\\u0020-\\u007E]|\\?", "");
            // powercfg prints the trademark symbols as plain letters (Ryzen(TM) becomes RyzenT), so drop those
            cleaned = Regex.Replace(cleaned, "(?<=Ryzen)[TR](?=\\s|$)", "");
            p.Name = cleaned.Trim();
            list.Add(p);
        }
        return list;
    }

    void RefreshPlans()
    {
        List<PlanInfo> found = ReadPlans();
        if (found.Count > 0) plans = found;
        string g = ResolveGaming();
        if (g != null) gamingGuid = g;
        idleGuid = ResolveIdle();
    }

    bool HasPlan(string id)
    {
        foreach (PlanInfo p in plans) if (p.Id == id) return true;
        return false;
    }

    string FindByName(string pattern)
    {
        foreach (PlanInfo p in plans)
            if (Regex.IsMatch(p.Name, pattern, RegexOptions.IgnoreCase)) return p.Id;
        return null;
    }

    string ResolveGaming()
    {
        if (gamingPlanSetting != "auto" && HasPlan(gamingPlanSetting)) return gamingPlanSetting;
        string g = FindByName("ryzen.*high performance");
        if (g == null && HasPlan(StockHigh)) g = StockHigh;
        if (g == null) g = FindByName("^high performance$");
        return g;
    }

    string ResolveIdle()
    {
        if (idlePlanSetting != "auto" && HasPlan(idlePlanSetting)) return idlePlanSetting;
        string g = FindByName("ryzen.*balanced");
        if (g == null && HasPlan(StockBalanced)) g = StockBalanced;
        if (g == null) g = FindByName("^balanced$");
        if (g == null) g = StockBalanced;
        return g;
    }

    string PlanName(string id)
    {
        if (id == null) return "unknown";
        foreach (PlanInfo p in plans) if (p.Id == id) return p.Name;
        return id;
    }

    // ---- Game detection ---------------------------------------------------

    static string GetPath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            StringBuilder sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString(0, size);
            return null;
        }
        finally { CloseHandle(h); }
    }

    static bool PidAlive(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            uint code;
            return GetExitCodeProcess(h, out code) && code == 259;   // STILL_ACTIVE
        }
        finally { CloseHandle(h); }
    }

    int FindGame(out string name)
    {
        name = null;
        foreach (Process p in Process.GetProcesses())
        {
            int id = p.Id;
            p.Dispose();
            string path = GetPath(id);
            if (path == null) continue;

            bool isGame = false;
            foreach (string frag in games)
                if (path.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) { isGame = true; break; }
            if (!isGame) continue;

            string exeName = Path.GetFileNameWithoutExtension(path);
            bool ignored = false;
            foreach (Regex r in ignoreRegexes)
                if (r.IsMatch(exeName)) { ignored = true; break; }
            if (ignored) continue;

            name = exeName;
            return id;
        }
        return 0;
    }

    bool GameRunning()
    {
        if (gamePid != 0)
        {
            if (PidAlive(gamePid)) return true;   // cheap: just check the one game process
            gamePid = 0;
        }
        string name;
        int pid = FindGame(out name);              // full scan only when no game is being tracked
        if (pid != 0)
        {
            gamePid = pid;
            gameName = name;
            gameSince = DateTime.Now;
            return true;
        }
        return false;
    }

    // ---- Adding and removing games ---------------------------------------

    static bool IsTooBroad(string folder)
    {
        string f = folder.TrimEnd('\\') + "\\";
        if (f.Length <= 3) return true;   // a drive root such as D:\
        string[] blocked = new string[] {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (string b in blocked)
            if (b.Length > 0 && string.Equals(f, b.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    void AddGamePath(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        string full = folder.TrimEnd('\\') + "\\";
        if (IsTooBroad(full))
        {
            MessageBox.Show("That folder is too broad. It would count everything inside it (Windows, a whole drive or all of Program Files) as a game.\n\nPick the game's own folder or your games library folder instead.",
                "GamePowerPlan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        foreach (string g in games)
            if (string.Equals(g, full, StringComparison.OrdinalIgnoreCase)) { Notify("Already in the list", full); return; }
        games.Add(full);
        SaveConfig();
        gamePid = 0;
        Notify("Game folder added", full);
        Tick(null, EventArgs.Empty);
    }

    static Form MakeOwner()
    {
        Form o = new Form();
        o.ShowInTaskbar = false;
        o.FormBorderStyle = FormBorderStyle.None;
        o.StartPosition = FormStartPosition.Manual;
        o.Location = new Point(-2000, -2000);
        o.Size = new Size(1, 1);
        o.TopMost = true;
        o.Show();
        return o;
    }

    void AddGameFromExe()
    {
        Form owner = MakeOwner();
        try
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Pick the game's .exe (its folder will be added)";
                d.Filter = "Programs (*.exe)|*.exe";
                if (d.ShowDialog(owner) == DialogResult.OK) AddGamePath(Path.GetDirectoryName(d.FileName));
            }
        }
        finally { owner.Dispose(); }
    }

    void AddGameFromFolder()
    {
        Form owner = MakeOwner();
        try
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Pick the game's folder, or your games library folder";
                if (d.ShowDialog(owner) == DialogResult.OK) AddGamePath(d.SelectedPath);
            }
        }
        finally { owner.Dispose(); }
    }

    void AddIgnoreName(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        foreach (string i in ignores)
            if (string.Equals(i, name, StringComparison.OrdinalIgnoreCase)) { Notify("Already ignored", name); return; }
        ignores.Add(name);
        RebuildIgnoreRegexes();
        SaveConfig();
        gamePid = 0;
        lastGame = DateTime.MinValue;
        Notify("Ignoring", name);
        Tick(null, EventArgs.Empty);
    }

    void AddIgnoreFromExe()
    {
        Form owner = MakeOwner();
        try
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Pick the program to ignore";
                d.Filter = "Programs (*.exe)|*.exe";
                if (d.ShowDialog(owner) == DialogResult.OK) AddIgnoreName(Path.GetFileNameWithoutExtension(d.FileName));
            }
        }
        finally { owner.Dispose(); }
    }

    void IgnoreDetected()
    {
        if (gamePid != 0 && gameName != null) AddIgnoreName(gameName);
    }

    // ---- Settings dialogs -------------------------------------------------

    void ChangeGrace()
    {
        int v = AskNumber("Grace period", "Seconds to stay on the gaming plan after the last game closes (0 to 3600):", 0, 3600, graceSeconds);
        if (v < 0) return;
        graceSeconds = v;
        SaveConfig();
    }

    void ChangeInterval()
    {
        int v = AskNumber("Check interval", "Seconds between checks for a game (1 to 300). 5 is a good balance:", 1, 300, checkSeconds);
        if (v < 0) return;
        checkSeconds = v;
        timer.Interval = checkSeconds * 1000;
        SaveConfig();
    }

    void ChangeColour(bool gaming)
    {
        Form owner = MakeOwner();
        try
        {
            using (ColorDialog d = new ColorDialog())
            {
                d.FullOpen = true;
                d.Color = gaming ? gamingColour : balancedColour;
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                if (gaming) gamingColour = d.Color; else balancedColour = d.Color;
            }
        }
        finally { owner.Dispose(); }
        SaveConfig();
        RebuildIcons();
    }

    void ChangeHotkey()
    {
        string v = CaptureHotkey(hotkeyText);
        if (v == null) return;
        string old = hotkeyText;
        hotkeyText = v;
        if (!RegisterHotkeyFromSetting())
        {
            MessageBox.Show("Windows would not register that combination. Another program may already be using it.",
                "GamePowerPlan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            hotkeyText = old;
            RegisterHotkeyFromSetting();
            return;
        }
        SaveConfig();
        Notify("Hotkey", v.Length == 0 ? "Hotkey cleared" : "Hotkey set to " + v);
    }

    static int AskNumber(string title, string prompt, int min, int max, int current)
    {
        using (Form f = new Form())
        {
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.TopMost = true;
            f.ClientSize = new Size(320, 112);

            Label l = new Label();
            l.Text = prompt;
            l.SetBounds(12, 10, 296, 36);

            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, current));
            n.SetBounds(12, 52, 100, 24);

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(150, 74, 75, 28);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(233, 74, 75, 28);

            f.AcceptButton = ok;
            f.CancelButton = cancel;
            f.Controls.AddRange(new Control[] { l, n, ok, cancel });
            return f.ShowDialog() == DialogResult.OK ? (int)n.Value : -1;
        }
    }

    // Returns the new hotkey text, "" to clear it, or null if cancelled.
    static string CaptureHotkey(string current)
    {
        using (Form f = new Form())
        {
            f.Text = "Set hotkey";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.TopMost = true;
            f.KeyPreview = true;
            f.ClientSize = new Size(340, 124);

            Label info = new Label();
            info.Text = "Press the key combination you want. It must include Ctrl or Alt.";
            info.SetBounds(12, 10, 316, 34);

            Label shown = new Label();
            shown.Text = current.Length == 0 ? "(not set)" : current;
            shown.Font = new Font(f.Font, FontStyle.Bold);
            shown.SetBounds(12, 48, 316, 24);

            Button ok = new Button();
            ok.Text = "Save";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(100, 84, 75, 28);

            Button clear = new Button();
            clear.Text = "Clear";
            clear.DialogResult = DialogResult.No;
            clear.SetBounds(182, 84, 75, 28);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(264, 84, 68, 28);

            string pending = null;
            f.KeyDown += (s, e) =>
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Keys k = e.KeyCode;
                if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return;
                if (!e.Control && !e.Alt) { shown.Text = "Needs Ctrl or Alt"; return; }
                StringBuilder sb = new StringBuilder();
                if (e.Control) sb.Append("Ctrl+");
                if (e.Alt) sb.Append("Alt+");
                if (e.Shift) sb.Append("Shift+");
                sb.Append(k.ToString());
                pending = sb.ToString();
                shown.Text = pending;
            };

            f.CancelButton = cancel;
            f.Controls.AddRange(new Control[] { info, shown, ok, clear, cancel });
            DialogResult r = f.ShowDialog();
            if (r == DialogResult.OK) return pending;   // null if nothing was pressed
            if (r == DialogResult.No) return "";
            return null;
        }
    }

    // ---- Hotkey -----------------------------------------------------------

    static bool ParseHotkey(string text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        foreach (string raw in text.Split('+'))
        {
            string p = raw.Trim();
            if (p.Length == 0) return false;
            string l = p.ToLowerInvariant();
            if (l == "ctrl" || l == "control") mods |= 2;
            else if (l == "alt") mods |= 1;
            else if (l == "shift") mods |= 4;
            else if (l == "win") mods |= 8;
            else
            {
                if (vk != 0) return false;
                int num;
                if (int.TryParse(p, out num) && num >= 0 && num <= 9) p = "D" + p;
                try { vk = (uint)(int)Enum.Parse(typeof(Keys), p, true); }
                catch { return false; }
            }
        }
        return vk != 0 && (mods & 3) != 0;
    }

    void UnregisterHotkey()
    {
        if (hotkeyActive && hotkeyWindow != null) UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
        hotkeyActive = false;
    }

    bool RegisterHotkeyFromSetting()
    {
        UnregisterHotkey();
        if (hotkeyText.Length == 0) return true;
        uint mods, vk;
        if (!ParseHotkey(hotkeyText, out mods, out vk)) return false;
        hotkeyActive = RegisterHotKey(hotkeyWindow.Handle, HotkeyId, mods | 0x4000, vk);   // 0x4000 = no auto-repeat
        return hotkeyActive;
    }

    // ---- Flyout (left click) ---------------------------------------------

    string GameLineText()
    {
        if (mode != Mode.Auto) return "Game detection paused (forced mode)";
        if (gamePid != 0 && gameName != null)
        {
            TimeSpan t = DateTime.Now - gameSince;
            string dur = t.TotalHours >= 1 ? ((int)t.TotalHours) + " h " + t.Minutes + " min" : ((int)t.TotalMinutes) + " min";
            return "Game: " + gameName + " (" + dur + ")";
        }
        if (lastGame != DateTime.MinValue)
        {
            int left = graceSeconds - (int)(DateTime.Now - lastGame).TotalSeconds;
            if (left > 0) return "Game closed, back to idle in " + left + " s";
        }
        return "No game running";
    }

    void ShowFlyout()
    {
        if ((DateTime.Now - flyoutClosed).TotalMilliseconds < 300) return;
        if (flyout != null && !flyout.IsDisposed) return;

        Form f = new Form();
        flyout = f;
        f.FormBorderStyle = FormBorderStyle.None;
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.StartPosition = FormStartPosition.Manual;
        f.BackColor = Color.FromArgb(36, 36, 36);
        f.ForeColor = Color.White;
        f.Size = new Size(280, 184);
        f.Paint += (s, e) => e.Graphics.DrawRectangle(Pens.DimGray, 0, 0, f.Width - 1, f.Height - 1);

        Color grey = Color.FromArgb(170, 170, 170);
        bool gaming = (activeNow != null && activeNow == gamingGuid);

        Func<string, int, int, int, bool, Color, Label> mk = (text, top, h, size, bold, color) =>
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Text = text;
            l.SetBounds(14, top, 252, h);
            l.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            return l;
        };

        f.Controls.Add(mk(PlanName(activeNow), 10, 28, 11, true, gaming ? gamingColour : balancedColour));
        f.Controls.Add(mk("Mode: " + ModeText(), 40, 20, 9, false, Color.White));
        f.Controls.Add(mk(GameLineText(), 62, 20, 9, false, Color.White));
        f.Controls.Add(mk("Gaming plan: " + PlanName(gamingGuid), 92, 18, 8, false, grey));
        f.Controls.Add(mk("Idle plan: " + PlanName(idleGuid), 110, 18, 8, false, grey));

        string[] labels = new string[] { "Auto", "High", "Balanced" };
        Mode[] modes = new Mode[] { Mode.Auto, Mode.ForceHigh, Mode.ForceBalanced };
        for (int i = 0; i < 3; i++)
        {
            Mode target = modes[i];
            Button b = new Button();
            b.Text = labels[i];
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.DimGray;
            b.BackColor = (mode == target) ? Color.FromArgb(70, 110, 170) : Color.FromArgb(52, 52, 52);
            b.ForeColor = Color.White;
            b.SetBounds(14 + i * 86, 140, 80, 30);
            b.Click += (s, e) => { SetMode(target); f.Close(); };
            f.Controls.Add(b);
        }

        f.Deactivate += (s, e) => f.Close();
        f.FormClosed += (s, e) => { flyoutClosed = DateTime.Now; flyout = null; };

        Point p = Cursor.Position;
        Rectangle wa = Screen.FromPoint(p).WorkingArea;
        int x = Math.Min(Math.Max(wa.Left, p.X - f.Width / 2), wa.Right - f.Width);
        int y = p.Y - f.Height - 12;
        if (y < wa.Top) y = p.Y + 12;
        if (y + f.Height > wa.Bottom) y = wa.Bottom - f.Height;
        f.Location = new Point(x, y);

        f.Show();
        f.Activate();
    }

    // ---- Config file ------------------------------------------------------

    static string ChooseConfigPath()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string p = Path.Combine(dir, "GamePowerPlan.cfg");
        try
        {
            using (new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite)) { }
            return p;
        }
        catch
        {
            // the exe's folder is not writable (for example Program Files), so use AppData instead
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GamePowerPlan");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "GamePowerPlan.cfg");
        }
    }

    void SetDefaults()
    {
        checkSeconds = 5;
        graceSeconds = 60;
        balancedColour = Color.FromArgb(46, 160, 67);
        gamingColour = Color.FromArgb(240, 120, 20);
        hotkeyText = "";
        gamingPlanSetting = "auto";
        idlePlanSetting = "auto";
        notifyOverride = true;
        holdPlan = true;
    }

    static bool TryColour(string s, out Color c)
    {
        c = Color.Empty;
        try
        {
            c = ColorTranslator.FromHtml(s);
            return !c.IsEmpty;
        }
        catch { return false; }
    }

    static string ColourText(Color c)
    {
        return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
    }

    static bool ParseBool(string v, bool fallback)
    {
        string l = v.ToLowerInvariant();
        if (l == "true" || l == "yes" || l == "1" || l == "on") return true;
        if (l == "false" || l == "no" || l == "0" || l == "off") return false;
        return fallback;
    }

    void ApplySetting(string line)
    {
        int eq = line.IndexOf('=');
        if (eq < 1) return;
        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
        string v = line.Substring(eq + 1).Trim();
        int n;
        Color c;
        switch (k)
        {
            case "check_seconds":
                if (int.TryParse(v, out n)) checkSeconds = Math.Max(1, Math.Min(300, n));
                break;
            case "grace_seconds":
                if (int.TryParse(v, out n)) graceSeconds = Math.Max(0, Math.Min(3600, n));
                break;
            case "balanced_colour":
                if (TryColour(v, out c)) balancedColour = c;
                break;
            case "gaming_colour":
                if (TryColour(v, out c)) gamingColour = c;
                break;
            case "hotkey":
                hotkeyText = v;
                break;
            case "gaming_plan":
                gamingPlanSetting = v.Length == 0 ? "auto" : v.ToLowerInvariant();
                break;
            case "idle_plan":
                idlePlanSetting = v.Length == 0 ? "auto" : v.ToLowerInvariant();
                break;
            case "notify_on_override":
                notifyOverride = ParseBool(v, true);
                break;
            case "hold_plan":
                holdPlan = ParseBool(v, true);
                break;
        }
    }

    void RebuildIgnoreRegexes()
    {
        List<Regex> list = new List<Regex>();
        foreach (string pat in ignores)
        {
            if (pat.Length == 0) continue;
            list.Add(new Regex("^" + Regex.Escape(pat).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase));
        }
        ignoreRegexes = list;
    }

    void ImportOldOrDefaults()
    {
        games = new List<string>();
        ignores = new List<string>();
        string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GamePowerPlan", "GamePowerPlan.txt");
        bool imported = false;
        try
        {
            if (File.Exists(old))
            {
                foreach (string raw in File.ReadAllLines(old))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line[0] == '!') { if (line.Length > 1) ignores.Add(line.Substring(1).Trim()); }
                    else games.Add(line);
                }
                imported = true;
            }
        }
        catch { }
        if (games.Count == 0) games.AddRange(DefaultGames);
        if (!imported) ignores.AddRange(DefaultIgnore);
    }

    void LoadConfig()
    {
        SetDefaults();
        bool isNew = !File.Exists(cfgPath) || new FileInfo(cfgPath).Length == 0;
        if (isNew)
        {
            ImportOldOrDefaults();
            RebuildIgnoreRegexes();
            SaveConfig();
            return;
        }

        string[] lines;
        try { lines = File.ReadAllLines(cfgPath); }
        catch { return; }

        List<string> newGames = new List<string>();
        List<string> newIgnores = new List<string>();
        bool sawGames = false;
        string section = "";
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line[0] == '[' && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                if (section == "games") sawGames = true;
                continue;
            }
            if (section == "settings") ApplySetting(line);
            else if (section == "games") newGames.Add(line);
            else if (section == "ignore") newIgnores.Add(line);
        }
        if (!sawGames) newGames.AddRange(DefaultGames);
        games = newGames;
        ignores = newIgnores;
        RebuildIgnoreRegexes();
    }

    void SaveConfig()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# GamePowerPlan settings. Everything here can be changed from the tray menu.");
        sb.AppendLine("# If you edit this file by hand, choose Reload settings in the tray menu to apply the changes.");
        sb.AppendLine();
        sb.AppendLine("[settings]");
        sb.AppendLine("# seconds between checks for a game (1 to 300)");
        sb.AppendLine("check_seconds=" + checkSeconds);
        sb.AppendLine("# seconds to stay on the gaming plan after the last game closes (0 to 3600)");
        sb.AppendLine("grace_seconds=" + graceSeconds);
        sb.AppendLine("# tray icon colours as #RRGGBB");
        sb.AppendLine("balanced_colour=" + ColourText(balancedColour));
        sb.AppendLine("gaming_colour=" + ColourText(gamingColour));
        sb.AppendLine("# global hotkey that cycles Auto, forced gaming plan, forced Balanced (blank = none), for example Ctrl+Alt+P");
        sb.AppendLine("hotkey=" + hotkeyText);
        sb.AppendLine("# plans: auto, or a plan GUID from powercfg /list");
        sb.AppendLine("gaming_plan=" + gamingPlanSetting);
        sb.AppendLine("idle_plan=" + idlePlanSetting);
        sb.AppendLine("# show a notification when something else changes the power plan");
        sb.AppendLine("notify_on_override=" + (notifyOverride ? "true" : "false"));
        sb.AppendLine("# put the plan back when something else changes it");
        sb.AppendLine("hold_plan=" + (holdPlan ? "true" : "false"));
        sb.AppendLine();
        sb.AppendLine("[games]");
        sb.AppendLine("# any running program whose path contains one of these lines counts as a game");
        foreach (string g in games) sb.AppendLine(g);
        sb.AppendLine();
        sb.AppendLine("[ignore]");
        sb.AppendLine("# program names (without .exe) to ignore even inside a game folder, * works as a wildcard");
        foreach (string i in ignores) sb.AppendLine(i);

        try { File.WriteAllText(cfgPath, sb.ToString()); }
        catch { Notify("Could not save settings", cfgPath); }
    }

    void OpenSettingsFile()
    {
        try { Process.Start("notepad.exe", "\"" + cfgPath + "\""); } catch { }
    }

    void ReloadSettings()
    {
        LoadConfig();
        RefreshPlans();
        timer.Interval = checkSeconds * 1000;
        RebuildIcons();
        BuildPlanMenus();
        if (!RegisterHotkeyFromSetting())
            Notify("Hotkey not set", "Could not register " + hotkeyText + ". It may be used by another program.");
        gamePid = 0;
        lastSet = null;
        Tick(null, EventArgs.Empty);
        Notify("Settings reloaded", "Using " + PlanName(gamingGuid) + " for games and " + PlanName(idleGuid) + " otherwise.");
    }

    // ---- Startup, icons, exit ---------------------------------------------

    static bool StartupEnabled()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            return k != null && k.GetValue(RunName) != null;
        }
    }

    void ToggleStartup()
    {
        if (packaged)
        {
            try { Process.Start("ms-settings:startupapps"); } catch { }
            return;
        }
        bool enabled = StartupEnabled();
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k != null)
            {
                if (enabled) k.DeleteValue(RunName, false);
                else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
            }
        }
        startupItem.Checked = StartupEnabled();
    }

    static Icon MakeIcon(Color c, out IntPtr handle)
    {
        Bitmap bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Brush b = new SolidBrush(c)) g.FillEllipse(b, 3, 3, 26, 26);
        }
        handle = bmp.GetHicon();
        bmp.Dispose();
        return Icon.FromHandle(handle);
    }

    void RebuildIcons()
    {
        IntPtr oldIdle = hIdle;
        IntPtr oldGaming = hGaming;
        iconIdle = MakeIcon(balancedColour, out hIdle);
        iconGaming = MakeIcon(gamingColour, out hGaming);
        if (tray != null) UpdateUi();
        if (oldIdle != IntPtr.Zero) DestroyIcon(oldIdle);
        if (oldGaming != IntPtr.Zero) DestroyIcon(oldGaming);
    }

    void ExitApp()
    {
        timer.Stop();
        UnregisterHotkey();
        tray.Visible = false;
        tray.Dispose();
        if (hIdle != IntPtr.Zero) DestroyIcon(hIdle);
        if (hGaming != IntPtr.Zero) DestroyIcon(hGaming);
        ExitThread();
    }
}
