using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using static Lilium.MotionEditorLocalization;

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
    class RigValuesOverlay : MotionOverlay
    {
        IMGUIContainer container_;
        Vector2 scroll_;

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = 240;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => 240);
            window_ = ResolveWindow ();
            if (window_ == null) return root;

            container_ = new IMGUIContainer (OnPanelGUI);
            container_.style.maxHeight = 320;
            root.Add (container_);

            Resubscribe ();
            return root;
        }

        protected override void Subscribe (bool on)
        {
            window_.stateChanged -= Refresh;
            window_.poseSampled -= Refresh;
            if (!on) return;
            window_.stateChanged += Refresh;
            window_.poseSampled += Refresh;
        }

        void Refresh ()
        {
            if (container_ != null) container_.MarkDirtyRepaint ();
        }

        void OnPanelGUI ()
        {
            DrawHeldObjects ();
            DrawParentSwitches ();
            RigProxies proxies = window_ != null ? window_.rigProxies : null;
            if (proxies == null || proxies.rigs.Count == 0) {
                EditorGUILayout.LabelField (Tr ("RIG_VALUES_OVERLAY_NO_RIG"), EditorStyles.miniLabel);
                DrawNotes (proxies);
                return;
            }
            if (window_.clip == null || window_.isClipReadOnly) {
                EditorGUILayout.HelpBox (Tr ("RIG_VALUES_OVERLAY_CLIP_READ_ONLY"), MessageType.None);
            }

            scroll_ = EditorGUILayout.BeginScrollView (scroll_);
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 120;
            foreach (RigProxies.RigEntry rig in proxies.rigs) {
                EditorGUILayout.LabelField (rig.name, EditorStyles.boldLabel);
                WeightField (Tr ("RIG_VALUES_OVERLAY_WEIGHT"), rig.weight);
                foreach (RigProxies.Weight constraint in rig.constraints) {
                    // 拘束の名前は Rig からの相対（Rig と同じ GameObject なら型名）
                    string name = constraint.label.Substring (rig.name.Length + 1);
                    WeightField (name, constraint);
                }
                foreach (RigProxies.Source source in rig.sources) {
                    bool selected = window_.IsSelected (source.proxy.gameObject);
                    string name = source.label.Substring (rig.name.Length + 1);
                    if (GUILayout.Toggle (selected, Tr ("RIG_VALUES_OVERLAY_TARGET", name), EditorStyles.miniButton) && !selected) {
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
        /// 持ち物（S91）。手ごとに、今のフレームで持っているかを押された状態で出し、押すと今のフレームで切り替える
        /// </summary>
        void DrawHeldObjects ()
        {
            if (window_ == null || window_.heldObjects.Count == 0) return;
            using (new EditorGUI.DisabledScope (window_.heldObjectBlockReason != null)) {
                foreach (HeldObjectRig.Entry entry in window_.heldObjects) {
                    EditorGUILayout.LabelField (Tr ("RIG_VALUES_OVERLAY_HELD_OBJECT", entry.name), EditorStyles.boldLabel);
                    EditorGUILayout.BeginHorizontal ();
                    HoldToggle (entry, true, entry.values.leftHold >= 0.5f, Tr ("RIG_VALUES_OVERLAY_LEFT_HAND"));
                    HoldToggle (entry, false, entry.values.rightHold >= 0.5f, Tr ("RIG_VALUES_OVERLAY_RIGHT_HAND"));
                    EditorGUILayout.EndHorizontal ();
                    SlideField (entry.left, Tr ("RIG_VALUES_OVERLAY_LEFT_SLIDE"));
                    SlideField (entry.right, Tr ("RIG_VALUES_OVERLAY_RIGHT_SLIDE"));
                    SocketToolbar (entry);
                }
            }
            // スライダーを離したときにキーを打つ（動かしている間に毎回打たない）
            if (GUIUtility.hotControl == 0) window_.EndObjectSlide ();
            EditorGUILayout.Space (4);
        }

        /// <summary>手が握りを柄に沿ってずらす量のスライダー（持っている手だけ）</summary>
        void SlideField (HeldObjectRig.Hand hand, string label)
        {
            if (hand == null || !hand.held) return;
            EditorGUI.BeginChangeCheck ();
            float value = EditorGUILayout.Slider (label, hand.slide, -kSlideRange, kSlideRange);
            if (EditorGUI.EndChangeCheck ()) window_.SetObjectSlide (hand, value);
        }

        /// <summary>握りのずれのスライダーの範囲（メートル）</summary>
        const float kSlideRange = 0.4f;

        void HoldToggle (HeldObjectRig.Entry entry, bool left, bool holding, string label)
        {
            if (GUILayout.Toggle (holding, label, EditorStyles.miniButton) != holding) window_.SetObjectHold (entry, left, !holding);
        }

        /// <summary>差す場所（S91f）。今の場所を押された状態で出し、押すと今のフレームで切り替える。差さないを先頭に出す</summary>
        void SocketToolbar (HeldObjectRig.Entry entry)
        {
            if (entry.sockets.Count == 0) return;
            string[] labels = new string[entry.sockets.Count + 1];
            labels[0] = Tr ("RIG_VALUES_OVERLAY_NO_SOCKET");
            for (int i = 0; i < entry.sockets.Count; i++) labels[i + 1] = entry.sockets[i].definition.name;
            int current = entry.sockets.IndexOf (entry.socket) + 1;
            int chosen = GUILayout.Toolbar (current, labels, EditorStyles.miniButton);
            if (chosen != current) window_.SetObjectSocket (entry, chosen > 0 ? entry.sockets[chosen - 1] : null);
        }

        /// <summary>
        /// 物の持ち替え（キャラの設定の parentSwitches）。今の親を押された状態で出し、別の親を押すと今のフレームで切り替える
        /// </summary>
        void DrawParentSwitches ()
        {
            if (window_ == null || window_.parentSwitches.Count == 0) return;
            string blocked = window_.parentSwitchBlockReason;
            foreach (ParentSwitchDefinition definition in window_.parentSwitches) {
                EditorGUILayout.LabelField (Tr ("RIG_VALUES_OVERLAY_PARENT_SWITCH", definition.name), EditorStyles.boldLabel);
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
