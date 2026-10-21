# 第三者コードとゲーム入力

learn-the-spireの既存コードはルートのLICENSEにあるUnlicenseで提供されています。

`addons/public-observation/shared/CardProjection.cs`と`addons/public-observation/shared/PublicData.cs`は、
Jtshieh/STS2-ToolsのMac公開観測コードとそのローカル拡張に由来します。
この部分にはMITライセンスが適用されます。
Copyright (c) 2026 Jtshieh and contributors。
全文は`licenses/STS2-Tools.txt`に保持しています。
`CardProjection`の山札向け識別子の扱いはこのforkで変更しています。
観測コードの上流来歴で言及されているMIT通知も、
`licenses/RunReplays.txt`と`licenses/divine-sts2.txt`に保持しています。

ゲーム本体・ゲームDLL・素材・逆コンパイル出力・実保存・実プレイログは、
このリポジトリの配布物に含みません。ユーザー自身のゲームからローカルに用意します。
GodotやHarmony等の実行時依存もゲーム側のローカル入力であり、同梱しません。
このforkはMega Critの公式製品でも、同社と提携した製品でもありません。

実ゲーム参照の取得に使った外部ブリッジや受動記録器は、このリポジトリには含めていません。
それらを別途利用・再配布する場合は、各プロジェクトのライセンス通知を保持してください。
