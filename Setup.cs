// Installer / uninstaller for Jets Rust Admin. Per-user install (no admin rights needed).
// Embeds JetsRustAdmin.exe as the resource "app.exe". C# 5 compatible.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Jets Rust Admin Setup")]
[assembly: AssemblyProduct("Jets Rust Admin")]
[assembly: AssemblyVersion("2.0.3.0")]
[assembly: AssemblyFileVersion("2.0.3.0")]

static class Setup
{
    const string AppName = "Jets Rust Admin";
    const string AppExe = "JetsRustAdmin.exe";
    const string UninstExe = "Uninstall.exe";
    const string Version = "2.0.3";
    const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\JetsRustAdmin";
    const string OldRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\JetsAdminPanel";

    // The previous name of this app. Its settings are picked up automatically by Jets Rust Admin on first start.
    public static bool OldInstalled
    {
        get { try { using (var k = Registry.CurrentUser.OpenSubKey(OldRegPath)) return k != null; } catch { return false; } }
    }
    static void RemoveOld()
    {
        try
        {
            string cmd;
            using (var k = Registry.CurrentUser.OpenSubKey(OldRegPath)) cmd = k == null ? null : Convert.ToString(k.GetValue("QuietUninstallString"));
            if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith("\"")) return;
            int q = cmd.IndexOf('"', 1);
            string exe = cmd.Substring(1, q - 1), args = cmd.Substring(q + 1).Trim();
            if (!File.Exists(exe)) { Registry.CurrentUser.DeleteSubKeyTree(OldRegPath, false); return; }
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(20000);
        }
        catch { }
    }

    static string DefaultDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Jets Rust Admin"); }
    }
    static string StartMenuLink
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); }
    }
    static string DesktopLink
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); }
    }

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool uninstall = false, silent = false;
        foreach (string a in args)
        {
            if (a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
            if (a.Equals("/silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/S", StringComparison.OrdinalIgnoreCase)) silent = true;
        }
        if (uninstall) return DoUninstall(silent);

        string dir = DefaultDir; bool desktop = true, start = true, removeOld = OldInstalled;
        if (!silent)
        {
            using (var f = new InstallForm(dir))
            {
                if (f.ShowDialog() != DialogResult.OK) return 1;
                dir = f.InstallDir; desktop = f.Desktop; start = f.StartMenu; removeOld = f.RemoveOld;
            }
        }
        try
        {
            if (removeOld) RemoveOld();
            string exe = Install(dir, desktop, start);
            if (silent) return 0;
            if (MessageBox.Show(AppName + " was installed.\n\nLaunch it now?", AppName + " Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(exe);
            return 0;
        }
        catch (Exception e)
        {
            if (!silent) MessageBox.Show("Installation failed:\n\n" + e.Message + "\n\nIf the app is running, close it and try again.", AppName + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    static string Install(string dir, bool desktop, bool start)
    {
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, AppExe);
        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.exe"))
        using (var fs = new FileStream(exe, FileMode.Create, FileAccess.Write))
        {
            if (res == null) throw new InvalidOperationException("Installer is damaged (app payload missing).");
            res.CopyTo(fs);
        }
        string uninst = Path.Combine(dir, UninstExe);
        File.Copy(Application.ExecutablePath, uninst, true);

        if (start) TryShortcut(StartMenuLink, exe, dir); else TryDelete(StartMenuLink);
        if (desktop) TryShortcut(DesktopLink, exe, dir); else TryDelete(DesktopLink);

        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegPath))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Jet");
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)((new FileInfo(exe).Length + new FileInfo(uninst).Length) / 1024), RegistryValueKind.DWord);
            }
        }
        catch { }
        return exe;
    }

    static int DoUninstall(bool silent)
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        if (!silent && MessageBox.Show("Remove " + AppName + " from this computer?\n\nYour saved server profiles and settings are kept.", AppName + " Uninstall",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;

        foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)))
        {
            try { if (p.MainModule.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) { p.CloseMainWindow(); if (!p.WaitForExit(3000)) p.Kill(); } } catch { }
        }
        TryDelete(StartMenuLink); TryDelete(DesktopLink);
        try { Registry.CurrentUser.DeleteSubKeyTree(RegPath, false); } catch { }
        TryDelete(Path.Combine(dir, AppExe));

        // The uninstaller is the running file, so remove the folder just after we exit.
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
        }
        catch { }
        if (!silent) MessageBox.Show(AppName + " was removed.", AppName + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    // Creates a .lnk through the Windows Script Host COM object (late-bound, no extra references).
    static void TryShortcut(string linkPath, string target, string workDir)
    {
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { linkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { AppName });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
        catch { }
    }
}

class InstallForm : Form
{
    TextBox txtDir; CheckBox chkDesk, chkStart, chkOld;
    public string InstallDir { get { return txtDir.Text.Trim(); } }
    public bool Desktop { get { return chkDesk.Checked; } }
    public bool StartMenu { get { return chkStart.Checked; } }
    public bool RemoveOld { get { return chkOld != null && chkOld.Checked; } }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

