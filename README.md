# ツイキャス録音君

TwitCasting の配信を自動で音声録音するツールです。

## 特徴

| 機能 | 対応 |
|------|------|
| 通常配信の自動録音 | ✅ |
| **合言葉・パスワード保護配信** | ✅ |
| **メンバーシップ限定配信** | ✅ |
| 複数配信者の同時監視 | ✅ |
| 録音形式 | AAC (.aac) |
| **Whisper 文字起こし** | ✅ (日本語 large モデル対応) |

---

## バージョンについて

| ファイル | 言語 | exeサイズ | 備考 |
|----------|------|-----------|------|
| `ツイキャス録音君.exe` | **C# / WinForms** | **32 KB** | **推奨** |
| `main.py` | Python | (要PyInstaller) | 開発・改造用 |

C# 版は Windows に標準搭載の .NET Framework 4.x を使用するため、追加ランタイム不要で超軽量です。

---

## 必要なもの

### yt-dlp（必須）

録音のバックエンドとして使用します。コマンドプロンプトで以下を実行してください。

```
pip install yt-dlp
```

> Python がない場合は https://www.python.org/downloads/ からインストール後に実行してください。

### Whisper（文字起こし機能を使う場合）

録音ファイルを自動で文字起こしします。

```
pip install openai-whisper
```

> **注意**: large モデルは約 3GB のダウンロードが発生します。
> VRAM 10GB 以上の GPU を推奨します（CPU でも動作しますが非常に遅くなります）。

---

### ffmpeg（必須）

yt-dlp が音声変換に使用します。

1. https://github.com/BtbN/FFmpeg-Builds/releases から `ffmpeg-master-latest-win64-gpl.zip` をダウンロード
2. 展開して `ffmpeg.exe` を適当なフォルダに配置
3. そのフォルダをシステム環境変数 `PATH` に追加（または後述の「詳細設定」で直接パスを指定）

---

## インストール

```bash
git clone https://github.com/yukizi1113/twitcas-recorder.git
```

または ZIP でダウンロードして展開するだけです。

---

## 起動方法

`ツイキャス録音君.exe` をダブルクリックするだけです。

> **Python 不要** — .NET Framework 4.x は Windows 10 / 11 に標準搭載されています。

---

## 使い方

### 1. 配信者を追加する

「配信者設定」タブ → **追加** ボタン

| 項目 | 説明 |
|------|------|
| ユーザーID | `twitcasting.tv/` の後に続く文字列 |
| 表示名 | ログに表示される名前（省略可） |
| 合言葉 / パスワード | パスワード保護配信のみ入力（通常配信は空欄） |

---

### 2. メンバーシップ限定配信を録音する場合

「アカウント設定」タブで認証情報をセットします。

#### 方法 A: TC アカウントでログイン

TwitCasting のユーザー名（またはメールアドレス）とパスワードを入力して「ログイン」。

#### 方法 B: ブラウザの Cookie を貼り付ける（推奨）

Twitter/X・Google などの OAuth でログインしている場合はこちら。

