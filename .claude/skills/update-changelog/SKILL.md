---
name: update-changelog
description: jp.lilium.motioneditor の CHANGELOG.md に、今の版のエントリを差分から書く（前回チェックした位置以降の変更を拾う）。LiveStudio と同じ Keep a Changelog 形式・日本語。「CHANGELOG を更新して」「変更履歴を書いて」「update-changelog」等の依頼や、版を上げた直後に使う。
---

# update-changelog

`Packages/jp.lilium.motioneditor/CHANGELOG.md` に、今の版のエントリを差分から書く。形式は LiveStudio（`C:\Work\Lilium\Virgo\.claude\skills\update-changelog`）と同じ Keep a Changelog。ただし本文は日本語。

- **版は上げない。** 版の見出しは `package.json` の `version` をそのまま使う。版を上げるのはユーザーが指示したときだけ（CLAUDE.md）
- **コミットしない。** ユーザーが確かめてからコミットする

## 使い方

```
/update-changelog
```

引数は取らない。版は `Packages/jp.lilium.motioneditor/package.json` の `version` から読む。

## どちらのファイルを書くか

パッケージは extrival（`C:\Work\Uracrasta\extrival`）が正。**extrival 側を書いてから、このリポジトリへ同じ中身を写す**。

```bash
E=C:/Work/Uracrasta/extrival/Packages/jp.lilium.motioneditor/CHANGELOG.md
cp "$E" Packages/jp.lilium.motioneditor/CHANGELOG.md
cmp "$E" Packages/jp.lilium.motioneditor/CHANGELOG.md && echo same
```

- 写すスクリプト（`sync-lilium-package.ps1`）は、写す先に未コミットの変更があると止まるので、CHANGELOG だけなら `cp` で写す
- 書く前に両方を比べる（`diff <(tr -d '\r' < A) <(tr -d '\r' < B)`）。こちらだけ行が欠けている・違うときは、extrival 側をもとにしてユーザーに伝える
- 改行は LF（`.gitattributes`）。Write で書けば LF になる

## 書き方

```markdown
# Changelog

## [0.1.1-exp.1] - 2026-10-04
<!-- changelog-sha: <extrival の HEAD の 40 文字> -->

### Added

- **大きな機能は、結果を 1 文で太字にする。** 続けて、使い方・設定の場所・既定値など

- 小さな変更は太字にしない（`API 名` などの補足は括弧で）

### Changed

- ...

### Removed

- ...

### Fixed

- ...

## [0.1.0] - 2026-10-04

- 初期コミット
```

- 先頭は `# Changelog`。版は新しい順に上から並べる
- 版の見出しは `## [<package.json の version>] - YYYY-MM-DD`。`-exp.N` などの後ろも付けたまま（LiveStudio は外すが、こちらは付ける）。日付は書いた日
- 見出しのすぐ下の行に `<!-- changelog-sha: <sha> -->`（下の「前回の位置の記録」）
- 分類は Added / Changed / Deprecated / Removed / Fixed / Security の順。変更の無い分類の見出しは書かない
- 項目と項目の間は空行を 1 つ空ける（LiveStudio と同じ）
- **入れ子の箇条書きにしない。** 1 項目 = 1 課題。同じ課題を何回かに分けて直したなら 1 項目にまとめ、1 回の変更で別の課題も直したなら項目を分ける
- **先頭に結果を書く**（何ができるようになったか・何が直ったか）。どう作ったかは、読む人に要るときだけ後ろに続ける。なぜそう作ったかは書かない（コミットメッセージに書く）
- 画面に出る名前（ボタン・メニュー・窓）は、今の表示に合わせる。日本語の表示は `Editor/Localization/MotionEditorLocales/ja.json` で確かめる
- 名前は今のものを使う（古い名前は「以前は〜」と書くときだけ）
- **公開するので、ゲーム固有の話は書かない**（ゲーム名・キャラ名・ゲーム側のクラスやパイプライン・チケット）。キャラは「キャラ A」などのラベルで書く（CLAUDE.md の仕様書と同じ）
- 内部の作り替え・テストだけの変更は書かない。分からないことは推し量って書かず、`- TODO: ...` で残してユーザーに聞く

## `[Unreleased]` があるとき

- `## [Unreleased]` を `## [<version>] - YYYY-MM-DD` に変え、すぐ下に `changelog-sha` を足す。中身は今の版の項目として残し、上の形にそろえる
- 書いた後に新しい空の `[Unreleased]` は作らない
- 同じ版の見出しがもうあるときは、見出しを重ねて作らない。足りない項目だけを足し、`changelog-sha` を今の HEAD に書き換える

## 前回の位置の記録（changelog-sha）

- 入れるのは **extrival の HEAD**（`git -C C:/Work/Uracrasta/extrival rev-parse HEAD` の 40 文字）。パッケージを作っているのは extrival なので、次に比べるのも extrival の履歴
- HTML のコメントなので、表示にも Unity の取り込みにも出ない

## 差分の拾い方

1. 起点を決める
   - 一番上の版の `changelog-sha` があれば、それを起点にする
   - 無ければ、ひとつ下の版の `changelog-sha`。それも無ければ、このリポジトリでその版を出したコミット（`[0.1.0]` は Initial commit `6fa3121`。比べるのはこのリポジトリのパッケージ）
2. 起点から**今の作業コピー**までを見る。extrival ではコミットしていない変更が多いので、`<sha>..HEAD` ではなく作業コピーと比べ、追跡していないファイルも見る
   ```bash
   X=C:/Work/Uracrasta/extrival; P=Packages/jp.lilium.motioneditor
   git -C $X log --oneline <sha>..HEAD -- $P
   git -C $X diff --stat <sha> -- $P
   git -C $X status --short -- $P      # ?? は新しいファイル、D は消したファイル
   ```
3. 意味のある変更だけを読む。次は除く: `.meta`、`package.json` の version の行、CHANGELOG そのもの、改行だけの差
   - 画面の文字を翻訳のキー（`Tr ("...")`・`Loc.`）へ移しただけの行が多いときは、日本語の文字・`Tr (`・`MotionEditorLocalization` を含む行を除いて残りを見る（`LC_ALL=C.UTF-8` で `grep -P "[\x{3040}-\x{30ff}\x{4e00}-\x{9fff}]"`）
   - 見るとよい変更: `[MenuItem]`・`[Shortcut]` の追加・変更、public の型やメソッドの追加・削除、ファイルの追加・削除、設定の項目、メニュー・ボタンの増減
4. 今の `[Unreleased]` や同じ版の項目にもう書いてあるものは重ねて書かない。書いていない変更だけを足す
5. 分類する
   - 「〜できるようにした」「〜を追加」→ Added
   - 「〜にした」「〜へ移した」「名前を変えた」→ Changed
   - 「〜を削除」→ Removed
   - 「〜を修正」「〜を直した」→ Fixed

## 終わったら

- 両方のファイルが同じことを `cmp` で確かめる
- 足した項目・入れた `changelog-sha`・書いた版の見出しを表で返す。差分から拾って足した項目（元の CHANGELOG に無かったもの）は分けて示す
- コミットはしない。extrival 側とこのリポジトリは、コミットするなら別々に（CLAUDE.md）
