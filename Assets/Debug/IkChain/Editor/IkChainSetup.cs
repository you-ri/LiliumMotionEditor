using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の制御を確かめる・調整するための鎖と紐のキャラ（このリポジトリだけのデバッグ用。パッケージには入れない）の、取り込みの設定・リグ定義・キャラの設定を作る。
    /// 人型の体だと腰・背骨・手足の割り振りが絡んで、解き方の癖が見分けにくいので、Generic の一本の鎖で試す。
    /// モデルは Blender で作る（make_ik_chain.py。輪・紐の節 1 つが骨 1 本で、Root・Joint1〜Joint5・End。両端の Root・End は同じ形の台）。
    /// 鎖（IkChain）は関節ごとに角度制限を入れ、紐（IkRope。make_ik_chain.py に rope を付けて書き出す）は入れない。FBX を書き出し直したら、メニューから実行し直す
    /// </summary>
    static class IkChainSetup
    {
        const string kFolder = "Assets/Debug/IkChain";
        // 作るキャラ: 名前（FBX・定義・設定のファイル名）と、関節に角度制限を入れるか
        static readonly (string name, bool limited)[] kModels = { ("IkChain", true), ("IkRope", false) };
        // 輪ごとのひねりの範囲（度）。根元より先の 6 本（Joint1〜Joint5・End）で、合わせて (5 + 遊び 5) × 6 = 60° までひねれる
        const float kTwistRange = 5;
        // 輪ごとの曲がり（振りの 2 軸）の範囲（度）。噛み合った輪は、相手の針金に当たるまでしか曲がらない。
        // 制限が無いと、たるませたときに 1 つの関節だけが 90° 以上折れて輪が重なる
        const float kBendRange = 60;
        // 遊び（度。骨ごとに 1 つで、ひねりと曲がりで共通）
        const float kSlack = 5;
        // End の台の外側の面の、End の付け根からの高さ（make_ik_chain.py の ANCHOR_RISE + BASE_HEIGHT）。Root の点（台の底）と対にする
        const float kEndPlateHeight = 0.15f;
        // 台（円盤）の半径（make_ik_chain.py の BASE_RADIUS）。縁の点を置く
        const float kPlateRadius = 0.15f;
        // Blender の骨を入れる物（アーマチュア）。骨ではないので、リグ定義に入れない
        const string kArmatureNode = "Armature";

        [MenuItem ("Tools/Lilium Motion Editor Debug/Set Up IK Chain")]
        public static void SetUp ()
        {
            foreach ((string name, bool limited) in kModels) SetUp (name, limited);
            AssetDatabase.SaveAssets ();
        }

        static void SetUp (string name, bool limited)
        {
            string modelPath = kFolder + "/" + name + ".fbx";
            ModelImporter importer = AssetImporter.GetAtPath (modelPath) as ModelImporter;
            if (importer == null) {
                Debug.LogError ("IkChainSetup: " + modelPath + " がありません。make_ik_chain.py で書き出してください");
                return;
            }
            // Blender の向き（z 上）を骨の Transform に焼き込み、アーマチュアに回転を残さない
            importer.bakeAxisConversion = true;
            // FBX のいちばん上が骨の入れ物（アーマチュア）1 つだけなので、まとめるとその回転（Blender の向きの 90°）がモデルの根に移り、体の向きが倒れる
            importer.preserveHierarchy = true;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = false;
            importer.SaveAndReimport ();

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject> (modelPath);
            EditRigDefinition definition = SaveDefinition (model, name, limited);
            SaveSettings (model, definition, name);
        }

        /// <summary>リグ定義は無いときだけ骨から作る（作り直しても、直した値を消さない）</summary>
        static EditRigDefinition SaveDefinition (GameObject model, string name, bool limited)
        {
            string path = kFolder + "/" + name + " Rig Definition.asset";
            EditRigDefinition definition = AssetDatabase.LoadAssetAtPath<EditRigDefinition> (path);
            if (definition != null) {
                // 後から足した点は、前に作った定義にも足す（ほかの値は直したまま残す）
                if (AddEdgePoints (definition)) {
                    EditorUtility.SetDirty (definition);
                    AssetDatabase.SaveAssetIfDirty (definition);
                }
                return definition;
            }
            definition = ScriptableObject.CreateInstance<EditRigDefinition> ();
            definition.SetFromBones (model.GetComponent<Animator> ());
            // 骨から作ると、骨の親のアーマチュアがいちばん上の骨になる。外して、台の骨（Root）を場所も動かす根にする
            definition.extraControls.RemoveAll (c => c.name == kArmatureNode);
            definition.bodyPoints.RemoveAll (p => p.name == kArmatureNode);
            definition.extraControls.Find (c => c.name == "Root").position = true;
            // 根（Root）を元の場所に留めない。両端は同じ形の台なので、どちらを動かしても同じ動きにする（留めると Root 側だけが残りやすい）
            definition.fullBody.hipsPin = 0;
            LimitJoints (definition, limited);
            SetPoints (definition);
            AssetDatabase.CreateAsset (definition, path);
            return definition;
        }

        /// <summary>
        /// 噛み合った輪は、隣の輪に対してほとんどひねれず、曲がりも相手の針金に当たるまで。limited なら根元より先の輪ごとに、
        /// ひねり（骨の向きの上 y 軸まわり）を狭い範囲に、曲がり（右 x・前 z 軸まわり）をそれより広い範囲にする（紐は入れない）。
        /// 重さは鎖も紐も、節ごとに自分の分だけにする（先の節を足すと根元の節が回りにくく、たるませたときに曲がりが End 側に集まる）
        /// </summary>
        static void LimitJoints (EditRigDefinition definition, bool limited)
        {
            foreach (EditRigDefinition.ExtraControl control in definition.extraControls) {
                if (control.name == "Root") continue;
                control.ownWeightOnly = true;
                if (!limited) continue;
                control.limit.x = Range (kBendRange);
                control.limit.y = Range (kTwistRange);
                control.limit.z = Range (kBendRange);
                control.limit.slack = kSlack;
            }
        }

        static EditRigDefinition.AxisLimit Range (float range)
        {
            return new EditRigDefinition.AxisLimit { mode = EditRigDefinition.LimitMode.Range, min = -range, max = range };
        }

        /// <summary>
        /// 両端の台の点は重さ 1 で向きも持つ（台ごと動かす・留める）。間の輪の点は重さ 0（引いても両端の台は動かず、間の輪だけが付いてくる）。
        /// End の点は台の外側の面に置き、Root の点（台の底）と対にする。End の付け根（Joint5 と噛み合う所）には、根元側の Joint1 の点と対になる
        /// 重さ 0 の点（EndJoint）を足す（骨から作ると点は骨の付け根に 1 つずつなので、End の点を台の面へ動かすと、ここに点が無くなる）。両端の台の縁にも点を足す
        /// </summary>
        static void SetPoints (EditRigDefinition definition)
        {
            foreach (EditRigDefinition.BodyPoint point in definition.bodyPoints) {
                bool fixture = point.name == "Root" || point.name == "End";
                point.weight = fixture ? 1 : 0;
                point.rotation = fixture;
                if (point.name == "End") point.offset = new Vector3 (0, kEndPlateHeight, 0);
            }
            int end = definition.bodyPoints.FindIndex (p => p.name == "End");
            definition.bodyPoints.Insert (end, new EditRigDefinition.BodyPoint {
                name = "EndJoint", control = "End", origin = HumanBodyBones.LastBone, toward = HumanBodyBones.LastBone, weight = 0,
            });
            AddEdgePoints (definition);
        }

        /// <summary>
        /// 両端の台の縁の点（右 +X・前 +Z）。台の真ん中の点と同じ面に置き、真ん中の点と合わせた 3 点で台の向きまで決められるようにする。
        /// 台の点なので重さ 1。無い点だけ、その台の真ん中の点の後ろへ足す。足したら true
        /// </summary>
        static bool AddEdgePoints (EditRigDefinition definition)
        {
            bool added = false;
            foreach ((string plate, float height) in new[] { ("Root", 0f), ("End", kEndPlateHeight) }) {
                foreach ((string side, Vector3 direction) in new[] { ("Right", Vector3.right), ("Front", Vector3.forward) }) {
                    string name = plate + side;
                    if (definition.bodyPoints.Exists (p => p.name == name)) continue;
                    int center = definition.bodyPoints.FindIndex (p => p.name == plate);
                    definition.bodyPoints.Insert (center + 1 + (side == "Front" ? 1 : 0), new EditRigDefinition.BodyPoint {
                        name = name, control = plate, origin = HumanBodyBones.LastBone, toward = HumanBodyBones.LastBone, weight = 1,
                        offset = direction * kPlateRadius + Vector3.up * height,
                    });
                    added = true;
                }
            }
            return added;
        }

        static void SaveSettings (GameObject model, EditRigDefinition definition, string name)
        {
            string path = kFolder + "/" + name + " Motion Editor.asset";
            CharacterSettings settings = AssetDatabase.LoadAssetAtPath<CharacterSettings> (path);
            if (settings == null) {
                settings = ScriptableObject.CreateInstance<CharacterSettings> ();
                settings.overrideRigDefinition = true;
                settings.rigDefinition = definition;
                AssetDatabase.CreateAsset (settings, path);
            }
            SettingsLookup.Assign (settings, model);
            EditRigDefinition.NotifyChanged (definition);
        }
    }
}
