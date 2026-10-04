# Changelog

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
