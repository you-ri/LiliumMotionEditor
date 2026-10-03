# Lilium Motion Editor (Experimental)

Unity エディタの中でキャラクターのモーションを作るためのエディタ拡張です。

+ キャラクターの骨を複製した「編集用リグ」を通してポーズを付け、リグの値をアニメーションクリップへ保存します。
+ FK・手足の 2 ボーン IK・全身 IK・Animation Rigging の値を扱えます。
+ 体型の違うキャラクターでも、同じ編集用リグのクリップを使い回せます。
+ 開発中です。将来のバージョンで仕様が変わります。

## Dependencies

+ Unity 6000.0 以降（6000.6 以降では、プレビュー窓の骨ハンドルが Unity のツールの仕組みに乗り、Tools オーバーレイから道具を選べます）
+ Burst / Mathematics（パッケージの依存として自動で入ります）
+ Animation Rigging（任意。入っていると Animation Rigging の値を編集できます）
+ Timeline（任意。入っていると Timeline のクリップを編集できます）

## Install

Package Manager の `Install package from git URL...` に次を入力します。

```
https://github.com/you-ri/LiliumMotionEditor.git?path=/Packages/jp.lilium.motioneditor
```

## How to use

1. `Window > Motion Editor (Preview)` を開きます。
2. Animator の付いたキャラクターの prefab を選びます。
3. ポーズを付けてキーを打ち、クリップへ保存します。

+ プロジェクト全体の設定は `Project Settings > Lilium Motion Editor` にあります。
+ 編集用リグの定義は `Create > Lilium Motion Editor > Rig Definition` で作れます。

### Demo

Package Manager でこのパッケージを選び、Samples の `Demo` をインポートします。

デモのモデルは Quaternius の [Universal Base Characters](https://quaternius.com/packs/universalbasecharacters.html)（CC0）の男女 2 体です。

`Demo/Poses` には手の形の姿勢（Fist・Open・Point など 8 つ）が入っています。PoseBank に出すには、`Project Settings > Lilium Motion Editor` の PoseBank Folders に、`{prefabFolder}/Poses` と、インポートした `Demo/Poses` のフォルダ（例 `Assets/Samples/Lilium Motion Editor/0.1.0/Demo/Poses`）を足します。Humanoid の指の値なので、指の骨のある Humanoid のキャラクターならそのまま貼れます。

## License

[MIT](LICENSE)

アイコンには Google の [Material Icons](https://github.com/google/material-design-icons)（Apache License 2.0）を、デモのモデルには Quaternius の Universal Base Characters（CC0）を使っています。詳しくはパッケージの `Third-Party Notices.txt` を見てください。
