// ツイキャス録音君 - C# 5 / WinForms (.NET Framework 4.x) 実装
// パスワード保護配信 + メンバーシップ限定配信 対応
// Compile:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
//     /target:winexe /out:ツイキャス録音君.exe
//     /reference:System.Windows.Forms.dll /reference:System.Drawing.dll
//     /reference:System.Web.Extensions.dll /reference:System.Net.dll
//     /utf8output /optimize+ ツイキャス録音君.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

[assembly: System.Runtime.InteropServices.ComVisible(false)]

namespace TwitCasRecorder
{
    // ============================================================
    // 設定クラス
    // ============================================================
    class StreamerEntry
    {
        public string UserId { get; set; }
        public string DisplayName { get; set; }
        public string Password { get; set; }
        public bool Enabled { get; set; }

        public StreamerEntry()
        {
            UserId = "";
            DisplayName = "";
            Password = "";
            Enabled = true;
        }
    }

    class AppConfig
    {
        public List<StreamerEntry> Streamers { get; set; }
        public string AccountUsername { get; set; }
        public string OutputDir { get; set; }
        public int CheckInterval { get; set; }
        public string YtdlpPath { get; set; }
        public string FfmpegPath { get; set; }

        public AppConfig()
        {
            Streamers = new List<StreamerEntry>();
            AccountUsername = "";
            OutputDir = Path.Combine(AppDir(), "recordings");
            CheckInterval = 30;
            YtdlpPath = "yt-dlp";
            FfmpegPath = "ffmpeg";
        }

        public static string AppDir()
        {
            return Path.GetDirectoryName(Application.ExecutablePath);
        }
    }

    // ============================================================
    // 認証クラス
    // ============================================================
    class TwitCastingAuth
    {
        private readonly CookieContainer _cookies = new CookieContainer();
        private static readonly Uri TcUri = new Uri("https://twitcasting.tv/");
        public bool IsLoggedIn { get; private set; }

        // TC アカウントでログイン
        public Tuple<bool, string> LoginTcAccount(string username, string password)
        {
            try
            {
                string html = GetPage("https://twitcasting.tv/tc_login.php");
                string csrf = ExtractBetween(html, "csrf_token", "value=\"", "\"");

                string post = "username=" + Uri.EscapeDataString(username)
                            + "&password=" + Uri.EscapeDataString(password)
                            + "&csrf_token=" + Uri.EscapeDataString(csrf)
                            + "&mode=login";
                PostPage("https://twitcasting.tv/tc_login.php", post,
                         "https://twitcasting.tv/tc_login.php");

                if (HasAuthCookie())
                {
                    IsLoggedIn = true;
                    PersistCookies();
                    return new Tuple<bool, string>(true, "ログイン成功");
                }
                return new Tuple<bool, string>(false,
                    "ログイン失敗 (ユーザー名/パスワードを確認してください)");
            }
            catch (Exception ex)
            {
                return new Tuple<bool, string>(false, "通信エラー: " + ex.Message);
            }
        }

        // ブラウザ Cookie 文字列をセット
        public void SetCookiesFromString(string cookieStr)
        {
            foreach (string part in cookieStr.Split(';'))
            {
                string p = part.Trim();
                int eq = p.IndexOf('=');
                if (eq > 0)
                {
                    string name = p.Substring(0, eq).Trim();
                    string val  = p.Substring(eq + 1).Trim();
                    try { _cookies.Add(new Cookie(name, val, "/", ".twitcasting.tv")); }
                    catch { /* 不正クッキーは無視 */ }
                }
            }
            IsLoggedIn = HasAuthCookie();
            if (IsLoggedIn) PersistCookies();
        }

        // 保存済みクッキー読込
        public bool LoadCookies()
        {
            string path = CookieJsonPath();
            if (!File.Exists(path)) return false;
            try
            {
                var ser  = new JavaScriptSerializer();
                string json = File.ReadAllText(path, Encoding.UTF8);
                var list = ser.Deserialize<List<Dictionary<string, object>>>(json);
                foreach (var c in list)
                {
                    string name   = DictStr(c, "name");
                    string val    = DictStr(c, "value");
                    string domain = DictStr(c, "domain");
                    if (domain == "") domain = ".twitcasting.tv";
                    if (name != "")
                        try { _cookies.Add(new Cookie(name, val, "/", domain)); }
                        catch { }
                }
                IsLoggedIn = HasAuthCookie();
                if (IsLoggedIn) WriteNetscapeCookies();
                return IsLoggedIn;
            }
            catch { return false; }
        }

