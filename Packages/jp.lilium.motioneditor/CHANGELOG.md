# Changelog

## [Unreleased]

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