1. ブラウザで [twitcasting.tv](https://twitcasting.tv/) にログイン
2. `F12` → **アプリケーション** → **Cookie** → `twitcasting.tv` を開く
3. `tc_id` と `tc_ss` の値をコピーして以下の形式で貼り付け

```
tc_id=（値）; tc_ss=（値）
```

4. 「Cookie をセット」ボタンをクリック → 緑色で「ログイン済み」と表示されれば完了

> Cookie の有効期限が切れた場合は同じ手順で再取得・再入力してください。

---

### 3. 監視・録音を開始する

画面下部の **▶ 監視・録音開始** ボタンをクリック。

- 設定したチェック間隔（デフォルト 30 秒）ごとに配信状態を確認します
- 配信開始を検出すると自動で録音を開始します
- 録音ファイルは `recordings/` フォルダに `ユーザーID_movieID_日時.aac` で保存されます

### 4. 文字起こし（Whisper）

「文字起こし」タブで設定します。

| 項目 | 説明 |
|------|------|
| 録音後に自動で文字起こし | 録音完了後に自動的に Whisper を実行 |
| モデル | `large`（最高精度・3GB）/ `medium`・`small`（軽量） |
| 言語 | `ja`（日本語）を推奨 |
| Whisper パス | `whisper` が PATH に通っていない場合は絶対パスを指定 |

文字起こし結果は録音ファイルと同じフォルダに `.txt` で保存されます。

「ファイルを選んで文字起こし」ボタンで手動実行も可能です。

---

### 5. 録音を停止する

**■ 停止** ボタンをクリック。配信が終了した場合は yt-dlp が自動的に録音を終了します。

---

## 文字起こし機能について

Whisper（OpenAI）の日本語 large モデルを使用します。

```
pip install openai-whisper
```

- 自動モード: 録音完了後に自動で文字起こしを開始
- 手動モード: 「文字起こし」タブの「ファイルを選んで文字起こし」ボタン
- 出力形式: `.txt`（録音ファイルと同じフォルダ）

### モデルの選択目安

| モデル | VRAM | 精度 |
|--------|------|------|
| large / large-v3 | 10GB+ | 最高 |
| medium | 5GB | 高 |
| small | 2GB | 中 |
| base / tiny | 1GB | 低 |

---

## ファイル構成

```
twitcas-recorder/
├── ツイキャス録音君.exe   # 実行ファイル (C#, 32 KB)
├── ツイキャス録音君.cs    # C# ソースコード
├── build.bat             # C# 再コンパイル用スクリプト
├── main.py               # Python 版ソースコード
├── requirements.txt      # Python 版依存パッケージ
├── README.md
├── .gitignore
│
│   ── 以下は起動時に自動生成 ──
├── config.json           # 設定ファイル (Git管理外)
├── cookies.json          # ログインクッキー (Git管理外)
├── cookies.txt           # Netscape形式クッキー・yt-dlp用 (Git管理外)
├── recorder.log          # ログファイル (Git管理外)
└── recordings/           # 録音ファイル保存先 (Git管理外)
```

---

## 設定ファイル (config.json)

初回起動で自動生成されます。直接編集することも可能です。

```json
{
  "Streamers": [
    {
      "UserId": "example",
      "DisplayName": "サンプル配信者",
      "Password": "合言葉（不要な場合は空文字）",
      "Enabled": true
    }
  ],
  "AccountUsername": "",
  "OutputDir": "recordings",
  "CheckInterval": 30,
  "YtdlpPath": "yt-dlp",
  "FfmpegPath": "ffmpeg"
}
```

---

## C# 版の再コンパイル

`build.bat` をダブルクリックするだけで `ツイキャス録音君.exe` が再生成されます。
Visual Studio・.NET SDK は不要です（Windows 標準の `csc.exe` を使用）。

```bat
build.bat
```

---

## トラブルシューティング

### `yt-dlp が見つかりません` と表示される

```
pip install yt-dlp
```

PATH が通っていない場合は「詳細設定」タブの「yt-dlp パス」に絶対パスを入力してください。

### メンバーシップ限定配信が録音できない

- アカウント設定で Cookie をセットしているか確認してください
- Cookie の有効期限が切れていないか確認してください（ブラウザで再ログイン後に再取得）

### パスワード保護配信が録音できない

- 配信者設定の「合言葉 / パスワード」欄に正しい合言葉が入力されているか確認してください

---

## 注意事項

- このツールはご自身がアクセス権を持つ配信のみに使用してください
- 録音したコンテンツの取り扱いは各配信者の利用規約に従ってください
- TwitCasting の仕様変更により動作しなくなる場合があります

---

## 動作確認環境

- Windows 11
- .NET Framework 4.8（Windows 標準搭載）
- yt-dlp 2024.x 以降