        // パスワード配信の解錠
        public bool UnlockPasswordStream(string movieId, string password)
        {
            try
            {
                string post = "movie_id=" + Uri.EscapeDataString(movieId)
                            + "&password=" + Uri.EscapeDataString(password);
                PostPage("https://twitcasting.tv/checkpassword.php", post,
                         "https://twitcasting.tv/");
                return true;
            }
            catch { return false; }
        }

        // yt-dlp 向け Netscape Cookie パス
        public string NetscapeCookiePath()
        {
            return Path.Combine(AppConfig.AppDir(), "cookies.txt");
        }

        // 認証済み HttpWebRequest を作成
        public HttpWebRequest CreateAuthRequest(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.CookieContainer = _cookies;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                          + "AppleWebKit/537.36 (KHTML, like Gecko) "
                          + "Chrome/122.0.0.0 Safari/537.36";
            req.Accept = "application/json, text/html, */*";
            req.Headers["Accept-Language"] = "ja,en-US;q=0.9";
            req.AllowAutoRedirect = true;
            return req;
        }

        // ---- private ----

        private bool HasAuthCookie()
        {
            CookieCollection col = _cookies.GetCookies(TcUri);
            foreach (Cookie c in col)
                if (c.Name == "tc_ss" || c.Name == "tc_id") return true;
            return false;
        }

        private void PersistCookies()
        {
            // JSON 保存
            var list = new List<Dictionary<string, object>>();
            foreach (Cookie c in _cookies.GetCookies(TcUri))
            {
                var d = new Dictionary<string, object>();
                d.Add("name",   c.Name);
                d.Add("value",  c.Value);
                d.Add("domain", c.Domain);
                d.Add("path",   c.Path);
                d.Add("secure", c.Secure);
                list.Add(d);
            }
            File.WriteAllText(CookieJsonPath(),
                new JavaScriptSerializer().Serialize(list), Encoding.UTF8);
            WriteNetscapeCookies();
        }

        private void WriteNetscapeCookies()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            foreach (Cookie c in _cookies.GetCookies(TcUri))
            {
                string domain = c.Domain.StartsWith(".") ? c.Domain : "." + c.Domain;
                sb.AppendLine(domain + "\tTRUE\t" + c.Path + "\t"
                    + (c.Secure ? "TRUE" : "FALSE") + "\t0\t" + c.Name + "\t" + c.Value);
            }
            File.WriteAllText(NetscapeCookiePath(), sb.ToString(), Encoding.UTF8);
        }

