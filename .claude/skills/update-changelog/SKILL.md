---
name: update-changelog
description: jp.lilium.motioneditor の CHANGELOG.md の `## [Unreleased]` に、差分から項目を書く（前回チェックした位置以降の変更を拾う）。LiveStudio と同じ Keep a Changelog 形式・日本語。「CHANGELOG を更新して」「変更履歴を書いて」「update-changelog」等の依頼や、ユーザーが版の確定を指示したときに使う。
---

# update-changelog

`Packages/jp.lilium.motioneditor/CHANGELOG.md` の `## [Unreleased]` に、差分から項目を書く。形式は LiveStudio（`C:\Work\Lilium\Virgo\.claude\skills\update-changelog`）と同じ Keep a Changelog。ただし本文は日本語。

- **いつも `## [Unreleased]` に書く。** 版の見出しにする（版を確定する）のは、ユーザーが指示したときだけ（下の「版を確定するとき」。CLAUDE.md）
- **版は上げない。** `package.json` の `version` を変えるのも、ユーザーが指示したときだけ
- **コミットしない。** ユーザーが確かめてからコミットする

## 使い方

```
/update-changelog
```

引数は取らない。版の確定を指示されたときだけ、版を `Packages/jp.lilium.motioneditor/package.json` の `version` から読む。

## どちらのファイルを書くか

パッケージはこのリポジトリが正（2026-10-05 から。それまでは extrival が正だった）。**このリポジトリの CHANGELOG だけを書く。**
extrival へは写さない。写すのはユーザーが `/sync-to-extrival` を指示したとき。

- 改行は LF（`.gitattributes`）。Write で書けば LF になる

## 書き方

```markdown
# Changelog

## [Unreleased]
<!-- changelog-sha: <このリポジトリの HEAD の 40 文字> -->

### Added

- 結果を短く 1 文で書く。要るときだけ、使い方・設定の場所・前と変わることを 1〜2 文で続けます。

- 小さな変更は 1 文だけ（`API 名` などの補足は括弧で）。

### Changed

- ...

### Removed

- ...

### Fixed

- ...

## [0.1.1-exp.1] - 2026-10-04
<!-- changelog-sha: <確定したときの HEAD> -->

### Added

- ...

## [0.1.0] - 2026-10-04

- 初期コミット
```

- 先頭は `# Changelog`。一番上が `## [Unreleased]`、その下に確定した版を新しい順に並べる
- `[Unreleased]` が無ければ、`# Changelog` のすぐ下に作る
- `[Unreleased]` と版の見出しのすぐ下の行に `<!-- changelog-sha: <sha> -->`（下の「前回の位置の記録」）
- 分類は Added / Changed / Deprecated / Removed / Fixed / Security の順。変更の無い分類の見出しは書かない
- 項目と項目の間は空行を 1 つ空ける（LiveStudio と同じ）
- **入れ子の箇条書きにしない。** 1 項目 = 1 課題。同じ課題を何回かに分けて直したなら 1 項目にまとめ、1 回の変更で別の課題も直したなら項目を分ける
- **最初の 1 文は結果だけを短く書く**（何ができるようになったか・何が直ったか）。条件・対象の一覧・数値・括弧の補足は最初の 1 文に入れない。込み入ったものだけ、2 文目に使い方・設定の場所・前と変わること（キーが当たらなくなる・姿勢が変わるなど）を短く足す。詳しさは 1〜2 文まで。細かい挙動・数値・内部の名前（クラス・USS・.meta の持ち方）は書かない。なぜそう作ったかは書かない（コミットメッセージに書く）
- **最初の 1 文は常体（「〜を追加した。」「〜を直した。」）、2 文目からの詳しい説明はですます調**（「〜を右クリックして入れます。」）。どの項目も最後は「。」で終える
- **項目の文を太字（`**`）にしない。** 大きな変更も、最初の 1 文を短く書くことで目立たせる
- **絵文字は使わない**（UI に絵文字のボタンがあっても、画面の名前（「つかむ段」など）や言葉で書く）
- 画面に出る名前（ボタン・メニュー・窓）は、今の表示に合わせる。日本語の表示は `Editor/Localization/MotionEditorLocales/ja.json` で確かめる
- 名前は今のものを使う（古い名前は「以前は〜」と書くときだけ）
- **公開するので、ゲーム固有の話は書かない**（ゲーム名・キャラ名・ゲーム側のクラスやパイプライン・チケット）。キャラは「キャラ A」などのラベルで書く（CLAUDE.md の仕様書と同じ）
- 内部の作り替え・テストだけの変更は書かない。分からないことは推し量って書かず、`- TODO: ...` で残してユーザーに聞く

