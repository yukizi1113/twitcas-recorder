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
import html
import re
import threading
import time
import logging
import os
import subprocess
import sys
import tempfile
import requests
from pathlib import Path
from datetime import datetime
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit

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
        with open(CONFIG_FILE, "r", encoding="utf-8-sig") as f:
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
            with open(COOKIES_JSON_FILE, "r", encoding="utf-8-sig") as f:
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

    def unlock_password_stream(self, user_id: str, movie_id: str, password: str) -> bool:
        """パスワード保護配信を解錠する"""
        legacy_ok = False

        try:
            if movie_id:
                r = self.session.post(
                    self.CHECK_PASS_URL,
                    data={"movie_id": movie_id, "password": password},
                    headers={"Referer": "https://twitcasting.tv/"},
                    timeout=10,
                )
                legacy_ok = r.status_code == 200
        except Exception:
            legacy_ok = False

        try:
            page_url = f"https://twitcasting.tv/{user_id}"
            page = self.session.get(page_url, timeout=10)
            m = re.search(r'name="cs_session_id"\s+value="([^"]+)"', page.text)
            if not m:
                if legacy_ok:
                    self._persist_cookies()
                return legacy_ok

            unlocked = self.session.post(
                page_url,
                data={"password": password, "cs_session_id": m.group(1)},
                headers={"Referer": page_url},
                timeout=10,
            )
            ok = "tc-page-variables" in unlocked.text
            if ok or legacy_ok:
                self._persist_cookies()
            return ok or legacy_ok
        except Exception:
            if legacy_ok:
                self._persist_cookies()
            return legacy_ok

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
    """Resolve live HLS with yt-dlp and capture audio-only MP3 with ffmpeg."""

    def __init__(self, config: dict, auth: TwitCastingAuth, log_callback=None):
        self.config = config
        self.auth = auth
        self.log_callback = log_callback or print
        self.output_dir = Path(config.get("output_dir", str(APP_DIR / "recordings")))
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self._active: dict[str, subprocess.Popen] = {}  # user_id -> process
        self._recording_users: set[str] = set()
        self._stop_requested: set[str] = set()
        self._lock = threading.Lock()

    def _log(self, msg: str):
        self.log_callback(msg)

    def is_recording(self, user_id: str) -> bool:
        with self._lock:
            return user_id in self._recording_users

    def start_recording(self, user_id: str, movie_info: dict, password: str = ""):
        """録音スレッドを起動する"""
        with self._lock:
            if user_id in self._active and self._active[user_id].poll() is None:
                return  # 既に録音中

        movie_id = str(movie_info.get("id", "unknown"))
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        out_path = str(self.output_dir / f"{user_id}_{movie_id}_{timestamp}.mp3")

        t = threading.Thread(
            target=self._record,
            args=(user_id, movie_id, out_path, password),
            daemon=True,
        )
        t.start()

    def _record(self, user_id: str, movie_id: str, out_path: str, password: str):
        stream_url = (
            f"https://twitcasting.tv/{user_id}/movie/{movie_id}"
            if movie_id and movie_id != "unknown"
            else f"https://twitcasting.tv/{user_id}"
        )

        try:
            source = self._resolve_stream_source(user_id, movie_id, stream_url, password)
            if not source or not source.get("url"):
                self._log(f"[録音] m3u8 を取得できなかったため旧方式にフォールバックします: {user_id}")
                self._record_via_ytdlp_fallback(user_id, stream_url, out_path, password)
                return

            ffmpeg = self.config.get("ffmpeg_path", "ffmpeg")
            cmd = [ffmpeg, "-y", "-nostdin", "-loglevel", "info"]

            if source.get("cookies"):
                cmd += ["-cookies", source["cookies"]]

            user_agent = source["headers"].get("User-Agent")
            if user_agent:
                cmd += ["-user_agent", user_agent]

            header_block = self._build_header_block(source["headers"])
            if header_block:
                cmd += ["-headers", header_block]

            cmd += [
                "-reconnect", "1",
                "-reconnect_streamed", "1",
                "-reconnect_delay_max", "5",
                "-i", source["url"],
                "-map", "0:a:0?",
                "-vn",
                "-c:a", "libmp3lame",
                "-q:a", "0",
                "-f", "mp3",
                out_path,
            ]

            self._log(f"[録音開始] {user_id}  url={source['url']}")

            create_no_window = 0x08000000 if sys.platform == "win32" else 0
            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                creationflags=create_no_window,
                encoding="utf-8",
                errors="replace",
            )

            with self._lock:
                self._active[user_id] = proc

            disk_full = False
            for line in proc.stdout:
                line = line.rstrip()
                if line and not line.startswith("[debug]"):
                    if "No space left on device" in line:
                        disk_full = True
                    self._log(f"  [{user_id}] {line}")

            proc.wait()

            with self._lock:
                self._active.pop(user_id, None)

            has_output = Path(out_path).exists() and Path(out_path).stat().st_size > 0
            if proc.returncode == 0 and has_output:
                self._log(f"[録音完了] {user_id}")
            else:
                self._log(f"[録音終了] {user_id}  (終了コード: {proc.returncode})")
                if disk_full:
                    self._log("[エラー] 保存先ドライブの空き容量が不足しています")
                elif not has_output:
                    self._log("[エラー] 音声ファイルが生成されませんでした")

        except FileNotFoundError:
            self._log("[エラー] yt-dlp または ffmpeg が見つかりません。パスを確認してください。")
            with self._lock:
                self._active.pop(user_id, None)
        except Exception as e:
            self._log(f"[エラー] {user_id}: {e}")
            with self._lock:
                self._active.pop(user_id, None)

    def _resolve_stream_source(self, user_id: str, movie_id: str, stream_url: str, password: str) -> dict | None:
        prefer_ytdlp = bool(movie_id and movie_id != "unknown")
        if prefer_ytdlp:
            try:
                via_ytdlp = self._resolve_stream_source_via_ytdlp(stream_url, password)
            except Exception:
                via_ytdlp = None
            if via_ytdlp and via_ytdlp.get("url"):
                return via_ytdlp

        try:
            direct = self._resolve_stream_source_direct(user_id, movie_id, password)
        except Exception:
            direct = None
        if direct and direct.get("url"):
            return direct
        if not prefer_ytdlp:
            return self._resolve_stream_source_via_ytdlp(stream_url, password)
        return None

    def _resolve_stream_source_direct(self, user_id: str, movie_id: str, password: str) -> dict | None:
        session = self.auth.get_session()
        page_url = (
            f"https://twitcasting.tv/{user_id}/movie/{movie_id}"
            if movie_id and movie_id != "unknown"
            else f"https://twitcasting.tv/{user_id}"
        )

        page = session.get(page_url, timeout=15)
        html_text = page.text
        if password:
            m = re.search(r'name="cs_session_id"\s+value="([^"]+)"', html_text)
            if m:
                page = session.post(
                    page_url,
                    data={"password": password, "cs_session_id": m.group(1)},
                    headers={"Referer": page_url},
                    timeout=15,
                )
                html_text = page.text

        m = re.search(r'<meta name="tc-page-variables" content="([^"]+)"', html_text)
        if not m:
            return None

        page_vars = json.loads(html.unescape(m.group(1)))
        broadcaster_id = page_vars.get("broadcaster_id") or user_id
        pass_code = page_vars.get("pass_code") or ""

        streamserver = session.get(
            f"https://twitcasting.tv/streamserver.php?target={broadcaster_id}&mode=client&player=pc_web",
            headers={"Referer": page_url},
            timeout=15,
        )
        streamserver.raise_for_status()
        info = streamserver.json()

        streams = ((info.get("tc-hls") or {}).get("streams") or {})
        stream_url = streams.get("high") or streams.get("medium") or streams.get("low") or ""
        if not stream_url:
            return None

        if pass_code:
            stream_url = self._append_query_param(stream_url, "word", pass_code)

        return {
            "url": stream_url,
            "cookies": self._ffmpeg_cookie_string(),
            "headers": {
                "User-Agent": session.headers.get("User-Agent", ""),
                "Origin": "https://twitcasting.tv",
                "Referer": "https://twitcasting.tv/",
            },
        }

    def _resolve_stream_source_via_ytdlp(self, stream_url: str, password: str) -> dict | None:
        ytdlp = self.config.get("ytdlp_path", "yt-dlp")
        cmd = [ytdlp, "-J", "--no-playlist"]

        cookie_file = self._external_cookie_file()
        if cookie_file:
            cmd += ["--cookies", str(cookie_file)]
        if password:
            cmd += ["--video-password", password]
        cmd.append(stream_url)

        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=0x08000000 if sys.platform == "win32" else 0,
            check=False,
        )
        if result.returncode != 0:
            detail = result.stderr.strip() or "yt-dlp metadata resolve failed"
            if "Failed to extract" in detail or "[PYI-" in detail:
                detail = (
                    "yt-dlp.exe の展開に失敗しました。TEMP の空き容量不足か "
                    "スタンドアロン版の破損が疑われます"
                )
            raise RuntimeError(detail)

        info = json.loads(result.stdout)
        for fmt in info.get("requested_downloads", []):
            if not str(fmt.get("protocol", "")).startswith("m3u8"):
                continue
            if not fmt.get("url"):
                continue
            return {
                "url": fmt.get("url", ""),
                "cookies": fmt.get("cookies", ""),
                "headers": dict(fmt.get("http_headers") or {}),
            }

        best = None
        best_quality = -10**9
        for fmt in info.get("formats", []):
            if not str(fmt.get("protocol", "")).startswith("m3u8"):
                continue
            if not fmt.get("url"):
                continue
            quality = fmt.get("quality")
            try:
                quality = int(quality)
            except (TypeError, ValueError):
                quality = -1
            if best is None or quality >= best_quality:
                best = fmt
                best_quality = quality

        if not best:
            return None

        return {
            "url": best.get("url", ""),
            "cookies": best.get("cookies", ""),
            "headers": dict(best.get("http_headers") or {}),
        }

    def _session_cookie_header(self) -> str:
        cookies = []
        for c in self.auth.get_session().cookies:
            domain = c.domain or ""
            if "twitcasting.tv" in domain or domain == "":
                cookies.append(f"{c.name}={c.value}")
        return "; ".join(cookies)

    def _ffmpeg_cookie_string(self) -> str:
        parts = []
        for c in self.auth.get_session().cookies:
            domain = c.domain or ""
            if "twitcasting.tv" not in domain and domain != "":
                continue
            if not domain:
                domain = ".twitcasting.tv"
            if not domain.startswith("."):
                domain = "." + domain
            path = c.path or "/"
            segment = f"{c.name}={c.value}; path={path}; domain={domain};"
            if c.secure:
                segment += " secure;"
            parts.append(segment)
        return "\r\n".join(parts)

    def _external_cookie_file(self) -> Path | None:
        if not NETSCAPE_COOKIES_FILE.exists():
            return None
        target = Path(tempfile.gettempdir()) / "twitcas_recorder_cookies.txt"
        target.write_text(
            NETSCAPE_COOKIES_FILE.read_text(encoding="utf-8-sig"),
            encoding="utf-8",
        )
        return target

    def _append_query_param(self, url: str, key: str, value: str) -> str:
        parsed = urlsplit(url)
        query = dict(parse_qsl(parsed.query, keep_blank_values=True))
        query[key] = value
        return urlunsplit((
            parsed.scheme,
            parsed.netloc,
            parsed.path,
            urlencode(query),
            parsed.fragment,
        ))

    def _build_header_block(self, headers: dict) -> str:
        parts = []
        for key, value in headers.items():
            if key.lower() == "user-agent":
                continue
            if value:
                parts.append(f"{key}: {value}")
        return "\r\n".join(parts) + ("\r\n" if parts else "")

    def _record_via_ytdlp_fallback(self, user_id: str, stream_url: str, out_path: str, password: str):
        ytdlp = self.config.get("ytdlp_path", "yt-dlp")
        out_tmpl = str(Path(out_path).with_suffix(".%(ext)s"))
        cmd = [
            ytdlp,
            "--no-playlist",
            "--no-part",
            "-x",
            "--audio-format", "mp3",
            "--audio-quality", "0",
            "-o", out_tmpl,
        ]
        cookie_file = self._external_cookie_file()
        if cookie_file:
            cmd += ["--cookies", str(cookie_file)]
        if password:
            cmd += ["--video-password", password]
        ffmpeg = self.config.get("ffmpeg_path", "ffmpeg")
        if ffmpeg:
            cmd += ["--ffmpeg-location", ffmpeg]
        cmd.append(stream_url)

        create_no_window = 0x08000000 if sys.platform == "win32" else 0
        proc = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            creationflags=create_no_window,
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

    def is_recording(self, user_id: str) -> bool:
        with self._lock:
            return user_id in self._recording_users

    def start_recording(self, user_id: str, movie_info: dict, password: str = ""):
        with self._lock:
            if user_id in self._recording_users:
                return
            self._recording_users.add(user_id)
            self._stop_requested.discard(user_id)

        movie_id = str(movie_info.get("id", "unknown"))
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        out_path = str(self.output_dir / f"{user_id}_{movie_id}_{timestamp}.mp3")

        t = threading.Thread(
            target=self._record,
            args=(user_id, movie_id, out_path, password),
            daemon=True,
        )
        t.start()

    def _record(self, user_id: str, movie_id: str, out_path: str, password: str):
        stream_url = (
            f"https://twitcasting.tv/{user_id}/movie/{movie_id}"
            if movie_id and movie_id != "unknown"
            else f"https://twitcasting.tv/{user_id}"
        )

        try:
            max_attempts = 4
            for attempt in range(1, max_attempts + 1):
                if self._is_stop_requested(user_id):
                    return

                source = self._resolve_stream_source(user_id, movie_id, stream_url, password)
                if not source or not source.get("url"):
                    if attempt < max_attempts:
                        self._log(f"[再試行] {user_id} m3u8 を再解決します ({attempt}/{max_attempts})")
                        if self._wait_before_retry(user_id, 3.0):
                            continue
                        return

                    self._log(f"[録音] m3u8 を解決できませんでした。yt-dlp にフォールバックします: {user_id}")
                    self._record_via_ytdlp_fallback(user_id, stream_url, out_path, password)
                    return

                ffmpeg = self.config.get("ffmpeg_path", "ffmpeg")
                cmd = [ffmpeg, "-hide_banner", "-y", "-nostdin", "-loglevel", "info"]

                ffmpeg_cookies = self._ffmpeg_cookie_string() or source.get("cookies", "")
                if ffmpeg_cookies:
                    cmd += ["-cookies", ffmpeg_cookies]

                headers = source.get("headers") or {}
                user_agent = headers.get("User-Agent")
                if user_agent:
                    cmd += ["-user_agent", user_agent]

                header_block = self._build_header_block(headers)
                if header_block:
                    cmd += ["-headers", header_block]

                cmd += [
                    "-reconnect", "1",
                    "-reconnect_streamed", "1",
                    "-reconnect_delay_max", "5",
                    "-i", source["url"],
                    "-map", "0:a:0?",
                    "-vn",
                    "-c:a", "libmp3lame",
                    "-q:a", "0",
                    "-f", "mp3",
                    out_path,
                ]

                self._log(f"[録音開始] {user_id}  url={source['url']}")

                create_no_window = 0x08000000 if sys.platform == "win32" else 0
                proc = subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    creationflags=create_no_window,
                    encoding="utf-8",
                    errors="replace",
                )

                with self._lock:
                    self._active[user_id] = proc

                disk_full = False
                retryable_failure = False
                try:
                    for line in proc.stdout:
                        line = line.rstrip()
                        if line and not line.startswith("[debug]"):
                            if "No space left on device" in line:
                                disk_full = True
                            if self._is_retryable_input_failure_line(line):
                                retryable_failure = True
                            self._log(f"  [{user_id}] {line}")
                finally:
                    proc.wait()
                    with self._lock:
                        self._active.pop(user_id, None)

                has_output = Path(out_path).exists() and Path(out_path).stat().st_size > 0
                if proc.returncode == 0 and has_output:
                    self._log(f"[録音完了] {user_id}")
                    return

                if (
                    retryable_failure
                    and not disk_full
                    and not has_output
                    and not self._is_stop_requested(user_id)
                    and attempt < max_attempts
                ):
                    self._delete_if_exists(out_path)
                    self._log(f"[再試行] {user_id} 入力 URL を再解決します ({attempt}/{max_attempts})")
                    if self._wait_before_retry(user_id, 3.0):
                        continue
                    return

                self._log(f"[録音終了] {user_id}  (code:{proc.returncode})")
                if disk_full:
                    self._log("[エラー] 保存先ドライブの空き容量が不足しています")
                elif not has_output:
                    self._log("[エラー] 音声ファイルが生成されませんでした")
                return

        except FileNotFoundError:
            self._log("[エラー] yt-dlp または ffmpeg が見つかりません。パスを確認してください")
        except Exception as e:
            self._log(f"[エラー] {user_id}: {e}")
        finally:
            with self._lock:
                self._active.pop(user_id, None)
                self._recording_users.discard(user_id)
                self._stop_requested.discard(user_id)

    def _is_retryable_input_failure_line(self, line: str) -> bool:
        line = line.lower()
        return (
            "http error 404" in line
            or "http error 403" in line
            or "server returned 404" in line
            or "server returned 403" in line
            or "error opening input file" in line
            or "error opening input files" in line
        )

    def _is_stop_requested(self, user_id: str) -> bool:
        with self._lock:
            return user_id in self._stop_requested

    def _wait_before_retry(self, user_id: str, seconds: float) -> bool:
        deadline = time.time() + seconds
        while time.time() < deadline:
            if self._is_stop_requested(user_id):
                return False
            time.sleep(0.25)
        return not self._is_stop_requested(user_id)

    def _delete_if_exists(self, path: str):
        try:
            Path(path).unlink(missing_ok=True)
        except OSError:
            pass

    def stop_recording(self, user_id: str):
        with self._lock:
            is_recording = user_id in self._recording_users
            if is_recording:
                self._stop_requested.add(user_id)
            proc = self._active.get(user_id)
        if proc and proc.poll() is None:
            proc.terminate()
        if is_recording:
            self._log(f"[停止] {user_id} の録音を停止しました")

    def stop_all(self):
        with self._lock:
            targets = list(self._recording_users)
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
                            unlocked = self.auth.unlock_password_stream(uid, movie_id, pw)
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