        private string GetPage(string url)
        {
            var req = CreateAuthRequest(url);
            req.Method = "GET";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private void PostPage(string url, string postData, string referer)
        {
            var req = CreateAuthRequest(url);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Referer = referer;
            byte[] bytes = Encoding.UTF8.GetBytes(postData);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                sr.ReadToEnd(); // consume
        }

        private static string ExtractBetween(string html, string anchor,
            string startMark, string endMark)
        {
            int a = html.IndexOf(anchor, StringComparison.Ordinal);
            if (a < 0) return "";
            int s = html.IndexOf(startMark, a, StringComparison.Ordinal);
            if (s < 0) return "";
            s += startMark.Length;
            int e = html.IndexOf(endMark, s, StringComparison.Ordinal);
            return e < 0 ? "" : html.Substring(s, e - s);
        }

        // Dictionary から文字列を安全に取得
        private static string DictStr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) return v.ToString();
            return "";
        }

        private static string CookieJsonPath()
        {
            return Path.Combine(AppConfig.AppDir(), "cookies.json");
        }
    }

    // ============================================================
    // 配信監視クラス
    // ============================================================
    class LiveInfo
    {
        public bool IsOnLive { get; set; }
        public string MovieId { get; set; }
        public bool IsProtected { get; set; }
        public LiveInfo() { MovieId = ""; }
    }

    class StreamMonitor
    {
        private readonly TwitCastingAuth _auth;
        public StreamMonitor(TwitCastingAuth auth) { _auth = auth; }

        public LiveInfo CheckLive(string userId)
        {
            var info = new LiveInfo();
            try
            {
                var req = _auth.CreateAuthRequest(
                    "https://frontendapi.twitcasting.tv/users/" + userId + "/latest-movie");
                req.Method = "GET";
                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = sr.ReadToEnd();

                var ser = new JavaScriptSerializer();
                var data = ser.Deserialize<Dictionary<string, object>>(json);

                object livVal;
                if (data.TryGetValue("is_on_live", out livVal) && livVal is bool)
                    info.IsOnLive = (bool)livVal;

                object movieVal;
                if (data.TryGetValue("movie", out movieVal))
                {
                    var movie = movieVal as Dictionary<string, object>;
                    if (movie != null)
                    {
                        object idVal;
                        if (movie.TryGetValue("id", out idVal) && idVal != null)
                            info.MovieId = idVal.ToString();

                        object protVal;
                        if (movie.TryGetValue("is_protected", out protVal) && protVal is bool)
                            info.IsProtected = (bool)protVal;
                    }
                }
            }
            catch { }
            return info;
        }
    }

    // ============================================================
    // 録音クラス
    // ============================================================
    class StreamRecorder
    {
        private readonly AppConfig _config;
        private readonly TwitCastingAuth _auth;
        private readonly Action<string> _log;
        private readonly Dictionary<string, Process> _active =
            new Dictionary<string, Process>();
        private readonly object _lock = new object();

        public StreamRecorder(AppConfig config, TwitCastingAuth auth, Action<string> log)
        {
            _config = config;
            _auth   = auth;
            _log    = log;
            Directory.CreateDirectory(config.OutputDir);
        }

        public bool IsRecording(string userId)
        {
            lock (_lock)
            {
                if (!_active.ContainsKey(userId)) return false;
                if (_active[userId].HasExited) { _active.Remove(userId); return false; }
                return true;
            }
        }

        public void StartRecording(string userId, string movieId, string password)
        {
            lock (_lock) { if (IsRecording(userId)) return; }
            var t = new Thread(delegate() { Record(userId, movieId, password); });
            t.IsBackground = true;
            t.Start();
        }

        private void Record(string userId, string movieId, string password)
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outPath = Path.Combine(_config.OutputDir,
                userId + "_" + movieId + "_" + ts + ".%(ext)s");
            string url = "https://twitcasting.tv/" + userId;

            var sb = new StringBuilder();
            sb.Append("--no-playlist --no-part ");
            sb.Append("-x --audio-format aac --audio-quality 0 ");
            sb.Append("-o \"" + outPath + "\" ");
            if (_auth.IsLoggedIn && File.Exists(_auth.NetscapeCookiePath()))
                sb.Append("--cookies \"" + _auth.NetscapeCookiePath() + "\" ");
            if (!string.IsNullOrEmpty(password))
                sb.Append("--video-password \"" + password + "\" ");
            sb.Append("\"" + url + "\"");

            _log("[録音開始] " + userId);

            var psi = new ProcessStartInfo();
            psi.FileName               = _config.YtdlpPath;
            psi.Arguments              = sb.ToString();
            psi.UseShellExecute        = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            psi.CreateNoWindow         = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding  = Encoding.UTF8;

            try
            {
                var proc = Process.Start(psi);
                lock (_lock) { _active[userId] = proc; }

                proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data) && !e.Data.StartsWith("[debug]"))
                        _log("  [" + userId + "] " + e.Data);
                };
                proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data) && !e.Data.StartsWith("[debug]"))
                        _log("  [" + userId + "] " + e.Data);
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                lock (_lock) { _active.Remove(userId); }
                if (proc.ExitCode == 0)
                    _log("[録音完了] " + userId);
                else
                    _log("[録音終了] " + userId + "  (code:" + proc.ExitCode + ")");
            }
            catch (Exception ex)
            {
                lock (_lock) { _active.Remove(userId); }
                string msg = ex.Message;
                bool notFound = msg.Contains("ファイルが見つかりません")
                             || msg.Contains("cannot find")
                             || msg.Contains("No such file");
                if (notFound)
                    _log("[エラー] yt-dlp が見つかりません。pip install yt-dlp を実行してください。");
                else
                    _log("[エラー] " + userId + ": " + msg);
            }
        }

        public void StopRecording(string userId)
        {
            Process proc = null;
            lock (_lock) { _active.TryGetValue(userId, out proc); }
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                lock (_lock) { _active.Remove(userId); }
                _log("[停止] " + userId + " の録音を停止しました");
            }
        }

        public void StopAll()
        {
            string[] ids;
            lock (_lock)
            {
                ids = new string[_active.Count];
                _active.Keys.CopyTo(ids, 0);
            }
            foreach (string id in ids) StopRecording(id);
        }
    }

    // ============================================================
    // 配信者 追加/編集 ダイアログ
    // ============================================================
    class StreamerDialog : Form
    {
        public StreamerEntry Result { get; private set; }

        private TextBox _tbUserId, _tbDisplayName, _tbPassword;
        private CheckBox _cbEnabled;

        public StreamerDialog(StreamerEntry entry)
        {
            Text = "配信者設定";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(510, 235);
            Font = new Font("Meiryo UI", 9f);

            int lx = 15, lw = 155, ex = 175, ew = 200;
            int y = 20;

            MkLabel("ユーザーID  *", lx, y, lw);
            _tbUserId = MkTextBox(ex, y, ew);
            MkLabel("twitcasting.tv/ の後ろの部分", ex + ew + 8, y + 3, 130, Color.Gray);
            y += 38;

            MkLabel("表示名", lx, y, lw);
            _tbDisplayName = MkTextBox(ex, y, ew);
            MkLabel("省略可", ex + ew + 8, y + 3, 60, Color.Gray);
            y += 38;

            MkLabel("合言葉 / パスワード", lx, y, lw);
            _tbPassword = MkTextBox(ex, y, ew);
            MkLabel("不要なら空欄", ex + ew + 8, y + 3, 100, Color.Gray);
            y += 38;

            _cbEnabled = new CheckBox();
            _cbEnabled.Text     = "監視を有効にする";
            _cbEnabled.Location = new Point(ex, y);
            _cbEnabled.AutoSize = true;
            _cbEnabled.Checked  = true;
            Controls.Add(_cbEnabled);
            y += 38;

            var btnOk     = new Button { Text = "OK",        Location = new Point(ex, y),      Width = 80,  Parent = this };
            var btnCancel = new Button { Text = "キャンセル", Location = new Point(ex + 90, y), Width = 90, Parent = this };
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            if (entry != null)
            {
                _tbUserId.Text      = entry.UserId;
                _tbDisplayName.Text = entry.DisplayName;
                _tbPassword.Text    = entry.Password;
                _cbEnabled.Checked  = entry.Enabled;
            }

            btnOk.Click     += OnOk;
            btnCancel.Click += delegate(object s, EventArgs e) { DialogResult = DialogResult.Cancel; };
        }

        private void OnOk(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_tbUserId.Text))
            { MessageBox.Show("ユーザーIDは必須です", "入力エラー"); return; }
            string uid = _tbUserId.Text.Trim();
            string dn  = _tbDisplayName.Text.Trim();
            Result = new StreamerEntry
            {
                UserId      = uid,
                DisplayName = string.IsNullOrEmpty(dn) ? uid : dn,
                Password    = _tbPassword.Text.Trim(),
                Enabled     = _cbEnabled.Checked,
            };
            DialogResult = DialogResult.OK;
        }

        private Label MkLabel(string text, int x, int y, int w, Color? color = null)
        {
            var lbl = new Label { Text = text, Location = new Point(x, y), Width = w, Parent = this };
            if (color.HasValue) lbl.ForeColor = color.Value;
            return lbl;
        }

        private TextBox MkTextBox(int x, int y, int w)
        {
            return new TextBox { Location = new Point(x, y), Width = w, Parent = this };
        }
    }

    // ============================================================
    // メインフォーム
    // ============================================================
    class MainForm : Form
    {
        private AppConfig _config;
        private TwitCastingAuth _auth;
        private StreamMonitor _monitor;
        private StreamRecorder _recorder;
        private bool _monitoring;

        private ListView _lv;
        private TextBox _tbTcUser, _tbTcPass, _tbCookies;
        private TextBox _tbOutputDir, _tbInterval, _tbYtdlp, _tbFfmpeg;
        private Label _lblLoginStatus, _lblMonitorStatus;
        private Button _btnStart, _btnStop;
        private RichTextBox _rtbLog;

        public MainForm()
        {
            Text = "ツイキャス録音君";
            ClientSize = new Size(830, 650);
            MinimumSize = new Size(640, 540);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Meiryo UI", 9f);

            _config   = ConfigManager.Load();
            _auth     = new TwitCastingAuth();
            _monitor  = new StreamMonitor(_auth);
            _recorder = new StreamRecorder(_config, _auth, Log);

            BuildUI();

            if (_auth.LoadCookies())
            {
                Log("保存済みクッキーを読み込みました (ログイン済み)");
                SetLoginLabel("ログイン済み", Color.Green);
            }

            FormClosing += OnFormClosing;
        }

        // ---- UI 構築 ----

        private void BuildUI()
        {
            var bottom = new Panel { Height = 195, Dock = DockStyle.Bottom, Parent = this };

            var tab = new TabControl { Dock = DockStyle.Fill, Parent = this };
            tab.Padding = new Point(10, 4);

            var p1 = new TabPage("  配信者設定  ");
            tab.TabPages.Add(p1);
            BuildStreamersPage(p1);

            var p2 = new TabPage("  アカウント設定  ");
            tab.TabPages.Add(p2);
            BuildAccountPage(p2);

            var p3 = new TabPage("  詳細設定  ");
            tab.TabPages.Add(p3);
            BuildSettingsPage(p3);

            BuildBottomPanel(bottom);
        }

        // ---- 配信者設定 ----

        private void BuildStreamersPage(TabPage page)
        {
            var btnPanel = new Panel { Width = 85, Dock = DockStyle.Right, Parent = page };

            _lv = new ListView { View = View.Details, FullRowSelect = true,
                GridLines = true, Dock = DockStyle.Fill, Parent = page };
            _lv.Columns.Add("ユーザーID", 160);
            _lv.Columns.Add("表示名",     160);
            _lv.Columns.Add("合言葉/PW",   90);
            _lv.Columns.Add("状態",        90);

            MkBtn("追加", 0, btnPanel).Click += OnAddStreamer;
            MkBtn("編集", 1, btnPanel).Click += OnEditStreamer;
            MkBtn("削除", 2, btnPanel).Click += OnDeleteStreamer;

            RefreshLv();
        }

        private void OnAddStreamer(object s, EventArgs e)
        {
            using (var d = new StreamerDialog(null))
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    _config.Streamers.Add(d.Result);
                    ConfigManager.Save(_config);
                    RefreshLv();
                }
            }
        }

        private void OnEditStreamer(object s, EventArgs e)
        {
            if (_lv.SelectedIndices.Count == 0)
            { MessageBox.Show("編集する配信者を選択してください"); return; }
            int idx = _lv.SelectedIndices[0];
            using (var d = new StreamerDialog(_config.Streamers[idx]))
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    _config.Streamers[idx] = d.Result;
                    ConfigManager.Save(_config);
                    RefreshLv();
                }
            }
        }

        private void OnDeleteStreamer(object s, EventArgs e)
        {
            if (_lv.SelectedIndices.Count == 0)
            { MessageBox.Show("削除する配信者を選択してください"); return; }
            if (MessageBox.Show("削除しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                _config.Streamers.RemoveAt(_lv.SelectedIndices[0]);
                ConfigManager.Save(_config);
                RefreshLv();
            }
        }

        // ---- アカウント設定 ----

        private void BuildAccountPage(TabPage page)
        {
            int y = 12;

            var grp1 = new GroupBox();
            grp1.Text     = "TwitCasting アカウントでログイン  (メンバーシップ限定配信向け)";
            grp1.Location = new Point(10, y);
            grp1.Height   = 155;
            grp1.Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(grp1);
            page.Resize += delegate(object s, EventArgs e) { grp1.Width = page.Width - 20; };
            grp1.Width = page.Width - 20;

            MkFormLabel(grp1, "ユーザー名 / メール:", 25, 175);
            _tbTcUser = MkFormEntry(grp1, 25, 175, 220, false);
            _tbTcUser.Text = _config.AccountUsername;
            MkFormLabel(grp1, "パスワード:", 58, 175);
            _tbTcPass = MkFormEntry(grp1, 58, 175, 220, true);

            var btnLogin = new Button { Text = "ログイン", Location = new Point(175, 90), Width = 85, Parent = grp1 };
            _lblLoginStatus = new Label { Location = new Point(270, 93), Width = 250, Parent = grp1 };
            btnLogin.Click += OnLogin;

            y += 170;

            var grp2 = new GroupBox();
            grp2.Text     = "ブラウザ Cookie を直接貼り付ける  (ログインが失敗する場合の代替手段)";
            grp2.Location = new Point(10, y);
            grp2.Height   = 190;
            grp2.Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(grp2);
            page.Resize += delegate(object s, EventArgs e) { grp2.Width = page.Width - 20; };
            grp2.Width = page.Width - 20;

            var helpLbl = new Label
            {
                Text = "① ブラウザで twitcasting.tv にログイン\r\n"
                     + "② F12 → アプリケーション → Cookie → twitcasting.tv を開く\r\n"
                     + "③ tc_ss や tc_id などを「名前=値; 名前=値;」形式でコピーして貼り付け",
                Location = new Point(10, 22), Size = new Size(grp2.Width - 20, 60),
                ForeColor = Color.Gray, Parent = grp2
            };
            grp2.Resize += delegate(object s, EventArgs e)
            {
                helpLbl.Width = grp2.Width - 20;
                _tbCookies.Width = grp2.Width - 20;
            };

            _tbCookies = new TextBox
            {
                Location = new Point(10, 88), Width = grp2.Width - 20,
                Height = 55, Multiline = true, ScrollBars = ScrollBars.Vertical, Parent = grp2
            };

            var btnCk = new Button { Text = "Cookie をセット", Location = new Point(10, 152), Width = 115, Parent = grp2 };
            btnCk.Click += OnSetCookies;
        }

        private void OnLogin(object sender, EventArgs e)
        {
            string u = _tbTcUser.Text.Trim();
            string p = _tbTcPass.Text.Trim();
            if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p))
            { MessageBox.Show("ユーザー名とパスワードを入力してください"); return; }
            SetLoginLabel("ログイン中...", Color.Blue);
            var t = new Thread(delegate()
            {
                var res = _auth.LoginTcAccount(u, p);
                if (res.Item1)
                {
                    _config.AccountUsername = u;
                    ConfigManager.Save(_config);
                }
                Invoke(new Action(delegate()
                {
                    SetLoginLabel(res.Item2, res.Item1 ? Color.Green : Color.Red);
                    Log(res.Item2);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OnSetCookies(object sender, EventArgs e)
        {
            string ck = _tbCookies.Text.Trim();
            if (string.IsNullOrEmpty(ck)) { MessageBox.Show("Cookie 文字列を入力してください"); return; }
            _auth.SetCookiesFromString(ck);
            if (_auth.IsLoggedIn)
            { SetLoginLabel("Cookie をセット済み (ログイン済み)", Color.Green); Log("ブラウザ Cookie をセットしました"); }
            else
                SetLoginLabel("認証 Cookie が見つかりません", Color.Red);
        }

        // ---- 詳細設定 ----

        private void BuildSettingsPage(TabPage page)
        {
            int y = 20;

            MkFormLabel(page, "録音ファイル保存先:", y, 175);
            _tbOutputDir = MkFormEntry(page, y, 175, 340, false);
            _tbOutputDir.Text = _config.OutputDir;
            var btnBrowse = new Button { Text = "参照...", Location = new Point(525, y), Width = 65, Parent = page };
            btnBrowse.Click += delegate(object s, EventArgs e)
            {
                using (var d = new FolderBrowserDialog { SelectedPath = _tbOutputDir.Text })
                    if (d.ShowDialog() == DialogResult.OK) _tbOutputDir.Text = d.SelectedPath;
            };
            y += 35;

            MkFormLabel(page, "チェック間隔 (秒):", y, 175);
            _tbInterval = MkFormEntry(page, y, 175, 80, false);
            _tbInterval.Text = _config.CheckInterval.ToString();
            y += 35;

            MkFormLabel(page, "yt-dlp パス:", y, 175);
            _tbYtdlp = MkFormEntry(page, y, 175, 340, false);
            _tbYtdlp.Text = _config.YtdlpPath;
            y += 35;

            MkFormLabel(page, "ffmpeg パス:", y, 175);
            _tbFfmpeg = MkFormEntry(page, y, 175, 340, false);
            _tbFfmpeg.Text = _config.FfmpegPath;
            y += 40;

            var btnSave = new Button { Text = "設定を保存", Location = new Point(175, y), Width = 100, Parent = page };
            btnSave.Click += OnSaveSettings;
        }

        private void OnSaveSettings(object sender, EventArgs e)
        {
            _config.OutputDir = _tbOutputDir.Text.Trim();
            int iv;
            if (int.TryParse(_tbInterval.Text, out iv) && iv > 0) _config.CheckInterval = iv;
            _config.YtdlpPath = _tbYtdlp.Text.Trim();
            _config.FfmpegPath = _tbFfmpeg.Text.Trim();
            ConfigManager.Save(_config);
            _recorder = new StreamRecorder(_config, _auth, Log);
            MessageBox.Show("設定を保存しました");
        }

        // ---- 下部パネル ----

        private void BuildBottomPanel(Panel panel)
        {
            var ctrlBar = new Panel { Height = 38, Dock = DockStyle.Top, Parent = panel };

            _btnStart = new Button { Text = "▶  監視・録音開始", Width = 135, Location = new Point(5, 5), Parent = ctrlBar };
            _btnStop  = new Button { Text = "■  停止", Width = 80, Location = new Point(148, 5), Enabled = false, Parent = ctrlBar };
            _lblMonitorStatus = new Label { Text = "待機中", Location = new Point(242, 10), Width = 200, ForeColor = Color.Gray, Parent = ctrlBar };

            _btnStart.Click += delegate(object s, EventArgs e) { StartMonitoring(); };
            _btnStop.Click  += delegate(object s, EventArgs e) { StopMonitoring(); };

            var logLabel = new Label { Text = "ログ", Dock = DockStyle.Top, Height = 18, Parent = panel };
            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true,
                Font = new Font("Consolas", 9f), BackColor = Color.White, Parent = panel
            };
        }

        // ---- 監視ループ ----

        private void StartMonitoring()
        {
            var enabled = _config.Streamers.FindAll(delegate(StreamerEntry s) { return s.Enabled; });
            if (enabled.Count == 0)
            { MessageBox.Show("有効な配信者が設定されていません。\n「配信者設定」タブで追加してください。"); return; }

            _recorder = new StreamRecorder(_config, _auth, Log);
            _monitoring = true;
            _btnStart.Enabled = false;
            _btnStop.Enabled  = true;
            _lblMonitorStatus.Text      = "監視中 (" + enabled.Count + " 名)";
            _lblMonitorStatus.ForeColor = Color.Green;
            Log("監視開始 — " + enabled.Count + " 名を " + _config.CheckInterval + " 秒ごとにチェック");

            var t = new Thread(MonitorLoop);
            t.IsBackground = true;
            t.Start();
        }

        private void StopMonitoring()
        {
            _monitoring = false;
            _recorder.StopAll();
            _btnStart.Enabled = true;
            _btnStop.Enabled  = false;
            _lblMonitorStatus.Text      = "待機中";
            _lblMonitorStatus.ForeColor = Color.Gray;
            Log("監視を停止しました");
            RefreshLv();
        }

        private void MonitorLoop()
        {
            while (_monitoring)
            {
                var streamers = _config.Streamers.FindAll(
                    delegate(StreamerEntry s) { return s.Enabled; });

                foreach (var st in streamers)
                {
                    if (!_monitoring) break;

                    LiveInfo info = _monitor.CheckLive(st.UserId);
                    bool rec = _recorder.IsRecording(st.UserId);

                    string status = rec ? "録音中" : (info.IsOnLive ? "ライブ中" : "有効");
                    UpdateLvStatus(st.UserId, status, rec);

                    if (info.IsOnLive && !rec)
                    {
                        Log("[検出] " + st.UserId + " がライブ配信中 (movie_id=" + info.MovieId + ")");

                        if (info.IsProtected)
                        {
                            if (!string.IsNullOrEmpty(st.Password))
                            {
                                Log("[パスワード認証] " + st.UserId);
                                _auth.UnlockPasswordStream(info.MovieId, st.Password);
                            }
                            else
                            {
                                Log("[スキップ] " + st.UserId + ": パスワード必要ですが未設定");
                                continue;
                            }
                        }
                        _recorder.StartRecording(st.UserId, info.MovieId, st.Password);
                    }
                }

                for (int i = 0; i < _config.CheckInterval * 10 && _monitoring; i++)
                    Thread.Sleep(100);
            }
        }

        // ---- UI ヘルパー ----

        private void RefreshLv()
        {
            if (_lv.InvokeRequired) { _lv.Invoke(new Action(RefreshLv)); return; }
            _lv.Items.Clear();
            foreach (var s in _config.Streamers)
            {
                var item = new ListViewItem(s.UserId);
                item.SubItems.Add(s.DisplayName);
                item.SubItems.Add(string.IsNullOrEmpty(s.Password) ? "" : "****");
                item.SubItems.Add(s.Enabled ? "有効" : "無効");
                _lv.Items.Add(item);
            }
        }

        private void UpdateLvStatus(string userId, string status, bool recording)
        {
            if (_lv.InvokeRequired)
            {
                _lv.Invoke(new Action<string, string, bool>(UpdateLvStatus), userId, status, recording);
                return;
            }
            foreach (ListViewItem item in _lv.Items)
            {
                if (item.Text == userId)
                {
                    item.SubItems[3].Text = status;
                    item.BackColor = recording ? Color.LightCoral
                        : (status == "ライブ中" ? Color.LightYellow : Color.White);
                    break;
                }
            }
        }

        private void SetLoginLabel(string text, Color color)
        {
            if (_lblLoginStatus.InvokeRequired)
            { _lblLoginStatus.Invoke(new Action<string, Color>(SetLoginLabel), text, color); return; }
            _lblLoginStatus.Text      = text;
            _lblLoginStatus.ForeColor = color;
        }

        private void Log(string msg)
        {
            if (_rtbLog.InvokeRequired)
            { _rtbLog.Invoke(new Action<string>(Log), msg); return; }
            _rtbLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n");
            _rtbLog.ScrollToCaret();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_monitoring)
            {
                if (MessageBox.Show("録音中です。終了しますか？", "確認",
                    MessageBoxButtons.YesNo) == DialogResult.No)
                { e.Cancel = true; return; }
                _monitoring = false;
                _recorder.StopAll();
            }
        }

        // ---- 小物ヘルパー ----

        private static Button MkBtn(string text, int index, Control parent)
        {
            return new Button
            {
                Text = text, Width = 72, Height = 28,
                Location = new Point(6, 6 + index * 33), Parent = parent
            };
        }

        private static void MkFormLabel(Control parent, string text, int y, int lw)
        {
            new Label { Text = text, Location = new Point(15, y + 3), Width = lw, Parent = parent };
        }

        private static TextBox MkFormEntry(Control parent, int y, int lx, int ew, bool password)
        {
            var tb = new TextBox { Location = new Point(lx + 5, y), Width = ew, Parent = parent };
            if (password) tb.UseSystemPasswordChar = true;
            return tb;
        }
    }

    // ============================================================
    // 設定ファイル管理
    // ============================================================
    static class ConfigManager
    {
        private static string ConfigPath()
        {
            return Path.Combine(AppConfig.AppDir(), "config.json");
        }

        public static AppConfig Load()
        {
            var cfg = new AppConfig();
            if (!File.Exists(ConfigPath())) return cfg;
            try
            {
                var ser = new JavaScriptSerializer();
                var raw = ser.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(ConfigPath(), Encoding.UTF8));

                cfg.AccountUsername = GetStr(raw, "AccountUsername", cfg.AccountUsername);
                cfg.OutputDir       = GetStr(raw, "OutputDir",       cfg.OutputDir);
                cfg.YtdlpPath       = GetStr(raw, "YtdlpPath",       cfg.YtdlpPath);
                cfg.FfmpegPath      = GetStr(raw, "FfmpegPath",      cfg.FfmpegPath);

                object ciObj;
                int ci;
                if (raw.TryGetValue("CheckInterval", out ciObj) && ciObj != null
                    && int.TryParse(ciObj.ToString(), out ci))
                    cfg.CheckInterval = ci;

                object stObj;
                if (raw.TryGetValue("Streamers", out stObj))
                {
                    var arr = stObj as System.Collections.ArrayList;
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            var s = item as Dictionary<string, object>;
                            if (s == null) continue;
                            var e = new StreamerEntry();
                            e.UserId      = GetStr(s, "UserId",      "");
                            e.DisplayName = GetStr(s, "DisplayName", "");
                            e.Password    = GetStr(s, "Password",    "");
                            object enObj;
                            if (s.TryGetValue("Enabled", out enObj) && enObj is bool)
                                e.Enabled = (bool)enObj;
                            cfg.Streamers.Add(e);
                        }
                    }
                }
            }
            catch { }
            return cfg;
        }

        public static void Save(AppConfig cfg)
        {
            File.WriteAllText(ConfigPath(),
                new JavaScriptSerializer().Serialize(cfg), Encoding.UTF8);
        }

        private static string GetStr(Dictionary<string, object> d, string key, string defaultVal)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null && v.ToString() != "")
                return v.ToString();
            return defaultVal;
        }
    }

    // ============================================================
    // エントリーポイント
    // ============================================================
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
