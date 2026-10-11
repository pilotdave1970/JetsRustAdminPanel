// Jet's Admin Panel for Rust - Windows desktop admin tool using WebRCON.
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

[assembly: AssemblyTitle("Jet's Admin Panel")]
[assembly: AssemblyProduct("Jet's Admin Panel")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace JetsAdminPanel
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

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetsAdminPanel"); } }
        static string FilePath { get { return Path.Combine(Dir, "config.json"); } }

        public static Cfg Load()
        {
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
                f.MaximizeBox = false; f.MinimizeBox = false; f.ClientSize = new Size(380, 110); f.ShowInTaskbar = false;
                var l = new Label { Text = label, Left = 12, Top = 12, Width = 356, Height = 32 };
                var t = new TextBox { Left = 12, Top = 48, Width = 356, Text = def ?? "", UseSystemPasswordChar = pw };
                var ok = new Button { Text = "OK", Left = 212, Top = 76, Width = 76, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 292, Top = 76, Width = 76, DialogResult = DialogResult.Cancel };
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
            payload["Identifier"] = id; payload["Message"] = cmd; payload["Name"] = "JetsAdminPanel";
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
                            if (cx > 0 && cx < Width)
                            {
                                string t = i < 26 ? ((char)('A' + i)).ToString() : "A" + (char)('A' + (i - 26) % 26);
                                var sz = g.MeasureString(t, f);
                                g.FillRectangle(bk, cx - sz.Width / 2, 0, sz.Width, sz.Height);
                                g.DrawString(t, f, br, cx - sz.Width / 2, 0);
                            }
                            float cy = oy + i * cell + cell / 2f;
                            if (cy > 0 && cy < Height)
                            {
                                string t = i.ToString();
                                var sz = g.MeasureString(t, f);
                                g.FillRectangle(bk, 0, cy - sz.Height / 2, sz.Width, sz.Height);
                                g.DrawString(t, f, br, 0, cy - sz.Height / 2);
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
                using (var path = Gfx.Round(new RectangleF(10, Height - 36, isz.Width + 12, 24), 6))
                using (var br = new SolidBrush(Color.FromArgb(190, 13, 17, 23))) g.FillPath(br, path);
                g.DrawString(info, f, Brushes.WhiteSmoke, 16, Height - 33);
            }
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
            using (var br = new SolidBrush(Color.FromArgb(38, Theme.Accent))) g.FillPolygon(br, area.ToArray());
            using (var pen = new Pen(Theme.Accent, 2f)) g.DrawLines(pen, pts.ToArray());
            var last = pts[pts.Count - 1];
            using (var br = new SolidBrush(Theme.Accent)) g.FillEllipse(br, last.X - 4, last.Y - 4, 8, 8);
        }
    }

    // ------------------------------------------------------------------ dark theme
    static class Gfx
    {
        public static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
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
        public static readonly Color Bg = Color.FromArgb(13, 17, 23);
        public static readonly Color Surface = Color.FromArgb(22, 27, 34);
        public static readonly Color Surface2 = Color.FromArgb(33, 40, 51);
        public static readonly Color Input = Color.FromArgb(10, 14, 19);
        public static readonly Color Border = Color.FromArgb(48, 56, 68);
        public static readonly Color Hover = Color.FromArgb(42, 51, 64);
        public static readonly Color Text = Color.FromArgb(230, 237, 243);
        public static readonly Color Muted = Color.FromArgb(139, 152, 168);
        public static readonly Color Accent = Color.FromArgb(47, 191, 143);
        public static readonly Color AccentHover = Color.FromArgb(80, 214, 168);
        public static readonly Color Danger = Color.FromArgb(229, 83, 75);
        public static readonly Color DangerText = Color.FromArgb(255, 123, 114);
        public static readonly Color Warn = Color.FromArgb(227, 179, 65);
        public static readonly Color Ok = Color.FromArgb(63, 185, 80);
        public static readonly Color Info = Color.FromArgb(88, 166, 255);
        public static readonly Color RowAlt = Color.FromArgb(18, 23, 30);
        public static readonly Color Select = Color.FromArgb(30, 78, 66);

        public static Font F(float size, FontStyle st) { return new Font("Segoe UI", size, st); }
        public static Font F(float size) { return new Font("Segoe UI", size, FontStyle.Regular); }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        static bool nativeOk = true;

        public static void DarkTitle(Form f)
        {
            if (!nativeOk) return;
            try { int v = 1; if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4); }
            catch { nativeOk = false; }
        }
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

        static readonly string[] DangerButtons = { "Kick", "Ban", "Quit now", "Unban selected", "Delete", "Unload selected", "Remove role", "Restart", "Disconnect", "Reload ALL" };
        static readonly string[] PrimaryButtons = { "Connect", "Install", "Fetch map from RustMaps", "Apply && save", "Send to all", "Save profile", "OK", "Save", "Yes", "Send as server" };

        public static void Apply(Control root)
        {
            Style(root);
            foreach (Control k in root.Controls) Apply(k);
        }

        static void Style(Control c)
        {
            Form f = c as Form;
            if (f != null) { f.BackColor = Bg; f.ForeColor = Text; return; }
            if (c is Page || c is Card || c is NavTabs || c is NavBar || c is MapControl || c is SparkPanel) return;

            Button b = c as Button;
            if (b != null)
            {
                string kind = Array.IndexOf(DangerButtons, b.Text) >= 0 ? "danger" : Array.IndexOf(PrimaryButtons, b.Text) >= 0 ? "primary" : "normal";
                StyleButton(b, kind); return;
            }
            RichTextBox rt = c as RichTextBox;
            if (rt != null) { rt.BackColor = Input; rt.ForeColor = Text; rt.BorderStyle = BorderStyle.None; DoubleBuffer(rt); NativeTheme(rt, "DarkMode_Explorer"); return; }
            TextBox tb = c as TextBox;
            if (tb != null) { tb.BackColor = Input; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.FixedSingle; if (tb.Multiline) NativeTheme(tb, "DarkMode_Explorer"); return; }
            ComboBox cb = c as ComboBox;
            if (cb != null) { cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Input; cb.ForeColor = Text; NativeTheme(cb, "DarkMode_CFD"); return; }
            NumericUpDown nu = c as NumericUpDown;
            if (nu != null) { nu.BackColor = Input; nu.ForeColor = Text; nu.BorderStyle = BorderStyle.FixedSingle; NativeTheme(nu, "DarkMode_Explorer"); return; }
            CheckBox ck = c as CheckBox;
            if (ck != null) { ck.FlatStyle = FlatStyle.Flat; ck.ForeColor = Text; ck.Cursor = Cursors.Hand; return; }
            RadioButton rb = c as RadioButton;
            if (rb != null) { rb.FlatStyle = FlatStyle.Flat; rb.ForeColor = Text; rb.Cursor = Cursors.Hand; return; }
            Label l = c as Label;
            if (l != null)
            {
                int a = l.ForeColor.ToArgb();
                if (a == Color.DimGray.ToArgb() || a == Color.Gray.ToArgb()) l.ForeColor = Muted;
                else if (a == Color.Firebrick.ToArgb()) l.ForeColor = DangerText;
                else if (a == SystemColors.ControlText.ToArgb() || a == Color.Black.ToArgb()) l.ForeColor = Text;
            }
        }

        public static void StyleButton(Button b, string kind)
        {
            b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false; b.Cursor = Cursors.Hand;
            b.MinimumSize = new Size(0, 30);
            var fa = b.FlatAppearance; fa.BorderSize = 1;
            if (kind == "primary")
            {
                b.BackColor = Accent; b.ForeColor = Color.FromArgb(6, 24, 18); fa.BorderColor = Accent;
                fa.MouseOverBackColor = AccentHover; fa.MouseDownBackColor = Accent;
            }
            else if (kind == "danger")
            {
                b.BackColor = Surface2; b.ForeColor = DangerText; fa.BorderColor = Color.FromArgb(104, 52, 52);
                fa.MouseOverBackColor = Color.FromArgb(66, 34, 38); fa.MouseDownBackColor = Color.FromArgb(90, 40, 44);
            }
            else
            {
                b.BackColor = Surface2; b.ForeColor = Text; fa.BorderColor = Border;
                fa.MouseOverBackColor = Hover; fa.MouseDownBackColor = Border;
            }
        }

        public static void StyleMenu(ContextMenuStrip m)
        {
            m.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
            m.BackColor = Surface2; m.ForeColor = Text; m.ShowImageMargin = false;
            foreach (ToolStripItem i in m.Items) i.ForeColor = Text;
        }

        public static void StyleListBox(ListBox lb)
        {
            lb.BorderStyle = BorderStyle.None; lb.BackColor = Surface; lb.ForeColor = Text;
            lb.DrawMode = DrawMode.OwnerDrawFixed; lb.ItemHeight = 28;
            lb.DrawItem += delegate (object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var br = new SolidBrush(sel ? Select : (e.Index % 2 == 1 ? RowAlt : Surface))) e.Graphics.FillRectangle(br, e.Bounds);
                TextRenderer.DrawText(e.Graphics, lb.GetItemText(lb.Items[e.Index]), lb.Font,
                    new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height), Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            DoubleBuffer(lb); NativeTheme(lb, "DarkMode_Explorer");
        }

        static void FitLast(ListView lv)
        {
            int n = lv.Columns.Count; if (n == 0) return;
            int sum = 0; for (int i = 0; i < n - 1; i++) sum += lv.Columns[i].Width;
            int w = lv.ClientSize.Width - sum - 24;
            if (w > 80 && lv.Columns[n - 1].Width != w) lv.Columns[n - 1].Width = w;
        }

        public static void StyleList(ListView lv)
        {
            lv.OwnerDraw = true; lv.GridLines = false; lv.BorderStyle = BorderStyle.None; lv.BackColor = Surface; lv.ForeColor = Text;
            lv.FullRowSelect = true; lv.HideSelection = false;
            lv.SmallImageList = new ImageList { ImageSize = new Size(1, 30) };
            DoubleBuffer(lv); NativeTheme(lv, "DarkMode_Explorer");
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            lv.DrawColumnHeader += delegate (object s, DrawListViewColumnHeaderEventArgs e)
            {
                using (var br = new SolidBrush(Surface2)) e.Graphics.FillRectangle(br, e.Bounds);
                using (var pen = new Pen(Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), F(8f, FontStyle.Bold),
                    new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), Muted, flags);
            };
            lv.DrawItem += delegate (object s, DrawListViewItemEventArgs e) { e.DrawDefault = false; };
            lv.DrawSubItem += delegate (object s, DrawListViewSubItemEventArgs e)
            {
                Rectangle r = e.Bounds;
                if (e.ColumnIndex == 0) r = new Rectangle(r.X, r.Y, lv.Columns[0].Width, r.Height);
                Color bg = e.Item.Selected ? Select : (e.ItemIndex % 2 == 1 ? RowAlt : Surface);
                using (var br = new SolidBrush(bg)) e.Graphics.FillRectangle(br, r);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font, new Rectangle(r.X + 10, r.Y, r.Width - 12, r.Height), Text, flags);
            };
            lv.Resize += delegate { FitLast(lv); };
            lv.ColumnWidthChanged += delegate { FitLast(lv); };
            lv.HandleCreated += delegate { FitLast(lv); };
        }
    }

    // A rounded, bordered panel with an optional small-caps title.
    class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface; ForeColor = Theme.Text; Padding = new Padding(12);
        }
        int TitleH { get { return string.IsNullOrEmpty(Text) ? 0 : 30; } }
        public override Rectangle DisplayRectangle
        {
            get { Rectangle r = base.DisplayRectangle; return new Rectangle(r.X, r.Y + TitleH, r.Width, Math.Max(0, r.Height - TitleH)); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Gfx.Round(r, 8))
            {
                using (var br = new SolidBrush(BackColor)) g.FillPath(br, path);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }
            if (TitleH > 0)
                TextRenderer.DrawText(g, Text.Replace("&&", "&").ToUpperInvariant(), Theme.F(8f, FontStyle.Bold), new Point(14, 10), Theme.Muted,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    // ------------------------------------------------------------------ sidebar navigation (replaces NavTabs)
    class Page : Panel
    {
        public Page(string text)
        {
            Text = text; Dock = DockStyle.Fill; Padding = new Padding(22, 18, 22, 16);
            BackColor = Theme.Bg; ForeColor = Theme.Text; Visible = false;
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

    class NavTabs : Panel
    {
        public readonly List<Page> Pages = new List<Page>();
        public readonly PageList TabPages;
        readonly NavBar bar;
        readonly Panel host;
        int sel = -1;
        public event EventHandler SelectedIndexChanged;

        public NavTabs()
        {
            TabPages = new PageList(this);
            host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            bar = new NavBar(this) { Dock = DockStyle.Left, Width = 208 };
            Controls.Add(host); Controls.Add(bar);
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
        const int ItemH = 40, Gap = 2, Top0 = 16;

        public NavBar(NavTabs o)
        {
            owner = o;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }
        Rectangle ItemRect(int i) { return new Rectangle(10, Top0 + i * (ItemH + Gap), Width - 21, ItemH); }
        int HitTest(Point p) { for (int i = 0; i < owner.Pages.Count; i++) if (ItemRect(i).Contains(p)) return i; return -1; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            for (int i = 0; i < owner.Pages.Count; i++)
            {
                var pg = owner.Pages[i]; var r = ItemRect(i);
                bool selected = i == owner.Selected, enabled = pg.Enabled;
                if (selected || (hover == i && enabled))
                    using (var path = Gfx.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), 7))
                    using (var br = new SolidBrush(selected ? Theme.Surface2 : Color.FromArgb(120, Theme.Hover))) g.FillPath(br, path);
                if (selected)
                    using (var br = new SolidBrush(Theme.Accent)) g.FillRectangle(br, r.X, r.Y + 10, 3, r.Height - 20);
                Color c = !enabled ? Color.FromArgb(86, 96, 110) : selected ? Theme.Text : (hover == i ? Theme.Text : Theme.Muted);
                TextRenderer.DrawText(g, pg.Text.Replace("&&", "&"), Theme.F(10f, selected ? FontStyle.Bold : FontStyle.Regular),
                    new Rectangle(r.X + 18, r.Y, r.Width - 20, r.Height), c,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            // connection chip at the bottom
            int y = Height - 46;
            using (var br = new SolidBrush(Online ? Theme.Ok : Theme.Danger)) g.FillEllipse(br, 22, y + 5, 10, 10);
            TextRenderer.DrawText(g, StatusText, Theme.F(9f), new Rectangle(40, y, Width - 48, 20), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
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
        public static DialogResult Show(string text) { return Show(text, "Jet's Admin Panel", false); }
        public static DialogResult Show(string text, string title, bool yesNo)
        {
            using (var f = new Form())
            {
                f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false; f.Font = Theme.F(9.5f);
                Size sz = TextRenderer.MeasureText(text, f.Font, new Size(420, 0), TextFormatFlags.WordBreak);
                var l = new Label { Text = text, Left = 22, Top = 22, Width = 420, Height = Math.Max(22, sz.Height + 4), AutoSize = false };
                f.ClientSize = new Size(464, 22 + l.Height + 28 + 36 + 18);
                int by = 22 + l.Height + 28;
                if (yesNo)
                {
                    var yes = new Button { Text = "Yes", Left = 270, Top = by, Width = 84, DialogResult = DialogResult.Yes };
                    var no = new Button { Text = "No", Left = 360, Top = by, Width = 84, DialogResult = DialogResult.No };
                    f.Controls.AddRange(new Control[] { l, yes, no }); f.AcceptButton = yes; f.CancelButton = no;
                }
                else
                {
                    var ok = new Button { Text = "OK", Left = 360, Top = by, Width = 84, DialogResult = DialogResult.OK };
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
            Text = "Jet's Admin Panel for Rust";
            Font = Theme.F(9.25f);
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Size = new Size(1280, 800);
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            U.WorldSize = cfg.WorldSize > 0 ? cfg.WorldSize : 4000f;

            BuildConnectionBar();
            tabs = new NavTabs { Dock = DockStyle.Fill };
            tabDash = new Page("Dashboard"); tabPlayers = new Page("Players"); tabMap = new Page("Live Map");
            tabConsole = new Page("Console"); tabChat = new Page("Chat"); tabBans = new Page("Bans");
            tabServer = new Page("Server"); tabSched = new Page("Restart Scheduler"); tabPlugins = new Page("Plugins & Permissions");
            tabs.TabPages.AddRange(new Page[] { tabDash, tabPlayers, tabMap, tabConsole, tabChat, tabBans, tabServer, tabSched, tabPlugins });
            Controls.Add(tabs);
            tabs.BringToFront();

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
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 1, 8, 1), Margin = new Padding(0, 0, 8, 6) };
            b.Click += click; return b;
        }
        static FlowLayoutPanel Bar()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, 0, 10), WrapContents = true };
        }
        static Label Lbl(string t) { return new Label { Text = t, AutoSize = true, Margin = new Padding(8, 8, 6, 0), ForeColor = Theme.Muted }; }
        static ListView MakeList(params string[] cols)
        {
            var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            foreach (var c in cols) lv.Columns.Add(c, 120);
            Theme.StyleList(lv);
            return lv;
        }

        void BuildConnectionBar()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 10, 16, 8), BackColor = Theme.Surface, WrapContents = true };
            bar.Paint += delegate (object sender, PaintEventArgs pe) { using (var pen = new Pen(Theme.Border)) pe.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
            var logo = new PictureBox { Size = new Size(30, 30), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 10, 0), BackColor = Theme.Surface };
            try { logo.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }
            var title = new Label { Text = "JETS ADMIN PANEL", AutoSize = true, Font = Theme.F(11f, FontStyle.Bold), Margin = new Padding(0, 5, 26, 0), ForeColor = Theme.Text };
            txtHost = new TextBox { Width = 122, Text = cfg.Host };
            txtPort = new TextBox { Width = 56, Text = cfg.Port.ToString() };
            txtPw = new TextBox { Width = 112, UseSystemPasswordChar = true, Text = Cfg.Unprotect(cfg.PwEnc) };
            chkRemember = new CheckBox { Text = "Remember", AutoSize = true, Checked = cfg.RememberPw, Margin = new Padding(10, 5, 4, 0) };
            chkAuto = new CheckBox { Text = "Auto-reconnect", AutoSize = true, Checked = cfg.AutoReconnect, Margin = new Padding(6, 5, 12, 0) };
            btnConnect = Btn("Connect", delegate { ToggleConnect(); });
            lblStatus = new Label { AutoSize = true, Text = "Not connected", ForeColor = Theme.Muted, Font = Theme.F(9f) };
            cmbProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 134, Margin = new Padding(0, 2, 6, 0) };
            cmbProfile.SelectedIndexChanged += delegate { ApplyProfile(); };
            foreach (var t in new TextBox[] { txtHost, txtPort, txtPw }) t.Margin = new Padding(0, 2, 6, 0);
            var saveBtn = Btn("Save", delegate { SaveProfile(); });
            var delBtn = Btn("Delete", delegate { DeleteProfile(); });
            saveBtn.Margin = new Padding(0, 0, 6, 0); delBtn.Margin = new Padding(0, 0, 16, 0); btnConnect.Margin = new Padding(0, 0, 0, 0);
            var chkStack = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            chkRemember.Margin = new Padding(0, 0, 0, 2); chkAuto.Margin = new Padding(0);
            chkStack.Controls.Add(chkRemember); chkStack.Controls.Add(chkAuto);
            btnUpdate = Btn("Updates", delegate { CheckForUpdates(true); });
            bar.Controls.AddRange(new Control[] { logo, title, cmbProfile, saveBtn, delBtn, Lbl("HOST"), txtHost, Lbl("PORT"), txtPort, Lbl("PASSWORD"), txtPw, btnConnect, chkStack });
            foreach (Control c in bar.Controls) { Label lb = c as Label; if (lb != null && lb != title) { lb.Font = Theme.F(8f, FontStyle.Bold); lb.Margin = new Padding(0, 8, 6, 0); } }
            btnConnect.Margin = new Padding(6, 0, 10, 0);
            Controls.Add(bar);
            var foot = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Theme.Surface };
            foot.Paint += delegate (object sender, PaintEventArgs pe) { using (var pen = new Pen(Theme.Border)) pe.Graphics.DrawLine(pen, 0, 0, foot.Width, 0); };
            lblStatus.Margin = new Padding(0); lblStatus.Location = new Point(16, 7);
            foot.Controls.Add(lblStatus);
            btnUpdate.Height = 24; btnUpdate.Width = 120; btnUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Right; btnUpdate.Location = new Point(foot.Width - 120 - 12, 3);
            btnUpdate.Text = "Updates v" + Updater.Current;
            foot.Controls.Add(btnUpdate);
            Controls.Add(foot);
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
            btnUpdate.Enabled = true; btnUpdate.Text = "Updates v" + Updater.Current; updating = false;
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
                        else { updating = false; btnUpdate.Enabled = true; btnUpdate.Text = "Updates v" + Updater.Current; Dlg.Show("Update failed:\n" + e2); }
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
            var tiles = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            string[] keys = { "Hostname", "Players", "Queue / Joining", "Server FPS", "Entities", "Uptime", "Memory", "Map", "Game time", "Network in/out", "Version", "Save" };
            foreach (var k in keys)
            {
                var tile = new Card { Width = k == "Hostname" ? 388 : 188, Height = 80, Margin = new Padding(0, 0, 12, 12), Padding = new Padding(0) };
                var lk = new Label { Text = k.ToUpperInvariant(), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.F(8f, FontStyle.Bold), Location = new Point(14, 12) };
                var v = new Label { Text = "-", AutoSize = false, AutoEllipsis = true, Font = Theme.F(13f, FontStyle.Bold), Location = new Point(12, 36), Size = new Size(tile.Width - 24, 34) };
                tile.Controls.Add(lk); tile.Controls.Add(v);
                dash[k] = v; tiles.Controls.Add(tile);
            }

            var sc = new Card { Text = "Players online", Dock = DockStyle.Fill, Padding = new Padding(10, 0, 12, 10) };
            spark = new SparkPanel { Dock = DockStyle.Fill };
            sc.Controls.Add(spark);

            var act = new Card { Text = "Quick actions", Dock = DockStyle.Bottom, Height = 142, Padding = new Padding(14, 0, 14, 10) };
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
            foreach (Control c in flow.Controls) { Label lb = c as Label; if (lb != null) { lb.Font = Theme.F(8f, FontStyle.Bold); lb.Margin = new Padding(8, 9, 6, 0); } }
            act.Controls.Add(flow);

            var holder = new Panel { Dock = DockStyle.Fill };
            holder.Controls.Add(sc); holder.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12 }); holder.Controls.Add(act); holder.Controls.Add(tiles);
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
            tabPlayers.Controls.Add(lvPlayers);
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
                f.Text = "Custom player actions"; f.StartPosition = FormStartPosition.CenterParent; f.ClientSize = new Size(560, 330);
                var l = new Label { Left = 10, Top = 8, Width = 540, Height = 54, Text = "One per line:  Label|command   Use {id} for the SteamID and {name} for the player name (add quotes yourself if needed). Examples: Kill|global.killplayer {id}   Heal|heal {id}. Commands not known to your server will show an error in the Console tab." };
                var t = new TextBox { Left = 10, Top = 66, Width = 540, Height = 220, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f), Text = string.Join("\r\n", cfg.Templates.ToArray()) };
                var ok = new Button { Text = "Save", Left = 394, Top = 294, Width = 76, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 474, Top = 294, Width = 76, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { l, t, ok, cancel }); f.AcceptButton = null; f.CancelButton = cancel; f.Font = Theme.F(9.5f); Theme.Apply(f); f.HandleCreated += delegate { Theme.DarkTitle(f); };
                if (f.ShowDialog() != DialogResult.OK) return;
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

            var side = new Panel { Dock = DockStyle.Right, Width = 284, Padding = new Padding(16, 0, 0, 0) };
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
            var opts = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            chkNames = new CheckBox { Text = "Show player names", AutoSize = true, Checked = cfg.ShowNames };
            chkNames.CheckedChanged += delegate { map.ShowNames = chkNames.Checked; cfg.ShowNames = chkNames.Checked; map.Invalidate(); };
            chkTrack = new CheckBox { Text = "Track positions (printpos)", AutoSize = true, Checked = cfg.TrackPos };
            chkTrack.CheckedChanged += delegate { cfg.TrackPos = chkTrack.Checked; };
            numWorld = new NumericUpDown { Minimum = 1000, Maximum = 8000, Increment = 250, Value = (decimal)U.WorldSize, Width = 80 };
            numWorld.ValueChanged += delegate { if (!settingWorld) SetWorldSize((float)numWorld.Value, true); };
            var wr = new FlowLayoutPanel { AutoSize = true }; wr.Controls.Add(Lbl("World size")); wr.Controls.Add(numWorld);
            numMargin = new NumericUpDown { Minimum = 0, Maximum = 2000, Increment = 25, Value = cfg.MapMargin, Width = 70 };
            map.Margin = cfg.MapMargin;
            numMargin.ValueChanged += delegate { map.Margin = (float)numMargin.Value; cfg.MapMargin = (int)numMargin.Value; map.Invalidate(); };
            var mr = new FlowLayoutPanel { AutoSize = true }; mr.Controls.Add(Lbl("Image edge margin (m)")); mr.Controls.Add(numMargin);
            lblMapNote = new Label { AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = Theme.Muted, Margin = new Padding(0, 8, 3, 10),
                Text = "The map is fetched from RustMaps using your server's seed and size, or load your own image. If dots look slightly off, adjust the image edge margin until a player lines up. Scroll to zoom, drag to pan, click a player to select." };
            opts.Controls.AddRange(new Control[] { chkNames, chkTrack, wr, mr,
                Btn("Fetch map from RustMaps", async delegate { await FetchRustMaps(false); }),
                Btn("Set RustMaps API key...", delegate { SetRmKey(); }),
                Btn("Load map image...", delegate { PickMapImage(); }),
                Btn("Clear map image", delegate { map.Bg = null; cfg.MapPath = ""; map.Invalidate(); }),
                Btn("Reset view", delegate { map.ResetView(); }),
                lblMapNote });
            side.Controls.Add(lbMapPlayers); side.Controls.Add(opts);

            tabMap.Controls.Add(map); tabMap.Controls.Add(side);
        }

        void BuildConsole()
        {
            rtbConsole = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Theme.Input, ForeColor = Theme.Text, Font = new Font("Consolas", 9.5f), WordWrap = true, HideSelection = false };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 10, 0, 0) };
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
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
            chkScroll = new CheckBox { Text = "Auto-scroll", Checked = true, AutoSize = true, Margin = new Padding(6, 6, 3, 0) };
            right.Controls.Add(chkScroll);
            right.Controls.Add(Btn("Clear", delegate { rtbConsole.Clear(); }));
            right.Controls.Add(Btn("Save log...", delegate
            {
                using (var d = new SaveFileDialog { Filter = "Text|*.txt", FileName = "rcon-log.txt" })
                    if (d.ShowDialog() == DialogResult.OK) File.WriteAllText(d.FileName, rtbConsole.Text);
            }));
            bottom.Controls.Add(txtCmd); bottom.Controls.Add(right);
            var chost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Input, Padding = new Padding(14, 10, 8, 8) };
            chost.Controls.Add(rtbConsole);
            tabConsole.Controls.Add(chost); tabConsole.Controls.Add(bottom);
        }

        void BuildChat()
        {
            rtbChat = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Segoe UI", 10.5f), HideSelection = false, BackColor = Theme.Input, ForeColor = Theme.Text };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 10, 0, 0) };
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
            send.Dock = DockStyle.Right;
            bottom.Controls.Add(txtChatSend); bottom.Controls.Add(send);
            var thost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Input, Padding = new Padding(16, 12, 10, 8) };
            thost.Controls.Add(rtbChat);
            tabChat.Controls.Add(thost); tabChat.Controls.Add(bottom);
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
            tabBans.Controls.Add(lvBans); tabBans.Controls.Add(bar);
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

            var ev = new Card { Text = "Environment && events", Dock = DockStyle.Right, Width = 310, Padding = new Padding(14, 0, 14, 12) };
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
            foreach (Control c in f.Controls) if (c is Button) { c.Width = 270; ((Button)c).AutoSize = false; c.Height = 32; }
            ev.Controls.Add(f);

            tabServer.Controls.Add(lvVars); tabServer.Controls.Add(ev); tabServer.Controls.Add(bar);
        }

        void BuildScheduler()
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14), AutoScroll = true };
            chkSchedOn = new CheckBox { Text = "Enable scheduled restarts from this panel (only runs while the panel is open and connected)", AutoSize = true, Checked = cfg.SchedEnabled };
            txtSchedTimes = new TextBox { Width = 420, Text = cfg.SchedTimes };
            txtSchedWarn = new TextBox { Width = 420, Text = cfg.SchedWarn };
            txtSchedMsg = new TextBox { Width = 420, Text = cfg.SchedMsg };
            txtSchedReason = new TextBox { Width = 420, Text = cfg.SchedReason };
            lblNext = new Label { AutoSize = true, Font = Theme.F(13f, FontStyle.Bold), ForeColor = Theme.Accent, Margin = new Padding(3, 14, 3, 4), Text = "Next restart: -" };
            lblUpcoming = new Label { AutoSize = true, ForeColor = Theme.Muted, Text = "" };
            p.Controls.Add(chkSchedOn);
            p.Controls.Add(Lbl("Restart times, daily, in server UTC (HH:mm, comma separated)")); p.Controls.Add(txtSchedTimes);
            p.Controls.Add(Lbl("Warn this many minutes before (comma separated)")); p.Controls.Add(txtSchedWarn);
            p.Controls.Add(Lbl("Warning message ({time} = e.g. \"5 minutes\")")); p.Controls.Add(txtSchedMsg);
            p.Controls.Add(Lbl("Restart reason (shown by the server countdown)")); p.Controls.Add(txtSchedReason);
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            row.Controls.Add(Btn("Apply && save", delegate { ApplySchedule(); }));
            row.Controls.Add(Btn("Send test warning", async delegate { await Run("say " + U.Q(txtSchedMsg.Text.Replace("{time}", "5 minutes") + " (test)")); }));
            row.Controls.Add(Btn("Reload JetsScheduledRestart plugin", async delegate { await Run("o.reload JetsScheduledRestart"); await RefreshPlugins(); }));
            p.Controls.Add(row);
            p.Controls.Add(lblNext); p.Controls.Add(lblUpcoming);
            p.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 14, 3, 3),
                Text = "Warnings are sent as server chat. At 60 seconds before the restart the panel runs the server's own 'restart' countdown. For restarts that must happen while no admin is online, keep using your server-side restarter plugin; this scheduler is for when the panel is left running. Don't run both for the same time or players will get duplicate warnings." });
            tabSched.Controls.Add(p);
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

            var perm = new Card { Text = "Oxide permissions", Dock = DockStyle.Bottom, Height = 270, Padding = new Padding(14, 0, 14, 12) };
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

            tabPlugins.Controls.Add(lvPlugins); tabPlugins.Controls.Add(perm); tabPlugins.Controls.Add(bar);
        }

        // ---------------------------------------------------------- connection
        void SetStatus(string s, bool bad = false) { lblStatus.Text = s; lblStatus.ForeColor = bad ? Theme.DangerText : Theme.Muted; }

        void SetConnectedUi(bool on)
        {
            btnConnect.Text = on ? "Disconnect" : "Connect"; Theme.StyleButton(btnConnect, on ? "danger" : "primary"); tabs.SetStatus(on, on ? "Connected" : "Offline");
            txtHost.Enabled = txtPort.Enabled = txtPw.Enabled = !on;
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
            foreach (var p in sel) await Run(cmd + " " + p.Id + " " + U.Q(p.Name) + " " + U.Q("Added by Jets Admin Panel"));
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
            req.UserAgent = "JetsAdminPanel/1.0"; req.Accept = "application/json"; req.Timeout = 20000;
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
            if (json == null) throw new Exception("RustMaps rejected the API key (HTTP 401/403). Check the key with 'Set RustMaps API key...'.");
            object o = U.Parse(json);
            string img = U.FindStr(o, "rawImageUrl") ?? U.FindStr(o, "imageUrl");
            if (img == null)
            {
                string snippet = json.Length > 300 ? json.Substring(0, 300) : json;
                throw new Exception("RustMaps replied but no image URL was found. Reply began: " + snippet);
            }
            var req = (HttpWebRequest)WebRequest.Create(img);
            req.UserAgent = "JetsAdminPanel/1.0"; req.Timeout = 60000;
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
        public const string Repository = "pilotdave1970/JetsAdminPanel";
        public const string AssetName = "JetsAdminPanel.exe";

        public class Info { public Version Version; public string Url; public string Notes; }

        public static Version Current
        {
            get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        }

        static HttpWebRequest Req(string url)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls; } catch { }
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "JetsAdminPanel/" + Current; r.Timeout = 20000; r.ReadWriteTimeout = 60000; r.AllowAutoRedirect = true;
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
            string tmp = Path.Combine(Path.GetTempPath(), "JetsAdminPanel-" + Guid.NewGuid().ToString("N") + ".exe");
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
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\JetsAdminPanel", true)) if (k != null) k.SetValue("DisplayVersion", up.Version.ToString()); } catch { }
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
