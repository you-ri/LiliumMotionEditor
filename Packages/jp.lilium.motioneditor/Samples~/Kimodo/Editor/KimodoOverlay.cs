using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;
using static Lilium.KimodoText;

namespace Lilium
{

    /// <summary>
    /// Motion Editor の窓のパネル: 窓のキーと文章から、Kimodo でキーの間を作って取り込む（S77）。
    /// 窓の中に置くので、相手の窓を探さずに載っている窓を使う。Kimodo の出力（ログ）は場所を取るので別の窓（KimodoConsoleWindow）に出す。
    /// Kimodo が使えるようになっていなければ、中身を入れ方の案内（KimodoSetup）に替える。
    /// 取り込んだ Override を見るには ✏ をそこへ移す必要がある（書き込み先より上の段は編集中は効かない）。
    /// ✏ を移したまま打ったキーは Override に入り次の生成に渡らないので、「結果を見る」と「キーを直す」を切り替えられるようにする
    /// </summary>
    [Overlay (typeof (PreviewWindow), kId, "Kimodo", defaultDisplay = true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    sealed class KimodoOverlay : Overlay
    {
        public const string kId = "lilium-kimodo";
        const float kWidth = 300;
        const float kMinWidth = 230;
        /// <summary>入りきらないときも、これだけは見せる（スクロールして使える高さ）</summary>
        const float kMinHeight = 140;
        const float kSegmentWidth = 64;

        static readonly string[] kTargets = { KimodoJob.kBetween, KimodoJob.kOverride };

        PreviewWindow window_;
        VisualElement root_;
        IMGUIContainer gui_;
        /// <summary>補間として使うときに外部の動きにする区間（始まりのフレーム）と、それを選んだクリップ（S83）</summary>
        List<int> chosen_ = new List<int> ();
        AnimationClip chosenClip_;
        /// <summary>最後に見た区間の始まり。キーの並びが変わったことを知るため</summary>
        List<int> knownStarts_ = new List<int> ();
        string problem_;
        /// <summary>
        /// IMGUI の中身の高さ。IMGUIContainer は中身が伸び縮みしても自分の高さを変えないので（はみ出して下のパネルに重なった）、
        /// 描いた中身の高さを測って合わせる
        /// </summary>
        float height_ = 200;
        Vector2 scroll_;

        /// <summary>開いている Motion Editor の窓（無ければ開く）に、このパネルを出す</summary>
        [MenuItem ("Window/Lilium Motion Editor/Kimodo")]
        static void Show ()
        {
            PreviewWindow window = EditorWindow.focusedWindow as PreviewWindow;
            if (window == null) window = Resources.FindObjectsOfTypeAll<PreviewWindow> ().FirstOrDefault (w => w.baseClip != null);
            if (window == null) window = Resources.FindObjectsOfTypeAll<PreviewWindow> ().FirstOrDefault ();
            if (window == null) {
                EditorApplication.ExecuteMenuItem ("Window/Lilium Motion Editor/Motion Editor");
                window = Resources.FindObjectsOfTypeAll<PreviewWindow> ().FirstOrDefault ();
                if (window == null) return;
            }
            window.Focus ();
            if (window.TryGetOverlay (kId, out Overlay overlay) && overlay != null) {
                overlay.displayed = true;
                overlay.collapsed = false;
            }
        }

        public override void OnCreated ()
        {
            base.OnCreated ();
            collapsedIcon = KimodoSetup.icon;
        }

        public override VisualElement CreatePanelContent ()
        {
            Subscribe (false);
            window_ = containerWindow as PreviewWindow;
            // IMGUI は外枠いっぱいに敷き、外枠の大きさはこちらで決める。IMGUIContainer に大きさを測らせると、
            // 折り返す文字を 1 行に並べた幅で測って列が広がり（949px）、高さは中身が伸びても変わらなかった
            root_ = new VisualElement ();
            root_.style.height = height_;
            gui_ = new IMGUIContainer (OnGUI);
            gui_.style.position = Position.Absolute;
            gui_.style.left = 0;
            gui_.style.right = 0;
            gui_.style.top = 0;
            gui_.style.bottom = 0;
            root_.Add (gui_);
            FollowWidth ();
            root_.RegisterCallback<AttachToPanelEvent> (e => FollowWidth ());
            floatingChanged -= OnFloatingChanged;
            floatingChanged += OnFloatingChanged;
            Subscribe (true);
            return root_;
        }

        void OnFloatingChanged (bool floating)
        {
            FollowWidth ();
        }

        /// <summary>
        /// 浮いているときは決まった幅、列にドックしているときは列の幅に合わせる（ほかのパネルと同じ。
        /// 幅を決めたままだと列より広くなり、左が切れた）
        /// </summary>
        void FollowWidth ()
        {
            if (root_ == null) return;
            if (floating) {
                root_.style.width = kWidth;
                root_.style.flexGrow = 0;
            }
            else {
                root_.style.width = StyleKeyword.Auto;
                root_.style.minWidth = kMinWidth;
                root_.style.flexGrow = 1;
            }
        }

        public override void OnWillBeDestroyed ()
        {
            floatingChanged -= OnFloatingChanged;
            Subscribe (false);
            base.OnWillBeDestroyed ();
        }

        /// <summary>知らせを付ける・外す（外してから付けるので、何度呼んでも二重にならない）</summary>
        void Subscribe (bool on)
        {
            KimodoRunner.changed -= Repaint;
            KimodoTextServer.changed -= Repaint;
            if (window_ != null) {
                window_.stateChanged -= Repaint;
                window_.keysChanged -= Repaint;
            }
            if (!on) return;
            KimodoRunner.changed += Repaint;
            KimodoTextServer.changed += Repaint;
            if (window_ != null) {
                window_.stateChanged += Repaint;
                window_.keysChanged += Repaint;
            }
        }

        void Repaint ()
        {
            if (gui_ != null) gui_.MarkDirtyRepaint ();
        }

        void OnGUI ()
        {
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 110;
            try {
                scroll_ = EditorGUILayout.BeginScrollView (scroll_, GUILayout.Height (height_));
                EditorGUILayout.BeginVertical ();
                if (KimodoSetup.ready) DrawPanel ();
                else DrawGuideOrJob ();
                EditorGUILayout.EndVertical ();
                float content = GUILayoutUtility.GetLastRect ().yMax + 4;
                EditorGUILayout.EndScrollView ();
                if (Event.current.type == EventType.Repaint) FitHeight (content);
            }
            finally {
                EditorGUIUtility.labelWidth = labelWidth;
            }
        }

        /// <summary>中身の高さに合わせる。入りきらなければ入る高さにしてスクロールさせる</summary>
        void FitHeight (float content)
        {
            float height = Mathf.Min (content, AvailableHeight ());
            if (Mathf.Abs (height - height_) < 1) return;
            height_ = height;
            root_.style.height = height;
        }

        /// <summary>
        /// パネルの中身に使える高さ。列にドックしているときは、列の高さから同じ列のほかのパネルを引いた残り
        /// （列はスクロールせず、入りきらないパネルは上のパネルに重なった）。浮いているときは窓の高さ
        /// </summary>
        float AvailableHeight ()
        {
            VisualElement column = null;
            VisualElement canvas = null;
            VisualElement self = null;
            for (VisualElement element = root_.parent; element != null; element = element.parent) {
                if (self == null && element.ClassListContains ("unity-overlay")) self = element;
                if (column == null && element.ClassListContains ("unity-overlay-container")) column = element;
                if (element.ClassListContains ("unity-overlay-canvas")) canvas = element;
            }
            // パネルの見出しと枠（中身の外）の分
            float chrome = self != null ? self.layout.height - root_.layout.height : 30;
            if (float.IsNaN (chrome) || chrome < 0) chrome = 30;
            float available;
            if (!floating && column != null) {
                available = column.layout.height - chrome;
                foreach (VisualElement other in column.Query (className: "unity-overlay").ToList ()) {
                    if (other != self && other.resolvedStyle.display != DisplayStyle.None) available -= other.layout.height;
                }
            }
            else {
                available = (canvas != null ? canvas.layout.height : 600) - chrome - 40;
            }
            return float.IsNaN (available) ? kMinHeight : Mathf.Max (kMinHeight, available);
        }

        /// <summary>中身の幅（区間のボタンを折り返す）</summary>
        float contentWidth
        {
            get {
                float width = gui_ != null ? gui_.contentRect.width : 0;
                return float.IsNaN (width) || width < 1 ? kWidth : width;
            }
        }

        /// <summary>
        /// 使えないときは、案内の窓（KimodoInstallWindow）を開くボタンだけを出す（案内はパネルでは狭くて読みにくかった）。
        /// 生成の途中（入れた後に設定を消したなど）なら、その結果も出す
        /// </summary>
        void DrawGuideOrJob ()
        {
            EditorGUILayout.LabelField (Tr ("KIMODO_PANEL_NOT_READY"), EditorStyles.wordWrappedLabel);
            string environment = KimodoRunner.environmentProblem;
            if (environment != null) EditorGUILayout.HelpBox (environment, MessageType.Warning);
            if (GUILayout.Button (Tr ("KIMODO_PANEL_OPEN_INSTALL"), GUILayout.Height (28))) KimodoInstallWindow.Open ();
            KimodoJob job = KimodoRunner.job;
            if (job != null) DrawJob (job);
        }

        void DrawPanel ()
        {
            KimodoSettings settings = KimodoSettings.instance;
            KimodoJob job = KimodoRunner.job;

            EditorGUILayout.LabelField (Tr ("KIMODO_PANEL_CLIP"), window_ != null && window_.baseClip != null ? window_.baseClip.name : Tr ("KIMODO_PANEL_NO_CLIP"), EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck ();
            EditorGUILayout.LabelField (Tr ("KIMODO_PANEL_PROMPT"), EditorStyles.miniLabel);
            settings.prompt = EditorGUILayout.TextArea (settings.prompt, EditorStyles.textArea, GUILayout.MinHeight (40));
            int mode = System.Array.IndexOf (KimodoSettings.kModes, settings.mode);
            mode = EditorGUILayout.Popup (Tr ("KIMODO_PANEL_CONSTRAINTS"), Mathf.Max (0, mode), new[] { Tr ("KIMODO_PANEL_MODE_FULL_BODY"), Tr ("KIMODO_PANEL_MODE_ENDS"), Tr ("KIMODO_PANEL_MODE_ROOT") });
            settings.mode = KimodoSettings.kModes[mode];
            int target = Mathf.Max (0, System.Array.IndexOf (kTargets, settings.target));
            target = EditorGUILayout.Popup (Tr ("KIMODO_PANEL_TARGET"), target, new[] { Tr ("KIMODO_PANEL_TARGET_BETWEEN"), Tr ("KIMODO_PANEL_TARGET_OVERRIDE") });
            settings.target = kTargets[target];
            if (EditorGUI.EndChangeCheck ()) settings.Save ();
            if (settings.target == KimodoJob.kBetween) DrawSegments ();

            EditorGUILayout.Space ();
            bool running = job != null && job.running;
            using (new EditorGUILayout.HorizontalScope ()) {
                using (new EditorGUI.DisabledScope (running || window_ == null)) {
                    if (GUILayout.Button (Tr ("KIMODO_PANEL_GENERATE"), GUILayout.Height (24))) {
                        problem_ = KimodoRunner.Start (window_, settings.prompt, settings.mode, settings.target, ChosenSegments ());
                    }
                }
                using (new EditorGUI.DisabledScope (!running)) {
                    if (GUILayout.Button (Tr ("KIMODO_PANEL_STOP"), GUILayout.Width (48), GUILayout.Height (24))) KimodoRunner.Cancel ();
                }
            }
            if (problem_ != null && !running) EditorGUILayout.HelpBox (problem_, MessageType.Warning);
            if (job != null) DrawJob (job);
            else if (GUILayout.Button (Tr ("KIMODO_PANEL_CONSOLE"))) KimodoConsoleWindow.Open ();

            // 文章の読み取りのサーバーは Docker のときだけ（Python の環境では WSL のメモリの上限が無く、読み込みも速いので要らない）
            if (settings.runner == KimodoSettings.kDocker) DrawTextServer ();

            EditorGUILayout.Space ();
            // 設定は Project Settings の「Lilium Motion Editor/Kimodo」に置く（パネルを狭く保つ）
            if (GUILayout.Button (Tr ("KIMODO_PANEL_SETTINGS"))) KimodoSetup.OpenSettings ();
        }

        /// <summary>今のクリップの区間（隣り合うキーの間）</summary>
        List<BetweenClips.Range> Segments ()
        {
            List<BetweenClips.Range> segments = new List<BetweenClips.Range> ();
            AnimationClip clip = window_ != null ? window_.baseClip : null;
            if (clip == null) return segments;
            int[] frames = ClipCycle.GetFrames (clip);
            for (int i = 0; i + 1 < frames.Length; i++) segments.Add (new BetweenClips.Range { start = frames[i], end = frames[i + 1] });
            return segments;
        }

        List<BetweenClips.Range> ChosenSegments ()
        {
            return Segments ().Where (r => chosen_.Contains (r.start)).ToList ();
        }

        /// <summary>
        /// 補間として使う区間を選ぶ（S83）。クリップが替わったら、外部の動きを使っている区間（無ければ全部）を選び直す。
        /// 同じクリップでキーの並びが変わったら、残った区間の選択を引き継ぎ、新しい区間を選ぶ。
        /// Kimodo は毎回クリップ全体を作り直し、結果のファイルを丸ごと置き換える（選ばなかった区間は元の補間のまま）
        /// </summary>
        void DrawSegments ()
        {
            AnimationClip clip = window_ != null ? window_.baseClip : null;
            List<BetweenClips.Range> segments = Segments ();
            List<int> starts = segments.Select (r => r.start).ToList ();
            if (clip != chosenClip_) {
                chosenClip_ = clip;
                List<int> external = segments.Where (r => {
                    BetweenClips.State state = BetweenClips.GetSegmentState (clip, r);
                    return state == BetweenClips.State.Fresh || state == BetweenClips.State.Stale;
                }).Select (r => r.start).ToList ();
                chosen_ = external.Count > 0 ? external : new List<int> (starts);
                knownStarts_ = starts;
            } else if (!starts.SequenceEqual (knownStarts_)) {
                // キーを打ち足したり動かしたりした。残った区間は選んだままにし、新しくできた区間は選ぶ
                // （選び直さないと、消えた区間の始まりだけが残り、新しい区間が Kimodo にならない）
                chosen_ = starts.Where (s => chosen_.Contains (s) || !knownStarts_.Contains (s)).ToList ();
                if (chosen_.Count == 0) chosen_ = new List<int> (starts);
                knownStarts_ = starts;
            }
            EditorGUILayout.LabelField (Tr ("KIMODO_PANEL_SEGMENTS"), EditorStyles.wordWrappedMiniLabel);
            if (segments.Count == 0) {
                EditorGUILayout.HelpBox (Tr ("KIMODO_PANEL_NO_SEGMENTS"), MessageType.None);
                return;
            }
            // パネルは幅が狭いので、区間のボタンは折り返して並べる
            int perRow = Mathf.Max (1, Mathf.FloorToInt ((contentWidth - 8) / kSegmentWidth));
            for (int row = 0; row < segments.Count; row += perRow) {
                using (new EditorGUILayout.HorizontalScope ()) {
                    foreach (BetweenClips.Range range in segments.Skip (row).Take (perRow)) {
                        bool on = chosen_.Contains (range.start);
                        bool next = GUILayout.Toggle (on, range.start + "-" + range.end, EditorStyles.miniButton, GUILayout.Width (kSegmentWidth - 4));
                        if (next == on) continue;
                        if (next) chosen_.Add (range.start);
                        else chosen_.Remove (range.start);
                    }
                    GUILayout.FlexibleSpace ();
                }
            }
        }

        /// <summary>
        /// 文章の読み取りのサーバー（S78）。立てておくと 2 本目からの生成が速くなる（モデルの読み込みの大半が文章の読み取り）。
        /// 止めるまで PC のメモリを約 15GB 使い続ける。立ち上がるまでのログはコンソールの窓に出す
        /// </summary>
        static void DrawTextServer ()
        {
            EditorGUILayout.Space ();
            KimodoTextServer.State state = KimodoTextServer.state;
            string label = state == KimodoTextServer.State.Ready ? Tr ("KIMODO_PANEL_SERVER_READY")
                : state == KimodoTextServer.State.Starting ? Tr ("KIMODO_PANEL_SERVER_STARTING")
                : Tr ("KIMODO_PANEL_SERVER_STOPPED");
            EditorGUILayout.LabelField (label, EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope ()) {
                using (new EditorGUI.DisabledScope (state != KimodoTextServer.State.Stopped)) {
                    if (GUILayout.Button (Tr ("KIMODO_PANEL_SERVER_START"))) {
                        string problem = KimodoTextServer.Start ();
                        if (problem != null) EditorUtility.DisplayDialog ("Kimodo", problem, Tr ("KIMODO_OK"));
                    }
                }
                using (new EditorGUI.DisabledScope (state == KimodoTextServer.State.Stopped)) {
                    if (GUILayout.Button (Tr ("KIMODO_PANEL_SERVER_STOP"))) KimodoTextServer.Stop ();
                }
            }
            if (state != KimodoTextServer.State.Stopped) EditorGUILayout.LabelField (Tr ("KIMODO_PANEL_SERVER_MEMORY"), EditorStyles.wordWrappedMiniLabel);
        }

        void DrawJob (KimodoJob job)
        {
            EditorGUILayout.Space ();
            if (job.running) {
                System.TimeSpan elapsed = System.TimeSpan.FromTicks (System.DateTime.Now.Ticks - job.started);
                string hint = job.pid > 0 ? Tr ("KIMODO_PANEL_HINT_PYTHON")
                    : job.server ? Tr ("KIMODO_PANEL_HINT_SERVER") : Tr ("KIMODO_PANEL_HINT_DOCKER");
                EditorGUILayout.LabelField (Tr ("KIMODO_PANEL_GENERATING", elapsed.ToString (@"m\:ss"), hint), EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button (Tr ("KIMODO_PANEL_CONSOLE"))) KimodoConsoleWindow.Open ();
                return;
            }
            MessageType type = job.state == KimodoJob.kFailed ? MessageType.Error : job.state == KimodoJob.kCancelled ? MessageType.Warning : MessageType.Info;
            EditorGUILayout.HelpBox (KimodoConsoleWindow.Summary (job), type);
            if (job.state == KimodoJob.kGenerated) {
                PreviewWindow window = KimodoRunner.FindWindow (job.baseClip);
                using (new EditorGUI.DisabledScope (window == null)) {
                    if (GUILayout.Button (Tr ("KIMODO_PANEL_IMPORT"))) problem_ = KimodoRunner.TryImport (window);
                }
            }
            if (job.state == KimodoJob.kImported && job.target != KimodoJob.kBetween) DrawResult (job);
            using (new EditorGUILayout.HorizontalScope ()) {
                if (GUILayout.Button (job.state == KimodoJob.kFailed ? Tr ("KIMODO_PANEL_CONSOLE_LOG") : Tr ("KIMODO_PANEL_CONSOLE"))) KimodoConsoleWindow.Open ();
                if (GUILayout.Button (Tr ("KIMODO_PANEL_CLEAR"), GUILayout.Width (60))) KimodoRunner.Clear ();
            }
        }

        void DrawResult (KimodoJob job)
        {
            PreviewWindow window = KimodoRunner.FindWindow (job.baseClip);
            int index = KimodoRunner.FindOverride (window, job.destination);
            if (window != null && index >= 0) {
                bool showing = window.overrideWriteIndex == index;
                EditorGUILayout.LabelField (showing
                    ? Tr ("KIMODO_PANEL_RESULT_SHOWING")
                    : Tr ("KIMODO_PANEL_RESULT_EDITING"), EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope ()) {
                    using (new EditorGUI.DisabledScope (showing)) {
                        if (GUILayout.Button (Tr ("KIMODO_PANEL_SEE_RESULT"))) window.SetOverrideWrite (index);
                    }
                    using (new EditorGUI.DisabledScope (!showing)) {
                        if (GUILayout.Button (Tr ("KIMODO_PANEL_EDIT_KEYS"))) window.SetOverrideWrite (-1);
                    }
                }
            }
            if (job.keyDeviations == null || job.keyDeviations.Count == 0) return;
            // 寄せる前に Kimodo の動きがキーからどれだけ離れていたか。大きいキーは寄せる前後が目に見える（キーを足すか文章を変える）
            EditorGUILayout.LabelField (Tr ("KIMODO_PANEL_DEVIATIONS"), EditorStyles.wordWrappedMiniLabel);
            foreach (PoseSession.KeyDeviation deviation in job.keyDeviations) {
                string line = Tr ("KIMODO_PANEL_DEVIATION", deviation.frame, deviation.distance * 100, deviation.bone, deviation.hips * 100);
                EditorGUILayout.LabelField (deviation.distance > 0.1f ? Tr ("KIMODO_PANEL_DEVIATION_LARGE", line) : line, EditorStyles.miniLabel);
            }
        }
    }

}
