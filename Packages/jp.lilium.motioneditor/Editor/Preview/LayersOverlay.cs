using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.UIElements;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 姿勢がどう作られているかの段を並べて見せるパネル。段ごとに 表示（目）/ 書き出し先（鉛筆）/ つかむ段（手）をアイコンのボタンで切り替える。
    /// 段は対象から自動で決まるので、ここで段を足したり並べ替えたりはしない（PoseStack.Build）。
    /// 行の間には、下の段から上の段へ戻れるか（そのまま戻せる / ずれる / 戻せない）を出す。
    /// クリップを持つ段（編集するクリップ・焼いたクリップ）は、その行にクリップと状態を出す
    /// </summary>
    [Icon (Icons.kFolder + "Layers.png")]
    [Overlay (typeof (PreviewWindow), "mkt-layers", "Layers", defaultDisplay = true,
        defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    class LayersOverlay : Overlay
    {
        const float kHandleHeight = 5;

        static readonly Color kNoteColor = new Color (0.6f, 0.6f, 0.6f);
        static readonly Color kExactColor = new Color (0.5f, 0.75f, 0.5f);
        static readonly Color kApproximateColor = new Color (0.9f, 0.75f, 0.35f);
        static readonly Color kNoneColor = new Color (0.9f, 0.45f, 0.45f);
        static readonly Color kOnColor = new Color (0.35f, 0.55f, 0.85f);
        static readonly Color kHandleColor = new Color (0, 0, 0, 0.15f);
        /// <summary>書き出す段（Output）の目印の色</summary>
        static readonly Color kOutputColor = new Color (0.55f, 0.4f, 0.2f);
        /// <summary>段の枠（段と段の区切り）</summary>
        static readonly Color kCardColor = new Color (1, 1, 1, 0.04f);
        static readonly Color kCardBorderColor = new Color (1, 1, 1, 0.12f);
        static readonly Color kOutputCardColor = new Color (0.55f, 0.4f, 0.2f, 0.12f);
        // 行のボタンのアイコン（表示 / 書き出し先 / つかむ段。Editor/Icons）
        const string kViewIcon = "LayerEnabled";
        const string kWriteIcon = "LayerWrite";
        const string kManipulateIcon = "LayerManipulate";
        /// <summary>クリップの状態（未保存の変更など）は窓の出来事で拾いきれないので、この間隔で見直す</summary>
        const int kClipRefreshMilliseconds = 500;

        PreviewWindow window_;
        VisualElement root_;
        ScrollView content_;
        // 重みを動かしている間は行を作り直さない（入力が切れる）。矢印と経路の注意だけを差し替える
        readonly System.Collections.Generic.List<System.Action> refreshers_ = new System.Collections.Generic.List<System.Action> ();
        readonly System.Collections.Generic.List<System.Action> clipRefreshers_ = new System.Collections.Generic.List<System.Action> ();
        // クリップの欄は行を作り直しても使い回す。クリップを選ぶと段が作り直されるが、開いたままの選択窓（Select AnimationClip）は
        // 最初の欄に結び付いている。欄を作り直すと 2 回目以降の選択はパネルから外れた欄に届き、変更が配られずに捨てられる
        readonly System.Collections.Generic.Dictionary<string, ObjectField> clipFields_ = new System.Collections.Generic.Dictionary<string, ObjectField> ();
        readonly System.Collections.Generic.HashSet<string> usedClipFields_ = new System.Collections.Generic.HashSet<string> ();

        public override VisualElement CreatePanelContent ()
        {
            root_ = new VisualElement ();
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return root_;

            // 浮いているときは、幅と高さを下端の帯のドラッグで変える（値は窓が覚える）。
            // ドック域に入っているときは、その列・帯の幅に合わせる（枠を動かすと中身も付いてくる）
            root_.style.width = window_.layersWidth;
            OverlayLayout.FollowDockWidth (this, root_, () => window_.layersWidth);
            root_.Add (MakeAddButtons ());
            content_ = new ScrollView ();
            content_.style.height = window_.layersHeight;
            root_.Add (content_);
            root_.Add (ResizeHandle ());

            Rebuild ();
            window_.stackChanged -= OnStackChanged;
            window_.stackChanged += OnStackChanged;
            window_.displayResidualChanged -= Refresh;
            window_.displayResidualChanged += Refresh;
            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            root_.schedule.Execute (RefreshClips).Every (kClipRefreshMilliseconds);
            return root_;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) {
                window_.stackChanged -= OnStackChanged;
                window_.displayResidualChanged -= Refresh;
                window_.stateChanged -= Refresh;
            }
            base.OnWillBeDestroyed ();
        }

        Button editStructure_;
        Button addOverride_;
        Button addHumanoid_;
        Button addOutput_;

        /// <summary>
        /// 一覧の上のボタンの段（Picker・Transform と同じく、パネルの上端に並べる）。段を足すボタン
        /// </summary>
        VisualElement MakeAddButtons ()
        {
            VisualElement row = new VisualElement ();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 2;
            editStructure_ = MakeAddButton (Tr ("LAYERS_OVERLAY_EDIT_STRUCTURE"), Tr ("LAYERS_OVERLAY_EDIT_STRUCTURE_TOOLTIP"),
                () => window_.layerStructureEditing = !window_.layerStructureEditing);
            row.Add (editStructure_);
            addOverride_ = MakeAddButton ("+Override", Tr ("LAYERS_OVERLAY_ADD_OVERRIDE_TOOLTIP"), () => window_.AddOverride ());
            addOutput_ = MakeAddButton ("+Output", Tr ("LAYERS_OVERLAY_ADD_OUTPUT_TOOLTIP"), () => window_.AddOutput ());
            addHumanoid_ = MakeAddButton ("+Humanoid Pose", Tr ("LAYERS_OVERLAY_ADD_HUMANOID_POSE_TOOLTIP"), () => window_.SetHumanoidPoseLayer (true));
            row.Add (addOverride_);
            row.Add (addHumanoid_);
            row.Add (addOutput_);
            RefreshAddButtons ();
            return row;
        }

        static Button MakeAddButton (string text, string tooltip, System.Action clicked)
        {
            Button button = new Button (clicked) { text = text, tooltip = tooltip };
            button.style.flexGrow = 1;
            button.style.flexBasis = 0;
            button.style.height = 18;
            button.style.marginLeft = 0;
            button.style.marginRight = 1;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            return button;
        }

        void RefreshAddButtons ()
        {
            if (addOverride_ == null || window_ == null) return;
            // 構造を編集している間だけ、段を足すボタンを出す
            bool editing = window_.layerStructureEditing;
            editStructure_.style.backgroundColor = editing ? kOnColor : StyleKeyword.Null;
            editStructure_.style.color = editing ? Color.white : StyleKeyword.Null;
            editStructure_.text = editing ? Tr ("LAYERS_OVERLAY_EDITING_STRUCTURE") : Tr ("LAYERS_OVERLAY_EDIT_STRUCTURE");
            addOverride_.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            addOutput_.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            addHumanoid_.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            bool canAddHumanoid = window_.isHumanModel && !window_.humanoidPoseLayer;
            addHumanoid_.SetEnabled (canAddHumanoid);
            addHumanoid_.tooltip = canAddHumanoid ? Tr ("LAYERS_OVERLAY_ADD_HUMANOID_POSE_TOOLTIP")
                : Tr ("LAYERS_OVERLAY_CANNOT_ADD_HUMANOID_POSE", window_.isHumanModel ? Tr ("LAYERS_OVERLAY_ALREADY_PLACED") : Tr ("LAYERS_OVERLAY_DISPLAY_MODEL_NOT_HUMANOID"));
            string overrideProblem = window_.GetAddOverrideProblem ();
            addOverride_.SetEnabled (overrideProblem == null);
            addOverride_.tooltip = overrideProblem == null
                ? Tr ("LAYERS_OVERLAY_ADD_OVERRIDE_TOOLTIP")
                : Tr ("LAYERS_OVERLAY_CANNOT_ADD_OVERRIDE", overrideProblem);
            // 書き出せるのは今は Humanoid のクリップだけ
            bool human = window_.isHumanModel;
            addOutput_.SetEnabled (human);
            addOutput_.tooltip = human
                ? Tr ("LAYERS_OVERLAY_ADD_OUTPUT_TOOLTIP")
                : Tr ("LAYERS_OVERLAY_CANNOT_ADD_OUTPUT", Tr ("LAYERS_OVERLAY_DISPLAY_MODEL_NOT_HUMANOID"));
        }

        /// <summary>
        /// パネル下端の細い帯。上下で一覧の高さ、左右でパネルの幅を変える（値は PreviewWindow.layersHeight / layersWidth に持つ）
        /// </summary>
        VisualElement ResizeHandle ()
        {
            VisualElement handle = new VisualElement { tooltip = Tr ("LAYERS_OVERLAY_RESIZE_HANDLE_TOOLTIP") };
            handle.style.height = kHandleHeight;
            handle.style.marginTop = 2;
            handle.style.backgroundColor = kHandleColor;

            float startPointerY = 0;
            float startHeight = 0;
            handle.RegisterCallback<PointerDownEvent> (e => {
                startPointerY = e.position.y;
                startHeight = content_.resolvedStyle.height;
                handle.CapturePointer (e.pointerId);
                e.StopPropagation ();
            });
            handle.RegisterCallback<PointerMoveEvent> (e => {
                if (!handle.HasPointerCapture (e.pointerId)) return;
                window_.layersHeight = startHeight + (e.position.y - startPointerY);
                content_.style.height = window_.layersHeight;
            });
            handle.RegisterCallback<PointerUpEvent> (e => {
                if (handle.HasPointerCapture (e.pointerId)) handle.ReleasePointer (e.pointerId);
            });
            return handle;
        }

        /// <summary>
        /// 押したボタンを、そのボタンのハンドラの中で捨てないよう 1 フレーム遅らせる
        /// </summary>
        void OnStackChanged ()
        {
            if (root_ != null) root_.schedule.Execute (Rebuild);
        }

        void Rebuild ()
        {
            if (content_ == null || window_ == null) return;
            content_.Clear ();
            refreshers_.Clear ();
            clipRefreshers_.Clear ();
            usedClipFields_.Clear ();
            try {
                BuildContent ();
            }
            finally {
                // 段が無くなった欄は捨てる
                System.Collections.Generic.List<string> stale = new System.Collections.Generic.List<string> ();
                foreach (string key in clipFields_.Keys) if (!usedClipFields_.Contains (key)) stale.Add (key);
                foreach (string key in stale) clipFields_.Remove (key);
            }
        }

        void BuildContent ()
        {
            PoseStack stack = window_.poseStack;
            if (stack == null || stack.layers.Count == 0) {
                content_.Add (Note (Tr ("LAYERS_OVERLAY_NO_CHARACTER")));
                return;
            }

            // Layers の設定はキャラの設定に保存する（S19）。無いときは窓の中だけで、窓を閉じると消える
            if (window_.characterSettings == null && window_.model != null) {
                Label unsaved = Note (Tr ("LAYERS_OVERLAY_SETTINGS_NOT_SAVED"));
                unsaved.style.color = kApproximateColor;
                content_.Add (unsaved);
            }

            for (int i = 0; i < stack.layers.Count; i++) {
                PoseLayer layer = stack.layers[i];
                // 矢印は姿勢の段の上にだけ出す（姿勢を逆に通れるか）。最上段（Editing Rig）の上には何も無い
                // 段と段の間の矢印（その下の段を逆に通れるか）は、段の枠の外に置いてつなぎに見せる
                if (i > 0 && layer.isPoseStage) content_.Add (Arrow (stack, layer));
                content_.Add (Card (Row (stack, layer), layer));
            }


            Label pathNote = Note ("");
            content_.Add (pathNote);
            System.Action refreshPath = () => {
                string reason;
                InverseKind path = stack.PathKind (out reason);
                pathNote.style.display = path == InverseKind.Exact ? DisplayStyle.None : DisplayStyle.Flex;
                pathNote.text = Tr ("LAYERS_OVERLAY_PATH_TO_WRITE", PoseStack.Describe (path))
                    + (string.IsNullOrEmpty (reason) ? "" : Tr ("LAYERS_OVERLAY_REASON_SUFFIX", reason));
                pathNote.style.color = path == InverseKind.None ? kNoneColor : kApproximateColor;
            };
            refreshPath ();
            refreshers_.Add (refreshPath);

            foreach (string note in stack.notes) {
                content_.Add (Note (note, kNoteColor));
            }
        }

        void Refresh ()
        {
            RefreshAddButtons ();
            foreach (System.Action refresh in refreshers_) refresh ();
            RefreshClips ();
        }

        void RefreshClips ()
        {
            foreach (System.Action refresh in clipRefreshers_) refresh ();
        }

        /// <summary>
        /// 段 1 つを枠で囲む（段と段の区切りを見やすくする）。ファイルに書き出す段（Editing Rig・Override・Output）はオレンジの枠
        /// </summary>
        static VisualElement Card (VisualElement row, PoseLayer layer)
        {
            VisualElement card = new VisualElement ();
            card.style.marginTop = 3;
            card.style.paddingTop = 2;
            card.style.paddingBottom = 3;
            card.style.paddingLeft = 2;
            card.style.paddingRight = 2;
            // ファイルに書き出す段（編集用クリップ・Override のクリップを持つ段と、焼いて書き出す Output）はオレンジの枠
            bool writes = layer.isData || !layer.hasBadges;
            card.style.backgroundColor = writes ? kOutputCardColor : kCardColor;
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            Color border = writes ? kOutputColor : kCardBorderColor;
            card.style.borderTopColor = border;
            card.style.borderBottomColor = border;
            card.style.borderLeftColor = border;
            card.style.borderRightColor = border;
            card.style.borderTopLeftRadius = 4;
            card.style.borderTopRightRadius = 4;
            card.style.borderBottomLeftRadius = 4;
            card.style.borderBottomRightRadius = 4;
            card.Add (row);
            return card;
        }

        VisualElement Row (PoseStack stack, PoseLayer layer)
        {
            VisualElement row = new VisualElement ();
            row.style.marginTop = 2;

            VisualElement head = new VisualElement ();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;

            // 書き出すだけの段（Output）は 👁・✏・✋ を出さない（作ったボタンは行に足さないだけ）
            Button write = Badge (kWriteIcon, Tr ("LAYERS_OVERLAY_WRITE_BADGE_TOOLTIP"), stack.writeLayer == layer, null, () => window_.SetWriteLayer (layer));
            Button manipulate = Badge (kManipulateIcon, Tr ("LAYERS_OVERLAY_MANIPULATE_BADGE_TOOLTIP"), stack.manipulateLayer == layer, null, () => window_.SetManipulateLayer (layer));
            if (layer.hasBadges) {
                head.Add (Badge (kViewIcon, Tr ("LAYERS_OVERLAY_ENABLED_BADGE_TOOLTIP"), layer.enabled, null,
                    () => window_.SetLayerEnabled (layer, !layer.enabled)));
                head.Add (write);
                head.Add (manipulate);
            }
            else {
                // 書き出すだけの段（Output）。アイコンの位置に目印を出す（段の名前の位置は他の段とそろえる: アイコン 3 つ分 幅 22＋右の余白 1）
                Label mark = new Label (Tr ("LAYERS_OVERLAY_OUTPUT_MARK")) { tooltip = Tr ("LAYERS_OVERLAY_OUTPUT_MARK_TOOLTIP") };
                mark.style.width = 3 * 23;
                mark.style.height = 18;
                mark.style.unityTextAlign = TextAnchor.MiddleCenter;
                mark.style.fontSize = 10;
                mark.style.color = Color.white;
                mark.style.backgroundColor = kOutputColor;
                mark.style.borderTopLeftRadius = 3;
                mark.style.borderTopRightRadius = 3;
                mark.style.borderBottomLeftRadius = 3;
                mark.style.borderBottomRightRadius = 3;
                head.Add (mark);
            }

            // 段の名前に乗せると説明を出す（行に直接書くと一覧が読みにくい）
            Label label = new Label (layer.label);
            label.style.marginLeft = 4;
            label.style.flexGrow = 1;
            label.style.overflow = Overflow.Hidden;
            if (layer.hasBadges && (!layer.canEvaluate || !layer.enabled)) label.style.color = kNoteColor;
            label.RegisterCallback<PointerEnterEvent> (e => LayerHelpPopup.Open (label, window_, stack, layer));
            label.RegisterCallback<PointerLeaveEvent> (e => LayerHelpPopup.Close (layer));
            head.Add (label);
            // 段の ↑ ↓ 削除 は構造を編集している間だけ
            if (window_.layerStructureEditing) foreach (LayerClipButton spec in layer.headButtons) {
                LayerClipButton button = spec;
                Button element = new Button (() => button.run ()) { text = button.label, tooltip = button.tooltip };
                element.style.fontSize = 10;
                element.style.height = 18;
                element.style.marginLeft = 2;
                element.style.marginRight = 0;
                element.style.paddingLeft = 4;
                element.style.paddingRight = 4;
                head.Add (element);
                System.Action refreshButton = () => {
                    bool visible = button.visible == null || button.visible ();
                    element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                    string problem = button.problem != null ? button.problem () : null;
                    element.SetEnabled (problem == null);
                    element.tooltip = problem == null ? button.tooltip : Tr ("LAYERS_OVERLAY_BUTTON_CANNOT", button.label, problem);
                };
                refreshButton ();
                refreshers_.Add (refreshButton);
            }
            row.Add (head);

            if (layer.clip != null) {
                row.Add (Clip (layer.clip, ClipFieldKey (stack, layer)));
            }
            // 説明（note・値の出どころ・子の行）は段の名前の小窓へ移した（LayerHelpPopup）

            if (layer.hasWeight) {
                Slider weight = new Slider (Tr ("LAYERS_OVERLAY_WEIGHT"), 0, 1) { value = layer.weight, showInputField = true };
                weight.style.marginLeft = 26;
                weight.style.fontSize = 10;
                weight.tooltip = Tr ("LAYERS_OVERLAY_WEIGHT_TOOLTIP");
                weight.RegisterValueChangedCallback (e => {
                    window_.SetLayerWeight (layer, e.newValue);
                    Refresh ();
                });
                row.Add (weight);
            }

            // 「本来は選べる段なのに、いまの経路のせいで選べない」ときは、ツールチップに頼らず理由を出す。
            // 重みで選べるかが変わるので、行を作り直さずに差し替えられるようにしておく
            Label writeBlocked = Note ("", kNoneColor, 26);
            Label manipulateBlocked = Note ("", kNoneColor, 26);
            row.Add (writeBlocked);
            row.Add (manipulateBlocked);
            System.Action refreshBadges = () => {
                string writeReason;
                bool canWrite = stack.CanWrite (layer, out writeReason);
                SetBadge (write, Tr ("LAYERS_OVERLAY_WRITE_BADGE_TOOLTIP"), canWrite ? null : writeReason);
                SetBlocked (writeBlocked, layer.canWrite && !canWrite, Tr ("LAYERS_OVERLAY_CANNOT_WRITE", writeReason));

                string manipulateReason;
                bool canManipulate = stack.CanManipulate (layer, out manipulateReason);
                SetBadge (manipulate, Tr ("LAYERS_OVERLAY_MANIPULATE_BADGE_TOOLTIP"), canManipulate ? null : manipulateReason);
                SetBlocked (manipulateBlocked, layer.canManipulate && !canManipulate, Tr ("LAYERS_OVERLAY_CANNOT_MANIPULATE", manipulateReason));
            };
            refreshBadges ();
            refreshers_.Add (refreshBadges);

            foreach (PoseParameter parameter in layer.parameters) {
                VisualElement field = Parameter (parameter);
                if (field == null) continue;
                field.style.marginLeft = 26;
                field.style.fontSize = 10;
                // キーを打つ受け口がまだ無いので、モーションの値は表示だけ（編集できると見せて保存されないのを避ける）。
                // 見え方の設定（左右反転など）はキーに打たないので触れる
                field.SetEnabled (parameter.display);
                row.Add (field);
            }
            return row;
        }

        /// <summary>
        /// 段が持つクリップ。欄・ボタン・状態を 1 つにまとめる。
        /// 出力のクリップは選び直せないが、欄を押すと Project で場所が分かるように、押せるままにして値だけ戻す
        /// </summary>
        VisualElement Clip (LayerClip clip, string fieldKey)
        {
            VisualElement box = new VisualElement ();
            box.style.marginLeft = 26;

            VisualElement line = new VisualElement ();
            line.style.flexDirection = FlexDirection.Row;
            line.style.alignItems = Align.Center;

            Label name = new Label (clip.label) { tooltip = clip.tooltip };
            name.style.fontSize = 10;
            name.style.width = 28;
            name.style.color = kNoteColor;
            line.Add (name);

            ObjectField field = ClipField (fieldKey);
            field.tooltip = clip.tooltip;
            field.SetValueWithoutNotify (clip.clip);
            // 欄は使い回すので、選んだときの行き先は今の段のものへ差し替える
            field.userData = (System.Action<AnimationClip>)(value => {
                if (clip.canAssign) clip.assign (value);
                else field.SetValueWithoutNotify (clip.clip);
            });
            line.Add (field);

            System.Collections.Generic.List<System.Action> refreshControls = new System.Collections.Generic.List<System.Action> ();
            foreach (LayerClipButton spec in clip.buttons) {
                LayerClipButton button = spec;
                Button element = new Button (() => button.run ()) { text = button.label, tooltip = button.tooltip };
                element.style.fontSize = 10;
                element.style.height = 18;
                element.style.marginLeft = 2;
                element.style.marginRight = 0;
                element.style.paddingLeft = 4;
                element.style.paddingRight = 4;
                line.Add (element);
                refreshControls.Add (() => {
                    bool visible = button.visible == null || button.visible ();
                    element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                    string problem = button.problem != null ? button.problem () : null;
                    element.SetEnabled (problem == null);
                    element.tooltip = problem == null ? button.tooltip : Tr ("LAYERS_OVERLAY_BUTTON_CANNOT", button.label, problem);
                });
            }
            foreach (LayerClipToggle spec in clip.toggles) {
                LayerClipToggle toggle = spec;
                Toggle element = new Toggle { text = toggle.label, tooltip = toggle.tooltip };
                element.style.fontSize = 10;
                element.style.marginLeft = 4;
                element.RegisterValueChangedCallback (e => toggle.set (e.newValue));
                line.Add (element);
                refreshControls.Add (() => element.SetValueWithoutNotify (toggle.get ()));
            }
            box.Add (line);

            if (clip.hasWeight) {
                Slider weight = new Slider (Tr ("LAYERS_OVERLAY_BLEND"), 0, 1) { value = clip.getWeight (), showInputField = true };
                weight.style.fontSize = 10;
                weight.tooltip = Tr ("LAYERS_OVERLAY_BLEND_TOOLTIP");
                weight.labelElement.style.minWidth = 28;
                weight.labelElement.style.width = 28;
                weight.RegisterValueChangedCallback (e => clip.setWeight (e.newValue));
                box.Add (weight);
                refreshControls.Add (() => {
                    weight.style.display = clip.clip != null ? DisplayStyle.Flex : DisplayStyle.None;
                    if (!Mathf.Approximately (weight.value, clip.getWeight ())) weight.SetValueWithoutNotify (clip.getWeight ());
                });
            }

            Label status = Note ("");
            box.Add (status);

            System.Action refresh = () => {
                AnimationClip current = clip.clip;
                if (field.value != current) field.SetValueWithoutNotify (current);
                foreach (System.Action refreshControl in refreshControls) refreshControl ();

                LayerClipStatus value = clip.status;
                status.text = value.text ?? "";
                status.style.display = string.IsNullOrEmpty (value.text) ? DisplayStyle.None : DisplayStyle.Flex;
                status.style.color = value.state == LayerClipState.Blocked ? kNoneColor
                    : value.state == LayerClipState.Stale ? kApproximateColor
                    : value.state == LayerClipState.Ready ? kExactColor : kNoteColor;
            };
            refresh ();
            clipRefreshers_.Add (refresh);
            return box;
        }

        /// <summary>
        /// 段のクリップの欄。同じ段（種類と、その種類の中での並び）の欄は前の行から引き継ぐ
        /// </summary>
        ObjectField ClipField (string key)
        {
            usedClipFields_.Add (key);
            ObjectField field;
            if (clipFields_.TryGetValue (key, out field)) return field;

            field = new ObjectField { objectType = typeof (AnimationClip), allowSceneObjects = false };
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 60;
            field.style.marginLeft = 0;
            ObjectField self = field;
            field.RegisterValueChangedCallback (e => {
                System.Action<AnimationClip> assign = self.userData as System.Action<AnimationClip>;
                if (assign != null) assign (e.newValue as AnimationClip);
            });
            clipFields_[key] = field;
            return field;
        }

        static string ClipFieldKey (PoseStack stack, PoseLayer layer)
        {
            int index = 0;
            foreach (PoseLayer other in stack.layers) {
                if (other == layer) break;
                if (other.kind == layer.kind) index++;
            }
            return layer.kind + "/" + index;
        }

        /// <summary>
        /// ゲーム側が [LayerParameter] で出した値。型に合う入力を作る
        /// </summary>
        VisualElement Parameter (PoseParameter parameter)
        {
            object value = parameter.getValue ();

            if (parameter.type == typeof (float)) {
                float current = value is float ? (float)value : 0;
                if (parameter.hasRange) {
                    Slider slider = new Slider (parameter.label, parameter.min, parameter.max) { value = current, showInputField = true };
                    Bind (parameter, slider);
                    return slider;
                }
                FloatField number = new FloatField (parameter.label) { value = current };
                Bind (parameter, number);
                return number;
            }
            if (parameter.type == typeof (bool)) {
                Toggle toggle = new Toggle (parameter.label) { value = value is bool && (bool)value };
                Bind (parameter, toggle);
                return toggle;
            }
            if (parameter.type == typeof (int)) {
                IntegerField integer = new IntegerField (parameter.label) { value = value is int ? (int)value : 0 };
                Bind (parameter, integer);
                return integer;
            }
            if (parameter.type == typeof (Vector3)) {
                Vector3Field vector = new Vector3Field (parameter.label) { value = value is Vector3 ? (Vector3)value : Vector3.zero };
                Bind (parameter, vector);
                return vector;
            }
            // 対応していない型は読むだけ
            return Note (parameter.label + ": " + value, kNoteColor);
        }

        /// <summary>
        /// 触った値を段へ入れて、姿勢を作り直す（入れただけでは画面に出ない）
        /// </summary>
        void Bind<T> (PoseParameter parameter, BaseField<T> field)
        {
            if (parameter.setValue == null) return;
            field.RegisterValueChangedCallback (e => {
                parameter.setValue (e.newValue);
                if (window_ != null) window_.RefreshPose ();
            });
        }

        /// <summary>
        /// 姿勢の段の上に出す矢印の行。左半分は ↓（普段の流れ。上の段の姿勢が下の段へ渡る）、
        /// 右半分は ↑（その段を逆に通れるか。いまの ✋ から ✏ への経路で通る段は濃く出す）
        /// </summary>
        VisualElement Arrow (PoseStack stack, PoseLayer below)
        {
            VisualElement row = new VisualElement ();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginLeft = 26;
            row.style.marginTop = 1;

            Label down = Note ("↓", kNoteColor);
            down.style.flexGrow = 1;
            down.style.flexBasis = 0;
            down.style.unityTextAlign = TextAnchor.MiddleCenter;
            down.tooltip = Tr ("LAYERS_OVERLAY_FLOW_TOOLTIP");
            row.Add (down);

            Label label = Note ("", kNoteColor);
            label.style.flexGrow = 1;
            label.style.flexBasis = 0;
            row.Add (label);
            System.Action refresh = () => {
                string reason;
                InverseKind kind = stack.StepKind (below, out reason);
                bool onPath = stack.IsOnPath (below);

                string text = "↑ " + PoseStack.Describe (kind);
                if (onPath && !string.IsNullOrEmpty (reason)) text += Tr ("LAYERS_OVERLAY_REASON_SUFFIX", reason);

                Color color = kind == InverseKind.None ? kNoneColor
                    : kind == InverseKind.Approximate ? kApproximateColor : kExactColor;
                // 近似の段は、表示の骨をつかんだ最後の残差で色を決める（閾値の 1°・1cm 以内なら緑、超えたら黄色）
                float angle, distance;
                bool warning;
                if (onPath && kind == InverseKind.Approximate && window_.TryGetDisplayResidual (out angle, out distance, out warning)) {
                    color = warning ? kApproximateColor : kExactColor;
                    text += Tr ("LAYERS_OVERLAY_RESIDUAL", angle.ToString ("F2"), (distance * 1000).ToString ("F1"));
                }
                if (!onPath) color = new Color (color.r, color.g, color.b, 0.45f);
                label.text = text;
                label.style.color = color;
                // 段を 👁 で切っているときは、流れはその段を素通りする
                down.style.color = below.enabled ? kNoteColor : new Color (kNoteColor.r, kNoteColor.g, kNoteColor.b, 0.35f);
                down.tooltip = below.enabled ? Tr ("LAYERS_OVERLAY_FLOW_TOOLTIP") : Tr ("LAYERS_OVERLAY_FLOW_DISABLED_TOOLTIP");
            };
            refresh ();
            refreshers_.Add (refresh);
            return row;
        }

        static Label Note (string text)
        {
            return Note (text, kNoteColor);
        }

        static Label Note (string text, Color color, float indent = 0)
        {
            Label label = new Label (text);
            label.style.color = color;
            label.style.fontSize = 10;
            label.style.marginLeft = indent;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        /// <summary>
        /// 表示 / 書き出し先 / つかむ段 のボタン。選べないときは押せなくし、理由をツールチップに出す（理由は行の下にも出す）
        /// </summary>
        static Button Badge (string icon, string tooltip, bool on, string disabledReason, System.Action click)
        {
            Button button = new Button (click) { tooltip = disabledReason ?? tooltip };
            Texture2D texture = Icons.Load (icon);
            if (texture != null) {
                button.iconImage = Background.FromTexture2D (texture);
            } else {
                // アイコンが読めないときは名前の頭文字で代える
                button.text = icon.Substring (0, 1);
            }
            button.style.width = 22;
            button.style.height = 18;
            button.style.marginLeft = 0;
            button.style.marginRight = 1;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            button.style.fontSize = 10;
            if (on) {
                button.style.backgroundColor = kOnColor;
                button.style.color = Color.white;
            }
            SetBadge (button, tooltip, disabledReason);
            return button;
        }

        /// <summary>
        /// 選べないときは押せなくし、理由をツールチップに出す
        /// </summary>
        static void SetBadge (Button button, string tooltip, string disabledReason)
        {
            button.tooltip = disabledReason ?? tooltip;
            button.SetEnabled (disabledReason == null);
        }

        static void SetBlocked (Label label, bool show, string text)
        {
            label.text = text;
            label.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Layers.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-layers", "Layers", defaultDisplay = true,
        defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    sealed class LayersOverlayScene : LayersOverlay
    {
    }

}