## `[Unreleased]` に書くとき（ふだん）

- 差分から拾った項目を `[Unreleased]` に足し、`changelog-sha` を今の HEAD に書き換える（無ければ見出しのすぐ下に足す）
- もとからある項目も上の形にそろえる（分類の順・空行・同じ分類の見出しが 2 つあればまとめる）。中身の書き換えは、今の名前に合わせるなど要るときだけ
- 確定した版の項目は書き換えない

## 版を確定するとき（ユーザーが指示したときだけ）

1. まず上の「ふだん」の手順で、`[Unreleased]` を今の HEAD まで書き足す
2. `## [Unreleased]` を `## [<package.json の version>] - YYYY-MM-DD` に変える。`changelog-sha` はそのまま見出しの下に残る。`-exp.N` などの後ろも付けたまま（LiveStudio は外すが、こちらは付ける）。日付は確定した日
3. 新しい空の `[Unreleased]` は作らない（次に書くときに作る）

- `package.json` の `version` は変えない。版を上げるのも指示されたときだけで、上げるならそれを先に済ませてから確定する
- 同じ版の見出しがもうある（版を上げていない）ときは、確定せずにユーザーに聞く

## 前回の位置の記録（changelog-sha）

- 入れるのは **このリポジトリの HEAD**（`git rev-parse HEAD` の 40 文字）
- `0.1.1-exp.1` までの `changelog-sha` は extrival の sha（そのころは extrival が正だった）。このリポジトリに無い sha（`git cat-file -e <sha>` が失敗する）は extrival のものとして扱う（下の「差分の拾い方」）
- HTML のコメントなので、表示にも Unity の取り込みにも出ない

## 差分の拾い方

1. 起点を決める
   - `[Unreleased]` の `changelog-sha` があれば、それを起点にする
   - 無ければ、一番上の確定した版の `changelog-sha`。それも無ければ、このリポジトリでその版を出したコミット（`[0.1.0]` は Initial commit `6fa3121`。比べるのはこのリポジトリのパッケージ）
2. 起点から**今の作業コピー**までを見る。`<sha>..HEAD` ではなく作業コピーと比べ、追跡していないファイルも見る
   ```bash
   P=Packages/jp.lilium.motioneditor
   git log --oneline <sha>..HEAD -- $P
   git diff --stat <sha> -- $P
   git status --short -- $P      # ?? は新しいファイル、D は消したファイル
   ```
   - 起点が extrival の sha のとき: 向きを逆にした時点（このリポジトリの `9cf7c8b`。このとき extrival の作業コピーと同じ中身だった）までは extrival で、そこからはこのリポジトリで見る
     ```bash
     X=C:/Work/Uracrasta/extrival
     git -C $X log --oneline <sha>..HEAD -- $P; git -C $X diff --stat <sha> -- $P   # 9cf7c8b までの分（extrival の作業コピーとの差）
     git log --oneline 9cf7c8b..HEAD -- $P; git diff --stat 9cf7c8b -- $P           # それ以降
     ```
3. 意味のある変更だけを読む。次は除く: `.meta`、`package.json` の version の行、CHANGELOG そのもの、改行だけの差
   - 画面の文字を翻訳のキー（`Tr ("...")`・`Loc.`）へ移しただけの行が多いときは、日本語の文字・`Tr (`・`MotionEditorLocalization` を含む行を除いて残りを見る（`LC_ALL=C.UTF-8` で `grep -P "[\x{3040}-\x{30ff}\x{4e00}-\x{9fff}]"`）
   - 見るとよい変更: `[MenuItem]`・`[Shortcut]` の追加・変更、public の型やメソッドの追加・削除、ファイルの追加・削除、設定の項目、メニュー・ボタンの増減
4. 今の `[Unreleased]` にもう書いてあるものは重ねて書かない。書いていない変更だけを足す（`[Unreleased]` に sha が無いときは、起点より後の変更がもう書いてあることが多い）
5. 分類する
   - 「〜できるようにした」「〜を追加」→ Added
   - 「〜にした」「〜へ移した」「名前を変えた」→ Changed
   - 「〜を削除」→ Removed
   - 「〜を修正」「〜を直した」→ Fixed

## 終わったら

- 足した項目・入れた `changelog-sha`（確定したときは版の見出しも）を表で返す。差分から拾って足した項目（元の CHANGELOG に無かったもの）は分けて示す
- コミットはしない。extrival へも写さない（どちらもユーザーが指示したときだけ）
