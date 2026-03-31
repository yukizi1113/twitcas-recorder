#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ツイキャス録音君
TwitCasting Stream Audio Recorder
- パスワード保護配信対応
- メンバーシップ限定配信対応 (ログイン / クッキー貼り付け)
"""

import tkinter as tk
from tkinter import ttk, messagebox, scrolledtext, filedialog
import json
import threading
import time
import logging
import os
import subprocess
import sys
import requests
from pathlib import Path
from datetime import datetime

# ============================================================
# パス定義
# ============================================================

# PyInstaller --onefile でビルドした場合は sys.executable の親ディレクトリを使う
# (sys._MEIPASS は一時展開先のため設定ファイル保存に不適)
if getattr(sys, "frozen", False):
    APP_DIR = Path(sys.executable).parent
else:
    APP_DIR = Path(__file__).parent
CONFIG_FILE = APP_DIR / "config.json"
COOKIES_JSON_FILE = APP_DIR / "cookies.json"
NETSCAPE_COOKIES_FILE = APP_DIR / "cookies.txt"
LOG_FILE = APP_DIR / "recorder.log"

DEFAULT_CONFIG = {
    "streamers": [],
    "account": {
        "username": ""
    },
    "output_dir": str(APP_DIR / "recordings"),
    "check_interval": 30,
    "ytdlp_path": "yt-dlp",
    "ffmpeg_path": "ffmpeg"
}


def load_config():
    if CONFIG_FILE.exists():
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            config = json.load(f)
        for key, val in DEFAULT_CONFIG.items():
            if key not in config:
                config[key] = val
        return config
    return DEFAULT_CONFIG.copy()


def save_config(config):
    with open(CONFIG_FILE, "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)


# ============================================================
# 認証モジュール
# ============================================================

class TwitCastingAuth:
    """TwitCasting認証管理 (TC account login / cookie import)"""

    TC_LOGIN_URL = "https://twitcasting.tv/tc_login.php"
    CHECK_PASS_URL = "https://twitcasting.tv/checkpassword.php"

    def __init__(self):
        self.session = requests.Session()
        self.session.headers.update({
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/122.0.0.0 Safari/537.36"
            ),
            "Accept-Language": "ja,en-US;q=0.9,en;q=0.8",
        })
        self.is_logged_in = False

    # ---- ログイン ----

    def login_tc_account(self, username: str, password: str):
        """TC account (email + password) でログイン"""
        try:
            resp = self.session.get(self.TC_LOGIN_URL, timeout=10)

            import re
            csrf = re.search(r'name="csrf_token"[^>]*value="([^"]+)"', resp.text)
            csrf_token = csrf.group(1) if csrf else ""

            data = {
                "username": username,
                "password": password,
                "csrf_token": csrf_token,
                "mode": "login",
            }
            self.session.post(
                self.TC_LOGIN_URL,
                data=data,
                headers={"Referer": self.TC_LOGIN_URL},
                allow_redirects=True,
                timeout=10,
            )

            if self._has_auth_cookie() or self._verify_logged_in():
                self.is_logged_in = True
                self._persist_cookies()
                return True, "ログイン成功"

            return False, "ログイン失敗 (ユーザー名/パスワードを確認してください)"

        except requests.RequestException as e:
            return False, f"通信エラー: {e}"

    def _has_auth_cookie(self):
        names = {c.name for c in self.session.cookies}
        return bool(names & {"tc_ss", "tc_id"})

    def _verify_logged_in(self):
        try:
            r = self.session.get("https://twitcasting.tv/", timeout=5)
            return "tc_ss" in r.cookies or "ログアウト" in r.text
        except Exception:
            return False

    # ---- クッキー手動入力 ----

    def set_cookies_from_string(self, cookie_string: str):
        """ブラウザ DevTools からコピーした Cookie 文字列をセット"""
        for part in cookie_string.split(";"):
            part = part.strip()
            if "=" in part:
                name, _, value = part.partition("=")
                self.session.cookies.set(
                    name.strip(), value.strip(), domain=".twitcasting.tv"
                )
        if self._has_auth_cookie():
            self.is_logged_in = True
        self._persist_cookies()

    # ---- 保存・読込 ----

    def _persist_cookies(self):
        """JSON + Netscape の両形式でクッキーを保存"""
        cookies_data = [
            {
                "name": c.name,
                "value": c.value,
                "domain": c.domain or ".twitcasting.tv",
                "path": c.path or "/",
                "secure": c.secure,
                "expires": c.expires,
            }
            for c in self.session.cookies
        ]
        with open(COOKIES_JSON_FILE, "w", encoding="utf-8") as f:
            json.dump(cookies_data, f)
        self._write_netscape_cookies()

    def load_cookies(self):
        """保存済みクッキーを読み込む"""
        if not COOKIES_JSON_FILE.exists():
            return False
        try:
            with open(COOKIES_JSON_FILE, "r", encoding="utf-8") as f:
                cookies_data = json.load(f)
            for c in cookies_data:
                self.session.cookies.set(
                    c["name"], c["value"],
                    domain=c.get("domain", ".twitcasting.tv"),
                )
            if self._has_auth_cookie():
                self.is_logged_in = True
                self._write_netscape_cookies()
                return True
        except Exception:
            pass
        return False

    def _write_netscape_cookies(self):
        """yt-dlp / streamlink 向け Netscape 形式クッキーを書き出す"""
        with open(NETSCAPE_COOKIES_FILE, "w", encoding="utf-8") as f:
            f.write("# Netscape HTTP Cookie File\n")
            for c in self.session.cookies:
                domain = c.domain or ".twitcasting.tv"
                if not domain.startswith("."):
                    domain = "." + domain
                path = c.path or "/"
                secure = "TRUE" if c.secure else "FALSE"
                expires = str(int(c.expires)) if c.expires else "0"
                f.write(f"{domain}\tTRUE\t{path}\t{secure}\t{expires}\t{c.name}\t{c.value}\n")

    # ---- パスワード配信の解錠 ----

    def unlock_password_stream(self, movie_id: str, password: str) -> bool:
        """パスワード保護配信を解錠する"""
        try:
            r = self.session.post(
                self.CHECK_PASS_URL,
                data={"movie_id": movie_id, "password": password},
                headers={"Referer": "https://twitcasting.tv/"},
                timeout=10,
            )
            return r.status_code == 200
        except Exception:
            return False

    def get_session(self):
        return self.session


# ============================================================
# 配信監視モジュール
# ============================================================

class StreamMonitor:
    """TwitCasting フロントエンド API で配信状態を確認"""

    LATEST_MOVIE_API = (
        "https://frontendapi.twitcasting.tv/users/{user_id}/latest-movie"
    )

    def __init__(self, auth: TwitCastingAuth):
        self.session = auth.get_session()

    def check_live(self, user_id: str):
        """
        Returns:
            (is_live: bool, movie_info: dict | None)
        """
        try:
            url = self.LATEST_MOVIE_API.format(user_id=user_id)
            resp = self.session.get(url, timeout=10)
            if resp.status_code != 200:
                return False, None
            data = resp.json()
            is_live = data.get("is_on_live", False)
            movie = data.get("movie") or {}
            return is_live, movie
        except Exception:
            return False, None


# ============================================================
# 録音モジュール
# ============================================================

class StreamRecorder:
    """yt-dlp を使って TwitCasting 配信の音声を録音する"""

    def __init__(self, config: dict, auth: TwitCastingAuth, log_callback=None):
        self.config = config
        self.auth = auth
        self.log_callback = log_callback or print
        self.output_dir = Path(config.get("output_dir", str(APP_DIR / "recordings")))
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self._active: dict[str, subprocess.Popen] = {}  # user_id -> process
        self._lock = threading.Lock()

    def _log(self, msg: str):
        self.log_callback(msg)

    def is_recording(self, user_id: str) -> bool:
        with self._lock:
            proc = self._active.get(user_id)
            if proc is None:
                return False
            if proc.poll() is not None:
                del self._active[user_id]
                return False
            return True

    def start_recording(self, user_id: str, movie_info: dict, password: str = ""):
        """録音スレッドを起動する"""
        with self._lock:
            if user_id in self._active and self._active[user_id].poll() is None:
                return  # 既に録音中

        movie_id = str(movie_info.get("id", "unknown"))
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        out_tmpl = str(self.output_dir / f"{user_id}_{movie_id}_{timestamp}.%(ext)s")

        t = threading.Thread(
            target=self._record,
            args=(user_id, movie_id, out_tmpl, password),
            daemon=True,
        )
        t.start()

    def _record(self, user_id: str, movie_id: str, out_tmpl: str, password: str):
        stream_url = f"https://twitcasting.tv/{user_id}"
        ytdlp = self.config.get("ytdlp_path", "yt-dlp")

        cmd = [
            ytdlp,
            "--no-playlist",
            "--no-part",
            "-x",                    # 音声のみ抽出
            "--audio-format", "aac",
            "--audio-quality", "0",
            "-o", out_tmpl,
        ]

        # ログイン済みなら Cookie を渡す (メンバーシップ限定配信用)
        if self.auth.is_logged_in and NETSCAPE_COOKIES_FILE.exists():
            cmd += ["--cookies", str(NETSCAPE_COOKIES_FILE)]

        # パスワード保護配信
        if password:
            cmd += ["--video-password", password]

        cmd.append(stream_url)

        self._log(f"[録音開始] {user_id}  →  {Path(out_tmpl).parent.name}/...")

        try:
            CREATE_NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0
            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                creationflags=CREATE_NO_WINDOW,
                encoding="utf-8",
                errors="replace",
            )

            with self._lock:
                self._active[user_id] = proc

            for line in proc.stdout:
                line = line.rstrip()
                if line and not line.startswith("[debug]"):
                    self._log(f"  [{user_id}] {line}")

            proc.wait()

            with self._lock:
                self._active.pop(user_id, None)

            if proc.returncode == 0:
                self._log(f"[録音完了] {user_id}")
            else:
                self._log(f"[録音終了] {user_id}  (終了コード: {proc.returncode})")

        except FileNotFoundError:
            self._log(
                "[エラー] yt-dlp が見つかりません。"
                "コマンドプロンプトで  pip install yt-dlp  を実行してください。"
            )
            with self._lock:
                self._active.pop(user_id, None)
        except Exception as e:
            self._log(f"[エラー] {user_id}: {e}")
            with self._lock:
                self._active.pop(user_id, None)

    def stop_recording(self, user_id: str):
        with self._lock:
            proc = self._active.get(user_id)
        if proc and proc.poll() is None:
            proc.terminate()
            self._log(f"[停止] {user_id} の録音を停止しました")

    def stop_all(self):
        with self._lock:
            targets = list(self._active.keys())
        for uid in targets:
            self.stop_recording(uid)


# ============================================================
# GUI アプリケーション
# ============================================================

class App(tk.Tk):
    TREE_COLS = ("user_id", "display_name", "password", "status")

    def __init__(self):
        super().__init__()
        self.title("ツイキャス録音君")
        self.geometry("820x650")
        self.resizable(True, True)

        self.config_data = load_config()
        self.auth = TwitCastingAuth()
        self.monitor = StreamMonitor(self.auth)
        self.recorder = StreamRecorder(self.config_data, self.auth, self._log)

        self._monitoring = False
        self._monitor_thread = None

        self._setup_file_logging()
        self._build_ui()

        # 保存済みクッキー読込
        if self.auth.load_cookies():
            self._log("保存済みクッキーを読み込みました (ログイン済み)")
            self._update_login_label(logged_in=True)

        self.protocol("WM_DELETE_WINDOW", self._on_close)

    # ---- ロギング設定 ----

    def _setup_file_logging(self):
        logging.basicConfig(
            filename=str(LOG_FILE),
            level=logging.INFO,
            format="%(asctime)s %(message)s",
            encoding="utf-8",
        )

    # ============================================================
    # UI 構築
    # ============================================================

    def _build_ui(self):
        main = ttk.Frame(self)
        main.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        nb = ttk.Notebook(main)
        nb.pack(fill=tk.BOTH, expand=True)

        # タブ
        self._tab_streamers = ttk.Frame(nb)
        nb.add(self._tab_streamers, text="  配信者設定  ")
        self._build_streamers_tab()

        self._tab_account = ttk.Frame(nb)
        nb.add(self._tab_account, text="  アカウント設定  ")
        self._build_account_tab()

        self._tab_settings = ttk.Frame(nb)
        nb.add(self._tab_settings, text="  詳細設定  ")
        self._build_settings_tab()

        # コントロールバー
        ctrl = ttk.Frame(main)
        ctrl.pack(fill=tk.X, pady=(8, 0))

        self._btn_start = ttk.Button(ctrl, text="▶  監視・録音開始", command=self._start_monitoring)
        self._btn_start.pack(side=tk.LEFT, padx=(0, 6))

        self._btn_stop = ttk.Button(ctrl, text="■  停止", command=self._stop_monitoring, state=tk.DISABLED)
        self._btn_stop.pack(side=tk.LEFT)

        self._lbl_status = ttk.Label(ctrl, text="待機中", foreground="gray")
        self._lbl_status.pack(side=tk.LEFT, padx=12)

        # ログエリア
        log_frame = ttk.LabelFrame(main, text="ログ")
        log_frame.pack(fill=tk.BOTH, expand=False, pady=(8, 0))

        self._log_area = scrolledtext.ScrolledText(
            log_frame, height=10, state=tk.DISABLED,
            font=("Consolas", 9), wrap=tk.WORD
        )
        self._log_area.pack(fill=tk.BOTH, expand=True, padx=4, pady=4)

    # ---- 配信者設定タブ ----

    def _build_streamers_tab(self):
        f = self._tab_streamers

        self._tree = ttk.Treeview(f, columns=self.TREE_COLS, show="headings", height=10)
        self._tree.heading("user_id", text="ユーザーID")
        self._tree.heading("display_name", text="表示名")
        self._tree.heading("password", text="合言葉/PW")
        self._tree.heading("status", text="状態")

        self._tree.column("user_id", width=160)
        self._tree.column("display_name", width=160)
        self._tree.column("password", width=100, anchor=tk.CENTER)
        self._tree.column("status", width=90, anchor=tk.CENTER)

        vsb = ttk.Scrollbar(f, orient=tk.VERTICAL, command=self._tree.yview)
        self._tree.configure(yscrollcommand=vsb.set)

        self._tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(5, 0), pady=5)
        vsb.pack(side=tk.LEFT, fill=tk.Y, pady=5)

        btn_f = ttk.Frame(f)
        btn_f.pack(side=tk.LEFT, fill=tk.Y, padx=8, pady=5)
        ttk.Button(btn_f, text="追加", width=10, command=self._add_streamer).pack(pady=3)
        ttk.Button(btn_f, text="編集", width=10, command=self._edit_streamer).pack(pady=3)
        ttk.Button(btn_f, text="削除", width=10, command=self._delete_streamer).pack(pady=3)

        self._refresh_tree()

    # ---- アカウント設定タブ ----

    def _build_account_tab(self):
        f = self._tab_account

        # TC アカウントログイン
        tc_frame = ttk.LabelFrame(f, text="TwitCasting アカウントでログイン  (メンバーシップ限定配信向け)")
        tc_frame.pack(fill=tk.X, padx=12, pady=12)

        ttk.Label(tc_frame, text="ユーザー名 / メールアドレス:").grid(row=0, column=0, sticky=tk.W, padx=8, pady=6)
        self._tc_user_var = tk.StringVar(value=self.config_data["account"].get("username", ""))
        ttk.Entry(tc_frame, textvariable=self._tc_user_var, width=32).grid(row=0, column=1, padx=6, pady=6)

        ttk.Label(tc_frame, text="パスワード:").grid(row=1, column=0, sticky=tk.W, padx=8, pady=6)
        self._tc_pass_var = tk.StringVar()
        ttk.Entry(tc_frame, textvariable=self._tc_pass_var, show="*", width=32).grid(row=1, column=1, padx=6, pady=6)

        btn_row = ttk.Frame(tc_frame)
        btn_row.grid(row=2, column=0, columnspan=2, sticky=tk.EW, padx=8, pady=6)
        ttk.Button(btn_row, text="ログイン", command=self._do_login).pack(side=tk.RIGHT)
        self._lbl_login = ttk.Label(btn_row, text="")
        self._lbl_login.pack(side=tk.LEFT)

        # クッキー貼り付け
        ck_frame = ttk.LabelFrame(
            f,
            text="ブラウザ Cookie を直接貼り付ける  (上のログインが失敗する場合の代替手段)"
        )
        ck_frame.pack(fill=tk.X, padx=12, pady=(0, 12))

        help_txt = (
            "① ブラウザで https://twitcasting.tv/ にログイン\n"
            "② F12 → [アプリケーション] → [Cookie] → [twitcasting.tv] を開く\n"
            "③ tc_ss や tc_id 等のクッキーを「名前=値; 名前=値; ...」形式でコピーして下に貼り付け"
        )
        ttk.Label(ck_frame, text=help_txt, foreground="gray", justify=tk.LEFT).pack(
            anchor=tk.W, padx=8, pady=(6, 2)
        )

        self._cookie_text = tk.Text(ck_frame, height=4, width=70)
        self._cookie_text.pack(padx=8, pady=4, fill=tk.X)

        ttk.Button(ck_frame, text="Cookie をセット", command=self._set_cookies).pack(
            anchor=tk.E, padx=8, pady=(0, 8)
        )

    # ---- 詳細設定タブ ----

    def _build_settings_tab(self):
        f = self._tab_settings

        rows = [
            ("録音ファイル保存先:", "output_dir", "dir"),
            ("チェック間隔 (秒):", "check_interval", "int"),
            ("yt-dlp パス:", "ytdlp_path", "str"),
            ("ffmpeg パス:", "ffmpeg_path", "str"),
        ]

        self._setting_vars = {}
        for i, (label, key, kind) in enumerate(rows):
            ttk.Label(f, text=label).grid(row=i, column=0, sticky=tk.W, padx=12, pady=8)
            var = tk.StringVar(value=str(self.config_data.get(key, "")))
            self._setting_vars[key] = var
            entry = ttk.Entry(f, textvariable=var, width=42)
            entry.grid(row=i, column=1, padx=6, pady=8, sticky=tk.W)
            if kind == "dir":
                ttk.Button(f, text="参照...", command=lambda e=entry, v=var: self._browse_dir(v)).grid(
                    row=i, column=2, padx=4
                )

        ttk.Button(f, text="設定を保存", command=self._save_settings).grid(
            row=len(rows) + 1, column=1, sticky=tk.E, padx=6, pady=16
        )

    # ============================================================
    # 配信者管理
    # ============================================================

    def _refresh_tree(self):
        self._tree.delete(*self._tree.get_children())
        for s in self.config_data.get("streamers", []):
            pw_display = "****" if s.get("password") else ""
            status = "有効" if s.get("enabled", True) else "無効"
            self._tree.insert("", tk.END, values=(
                s.get("user_id", ""),
                s.get("display_name", ""),
                pw_display,
                status,
            ))

    def _add_streamer(self):
        dlg = StreamerDialog(self)
        self.wait_window(dlg)
        if dlg.result:
            self.config_data.setdefault("streamers", []).append(dlg.result)
            save_config(self.config_data)
            self._refresh_tree()

    def _edit_streamer(self):
        sel = self._tree.selection()
        if not sel:
            messagebox.showwarning("選択なし", "編集する配信者を選択してください", parent=self)
            return
        idx = self._tree.index(sel[0])
        dlg = StreamerDialog(self, self.config_data["streamers"][idx])
        self.wait_window(dlg)
        if dlg.result:
            self.config_data["streamers"][idx] = dlg.result
            save_config(self.config_data)
            self._refresh_tree()

    def _delete_streamer(self):
        sel = self._tree.selection()
        if not sel:
            messagebox.showwarning("選択なし", "削除する配信者を選択してください", parent=self)
            return
        if messagebox.askyesno("確認", "選択した配信者を削除しますか？", parent=self):
            idx = self._tree.index(sel[0])
            self.config_data["streamers"].pop(idx)
            save_config(self.config_data)
            self._refresh_tree()

    # ============================================================
    # アカウント設定
    # ============================================================

    def _do_login(self):
        user = self._tc_user_var.get().strip()
        pw = self._tc_pass_var.get().strip()
        if not user or not pw:
            messagebox.showwarning("入力エラー", "ユーザー名とパスワードを入力してください", parent=self)
            return
        self._update_login_label(text="ログイン中...", color="blue")

        def task():
            ok, msg = self.auth.login_tc_account(user, pw)
            if ok:
                self.config_data["account"]["username"] = user
                save_config(self.config_data)
            self.after(0, lambda: (
                self._update_login_label(logged_in=ok, text=msg),
                self._log(msg)
            ))

        threading.Thread(target=task, daemon=True).start()

    def _update_login_label(self, logged_in=None, text=None, color=None):
        if logged_in is True:
            self._lbl_login.config(text=text or "ログイン済み", foreground="green")
        elif logged_in is False:
            self._lbl_login.config(text=text or "未ログイン", foreground="red")
        elif text:
            self._lbl_login.config(text=text, foreground=color or "black")

    def _set_cookies(self):
        ck = self._cookie_text.get("1.0", tk.END).strip()
        if not ck:
            messagebox.showwarning("入力エラー", "Cookie 文字列を貼り付けてください", parent=self)
            return
        self.auth.set_cookies_from_string(ck)
        if self.auth.is_logged_in:
            self._update_login_label(logged_in=True, text="Cookie をセット済み (ログイン済み)")
            self._log("ブラウザ Cookie をセットしました")
        else:
            self._update_login_label(logged_in=False, text="Cookie をセットしましたが認証クッキーが見つかりません")

    # ============================================================
    # 詳細設定
    # ============================================================

    def _browse_dir(self, var: tk.StringVar):
        d = filedialog.askdirectory(parent=self)
        if d:
            var.set(d)

    def _save_settings(self):
        for key, var in self._setting_vars.items():
            val = var.get().strip()
            if key == "check_interval":
                try:
                    val = int(val)
                except ValueError:
                    val = 30
            self.config_data[key] = val
        save_config(self.config_data)
        self.recorder = StreamRecorder(self.config_data, self.auth, self._log)
        messagebox.showinfo("設定", "設定を保存しました", parent=self)

    # ============================================================
    # 監視ループ
    # ============================================================

    def _start_monitoring(self):
        if self._monitoring:
            return
        enabled = [s for s in self.config_data.get("streamers", []) if s.get("enabled", True)]
        if not enabled:
            messagebox.showwarning(
                "設定エラー",
                "有効な配信者が登録されていません。\n「配信者設定」タブで追加してください。",
                parent=self,
            )
            return

        # 設定を最新に更新
        self.recorder = StreamRecorder(self.config_data, self.auth, self._log)

        self._monitoring = True
        self._btn_start.config(state=tk.DISABLED)
        self._btn_stop.config(state=tk.NORMAL)
        self._lbl_status.config(text=f"監視中 ({len(enabled)} 名)", foreground="green")
        self._log(f"監視開始 — {len(enabled)} 名の配信者を {self.config_data.get('check_interval', 30)} 秒ごとにチェック")

        self._monitor_thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self._monitor_thread.start()

    def _stop_monitoring(self):
        self._monitoring = False
        self.recorder.stop_all()
        self._btn_start.config(state=tk.NORMAL)
        self._btn_stop.config(state=tk.DISABLED)
        self._lbl_status.config(text="待機中", foreground="gray")
        self._log("監視を停止しました")
        self._refresh_tree()

    def _monitor_loop(self):
        while self._monitoring:
            streamers = [
                s for s in self.config_data.get("streamers", [])
                if s.get("enabled", True)
            ]

            for streamer in streamers:
                if not self._monitoring:
                    break

                uid = streamer.get("user_id", "")
                pw = streamer.get("password", "")

                is_live, movie_info = self.monitor.check_live(uid)
                is_recording = self.recorder.is_recording(uid)

                # ツリー状態更新
                def update_tree(u=uid, live=is_live, rec=is_recording):
                    items = self._tree.get_children()
                    uids = [self._tree.item(it, "values")[0] for it in items]
                    if u in uids:
                        idx = uids.index(u)
                        vals = list(self._tree.item(items[idx], "values"))
                        if rec:
                            vals[3] = "🔴 録音中"
                        elif live:
                            vals[3] = "📡 ライブ中"
                        else:
                            vals[3] = "有効"
                        self._tree.item(items[idx], values=vals)

                self.after(0, update_tree)

                if is_live and movie_info and not is_recording:
                    movie_id = str(movie_info.get("id", ""))
                    self._log(f"[検出] {uid} がライブ配信中 (movie_id={movie_id})")

                    is_protected = movie_info.get("is_protected", False)
                    if is_protected:
                        if pw:
                            self._log(f"[パスワード認証] {uid}")
                            unlocked = self.auth.unlock_password_stream(movie_id, pw)
                            if not unlocked:
                                self._log(f"[警告] {uid}: パスワード認証に失敗しました")
                        else:
                            self._log(f"[スキップ] {uid}: パスワード必要ですが未設定です")
                            continue

                    self.recorder.start_recording(uid, movie_info, pw)

            # インターバル (細かく区切って停止を素早く検知)
            interval = self.config_data.get("check_interval", 30)
            for _ in range(interval * 10):
                if not self._monitoring:
                    break
                time.sleep(0.1)

    # ============================================================
    # ログ出力
    # ============================================================

    def _log(self, msg: str):
        ts = datetime.now().strftime("%H:%M:%S")
        line = f"[{ts}] {msg}"
        logging.info(msg)

        def _update():
            self._log_area.config(state=tk.NORMAL)
            self._log_area.insert(tk.END, line + "\n")
            self._log_area.see(tk.END)
            self._log_area.config(state=tk.DISABLED)

        self.after(0, _update)

    # ============================================================
    # 終了処理
    # ============================================================

    def _on_close(self):
        if self._monitoring:
            if messagebox.askyesno("確認", "録音中です。終了しますか？", parent=self):
                self._monitoring = False
                self.recorder.stop_all()
                self.destroy()
        else:
            self.destroy()


# ============================================================
# 配信者追加/編集ダイアログ
# ============================================================

class StreamerDialog(tk.Toplevel):
    def __init__(self, parent, streamer: dict = None):
        super().__init__(parent)
        self.title("配信者設定")
        self.resizable(False, False)
        self.grab_set()
        self.result = None
        self._streamer = streamer or {}
        self._build_ui()
        self._center(parent)

    def _center(self, parent):
        self.update_idletasks()
        x = parent.winfo_x() + (parent.winfo_width() - self.winfo_width()) // 2
        y = parent.winfo_y() + (parent.winfo_height() - self.winfo_height()) // 2
        self.geometry(f"+{x}+{y}")

    def _build_ui(self):
        f = ttk.Frame(self, padding=20)
        f.pack(fill=tk.BOTH, expand=True)

        fields = [
            ("ユーザーID *", "user_id",
             "twitcasting.tv/[ここ] の部分 (例: test)"),
            ("表示名", "display_name",
             "ログに表示される名前 (省略可)"),
            ("合言葉 / パスワード", "password",
             "パスワード保護配信の場合のみ入力。不要な場合は空欄"),
        ]

        self._vars: dict[str, tk.StringVar] = {}
        for row, (label, key, hint) in enumerate(fields):
            ttk.Label(f, text=label).grid(row=row, column=0, sticky=tk.W, pady=6)
            var = tk.StringVar(value=self._streamer.get(key, ""))
            self._vars[key] = var
            show = "*" if key == "password" else ""
            ttk.Entry(f, textvariable=var, width=28, show=show).grid(row=row, column=1, padx=10)
            ttk.Label(f, text=hint, foreground="gray").grid(row=row, column=2, sticky=tk.W)

        self._enabled_var = tk.BooleanVar(value=self._streamer.get("enabled", True))
        ttk.Checkbutton(f, text="監視を有効にする", variable=self._enabled_var).grid(
            row=len(fields), column=1, sticky=tk.W, pady=8
        )

        btn_f = ttk.Frame(f)
        btn_f.grid(row=len(fields) + 1, column=0, columnspan=3, pady=(10, 0))
        ttk.Button(btn_f, text="OK", width=10, command=self._ok).pack(side=tk.LEFT, padx=6)
        ttk.Button(btn_f, text="キャンセル", width=10, command=self.destroy).pack(side=tk.LEFT, padx=6)

    def _ok(self):
        uid = self._vars["user_id"].get().strip()
        if not uid:
            messagebox.showwarning("入力エラー", "ユーザーID は必須です", parent=self)
            return
        self.result = {
            "user_id": uid,
            "display_name": self._vars["display_name"].get().strip() or uid,
            "password": self._vars["password"].get().strip(),
            "enabled": self._enabled_var.get(),
        }
        self.destroy()


# ============================================================
# エントリーポイント
# ============================================================

if __name__ == "__main__":
    app = App()
    app.mainloop()
