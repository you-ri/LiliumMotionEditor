# Changelog

## [Unreleased]

### Added
- **全身 IK の点に「引く強さ」（`pullStrength`。0 か 1）を足した。** 0 の点は、乗っている骨を付け根まわりに回して点を目標の方へ向けるだけで、ほかの骨を引かない（顔の前の点を動かしても首・胴が引かれず、ほかの点に引かれて胴が動いても頭は付いて行きつつ目標を向く）。1 は今までどおり骨ごと引く。リグ定義の点の一覧で決める。同じ骨に 0 の点が 2 つあれば、後ろの点（頭のてっぺん）は前の点（顔の前）の向きまわりのかしげだけを決める。骨の付け根そのものの点は 0 にできない（定義チェックで知らせる）。ソルバーでは `FullBodyIkEffector.aim`
- 表示域で選ぶとき、Shift を押していれば今の選択に足し、Ctrl（Mac は Command）を押していれば押した物の子階層（表示モデルの骨の階層で、その骨より先に付いている物。骨の付け根の点・骨なら同じ骨の上の物も）をまとめて選ぶ。ハンドル・メッシュのクリック・全身 IK の点の枠選択・Motion Editor In Scene で効く。選べるのは今ハンドルを出している物だけ。まとめて選んだ物は、押したまま動かしても選択が保たれる

### Changed
- 新しく作るリグ定義とパッケージの既定の定義（`Default Rig Definition.asset`）では、向きの点（`HipsDirection`・`UpperChestDirection`・`HeadDirection`・`HeadTop`）の引く強さを 0 にした。**既定の定義を使うクリップで向きの点を固定してある所は、解いた姿勢が変わる**（頭・胸・腰が向くだけになり、体を引かない）。自分で作った定義アセットは 1 のまま（項目の無い定義は 1 で読む）
- 全身 IK の点を 1 つ選んで回転のギズモで回したとき、その骨と子の骨にある固定した点（Pin・Locked。同じ骨の上の点も）を一緒に回すのをやめた。移動と同じく、固定した点はほかの点の操作に連動せず、置いた場所と向きのまま（全身 IK が手足を曲げて届かせる）。Free の点は今までどおり骨に付いて回る
- 全身 IK の点の枠選択で、Ctrl を押していても今の選択に足していたのをやめた。足すのは Shift だけで、Ctrl は枠の中の点の子階層も選ぶ

### Fixed
- ✋ が全身 IK の段のとき（点をつかむ間）、指など点の無い骨の FK のハンドルまで隠れていて、見えず動かせなかったのを直した。どの骨を出すかはリグ定義の FK の骨ごとの `fullBodyHandle` で決める（既定は全身 IK で解かない骨 = 指・目・顎。追加のコントロールは既定で出す）。出している骨はメッシュのクリックでも選べる。定義の版を 4 に上げ、版 3 までの定義は読み込むときに今の定義で解かない骨を出すようにする

### Changed
- パネルのアクセントの色（Layers の入っているボタン・Transform のモードと今の行・Stacker の今のキー・タイムラインの選んだキーと枠選択・演出で使われている区間）を、自前の水色から Unity のエディタのテーマの選択の色（USS 変数 `--unity-colors-highlight-background`）に替えた。明るいテーマ・暗いテーマに合わせて替わる（`AccentStyle`・`Editor/Preview/MotionEditor.uss`）
- Transform のパネルから Mirror ボタンと、何も選んでいないときの「骨を選んでください」を外した（窓の `MirrorSpinValues` は残す）

- Layers の段と段の間の矢印を Material Icons の矢印にし、逆に通れるか（そのまま戻せる・ずれる・戻せない）の言葉は ↑ の矢印のツールチップに移した（矢印の色は今までどおり）。つかむ段から書き出す段への経路では、残差だけを矢印の横に出す

## [0.1.1-exp.1] - 2026-10-04
<!-- changelog-sha: e5696fb037f9e7db9054e011600b7974bc3b015c -->

### Added

- **画面の文字を英語と日本語に対応した。** 言語は `Preferences > Lilium Motion Editor` の Language で選ぶ（この PC の設定。既定は OS の言語で、日本語なら日本語・それ以外は英語）。切り替えるとスクリプトを読み直して窓を作り直す。文字はキーで引き、`Editor/Localization/MotionEditorLocales/en.json`・`ja.json` に持つ（今の言語に無ければ英語、それも無ければキーのまま）。インスペクタの説明（持ち替えの定義・キャラの設定の `[Tooltip]`）は英語にした（パッケージの依存は増やしていない）

- 今のフレームの全部にキーを打つ Key All（Stacker のボタン）をショートカット K で押せるようにした（モーションエディタの窓と Motion Editor In Scene。Shortcut Manager の `Lilium Motion Editor/Key All`）

### Changed

- Window メニューの項目を `Window > Lilium Motion Editor` の下にまとめた（`Motion Editor` / `Motion Editor In Scene (Experimental)`。以前は `Window > Motion Editor (Preview)` / `Window > Motion Editor (Scene)`）

- タイムラインのキーの箱から通し番号を外し、箱の幅をそろえた。キーの間のフレーム数は、間の線を切ってその中に出す

- タイムラインの右クリックメニューから、キーのコピー・貼り付けと「Go to Frame」「Delete Key」を外した。コピー・貼り付けは Ctrl+C / Ctrl+V、押したキーは選ばれるので消すのは「キーを削除」で行う

### Removed

- 使っていない古いショートカットの説明の窓 `HelpWindow` を削除した

## [0.1.0] - 2026-10-04

- 初期コミット
