using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lilium
{

    /// <summary>
    /// パネル（Overlay）が扱うモーションエディタの窓を決める。パネルはプレビュー窓にも Motion Scene（S15d）にも載り、
    /// Motion Scene に載ったときは、姿勢を作っているプレビュー窓を扱う（2 窓構成）
    /// </summary>
    static class OverlayWindows
    {
        public static PreviewWindow Resolve (EditorWindow container)
        {
            PreviewWindow preview = container as PreviewWindow;
            if (preview != null) return preview;
            SceneWindow scene = container as SceneWindow;
            if (scene != null && scene.source != null) return scene.source;
            return Resources.FindObjectsOfTypeAll<PreviewWindow> ().FirstOrDefault ();
        }

        static readonly Dictionary<System.Type, string[]> panelIds_ = new Dictionary<System.Type, string[]> ();

        /// <summary>
        /// 窓に載るパネルの ID（[Overlay] で窓の型を指したもの）。手で並べると、足したパネルが漏れる（Lint が漏れて押し戻されなかった）
        /// </summary>
        public static string[] PanelIds (System.Type windowType)
        {
            string[] ids;
            if (panelIds_.TryGetValue (windowType, out ids)) return ids;
            ids = TypeCache.GetTypesWithAttribute<OverlayAttribute> ()
                .SelectMany (t => t.GetCustomAttributes (typeof (OverlayAttribute), false).Cast<OverlayAttribute> ())
                .Where (a => a.editorWindowType == windowType && !string.IsNullOrEmpty (a.id))
                .Select (a => a.id)
                .ToArray ();
            panelIds_[windowType] = ids;
            return ids;
        }

        const string kOverlayMenuId = "Overlays/OverlayMenu";

        sealed class MenuFold
        {
            /// <summary>ツールバーに入れたので畳んだ（帯から出したら開き直す。自分で畳んだものは開かない）</summary>
            public bool foldedByToolbar;
        }

        static readonly ConditionalWeakTable<Overlay, MenuFold> menuFolds_ = new ConditionalWeakTable<Overlay, MenuFold> ();

        /// <summary>
        /// Unity の Overlay メニュー（パネルごとの表示の切り替え）をツールバーに入れたら、目のアイコンのボタン 1 つに畳む。
        /// 帯に切り替えのボタンが横に並んで場所を取らないため。押すとパネルの一覧が出る。帯から列・浮きへ出したら開き直す。
        /// 窓の描画ごとに呼んでよい（窓のメニューごとに初めの 1 回だけ結び付ける）
        /// </summary>
        public static void FoldMenuInToolbar (EditorWindow window)
        {
            Overlay menu;
            if (window == null || !window.TryGetOverlay (kOverlayMenuId, out menu) || menu == null || menu.rootVisualElement == null) return;
            if (menuFolds_.TryGetValue (menu, out _)) return;
            MenuFold state = new MenuFold ();
            menuFolds_.Add (menu, state);
            menu.collapsedIcon = Icons.LoadBuiltin ("scenevis_visible_hover");

            System.Action apply = () => {
                if (menu.isInToolbar) {
                    state.foldedByToolbar = true;
                    if (!menu.collapsed) menu.collapsed = true;
                }
                else if (state.foldedByToolbar) {
                    state.foldedByToolbar = false;
                    menu.collapsed = false;
                }
            };
            // 枠を移した後（親が入れ替わってから）に見る
            VisualElement element = menu.rootVisualElement;
            System.Action later = () => element.schedule.Execute (apply);
            element.RegisterCallback<AttachToPanelEvent> (e => later ());
            element.RegisterCallback<GeometryChangedEvent> (e => later ());
            menu.floatingChanged += floating => later ();
            menu.layoutChanged += layout => later ();
            later ();
        }

        /// <summary>Picker を浮かせて置く既定の場所（右上。PlaceDefaults の floating に渡す）</summary>
        public static readonly Vector2 kPickerDefault = new Vector2 (-439, 63);
        /// <summary>Controller を浮かせて置く既定の場所（タイムラインの上の左寄り）</summary>
        public static readonly Vector2 kControllerDefault = new Vector2 (313, -541);

        const System.Reflection.BindingFlags kInternal = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        static readonly System.Reflection.FieldInfo kCanvasSaveData = typeof (OverlayCanvas).GetField ("m_SaveData", kInternal);
        static readonly System.Reflection.MethodInfo kDockBefore = typeof (Overlay).GetMethod ("DockBefore", kInternal, null, new[] { typeof (Overlay) }, null);
        static readonly System.Reflection.MethodInfo kDockAfter = typeof (Overlay).GetMethod ("DockAfter", kInternal, null, new[] { typeof (Overlay) }, null);

        /// <summary>
        /// [Overlay] では決められない既定の置き場所を、Unity がまだ置き場所を覚えていない窓（初めて開いた窓）に当てる。
        /// - 浮かせるパネル（floating）: [Overlay] の Floating だけだと、どれも左上に重なって出る。窓の隅からの距離で置く。
        ///   負の値は右端・下端から（Unity も近い隅からの距離で覚えるので、窓の大きさを変えても隅からの距離は保たれる）
        /// - Unity の Overlay メニュー: 既定は左の列の下に開いて出るので、上端のツールバーの menuNeighbor のパネルの隣へ移す
        ///   （menuAfter なら後ろ。右寄せの並びでは前が右、左寄せの並びでは後ろが右）
        /// 覚えているかどうか（この窓のパネルの置き場所があるか）・ドックの移し方は Unity の内部（OverlayCanvas.m_SaveData・Overlay.DockBefore / DockAfter）を使う。
        /// 見つからなければ何もしない（Unity の既定のまま）
        /// </summary>
        /// <returns>見終えた（もう呼ばなくてよい）。パネルがまだできていなければ false</returns>
        public static bool PlaceDefaults (EditorWindow window, IEnumerable<KeyValuePair<string, Vector2>> floating, string menuNeighbor, bool menuAfter)
        {
            if (window == null || window.overlayCanvas == null) return false;
            Vector2 size = window.position.size;
            if (size.x < 1 || size.y < 1 || !window.overlayCanvas.overlays.Any ()) return false;
            // この窓のパネルの置き場所が 1 つでも覚えてあれば、既定ではない（Scene ビューは Unity のパネルの置き場所を既定に持つので、数では見ない）
            System.Collections.IList saved = kCanvasSaveData != null ? kCanvasSaveData.GetValue (window.overlayCanvas) as System.Collections.IList : null;
            if (saved == null) return true;
            string[] ids = PanelIds (window.GetType ());
            foreach (object data in saved) {
                System.Reflection.FieldInfo idField = data != null ? data.GetType ().GetField ("id", kInternal | System.Reflection.BindingFlags.Public) : null;
                if (idField == null) return true;
                if (System.Array.IndexOf (ids, idField.GetValue (data) as string) >= 0) return true;
            }

            foreach (KeyValuePair<string, Vector2> place in floating) {
                Overlay overlay;
                if (!window.TryGetOverlay (place.Key, out overlay) || overlay == null || !overlay.floating) continue;
                Vector2 offset = place.Value;
                overlay.floatingPosition = new Vector2 (
                    offset.x < 0 ? size.x + offset.x : offset.x,
                    offset.y < 0 ? size.y + offset.y : offset.y);
            }
            Overlay menu, neighbor;
            System.Reflection.MethodInfo dock = menuAfter ? kDockAfter : kDockBefore;
            if (dock != null && window.TryGetOverlay (kOverlayMenuId, out menu) && menu != null
                && window.TryGetOverlay (menuNeighbor, out neighbor) && neighbor != null && neighbor.isInToolbar) {
                dock.Invoke (menu, new object[] { neighbor });
            }
            return true;
        }

        /// <summary>画面内に残す幅（見出しの左端からこの幅は見えるようにする）</summary>
        const float kVisibleWidth = 80f;
        /// <summary>画面内に残す高さ（見出しの帯）</summary>
        const float kVisibleHeight = 24f;

        /// <summary>
        /// 浮いているパネルを、見出しがつかめる位置まで窓の中へ押し戻す。Unity はパネルを窓の外まで動かせてしまい、
        /// 見出しが外に出るとマウスで戻せなくなる（窓を縮めたときも同じ）。左端と上端は窓の中、右と下は見出しの一部が残るまで
        /// </summary>
        public static void KeepInside (EditorWindow window, IEnumerable<string> ids)
        {
            if (window == null) return;
            Vector2 size = window.position.size;
            if (size.x < 1 || size.y < 1) return;
            float maxX = Mathf.Max (0f, size.x - kVisibleWidth);
            float maxY = Mathf.Max (0f, size.y - kVisibleHeight);
            foreach (string id in ids) {
                Overlay overlay;
                if (!window.TryGetOverlay (id, out overlay) || overlay == null) continue;
                if (!overlay.displayed || !overlay.floating) continue;
                Vector2 position = overlay.floatingPosition;
                Vector2 clamped = new Vector2 (Mathf.Clamp (position.x, 0f, maxX), Mathf.Clamp (position.y, 0f, maxY));
                if (clamped != position) overlay.floatingPosition = clamped;
            }
        }
    }

}
