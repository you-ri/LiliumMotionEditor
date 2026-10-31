# Lilium Motion Editor (Experimental)

[English](README.md) | 日本語

Unity エディタの中でキャラクターのモーションを作るためのエディタ拡張です。

![Motion Editor の窓で、Kimodo でキーの間の動きを作っているところ](Docs/Images/demo.gif)

+ DCC ツールを行き来する必要はありません。編集するとすぐにモーションが反映されます。
+ 非破壊でゲーム中の最終的なポーズを確認しながらモーションを作成できます。
+ フルボディ IK でポーズを編集できます。
+ ゲーム用の Humanoid クリップを出力します。Animation Rigging の値やほかのプロパティも入ります。
+ キーの間の補間を曲線・直線・ステップ・AI から選べます。ループ用のサイクルキーもあります。
+ NVIDIA のモーション生成モデル Kimodo を使い、キーの間の姿勢を文章から生成します。（オプション）
+ 開発中です。将来のバージョンで仕様が変わります。

## Dependencies

+ Unity 6000.0 以降
+ Burst / Mathematics（パッケージの依存として自動で入ります）
+ Animation Rigging（任意。入っていると Animation Rigging の値を編集できます）

## Install

Package Manager の `Install package from git URL...` に次を入力します。

```
https://github.com/you-ri/LiliumMotionEditor.git?path=/Packages/jp.lilium.motioneditor
```

## How to use

1. `Window > Lilium Motion Editor > Motion Editor` を開きます。
2. Animator の付いたキャラクターの prefab を選びます。
3. ポーズを付けてキーを打ち、クリップへ保存します。

+ プロジェクト全体の設定は `Project Settings > Lilium Motion Editor` にあります。
+ 編集用リグの定義は `Create > Lilium Motion Editor > Rig Definition` で作れます。
+ 画面の言語（English / 日本語）は `Preferences > Lilium Motion Editor` の Language で選べます。既定は OS の言語に合わせます。

### Demo

Package Manager でこのパッケージを選び、Samples の `Demo` をインポートします。

デモのモデルは Quaternius の [Universal Base Characters](https://quaternius.com/packs/universalbasecharacters.html)（CC0）の男女 2 体です。`Demo/Sample/Props` には、このパッケージ用に作った両手剣（MIT）があります。持たせるには、リグエディタの **持ち物を足す** に `TwoHandedSword.fbx` を入れます。剣をドラッグすると、両手と体がついてきます。

![Motion Editor の窓で、フルボディ IK で両手剣のポーズを付けているところ](Docs/Images/two-handed-sword.gif)

`Demo/Poses` には手の形の姿勢（Fist・Open・Point など 8 つ）が入っています。PoseBank に出すには、`Project Settings > Lilium Motion Editor` の PoseBank Folders に、`{prefabFolder}/Poses` と、インポートした `Demo/Poses` のフォルダ（例 `Assets/Samples/Lilium Motion Editor/<version>/Demo/Poses`）を足します。Humanoid の指の値なので、指の骨のある Humanoid のキャラクターならそのまま貼れます。

### Kimodo（オプション）

Samples の Kimodo (オプション) を使うと、NVIDIA のモーション生成モデル [Kimodo](https://github.com/nv-tlabs/kimodo) に、打ったキーの間の動きを文章から生成できます。
Kimodo 自体は同梱していませんが、セットアップ機能を用意しています。ライセンスを確かめたうえで、各自実行してください。モーションエディタ自体は Kimodo が無くても動きます。

Kimodo を動かすのに必要なもの:

+ NVIDIA の GPU（RTX 20 シリーズ以降。ビデオメモリは約 1.5 GB 使います）
+ 空きメモリ約 16 GB（文章を読む部分を CPU で動かすため）
+ ディスク約 25 GB（Python の環境とAIモデル）
+ Hugging Face のアカウントと、Llama 3（`meta-llama/Meta-Llama-3-8B-Instruct`）の利用許可
+ Windows 11・RTX 2070 で動くことを確かめています

## License

[MIT](LICENSE)

アイコンには Google の [Material Icons](https://github.com/google/material-design-icons)（Apache License 2.0）を、デモのモデルには Quaternius の Universal Base Characters（CC0）を使っています。詳しくはパッケージの `Third-Party Notices.txt` を見てください。
