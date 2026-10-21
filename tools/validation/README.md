# 非公開の実ゲーム参照との照合

これらは通常のxUnit回帰テストとは別の、取得済み記録を使う比較ツールです。
ゲーム本体・参照保存・実操作ログはこのリポジトリに含みません。
出力にも非公開情報を含むため、Gitへ追加しないでください。

## 前提と実行

1. `docs/v111.md`に従いローカルの`lib/`を用意し、`addons/public-observation/public-observation.sln`をReleaseビルドする。
2. 参照記録のコーパスJSONと、その相対パスの基準ディレクトリを用意する。
3. 存在しない出力ディレクトリを指定して次を実行する。

```bash
python3 tools/validation/verify_references.py \
  --corpus /absolute/path/to/acceptance-corpus.json \
  --input-root /absolute/path/to/reference-root \
  --output .private/reference-check
```

`LEARN_DOTNET`でdotnetの実行ファイルを指定できます。
参照入力とゲームDLLのSHA256を検査してから、新規開始の経路を2回、保存経路を1回再生します。
`summary.json`の全項目がpassedであることと終了コード0を確認してください。
参照入力のない環境ではこの比較を実行できません。通常の回帰テストは実保存なしで実行できます。

## コーパスの構造

`gameSha256`、`fresh`、`saved`を持つJSONです。各capture/saveは
`{"path":"基準ディレクトリからの相対パス","sha256":"ファイルのSHA256"}`。
`fresh`の各要素は`case`と`capture`、`saved`の各要素は`id`、`capture`、`save`、
`nativeActions`、`fixtureHost`、`publicRecorder`を持ちます。
freshのcase番号は1〜6を想定しています。

captureは`seed`と`actions`を持ち、各操作には`index`、`chosen`、
`before.observation`、`after.observation`が必要です。
新規開始のcaptureには`final.observation`も必要です。
取得に用いた外部ブリッジの版と、保存作成時の介入は別途来歴として保管してください。

`FixtureHost`は強制撃破を含む非通常記録の再生専用です。
通常の方策入力・学習データ生成には使いません。通常の公開ホストに強制撃破APIはありません。

比較するのは、選択した経路の勝敗、Act・階、HP・所持金・所持品、
戦闘中の手札・山札枚数・敵の状態・予告等です。
画面を閉じる確認は対応操作として記録します。全合法手集合や全カード修飾の同値は保証しません。
公開記録器では表示量・使用可否・終端を比較し、禁止キー、行動と結果の対応、未解決行動も検査します。
