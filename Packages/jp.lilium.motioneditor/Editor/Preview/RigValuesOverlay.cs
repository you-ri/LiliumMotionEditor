using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;

namespace Lilium
{

    /// <summary>
    /// ゲームの Rig の値（Controls/Game の代理）。Rig ごとに層と拘束の重み、ターゲットの一覧を出す。
    /// キャラの設定に物の持ち替えがあれば、先頭に親を選ぶボタンを出す（押すと今のフレームで切り替える）。
    /// 重みを動かすと今のフレームにキーが打たれる。ターゲットの名前を押すと選ぶ（表示域のハンドルで動かす）。
    /// 重みの効き目が画面に出るのは S4（Rig の段を通す）から
    /// </summary>
    [Icon (Icons.kFolder + "RigValues.png")]
    [Overlay (typeof (PreviewWindow), "mkt-rig-values", "Rig Values", defaultDisplay = false,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    class RigValuesOverlay : Overlay
    {
        PreviewWindow window_;
        IMGUIContainer container_;
        Vector2 scroll_;

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = 240;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => 240);
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return root;

            container_ = new IMGUIContainer (OnPanelGUI);
            container_.style.maxHeight = 320;
            root.Add (container_);

            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            window_.poseSampled -= Refresh;
            window_.poseSampled += Refresh;
            return root;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) {
                window_.stateChanged -= Refresh;
                window_.poseSampled -= Refresh;
            }
            base.OnWillBeDestroyed ();
        }

        void Refresh ()
        {
            if (container_ != null) container_.MarkDirtyRepaint ();
        }

        void OnPanelGUI ()
        {
            DrawParentSwitches ();
            RigProxies proxies = window_ != null ? window_.rigProxies : null;
            if (proxies == null || proxies.rigs.Count == 0) {
                EditorGUILayout.LabelField ("このキャラに有効な Rig は無い", EditorStyles.miniLabel);
                DrawNotes (proxies);
                return;
            }
            if (window_.clip == null || window_.isClipReadOnly) {
                EditorGUILayout.HelpBox ("クリップを編集できないので、動かしてもキーは打たれない", MessageType.None);
            }

            scroll_ = EditorGUILayout.BeginScrollView (scroll_);
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 120;
            foreach (RigProxies.RigEntry rig in proxies.rigs) {
                EditorGUILayout.LabelField (rig.name, EditorStyles.boldLabel);
                WeightField ("重み", rig.weight);
                foreach (RigProxies.Weight constraint in rig.constraints) {
                    // 拘束の名前は Rig からの相対（Rig と同じ GameObject なら型名）
                    string name = constraint.label.Substring (rig.name.Length + 1);
                    WeightField (name, constraint);
                }
                foreach (RigProxies.Source source in rig.sources) {
                    bool selected = window_.IsSelected (source.proxy.gameObject);
                    string name = source.label.Substring (rig.name.Length + 1);
                    if (GUILayout.Toggle (selected, "ターゲット: " + name, EditorStyles.miniButton) && !selected) {
                        window_.SelectRigSource (source);
                    }
                }
                EditorGUILayout.Space (4);
            }
            EditorGUIUtility.labelWidth = labelWidth;
            DrawNotes (proxies);
            EditorGUILayout.EndScrollView ();
        }

        /// <summary>
        /// 物の持ち替え（キャラの設定の parentSwitches）。今の親を押された状態で出し、別の親を押すと今のフレームで切り替える
        /// </summary>
        void DrawParentSwitches ()
        {
            if (window_ == null || window_.parentSwitches.Count == 0) return;
            string blocked = window_.parentSwitchBlockReason;
            foreach (ParentSwitchDefinition definition in window_.parentSwitches) {
                EditorGUILayout.LabelField ("持ち替え: " + definition.name, EditorStyles.boldLabel);
                string error;
                ParentSwitch.Bound bound = window_.BindParentSwitch (definition, out error);
                if (bound == null) {
                    EditorGUILayout.LabelField (error, EditorStyles.wordWrappedMiniLabel);
                    continue;
                }
                int current = window_.GetCurrentParent (bound);
                string[] labels = new string[definition.sources.Count];
                for (int i = 0; i < labels.Length; i++) labels[i] = definition.sources[i].label;
                using (new EditorGUI.DisabledScope (blocked != null)) {
                    int chosen = GUILayout.Toolbar (current, labels, EditorStyles.miniButton);
                    if (chosen != current && chosen >= 0) window_.SwitchParent (bound, chosen);
                }
            }
            if (blocked != null) EditorGUILayout.LabelField (blocked, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space (4);
        }

        void WeightField (string label, RigProxies.Weight weight)
        {
            EditorGUI.BeginChangeCheck ();
            float value = EditorGUILayout.Slider (new GUIContent (label, weight.label), weight.value, 0, 1);
            if (EditorGUI.EndChangeCheck ()) {
                window_.SetRigWeight (weight, value);
            }
        }

        static void DrawNotes (RigProxies proxies)
        {
            if (proxies == null) return;
            foreach (string note in proxies.notes) {
                EditorGUILayout.LabelField (note, EditorStyles.wordWrappedMiniLabel);
            }
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "RigValues.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-rig-values", "Rig Values", defaultDisplay = false,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    sealed class RigValuesOverlayScene : RigValuesOverlay
    {
    }

}
