---
name: sync-to-extrival
description: このリポジトリの jp.lilium.motioneditor（正）を extrival（C:\Work\Uracrasta\extrival）の Packages/jp.lilium.motioneditor へ写す。ユーザーが「extrival に反映して」「extrival へ写して」「sync-to-extrival」と指示したときだけ使う。
disable-model-invocation: true
---

# sync-to-extrival

このリポジトリの `Packages/jp.lilium.motioneditor` を extrival の `Packages/jp.lilium.motioneditor` へ写す。

- **ユーザーが指示したときだけ行う。** パッケージを直し終えても、自分から写さない（2026-10-05 に決めた）
- **コミットしない。** extrival 側もこのリポジトリも、コミットはユーザーが指示したときだけ。するなら別々に
- パッケージはこのリポジトリが正（2026-10-05 から）。extrival は写して使う側

## 使い方

```
/sync-to-extrival
```

引数は取らない。

## 手順

写すのは extrival の `Scripts/sync-lilium-package.ps1`（`-Package motioneditor` は公開リポジトリ → extrival の向き）。

```bash
S=C:/Work/Uracrasta/extrival/Scripts/sync-lilium-package.ps1
X=C:/Work/Uracrasta/extrival; P=Packages/jp.lilium.motioneditor
```

1. 写す元（このリポジトリ）を確かめる
   - `git status --short -- $P`。未コミットの変更があっても写せる（作業コピーをそのまま写す）。何が入るかは報告で触れる
2. 写す先（extrival）の未コミットの変更を確かめる
   ```bash
   git -C $X status --short -- $P
   ```
   - 何も無ければ 3 へ
   - あれば、それが**このリポジトリの作業コピーと同じ中身か**を見る（前に写した物がまだコミットされていないだけ、がよくある）。
     3 の DryRun を `-Force` 付きで流し、追加・更新・削除が 0 なら同じ中身。写す物も無いので、そのまま 5 へ
   - 違う中身があれば、**extrival 側で直接直した変更**かもしれない。写すと上書きで消える。
     どのファイルがどう違うかを示して、ユーザーに聞く（こちらへ取り込むか、捨てて上書きするか）。勝手に `-Force` で写さない
3. DryRun で写す内容を出す
   ```bash
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$S" -Package motioneditor -DryRun
   ```
   - 1 行目の `写す向き:` が `C:/Work/Lilium/LiliumMotionEditor -> ...extrival` であること
   - **削除**に `Samples~/Demo/Sample/Rage`・`1H_Hero_singe_animations`・`Robot Kyle` が出たらおかしい（スクリプトの除外で守っているはず）。止めてユーザーに伝える
4. 写す（2 で extrival が clean だったとき。同じ中身と確かめた・ユーザーが上書きを認めたときだけ `-Force`）
   ```bash
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$S" -Package motioneditor
   ```
5. 確かめる
   ```bash
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$S" -Package motioneditor -DryRun -Force   # 追加・更新・削除が 0
   git -C $X status --short -- $P
   ```

## 気を付けること

- **差し込み口（`IContextHook` / `IHumanoidBakeHook` / `IPoseGraphHook`、`[PoseLayer]` など）の形が変わっていたら、extrival のゲーム側がコンパイルできなくなる。**
  写す差分にこれらの変更があれば、報告で目立たせる。extrival 側の対応はユーザーと相談する
- extrival は Unity 6000.6.2f1。6.6 にしか無い API の分け方（`#if UNITY_6000_6_OR_NEWER`）は extrival 側で効く方なので、そちらでコンパイルが通るかは extrival を開いて確かめる（このスキルでは Unity を動かさない）
- パッケージ外（`Assets/`・`ProjectSettings/`・README・`Docs/`）は写さない

## 終わったら

- 追加・更新・削除の数と、主なファイル（`.meta` は除く）を返す
- extrival の `git status --short -- $P` を添える
- 差し込み口の変更・2 で見つけた extrival 側だけの変更があれば最後に書く
