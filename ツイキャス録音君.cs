// ツイキャス録音君 - C# 5 / WinForms (.NET Framework 4.x) 実装
// パスワード保護配信 + メンバーシップ限定配信 + Whisper文字起こし 対応
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
            UserId = ""; DisplayName = ""; Password = ""; Enabled = true;
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

        // TwitCasting API v2 認証
        public string ApiClientId { get; set; }
        public string ApiClientSecret { get; set; }

        // Whisper 設定
        public bool AutoTranscribe { get; set; }
        public string WhisperPath { get; set; }
        public string WhisperModel { get; set; }
        public string WhisperLanguage { get; set; }

        public AppConfig()
        {
            Streamers       = new List<StreamerEntry>();
            AccountUsername = "";
            OutputDir       = Path.Combine(AppDir(), "recordings");
            CheckInterval   = 30;
            YtdlpPath       = "yt-dlp";
            FfmpegPath      = "ffmpeg";
            ApiClientId     = "";
            ApiClientSecret = "";
            AutoTranscribe  = false;
            WhisperPath     = "whisper";
            WhisperModel    = "large";
            WhisperLanguage = "ja";
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
        public CookieContainer CookieJar { get { return _cookies; } }
        private static readonly Uri TcUri = new Uri("https://twitcasting.tv/");
        public bool IsLoggedIn { get; private set; }

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
                    catch { }
                }
            }
            IsLoggedIn = HasAuthCookie();
            if (IsLoggedIn) PersistCookies();
        }

        public bool LoadCookies()
        {
            string path = CookieJsonPath();
            if (!File.Exists(path)) return false;
            try
            {
                var ser  = new JavaScriptSerializer();
                var list = ser.Deserialize<List<Dictionary<string, object>>>(
                    File.ReadAllText(path, Encoding.UTF8));
                foreach (var c in list)
                {
                    string name   = DictStr(c, "name");
                    string val    = DictStr(c, "value");
                    string domain = DictStr(c, "domain");
                    if (domain == "") domain = ".twitcasting.tv";
                    if (name != "")
                        try { _cookies.Add(new Cookie(name, val, "/", domain)); } catch { }
                }
                IsLoggedIn = HasAuthCookie();
                if (IsLoggedIn) WriteNetscapeCookies();
                return IsLoggedIn;
            }
            catch { return false; }
        }

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

        public string NetscapeCookiePath()
        {
            return Path.Combine(AppConfig.AppDir(), "cookies.txt");
        }

        public HttpWebRequest CreateAuthRequest(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.CookieContainer = _cookies;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                          + "AppleWebKit/537.36 (KHTML, like Gecko) "
                          + "Chrome/122.0.0.0 Safari/537.36";
            req.Accept = "application/json, text/html, */*";
            req.Headers["Accept-Language"] = "ja,en-US;q=0.9";
            req.Headers["Origin"]          = "https://twitcasting.tv";
            req.Referer                    = "https://twitcasting.tv/";
            req.AllowAutoRedirect = true;
            return req;
        }

        private bool HasAuthCookie()
        {
            foreach (Cookie c in _cookies.GetCookies(TcUri))
                if (c.Name == "tc_ss" || c.Name == "tc_id") return true;
            return false;
        }

        private void PersistCookies()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (Cookie c in _cookies.GetCookies(TcUri))
            {
                var d = new Dictionary<string, object>();
                d.Add("name",   c.Name);  d.Add("value",  c.Value);
                d.Add("domain", c.Domain); d.Add("path",  c.Path);
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
            File.WriteAllText(NetscapeCookiePath(), sb.ToString(), new UTF8Encoding(false));
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
                sr.ReadToEnd();
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
        private readonly AppConfig _config;
        private readonly Action<string> _log;
        public StreamMonitor(TwitCastingAuth auth, AppConfig config, Action<string> log)
        {
            _auth   = auth;
            _config = config;
            _log    = log;
        }

        // 公式 API v2: GET /users/:user_id/current_live
        // 配信中 → 200 + JSON、配信なし → 404
        public LiveInfo CheckLive(string userId)
        {
            var info = new LiveInfo();
            try
            {
                string url = "https://apiv2.twitcasting.tv/users/" + userId + "/current_live";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method  = "GET";
                req.Accept  = "application/json";
                req.Headers["X-Api-Version"] = "2.0";

                // 認証: ClientID/Secret が設定されていれば Basic 認証
                if (!string.IsNullOrEmpty(_config.ApiClientId) &&
                    !string.IsNullOrEmpty(_config.ApiClientSecret))
                {
                    string cred = _config.ApiClientId + ":" + _config.ApiClientSecret;
                    string b64  = Convert.ToBase64String(Encoding.ASCII.GetBytes(cred));
                    req.Headers["Authorization"] = "Basic " + b64;
                }
                else
                {
                    // Cookie 認証フォールバック
                    req.CookieContainer = _auth.CookieJar;
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                                  + "AppleWebKit/537.36 (KHTML, like Gecko) "
                                  + "Chrome/122.0.0.0 Safari/537.36";
                    req.Headers["Origin"] = "https://twitcasting.tv";
                    req.Referer           = "https://twitcasting.tv/";
                }

                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = sr.ReadToEnd();

                var ser   = new JavaScriptSerializer();
                var data  = ser.Deserialize<Dictionary<string, object>>(json);
                object movieVal;
                if (data.TryGetValue("movie", out movieVal))
                {
                    var movie = movieVal as Dictionary<string, object>;
                    if (movie != null)
                    {
                        info.IsOnLive = true;
                        object idVal;
                        if (movie.TryGetValue("id", out idVal) && idVal != null)
                            info.MovieId = idVal.ToString();
                        object protVal;
                        if (movie.TryGetValue("is_protected", out protVal) && protVal is bool)
                            info.IsProtected = (bool)protVal;
                    }
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.NotFound)
                {
                    // 404 = API では配信なし → メンバーシップ限定の可能性があるので HTML で再確認
                    CheckLiveViaHtml(userId, info);
                }
                else if (resp != null && resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _log("[API エラー] " + userId + " : 401 Unauthorized — ClientID/Secret を確認してください");
                }
                else if (resp != null)
                {
                    _log("[API エラー] " + userId + " : HTTP " + (int)resp.StatusCode + " " + resp.StatusCode);
                }
                else
                {
                    _log("[API エラー] " + userId + " : " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                _log("[API エラー] " + userId + " : " + ex.Message);
            }
            return info;
        }

        // Cookie 認証でページ HTML を取得し、ライブ中かどうかを判定
        // メンバーシップ限定配信など API が 404 を返す場合のフォールバック
        private void CheckLiveViaHtml(string userId, LiveInfo info)
        {
            try
            {
                var req = _auth.CreateAuthRequest("https://twitcasting.tv/" + userId);
                req.Method = "GET";
                string html;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    html = sr.ReadToEnd();

                // パターン1: data-is-onlive="true" (TwitCasting の実際のHTML属性)
                if (html.IndexOf("data-is-onlive=\"true\"", StringComparison.Ordinal) >= 0)
                {
                    info.IsOnLive = true;
                }

                // パターン2: "is_on_live":true (JSON埋め込み)
                if (!info.IsOnLive &&
                    (html.IndexOf("\"is_on_live\":true", StringComparison.Ordinal) >= 0 ||
                     html.IndexOf("\"isOnLive\":true",   StringComparison.Ordinal) >= 0))
                {
                    info.IsOnLive = true;
                }

                // movie_id の抽出 (例: "movie_id":"833248531" または data-movie-id="...")
                if (info.IsOnLive && info.MovieId == "")
                {
                    info.MovieId = ExtractMovieId(html);
                }
            }
            catch { }
        }

        private static string ExtractMovieId(string html)
        {
            string[] patterns = new string[]
            {
                "\"movie_id\":\"", "\"movieId\":\"", "data-movie-id=\""
            };
            foreach (string pat in patterns)
            {
                int s = html.IndexOf(pat, StringComparison.Ordinal);
                if (s < 0) continue;
                s += pat.Length;
                int e = html.IndexOfAny(new char[] { '"', '\'', ' ', '>' }, s);
                if (e > s) return html.Substring(s, e - s);
            }
            return "";
        }
    }

    // ============================================================
    // 文字起こしクラス (Whisper)
    // ============================================================
    class Transcriber
    {
        private readonly AppConfig _config;
        private readonly Action<string> _log;
        private readonly Dictionary<string, Process> _active =
            new Dictionary<string, Process>();
        private readonly object _lock = new object();

        public Transcriber(AppConfig config, Action<string> log)
        {
            _config = config;
            _log    = log;
        }

        public bool IsTranscribing(string key)
        {
            lock (_lock)
            {
                if (!_active.ContainsKey(key)) return false;
                if (_active[key].HasExited) { _active.Remove(key); return false; }
                return true;
            }
        }

        // 指定した音声ファイルを文字起こしする (バックグラウンド実行)
        public void TranscribeAsync(string audioPath)
        {
            string key = Path.GetFileName(audioPath);
            lock (_lock) { if (IsTranscribing(key)) return; }

            var t = new Thread(delegate() { RunWhisper(audioPath, key); });
            t.IsBackground = true;
            t.Start();
        }

        private void RunWhisper(string audioPath, string key)
        {
            if (!File.Exists(audioPath))
            {
                _log("[文字起こし] ファイルが見つかりません: " + audioPath);
                return;
            }

            string outputDir = Path.GetDirectoryName(audioPath);

            // whisper "audio.aac" --model large --language ja
            //         --output_format txt --output_dir "dir"
            string args = "\"" + audioPath + "\""
                        + " --model "          + _config.WhisperModel
                        + " --language "       + _config.WhisperLanguage
                        + " --output_format txt"
                        + " --output_dir \""   + outputDir + "\"";

            _log("[文字起こし開始] " + Path.GetFileName(audioPath)
               + "  (モデル: " + _config.WhisperModel + ")");
            _log("  ※ large モデルは初回ダウンロード + 処理に数分かかります");

            var psi = new ProcessStartInfo();
            psi.FileName               = _config.WhisperPath;
            psi.Arguments              = args;
            psi.UseShellExecute        = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            psi.CreateNoWindow         = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding  = Encoding.UTF8;

            try
            {
                var proc = Process.Start(psi);
                lock (_lock) { _active[key] = proc; }

                proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) _log("  [whisper] " + e.Data);
                };
                proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) _log("  [whisper] " + e.Data);
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                lock (_lock) { _active.Remove(key); }

                if (proc.ExitCode == 0)
                {
                    // 出力ファイル名を確認
                    string baseName = Path.GetFileNameWithoutExtension(audioPath);
                    string txtPath  = Path.Combine(outputDir, baseName + ".txt");
                    if (File.Exists(txtPath))
                        _log("[文字起こし完了] " + baseName + ".txt に保存しました");
                    else
                        _log("[文字起こし完了] " + Path.GetFileName(audioPath));
                }
                else
                {
                    _log("[文字起こし終了] 終了コード: " + proc.ExitCode);
                }
            }
            catch (Exception ex)
            {
                lock (_lock) { _active.Remove(key); }
                bool notFound = ex.Message.Contains("ファイルが見つかりません")
                             || ex.Message.Contains("cannot find")
                             || ex.Message.Contains("No such file");
                if (notFound)
                    _log("[エラー] whisper が見つかりません。pip install openai-whisper を実行してください。");
                else
                    _log("[文字起こしエラー] " + ex.Message);
            }
        }

        public void StopAll()
        {
            string[] keys;
            lock (_lock) { keys = new string[_active.Count]; _active.Keys.CopyTo(keys, 0); }
            foreach (string k in keys)
            {
                Process proc;
                lock (_lock) { _active.TryGetValue(k, out proc); }
                if (proc != null) try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
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
        private readonly Action<string> _onRecordComplete; // 録音完了後コールバック(ファイルパス)
        private readonly Dictionary<string, Process> _active =
            new Dictionary<string, Process>();
        private readonly object _lock = new object();

        public StreamRecorder(AppConfig config, TwitCastingAuth auth,
            Action<string> log, Action<string> onRecordComplete = null)
        {
            _config           = config;
            _auth             = auth;
            _log              = log;
            _onRecordComplete = onRecordComplete;
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
            string ts       = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string baseName = userId + "_" + movieId + "_" + ts;
            string outTmpl  = Path.Combine(_config.OutputDir, baseName + ".%(ext)s");
            string url      = "https://twitcasting.tv/" + userId;

            var sb = new StringBuilder();
            sb.Append("--no-playlist --no-part ");
            sb.Append("-x --audio-format aac --audio-quality 0 ");
            sb.Append("-o \"" + outTmpl + "\" ");
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
                {
                    _log("[録音完了] " + userId);
                    // 実際に出力されたファイルを探してコールバック
                    if (_onRecordComplete != null)
                    {
                        string[] found = Directory.GetFiles(
                            _config.OutputDir, baseName + ".*");
                        if (found.Length > 0)
                            _onRecordComplete(found[0]);
                    }
                }
                else
                {
                    _log("[録音終了] " + userId + "  (code:" + proc.ExitCode + ")");
                }
            }
            catch (Exception ex)
            {
                lock (_lock) { _active.Remove(userId); }
                bool notFound = ex.Message.Contains("ファイルが見つかりません")
                             || ex.Message.Contains("cannot find")
                             || ex.Message.Contains("No such file");
                if (notFound)
                    _log("[エラー] yt-dlp が見つかりません。pip install yt-dlp を実行してください。");
                else
                    _log("[エラー] " + userId + ": " + ex.Message);
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

            int lx = 15, lw = 155, ex = 175, ew = 200, y = 20;
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

            _cbEnabled = new CheckBox { Text = "監視を有効にする", Location = new Point(ex, y),
                AutoSize = true, Checked = true, Parent = this };
            y += 38;

            var btnOk     = new Button { Text = "OK",         Location = new Point(ex, y),       Width = 80, Parent = this };
            var btnCancel = new Button { Text = "キャンセル", Location = new Point(ex + 90, y), Width = 90, Parent = this };
            AcceptButton = btnOk; CancelButton = btnCancel;

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
        private Transcriber _transcriber;
        private bool _monitoring;

        // 配信者タブ
        private ListView _lv;
        // アカウントタブ
        private TextBox _tbTcUser, _tbTcPass, _tbCookies;
        private Label _lblLoginStatus;
        // 文字起こしタブ
        private CheckBox _cbAutoTranscribe;
        private ComboBox _cbModel;
        private TextBox _tbWhisperLang, _tbWhisperPath, _tbManualFile;
        // 詳細設定タブ
        private TextBox _tbOutputDir, _tbInterval, _tbYtdlp, _tbFfmpeg;
        private TextBox _tbApiClientId, _tbApiClientSecret;
        // 下部
        private Label _lblMonitorStatus;
        private Button _btnStart, _btnStop;
        private RichTextBox _rtbLog;

        private static readonly string[] WhisperModels = new string[]
        {
            "tiny", "base", "small", "medium",
            "large", "large-v2", "large-v3"
        };

        public MainForm()
        {
            Text = "ツイキャス録音君";
            ClientSize = new Size(830, 680);
            MinimumSize = new Size(640, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Meiryo UI", 9f);

            _config      = ConfigManager.Load();
            _auth        = new TwitCastingAuth();
            _monitor     = new StreamMonitor(_auth, _config, Log);
            _transcriber = new Transcriber(_config, Log);
            _recorder    = new StreamRecorder(_config, _auth, Log, OnRecordComplete);

            BuildUI();

            if (_auth.LoadCookies())
            {
                Log("保存済みクッキーを読み込みました (ログイン済み)");
                SetLoginLabel("ログイン済み", Color.Green);
            }

            FormClosing += OnFormClosing;
        }

        // ============================================================
        // UI 構築
        // ============================================================
        private void BuildUI()
        {
            var bottom = new Panel { Height = 195, Dock = DockStyle.Bottom, Parent = this };

            var tab = new TabControl { Dock = DockStyle.Fill, Parent = this };
            tab.Padding = new Point(10, 4);

            var p1 = new TabPage("  配信者設定  "); tab.TabPages.Add(p1); BuildStreamersPage(p1);
            var p2 = new TabPage("  アカウント設定  "); tab.TabPages.Add(p2); BuildAccountPage(p2);
            var p3 = new TabPage("  文字起こし  "); tab.TabPages.Add(p3); BuildTranscribePage(p3);
            var p4 = new TabPage("  詳細設定  "); tab.TabPages.Add(p4); BuildSettingsPage(p4);

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
                if (d.ShowDialog(this) == DialogResult.OK)
                { _config.Streamers.Add(d.Result); ConfigManager.Save(_config); RefreshLv(); }
        }
        private void OnEditStreamer(object s, EventArgs e)
        {
            if (_lv.SelectedIndices.Count == 0) { MessageBox.Show("編集する配信者を選択してください"); return; }
            int idx = _lv.SelectedIndices[0];
            using (var d = new StreamerDialog(_config.Streamers[idx]))
                if (d.ShowDialog(this) == DialogResult.OK)
                { _config.Streamers[idx] = d.Result; ConfigManager.Save(_config); RefreshLv(); }
        }
        private void OnDeleteStreamer(object s, EventArgs e)
        {
            if (_lv.SelectedIndices.Count == 0) { MessageBox.Show("削除する配信者を選択してください"); return; }
            if (MessageBox.Show("削除しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes)
            { _config.Streamers.RemoveAt(_lv.SelectedIndices[0]); ConfigManager.Save(_config); RefreshLv(); }
        }

        // ---- アカウント設定 ----
        private void BuildAccountPage(TabPage page)
        {
            int y = 12;
            var grp1 = MkGroup(page, "TwitCasting アカウントでログイン  (メンバーシップ限定配信向け)", y, 155);
            MkFormLabel(grp1, "ユーザー名 / メール:", 25, 175);
            _tbTcUser = MkFormEntry(grp1, 25, 175, 220, false);
            _tbTcUser.Text = _config.AccountUsername;
            MkFormLabel(grp1, "パスワード:", 58, 175);
            _tbTcPass = MkFormEntry(grp1, 58, 175, 220, true);
            var btnLogin = new Button { Text = "ログイン", Location = new Point(175, 90), Width = 85, Parent = grp1 };
            _lblLoginStatus = new Label { Location = new Point(270, 93), Width = 250, Parent = grp1 };
            btnLogin.Click += OnLogin;

            y += 170;
            var grp2 = MkGroup(page, "ブラウザ Cookie を直接貼り付ける  (ログインが失敗する場合の代替手段)", y, 190);
            var helpLbl = new Label
            {
                Text = "① ブラウザで twitcasting.tv にログイン\r\n"
                     + "② F12 → アプリケーション → Cookie → twitcasting.tv を開く\r\n"
                     + "③ tc_id と tc_ss を「tc_id=値; tc_ss=値」の形式で貼り付け",
                Location = new Point(10, 22), Size = new Size(grp2.Width - 20, 55),
                ForeColor = Color.Gray, Parent = grp2
            };
            _tbCookies = new TextBox { Location = new Point(10, 83), Width = grp2.Width - 20,
                Height = 55, Multiline = true, ScrollBars = ScrollBars.Vertical, Parent = grp2 };
            grp2.Resize += delegate(object s, EventArgs e)
            {
                helpLbl.Width = grp2.Width - 20;
                _tbCookies.Width = grp2.Width - 20;
            };
            var btnCk = new Button { Text = "Cookie をセット", Location = new Point(10, 150), Width = 115, Parent = grp2 };
            btnCk.Click += OnSetCookies;
        }

        private void OnLogin(object sender, EventArgs e)
        {
            string u = _tbTcUser.Text.Trim(), p = _tbTcPass.Text.Trim();
            if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p))
            { MessageBox.Show("ユーザー名とパスワードを入力してください"); return; }
            SetLoginLabel("ログイン中...", Color.Blue);
            new Thread(delegate()
            {
                var res = _auth.LoginTcAccount(u, p);
                if (res.Item1) { _config.AccountUsername = u; ConfigManager.Save(_config); }
                Invoke(new Action(delegate()
                { SetLoginLabel(res.Item2, res.Item1 ? Color.Green : Color.Red); Log(res.Item2); }));
            }) { IsBackground = true }.Start();
        }
        private void OnSetCookies(object sender, EventArgs e)
        {
            string ck = _tbCookies.Text.Trim();
            if (string.IsNullOrEmpty(ck)) { MessageBox.Show("Cookie 文字列を入力してください"); return; }
            _auth.SetCookiesFromString(ck);
            if (_auth.IsLoggedIn)
            { SetLoginLabel("Cookie をセット済み (ログイン済み)", Color.Green); Log("ブラウザ Cookie をセットしました"); }
            else SetLoginLabel("認証 Cookie が見つかりません", Color.Red);
        }

        // ---- 文字起こしタブ ----
        private void BuildTranscribePage(TabPage page)
        {
            int y = 12;

            // --- 自動文字起こし設定 ---
            var grp1 = MkGroup(page, "自動文字起こし設定", y, 175);

            _cbAutoTranscribe = new CheckBox
            {
                Text = "録音完了後に自動で文字起こしを実行する",
                Location = new Point(12, 25), AutoSize = true,
                Checked = _config.AutoTranscribe, Parent = grp1
            };
            _cbAutoTranscribe.CheckedChanged += delegate(object s, EventArgs e)
            {
                _config.AutoTranscribe = _cbAutoTranscribe.Checked;
                ConfigManager.Save(_config);
            };

            MkFormLabel(grp1, "モデル:", 60, 100);
            _cbModel = new ComboBox
            {
                Location = new Point(108, 57), Width = 130,
                DropDownStyle = ComboBoxStyle.DropDownList, Parent = grp1
            };
            foreach (string m in WhisperModels) _cbModel.Items.Add(m);
            _cbModel.SelectedItem = _config.WhisperModel;
            if (_cbModel.SelectedIndex < 0) _cbModel.SelectedIndex = 4; // large
            _cbModel.SelectedIndexChanged += delegate(object s, EventArgs e)
            {
                _config.WhisperModel = _cbModel.SelectedItem.ToString();
                ConfigManager.Save(_config);
            };

            var modelNote = new Label
            {
                Text = "large: 最高精度、初回ダウンロード約3GB、要GPU(VRAM 10GB+)またはCPU(時間かかる)",
                Location = new Point(250, 60), Width = 400,
                ForeColor = Color.Gray, Parent = grp1
            };

            MkFormLabel(grp1, "言語:", 95, 100);
            _tbWhisperLang = MkFormEntry(grp1, 92, 108, 60, false);
            _tbWhisperLang.Text = _config.WhisperLanguage;
            var langNote = new Label
            {
                Text = "例: ja (日本語)  en (英語)  zh (中国語)",
                Location = new Point(180, 95), Width = 300,
                ForeColor = Color.Gray, Parent = grp1
            };

            MkFormLabel(grp1, "whisper パス:", 128, 100);
            _tbWhisperPath = MkFormEntry(grp1, 125, 108, 200, false);
            _tbWhisperPath.Text = _config.WhisperPath;
            var pathNote = new Label
            {
                Text = "pip install openai-whisper でインストール",
                Location = new Point(318, 128), Width = 260,
                ForeColor = Color.Gray, Parent = grp1
            };

            var btnSaveW = new Button { Text = "保存", Location = new Point(108, 152), Width = 70, Parent = grp1 };
            btnSaveW.Click += delegate(object s, EventArgs e)
            {
                _config.WhisperModel    = _cbModel.SelectedItem.ToString();
                _config.WhisperLanguage = _tbWhisperLang.Text.Trim();
                _config.WhisperPath     = _tbWhisperPath.Text.Trim();
                _config.AutoTranscribe  = _cbAutoTranscribe.Checked;
                ConfigManager.Save(_config);
                _transcriber = new Transcriber(_config, Log);
                MessageBox.Show("文字起こし設定を保存しました");
            };

            y += 190;

            // --- 手動文字起こし ---
            var grp2 = MkGroup(page, "手動文字起こし  (既存の音声ファイルを文字起こし)", y, 110);

            MkFormLabel(grp2, "音声ファイル:", 28, 100);
            _tbManualFile = MkFormEntry(grp2, 25, 110, 380, false);
            var btnBrowseFile = new Button { Text = "参照...", Location = new Point(500, 25), Width = 65, Parent = grp2 };
            btnBrowseFile.Click += delegate(object s, EventArgs e)
            {
                using (var d = new OpenFileDialog())
                {
                    d.Filter = "音声ファイル|*.aac;*.mp3;*.wav;*.m4a;*.flac;*.ogg|全てのファイル|*.*";
                    d.InitialDirectory = _config.OutputDir;
                    if (d.ShowDialog() == DialogResult.OK) _tbManualFile.Text = d.FileName;
                }
            };

            var btnTranscribe = new Button { Text = "文字起こし実行", Location = new Point(110, 62), Width = 120, Parent = grp2 };
            var noteLabel = new Label
            {
                Text = "出力: 音声ファイルと同じフォルダに .txt で保存されます",
                Location = new Point(240, 66), Width = 340, ForeColor = Color.Gray, Parent = grp2
            };
            btnTranscribe.Click += delegate(object s, EventArgs e)
            {
                string f = _tbManualFile.Text.Trim();
                if (string.IsNullOrEmpty(f)) { MessageBox.Show("音声ファイルを選択してください"); return; }
                if (!File.Exists(f)) { MessageBox.Show("ファイルが見つかりません:\n" + f); return; }
                _transcriber = new Transcriber(_config, Log);
                _transcriber.TranscribeAsync(f);
            };
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

            // TwitCasting API v2 認証
            var grpApi = new GroupBox
            {
                Text = "TwitCasting API v2 認証（ライブ検出に使用）",
                Location = new Point(10, y), Width = 590, Height = 105, Parent = page
            };
            int gy = 22;
            new Label { Text = "Client ID :", Location = new Point(10, gy + 3), Width = 120, Parent = grpApi };
            _tbApiClientId = new TextBox { Location = new Point(135, gy), Width = 420, Parent = grpApi };
            _tbApiClientId.Text = _config.ApiClientId;
            gy += 30;
            new Label { Text = "Client Secret :", Location = new Point(10, gy + 3), Width = 120, Parent = grpApi };
            _tbApiClientSecret = new TextBox
            {
                Location = new Point(135, gy), Width = 420,
                UseSystemPasswordChar = true, Parent = grpApi
            };
            _tbApiClientSecret.Text = _config.ApiClientSecret;
            gy += 30;
            new Label
            {
                Text = "※ https://twitcasting.tv/developer/ でアプリ登録して取得してください",
                Location = new Point(10, gy), Width = 540, ForeColor = Color.Gray, Parent = grpApi
            };
            y += 115;

            var btnSave = new Button { Text = "設定を保存", Location = new Point(175, y), Width = 100, Parent = page };
            btnSave.Click += OnSaveSettings;
        }

        private void OnSaveSettings(object sender, EventArgs e)
        {
            _config.OutputDir = _tbOutputDir.Text.Trim();
            int iv;
            if (int.TryParse(_tbInterval.Text, out iv) && iv > 0) _config.CheckInterval = iv;
            _config.YtdlpPath       = _tbYtdlp.Text.Trim();
            _config.FfmpegPath      = _tbFfmpeg.Text.Trim();
            _config.ApiClientId     = _tbApiClientId.Text.Trim();
            _config.ApiClientSecret = _tbApiClientSecret.Text.Trim();
            ConfigManager.Save(_config);
            _recorder = new StreamRecorder(_config, _auth, Log, OnRecordComplete);
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

            new Label { Text = "ログ", Dock = DockStyle.Top, Height = 18, Parent = panel };
            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true,
                Font = new Font("Consolas", 9f), BackColor = Color.White, Parent = panel
            };
        }

        // ============================================================
        // 録音完了コールバック → 自動文字起こし
        // ============================================================
        private void OnRecordComplete(string audioPath)
        {
            if (_config.AutoTranscribe)
            {
                _transcriber = new Transcriber(_config, Log);
                _transcriber.TranscribeAsync(audioPath);
            }
        }

        // ============================================================
        // 監視ループ
        // ============================================================
        private void StartMonitoring()
        {
            var enabled = _config.Streamers.FindAll(
                delegate(StreamerEntry s) { return s.Enabled; });
            if (enabled.Count == 0)
            { MessageBox.Show("有効な配信者が設定されていません。\n「配信者設定」タブで追加してください。"); return; }

            _recorder = new StreamRecorder(_config, _auth, Log, OnRecordComplete);
            _monitoring = true;
            _btnStart.Enabled = false; _btnStop.Enabled = true;
            _lblMonitorStatus.Text      = "監視中 (" + enabled.Count + " 名)";
            _lblMonitorStatus.ForeColor = Color.Green;
            new Thread(MonitorLoop) { IsBackground = true }.Start();
            Log("監視開始 — " + enabled.Count + " 名を " + _config.CheckInterval + " 秒ごとにチェック");
        }

        private void StopMonitoring()
        {
            _monitoring = false;
            _recorder.StopAll();
            _btnStart.Enabled = true; _btnStop.Enabled = false;
            _lblMonitorStatus.Text = "待機中"; _lblMonitorStatus.ForeColor = Color.Gray;
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
                            else { Log("[スキップ] " + st.UserId + ": パスワード必要ですが未設定"); continue; }
                        }
                        _recorder.StartRecording(st.UserId, info.MovieId, st.Password);
                    }
                }
                for (int i = 0; i < _config.CheckInterval * 10 && _monitoring; i++)
                    Thread.Sleep(100);
            }
        }

        // ============================================================
        // UI ヘルパー
        // ============================================================
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
            { _lv.Invoke(new Action<string, string, bool>(UpdateLvStatus), userId, status, recording); return; }
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
            _lblLoginStatus.Text = text; _lblLoginStatus.ForeColor = color;
        }

        private void Log(string msg)
        {
            if (_rtbLog == null) return;
            try
            {
            if (_rtbLog.InvokeRequired)
            { _rtbLog.Invoke(new Action<string>(Log), msg); return; }
            _rtbLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n");
            try { _rtbLog.ScrollToCaret(); } catch { }
            }
            catch { }
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
            _transcriber.StopAll();
        }

        // ---- 小物ヘルパー ----
        private static Button MkBtn(string text, int index, Control parent)
        {
            return new Button { Text = text, Width = 72, Height = 28,
                Location = new Point(6, 6 + index * 33), Parent = parent };
        }

        private static GroupBox MkGroup(TabPage page, string text, int y, int h)
        {
            var grp = new GroupBox { Text = text, Location = new Point(10, y), Height = h,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Parent = page };
            page.Resize += delegate(object s, EventArgs e) { grp.Width = page.Width - 20; };
            grp.Width = page.Width - 20;
            return grp;
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
                cfg.ApiClientId     = GetStr(raw, "ApiClientId",     cfg.ApiClientId);
                cfg.ApiClientSecret = GetStr(raw, "ApiClientSecret", cfg.ApiClientSecret);
                cfg.WhisperPath     = GetStr(raw, "WhisperPath",     cfg.WhisperPath);
                cfg.WhisperModel    = GetStr(raw, "WhisperModel",    cfg.WhisperModel);
                cfg.WhisperLanguage = GetStr(raw, "WhisperLanguage", cfg.WhisperLanguage);

                object ciObj; int ci;
                if (raw.TryGetValue("CheckInterval", out ciObj) && ciObj != null
                    && int.TryParse(ciObj.ToString(), out ci)) cfg.CheckInterval = ci;

                object atObj;
                if (raw.TryGetValue("AutoTranscribe", out atObj) && atObj is bool)
                    cfg.AutoTranscribe = (bool)atObj;

                object stObj;
                if (raw.TryGetValue("Streamers", out stObj))
                {
                    var arr = stObj as System.Collections.ArrayList;
                    if (arr != null)
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
            catch { }
            return cfg;
        }

        public static void Save(AppConfig cfg)
        {
            File.WriteAllText(ConfigPath(),
                new JavaScriptSerializer().Serialize(cfg), Encoding.UTF8);
        }

        private static string GetStr(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null && v.ToString() != "")
                return v.ToString();
            return def;
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
            // TLS 1.2 を強制 (.NET 4.x デフォルトは TLS 1.0 のため)
            ServicePointManager.SecurityProtocol =
                (SecurityProtocolType)3072  // Tls12
              | (SecurityProtocolType)768   // Tls11
              | SecurityProtocolType.Tls;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