    static readonly Color bg = Color.FromArgb(11, 14, 19), text = Color.FromArgb(232, 238, 244), muted = Color.FromArgb(138, 151, 167);
    static readonly Color accent = Color.FromArgb(240, 128, 56), accentHover = Color.FromArgb(252, 152, 84), surf = Color.FromArgb(31, 38, 48);
    static readonly Color hoverC = Color.FromArgb(40, 49, 61), border = Color.FromArgb(44, 52, 64), input = Color.FromArgb(13, 17, 23);

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // Rounded pill buttons, painted over the standard Button.
    static void SkinButton(Button b, bool primary)
    {
        bool hover = false;
        b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.UseVisualStyleBackColor = false; b.Cursor = Cursors.Hand; b.Height = 32;
        b.MouseEnter += delegate { hover = true; b.Invalidate(); };
        b.MouseLeave += delegate { hover = false; b.Invalidate(); };
        b.Paint += delegate (object s, PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(b.Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = primary ? (hover ? accentHover : accent) : (hover ? hoverC : surf);
            using (var path = Round(new RectangleF(0.5f, 0.5f, b.Width - 1.5f, b.Height - 1.5f), 8))
            {
                using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                using (var pen = new Pen(primary ? fill : border)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, b.Text, b.Font, b.ClientRectangle, primary ? Color.FromArgb(32, 14, 4) : text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.HidePrefix);
        };
    }

    // Rounded frame around the install-folder box.
    static Panel Frame(TextBox t)
    {
        var host = new Panel { Left = t.Left, Top = t.Top, Width = t.Width, Height = 32, BackColor = input };
        t.BorderStyle = BorderStyle.None; t.BackColor = input; t.ForeColor = text;
        t.Left = 10; t.Width = host.Width - 20; t.Top = (32 - t.Height) / 2;
        host.Controls.Add(t);
        host.Paint += delegate (object s, PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(new RectangleF(0.5f, 0.5f, host.Width - 1.5f, host.Height - 1.5f), 8))
            {
                using (var br = new SolidBrush(input)) g.FillPath(br, path);
                using (var pen = new Pen(t.Focused ? accent : border)) g.DrawPath(pen, path);
            }
        };
        t.Enter += delegate { host.Invalidate(); }; t.Leave += delegate { host.Invalidate(); };
        return host;
    }

    void DarkStyle()
    {
        BackColor = bg; ForeColor = text;
        foreach (Control c in Controls)
        {
            Button b = c as Button; CheckBox ck = c as CheckBox; Label l = c as Label;
            if (b != null) SkinButton(b, b.Text == "Install");
            else if (ck != null) { ck.FlatStyle = FlatStyle.Flat; ck.ForeColor = text; ck.Cursor = Cursors.Hand; }
            else if (l != null) { l.ForeColor = l.ForeColor == Color.DimGray ? muted : text; }
        }
        HandleCreated += delegate
        {
            try
            {
                int v = 1; if (DwmSetWindowAttribute(Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref v, 4);
                v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4);                          // rounded corners (Windows 11)
                v = 32 | (38 << 8) | (48 << 16); DwmSetWindowAttribute(Handle, 35, ref v, 4); // caption colour
            }
            catch { }
        };
    }

    public InstallForm(string defDir)
    {
        Text = "Jets Rust Admin Setup";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(520, Setup.OldInstalled ? 318 : 290);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var head = new Label { Text = "Install Jets Rust Admin", Left = 24, Top = 20, Width = 470, Height = 30, Font = new Font("Segoe UI", 15f, FontStyle.Bold) };
        var sub = new Label { Left = 24, Top = 56, Width = 470, Height = 40, ForeColor = Color.DimGray,
            Text = "A Windows admin tool for Rust servers using RCON: live player map, player management, console, chat, bans, restart scheduler." };
        var l1 = new Label { Text = "Install to:", Left = 24, Top = 106, Width = 200 };
        txtDir = new TextBox { Left = 24, Top = 128, Width = 370, Text = defDir };
        var browse = new Button { Text = "Browse...", Left = 402, Top = 128, Width = 94 };
        browse.Click += delegate
        {
            using (var d = new FolderBrowserDialog { SelectedPath = txtDir.Text }) if (d.ShowDialog() == DialogResult.OK) txtDir.Text = Path.Combine(d.SelectedPath, "Jets Rust Admin");
        };
        chkStart = new CheckBox { Text = "Add a Start Menu shortcut", Left = 24, Top = 172, Width = 320, Checked = true };
        chkDesk = new CheckBox { Text = "Add a Desktop shortcut", Left = 24, Top = 198, Width = 320, Checked = true };
        int by = 240;
        if (Setup.OldInstalled)
        {
            chkOld = new CheckBox { Text = "Remove the old Jet's Admin Panel (your profiles and settings carry over)", Left = 24, Top = 224, Width = 480, Checked = true };
            by = 268;
        }
        var ok = new Button { Text = "Install", Left = 308, Top = by, Width = 92, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 406, Top = by, Width = 92, DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { head, sub, l1, browse, chkStart, chkDesk, ok, cancel });
        if (chkOld != null) Controls.Add(chkOld);
        Controls.Add(Frame(txtDir));
        AcceptButton = ok; CancelButton = cancel;
        DarkStyle();
        FormClosing += delegate (object s, FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK && txtDir.Text.Trim().Length < 4) { MessageBox.Show("Please choose an install folder."); e.Cancel = true; }
        };
    }
}
