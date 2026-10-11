// Installer / uninstaller for Jet's Admin Panel. Per-user install (no admin rights needed).
// Embeds JetsAdminPanel.exe as the resource "app.exe". C# 5 compatible.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Jet's Admin Panel Setup")]
[assembly: AssemblyProduct("Jet's Admin Panel")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Setup
{
    const string AppName = "Jet's Admin Panel";
    const string AppExe = "JetsAdminPanel.exe";
    const string UninstExe = "Uninstall.exe";
    const string Version = "1.0.0";
    const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\JetsAdminPanel";

    static string DefaultDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Jets Admin Panel"); }
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

        string dir = DefaultDir; bool desktop = true, start = true;
        if (!silent)
        {
            using (var f = new InstallForm(dir))
            {
                if (f.ShowDialog() != DialogResult.OK) return 1;
                dir = f.InstallDir; desktop = f.Desktop; start = f.StartMenu;
            }
        }
        try
        {
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
    TextBox txtDir; CheckBox chkDesk, chkStart;
    public string InstallDir { get { return txtDir.Text.Trim(); } }
    public bool Desktop { get { return chkDesk.Checked; } }
    public bool StartMenu { get { return chkStart.Checked; } }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

    void DarkStyle()
    {
        Color bg = Color.FromArgb(13, 17, 23), text = Color.FromArgb(230, 237, 243), muted = Color.FromArgb(139, 152, 168);
        Color accent = Color.FromArgb(47, 191, 143), surf = Color.FromArgb(33, 40, 51), border = Color.FromArgb(48, 56, 68);
        BackColor = bg; ForeColor = text;
        foreach (Control c in Controls)
        {
            Button b = c as Button; TextBox tb = c as TextBox; CheckBox ck = c as CheckBox; Label l = c as Label;
            if (b != null)
            {
                bool primary = b.Text == "Install";
                b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false; b.Cursor = Cursors.Hand; b.Height = 30;
                b.BackColor = primary ? accent : surf; b.ForeColor = primary ? Color.FromArgb(6, 24, 18) : text;
                b.FlatAppearance.BorderColor = primary ? accent : border;
            }
            else if (tb != null) { tb.BackColor = Color.FromArgb(10, 14, 19); tb.ForeColor = text; tb.BorderStyle = BorderStyle.FixedSingle; }
            else if (ck != null) { ck.FlatStyle = FlatStyle.Flat; ck.ForeColor = text; }
            else if (l != null) { l.ForeColor = l.ForeColor == Color.DimGray ? muted : text; }
        }
        HandleCreated += delegate { try { int v = 1; if (DwmSetWindowAttribute(Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref v, 4); } catch { } };
    }

    public InstallForm(string defDir)
    {
        Text = "Jet's Admin Panel Setup";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(500, 270);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var head = new Label { Text = "Install Jet's Admin Panel", Left = 20, Top = 16, Width = 460, Height = 28, Font = new Font("Segoe UI", 14f, FontStyle.Bold) };
        var sub = new Label { Left = 20, Top = 52, Width = 460, Height = 40, ForeColor = Color.DimGray,
            Text = "A Windows admin tool for Rust servers using RCON: live player map, player management, console, chat, bans, restart scheduler." };
        var l1 = new Label { Text = "Install to:", Left = 20, Top = 104, Width = 200 };
        txtDir = new TextBox { Left = 20, Top = 124, Width = 370, Text = defDir };
        var browse = new Button { Text = "Browse...", Left = 398, Top = 122, Width = 82 };
        browse.Click += delegate
        {
            using (var d = new FolderBrowserDialog { SelectedPath = txtDir.Text }) if (d.ShowDialog() == DialogResult.OK) txtDir.Text = Path.Combine(d.SelectedPath, "Jets Admin Panel");
        };
        chkStart = new CheckBox { Text = "Add a Start Menu shortcut", Left = 20, Top = 160, Width = 300, Checked = true };
        chkDesk = new CheckBox { Text = "Add a Desktop shortcut", Left = 20, Top = 184, Width = 300, Checked = true };
        var ok = new Button { Text = "Install", Left = 312, Top = 226, Width = 84, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 402, Top = 226, Width = 84, DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { head, sub, l1, txtDir, browse, chkStart, chkDesk, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;
        DarkStyle();
        FormClosing += delegate (object s, FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK && txtDir.Text.Trim().Length < 4) { MessageBox.Show("Please choose an install folder."); e.Cancel = true; }
        };
    }
}
