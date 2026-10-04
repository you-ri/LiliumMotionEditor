using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo ("LiliumMotionEditor.Editor.Tests")]
// 画面の文字の翻訳（Loc）を Rigging・Timeline 対応からも使う
[assembly: InternalsVisibleTo ("LiliumMotionEditor.Rigging.Editor")]
[assembly: InternalsVisibleTo ("LiliumMotionEditor.Timeline.Editor")]
