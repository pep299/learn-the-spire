# 公開観測・操作ログのアドオン

`Sts2DllRecorder`は独自の観測JSONと操作ログを出力する実行プログラムです。
`Sts2PolicyHost`は実ゲーム記録との比較用で、元の観測を比較しやすい形へ加工します。
これらはv0.111対応のライブラリや元の`Lts2.AgentHost`を利用するための必須機能ではありません。

## ビルド・テスト

リポジトリのルートで実行します。ゲームDLLの用意は[本体の手順](../../docs/v111.md)に従います。

```bash
dotnet build addons/public-observation/public-observation.sln -c Release
dotnet test addons/public-observation/public-observation.sln -c Release --no-build
bash scripts/run-public.sh --log /absolute/path/to/new-events.jsonl
```

`LEARN_DOTNET`でdotnetの実行ファイルを指定できます。
ログ出力先は存在しないファイルを指定してください。ログ・再現用メタデータはGitへ追加しません。
入力・出力は1行1JSON、診断出力は標準エラーです。

```json
{"cmd":"reset","seed":"7KQ2M0AB9C","character":"IRONCLAD","ascension":0,"actGeneration":"seeded_all_unlocked"}
{"cmd":"observe"}
{"cmd":"step","index":0}
{"cmd":"step","cardIndices":[2]}
{"cmd":"close"}
```

この例はNew Leafのカード選択を経てマップへ進みます。
`index`は直前の`actions[].detail.nativeOptionIndex`、`cardIndices`は
`state.selection.cards`の候補番号です。同時指定はできません。
カード番号は重複せず、選択枚数のmin/maxを満たす必要があります。
`state.phase=terminal`の`state.terminal`は`victory`または`death`です。

## このアドオンの制約

公開観測の対応を確認したMac arm64 v0.111.0 DLLをハッシュで検査します。
対象SHA256は`9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4`です。
キャラクター・難易度はソロIronclad・A0です。CrystalSphereは未対応として停止します。
この制限とハッシュ検査は本体には課しません。

`seeded_all_unlocked`は全解放・チュートリアル無効・未発見Actの優先なしで
seedからActを選びます。所有者プロフィールの再現ではありません。
`Sts2PolicyHost`の新規開始も比較用にこの条件とIronclad・A0を使います。

モデルへの入力は`Sts2DllRecorder`の応答を使います。山札は順序なしで、山札内の`instance`はnullです。
比較用ホストの生応答、seed、保存、再現用メタデータ、標準エラー出力には非公開情報が含まれます。
通信エラーを敗北として成績に混ぜないでください。

```json
{"cmd":"restore","savePath":"/absolute/path/to/input.save","seed":"EXACT_SAVED_SEED"}
```

復元はseedの一致とソロIronclad・A0を要求します。戦闘途中のSnapshotは非対応で、
店内など未完了の部屋の完全な復元も保証しません。
実ゲーム記録の取得・照合手順は[比較ツール](../../tools/validation/README.md)を参照してください。
第三者コードの来歴とライセンスは[NOTICE](../../NOTICE.md)にあります。
