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

---

## 必要なもの

### Python (3.9 以上)

https://www.python.org/downloads/ からインストールしてください。

### yt-dlp

```
pip install yt-dlp
```

### ffmpeg

yt-dlp が音声変換に使用します。

1. https://github.com/BtbN/FFmpeg-Builds/releases から `ffmpeg-master-latest-win64-gpl.zip` をダウンロード
2. 展開して `ffmpeg.exe` を `C:\ffmpeg\bin\` に配置
3. システム環境変数 `PATH` に `C:\ffmpeg\bin` を追加

> yt-dlp は `pip install yt-dlp` でインストール済みの場合、`yt-dlp --update-to nightly` で ffmpeg を自動ダウンロードできることもあります。

---

## インストール

```bash
git clone https://github.com/yukizi1113/twitcas-recorder.git
cd twitcas-recorder
pip install -r requirements.txt
```

---

## 起動方法

```bash
python main.py
```

---

## 使い方

### 1. 配信者を追加する

「配信者設定」タブ → **追加** ボタン

| 項目 | 説明 |
|------|------|
| ユーザーID | `twitcasting.tv/` の後に続く文字列 |
| 表示名 | ログに表示される名前（省略可） |
| 合言葉 / パスワード | パスワード保護配信のみ入力（通常配信は空欄） |

### 2. メンバーシップ限定配信を録音する場合

「アカウント設定」タブで TwitCasting アカウントにログインします。

#### 方法 A: TC アカウントでログイン

メールアドレス（またはユーザー名）とパスワードを入力して「ログイン」。

#### 方法 B: ブラウザの Cookie を貼り付ける（推奨）

Twitter/X・Google などの OAuth ログインを使っている場合はこちらを使用してください。

1. ブラウザで [twitcasting.tv](https://twitcasting.tv/) にログイン
2. `F12` → **アプリケーション** → **Cookie** → `twitcasting.tv` を開く
3. `tc_ss` や `tc_id` などの Cookie をコピー
4. テキストボックスに `名前=値; 名前=値; ...` 形式で貼り付けて「Cookie をセット」

### 3. 監視・録音を開始する

画面下部の **▶ 監視・録音開始** ボタンをクリックします。

- 設定したチェック間隔（デフォルト 30 秒）ごとに配信状態を確認します
- 配信開始を検出すると自動で録音を開始します
- 録音ファイルは `recordings/` フォルダに保存されます

### 4. 録音を停止する

**■ 停止** ボタンをクリックします。配信が終了した場合は yt-dlp が自動的に録音を終了します。

---

## ファイル構成

```
ツイキャス録音君_20260331/
├── main.py              # メインアプリケーション
├── requirements.txt     # Python 依存パッケージ
├── README.md
├── .gitignore
├── config.json          # 設定ファイル（初回起動時に自動生成）
├── cookies.json         # ログインクッキー（自動生成・Git管理外）
├── cookies.txt          # Netscape 形式クッキー（yt-dlp 用・自動生成）
├── recorder.log         # ログファイル（自動生成）
└── recordings/          # 録音ファイル保存先（自動生成）
```

---

## 設定ファイル (config.json)

初回起動で自動生成されます。直接編集することも可能です。

```json
{
  "streamers": [
    {
      "user_id": "example",
      "display_name": "サンプル配信者",
      "password": "合言葉（不要な場合は空文字）",
      "enabled": true
    }
  ],
  "account": {
    "username": ""
  },
  "output_dir": "recordings",
  "check_interval": 30,
  "ytdlp_path": "yt-dlp",
  "ffmpeg_path": "ffmpeg"
}
```

---

## トラブルシューティング

### `yt-dlp が見つかりません` と表示される

```
pip install yt-dlp
```

`yt-dlp` コマンドのパスが通っていない場合は、「詳細設定」タブの「yt-dlp パス」に絶対パスを入力してください。

### メンバーシップ限定配信が録音できない

- アカウント設定でログインしているか確認してください
- TC アカウントでのログインが失敗する場合は、**ブラウザの Cookie を貼り付ける方法（方法 B）** をお試しください
- Cookie の有効期限が切れている場合は、再度貼り付けてください

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
- Python 3.11
- yt-dlp 2024.x
