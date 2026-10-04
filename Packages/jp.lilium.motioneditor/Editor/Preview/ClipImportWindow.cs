using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.UIElements;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 取込の候補の一覧（Editing Rig の段の「取込」）。プロジェクトの Humanoid のクリップを名前で絞り込んで選ぶ。
    /// 選んだクリップは Motion Editor に土台として開いて見せるので、タイムラインで好きなコマを見られる（ほかの窓を触っても閉じない）。
    /// 「取り込む」（一覧のダブルクリック・Enter）で編集用クリップへ書き出して開き、「やめる」・閉じると元のクリップへ戻す
    /// </summary>
    sealed class ClipImportWindow : EditorWindow
    {
        const int kRowHeight = 18;

        [SerializeField] PreviewWindow owner_;
        [SerializeField] string search_ = "";

        struct Candidate
        {
            public AnimationClip clip;
            public string path;
            /// <summary>絞り込みで比べる文字（小文字の名前とパス）</summary>
            public string key;
        }

        // 候補はクリップを全部読んで Humanoid かを見るので重い（千本台で 1 秒ほど）。開いている間は使い回し、プロジェクトが変わったら作り直す
        static List<Candidate> candidates_;
        readonly List<Candidate> rows_ = new List<Candidate> ();

        ToolbarSearchField searchField_;
        ListView list_;
        Label count_;
        Label info_;
        Button commit_;
        bool closing_;

        public static void Open (PreviewWindow owner)
        {
            ClipImportWindow window = GetWindow<ClipImportWindow> (true, "Import Humanoid Clip");
            if (window.owner_ != null && window.owner_ != owner) window.owner_.CancelImportPreview ();
            window.SetOwner (owner);
            window.minSize = new Vector2 (320, 300);
            window.Focus ();
        }

        void SetOwner (PreviewWindow owner)
        {
            if (owner_ != null) owner_.stateChanged -= RefreshInfo;
            owner_ = owner;
            if (owner_ == null) return;
            owner_.BeginImportPreview ();
            owner_.stateChanged -= RefreshInfo;
            owner_.stateChanged += RefreshInfo;
            RefreshInfo ();
        }

        void OnEnable ()
        {
            EditorApplication.projectChanged += OnProjectChanged;
            // スクリプトの再読み込みの後も試し見を続ける
            if (owner_ != null) {
                owner_.stateChanged -= RefreshInfo;
                owner_.stateChanged += RefreshInfo;
            }
        }

        void OnDisable ()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
            if (owner_ != null) owner_.stateChanged -= RefreshInfo;
        }

        void OnDestroy ()
        {
            // 取り込まずに閉じたら、元のクリップへ戻す
            if (owner_ != null && owner_.importPreviewing) owner_.CancelImportPreview ();
        }

        void OnInspectorUpdate ()
        {
            // Motion Editor が閉じられたら用が無い
            if (owner_ == null && !closing_) {
                closing_ = true;
                Close ();
            }
        }

        void OnProjectChanged ()
        {
            candidates_ = null;
            Filter ();
        }

        void CreateGUI ()
        {
            VisualElement root = rootVisualElement;
            root.style.paddingLeft = 4;
            root.style.paddingRight = 4;
            root.style.paddingTop = 4;
            root.style.paddingBottom = 4;

            searchField_ = new ToolbarSearchField { tooltip = Tr ("CLIP_IMPORT_WINDOW_SEARCH_TOOLTIP") };
            searchField_.style.width = StyleKeyword.Auto;
            searchField_.style.flexShrink = 0;
            searchField_.SetValueWithoutNotify (search_);
            searchField_.RegisterValueChangedCallback (e => {
                search_ = e.newValue ?? "";
                Filter ();
            });
            root.Add (searchField_);

            count_ = new Label ();
            count_.style.fontSize = 10;
            count_.style.color = new Color (0.65f, 0.65f, 0.65f);
            count_.style.flexShrink = 0;
            root.Add (count_);

            list_ = new ListView {
                fixedItemHeight = kRowHeight,
                selectionType = SelectionType.Single,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = rows_,
            };
            // 一覧だけが余った高さに収まる（中身の高さを基準にすると、多いとき下の説明とボタンが押しつぶされる）
            list_.style.flexGrow = 1;
            list_.style.flexShrink = 1;
            list_.style.flexBasis = 0;
            list_.style.minHeight = kRowHeight * 3;
            list_.selectionChanged += OnSelectionChanged;
            // ダブルクリック・Enter で取り込む
            list_.itemsChosen += chosen => Commit ();
            root.Add (list_);

            info_ = new Label ();
            info_.style.whiteSpace = WhiteSpace.Normal;
            info_.style.fontSize = 10;
            info_.style.marginTop = 2;
            info_.style.flexShrink = 0;
            root.Add (info_);

            VisualElement buttons = new VisualElement ();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.marginTop = 4;
            buttons.style.flexShrink = 0;
            buttons.style.minHeight = 22;
            commit_ = new Button (Commit) { text = Tr ("CLIP_IMPORT_WINDOW_IMPORT"), tooltip = Tr ("CLIP_IMPORT_WINDOW_IMPORT_TOOLTIP") };
            commit_.style.flexGrow = 1;
            buttons.Add (commit_);
            Button cancel = new Button (Cancel) { text = Tr ("CLIP_IMPORT_WINDOW_CANCEL"), tooltip = Tr ("CLIP_IMPORT_WINDOW_CANCEL_TOOLTIP") };
            cancel.style.flexGrow = 1;
            buttons.Add (cancel);
            root.Add (buttons);

            Filter ();
            RefreshInfo ();
            searchField_.schedule.Execute (() => searchField_.Focus ());
        }

        static VisualElement MakeRow ()
        {
            VisualElement row = new VisualElement ();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            Label name = new Label { name = "name" };
            name.style.flexShrink = 1;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            row.Add (name);
            Label file = new Label { name = "file" };
            file.style.flexGrow = 1;
            file.style.flexShrink = 1;
            file.style.marginLeft = 6;
            file.style.fontSize = 10;
            file.style.color = new Color (0.6f, 0.6f, 0.6f);
            file.style.overflow = Overflow.Hidden;
            file.style.textOverflow = TextOverflow.Ellipsis;
            row.Add (file);
            return row;
        }

        void BindRow (VisualElement element, int index)
        {
            if (index < 0 || index >= rows_.Count) return;
            Candidate candidate = rows_[index];
            element.Q<Label> ("name").text = candidate.clip != null ? candidate.clip.name : Tr ("CLIP_IMPORT_WINDOW_MISSING_CLIP");
            element.Q<Label> ("file").text = Path.GetFileName (candidate.path);
            element.tooltip = candidate.path;
        }

        /// <summary>
        /// プロジェクトの Humanoid のクリップを集める（.anim と、モデルの中のクリップ）
        /// </summary>
        static List<Candidate> Collect ()
        {
            List<Candidate> result = new List<Candidate> ();
            HashSet<string> seen = new HashSet<string> ();
            foreach (string guid in AssetDatabase.FindAssets ("t:AnimationClip", new[] { "Assets" })) {
                string path = AssetDatabase.GUIDToAssetPath (guid);
                if (!seen.Add (path)) continue;
                // AnimatorController・Timeline などはクリップを持っているだけ（元のファイルは別に拾う）
                if (!(AssetImporter.GetAtPath (path) is ModelImporter) && !path.EndsWith (".anim", System.StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath (path)) {
                    AnimationClip clip = asset as AnimationClip;
                    if (clip == null || !clip.humanMotion) continue;
                    // モデルの取り込みが作るプレビュー用の複製
                    if (clip.name.StartsWith ("__preview__")) continue;
                    result.Add (new Candidate { clip = clip, path = path, key = (clip.name + " " + path).ToLowerInvariant () });
                }
            }
            result.Sort ((a, b) => {
                int byPath = string.CompareOrdinal (a.path, b.path);
                return byPath != 0 ? byPath : string.CompareOrdinal (a.clip.name, b.clip.name);
            });
            return result;
        }

        void Filter ()
        {
            if (list_ == null) return;
            if (candidates_ == null) candidates_ = Collect ();
            string[] words = search_.ToLowerInvariant ().Split (new[] { ' ', '　' }, System.StringSplitOptions.RemoveEmptyEntries); // noloc: 全角空白も語の区切りにする（表示しない）
            AnimationClip current = owner_ != null && owner_.importPreviewing ? owner_.clip : null;
            rows_.Clear ();
            int selected = -1;
            foreach (Candidate candidate in candidates_) {
                bool match = true;
                foreach (string word in words) {
                    if (candidate.key.IndexOf (word, System.StringComparison.Ordinal) < 0) {
                        match = false;
                        break;
                    }
                }
                if (!match) continue;
                if (candidate.clip == current) selected = rows_.Count;
                rows_.Add (candidate);
            }
            list_.RefreshItems ();
            if (selected >= 0) {
                list_.SetSelectionWithoutNotify (new[] { selected });
                list_.ScrollToItem (selected);
            }
            else {
                list_.ClearSelection ();
            }
            count_.text = Tr ("CLIP_IMPORT_WINDOW_COUNT", rows_.Count, candidates_.Count);
        }

        void OnSelectionChanged (IEnumerable<object> selected)
        {
            int index = list_.selectedIndex;
            if (owner_ == null || index < 0 || index >= rows_.Count || rows_[index].clip == null) return;
            owner_.PreviewImportClip (rows_[index].clip);
        }

        void RefreshInfo ()
        {
            if (info_ == null) return;
            if (owner_ == null) {
                info_.text = Tr ("CLIP_IMPORT_WINDOW_EDITOR_CLOSED");
                commit_.SetEnabled (false);
                return;
            }
            AnimationClip clip = owner_.importPreviewing && owner_.useHumanoidBase ? owner_.clip : null;
            commit_.SetEnabled (clip != null);
            if (clip == null) {
                info_.text = Tr ("CLIP_IMPORT_WINDOW_HINT");
                return;
            }
            int frames = Mathf.RoundToInt (clip.length * clip.frameRate);
            info_.text = Tr ("CLIP_IMPORT_WINDOW_CLIP_INFO", clip.name, frames, clip.length.ToString ("0.00"), clip.frameRate.ToString ("0"), AssetDatabase.GetAssetPath (clip));
        }

        void Commit ()
        {
            if (owner_ == null || !owner_.CommitImportPreview ()) {
                RefreshInfo ();
                return;
            }
            closing_ = true;
            Close ();
        }

        void Cancel ()
        {
            if (owner_ != null) owner_.CancelImportPreview ();
            closing_ = true;
            Close ();
        }
    }

}
