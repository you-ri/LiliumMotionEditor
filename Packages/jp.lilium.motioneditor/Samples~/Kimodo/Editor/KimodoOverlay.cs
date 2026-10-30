using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
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
    /// ✏ を移したまま打ったキーは Override に入り次の生成に渡らないので、「結果を見る」と「キーを直す」を切り替えられるようにする。
    /// キーの間の補間として使うときは、キーが変わるたびに自動で作り直せる（S85）。生成中はきらめきのアイコンが動く
    /// </summary>
    // 既定は上端のツールバーの右（View の次）。パネルだけなのでボタンに畳まれる
    [Overlay (typeof (PreviewWindow), kId, "Kimodo", defaultDisplay = true,
        defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Bottom, defaultDockIndex = 2)]
    sealed class KimodoOverlay : Overlay
    {
        public const string kId = "lilium-kimodo";
        const float kWidth = 300;
        const float kMinWidth = 230;
        /// <summary>入りきらないときも、これだけは見せる（スクロールして使える高さ）</summary>
        const float kMinHeight = 140;
        /// <summary>キーが変わってから自動で作り始めるまで（秒）。打ち続けている間やつかんで動かしている間に、1 つごとに始めないように</summary>
        const double kAutoDelay = 1.5;
        /// <summary>説明やシードを書き換えてから .meta に保存するまで（秒）。保存するとクリップを取り込み直すので、1 文字ごとにはしない</summary>
        const double kSaveDelay = 1;

        static readonly string[] kTargets = { KimodoJob.kBetween, KimodoJob.kOverride };

        PreviewWindow window_;
        VisualElement root_;
        IMGUIContainer gui_;
        /// <summary>補間として使うときに外部の動きにする区間（始まりのフレーム）と、それを選んだクリップ（S83）</summary>
        List<int> chosen_ = new List<int> ();
        AnimationClip chosenClip_;
        /// <summary>最後に見た区間の始まり。キーの並びが変わったことを知るため</summary>
        List<int> knownStarts_ = new List<int> ();
        /// <summary>最後に見たとき外部の動きだった区間（「始まり-終わり」）。手で外したことを知るため（S85）</summary>
        List<string> seenExternal_ = new List<string> ();
        string problem_;
        /// <summary>
        /// IMGUI の中身の高さ。IMGUIContainer は中身が伸び縮みしても自分の高さを変えないので（はみ出して下のパネルに重なった）、
        /// 描いた中身の高さを測って合わせる
        /// </summary>
        float height_ = 200;
        Vector2 scroll_;
        /// <summary>自動で作るかを確かめる時刻（キーが変わったとき。0 未満なら待っていない。生成中は終わるまで持ち越す）</summary>
        double autoAt_ = -1;
        /// <summary>今パネルのアイコン（collapsedIcon）に出している絵。コマが替わったときだけ差し替える</summary>
        Texture2D shownIcon_;
        EditorToolbarToggle toolbarToggle_;
        /// <summary>今のクリップの説明とシード（.meta に持つ）と、その持ち主のクリップ</summary>
        KimodoClipSettings clipSettings_;
        AnimationClip clipSettingsClip_;
        /// <summary>説明やシードを .meta に保存する時刻（0 未満なら保存するものが無い）</summary>
        double saveAt_ = -1;

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
            shownIcon_ = KimodoSetup.icon;
            collapsedIcon = shownIcon_;
            // 自動で作るのとアイコンを動かすのは、パネルをたたんでいて中身を作っていないときもする
            window_ = containerWindow as PreviewWindow;
            Subscribe (true);
        }

        public override VisualElement CreatePanelContent ()
        {
            Subscribe (false);
            if (containerWindow is PreviewWindow window) window_ = window;
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
            SaveClipSettings ();
            base.OnWillBeDestroyed ();
        }

        /// <summary>知らせを付ける・外す（外してから付けるので、何度呼んでも二重にならない）</summary>
        void Subscribe (bool on)
        {
            KimodoRunner.changed -= Repaint;
            KimodoTextServer.changed -= Repaint;
            EditorApplication.update -= Tick;
            if (window_ != null) {
                window_.stateChanged -= Repaint;
                window_.keysChanged -= OnKeysChanged;
            }
            if (!on) return;
            KimodoRunner.changed += Repaint;
            KimodoTextServer.changed += Repaint;
            EditorApplication.update += Tick;
            if (window_ != null) {
                window_.stateChanged += Repaint;
                window_.keysChanged += OnKeysChanged;
            }
        }

        void OnKeysChanged ()
        {
            if (KimodoSettings.instance.auto) autoAt_ = EditorApplication.timeSinceStartup + kAutoDelay;
            Repaint ();
        }

        /// <summary>今のクリップの説明とシード（クリップが替わったら、前のクリップへ保存してから読み直す）</summary>
        KimodoClipSettings ClipSettings ()
        {
            AnimationClip clip = window_ != null ? window_.baseClip : null;
            if (clipSettings_ != null && clip == clipSettingsClip_) return clipSettings_;
            SaveClipSettings ();
            clipSettings_ = KimodoClipSettings.Load (clip);
            clipSettingsClip_ = clip;
            return clipSettings_;
        }

        /// <summary>書き換えた説明とシードを、まだ保存していなければ保存する</summary>
        void SaveClipSettings ()
        {
            if (saveAt_ < 0) return;
            saveAt_ = -1;
            if (clipSettings_ != null) clipSettings_.Save (clipSettingsClip_);
        }

        void Tick ()
        {
            if (saveAt_ >= 0 && EditorApplication.timeSinceStartup >= saveAt_) SaveClipSettings ();
            KimodoJob job = KimodoRunner.job;
            Animate (job != null && job.running);
            ShowInProgress (job);
            AutoGenerate (job);
        }

        /// <summary>
        /// 生成中はきらめきを動かす（S85）。たたんだパネルのアイコン・窓のツールバーのパネルのボタンと、パネルの中の「作る」ボタンと進み具合の行。
        /// コマが替わったときだけ差し替えて描き直す（update は毎フレーム来る）
        /// </summary>
        void Animate (bool busy)
        {
            Texture2D icon = busy ? KimodoSetup.BusyIcon (EditorApplication.timeSinceStartup) : KimodoSetup.icon;
            if (icon == shownIcon_) return;
            shownIcon_ = icon;
            collapsedIcon = icon;
            EditorToolbarToggle toggle = FindToolbarToggle ();
            if (toggle != null) toggle.icon = icon;
            Repaint ();
        }

        /// <summary>
        /// 窓のツールバーの、パネルを出し入れするボタン（Unity が作る。パネルごとに作ったときのアイコンを写すので、collapsedIcon を替えても変わらない）。
        /// ボタンは作り直されることがあるので、外れていたら探し直す。見つからなければ null（ツールバーを出していない）
        /// </summary>
        EditorToolbarToggle FindToolbarToggle ()
        {
            if (toolbarToggle_ != null && toolbarToggle_.panel != null) return toolbarToggle_;
            toolbarToggle_ = null;
            VisualElement root = window_ != null && window_.rootVisualElement.panel != null ? window_.rootVisualElement.panel.visualTree : null;
            if (root == null) return null;
            toolbarToggle_ = root.Query<EditorToolbarToggle> ().Where (t => t.tooltip == displayName && InToolbarOverlay (t)).First ();
            return toolbarToggle_;
        }

        static bool InToolbarOverlay (VisualElement element)
        {
            for (VisualElement parent = element.parent; parent != null; parent = parent.parent) {
                if (parent.ClassListContains ("unity-toolbar-overlay")) return true;
            }
            return false;
        }

        /// <summary>
        /// この窓のクリップのキーの間を作っている間は、作っている区間を窓に知らせる（S85。タイムラインがその区間の波線を流す）。
        /// 窓はこれを保存しないので、再読み込みの後もここで入れ直す
        /// </summary>
        void ShowInProgress (KimodoJob job)
        {
            if (window_ == null) return;
            bool making = job != null && job.running && job.target == KimodoJob.kBetween
                && window_.baseClip != null && AssetDatabase.GetAssetPath (window_.baseClip) == job.baseClip;
            if (making == window_.betweenInProgress.Count > 0) return;
            window_.SetBetweenInProgress (making ? job.segments : null);
        }

        /// <summary>
        /// キーが変わって少し経ったら、キーの間の動きを作り直す（S85）。今のキーで作ったことがある（指紋が同じ）なら作らない。
        /// 補間として取り込むとキーが足されて知らせが来るが、取り込んだ後の指紋と比べるので、作り直しが続かない。
        /// 生成中にキーが変わったら、終わって取り込んでから比べる（取り込んだ結果は古いキーのもの。送った指紋のままなので作り直す）
        /// </summary>
        void AutoGenerate (KimodoJob job)
        {
            if (autoAt_ < 0 || EditorApplication.timeSinceStartup < autoAt_) return;
            // 取り込みを待っている・少しずつ回している間も待つ（始めると仕事が置き換わり、作った動きが取り込まれない）
            if (job != null && (job.running || job.importPending)) return;
            autoAt_ = -1;
            KimodoSettings settings = KimodoSettings.instance;
            if (!settings.auto || settings.target != KimodoJob.kBetween || !KimodoSetup.ready) return;
            AnimationClip clip = window_ != null ? window_.baseClip : null;
            KimodoClipSettings clipSettings = ClipSettings ();
            if (clip == null || !EditorUtility.IsPersistent (clip) || string.IsNullOrWhiteSpace (clipSettings.prompt)) return;
            // 先読みが立ち上がりかけなら待つ（Start は断る）
            if (KimodoTextServer.state == KimodoTextServer.State.Starting) {
                autoAt_ = EditorApplication.timeSinceStartup + kAutoDelay;
                return;
            }
            if (KimodoRunner.Fingerprint (clip) == KimodoRunner.GeneratedFingerprint (clip)) return;
            List<BetweenClips.Range> segments = Segments ();
            RefreshChosen (clip, segments);
            List<BetweenClips.Range> chosen = segments.Where (r => chosen_.Contains (r.start)).ToList ();
            if (chosen.Count == 0) return;
            problem_ = KimodoRunner.Start (window_, clipSettings.prompt, settings.mode, KimodoJob.kBetween, chosen, true, clipSettings.seedToPass);
            Repaint ();
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
            // ツールバーのボタンから開いた吹き出しは、最初に測った高さを上限（maxHeight）にして伸びなくなる
            // （開いた直後は中身を測る前の高さなので、下が切れた）。上限を外して測り直させる
            VisualElement popup = Popup ();
            if (popup != null) popup.style.maxHeight = StyleKeyword.Null;
        }

        /// <summary>ツールバーにたたんだパネルをボタンから開いたときの吹き出し（Unity の OverlayPopup。internal なので型の名前で見る）。それ以外は null</summary>
        VisualElement Popup ()
        {
            for (VisualElement element = root_.parent; element != null; element = element.parent) {
                if (element.GetType ().Name == "OverlayPopup") return element;
            }
            return null;
        }

        /// <summary>
        /// パネルの中身に使える高さ。列にドックしているときは、列の高さから同じ列のほかのパネルを引いた残り
        /// （列はスクロールせず、入りきらないパネルは上のパネルに重なった）。浮いているときは窓の高さ。
        /// 吹き出しで開いているときは、吹き出しの上端から窓の下端まで
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
            VisualElement popup = Popup ();
            if (popup != null && canvas != null) {
                available = canvas.worldBound.yMax - popup.worldBound.yMin - chrome - 8;
            }
            else if (!floating && column != null) {
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
            // 説明とシードはクリップごと（.meta）。それ以外はこの PC の設定
            KimodoClipSettings clipSettings = ClipSettings ();
            EditorGUI.BeginChangeCheck ();
            EditorGUILayout.LabelField (Tr ("KIMODO_PANEL_PROMPT"), EditorStyles.miniLabel);
            clipSettings.prompt = EditorGUILayout.TextArea (clipSettings.prompt, EditorStyles.textArea, GUILayout.MinHeight (120));
            using (new EditorGUILayout.HorizontalScope ()) {
                clipSettings.useSeed = EditorGUILayout.ToggleLeft (new GUIContent (Tr ("KIMODO_PANEL_SEED"), Tr ("KIMODO_PANEL_SEED_TOOLTIP")), clipSettings.useSeed);
                using (new EditorGUI.DisabledScope (!clipSettings.useSeed)) {
                    clipSettings.seed = Mathf.Max (0, EditorGUILayout.IntField (clipSettings.seed, GUILayout.Width (80)));
                }
            }
            if (EditorGUI.EndChangeCheck ()) saveAt_ = EditorApplication.timeSinceStartup + kSaveDelay;

            EditorGUI.BeginChangeCheck ();
            int mode = System.Array.IndexOf (KimodoSettings.kModes, settings.mode);
            mode = EditorGUILayout.Popup (Tr ("KIMODO_PANEL_CONSTRAINTS"), Mathf.Max (0, mode), new[] { Tr ("KIMODO_PANEL_MODE_FULL_BODY"), Tr ("KIMODO_PANEL_MODE_ENDS"), Tr ("KIMODO_PANEL_MODE_ROOT") });
            settings.mode = KimodoSettings.kModes[mode];
            int target = Mathf.Max (0, System.Array.IndexOf (kTargets, settings.target));
            target = EditorGUILayout.Popup (Tr ("KIMODO_PANEL_TARGET"), target, new[] { Tr ("KIMODO_PANEL_TARGET_BETWEEN"), Tr ("KIMODO_PANEL_TARGET_OVERRIDE") });
            settings.target = kTargets[target];
            bool auto = settings.auto;
            if (settings.target == KimodoJob.kBetween) {
                settings.auto = EditorGUILayout.ToggleLeft (new GUIContent (Tr ("KIMODO_PANEL_AUTO"), Tr ("KIMODO_PANEL_AUTO_TOOLTIP")), settings.auto);
            }
            if (EditorGUI.EndChangeCheck ()) settings.Save ();
            // 入れたら、今のキーでまだ作っていなければすぐ作る（次にキーを打つまで待たない）
            if (settings.auto && !auto) autoAt_ = EditorApplication.timeSinceStartup;
            if (settings.target == KimodoJob.kBetween) DrawSegments ();

            EditorGUILayout.Space ();
            bool running = job != null && job.running;
            using (new EditorGUILayout.HorizontalScope ()) {
                using (new EditorGUI.DisabledScope (running || window_ == null)) {
                    // 生成中はボタンのきらめきも動かす（押せないが、何をしているかが分かる）
                    Vector2 iconSize = EditorGUIUtility.GetIconSize ();
                    EditorGUIUtility.SetIconSize (new Vector2 (16, 16));
                    GUIContent generate = new GUIContent (" " + Tr ("KIMODO_PANEL_GENERATE"), running ? KimodoSetup.BusyIcon (EditorApplication.timeSinceStartup) : KimodoSetup.icon);
                    bool pressed = GUILayout.Button (generate, GUILayout.Height (24));
                    EditorGUIUtility.SetIconSize (iconSize);
                    if (pressed) {
                        // AI の区間が 1 つも無い（タイムラインで全部外した）ときに押したら、全部の区間を AI にする
                        List<BetweenClips.Range> chosen = ChosenSegments ();
                        if (chosen.Count == 0) chosen = Segments ();
                        SaveClipSettings ();
                        problem_ = KimodoRunner.Start (window_, clipSettings.prompt, settings.mode, settings.target, chosen, false, clipSettings.seedToPass);
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
        /// 補間として使う区間を今のキーに合わせる（S83）。Kimodo は毎回クリップ全体（最大 10 秒）を作り直し、結果のファイルを丸ごと置き換えるので、
        /// パネルでは区間を選ばせない（10-26 ユーザー指示。前は区間のボタンを並べていた）。どの区間を AI にするかはタイムラインの矢印のメニューで決める。
        /// キーが 2 つ未満なら作れないことだけ出す
        /// </summary>
        void DrawSegments ()
        {
            AnimationClip clip = window_ != null ? window_.baseClip : null;
            List<BetweenClips.Range> segments = Segments ();
            RefreshChosen (clip, segments);
            if (segments.Count == 0) EditorGUILayout.HelpBox (Tr ("KIMODO_PANEL_NO_SEGMENTS"), MessageType.None);
        }

        /// <summary>
        /// 選んだ区間を今のキーに合わせる。クリップが替わったら、外部の動きを使っている区間（無ければ全部）を選び直す。
        /// 同じクリップでキーの並びが変わったら、残った区間の選択を引き継ぎ、新しい区間を選ぶ。
        /// 描くときと、自動で作るとき（S85。パネルをたたんでいると描かれない）に呼ぶ
        /// </summary>
        void RefreshChosen (AnimationClip clip, List<BetweenClips.Range> segments)
        {
            List<int> starts = segments.Select (r => r.start).ToList ();
            if (clip != chosenClip_) {
                chosenClip_ = clip;
                List<int> external = segments.Where (r => IsExternal (clip, r)).Select (r => r.start).ToList ();
                chosen_ = external.Count > 0 ? external : new List<int> (starts);
                knownStarts_ = starts;
                seenExternal_.Clear ();
            } else if (!starts.SequenceEqual (knownStarts_)) {
                // キーを打ち足したり動かしたりした。残った区間は選んだままにし、新しくできた区間は選ぶ
                // （選び直さないと、消えた区間の始まりだけが残り、新しい区間が Kimodo にならない）。
                // 1 フレームの区間は選ばない（間のフレームが無い。外部の動きをキーにすると毎フレームにキーができ、自動で作ると全部を作り直していた）
                chosen_ = segments.Where (r => chosen_.Contains (r.start) || !knownStarts_.Contains (r.start) && r.end - r.start >= 2).Select (r => r.start).ToList ();
                if (chosen_.Count == 0) chosen_ = new List<int> (starts);
                knownStarts_ = starts;
            }
            // 外部の動きだった区間を、同じ区間のまま曲線・リニア・一定にした（タイムラインのメニューなど）なら、選びから外す。
            // 外さないと、自動で作り直したときに外部の動きへ戻り、選んだ補間が消えた（10-25 の不具合）
            List<string> seen = new List<string> ();
            foreach (BetweenClips.Range range in segments) {
                string key = range.start + "-" + range.end;
                if (IsExternal (clip, range)) seen.Add (key);
                else if (seenExternal_.Contains (key)) chosen_.Remove (range.start);
            }
            seenExternal_ = seen;
        }

        static bool IsExternal (AnimationClip clip, BetweenClips.Range range)
        {
            BetweenClips.State state = BetweenClips.GetSegmentState (clip, range);
            return state == BetweenClips.State.Fresh || state == BetweenClips.State.Stale;
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
                Vector2 iconSize = EditorGUIUtility.GetIconSize ();
                EditorGUIUtility.SetIconSize (new Vector2 (16, 16));
                string text = job.auto ? Tr ("KIMODO_PANEL_GENERATING_AUTO", elapsed.ToString (@"m\:ss"), hint)
                    : Tr ("KIMODO_PANEL_GENERATING", elapsed.ToString (@"m\:ss"), hint);
                GUIContent generating = new GUIContent (text, KimodoSetup.BusyIcon (EditorApplication.timeSinceStartup));
                EditorGUILayout.LabelField (generating, EditorStyles.wordWrappedMiniLabel);
                EditorGUIUtility.SetIconSize (iconSize);
                // 作っている間にキーが変わった。終わって取り込んだら作り直す
                if (autoAt_ >= 0) EditorGUILayout.LabelField (Tr ("KIMODO_PANEL_AUTO_PENDING"), EditorStyles.wordWrappedMiniLabel);
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
