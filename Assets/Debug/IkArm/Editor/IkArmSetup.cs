using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の制御を確かめるための、棒を握った右腕のキャラ（このリポジトリだけのデバッグ用。パッケージには入れない）の、取り込みの設定・リグ定義・キャラの設定を作る。
    /// モデルは Blender でデモの男のキャラから切り出す（make_ik_arm.py。胸 spine_03 を根に、右の鎖骨・上腕・前腕・手・指。握りの中心の目印 StickGrip は手の骨の子）。
    /// 腕だけの骨は Humanoid にできないので Generic で取り込み、関節の設定（角度制限・肘の蝶番・硬さ）と点は、デモの男のキャラのリグ定義の同じ骨から写す。
    /// 棒（IkArmStick.fbx）は持ち物にする（持つ・離すをキーで切り替える）。FBX を書き出し直したら、メニューから実行し直す
    /// </summary>
    static class IkArmSetup
    {
        const string kFolder = "Assets/Debug/IkArm";
        const string kName = "IkArm";
        const string kStickModel = kFolder + "/" + kName + "Stick.fbx";
        // 握りの中心の目印（make_ik_arm.py の GRIP_NAME）。基準姿勢で棒の真ん中をここに置く
        const string kGripName = "StickGrip";
        // 前は持ち物にせず腕のリグ定義に置いていた、棒の両端の点（前に作った定義から外す）
        static readonly string[] kOldStickPoints = { "StickFront", "StickBack" };
        const string kSourceFolder = "Assets/Samples/Lilium Motion Editor/0.1.0/Demo/Sample/Quaternius";
        const string kSourceModel = kSourceFolder + "/Models/Superhero_Male_FullBody.fbx";
        const string kSourceDefinition = kSourceFolder + "/Models/Superhero_Male_FullBody Rig Definition.asset";
        const string kSourceMaterial = kSourceFolder + "/Materials/M_Superhero_Male.mat";
        // FBX の中の体の材質の名前（Blender で読んだ男のキャラの材質のまま）。デモの材質を当てる
        const string kBodyMaterial = "MI_Superhero_Male";
        // Blender の骨を入れる物（アーマチュア）。骨ではないので、リグ定義に入れない
        const string kArmatureNode = "Armature";
        const string kRootBone = "spine_03";
        const string kHandBone = "hand_r";
        const string kStick = "Stick";

        [MenuItem ("Tools/Lilium Motion Editor Debug/Set Up IK Arm")]
        public static void SetUp ()
        {
            string modelPath = kFolder + "/" + kName + ".fbx";
            ModelImporter importer = AssetImporter.GetAtPath (modelPath) as ModelImporter;
            if (importer == null) {
                Debug.LogError ("IkArmSetup: " + modelPath + " がありません。make_ik_arm.py で書き出してください");
                return;
            }
            // Blender の向き（z 上）を骨の Transform に焼き込む。いちばん上がアーマチュア 1 つでも、まとめると回転がモデルの根に移るのでまとめない
            importer.bakeAxisConversion = true;
            importer.preserveHierarchy = true;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = false;
            Material body = AssetDatabase.LoadAssetAtPath<Material> (kSourceMaterial);
            if (body != null) importer.AddRemap (new AssetImporter.SourceAssetIdentifier (typeof (Material), kBodyMaterial), body);
            importer.SaveAndReimport ();

            ModelImporter stickImporter = AssetImporter.GetAtPath (kStickModel) as ModelImporter;
            if (stickImporter != null) {
                stickImporter.bakeAxisConversion = true;
                stickImporter.animationType = ModelImporterAnimationType.None;
                stickImporter.importAnimation = false;
                stickImporter.SaveAndReimport ();
            }

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject> (modelPath);
            EditRigDefinition definition = SaveDefinition (model);
            if (definition == null) return;
            CharacterSettings settings = SaveSettings (model, definition);
            SaveHeldObject (model, settings);
            AssetDatabase.SaveAssets ();
            EditorGUIUtility.PingObject (model);
        }

        /// <summary>リグ定義は無いときだけ骨から作る（作り直しても、直した値を消さない）</summary>
        static EditRigDefinition SaveDefinition (GameObject model)
        {
            string path = kFolder + "/" + kName + " Rig Definition.asset";
            EditRigDefinition definition = AssetDatabase.LoadAssetAtPath<EditRigDefinition> (path);
            if (definition != null) {
                // 棒は持ち物の点で動かすので、前に作った定義にある棒の両端の点は外す
                if (definition.bodyPoints.RemoveAll (p => System.Array.IndexOf (kOldStickPoints, p.name) >= 0) > 0) {
                    EditorUtility.SetDirty (definition);
                    AssetDatabase.SaveAssetIfDirty (definition);
                }
                return definition;
            }
            EditRigDefinition source = AssetDatabase.LoadAssetAtPath<EditRigDefinition> (kSourceDefinition);
            Dictionary<string, HumanBodyBones> human = HumanBones ();
            if (source == null || human == null) {
                Debug.LogError ("IkArmSetup: デモの男のキャラ（" + kSourceModel + "）かそのリグ定義が無い。デモのサンプルを取り込んでください");
                return null;
            }

            definition = ScriptableObject.CreateInstance<EditRigDefinition> ();
            definition.SetFromBones (model.GetComponent<Animator> ());
            // 骨から作ると、骨の親のアーマチュアがいちばん上の骨になる。外して、胸を場所も動かす根にする
            definition.extraControls.RemoveAll (c => c.name == kArmatureNode);
            definition.bodyPoints.RemoveAll (p => p.name == kArmatureNode);
            definition.fullBody = JsonUtility.FromJson<EditRigDefinition.FullBodySettings> (JsonUtility.ToJson (source.fullBody));
            CopyJoints (definition, source, human);
            SetPoints (definition, source, human, model);
            AssetDatabase.CreateAsset (definition, path);
            return definition;
        }

        /// <summary>デモの男のキャラの FBX の骨の名前と、Humanoid の骨の対応（Avatar の割り当て）。読めなければ null</summary>
        static Dictionary<string, HumanBodyBones> HumanBones ()
        {
            ModelImporter importer = AssetImporter.GetAtPath (kSourceModel) as ModelImporter;
            if (importer == null) return null;
            Dictionary<string, HumanBodyBones> bones = new Dictionary<string, HumanBodyBones> ();
            string[] names = HumanTrait.BoneName;
            foreach (HumanBone bone in importer.humanDescription.human) {
                int index = System.Array.IndexOf (names, bone.humanName);
                if (index >= 0) bones[bone.boneName] = (HumanBodyBones)index;
            }
            return bones;
        }

        /// <summary>
        /// 骨ごとの全身 IK の設定（解くか・硬さ・蝶番・角度制限・重さ）を、デモの定義の同じ Humanoid の骨の FK コントロールから写す。
        /// 対応の無い骨（指の先の leaf）は解かない。解かない骨（指）は FK のハンドルで動かす。根（胸）は場所も動かす
        /// </summary>
        static void CopyJoints (EditRigDefinition definition, EditRigDefinition source, Dictionary<string, HumanBodyBones> human)
        {
            foreach (EditRigDefinition.ExtraControl control in definition.extraControls) {
                EditRigDefinition.FkControl fk = human.TryGetValue (control.name, out HumanBodyBones bone) ? source.FindFkControl (bone) : null;
                control.position = control.name == kRootBone;
                if (fk == null) {
                    control.fullBodySolve = false;
                    control.fullBodyHandle = true;
                    continue;
                }
                control.fullBodySolve = fk.fullBodySolve;
                control.fullBodyHandle = fk.fullBodyHandle || !fk.fullBodySolve;
                control.stiffness = fk.stiffness;
                control.hinge = fk.hinge;
                control.bendDirection = fk.bendDirection;
                control.ownWeightOnly = fk.ownWeightOnly;
                control.limit = JsonUtility.FromJson<EditRigDefinition.JointLimit> (JsonUtility.ToJson (fk.limit));
            }
        }

        /// <summary>
        /// 点は、デモの定義の点のうち、この腕で解く骨に乗るもの（胸 Chest・胸の前 ChestDirection・肩 RightShoulder・上腕・肘・手首・手のひらの 2 点）を、名前も設定もそのまま写す
        /// （重さ・向き・動かせない点 passive・つなぐ線）。点の場所は、デモの男のキャラの骨で元の決め方（骨の付け根からの割合など）のとおりに求め、
        /// 乗る骨の付け根からのずれ（体の向き。Generic の体の大きさは 1 なのでメートル）にする（胸の点は腰と頭の付け根の真ん中なので、腕のモデルに無い骨で決まる）。
        /// 棒の点は持ち物の点にする（SaveHeldObject）
        /// </summary>
        static void SetPoints (EditRigDefinition definition, EditRigDefinition source, Dictionary<string, HumanBodyBones> human, GameObject model)
        {
            Dictionary<HumanBodyBones, string> controls = new Dictionary<HumanBodyBones, string> ();
            foreach (EditRigDefinition.ExtraControl control in definition.extraControls) {
                if (control.fullBodySolve && human.TryGetValue (control.name, out HumanBodyBones bone)) controls[bone] = control.name;
            }
            definition.bodyPoints.Clear ();
            GameObject male = Object.Instantiate (AssetDatabase.LoadAssetAtPath<GameObject> (kSourceModel));
            male.hideFlags = HideFlags.HideAndDontSave;
            try {
                Animator animator = male.GetComponentInChildren<Animator> ();
                float humanScale = animator.humanScale;
                // 体の向き（右・上・前）。右は左右の上腕の付け根から、上は世界の上
                Vector3 right = animator.GetBoneTransform (HumanBodyBones.RightUpperArm).position - animator.GetBoneTransform (HumanBodyBones.LeftUpperArm).position;
                right.y = 0;
                Quaternion body = Quaternion.LookRotation (Vector3.Cross (right.normalized, Vector3.up), Vector3.up);
                foreach (EditRigDefinition.BodyPoint from in source.bodyPoints) {
                    if (from == null || !from.enabled || !string.IsNullOrEmpty (from.control) || !controls.TryGetValue (from.bone, out string control)) continue;
                    if (!TryGetRestPosition (animator, body, humanScale, from, out Vector3 position)) continue;
                    EditRigDefinition.BodyPoint point = JsonUtility.FromJson<EditRigDefinition.BodyPoint> (JsonUtility.ToJson (from));
                    point.control = control;
                    point.offset = Quaternion.Inverse (body) * (position - animator.GetBoneTransform (from.bone).position);
                    point.origin = point.toward = HumanBodyBones.LastBone;
                    point.along = point.across = 0;
                    point.axes = Vector3.one;
                    point.ground = false;
                    point.fallback = Vector3.zero;
                    if (controls.TryGetValue (from.blendBone, out string blend)) point.blendControl = blend;
                    else point.blend = 0;
                    definition.bodyPoints.Add (point);
                }
            }
            finally {
                Object.DestroyImmediate (male);
            }
            foreach (EditRigDefinition.BodyPoint point in definition.bodyPoints) {
                if (!string.IsNullOrEmpty (point.link) && !definition.bodyPoints.Exists (p => p.name == point.link)) point.link = "";
            }
        }

        /// <summary>
        /// デモの定義の点 point の、デモの男のキャラ（animator。今の姿勢＝基準姿勢）での場所（世界）。EditRigDefinition.BodyPoint の決め方:
        /// origin の骨の付け根 + along × d + across × (d を体の上の軸まわりに 90° 回した向き) + offset × humanScale（体の向き）。
        /// d は origin の付け根から toward の付け根への向き（axes の成分だけ残す）。toward が無ければ d は 0。地面へ落とす点（足）は扱わない
        /// </summary>
        static bool TryGetRestPosition (Animator animator, Quaternion body, float humanScale, EditRigDefinition.BodyPoint point, out Vector3 position)
        {
            position = Vector3.zero;
            Transform origin = animator.GetBoneTransform (point.origin == HumanBodyBones.LastBone ? point.bone : point.origin);
            if (origin == null || point.ground) return false;
            Vector3 d = Vector3.zero;
            if (point.toward != HumanBodyBones.LastBone) {
                Transform toward = animator.GetBoneTransform (point.toward);
                if (toward == null) return false;
                d = body * Vector3.Scale (Quaternion.Inverse (body) * (toward.position - origin.position), point.axes);
            }
            position = origin.position + d * point.along + Quaternion.AngleAxis (90, Vector3.up) * d * point.across + body * point.offset * humanScale;
            return true;
        }

        static Transform Find (Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform> (true)) {
                if (t.name == name) return t;
            }
            return null;
        }

        static CharacterSettings SaveSettings (GameObject model, EditRigDefinition definition)
        {
            string path = kFolder + "/" + kName + " Motion Editor.asset";
            CharacterSettings settings = AssetDatabase.LoadAssetAtPath<CharacterSettings> (path);
            if (settings == null) {
                settings = ScriptableObject.CreateInstance<CharacterSettings> ();
                settings.overrideRigDefinition = true;
                settings.rigDefinition = definition;
                AssetDatabase.CreateAsset (settings, path);
            }
            SettingsLookup.Assign (settings, model);
            EditRigDefinition.NotifyChanged (definition);
            return settings;
        }

        /// <summary>
        /// 棒を持ち物にする（無いときだけ足す。直した値は消さない）。物はキャラの根の子（付ける骨は無し）で、姿勢はルートから見た値で持つ
        /// （手・腰・胸に付ける・それらから見た値で持つ設定は Humanoid の骨で引くので、Generic のキャラには使えない）。右手で持ち、左手は持たない。
        /// キーの無いクリップでは、棒の真ん中を握りの中心（StickGrip）に置いて体の前（+Z）へ向け、そのときの手首を握りにする
        /// （手首の点の目標の向きは「物の向き × 握りの向き」で、基準姿勢ではこれが体の向き。Generic の体の向きは回転なし）。
        /// 点は棒の両端（前 Front・後ろ Back。重さ 0）と真ん中（Center。重さ 1 で向きあり）
        /// </summary>
        static void SaveHeldObject (GameObject model, CharacterSettings settings)
        {
            if (settings.heldObjects.Exists (h => h != null && h.name == kStick)) return;
            GameObject stick = AssetDatabase.LoadAssetAtPath<GameObject> (kStickModel);
            Transform grip = Find (model.transform, kGripName);
            Transform hand = Find (model.transform, kHandBone);
            if (stick == null || grip == null || hand == null) {
                Debug.LogError ("IkArmSetup: 棒の FBX（" + kStickModel + "）か、腕の " + kGripName + "・" + kHandBone + " が無い。make_ik_arm.py で書き出し直してください");
                return;
            }
            HeldObjectDefinition held = new HeldObjectDefinition {
                name = kStick, prefab = stick, parentBone = HumanBodyBones.LastBone, frame = HeldObjectFrame.Root,
            };
            // 点はメッシュの長い向きの両端と真ん中（剣の名前の Tip・Pommel を、棒の前・後ろにする）
            held.LoadPoints (stick);
            HeldObjectPoint front = held.points.Find (p => p.name == "Tip");
            HeldObjectPoint back = held.points.Find (p => p.name == "Pommel");
            HeldObjectPoint center = held.points.Find (p => p.name == HeldObjectDefinition.kCenterPointName);
            if (front == null || back == null || center == null) {
                Debug.LogError ("IkArmSetup: 棒のメッシュが無い（" + kStickModel + "）");
                return;
            }
            front.name = "Front";
            back.name = "Back";
            Vector3 axis = (front.position - back.position).normalized;
            Transform root = model.transform;
            Quaternion rotation = Quaternion.FromToRotation (axis, Vector3.forward);
            Vector3 position = root.InverseTransformPoint (grip.position) - rotation * center.position;
            held.restPosition = position;
            held.restRotation = rotation.eulerAngles;
            held.gripAxis = axis;
            held.rightGrip = new HeldObjectGrip {
                hold = true,
                position = Quaternion.Inverse (rotation) * (root.InverseTransformPoint (hand.position) - position),
                rotation = Quaternion.Inverse (rotation).eulerAngles,
            };
            held.leftGrip = new HeldObjectGrip { hold = false };
            settings.heldObjects.Add (held);
            EditorUtility.SetDirty (settings);
            AssetDatabase.SaveAssetIfDirty (settings);
            CharacterSettings.NotifyHeldObjectsChanged (settings);
        }
    }
}
