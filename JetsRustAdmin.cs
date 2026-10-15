// Jets Rust Admin - Windows desktop admin tool using WebRCON.
// Single file, C# 5 compatible so it builds with the compiler that ships with Windows (see build.bat).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Jets Rust Admin")]
[assembly: AssemblyProduct("Jets Rust Admin")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

namespace JetsRustAdmin
{
    // ------------------------------------------------------------------ config
    class Profile { public string Name = ""; public string Host = ""; public int Port = 28016; public string PwEnc = ""; }

    class Cfg
    {
        public List<Profile> Profiles = new List<Profile>();
        public string LastProfile = "";
        public string SchedTimes = "04:00,16:00";
        public string SchedWarn = "30,15,10,5,2,1";
        public string SchedMsg = "Server restarting in {time}";
        public string SchedReason = "Scheduled restart";
        public bool SchedEnabled = false;
        public List<string> Templates = new List<string> { "Kill|global.killplayer {id}", "Heal|heal {id}" };
        public string Host = "127.0.0.1";
        public int Port = 28016;
        public string PwEnc = "";
        public bool RememberPw = true;
        public string MapPath = "";
        public int PollSeconds = 5;
        public bool AutoReconnect = true;
        public bool ShowNames = true;
        public bool TrackPos = true;
        public int WorldSize = 0;
        public string RmKeyEnc = "";
        public int MapMargin = 0;
        public static string MapDir { get { return Path.Combine(Dir, "maps"); } }

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetsRustAdmin"); } }
        static string FilePath { get { return Path.Combine(Dir, "config.json"); } }

        static string OldDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetsAdminPanel"); } }

        // First run after the rename: bring over profiles, RustMaps key and cached maps from Jet's Admin Panel.
        static void MigrateOld()
        {
            try
            {
                if (File.Exists(FilePath) || !Directory.Exists(OldDir)) return;
                Directory.CreateDirectory(Dir);
                foreach (string f in Directory.GetFiles(OldDir, "*", SearchOption.AllDirectories))
                {
                    string dest = Path.Combine(Dir, f.Substring(OldDir.Length).TrimStart('\\', '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (!File.Exists(dest)) File.Copy(f, dest);
                }
            }
            catch { }
        }

        public static Cfg Load()
        {
            MigrateOld();
            try { if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<Cfg>(File.ReadAllText(FilePath)); }
            catch { }
            return new Cfg();
        }
        public void Save()
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); }
            catch { }
        }
        public static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        public static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
    }

    // ------------------------------------------------------------------ helpers
    static class U
    {
        public static float WorldSize = 4000f;
        public const float GridCell = 146.3f;
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static object Parse(string json)
        {
            try { return Ser.DeserializeObject(json); } catch { return null; }
        }
        public static string ToJson(object o) { return Ser.Serialize(o); }

        public static object Get(object d, string key)
        {
            var dict = d as Dictionary<string, object>;
            if (dict == null) return null;
            object v;
            return dict.TryGetValue(key, out v) ? v : null;
        }
        public static string Str(object d, string key)
        {
            object v = Get(d, key);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static double Num(object d, string key)
        {
            object v = Get(d, key);
            if (v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
        // Finds the first string value for a key anywhere in a parsed JSON tree (case-insensitive).
        public static string FindStr(object node, string name)
        {
            var d = node as Dictionary<string, object>;
            if (d != null)
            {
                foreach (var kv in d)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value is string && ((string)kv.Value).Length > 0) return (string)kv.Value;
                foreach (var kv in d) { string r = FindStr(kv.Value, name); if (r != null) return r; }
                return null;
            }
            var arr = node as System.Collections.IEnumerable;
            if (arr != null && !(node is string))
                foreach (object o in arr) { string r = FindStr(o, name); if (r != null) return r; }
            return null;
        }
        public static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "'") + "\""; }

        public static string Dur(double secs)
        {
            var t = TimeSpan.FromSeconds(secs);
            if (t.TotalDays >= 1) return string.Format("{0}d {1}h {2}m", (int)t.TotalDays, t.Hours, t.Minutes);
            if (t.TotalHours >= 1) return string.Format("{0}h {1}m", (int)t.TotalHours, t.Minutes);
            return string.Format("{0}m {1}s", t.Minutes, t.Seconds);
        }

        public static string Grid(float x, float z)
        {
            int col = (int)Math.Floor((x + WorldSize / 2f) / GridCell);
            int row = (int)Math.Floor((WorldSize / 2f - z) / GridCell);
            if (col < 0 || row < 0) return "-";
            string letters = col < 26 ? ((char)('A' + col)).ToString() : "A" + (char)('A' + (col - 26) % 26);
            return letters + row;
        }

        static readonly Regex PosRx = new Regex(@"(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
        public static bool TryPos(string reply, out float x, out float y, out float z)
        {
            x = y = z = 0;
            if (string.IsNullOrEmpty(reply)) return false;
            var m = PosRx.Match(reply);
            if (!m.Success) return false;
            x = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            y = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            z = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            return true;
        }

        public static string ConvarValue(string reply)
        {
            if (reply == null) return "";
            int i = reply.IndexOf(':');
            string v = i >= 0 ? reply.Substring(i + 1).Trim() : reply.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v;
        }

        public static string Ask(string title, string label, string def) { return Ask(title, label, def, false); }
        public static string Ask(string title, string label, string def, bool pw)
        {
            using (var f = new Form())
            {
                f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                var l = new Label { Text = label, Left = 20, Top = 18, Width = 384, AutoSize = false };
                l.Height = Math.Max(20, TextRenderer.MeasureText(label, Theme.F(9.5f), new Size(384, 0), TextFormatFlags.WordBreak).Height + 2);
                int ty = l.Bottom + 10;
                var t = new TextBox { Left = 20, Top = ty, Width = 384, Text = def ?? "", UseSystemPasswordChar = pw };
                var ok = new Button { Text = "OK", Left = 222, Top = ty + 50, Width = 88, Height = 32, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 316, Top = ty + 50, Width = 88, Height = 32, DialogResult = DialogResult.Cancel };
                f.ClientSize = new Size(424, ty + 50 + 32 + 18);
                f.Controls.AddRange(new Control[] { l, t, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Font = Theme.F(9.5f); Theme.Apply(f); f.HandleCreated += delegate { Theme.DarkTitle(f); };
                return f.ShowDialog(Form.ActiveForm) == DialogResult.OK ? t.Text : null;
            }
        }
        public static bool Confirm(string text)
        {
            return Dlg.Show(text, "Confirm", true) == DialogResult.Yes;
        }
    }

    class PlayerInfo
    {
        public string Id = "", Name = "", Ip = "";
        public int Ping;
        public int ConnectedSeconds;
        public double Health;
        public bool HasPos;
        public float X, Y, Z;
        public string GridRef { get { return HasPos ? U.Grid(X, Z) : "-"; } }
    }

    class LvSorter : System.Collections.IComparer
    {
        public int Col;
        public bool Asc = true;
        public int Compare(object a, object b)
        {
            string x = ((ListViewItem)a).SubItems[Col].Text, y = ((ListViewItem)b).SubItems[Col].Text;
            double dx, dy; int r;
            if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out dx) &&
                double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out dy)) r = dx.CompareTo(dy);
            else r = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            return Asc ? r : -r;
        }
    }

    // ------------------------------------------------------------------ WebRCON client
    class RconClient
    {
        ClientWebSocket ws;
        CancellationTokenSource cts;
        int nextId = 1000;
        bool closing;
        readonly Dictionary<int, TaskCompletionSource<string>> pending = new Dictionary<int, TaskCompletionSource<string>>();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

        public event Action<string, string> Log;
        public event Action<string> Closed;

        public bool Connected { get { return ws != null && ws.State == WebSocketState.Open; } }

        public async Task ConnectAsync(string host, int port, string password)
        {
            ws = new ClientWebSocket();
            cts = new CancellationTokenSource();
            var uri = new Uri("ws://" + host + ":" + port + "/" + Uri.EscapeDataString(password));
            using (var t = new CancellationTokenSource(8000))
                await ws.ConnectAsync(uri, t.Token);
            ReceiveLoop();
        }

        async void ReceiveLoop()
        {
            var buf = new byte[16384];
            string reason = "Connection closed";
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) throw new Exception("Server closed the connection");
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    Handle(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
            catch (Exception e) { reason = e.Message; }

            lock (pending)
            {
                foreach (var kv in pending) kv.Value.TrySetException(new Exception("Disconnected"));
                pending.Clear();
            }
            if (!closing && Closed != null) Closed(reason);
        }

        void Handle(string json)
        {
            object o = U.Parse(json);
            if (o == null) return;
            int id = (int)U.Num(o, "Identifier");
            string msg = U.Str(o, "Message");
            TaskCompletionSource<string> tcs = null;
            if (id >= 1000)
            {
                lock (pending) { if (pending.TryGetValue(id, out tcs)) pending.Remove(id); }
            }
            if (tcs != null) { tcs.TrySetResult(msg); return; }
            if (Log != null) Log(U.Str(o, "Type"), msg);
        }

        public async Task<string> Cmd(string cmd, int timeoutMs = 6000)
        {
            if (!Connected) throw new InvalidOperationException("Not connected");
            int id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<string>();
            lock (pending) pending[id] = tcs;
            var payload = new Dictionary<string, object>();
            payload["Identifier"] = id; payload["Message"] = cmd; payload["Name"] = "JetsRustAdmin";
            var bytes = Encoding.UTF8.GetBytes(U.ToJson(payload));
            await sendLock.WaitAsync();
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token); }
            finally { sendLock.Release(); }
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (done != tcs.Task)
            {
                lock (pending) pending.Remove(id);
                throw new TimeoutException("No reply to: " + cmd);
            }
            return tcs.Task.Result;
        }

        public void Close()
        {
            closing = true;
            try { if (cts != null) cts.Cancel(); } catch { }
            try { if (ws != null) ws.Abort(); } catch { }
        }
    }

    // ------------------------------------------------------------------ live map control
    class MapControl : Control
    {
        public float WorldSize = 4000f;
        public float Margin;
        public Image Bg;
        public List<PlayerInfo> Players = new List<PlayerInfo>();
        public string SelectedId = "";
        public bool ShowNames = true;
        public event Action<PlayerInfo> PlayerClicked;

        float zoom = 1f, panX, panY;
        Point lastMouse, downPt;
        bool dragging;
        PlayerInfo hover;
        string coordText = "";
        readonly ToolTip tip = new ToolTip();

        public MapControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(9, 13, 18);
        }

        float Side { get { return Math.Min(Width, Height) * zoom; } }
        float OX { get { return (Width - Side) / 2f + panX; } }
        float OY { get { return (Height - Side) / 2f + panY; } }

        PointF ToScreen(float x, float z)
        {
            float s = Side;
            return new PointF(OX + (x / WorldSize + 0.5f) * s, OY + (0.5f - z / WorldSize) * s);
        }
        PointF ToWorld(Point p)
        {
            float s = Side;
            return new PointF(((p.X - OX) / s - 0.5f) * WorldSize, (0.5f - (p.Y - OY) / s) * WorldSize);
        }

        public void ResetView() { zoom = 1f; panX = panY = 0; Invalidate(); }
        public void CenterOn(PlayerInfo p)
        {
            if (p == null || !p.HasPos) return;
            zoom = Math.Max(zoom, 3f);
            var s = ToScreen(p.X, p.Z);
            panX += Width / 2f - s.X; panY += Height / 2f - s.Y;
            Invalidate();
        }

        PlayerInfo HitTest(Point pt)
        {
            PlayerInfo best = null; float bd = 11f * 11f;
            foreach (var p in Players)
            {
                if (!p.HasPos) continue;
                var s = ToScreen(p.X, p.Z);
                float d = (s.X - pt.X) * (s.X - pt.X) + (s.Y - pt.Y) * (s.Y - pt.Y);
                if (d <= bd) { bd = d; best = p; }
            }
            return best;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float s = Side, ox = OX, oy = OY;
            var rect = new RectangleF(ox, oy, s, s);

            if (Bg != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float mpx = Margin / WorldSize * s;
                g.DrawImage(Bg, new RectangleF(rect.X - mpx, rect.Y - mpx, rect.Width + 2 * mpx, rect.Height + 2 * mpx));
            }
            else
            {
                using (var br = new SolidBrush(Color.FromArgb(20, 35, 33))) g.FillRectangle(br, rect);
            }

            // grid
            float cell = U.GridCell / WorldSize * s;
            int n = (int)Math.Ceiling(WorldSize / U.GridCell);
            if (cell > 8)
            {
                using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
                {
                    for (int i = 0; i <= n; i++)
                    {
                        float x = ox + i * cell, y = oy + i * cell;
                        g.DrawLine(pen, x, oy, x, oy + s);
                        g.DrawLine(pen, ox, y, ox + s, y);
                    }
                }
                if (cell > 22)
                {
                    using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                    using (var br = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    using (var bk = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    {
                        for (int i = 0; i < n; i++)
                        {
                            float cx = ox + i * cell + cell / 2f;
                            if (cx > 16 && cx < Width - 16)
                            {
                                string t = i < 26 ? ((char)('A' + i)).ToString() : "A" + (char)('A' + (i - 26) % 26);
                                var sz = g.MeasureString(t, f);
                                g.FillRectangle(bk, cx - sz.Width / 2, 5, sz.Width, sz.Height);
                                g.DrawString(t, f, br, cx - sz.Width / 2, 5);
                            }
                            float cy = oy + i * cell + cell / 2f;
                            if (cy > 16 && cy < Height - 44)
                            {
                                string t = i.ToString();
                                var sz = g.MeasureString(t, f);
                                g.FillRectangle(bk, 6, cy - sz.Height / 2, sz.Width, sz.Height);
                                g.DrawString(t, f, br, 6, cy - sz.Height / 2);
                            }
                        }
                    }
                }
            }
            using (var pen = new Pen(Color.FromArgb(160, 255, 255, 255), 2f)) g.DrawRectangle(pen, ox, oy, s, s);

            // players
            using (var f = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            {
                foreach (var p in Players)
                {
                    if (!p.HasPos) continue;
                    var sp = ToScreen(p.X, p.Z);
                    if (sp.X < -20 || sp.Y < -20 || sp.X > Width + 20 || sp.Y > Height + 20) continue;
                    bool sel = p.Id == SelectedId;
                    float r = sel ? 7f : 5f;
                    if (sel)
                        using (var pen = new Pen(Color.Gold, 2f)) g.DrawEllipse(pen, sp.X - r - 3, sp.Y - r - 3, (r + 3) * 2, (r + 3) * 2);
                    using (var br = new SolidBrush(sel ? Color.Gold : Color.FromArgb(255, 70, 70)))
                        g.FillEllipse(br, sp.X - r, sp.Y - r, r * 2, r * 2);
                    using (var pen = new Pen(Color.White, 1.5f)) g.DrawEllipse(pen, sp.X - r, sp.Y - r, r * 2, r * 2);
                    if (ShowNames || sel || p == hover)
                    {
                        g.DrawString(p.Name, f, Brushes.Black, sp.X + r + 3, sp.Y - 8 + 1);
                        g.DrawString(p.Name, f, Brushes.White, sp.X + r + 2, sp.Y - 8);
                    }
                }
            }

            using (var f = new Font("Consolas", 9f))
            {
                string info = coordText + (coordText.Length > 0 ? "   " : "") + "zoom " + zoom.ToString("0.0") + "x   players on map: " + Players.Count(p => p.HasPos);
                var isz = g.MeasureString(info, f);
                using (var path = Gfx.Round(new RectangleF(10, Height - 38, isz.Width + 14, 26), 13))
                using (var br = new SolidBrush(Color.FromArgb(190, 13, 17, 23))) g.FillPath(br, path);
                g.DrawString(info, f, Brushes.WhiteSmoke, 17, Height - 34);
            }
            Gfx.MaskCorners(g, ClientRectangle, Theme.RCard, Gfx.BackOf(this), Theme.Border);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var before = ToWorld(e.Location);
            zoom = Math.Max(0.5f, Math.Min(25f, zoom * (float)Math.Pow(1.15, e.Delta / 120.0)));
            var after = ToScreen(before.X, before.Y);
            panX += e.X - after.X; panY += e.Y - after.Y;
            Invalidate();
            base.OnMouseWheel(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left) { dragging = true; lastMouse = downPt = e.Location; }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                panX += e.X - lastMouse.X; panY += e.Y - lastMouse.Y;
                lastMouse = e.Location; Invalidate();
            }
            var w = ToWorld(e.Location);
            bool inside = Math.Abs(w.X) <= WorldSize / 2 && Math.Abs(w.Y) <= WorldSize / 2;
            coordText = inside ? string.Format("x {0:0}  z {1:0}  [{2}]", w.X, w.Y, U.Grid(w.X, w.Y)) : "";
            var h = HitTest(e.Location);
            if (h != hover)
            {
                hover = h;
                tip.SetToolTip(this, h == null ? "" : string.Format("{0}\n{1}\nx {2:0}  y {3:0}  z {4:0}  [{5}]", h.Name, h.Id, h.X, h.Y, h.Z, h.GridRef));
            }
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                dragging = false;
                if (Math.Abs(e.X - downPt.X) < 4 && Math.Abs(e.Y - downPt.Y) < 4)
                {
                    var h = HitTest(e.Location);
                    if (h != null) { SelectedId = h.Id; if (PlayerClicked != null) PlayerClicked(h); Invalidate(); }
                }
            }
            base.OnMouseUp(e);
        }
    }

    // ------------------------------------------------------------------ small history graph
    class SparkPanel : Control
    {
        public List<int> Values = new List<int>();
        public int Max = 10;
        public SparkPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(BackColor);
            float top = 8, bottom = Height - 8, left = 40, right = Width - 14;
            int max = Math.Max(1, Max);
            using (var f = Theme.F(8f))
            using (var pen = new Pen(Color.FromArgb(60, Theme.Border)))
            {
                for (int i = 0; i <= 2; i++)
                {
                    float y = bottom - (bottom - top) * i / 2f;
                    g.DrawLine(pen, left, y, right, y);
                    TextRenderer.DrawText(g, (max * i / 2).ToString(), f, new Rectangle(0, (int)y - 8, 34, 16), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.NoPadding);
                }
            }
            if (Values.Count < 2) return;
            var pts = new List<PointF>();
            for (int i = 0; i < Values.Count; i++)
            {
                float x = left + (right - left) * i / (float)(Values.Count - 1);
                float y = bottom - (bottom - top) * Math.Min(1f, Values[i] / (float)max);
                pts.Add(new PointF(x, y));
            }
            var area = new List<PointF>(pts); area.Add(new PointF(right, bottom)); area.Add(new PointF(left, bottom));
            using (var br = new LinearGradientBrush(new RectangleF(0, top - 1, 10, bottom - top + 2), Color.FromArgb(80, Theme.Accent), Color.FromArgb(4, Theme.Accent), 90f))
                g.FillPolygon(br, area.ToArray());
            using (var pen = new Pen(Theme.Accent, 2.2f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts.ToArray());
            var last = pts[pts.Count - 1];
            using (var br = new SolidBrush(Color.FromArgb(60, Theme.Accent))) g.FillEllipse(br, last.X - 8, last.Y - 8, 16, 16);
            using (var br = new SolidBrush(Theme.Accent)) g.FillEllipse(br, last.X - 4, last.Y - 4, 8, 8);
        }
    }

    // ------------------------------------------------------------------ theme + rounded controls
    static class Gfx
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            rad = Math.Max(0.5f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2f));
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static GraphicsPath Round(Rectangle r, float rad) { return Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), rad); }

        // The solid colour behind a control (first ancestor with an opaque background).
        public static Color BackOf(Control c)
        {
            for (Control p = c.Parent; p != null; p = p.Parent)
                if (p.BackColor.A == 255) return p.BackColor;
            return Theme.Bg;
        }

        // Paints over the square corners of a control so it reads as a rounded tile (anti-aliased).
        public static void MaskCorners(Graphics g, Rectangle b, float rad, Color outside, Color border)
        {
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var round = Round(b, rad))
            using (var p = new GraphicsPath(FillMode.Alternate))
            {
                p.AddRectangle(new RectangleF(b.X - 2, b.Y - 2, b.Width + 4, b.Height + 4));
                p.AddPath(round, false);
                using (var br = new SolidBrush(outside)) g.FillPath(br, p);
                if (border.A > 0) using (var pen = new Pen(border)) g.DrawPath(pen, round);
            }
            g.SmoothingMode = old;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static bool? iconFont;
        public static bool HasIconFont
        {
            get
            {
                if (iconFont == null)
                {
                    iconFont = false;
                    try { foreach (var f in FontFamily.Families) if (f.Name == Theme.IconFontName) { iconFont = true; break; } } catch { }
                }
                return iconFont.Value;
            }
        }
        // Draws a Segoe MDL2 / Fluent icon glyph centered in a rectangle.
        public static void DrawIcon(Graphics g, string glyph, Rectangle r, Color c, float size)
        {
            if (string.IsNullOrEmpty(glyph) || !HasIconFont) return;
            using (var f = new Font(Theme.IconFontName, size, FontStyle.Regular, GraphicsUnit.Point))
                TextRenderer.DrawText(g, glyph, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    // Dark context-menu renderer with rounded hover highlight.
    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = true; }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics; var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using (var br = new SolidBrush(Theme.Surface2)) g.FillRectangle(br, 0, 0, e.Item.Width, e.Item.Height);
            if (e.Item.Selected && e.Item.Enabled)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Gfx.Round(r, 6)) using (var br = new SolidBrush(Theme.Hover)) g.FillPath(br, path);
            }
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Surface2; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.Hover; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface2; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface2; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface2; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
    }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(11, 14, 19);
        public static readonly Color Surface = Color.FromArgb(20, 25, 32);
        public static readonly Color Surface2 = Color.FromArgb(31, 38, 48);
        public static readonly Color Input = Color.FromArgb(13, 17, 23);
        public static readonly Color Border = Color.FromArgb(44, 52, 64);
        public static readonly Color Hover = Color.FromArgb(40, 49, 61);
        public static readonly Color Text = Color.FromArgb(232, 238, 244);
        public static readonly Color Muted = Color.FromArgb(138, 151, 167);
        public static readonly Color Faint = Color.FromArgb(84, 95, 110);
        public static readonly Color Accent = Color.FromArgb(47, 191, 143);
        public static readonly Color AccentHover = Color.FromArgb(78, 212, 166);
        public static readonly Color AccentDown = Color.FromArgb(36, 160, 120);
        public static readonly Color AccentInk = Color.FromArgb(6, 24, 18);
        public static readonly Color AccentSoft = Color.FromArgb(24, 58, 50);
        public static readonly Color Danger = Color.FromArgb(229, 83, 75);
        public static readonly Color DangerText = Color.FromArgb(255, 123, 114);
        public static readonly Color DangerSoft = Color.FromArgb(52, 28, 32);
        public static readonly Color Warn = Color.FromArgb(227, 179, 65);
        public static readonly Color Ok = Color.FromArgb(63, 185, 80);
        public static readonly Color Info = Color.FromArgb(88, 166, 255);
        public static readonly Color RowAlt = Color.FromArgb(24, 30, 38);
        public static readonly Color Select = Color.FromArgb(27, 74, 61);

        public const float RCard = 12f, RCtl = 8f;
        public const string IconFontName = "Segoe MDL2 Assets";

        public static Font F(float size, FontStyle st) { return new Font("Segoe UI", size, st); }
        public static Font F(float size) { return new Font("Segoe UI", size, FontStyle.Regular); }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        static bool nativeOk = Environment.OSVersion.Platform == PlatformID.Win32NT;

        static int Ref(Color c) { return c.R | (c.G << 8) | (c.B << 16); }
        static void Dwm(IntPtr h, int attr, int v) { try { DwmSetWindowAttribute(h, attr, ref v, 4); } catch { nativeOk = false; } }

        // Dark title bar, plus on Windows 11: rounded window corners and a caption/border that match the app.
        public static void DarkTitle(Form f)
        {
            if (!nativeOk) return;
            try { int v = 1; if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4); }
            catch { nativeOk = false; return; }
            Dwm(f.Handle, 33, 2);              // DWMWA_WINDOW_CORNER_PREFERENCE = round
            Dwm(f.Handle, 35, Ref(Surface));   // caption colour
            Dwm(f.Handle, 34, Ref(Border));    // border colour
            Dwm(f.Handle, 36, Ref(Text));      // caption text colour
        }
        public static void RoundPopup(IntPtr h) { if (nativeOk) Dwm(h, 33, 3); }   // small rounded corners for menus

        static void NativeTheme(Control c, string name)
        {
            if (!nativeOk) return;
            EventHandler h = delegate { try { SetWindowTheme(c.Handle, name, null); } catch { nativeOk = false; } };
            if (c.IsHandleCreated) h(null, null);
            c.HandleCreated += h;
        }
        public static void DoubleBuffer(Control c)
        {
            try { typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, true, null); } catch { }
        }

        static readonly string[] DangerButtons = { "Kick", "Ban", "Quit now", "Unban selected", "Delete", "Unload selected", "Remove role", "Restart", "Disconnect", "Reload ALL", "Revoke", "Clear image" };
        static readonly string[] PrimaryButtons = { "Connect", "Install", "Fetch map from RustMaps", "Apply && save", "Send to all", "Save profile", "OK", "Save", "Yes", "Send as server", "Grant" };

        public static void Apply(Control root)
        {
            Style(root);
            var kids = new Control[root.Controls.Count];
            root.Controls.CopyTo(kids, 0);
            foreach (Control k in kids) Apply(k);
        }

        static void Style(Control c)
        {
            Form f = c as Form;
            if (f != null) { f.BackColor = Bg; f.ForeColor = Text; return; }
            if (c is Page || c is Card || c is NavTabs || c is NavBar || c is MapControl || c is SparkPanel || c is RoundHost || c is RoundFlow) return;

            Button b = c as Button;
            if (b != null)
            {
                string kind = Array.IndexOf(DangerButtons, b.Text) >= 0 ? "danger" : Array.IndexOf(PrimaryButtons, b.Text) >= 0 ? "primary" : "normal";
                StyleButton(b, kind); return;
            }
            RichTextBox rt = c as RichTextBox;
            if (rt != null) { rt.BackColor = Input; rt.ForeColor = Text; rt.BorderStyle = BorderStyle.None; DoubleBuffer(rt); NativeTheme(rt, "DarkMode_Explorer"); Wrap(rt); return; }
            TextBox tb = c as TextBox;
            if (tb != null) { tb.BackColor = Input; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.None; if (tb.Multiline) NativeTheme(tb, "DarkMode_Explorer"); Wrap(tb); return; }
            ComboBox cb = c as ComboBox;
            if (cb != null) { cb.BackColor = Input; cb.ForeColor = Text; ComboSkin.Attach(cb); NativeTheme(cb, "DarkMode_CFD"); return; }
            NumericUpDown nu = c as NumericUpDown;
            if (nu != null) { nu.BackColor = Input; nu.ForeColor = Text; nu.BorderStyle = BorderStyle.None; NativeTheme(nu, "DarkMode_Explorer"); Wrap(nu); return; }
            CheckBox ck = c as CheckBox;
            if (ck != null) { ToggleSkin.Attach(ck); return; }
            RadioButton rb = c as RadioButton;
            if (rb != null) { RadioSkin.Attach(rb); return; }
            Label l = c as Label;
            if (l != null)
            {
                int a = l.ForeColor.ToArgb();
                if (a == Color.DimGray.ToArgb() || a == Color.Gray.ToArgb()) l.ForeColor = Muted;
                else if (a == Color.Firebrick.ToArgb()) l.ForeColor = DangerText;
                else if (a == SystemColors.ControlText.ToArgb() || a == Color.Black.ToArgb()) l.ForeColor = Text;
            }
        }

        // Moves an input into a rounded frame that takes its place in the layout.
        public static RoundHost Wrap(Control c)
        {
            Control parent = c.Parent;
            if (parent == null || parent is RoundHost) return null;
            int idx = parent.Controls.GetChildIndex(c);
            var cell = parent is TableLayoutPanel ? ((TableLayoutPanel)parent).GetPositionFromControl(c) : new TableLayoutPanelCellPosition(-1, -1);
            DockStyle dock = c.Dock; AnchorStyles anchor = c.Anchor; Padding margin = c.Margin; Point loc = c.Location; Size size = c.Size;
            parent.SuspendLayout();
            var host = new RoundHost(c);
            bool single = host.Single;
            host.Dock = dock; host.Anchor = anchor; host.Margin = margin; host.Location = loc;
            host.Size = (dock == DockStyle.Fill || !single) ? size : new Size(size.Width, 32);
            parent.Controls.Add(host);
            parent.Controls.SetChildIndex(host, idx);
            var tlp = parent as TableLayoutPanel;
            if (tlp != null) tlp.SetCellPosition(host, cell);
            parent.ResumeLayout(true);
            return host;
        }

        public static void StyleButton(Button b, string kind)
        {
            if (b.MinimumSize.Height == 0) b.MinimumSize = new Size(b.MinimumSize.Width, 32);
            ButtonSkin.Attach(b, kind);
        }

        public static void StyleMenu(ContextMenuStrip m)
        {
            m.Renderer = new DarkRenderer();
            m.BackColor = Surface2; m.ForeColor = Text; m.ShowImageMargin = false; m.Padding = new Padding(2, 4, 2, 4);
            foreach (ToolStripItem i in m.Items) { i.ForeColor = Text; i.Padding = new Padding(6, 4, 6, 4); }
            m.HandleCreated += delegate { RoundPopup(m.Handle); };
        }

        // Rounded "pill" rows for owner-drawn lists.
        static void PaintRow(Graphics g, Rectangle cell, Rectangle row, Color bg, Color surface)
        {
            using (var br = new SolidBrush(surface)) g.FillRectangle(br, cell);
            if (bg.ToArgb() == surface.ToArgb()) return;
            var clip = g.Clip; g.SetClip(cell);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Gfx.Round(row, 7)) using (var br = new SolidBrush(bg)) g.FillPath(br, path);
            g.Clip = clip;
        }

        public static void StyleListBox(ListBox lb)
        {
            lb.BorderStyle = BorderStyle.None; lb.BackColor = Surface; lb.ForeColor = Text;
            lb.DrawMode = DrawMode.OwnerDrawFixed; lb.ItemHeight = 32;
            int hot = -1;
            lb.MouseMove += delegate (object s, MouseEventArgs e) { int h = lb.IndexFromPoint(e.Location); if (h != hot) { hot = h; lb.Invalidate(); } };
            lb.MouseLeave += delegate { hot = -1; lb.Invalidate(); };
            lb.DrawItem += delegate (object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) { using (var br = new SolidBrush(Surface)) e.Graphics.FillRectangle(br, e.Bounds); return; }
                bool sel = (e.State & DrawItemState.Selected) != 0;
                Color bg = sel ? Select : (e.Index == hot ? Hover : Surface);
                var row = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 4, e.Bounds.Height - 4);
                PaintRow(e.Graphics, e.Bounds, row, bg, Surface);
                TextRenderer.DrawText(e.Graphics, lb.GetItemText(lb.Items[e.Index]), lb.Font,
                    new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height), Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            DoubleBuffer(lb); NativeTheme(lb, "DarkMode_Explorer");
        }

        static void FitLast(ListView lv)
        {
            int n = lv.Columns.Count; if (n == 0) return;
            int sum = 0; for (int i = 0; i < n - 1; i++) sum += lv.Columns[i].Width;
            int w = lv.ClientSize.Width - sum - 20;
            if (w > 80 && lv.Columns[n - 1].Width != w) lv.Columns[n - 1].Width = w;
        }

        public static void StyleList(ListView lv)
        {
            lv.OwnerDraw = true; lv.GridLines = false; lv.BorderStyle = BorderStyle.None; lv.BackColor = Surface; lv.ForeColor = Text;
            lv.FullRowSelect = true; lv.HideSelection = false;
            lv.SmallImageList = new ImageList { ImageSize = new Size(1, 34) };
            DoubleBuffer(lv); NativeTheme(lv, "DarkMode_Explorer");
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            ListViewItem hot = null;
            lv.MouseMove += delegate (object s, MouseEventArgs e)
            {
                var h = lv.GetItemAt(e.X, e.Y);
                if (h == hot) return;
                if (hot != null && hot.ListView == lv) lv.Invalidate(hot.Bounds);
                hot = h;
                if (hot != null) lv.Invalidate(hot.Bounds);
            };
            lv.MouseLeave += delegate { if (hot != null && hot.ListView == lv) lv.Invalidate(hot.Bounds); hot = null; };
            lv.DrawColumnHeader += delegate (object s, DrawListViewColumnHeaderEventArgs e)
            {
                using (var br = new SolidBrush(Surface)) e.Graphics.FillRectangle(br, e.Bounds);
                using (var pen = new Pen(Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                using (var f = F(8f, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), f,
                        new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height), Muted, flags);
            };
            lv.DrawItem += delegate (object s, DrawListViewItemEventArgs e) { e.DrawDefault = false; };
            lv.DrawSubItem += delegate (object s, DrawListViewSubItemEventArgs e)
            {
                Rectangle cell = e.Bounds;
                if (e.ColumnIndex == 0) cell = new Rectangle(cell.X, cell.Y, lv.Columns[0].Width, cell.Height);
                Rectangle ib = e.Item.Bounds;
                var row = new Rectangle(ib.X + 2, ib.Y + 2, Math.Min(ib.Width, lv.ClientSize.Width - ib.X) - 4, ib.Height - 4);
                Color bg = e.Item.Selected ? Select : (e.Item == hot ? Hover : (e.ItemIndex % 2 == 1 ? RowAlt : Surface));
                PaintRow(e.Graphics, cell, row, bg, Surface);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font, new Rectangle(cell.X + 12, cell.Y, cell.Width - 14, cell.Height), Text, flags);
            };
            lv.Resize += delegate { FitLast(lv); };
            lv.ColumnWidthChanged += delegate { FitLast(lv); };
            lv.HandleCreated += delegate { FitLast(lv); };
        }

        // Puts a control inside a rounded card that takes over its docking.
        public static Card Framed(Control c, string title)
        {
            var card = new Card { Text = title ?? "", Dock = c.Dock, Padding = new Padding(6, string.IsNullOrEmpty(title) ? 6 : 0, 6, 6) };
            c.Dock = DockStyle.Fill;
            card.Controls.Add(c);
            return card;
        }
    }

    // ------------------------------------------------------------------ rounded control skins
    // Fully owner-paints a standard Button as a rounded pill (keeps all normal Button behaviour).
    class ButtonSkin
    {
        static readonly Dictionary<Button, ButtonSkin> all = new Dictionary<Button, ButtonSkin>();
        readonly Button b;
        public string Kind;
        bool hover, down;

        public static void Attach(Button b, string kind)
        {
            ButtonSkin s;
            if (all.TryGetValue(b, out s)) { s.Kind = kind; b.Invalidate(); return; }
            s = new ButtonSkin(b, kind); all[b] = s;
            b.Disposed += delegate { all.Remove(b); };
        }

        ButtonSkin(Button btn, string kind)
        {
            b = btn; Kind = kind;
            b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.Empty; b.FlatAppearance.MouseDownBackColor = Color.Empty;
            b.BackColor = Theme.Surface2; b.ForeColor = Theme.Text;
            b.MouseEnter += delegate { hover = true; b.Invalidate(); };
            b.MouseLeave += delegate { hover = false; down = false; b.Invalidate(); };
            b.MouseDown += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; b.Invalidate(); } };
            b.MouseUp += delegate { down = false; b.Invalidate(); };
            b.EnabledChanged += delegate { b.Invalidate(); };
            b.TextChanged += delegate { b.Invalidate(); };
            b.Paint += Paint;
        }

        void Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Gfx.BackOf(b));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, border, text;
            bool en = b.Enabled;
            if (Kind == "primary")
            {
                fill = !en ? Theme.AccentSoft : down ? Theme.AccentDown : hover ? Theme.AccentHover : Theme.Accent;
                border = fill; text = en ? Theme.AccentInk : Theme.Muted;
            }
            else if (Kind == "danger")
            {
                fill = !en ? Theme.Surface2 : down ? Color.FromArgb(92, 40, 44) : hover ? Color.FromArgb(70, 34, 38) : Theme.DangerSoft;
                border = en ? Color.FromArgb(96, 50, 52) : Theme.Border; text = en ? Theme.DangerText : Theme.Faint;
            }
            else
            {
                fill = !en ? Theme.Surface2 : down ? Theme.Border : hover ? Theme.Hover : Theme.Surface2;
                border = hover && en ? Color.FromArgb(62, 72, 86) : Theme.Border; text = en ? Theme.Text : Theme.Faint;
            }
            var r = new RectangleF(0.5f, 0.5f, b.Width - 1.5f, b.Height - 1.5f);
            using (var path = Gfx.Round(r, Theme.RCtl))
            {
                using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                using (var pen = new Pen(border)) g.DrawPath(pen, path);
            }
            if (Kind == "primary" && en && !down)   // soft top highlight
                using (var path = Gfx.Round(new RectangleF(1.5f, 1.5f, b.Width - 3.5f, b.Height / 2f), Theme.RCtl - 1))
                using (var br = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillPath(br, path);
            var tr = new Rectangle(4, down ? 1 : 0, b.Width - 8, b.Height);
            TextRenderer.DrawText(g, b.Text, b.Font, tr, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.HidePrefix);
        }
    }

    // Paints a CheckBox as a rounded toggle switch.
    static class ToggleSkin
    {
        static readonly HashSet<CheckBox> done = new HashSet<CheckBox>();
        public static void Attach(CheckBox ck)
        {
            if (!done.Add(ck)) return;
            ck.FlatStyle = FlatStyle.Flat; ck.ForeColor = Theme.Text; ck.Cursor = Cursors.Hand;
            ck.Padding = new Padding(24, 0, 0, 0); ck.MinimumSize = new Size(0, 24);
            bool hover = false;
            ck.MouseEnter += delegate { hover = true; ck.Invalidate(); };
            ck.MouseLeave += delegate { hover = false; ck.Invalidate(); };
            ck.CheckedChanged += delegate { ck.Invalidate(); };
            ck.Disposed += delegate { done.Remove(ck); };
            ck.Paint += delegate (object s, PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Gfx.BackOf(ck)); g.SmoothingMode = SmoothingMode.AntiAlias;
                bool on = ck.Checked, en = ck.Enabled;
                var sw = new RectangleF(1, (ck.Height - 20) / 2f, 36, 20);
                Color track = on ? (en ? (hover ? Theme.AccentHover : Theme.Accent) : Theme.AccentSoft) : (hover && en ? Theme.Border : Theme.Surface2);
                using (var path = Gfx.Round(sw, 10))
                {
                    using (var br = new SolidBrush(track)) g.FillPath(br, path);
                    using (var pen = new Pen(on ? track : Theme.Border)) g.DrawPath(pen, path);
                }
                float kx = on ? sw.Right - 17 : sw.X + 3;
                using (var br = new SolidBrush(on ? Color.White : (en ? Theme.Muted : Theme.Faint))) g.FillEllipse(br, kx, sw.Y + 3, 14, 14);
                TextRenderer.DrawText(g, ck.Text, ck.Font, new Rectangle(46, 0, ck.Width - 46, ck.Height), en ? Theme.Text : Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            };
        }
    }

    // Paints a RadioButton with a rounded accent ring.
    static class RadioSkin
    {
        static readonly HashSet<RadioButton> done = new HashSet<RadioButton>();
        public static void Attach(RadioButton rb)
        {
            if (!done.Add(rb)) return;
            rb.FlatStyle = FlatStyle.Flat; rb.ForeColor = Theme.Text; rb.Cursor = Cursors.Hand;
            rb.Padding = new Padding(8, 0, 0, 0); rb.MinimumSize = new Size(0, 24);
            rb.CheckedChanged += delegate { rb.Invalidate(); };
            rb.Disposed += delegate { done.Remove(rb); };
            rb.Paint += delegate (object s, PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Gfx.BackOf(rb)); g.SmoothingMode = SmoothingMode.AntiAlias;
                float y = (rb.Height - 18) / 2f;
                using (var br = new SolidBrush(Theme.Input)) g.FillEllipse(br, 1, y, 18, 18);
                using (var pen = new Pen(rb.Checked ? Theme.Accent : Theme.Border, 1.5f)) g.DrawEllipse(pen, 1, y, 18, 18);
                if (rb.Checked) using (var br = new SolidBrush(Theme.Accent)) g.FillEllipse(br, 5.5f, y + 4.5f, 9, 9);
                TextRenderer.DrawText(g, rb.Text, rb.Font, new Rectangle(26, 0, rb.Width - 26, rb.Height), rb.Enabled ? Theme.Text : Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            };
        }
    }

    // Rounded frame around a borderless TextBox / RichTextBox / NumericUpDown, with a focus glow.
    class RoundHost : Panel
    {
        public readonly Control Inner;
        public readonly bool Single;
        bool focused, hover;

        public RoundHost(Control inner)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Inner = inner; BackColor = Theme.Input; ForeColor = Theme.Text; Cursor = Cursors.IBeam;
            var tb = inner as TextBox;
            Single = !(inner is RichTextBox) && !(tb != null && tb.Multiline);
            Padding = Single ? new Padding(0) : new Padding(12, 9, 6, 9);
            inner.Margin = new Padding(0);
            inner.Dock = Single ? DockStyle.None : DockStyle.Fill;
            if (Single) inner.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(inner);
            inner.Enter += delegate { focused = true; Invalidate(); };
            inner.Leave += delegate { focused = false; Invalidate(); };
            inner.EnabledChanged += delegate { Invalidate(); };
            inner.MouseEnter += delegate { hover = true; Invalidate(); };
            inner.MouseLeave += delegate { hover = false; Invalidate(); };
            inner.SizeChanged += delegate { if (Single) PlaceInner(); };
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
            MouseDown += delegate { if (Inner.CanFocus) Inner.Focus(); };
            PlaceInner();
        }

        bool placing;
        void PlaceInner()
        {
            if (!Single || placing) return;
            placing = true;
            int right = Inner is NumericUpDown ? 4 : 10;
            Inner.Left = 11;
            Inner.Width = Math.Max(10, Width - 11 - right);
            Inner.Top = Math.Max(0, (Height - Inner.Height) / 2);
            placing = false;
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); PlaceInner(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Gfx.BackOf(this)); g.SmoothingMode = SmoothingMode.AntiAlias;
            bool en = Inner.Enabled;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (focused)   // outer glow ring
                using (var path = Gfx.Round(r, Theme.RCtl)) using (var pen = new Pen(Color.FromArgb(70, Theme.Accent), 3f)) g.DrawPath(pen, path);
            using (var path = Gfx.Round(new RectangleF(r.X + (focused ? 1 : 0), r.Y + (focused ? 1 : 0), r.Width - (focused ? 2 : 0), r.Height - (focused ? 2 : 0)), Theme.RCtl))
            {
                using (var br = new SolidBrush(en ? Theme.Input : Theme.Surface)) g.FillPath(br, path);
                Color bc = focused ? Theme.Accent : (hover && en ? Color.FromArgb(66, 76, 90) : Theme.Border);
                using (var pen = new Pen(bc)) g.DrawPath(pen, path);
            }
        }
    }

    // Rounded panel used for the connection bar and other toolbars.
    class RoundFlow : FlowLayoutPanel
    {
        public RoundFlow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface; ForeColor = Theme.Text; Padding = new Padding(14, 10, 14, 10);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Gfx.BackOf(this)); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Gfx.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.RCard))
            {
                using (var br = new SolidBrush(BackColor)) g.FillPath(br, path);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }
        }
    }

    // Draws the closed state of a ComboBox as a rounded field with a chevron, and dark dropdown rows.
    class ComboSkin : NativeWindow
    {
        static readonly List<ComboSkin> keep = new List<ComboSkin>();
        readonly ComboBox cb;
        bool hover;

        public static void Attach(ComboBox cb)
        {
            foreach (var k in keep) if (k.cb == cb) return;
            keep.Add(new ComboSkin(cb));
        }
        ComboSkin(ComboBox c)
        {
            cb = c;
            cb.FlatStyle = FlatStyle.Flat; cb.DrawMode = DrawMode.OwnerDrawFixed; cb.ItemHeight = 24; cb.Cursor = Cursors.Hand;
            cb.DrawItem += DrawItem;
            if (cb.IsHandleCreated) AssignHandle(cb.Handle);
            cb.HandleCreated += delegate { AssignHandle(cb.Handle); };
            cb.HandleDestroyed += delegate { ReleaseHandle(); };
            cb.MouseEnter += delegate { hover = true; cb.Invalidate(); };
            cb.MouseLeave += delegate { hover = false; cb.Invalidate(); };
            cb.SelectedIndexChanged += delegate { cb.Invalidate(); };
            cb.DropDownClosed += delegate { cb.Invalidate(); };
            cb.GotFocus += delegate { cb.Invalidate(); };
            cb.LostFocus += delegate { cb.Invalidate(); };
            cb.EnabledChanged += delegate { cb.Invalidate(); };
            cb.Disposed += delegate { keep.Remove(this); };
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F || m.Msg == 0x0085)   // WM_PAINT, WM_NCPAINT
                try { using (var g = Graphics.FromHwnd(Handle)) Draw(g); } catch { }
        }

        void Draw(Graphics g)
        {
            var b = new Rectangle(0, 0, cb.Width, cb.Height);
            using (var br = new SolidBrush(Gfx.BackOf(cb))) g.FillRectangle(br, b);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool en = cb.Enabled, foc = cb.Focused || cb.DroppedDown;
            using (var path = Gfx.Round(new RectangleF(0.5f, 0.5f, b.Width - 1.5f, b.Height - 1.5f), Theme.RCtl))
            {
                using (var br = new SolidBrush(en ? (hover ? Theme.Hover : Theme.Surface2) : Theme.Surface)) g.FillPath(br, path);
                using (var pen = new Pen(foc ? Theme.Accent : Theme.Border)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, cb.Text, cb.Font, new Rectangle(11, 0, b.Width - 36, b.Height), en ? Theme.Text : Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            // chevron
            float cx = b.Width - 17, cy = b.Height / 2f;
            using (var pen = new Pen(Theme.Muted, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(pen, new PointF[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
        }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            var g = e.Graphics;
            using (var br = new SolidBrush(Theme.Surface2)) g.FillRectangle(br, e.Bounds);
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            if (sel)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Gfx.Round(new Rectangle(e.Bounds.X + 3, e.Bounds.Y + 1, e.Bounds.Width - 6, e.Bounds.Height - 2), 6))
                using (var br = new SolidBrush(Theme.Select)) g.FillPath(br, path);
            }
            TextRenderer.DrawText(g, cb.GetItemText(cb.Items[e.Index]), cb.Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // A rounded, bordered panel with an optional small-caps title and icon.
    class Card : Panel
    {
        public string Glyph = "";
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface; ForeColor = Theme.Text; Padding = new Padding(12);
        }
        int TitleH { get { return string.IsNullOrEmpty(Text) ? 0 : 36; } }
        public override Rectangle DisplayRectangle
        {
            get { Rectangle r = base.DisplayRectangle; return new Rectangle(r.X, r.Y + TitleH, r.Width, Math.Max(0, r.Height - TitleH)); }
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); PerformLayout(); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Gfx.BackOf(this));
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Gfx.Round(r, Theme.RCard))
            {
                using (var br = new SolidBrush(BackColor)) g.FillPath(br, path);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }
            int tx = 16;
            if (TitleH > 0 && Glyph.Length > 0 && Gfx.HasIconFont)
            {
                Gfx.DrawIcon(g, Glyph, new Rectangle(14, 10, 18, 18), Theme.Accent, 10f);
                tx = 38;
            }
            if (TitleH > 0)
                using (var f = Theme.F(8.25f, FontStyle.Bold))
                    TextRenderer.DrawText(g, Text.Replace("&&", "&").ToUpperInvariant(), f, new Point(tx, 13), Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    // ------------------------------------------------------------------ sidebar navigation
    class Page : Panel
    {
        public string Glyph, Sub;
        public Page(string text, string glyph, string sub)
        {
            Text = text; Glyph = glyph; Sub = sub;
            Dock = DockStyle.Fill; Padding = new Padding(24, 66, 24, 18);
            BackColor = Theme.Bg; ForeColor = Theme.Text; Visible = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            using (var f = Theme.F(16f, FontStyle.Bold))
                TextRenderer.DrawText(g, Text.Replace("&&", "&"), f, new Point(22, 12), Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (!string.IsNullOrEmpty(Sub))
                using (var f = Theme.F(9f))
                    TextRenderer.DrawText(g, Sub, f, new Point(24, 42), Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    class PageList : System.Collections.Generic.IEnumerable<Page>
    {
        readonly NavTabs owner;
        public PageList(NavTabs o) { owner = o; }
        public void AddRange(Page[] pages) { foreach (var p in pages) owner.AddPage(p); }
        public void Add(Page p) { owner.AddPage(p); }
        public System.Collections.Generic.IEnumerator<Page> GetEnumerator() { return owner.Pages.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return owner.Pages.GetEnumerator(); }
    }

    // Sidebar on the left; on the right a header (connection card), the page area and a footer.
    class NavTabs : Panel
    {
        public readonly List<Page> Pages = new List<Page>();
        public readonly PageList TabPages;
        public readonly Panel Header, Footer;
        readonly NavBar bar;
        readonly Panel host;
        int sel = -1;
        public event EventHandler SelectedIndexChanged;

        public NavTabs()
        {
            TabPages = new PageList(this);
            BackColor = Theme.Bg;
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            Header = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Theme.Bg, Padding = new Padding(22, 16, 22, 0) };
            Footer = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Theme.Bg };
            right.Controls.Add(host); right.Controls.Add(Header); right.Controls.Add(Footer);
            bar = new NavBar(this) { Dock = DockStyle.Left, Width = 228 };
            Controls.Add(right); Controls.Add(bar);
        }
        public int Selected { get { return sel; } }
        public void SetStatus(bool ok, string text) { bar.Online = ok; bar.StatusText = text; bar.Invalidate(); }
        public void AddPage(Page p)
        {
            p.Dock = DockStyle.Fill; host.Controls.Add(p); Pages.Add(p);
            p.EnabledChanged += delegate { bar.Invalidate(); };
            if (sel < 0) Select(0);
        }
        public void Select(int i)
        {
            if (i < 0 || i >= Pages.Count) return;
            for (int k = 0; k < Pages.Count; k++) Pages[k].Visible = (k == i);
            sel = i; bar.Invalidate();
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }
        public Page SelectedTab { get { return sel >= 0 ? Pages[sel] : null; } set { Select(Pages.IndexOf(value)); } }
    }

    class NavBar : Control
    {
        readonly NavTabs owner;
        int hover = -1;
        public bool Online;
        public string StatusText = "Offline";
        Image logo;
        const int ItemH = 42, Gap = 4, Top0 = 92;

        public NavBar(NavTabs o)
        {
            owner = o;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            try { logo = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }
        }
        Rectangle ItemRect(int i) { return new Rectangle(12, Top0 + i * (ItemH + Gap), Width - 25, ItemH); }
        int HitTest(Point p) { for (int i = 0; i < owner.Pages.Count; i++) if (ItemRect(i).Contains(p)) return i; return -1; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            const TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

            // brand
            var lr = new Rectangle(18, 22, 40, 40);
            using (var path = Gfx.Round(lr, 11))
            {
                using (var br = new LinearGradientBrush(lr, Color.FromArgb(52, 206, 156), Color.FromArgb(28, 128, 102), 45f)) g.FillPath(br, path);
                if (logo != null) { var clip = g.Clip; g.SetClip(path); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(logo, new Rectangle(lr.X + 4, lr.Y + 4, 32, 32)); g.Clip = clip; }
            }
            using (var f = Theme.F(11.5f, FontStyle.Bold)) TextRenderer.DrawText(g, "Jets Rust Admin", f, new Rectangle(68, 22, Width - 76, 22), Theme.Text, tf);
            using (var f = Theme.F(8.25f)) TextRenderer.DrawText(g, "Server control  ·  v" + Updater.Current, f, new Rectangle(69, 44, Width - 76, 18), Theme.Muted, tf);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 18, 78, Width - 19, 78);

            for (int i = 0; i < owner.Pages.Count; i++)
            {
                var pg = owner.Pages[i]; var r = ItemRect(i);
                bool selected = i == owner.Selected, enabled = pg.Enabled, hot = hover == i && enabled;
                if (selected || hot)
                    using (var path = Gfx.Round(r, 10))
                    using (var br = new SolidBrush(selected ? Theme.AccentSoft : Theme.Hover)) g.FillPath(br, path);
                if (selected)
                    using (var path = Gfx.Round(new RectangleF(r.X + 1, r.Y + 11, 4, r.Height - 22), 2))
                    using (var br = new SolidBrush(Theme.Accent)) g.FillPath(br, path);
                Color c = !enabled ? Theme.Faint : selected ? Theme.Text : (hot ? Theme.Text : Theme.Muted);
                int tx = 16;
                if (Gfx.HasIconFont)
                {
                    Gfx.DrawIcon(g, pg.Glyph, new Rectangle(r.X + 12, r.Y, 22, r.Height), selected ? Theme.Accent : c, 11.5f);
                    tx = 44;
                }
                using (var f = Theme.F(10f, selected ? FontStyle.Bold : FontStyle.Regular))
                    TextRenderer.DrawText(g, pg.Text.Replace("&&", "&"), f, new Rectangle(r.X + tx, r.Y, r.Width - tx - 6, r.Height), c, tf);
            }

            // connection pill at the bottom
            var pr = new Rectangle(12, Height - 58, Width - 25, 42);
            using (var path = Gfx.Round(pr, 21))
            {
                using (var br = new SolidBrush(Theme.Surface2)) g.FillPath(br, path);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }
            Color dot = Online ? Theme.Ok : Theme.Danger;
            using (var br = new SolidBrush(Color.FromArgb(60, dot))) g.FillEllipse(br, pr.X + 13, pr.Y + 14, 14, 14);
            using (var br = new SolidBrush(dot)) g.FillEllipse(br, pr.X + 16, pr.Y + 17, 8, 8);
            using (var f = Theme.F(9f)) TextRenderer.DrawText(g, StatusText, f, new Rectangle(pr.X + 36, pr.Y, pr.Width - 44, pr.Height), Theme.Text, tf);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Cursor = (h >= 0 && owner.Pages[h].Enabled) ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h >= 0 && e.Button == MouseButtons.Left && owner.Pages[h].Enabled) owner.Select(h);
            base.OnMouseDown(e);
        }
    }

    // Dark replacement for MessageBox.
    static class Dlg
    {
        public static DialogResult Show(string text) { return Show(text, "Jets Rust Admin", false); }
        public static DialogResult Show(string text, string title, bool yesNo)
        {
            using (var f = new Form())
            {
                f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false; f.Font = Theme.F(9.5f);
                Size sz = TextRenderer.MeasureText(text, f.Font, new Size(420, 0), TextFormatFlags.WordBreak);
                var l = new Label { Text = text, Left = 24, Top = 24, Width = 420, Height = Math.Max(22, sz.Height + 4), AutoSize = false };
                int by = 24 + l.Height + 24;
                f.ClientSize = new Size(468, by + 32 + 20);
                if (yesNo)
                {
                    var yes = new Button { Text = "Yes", Left = 264, Top = by, Width = 88, Height = 32, DialogResult = DialogResult.Yes };
                    var no = new Button { Text = "No", Left = 358, Top = by, Width = 88, Height = 32, DialogResult = DialogResult.No };
                    f.Controls.AddRange(new Control[] { l, yes, no }); f.AcceptButton = yes; f.CancelButton = no;
                }
                else
                {
                    var ok = new Button { Text = "OK", Left = 358, Top = by, Width = 88, Height = 32, DialogResult = DialogResult.OK };
                    f.Controls.AddRange(new Control[] { l, ok }); f.AcceptButton = ok; f.CancelButton = ok;
                }
                Theme.Apply(f);
                f.HandleCreated += delegate { Theme.DarkTitle(f); };
                return f.ShowDialog(Form.ActiveForm);
            }
        }
    }

    // ------------------------------------------------------------------ main window
    class MainForm : Form
    {
        Cfg cfg = Cfg.Load();
        RconClient rc;
        bool userDisconnect, polling;
        readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer reconnectTimer = new System.Windows.Forms.Timer();
        readonly Dictionary<string, PlayerInfo> players = new Dictionary<string, PlayerInfo>();

        // connection bar
        TextBox txtHost, txtPort, txtPw;
        CheckBox chkRemember, chkAuto;
        Button btnConnect, btnUpdate;
        Label lblStatus;
        NavTabs tabs;
        Page tabDash, tabPlayers, tabMap, tabConsole, tabChat, tabBans, tabServer, tabPlugins;

        // profiles / templates / scheduler
        ComboBox cmbProfile; FlowLayoutPanel tplBar;
        Page tabSched; CheckBox chkSchedOn; TextBox txtSchedTimes, txtSchedWarn, txtSchedMsg, txtSchedReason; Label lblNext, lblUpcoming;
        readonly System.Windows.Forms.Timer schedTimer = new System.Windows.Forms.Timer();
        readonly HashSet<string> fired = new HashSet<string>();
        // dashboard
        readonly Dictionary<string, Label> dash = new Dictionary<string, Label>();
        SparkPanel spark;
        NumericUpDown numRestart;
        TextBox txtRestartReason, txtBroadcast;

        // players
        ListView lvPlayers; TextBox txtFilter; LvSorter sorter = new LvSorter();
        // map
        MapControl map; ListBox lbMapPlayers; NumericUpDown numWorld, numMargin; string mapSource = ""; CheckBox chkNames, chkTrack; Label lblMapNote;
        // console / chat
        RichTextBox rtbConsole, rtbChat; TextBox txtCmd, txtChatSend; CheckBox chkScroll;
        readonly List<string> history = new List<string>(); int histIdx;
        // bans
        ListView lvBans;
        // server
        ListView lvVars;
        // plugins
        ListView lvPlugins; TextBox txtPermTarget, txtPermName, txtPermOut; RadioButton rbUser, rbGroup;

        static readonly string[] ServerVars = new string[] {
            "server.hostname", "server.description", "server.url", "server.headerimage", "server.maxplayers",
            "server.pve", "server.saveinterval", "server.tickrate", "server.worldsize", "server.seed", "server.level",
            "server.identity", "server.secure", "server.radiation", "server.respawnresetrange",
            "decay.scale", "env.progresstime", "env.time", "fps.limit", "chat.enabled", "antihack.enabled"
        };

        public MainForm()
        {
            Text = "Jets Rust Admin";
            Font = Theme.F(9.25f);
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Max(1000, Math.Min(1440, wa.Width - 40)), Math.Max(640, Math.Min(900, wa.Height - 40)));
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            U.WorldSize = cfg.WorldSize > 0 ? cfg.WorldSize : 4000f;

            tabs = new NavTabs { Dock = DockStyle.Fill };
            tabDash = new Page("Dashboard", "", "Live overview of your server");
            tabPlayers = new Page("Players", "", "Everyone online right now - select players, then act on them");
            tabMap = new Page("Live Map", "", "Player positions on your server's map");
            tabConsole = new Page("Console", "", "Raw RCON console - Up/Down for command history");
            tabChat = new Page("Chat", "", "In-game chat, live");
            tabBans = new Page("Bans", "", "Server ban list");
            tabServer = new Page("Server", "", "Server settings, environment and events");
            tabSched = new Page("Restart Scheduler", "", "Daily restarts with in-game warnings");
            tabPlugins = new Page("Plugins & Permissions", "", "Oxide / uMod plugins and permissions");
            tabs.TabPages.AddRange(new Page[] { tabDash, tabPlayers, tabMap, tabConsole, tabChat, tabBans, tabServer, tabSched, tabPlugins });
            Controls.Add(tabs);
            BuildConnectionBar();

            BuildDashboard(); BuildPlayers(); BuildMap(); BuildConsole(); BuildChat(); BuildBans(); BuildServer(); BuildScheduler(); BuildPlugins();

            pollTimer.Tick += delegate { Poll(); };
            reconnectTimer.Interval = 10000;
            reconnectTimer.Tick += delegate { reconnectTimer.Stop(); if (!userDisconnect && (rc == null || !rc.Connected)) DoConnect(); };
            tabs.SelectedIndexChanged += delegate { OnTabChanged(); };
            schedTimer.Interval = 1000; schedTimer.Tick += delegate { SchedTick(); }; schedTimer.Start();
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                if (chkSchedOn.Checked)
                {
                    var up = UpcomingRestarts(1);
                    if (up.Count > 0 && (up[0] - DateTime.UtcNow).TotalMinutes < 35 &&
                        !U.Confirm("A scheduled restart runs at " + up[0].ToString("HH:mm") + " UTC (in " + U.Dur((up[0] - DateTime.UtcNow).TotalSeconds) + ") and only this panel sends its warnings. Close anyway?"))
                    { e.Cancel = true; return; }
                }
                cfg.Save(); if (rc != null) rc.Close();
            };
            Theme.Apply(this);
            Updater.CleanUp();
            Shown += delegate { CheckForUpdates(false); };
            SetConnectedUi(false);
            ActiveControl = btnConnect;
            if (!string.IsNullOrEmpty(cfg.MapPath)) LoadMapImage(cfg.MapPath, false);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitle(this); }

        // ---------------------------------------------------------- UI building
        static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(12, 1, 12, 1), Margin = new Padding(0, 0, 8, 8) };
            b.Click += click; return b;
        }
        static FlowLayoutPanel Bar()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, 0, 10), WrapContents = true };
        }
        static Label Lbl(string t) { return new Label { Text = t, AutoSize = true, Margin = new Padding(8, 9, 6, 0), ForeColor = Theme.Muted }; }
        static ListView MakeList(params string[] cols)
        {
            var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            foreach (var c in cols) lv.Columns.Add(c, 120);
            Theme.StyleList(lv);
            return lv;
        }

        void BuildConnectionBar()
        {
            var bar = new RoundFlow { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(14, 11, 14, 9) };
            txtHost = new TextBox { Width = 136, Text = cfg.Host };
            txtPort = new TextBox { Width = 66, Text = cfg.Port.ToString() };
            txtPw = new TextBox { Width = 128, UseSystemPasswordChar = true, Text = Cfg.Unprotect(cfg.PwEnc) };
            chkRemember = new CheckBox { Text = "Remember", AutoSize = true, Checked = cfg.RememberPw };
            chkAuto = new CheckBox { Text = "Auto-reconnect", AutoSize = true, Checked = cfg.AutoReconnect };
            btnConnect = Btn("Connect", delegate { ToggleConnect(); });
            btnConnect.MinimumSize = new Size(104, 32);
            lblStatus = new Label { AutoSize = true, Text = "Not connected", ForeColor = Theme.Muted, Font = Theme.F(9f) };
            cmbProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            cmbProfile.SelectedIndexChanged += delegate { ApplyProfile(); };
            var saveBtn = Btn("Save", delegate { SaveProfile(); });
            var delBtn = Btn("Delete", delegate { DeleteProfile(); });
            var div = new Panel { Width = 1, Height = 30, BackColor = Theme.Border, Margin = new Padding(6, 1, 14, 0) };
            bar.Controls.AddRange(new Control[] { Lbl("PROFILE"), cmbProfile, saveBtn, delBtn, div, Lbl("HOST"), txtHost, Lbl("PORT"), txtPort, Lbl("PASSWORD"), txtPw, btnConnect, chkRemember, chkAuto });
            foreach (Control c in bar.Controls)
            {
                Label lb = c as Label;
                if (lb != null) { lb.Font = Theme.F(7.75f, FontStyle.Bold); lb.Margin = new Padding(0, 9, 7, 0); }
                else if (c is TextBox || c is ComboBox) c.Margin = new Padding(0, 0, 12, 2);
                else if (c is Button) c.Margin = new Padding(0, 0, 6, 2);
                else if (c is CheckBox) c.Margin = new Padding(8, 4, 0, 0);
            }
            cmbProfile.Margin = new Padding(0, 1, 8, 2);
            btnConnect.Margin = new Padding(2, 0, 6, 2);
            var head = tabs.Header;
            head.Controls.Add(bar);
            bar.SizeChanged += delegate { head.Height = bar.Height + head.Padding.Vertical; };

            var foot = tabs.Footer;
            foot.Paint += delegate (object sender, PaintEventArgs pe) { using (var pen = new Pen(Theme.Border)) pe.Graphics.DrawLine(pen, 22, 0, foot.Width - 22, 0); };
            lblStatus.Margin = new Padding(0); lblStatus.Location = new Point(24, 10);
            foot.Controls.Add(lblStatus);
            btnUpdate = Btn("Updates", delegate { CheckForUpdates(true); });
            btnUpdate.AutoSize = false; btnUpdate.MinimumSize = new Size(0, 26); btnUpdate.Font = Theme.F(8.5f);
            btnUpdate.Size = new Size(130, 26); btnUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnUpdate.Location = new Point(foot.Width - 130 - 22, 5);
            btnUpdate.Text = "Updates  v" + Updater.Current;
            foot.Controls.Add(btnUpdate);
            RefillProfiles();
        }

        // ---------------------------------------------------------- updates
        bool updating;
        void CheckForUpdates(bool manual)
        {
            if (updating) return;
            updating = true; btnUpdate.Enabled = false; btnUpdate.Text = "Checking...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                Updater.Info up = null; string err = null;
                try { up = Updater.Check(); } catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((Action)delegate { UpdateChecked(up, err, manual); }); } catch { }
            });
        }

        void UpdateChecked(Updater.Info up, string err, bool manual)
        {
            btnUpdate.Enabled = true; btnUpdate.Text = "Updates  v" + Updater.Current; updating = false;
            if (err != null) { if (manual) Dlg.Show("Could not check for updates:\n" + err); return; }
            if (up == null) { if (manual) Dlg.Show("You're on the latest version (v" + Updater.Current + ")."); return; }
            string notes = string.IsNullOrWhiteSpace(up.Notes) ? "" : "\n\n" + up.Notes.Trim();
            if (notes.Length > 800) notes = notes.Substring(0, 800) + "...";
            if (!U.Confirm("Version " + up.Version + " is available (you have v" + Updater.Current + "). Download and install it now?" + notes)) return;
            updating = true; btnUpdate.Enabled = false; btnUpdate.Text = "Updating...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string e2 = null;
                try { Updater.Install(up); } catch (Exception ex) { e2 = ex.Message; }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (e2 == null) { cfg.Save(); if (rc != null) rc.Close(); Environment.Exit(0); }
                        else { updating = false; btnUpdate.Enabled = true; btnUpdate.Text = "Updates  v" + Updater.Current; Dlg.Show("Update failed:\n" + e2); }
                    });
                }
                catch { }
            });
        }

        // ---------------------------------------------------------- profiles
        bool applyingProfile;
        void RefillProfiles()
        {
            applyingProfile = true;
            cmbProfile.Items.Clear();
            cmbProfile.Items.Add("(new / unsaved)");
            foreach (var pr in cfg.Profiles) cmbProfile.Items.Add(pr.Name);
            int idx = cfg.Profiles.FindIndex(x => x.Name == cfg.LastProfile);
            cmbProfile.SelectedIndex = idx >= 0 ? idx + 1 : 0;
            applyingProfile = false;
            if (idx >= 0) ApplyProfile();
        }
        void ApplyProfile()
        {
            if (applyingProfile || cmbProfile.SelectedIndex <= 0) return;
            var pr = cfg.Profiles[cmbProfile.SelectedIndex - 1];
            txtHost.Text = pr.Host; txtPort.Text = pr.Port.ToString(); txtPw.Text = Cfg.Unprotect(pr.PwEnc);
            cfg.LastProfile = pr.Name;
            // each server has its own world size / map image
            cfg.WorldSize = 0;
        }
        void SaveProfile()
        {
            int port; if (!int.TryParse(txtPort.Text, out port)) { SetStatus("Invalid port", true); return; }
            string def = cmbProfile.SelectedIndex > 0 ? cmbProfile.Text : "";
            string name = U.Ask("Save server profile", "Profile name:", def);
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            var pr = cfg.Profiles.FirstOrDefault(x => x.Name == name);
            if (pr == null) { pr = new Profile { Name = name }; cfg.Profiles.Add(pr); }
            pr.Host = txtHost.Text.Trim(); pr.Port = port; pr.PwEnc = chkRemember.Checked ? Cfg.Protect(txtPw.Text) : "";
            cfg.LastProfile = name; cfg.Save(); RefillProfiles();
        }
        void DeleteProfile()
        {
            if (cmbProfile.SelectedIndex <= 0) return;
            string name = cmbProfile.Text;
            if (!U.Confirm("Delete profile '" + name + "'?")) return;
            cfg.Profiles.RemoveAll(x => x.Name == name); cfg.LastProfile = ""; cfg.Save(); RefillProfiles();
        }

        void BuildDashboard()
        {
            // Tiles sit in a host 12px narrower than the flow, so the last tile's right margin hangs off the edge and rows end flush.
            var tileHost = new Panel { Dock = DockStyle.Top, Height = 270 };
            var tiles = new FlowLayoutPanel { Location = new Point(0, 0), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            tileHost.Controls.Add(tiles);
            string[] keys = { "Hostname", "Players", "Queue / Joining", "Server FPS", "Entities", "Uptime", "Memory", "Map", "Game time", "Network in/out", "Version", "Save" };
            string[] glyphs = { "\uE774", "\uE716", "\uE916", "\uE9D9", "\uE7B8", "\uE823", "\uE7F4", "\uE707", "\uE706", "\uE895", "\uE8EC", "\uE74E" };
            for (int i = 0; i < keys.Length; i++)
            {
                string k = keys[i], gl = glyphs[i];
                var tile = new Card { Width = k == "Hostname" ? 404 : 196, Height = 78, Margin = new Padding(0, 0, 12, 12), Padding = new Padding(0) };
                tile.Paint += delegate (object s2, PaintEventArgs pe)
                {
                    if (!Gfx.HasIconFont) return;
                    var g = pe.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    var ir = new Rectangle(tile.Width - 42, 12, 28, 28);
                    using (var path = Gfx.Round(ir, 9)) using (var br = new SolidBrush(Theme.AccentSoft)) g.FillPath(br, path);
                    Gfx.DrawIcon(g, gl, ir, Theme.Accent, 10.5f);
                };
                var lk = new Label { Text = k.ToUpperInvariant(), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.F(7.75f, FontStyle.Bold), Location = new Point(16, 13), BackColor = Theme.Surface };
                var v = new Label { Text = "-", AutoSize = false, AutoEllipsis = true, Font = Theme.F(14f, FontStyle.Bold), Location = new Point(14, 37), Size = new Size(tile.Width - 28, 32), BackColor = Theme.Surface };
                v.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                tile.Controls.Add(lk); tile.Controls.Add(v);
                dash[k] = v; tiles.Controls.Add(tile);
            }
            // stretch the tiles so each row is filled edge to edge
            bool sizing = false;
            EventHandler relayout = delegate
            {
                if (sizing || tileHost.Width < 100) return;
                sizing = true;
                int avail = tileHost.Width + 12;
                int cols = Math.Max(3, Math.Min(7, avail / 214));
                int unit = avail / cols - 12;
                tiles.SuspendLayout();
                tiles.MaximumSize = new Size(avail, 0); tiles.MinimumSize = new Size(avail, 0);
                foreach (Control t in tiles.Controls) t.Width = t == tiles.Controls[0] ? unit * 2 + 12 : unit;
                tiles.ResumeLayout(true);
                tileHost.Height = tiles.Height;
                sizing = false;
            };
            tileHost.Resize += relayout;
            tiles.SizeChanged += delegate { if (!sizing) tileHost.Height = tiles.Height; };

            var sc = new Card { Text = "Players online", Glyph = "\uE9D2", Dock = DockStyle.Fill, Padding = new Padding(10, 0, 14, 12) };
            spark = new SparkPanel { Dock = DockStyle.Fill };
            sc.Controls.Add(spark);

            var act = new Card { Text = "Quick actions", Glyph = "\uE945", Dock = DockStyle.Bottom, Height = 134, Padding = new Padding(16, 0, 16, 6) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            txtBroadcast = new TextBox { Width = 380 };
            numRestart = new NumericUpDown { Minimum = 0, Maximum = 3600, Value = 300, Width = 70 };
            txtRestartReason = new TextBox { Width = 220, Text = "Server restarting" };
            flow.Controls.Add(Lbl("BROADCAST"));
            flow.Controls.Add(txtBroadcast);
            flow.Controls.Add(Btn("Send to all", async delegate { if (txtBroadcast.Text.Trim().Length > 0) { await Run("say " + U.Q(txtBroadcast.Text)); txtBroadcast.Clear(); } }));
            flow.SetFlowBreak(flow.Controls[flow.Controls.Count - 1], true);
            flow.Controls.Add(Btn("Save world", async delegate { await Run("server.save"); }));
            flow.Controls.Add(Btn("Write config", async delegate { await Run("server.writecfg"); }));
            flow.Controls.Add(Lbl("RESTART IN (SEC)"));
            flow.Controls.Add(numRestart);
            flow.Controls.Add(Lbl("REASON"));
            flow.Controls.Add(txtRestartReason);
            flow.Controls.Add(Btn("Restart", async delegate
            {
                if (U.Confirm("Restart the server in " + numRestart.Value + " seconds?"))
                    await Run("restart " + (int)numRestart.Value + " " + U.Q(txtRestartReason.Text));
            }));
            flow.Controls.Add(Btn("Quit now", async delegate { if (U.Confirm("Shut the server down immediately?")) await Run("quit"); }));
            foreach (Control c in flow.Controls)
            {
                Label lb = c as Label;
                if (lb != null) { lb.Font = Theme.F(7.75f, FontStyle.Bold); lb.Margin = new Padding(lb == flow.Controls[0] ? 0 : 8, 9, 7, 0); }
                else if (!(c is Button)) c.Margin = new Padding(0, 0, 10, 8);
            }
            act.Controls.Add(flow);

            var holder = new Panel { Dock = DockStyle.Fill };
            holder.Controls.Add(sc); holder.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12 }); holder.Controls.Add(act); holder.Controls.Add(tileHost);
            tabDash.Controls.Add(holder);
        }

        void BuildPlayers()
        {
            var bar = Bar();
            txtFilter = new TextBox { Width = 180 };
            txtFilter.TextChanged += delegate { RefreshPlayerList(); };
            bar.Controls.Add(Lbl("Filter")); bar.Controls.Add(txtFilter);
            bar.Controls.Add(Btn("Refresh", async delegate { await Poll(); }));
            bar.Controls.Add(Btn("Kick", delegate { ActKick(); }));
            bar.Controls.Add(Btn("Ban", delegate { ActBan(); }));
            bar.Controls.Add(Btn("Give item", delegate { ActGive(); }));
            bar.Controls.Add(Btn("Teleport A \u2192 B", delegate { ActTeleport(); }));
            bar.Controls.Add(Btn("Make moderator", delegate { ActRole("moderatorid"); }));
            bar.Controls.Add(Btn("Make owner", delegate { ActRole("ownerid"); }));
            bar.Controls.Add(Btn("Remove role", delegate { ActRemoveRole(); }));
            bar.Controls.Add(Btn("Show on map", delegate { ShowOnMap(); }));

            lvPlayers = MakeList("Name", "SteamID", "Ping", "Online (min)", "Health", "Grid", "IP");
            lvPlayers.Columns[0].Width = 200; lvPlayers.Columns[1].Width = 150; lvPlayers.Columns[2].Width = 60;
            lvPlayers.Columns[3].Width = 110; lvPlayers.Columns[4].Width = 80; lvPlayers.Columns[5].Width = 80; lvPlayers.Columns[6].Width = 160;
            lvPlayers.ListViewItemSorter = sorter;
            lvPlayers.ColumnClick += delegate (object s, ColumnClickEventArgs e)
            {
                if (sorter.Col == e.Column) sorter.Asc = !sorter.Asc; else { sorter.Col = e.Column; sorter.Asc = true; }
                lvPlayers.Sort();
            };
            lvPlayers.DoubleClick += delegate { ShowOnMap(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Kick...", null, delegate { ActKick(); });
            menu.Items.Add("Ban...", null, delegate { ActBan(); });
            menu.Items.Add("Give item...", null, delegate { ActGive(); });
            menu.Items.Add("Teleport to another player...", null, delegate { ActTeleport(); });
            menu.Items.Add("Show on map", null, delegate { ShowOnMap(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copy SteamID", null, delegate { var p = SelPlayers(); if (p.Count > 0) Clipboard.SetText(p[0].Id); });
            menu.Items.Add("Open Steam profile", null, delegate { var p = SelPlayers(); if (p.Count > 0) Process.Start("https://steamcommunity.com/profiles/" + p[0].Id); });
            Theme.StyleMenu(menu);
            lvPlayers.ContextMenuStrip = menu;

            tplBar = Bar();
            tabPlayers.Controls.Add(Theme.Framed(lvPlayers, null));
            tabPlayers.Controls.Add(tplBar);
            tabPlayers.Controls.Add(bar);
            BuildTemplateButtons();
        }

        void BuildTemplateButtons()
        {
            tplBar.Controls.Clear();
            tplBar.Controls.Add(new Label { Text = "Custom actions:", AutoSize = true, Margin = new Padding(6, 8, 2, 0), ForeColor = Color.DimGray });
            foreach (string t in cfg.Templates)
            {
                int i = t.IndexOf('|'); if (i <= 0) continue;
                string label = t.Substring(0, i).Trim(), cmd = t.Substring(i + 1).Trim();
                var b = Btn(label, async delegate
                {
                    var sel = SelPlayers(); if (sel.Count == 0) return;
                    foreach (var p in sel) await Run(cmd.Replace("{id}", p.Id).Replace("{name}", p.Name));
                });
                tplBar.Controls.Add(b);
            }
            tplBar.Controls.Add(Btn("Edit actions...", delegate { EditTemplates(); }));
            Theme.Apply(tplBar);
        }
        void EditTemplates()
        {
            using (var f = new Form())
            {
                f.Text = "Custom player actions"; f.StartPosition = FormStartPosition.CenterParent; f.ClientSize = new Size(576, 360);
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                var l = new Label { Left = 20, Top = 16, Width = 536, Height = 56, Text = "One per line:  Label|command   Use {id} for the SteamID and {name} for the player name (add quotes yourself if needed). Examples: Kill|global.killplayer {id}   Heal|heal {id}. Commands not known to your server will show an error in the Console tab." };
                var t = new TextBox { Left = 20, Top = 78, Width = 536, Height = 214, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f), Text = string.Join("\r\n", cfg.Templates.ToArray()) };
                var ok = new Button { Text = "Save", Left = 374, Top = 310, Width = 88, Height = 32, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 468, Top = 310, Width = 88, Height = 32, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { l, t, ok, cancel }); f.AcceptButton = null; f.CancelButton = cancel; f.Font = Theme.F(9.5f); Theme.Apply(f); f.HandleCreated += delegate { Theme.DarkTitle(f); };
                if (f.ShowDialog(this) != DialogResult.OK) return;
                cfg.Templates = t.Text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.IndexOf('|') > 0).ToList();
                cfg.Save(); BuildTemplateButtons();
            }
        }

        void BuildMap()
        {
            map = new MapControl { Dock = DockStyle.Fill, WorldSize = U.WorldSize, ShowNames = cfg.ShowNames };
            map.PlayerClicked += delegate (PlayerInfo p)
            {
                for (int i = 0; i < lbMapPlayers.Items.Count; i++)
                    if (((PlayerInfo)lbMapPlayers.Items[i]).Id == p.Id) { lbMapPlayers.SelectedIndex = i; break; }
            };

            var side = new Panel { Dock = DockStyle.Right, Width = 312, Padding = new Padding(16, 0, 0, 0) };
            lbMapPlayers = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, FormattingEnabled = true };
            Theme.StyleListBox(lbMapPlayers);
            lbMapPlayers.Format += delegate (object s, ListControlConvertEventArgs e)
            {
                var p = (PlayerInfo)e.ListItem; e.Value = p.Name + (p.HasPos ? "   [" + p.GridRef + "]" : "   (no position)");
            };
            lbMapPlayers.SelectedIndexChanged += delegate
            {
                var p = lbMapPlayers.SelectedItem as PlayerInfo;
                if (p != null) { map.SelectedId = p.Id; map.CenterOn(p); }
            };
            var optCard = new Card { Text = "Map options", Glyph = "\uE713", Dock = DockStyle.Top, Padding = new Padding(16, 0, 14, 10) };
            var opts = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            chkNames = new CheckBox { Text = "Show player names", AutoSize = true, Checked = cfg.ShowNames };
            chkNames.CheckedChanged += delegate { map.ShowNames = chkNames.Checked; cfg.ShowNames = chkNames.Checked; map.Invalidate(); };
            chkTrack = new CheckBox { Text = "Track positions (printpos)", AutoSize = true, Checked = cfg.TrackPos };
            chkTrack.CheckedChanged += delegate { cfg.TrackPos = chkTrack.Checked; };
            numWorld = new NumericUpDown { Minimum = 1000, Maximum = 8000, Increment = 250, Value = (decimal)U.WorldSize, Width = 92 };
            numWorld.ValueChanged += delegate { if (!settingWorld) SetWorldSize((float)numWorld.Value, true); };
            numMargin = new NumericUpDown { Minimum = 0, Maximum = 2000, Increment = 25, Value = cfg.MapMargin, Width = 92 };
            map.Margin = cfg.MapMargin;
            numMargin.ValueChanged += delegate { map.Margin = (float)numMargin.Value; cfg.MapMargin = (int)numMargin.Value; map.Invalidate(); };
            var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var l1 = Lbl("World size"); var l2 = Lbl("Image edge margin (m)");
            l1.Margin = l2.Margin = new Padding(0, 9, 6, 0);
            numWorld.Margin = numMargin.Margin = new Padding(0, 0, 0, 6);
            grid.Controls.Add(l1, 0, 0); grid.Controls.Add(numWorld, 1, 0); grid.Controls.Add(l2, 0, 1); grid.Controls.Add(numMargin, 1, 1);
            lblMapNote = new Label { AutoSize = true, MaximumSize = new Size(262, 0), ForeColor = Theme.Muted, Margin = new Padding(0, 4, 0, 4), Font = Theme.F(8.5f),
                Text = "Scroll to zoom, drag to pan, click a player to select. If dots look slightly off, adjust the edge margin." };
            var fetch = Btn("Fetch map from RustMaps", async delegate { await FetchRustMaps(false); });
            fetch.AutoSize = false; fetch.Size = new Size(262, 34); fetch.Margin = new Padding(0, 2, 0, 8);
            Func<Button, Button, Control> pair = delegate (Button x, Button y)
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
                x.AutoSize = y.AutoSize = false; x.Size = y.Size = new Size(127, 34);
                x.Margin = new Padding(0, 0, 8, 0); y.Margin = new Padding(0);
                row.Controls.Add(x); row.Controls.Add(y); return row;
            };
            var mapBtns = new Control[] { fetch,
                pair(Btn("RustMaps key...", delegate { SetRmKey(); }), Btn("Load image...", delegate { PickMapImage(); })),
                pair(Btn("Reset view", delegate { map.ResetView(); }), Btn("Clear image", delegate { map.Bg = null; cfg.MapPath = ""; map.Invalidate(); })) };
            chkNames.Margin = chkTrack.Margin = new Padding(0, 0, 0, 6);
            opts.Controls.AddRange(new Control[] { chkNames, chkTrack, grid });
            opts.Controls.AddRange(mapBtns);
            opts.Controls.Add(lblMapNote);
            optCard.Controls.Add(opts);
            opts.SizeChanged += delegate { optCard.Height = opts.Height + 36 + optCard.Padding.Bottom + 4; };
            var listCard = Theme.Framed(lbMapPlayers, "Players on map");
            listCard.Glyph = "\uE716"; listCard.Dock = DockStyle.Fill;
            side.Controls.Add(listCard); side.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 14 }); side.Controls.Add(optCard);

            tabMap.Controls.Add(map); tabMap.Controls.Add(side);
        }

        void BuildConsole()
        {
            rtbConsole = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Theme.Input, ForeColor = Theme.Text, Font = new Font("Consolas", 9.5f), WordWrap = true, HideSelection = false };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(0, 12, 0, 0) };
            txtCmd = new TextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 10f) };
            txtCmd.KeyDown += async delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    string c = txtCmd.Text.Trim(); if (c.Length == 0) return;
                    history.Add(c); histIdx = history.Count; txtCmd.Clear();
                    await Run(c);
                }
                else if (e.KeyCode == Keys.Up && history.Count > 0) { histIdx = Math.Max(0, histIdx - 1); txtCmd.Text = history[histIdx]; txtCmd.SelectionStart = txtCmd.TextLength; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down && history.Count > 0) { histIdx = Math.Min(history.Count, histIdx + 1); txtCmd.Text = histIdx < history.Count ? history[histIdx] : ""; txtCmd.SelectionStart = txtCmd.TextLength; e.SuppressKeyPress = true; }
            };
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(10, 0, 0, 0) };
            chkScroll = new CheckBox { Text = "Auto-scroll", Checked = true, AutoSize = true, Margin = new Padding(6, 4, 10, 0) };
            right.Controls.Add(chkScroll);
            right.Controls.Add(Btn("Clear", delegate { rtbConsole.Clear(); }));
            right.Controls.Add(Btn("Save log...", delegate
            {
                using (var d = new SaveFileDialog { Filter = "Text|*.txt", FileName = "rcon-log.txt" })
                    if (d.ShowDialog() == DialogResult.OK) File.WriteAllText(d.FileName, rtbConsole.Text);
            }));
            bottom.Controls.Add(txtCmd); bottom.Controls.Add(right);
            tabConsole.Controls.Add(rtbConsole); tabConsole.Controls.Add(bottom);
        }

        void BuildChat()
        {
            rtbChat = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Segoe UI", 10.5f), HideSelection = false, BackColor = Theme.Input, ForeColor = Theme.Text };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(0, 12, 0, 0) };
            txtChatSend = new TextBox { Dock = DockStyle.Fill };
            txtChatSend.KeyDown += async delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && txtChatSend.Text.Trim().Length > 0)
                {
                    e.SuppressKeyPress = true;
                    string t = txtChatSend.Text; txtChatSend.Clear();
                    await Run("say " + U.Q(t));
                }
            };
            var send = Btn("Send as server", async delegate
            {
                if (txtChatSend.Text.Trim().Length == 0) return;
                string t = txtChatSend.Text; txtChatSend.Clear(); await Run("say " + U.Q(t));
            });
            send.Dock = DockStyle.Right; send.AutoSize = false; send.Width = 150;
            bottom.Controls.Add(txtChatSend); bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10 }); bottom.Controls.Add(send);
            tabChat.Controls.Add(rtbChat); tabChat.Controls.Add(bottom);
        }

        void BuildBans()
        {
            var bar = Bar();
            bar.Controls.Add(Btn("Refresh", async delegate { await RefreshBans(); }));
            bar.Controls.Add(Btn("Add ban by SteamID...", async delegate
            {
                string id = U.Ask("Ban", "SteamID64 to ban:", ""); if (string.IsNullOrWhiteSpace(id)) return;
                string reason = U.Ask("Ban", "Reason:", "Banned by admin"); if (reason == null) return;
                await Run("banid " + id.Trim() + " " + U.Q("unknown") + " " + U.Q(reason)); await Run("server.writecfg"); await RefreshBans();
            }));
            bar.Controls.Add(Btn("Unban selected", async delegate
            {
                if (lvBans.SelectedItems.Count == 0) return;
                foreach (ListViewItem it in lvBans.SelectedItems) await Run("unban " + it.SubItems[0].Text);
                await Run("server.writecfg"); await RefreshBans();
            }));
            lvBans = MakeList("SteamID", "Name", "Expiry", "Reason");
            lvBans.Columns[0].Width = 170; lvBans.Columns[1].Width = 220; lvBans.Columns[2].Width = 120;
            tabBans.Controls.Add(Theme.Framed(lvBans, null)); tabBans.Controls.Add(bar);
        }

        void BuildServer()
        {
            var bar = Bar();
            bar.Controls.Add(Btn("Refresh values", async delegate { await RefreshVars(); }));
            bar.Controls.Add(Lbl("Double-click a value to change it"));
            lvVars = MakeList("Setting", "Value");
            lvVars.Columns[0].Width = 220; lvVars.Columns[1].Width = 420;
            foreach (var v in ServerVars) lvVars.Items.Add(new ListViewItem(new string[] { v, "" }));
            lvVars.DoubleClick += async delegate
            {
                if (lvVars.SelectedItems.Count == 0) return;
                var it = lvVars.SelectedItems[0];
                string nv = U.Ask("Change setting", it.Text, it.SubItems[1].Text);
                if (nv == null) return;
                await Run(it.Text + " " + U.Q(nv));
                await Run("server.writecfg");
                try { it.SubItems[1].Text = U.ConvarValue(await rc.Cmd(it.Text)); } catch { }
            };

            var ev = new Card { Text = "Environment && events", Glyph = "\uE706", Dock = DockStyle.Right, Width = 316, Padding = new Padding(16, 0, 14, 12) };
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            f.Controls.Add(Btn("Set time to morning (8)", async delegate { await Run("env.time 8"); }));
            f.Controls.Add(Btn("Set time to noon (12)", async delegate { await Run("env.time 12"); }));
            f.Controls.Add(Btn("Set time to night (22)", async delegate { await Run("env.time 22"); }));
            f.Controls.Add(Btn("Freeze time (env.progresstime false)", async delegate { await Run("env.progresstime false"); }));
            f.Controls.Add(Btn("Resume time (env.progresstime true)", async delegate { await Run("env.progresstime true"); }));
            f.Controls.Add(new Label { Text = " ", AutoSize = true });
            f.Controls.Add(Btn("Call airdrop", async delegate { await Run("supply.drop"); }));
            f.Controls.Add(Btn("Call patrol helicopter", async delegate { await Run("heli.call"); }));
            f.Controls.Add(new Label { Text = " ", AutoSize = true });
            f.Controls.Add(Btn("Print server stats (stats)", async delegate { await Run("stats"); tabs.SelectedTab = tabConsole; }));
            f.Controls.Add(Btn("Print server info (serverinfo)", async delegate { await Run("serverinfo"); tabs.SelectedTab = tabConsole; }));
            foreach (Control c in f.Controls) if (c is Button) { ((Button)c).AutoSize = false; c.Width = 282; c.Height = 34; c.Margin = new Padding(0, 0, 0, 8); }
            ev.Controls.Add(f);

            tabServer.Controls.Add(Theme.Framed(lvVars, null)); tabServer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 14 }); tabServer.Controls.Add(ev); tabServer.Controls.Add(bar);
        }

        void BuildScheduler()
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 4, 0, 0), AutoScroll = true };
            chkSchedOn = new CheckBox { Text = "Enable scheduled restarts from this panel (only runs while the panel is open and connected)", AutoSize = true, Checked = cfg.SchedEnabled };
            txtSchedTimes = new TextBox { Width = 420, Text = cfg.SchedTimes };
            txtSchedWarn = new TextBox { Width = 420, Text = cfg.SchedWarn };
            txtSchedMsg = new TextBox { Width = 420, Text = cfg.SchedMsg };
            txtSchedReason = new TextBox { Width = 420, Text = cfg.SchedReason };
            lblNext = new Label { AutoSize = true, Font = Theme.F(13f, FontStyle.Bold), ForeColor = Theme.Accent, Margin = new Padding(0, 18, 3, 4), Text = "Next restart: -" };
            lblUpcoming = new Label { AutoSize = true, ForeColor = Theme.Muted, Text = "", Margin = new Padding(0, 0, 0, 0) };
            p.Controls.Add(chkSchedOn);
            p.Controls.Add(Lbl("Restart times, daily, in server UTC (HH:mm, comma separated)")); p.Controls.Add(txtSchedTimes);
            p.Controls.Add(Lbl("Warn this many minutes before (comma separated)")); p.Controls.Add(txtSchedWarn);
            p.Controls.Add(Lbl("Warning message ({time} = e.g. \"5 minutes\")")); p.Controls.Add(txtSchedMsg);
            p.Controls.Add(Lbl("Restart reason (shown by the server countdown)")); p.Controls.Add(txtSchedReason);
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
            row.Controls.Add(Btn("Apply && save", delegate { ApplySchedule(); }));
            row.Controls.Add(Btn("Send test warning", async delegate { await Run("say " + U.Q(txtSchedMsg.Text.Replace("{time}", "5 minutes") + " (test)")); }));
            row.Controls.Add(Btn("Reload JetsScheduledRestart plugin", async delegate { await Run("o.reload JetsScheduledRestart"); await RefreshPlugins(); }));
            p.Controls.Add(row);
            p.Controls.Add(lblNext); p.Controls.Add(lblUpcoming);
            p.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 16, 3, 3),
                Text = "Warnings are sent as server chat. At 60 seconds before the restart the panel runs the server's own 'restart' countdown. For restarts that must happen while no admin is online, keep using your server-side restarter plugin; this scheduler is for when the panel is left running. Don't run both for the same time or players will get duplicate warnings." });
            foreach (Control c in p.Controls) { Label lb = c as Label; if (lb != null && lb != lblNext && lb != lblUpcoming && lb.Margin.Top == 9) lb.Margin = new Padding(0, 12, 0, 6); }
            chkSchedOn.Margin = new Padding(0, 0, 0, 6);
            foreach (var t in new TextBox[] { txtSchedTimes, txtSchedWarn, txtSchedMsg, txtSchedReason }) { t.Width = 460; t.Margin = new Padding(0); }
            var card = Theme.Framed(p, "Restart schedule");
            card.Glyph = "\uE823"; card.Padding = new Padding(18, 0, 18, 14); card.Dock = DockStyle.Fill;
            tabSched.Controls.Add(card);
        }

        static List<TimeSpan> ParseTimes(string s)
        {
            var l = new List<TimeSpan>();
            foreach (string part in s.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            { TimeSpan t; if (TimeSpan.TryParse(part.Trim(), CultureInfo.InvariantCulture, out t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)) l.Add(t); }
            return l;
        }
        static List<int> ParseOffsets(string s)
        {
            var l = new List<int>();
            foreach (string part in s.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            { int m; if (int.TryParse(part.Trim(), out m) && m > 0) l.Add(m); }
            return l.Distinct().OrderByDescending(x => x).ToList();
        }

        void ApplySchedule()
        {
            cfg.SchedEnabled = chkSchedOn.Checked; cfg.SchedTimes = txtSchedTimes.Text; cfg.SchedWarn = txtSchedWarn.Text;
            cfg.SchedMsg = txtSchedMsg.Text; cfg.SchedReason = txtSchedReason.Text; cfg.Save(); fired.Clear();
            if (ParseTimes(cfg.SchedTimes).Count == 0) Dlg.Show("No valid restart times found. Use HH:mm, e.g. 04:00,16:00");
        }

        List<DateTime> UpcomingRestarts(int n)
        {
            var times = ParseTimes(txtSchedTimes.Text);
            var res = new List<DateTime>();
            if (times.Count == 0) return res;
            DateTime day = DateTime.UtcNow.Date;
            for (int d = 0; d < 8 && res.Count < n; d++)
                foreach (var t in times.OrderBy(x => x))
                {
                    DateTime dt = day.AddDays(d) + t;
                    if (dt > DateTime.UtcNow && res.Count < n) res.Add(dt);
                }
            return res;
        }

        async void SchedTick()
        {
            var up = UpcomingRestarts(4);
            if (up.Count == 0) { lblNext.Text = "Next restart: (no valid times)"; lblUpcoming.Text = ""; return; }
            var next = up[0];
            double rem = (next - DateTime.UtcNow).TotalSeconds;
            lblNext.Text = "Next restart: " + next.ToString("ddd HH:mm") + " UTC  (in " + U.Dur(rem) + ")" + (chkSchedOn.Checked ? "" : "  - scheduler is OFF");
            lblUpcoming.Text = "Then: " + string.Join("   |   ", up.Skip(1).Select(x => x.ToString("ddd HH:mm") + " UTC").ToArray()) + "     (your local time for next: " + next.ToLocalTime().ToString("ddd h:mm tt") + ")";
            if (!chkSchedOn.Checked || rc == null || !rc.Connected) return;

            foreach (int off in ParseOffsets(txtSchedWarn.Text))
            {
                string key = next.Ticks + ":" + off;
                // only fire inside a 90s window so a stale schedule can't spam old warnings
                if (rem <= off * 60 && rem > off * 60 - 90 && fired.Add(key))
                    await Run("say " + U.Q(txtSchedMsg.Text.Replace("{time}", off + (off == 1 ? " minute" : " minutes"))));
            }
            string fk = next.Ticks + ":final";
            if (rem <= 60 && rem > 0 && fired.Add(fk))
                await Run("restart " + Math.Max(1, (int)Math.Ceiling(rem)) + " " + U.Q(txtSchedReason.Text));
            if (fired.Count > 200) fired.Clear();
        }

        void BuildPlugins()
        {
            var bar = Bar();
            bar.Controls.Add(Btn("Refresh", async delegate { await RefreshPlugins(); }));
            bar.Controls.Add(Btn("Reload selected", async delegate { await PluginCmd("o.reload"); }));
            bar.Controls.Add(Btn("Unload selected", async delegate { await PluginCmd("o.unload"); }));
            bar.Controls.Add(Btn("Load by name...", async delegate
            {
                string n = U.Ask("Load plugin", "Plugin name (no extension):", ""); if (string.IsNullOrWhiteSpace(n)) return;
                await Run("o.load " + n.Trim()); await RefreshPlugins();
            }));
            bar.Controls.Add(Btn("Reload ALL", async delegate { if (U.Confirm("Reload every plugin?")) { await Run("o.reload *"); await RefreshPlugins(); } }));
            lvPlugins = MakeList("Plugin", "Version", "Author", "File");
            lvPlugins.Columns[0].Width = 220; lvPlugins.Columns[1].Width = 80; lvPlugins.Columns[2].Width = 140; lvPlugins.Columns[3].Width = 200;

            var perm = new Card { Text = "Oxide permissions", Glyph = "\uE72E", Dock = DockStyle.Bottom, Height = 280, Padding = new Padding(16, 0, 16, 14) };
            var top = Bar();
            rbUser = new RadioButton { Text = "User (SteamID)", Checked = true, AutoSize = true };
            rbGroup = new RadioButton { Text = "Group", AutoSize = true };
            txtPermTarget = new TextBox { Width = 170 };
            txtPermName = new TextBox { Width = 220 };
            top.Controls.AddRange(new Control[] { rbUser, rbGroup, txtPermTarget, Lbl("Permission / group"), txtPermName,
                Btn("Grant", async delegate { await PermCmd("oxide.grant", true); }),
                Btn("Revoke", async delegate { await PermCmd("oxide.revoke", true); }),
                Btn("Add to group", async delegate { await Run2("oxide.usergroup add " + txtPermTarget.Text.Trim() + " " + txtPermName.Text.Trim()); }),
                Btn("Remove from group", async delegate { await Run2("oxide.usergroup remove " + txtPermTarget.Text.Trim() + " " + txtPermName.Text.Trim()); }),
                Btn("Show user", async delegate { await Run2("oxide.show user " + txtPermTarget.Text.Trim()); }),
                Btn("Show groups", async delegate { await Run2("oxide.show groups"); }),
                Btn("Show perms", async delegate { await Run2("oxide.show perms"); }) });
            txtPermOut = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f) };
            perm.Controls.Add(txtPermOut); perm.Controls.Add(top);

            tabPlugins.Controls.Add(Theme.Framed(lvPlugins, null)); tabPlugins.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 14 }); tabPlugins.Controls.Add(perm); tabPlugins.Controls.Add(bar);
        }

        // ---------------------------------------------------------- connection
        void SetStatus(string s, bool bad = false) { lblStatus.Text = s; lblStatus.ForeColor = bad ? Theme.DangerText : Theme.Muted; }

        void SetConnectedUi(bool on)
        {
            btnConnect.Text = on ? "Disconnect" : "Connect"; Theme.StyleButton(btnConnect, on ? "danger" : "primary"); tabs.SetStatus(on, on ? "Connected" : "Offline");
            foreach (var t in new TextBox[] { txtHost, txtPort, txtPw }) { t.ReadOnly = on; t.TabStop = !on; t.ForeColor = on ? Theme.Muted : Theme.Text; }
        }

        void ToggleConnect()
        {
            if (rc != null && rc.Connected) { userDisconnect = true; pollTimer.Stop(); rc.Close(); SetConnectedUi(false); SetStatus("Disconnected"); }
            else { userDisconnect = false; DoConnect(); }
        }

        async void DoConnect()
        {
            int port;
            if (!int.TryParse(txtPort.Text, out port)) { SetStatus("Invalid port", true); return; }
            cfg.Host = txtHost.Text.Trim(); cfg.Port = port; cfg.RememberPw = chkRemember.Checked; cfg.AutoReconnect = chkAuto.Checked;
            cfg.PwEnc = chkRemember.Checked ? Cfg.Protect(txtPw.Text) : ""; cfg.Save();
            btnConnect.Enabled = false; SetStatus("Connecting...");
            try
            {
                if (rc != null) rc.Close();
                rc = new RconClient();
                rc.Log += OnRconLog; rc.Closed += OnRconClosed;
                await rc.ConnectAsync(cfg.Host, cfg.Port, txtPw.Text);
                SetConnectedUi(true);
                SetStatus("Connected to " + cfg.Host + ":" + cfg.Port);
                AppendConsole("Connected to " + cfg.Host + ":" + cfg.Port, Theme.Ok);
                pollTimer.Interval = Math.Max(2, cfg.PollSeconds) * 1000; pollTimer.Start();
                await InitServer();
                await Poll();
            }
            catch (Exception e)
            {
                SetStatus("Connection failed: " + e.Message, true);
                if (cfg.AutoReconnect && !userDisconnect) reconnectTimer.Start();
            }
            btnConnect.Enabled = true;
        }

        void OnRconClosed(string reason)
        {
            BeginInvoke((Action)(delegate
            {
                pollTimer.Stop(); SetConnectedUi(false);
                SetStatus("Disconnected: " + reason, true);
                AppendConsole("Disconnected: " + reason, Theme.DangerText);
                if (chkAuto.Checked && !userDisconnect) { SetStatus("Disconnected - retrying in 10s", true); reconnectTimer.Start(); }
            }));
        }

        async Task InitServer()
        {
            if (cfg.WorldSize <= 0)
            {
                foreach (var c in new string[] { "server.worldsize", "world.size" })
                {
                    try
                    {
                        var m = Regex.Match(await rc.Cmd(c), @"\d{3,5}");
                        int ws;
                        if (m.Success && int.TryParse(m.Value, out ws) && ws >= 1000 && ws <= 8000) { SetWorldSize(ws, false); break; }
                    }
                    catch { }
                }
            }
            await RefreshBans();
            await RefreshPlugins();
            await RefreshVars();
            await FetchRustMaps(true);
        }

        void SetWorldSize(float ws, bool save)
        {
            U.WorldSize = ws; map.WorldSize = ws;
            if (save) cfg.WorldSize = (int)ws;
            if ((float)numWorld.Value != ws && ws >= 1000 && ws <= 8000) { settingWorld = true; numWorld.Value = (decimal)ws; settingWorld = false; }
            map.Invalidate();
        }
        bool settingWorld;

        // ---------------------------------------------------------- logging
        void OnRconLog(string type, string msg)
        {
            BeginInvoke((Action)(delegate
            {
                if (string.IsNullOrEmpty(msg)) return;
                if (type == "Chat") { HandleChat(msg); return; }
                Color c = type == "Error" ? Theme.DangerText : type == "Warning" ? Theme.Warn : Theme.Text;
                if (msg.StartsWith("[CHAT]")) { HandleChat(msg); }
                AppendConsole(msg.TrimEnd(), c);
            }));
        }

        void AppendConsole(string text, Color c)
        {
            AppendTo(rtbConsole, DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\n", c, chkScroll.Checked);
        }

        static void AppendTo(RichTextBox box, string text, Color c, bool scroll)
        {
            if (box.TextLength > 400000) { box.Select(0, 150000); box.SelectedText = ""; }
            box.SelectionStart = box.TextLength; box.SelectionLength = 0;
            box.SelectionColor = c; box.AppendText(text);
            if (scroll) { box.SelectionStart = box.TextLength; box.ScrollToCaret(); }
        }

        void HandleChat(string msg)
        {
            string name = "", text = msg; Color col = Theme.Accent;
            object o = msg.StartsWith("{") ? U.Parse(msg) : null;
            if (o != null)
            {
                name = U.Str(o, "Username"); text = U.Str(o, "Message");
                string hex = U.Str(o, "Color");
                if (hex.StartsWith("#") && hex.Length >= 7) { try { col = ColorTranslator.FromHtml(hex.Substring(0, 7)); } catch { } }
            }
            else
            {
                var m = Regex.Match(msg, @"^\[CHAT\]\s*(.+?)\s*:\s*(.*)$");
                if (m.Success) { name = m.Groups[1].Value; text = m.Groups[2].Value; }
            }
            AppendTo(rtbChat, DateTime.Now.ToString("HH:mm") + "  ", Theme.Muted, true);
            AppendTo(rtbChat, (name.Length > 0 ? name : "?") + ": ", col, true);
            AppendTo(rtbChat, text + "\n", Theme.Text, true);
            AppendConsole("[CHAT] " + name + ": " + text, Theme.Ok);
        }

        // Run a command, echo it and the reply into the console.
        async Task<string> Run(string cmd)
        {
            if (rc == null || !rc.Connected) { SetStatus("Not connected", true); return ""; }
            AppendConsole("> " + cmd, Theme.Info);
            try
            {
                string r = await rc.Cmd(cmd);
                if (!string.IsNullOrWhiteSpace(r)) AppendConsole(r.TrimEnd(), Theme.Text);
                return r;
            }
            catch (Exception e) { AppendConsole("! " + e.Message, Theme.DangerText); return ""; }
        }
        async Task Run2(string cmd) { string r = await Run(cmd); txtPermOut.Text = (r ?? "").Replace("\n", "\r\n"); }

        // ---------------------------------------------------------- polling
        void OnTabChanged()
        {
            if (tabs.SelectedTab == tabMap || tabs.SelectedTab == tabPlayers) Poll();
        }

        async Task Poll()
        {
            if (polling || rc == null || !rc.Connected) return;
            polling = true;
            try
            {
                UpdateDashboard(await rc.Cmd("serverinfo"));
                ParsePlayers(await rc.Cmd("playerlist"));
                if (cfg.TrackPos && (tabs.SelectedTab == tabMap || tabs.SelectedTab == tabPlayers)) await UpdatePositions();
                RefreshPlayerList(); RefreshMapList();
                SetStatus("Connected - " + players.Count + " online - updated " + DateTime.Now.ToString("HH:mm:ss")); tabs.SetStatus(true, "Online - " + players.Count + " players");
            }
            catch (Exception e) { SetStatus("Poll failed: " + e.Message, true); }
            finally { polling = false; }
        }

        void UpdateDashboard(string json)
        {
            object o = U.Parse(json);
            if (o == null) return;
            dash["Hostname"].Text = U.Str(o, "Hostname");
            int max = (int)U.Num(o, "MaxPlayers"), cur = (int)U.Num(o, "Players");
            dash["Players"].Text = cur + " / " + max;
            dash["Queue / Joining"].Text = (int)U.Num(o, "Queued") + " / " + (int)U.Num(o, "Joining");
            dash["Server FPS"].Text = U.Num(o, "Framerate").ToString("0.0");
            dash["Entities"].Text = ((int)U.Num(o, "EntityCount")).ToString("N0");
            dash["Uptime"].Text = U.Dur(U.Num(o, "Uptime"));
            dash["Memory"].Text = ((int)U.Num(o, "Memory")) + " MB";
            dash["Map"].Text = U.Str(o, "Map");
            dash["Game time"].Text = U.Str(o, "GameTime");
            dash["Network in/out"].Text = U.Num(o, "NetworkIn") + " / " + U.Num(o, "NetworkOut");
            dash["Version"].Text = U.Str(o, "Version") + (U.Str(o, "Protocol").Length > 0 ? " (" + U.Str(o, "Protocol") + ")" : "");
            dash["Save"].Text = U.Str(o, "SaveCreatedTime");
            spark.Max = Math.Max(1, max);
            spark.Values.Add(cur); if (spark.Values.Count > 120) spark.Values.RemoveAt(0);
            spark.Invalidate();
        }

        void ParsePlayers(string json)
        {
            var arr = U.Parse(json) as System.Collections.IEnumerable;
            if (arr == null) return;
            var seen = new HashSet<string>();
            foreach (object o in arr)
            {
                string id = U.Str(o, "SteamID"); if (id.Length == 0) continue;
                PlayerInfo p;
                if (!players.TryGetValue(id, out p)) { p = new PlayerInfo { Id = id }; players[id] = p; }
                p.Name = U.Str(o, "DisplayName"); p.Ping = (int)U.Num(o, "Ping");
                string ip = U.Str(o, "Address"); int c = ip.LastIndexOf(':'); p.Ip = c > 0 ? ip.Substring(0, c) : ip;
                p.ConnectedSeconds = (int)U.Num(o, "ConnectedSeconds"); p.Health = U.Num(o, "Health");
                seen.Add(id);
            }
            foreach (var k in players.Keys.ToList()) if (!seen.Contains(k)) players.Remove(k);
        }

        async Task UpdatePositions()
        {
            var list = players.Values.ToList();
            for (int i = 0; i < list.Count; i += 20)
            {
                var batch = list.Skip(i).Take(20).ToList();
                var tasks = batch.Select(p => rc.Cmd("printpos " + p.Id, 4000)).ToList();
                for (int j = 0; j < tasks.Count; j++)
                {
                    float x, y, z;
                    try
                    {
                        string r = await tasks[j];
                        if (U.TryPos(r, out x, out y, out z)) { batch[j].X = x; batch[j].Y = y; batch[j].Z = z; batch[j].HasPos = true; }
                        else batch[j].HasPos = false;
                    }
                    catch { }
                }
            }
        }

        void RefreshPlayerList()
        {
            string f = txtFilter.Text.Trim().ToLowerInvariant();
            var selected = new HashSet<string>(SelPlayers().Select(p => p.Id));
            lvPlayers.BeginUpdate();
            lvPlayers.Items.Clear();
            foreach (var p in players.Values)
            {
                if (f.Length > 0 && p.Name.ToLowerInvariant().IndexOf(f) < 0 && p.Id.IndexOf(f) < 0 && p.Ip.IndexOf(f) < 0) continue;
                var it = new ListViewItem(new string[] { p.Name, p.Id, p.Ping.ToString(), (p.ConnectedSeconds / 60).ToString(), p.Health.ToString("0"), p.GridRef, p.Ip });
                it.Tag = p; it.Selected = selected.Contains(p.Id);
                lvPlayers.Items.Add(it);
            }
            lvPlayers.Sort();
            lvPlayers.EndUpdate();
        }

        void RefreshMapList()
        {
            string sel = map.SelectedId;
            var list = players.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            map.Players = list;
            lbMapPlayers.BeginUpdate();
            lbMapPlayers.Items.Clear();
            foreach (var p in list) lbMapPlayers.Items.Add(p);
            for (int i = 0; i < list.Count; i++) if (list[i].Id == sel) { lbMapPlayers.SelectedIndex = i; break; }
            lbMapPlayers.EndUpdate();
            map.Invalidate();
        }

        // ---------------------------------------------------------- player actions
        List<PlayerInfo> SelPlayers()
        {
            var l = new List<PlayerInfo>();
            foreach (ListViewItem it in lvPlayers.SelectedItems) l.Add((PlayerInfo)it.Tag);
            return l;
        }

        async void ActKick()
        {
            var sel = SelPlayers(); if (sel.Count == 0) return;
            string reason = U.Ask("Kick", "Reason for kicking " + sel.Count + " player(s):", "Kicked by admin"); if (reason == null) return;
            foreach (var p in sel) await Run("kick " + U.Q(p.Id) + " " + U.Q(reason));
            await Poll();
        }
        async void ActBan()
        {
            var sel = SelPlayers(); if (sel.Count == 0) return;
            string reason = U.Ask("Ban", "Reason for banning " + sel.Count + " player(s):", "Banned by admin"); if (reason == null) return;
            if (!U.Confirm("Ban " + string.Join(", ", sel.Select(p => p.Name).ToArray()) + "?")) return;
            foreach (var p in sel) { await Run("banid " + p.Id + " " + U.Q(p.Name) + " " + U.Q(reason)); await Run("kick " + U.Q(p.Id) + " " + U.Q(reason)); }
            await Run("server.writecfg"); await RefreshBans(); await Poll();
        }
        async void ActGive()
        {
            var sel = SelPlayers(); if (sel.Count == 0) return;
            string item = U.Ask("Give item", "Item shortname (e.g. rifle.ak, wood, scrap):", ""); if (string.IsNullOrWhiteSpace(item)) return;
            string amt = U.Ask("Give item", "Amount:", "1"); if (string.IsNullOrWhiteSpace(amt)) return;
            foreach (var p in sel) await Run("inventory.giveto " + p.Id + " " + item.Trim() + " " + amt.Trim());
        }
        async void ActTeleport()
        {
            var sel = SelPlayers(); if (sel.Count == 0) return;
            string target = U.Ask("Teleport", "Teleport " + sel[0].Name + " to which player? (name or SteamID)", ""); if (string.IsNullOrWhiteSpace(target)) return;
            await Run("teleport " + U.Q(sel[0].Id) + " " + U.Q(target.Trim()));
        }
        async void ActRole(string cmd)
        {
            var sel = SelPlayers(); if (sel.Count == 0) return;
            if (!U.Confirm("Grant " + (cmd == "ownerid" ? "OWNER" : "moderator") + " to " + string.Join(", ", sel.Select(p => p.Name).ToArray()) + "?")) return;
            foreach (var p in sel) await Run(cmd + " " + p.Id + " " + U.Q(p.Name) + " " + U.Q("Added by Jets Rust Admin"));
            await Run("server.writecfg");
        }
        async void ActRemoveRole()
        {
            var sel = SelPlayers(); if (sel.Count == 0) return;
            foreach (var p in sel) { await Run("removemoderator " + p.Id); await Run("removeowner " + p.Id); }
            await Run("server.writecfg");
        }
        void ShowOnMap()
        {
            var sel = SelPlayers(); if (sel.Count == 0) return;
            map.SelectedId = sel[0].Id; tabs.SelectedTab = tabMap; RefreshMapList(); map.CenterOn(sel[0]);
        }

        // ---------------------------------------------------------- bans / vars / plugins
        async Task RefreshBans()
        {
            if (rc == null || !rc.Connected) return;
            try
            {
                string r = await rc.Cmd("banlistex");
                lvBans.Items.Clear();
                foreach (string line in r.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var m = Regex.Match(line, "(\\d{17})\\s+\"([^\"]*)\"\\s+\"([^\"]*)\"\\s*(-?\\d+)?");
                    if (m.Success)
                    {
                        string exp = m.Groups[4].Value;
                        lvBans.Items.Add(new ListViewItem(new string[] { m.Groups[1].Value, m.Groups[2].Value, (exp == "" || exp == "-1") ? "permanent" : exp, m.Groups[3].Value }));
                    }
                    else if (Regex.IsMatch(line, @"\d{17}"))
                    {
                        var id = Regex.Match(line, @"\d{17}").Value;
                        lvBans.Items.Add(new ListViewItem(new string[] { id, "", "", line.Trim() }));
                    }
                }
            }
            catch { }
        }

        async Task RefreshVars()
        {
            if (rc == null || !rc.Connected) return;
            foreach (ListViewItem it in lvVars.Items)
            {
                try { it.SubItems[1].Text = U.ConvarValue(await rc.Cmd(it.Text, 3000)); }
                catch { it.SubItems[1].Text = "(unavailable)"; }
            }
        }

        async Task RefreshPlugins()
        {
            if (rc == null || !rc.Connected) return;
            try
            {
                string r = await rc.Cmd("o.plugins");
                lvPlugins.Items.Clear();
                foreach (string line in r.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var m = Regex.Match(line, "\"([^\"]+)\"\\s*\\(([^)]*)\\)\\s*by\\s*(.+?)\\s*\\([^)]*\\)\\s*-\\s*(\\S+)");
                    if (m.Success) lvPlugins.Items.Add(new ListViewItem(new string[] { m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value }));
                }
            }
            catch { }
        }

        async Task PluginCmd(string verb)
        {
            if (lvPlugins.SelectedItems.Count == 0) return;
            foreach (ListViewItem it in lvPlugins.SelectedItems)
            {
                string file = it.SubItems[3].Text; if (file.Contains(".")) file = Path.GetFileNameWithoutExtension(file);
                await Run(verb + " " + file);
            }
            await RefreshPlugins();
        }

        async Task PermCmd(string verb, bool needPerm)
        {
            string target = txtPermTarget.Text.Trim(), perm = txtPermName.Text.Trim();
            if (target.Length == 0 || perm.Length == 0) return;
            await Run2(verb + " " + (rbGroup.Checked ? "group " : "user ") + target + " " + perm);
        }

        // ---------------------------------------------------------- RustMaps
        // Base URL can be overridden for testing: set JETS_RUSTMAPS_BASE.
        static string RmBase { get { string e = Environment.GetEnvironmentVariable("JETS_RUSTMAPS_BASE"); return string.IsNullOrEmpty(e) ? "https://api.rustmaps.com" : e.TrimEnd('/'); } }

        void SetRmKey()
        {
            string k = U.Ask("RustMaps API key", "Paste your RustMaps API key (stored encrypted for your Windows user):", "", true);
            if (k == null) return;
            cfg.RmKeyEnc = Cfg.Protect(k.Trim()); cfg.Save();
            AppendConsole(k.Trim().Length > 0 ? "RustMaps API key saved." : "RustMaps API key cleared.", Theme.Ok);
        }

        async Task FetchRustMaps(bool auto)
        {
            string key = Cfg.Unprotect(cfg.RmKeyEnc);
            if (key.Length == 0)
            {
                if (auto) return;
                SetRmKey(); key = Cfg.Unprotect(cfg.RmKeyEnc);
                if (key.Length == 0) return;
            }
            if (rc == null || !rc.Connected) { if (!auto) Dlg.Show("Connect to your server first - the seed and size come from it."); return; }
            string seed = "";
            try { seed = Regex.Match(await rc.Cmd("server.seed"), @"\d+").Value; } catch { }
            if (seed.Length == 0) { AppendConsole("RustMaps: could not read the server seed.", Theme.Warn); return; }
            int size = (int)U.WorldSize;
            string cache = Path.Combine(Cfg.MapDir, size + "_" + seed + ".png");
            if (File.Exists(cache))
            {
                LoadMapImage(cache, false); mapSource = size + "/" + seed;
                AppendConsole("Map loaded from cache (size " + size + ", seed " + seed + ").", Theme.Ok); return;
            }
            SetStatus("Fetching map from RustMaps (size " + size + ", seed " + seed + ")...");
            string err = null; byte[] png = null;
            try { png = await Task.Run(() => DownloadRustMap(key, size, seed)); }
            catch (Exception e) { err = e.Message; }
            if (err != null)
            {
                AppendConsole("RustMaps: " + err, Theme.DangerText);
                SetStatus("RustMaps fetch failed - see Console", true);
                if (!auto) Dlg.Show("Could not fetch the map from RustMaps:\n\n" + err);
                return;
            }
            try { Directory.CreateDirectory(Cfg.MapDir); File.WriteAllBytes(cache, png); } catch { }
            LoadMapImage(cache, false); mapSource = size + "/" + seed;
            AppendConsole("Map downloaded from RustMaps (size " + size + ", seed " + seed + ").", Theme.Ok);
            SetStatus("Map loaded");
        }

        static string HttpGetText(string url, Action<HttpWebRequest> setup)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "JetsRustAdmin/1.0"; req.Accept = "application/json"; req.Timeout = 20000;
            if (setup != null) setup(req);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static byte[] DownloadRustMap(string key, int size, string seed)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls; } catch { }
            string url = RmBase + "/v4/maps/" + size + "/" + seed;
            string json = null; WebException last = null;
            // The key goes in the X-API-Key header; fall back to a bearer token if that is rejected.
            Action<HttpWebRequest>[] attempts = new Action<HttpWebRequest>[] {
                r => r.Headers["X-API-Key"] = key,
                r => r.Headers["Authorization"] = "Bearer " + key };
            foreach (var setup in attempts)
            {
                try { json = HttpGetText(url, setup); break; }
                catch (WebException we)
                {
                    last = we;
                    var hr = we.Response as HttpWebResponse;
                    if (hr == null) throw new Exception("Could not reach RustMaps: " + we.Message);
                    int code = (int)hr.StatusCode;
                    if (code == 401 || code == 403) continue;
                    string body = ""; try { using (var sr = new StreamReader(hr.GetResponseStream())) body = sr.ReadToEnd(); } catch { }
                    if (code == 404) throw new Exception("RustMaps has no map for size " + size + " / seed " + seed + " yet (or it is a custom map). Generate it on rustmaps.com, then click Fetch again.");
                    if (code == 429) throw new Exception("RustMaps rate limit reached - wait a minute and try again.");
                    throw new Exception("RustMaps returned HTTP " + code + ". " + (body.Length > 200 ? body.Substring(0, 200) : body));
                }
            }
            if (json == null) throw new Exception("RustMaps rejected the API key (HTTP 401/403). Check the key with 'RustMaps key...' on the Live Map page.");
            object o = U.Parse(json);
            string img = U.FindStr(o, "rawImageUrl") ?? U.FindStr(o, "imageUrl");
            if (img == null)
            {
                string snippet = json.Length > 300 ? json.Substring(0, 300) : json;
                throw new Exception("RustMaps replied but no image URL was found. Reply began: " + snippet);
            }
            var req = (HttpWebRequest)WebRequest.Create(img);
            req.UserAgent = "JetsRustAdmin/1.0"; req.Timeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var st = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                st.CopyTo(ms);
                byte[] data = ms.ToArray();
                using (var test = new MemoryStream(data)) using (Image.FromStream(test)) { }   // throws if not an image
                return data;
            }
        }

        // ---------------------------------------------------------- map image
        void PickMapImage()
        {
            using (var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" })
                if (d.ShowDialog() == DialogResult.OK) LoadMapImage(d.FileName, true);
        }
        void LoadMapImage(string path, bool save)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var img = Image.FromStream(fs))
                    map.Bg = new Bitmap(img);
                if (save) { cfg.MapPath = path; cfg.Save(); }
                map.Invalidate();
            }
            catch (Exception e) { if (save) Dlg.Show("Could not load image: " + e.Message); }
        }
    }

    // Checks the GitHub repository's latest release and swaps in the new exe (same pattern as Jet's Server Messenger).
    static class Updater
    {
        // Releases still come from the existing JetsAdminPanel repo (GitHub redirects it if the repo is renamed later).
        public const string Repository = "pilotdave1970/JetsAdminPanel";
        public const string AssetName = "JetsRustAdmin.exe";

        public class Info { public Version Version; public string Url; public string Notes; }

        public static Version Current
        {
            get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        }

        static HttpWebRequest Req(string url)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls; } catch { }
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "JetsRustAdmin/" + Current; r.Timeout = 20000; r.ReadWriteTimeout = 60000; r.AllowAutoRedirect = true;
            return r;
        }

        // Returns null when already up to date. Throws on network errors.
        public static Info Check()
        {
            string json;
            using (var resp = Req("https://api.github.com/repos/" + Repository + "/releases/latest").GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = sr.ReadToEnd();
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("tag_name")) return null;
            Version latest;
            if (!Version.TryParse(Convert.ToString(root["tag_name"]).TrimStart('v', 'V'), out latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            if (latest <= Current) return null;
            var assets = root.ContainsKey("assets") ? root["assets"] as object[] : null;
            if (assets == null) return null;
            foreach (object o in assets)
            {
                var a = o as Dictionary<string, object>;
                if (a != null && string.Equals(Convert.ToString(a["name"]), AssetName, StringComparison.OrdinalIgnoreCase))
                    return new Info { Version = latest, Url = Convert.ToString(a["browser_download_url"]), Notes = root.ContainsKey("body") ? Convert.ToString(root["body"]) : "" };
            }
            return null;
        }

        // Downloads the new exe, renames the running one aside (Windows allows that), puts the new one in place and starts it.
        public static void Install(Info up)
        {
            string exe = Application.ExecutablePath, old = exe + ".old";
            string tmp = Path.Combine(Path.GetTempPath(), "JetsRustAdmin-" + Guid.NewGuid().ToString("N") + ".exe");
            using (var resp = Req(up.Url).GetResponse())
            using (var rs = resp.GetResponseStream())
            using (var fs = File.Create(tmp)) rs.CopyTo(fs);
            if (new FileInfo(tmp).Length < 100000) { try { File.Delete(tmp); } catch { } throw new IOException("The downloaded file looks incomplete."); }
            if (File.Exists(old)) { File.SetAttributes(old, FileAttributes.Normal); File.Delete(old); }
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
            catch { try { File.Delete(exe); File.Move(old, exe); } catch { } throw; }
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\JetsRustAdmin", true)) if (k != null) k.SetValue("DisplayVersion", up.Version.ToString()); } catch { }
        }

        public static void CleanUp() { try { string o = Application.ExecutablePath + ".old"; if (File.Exists(o)) File.Delete(o); } catch { } }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
