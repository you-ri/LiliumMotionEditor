using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;

namespace Lilium
{

    /// <summary>
    /// 窓を開かずにモーションを作る操作口（MotionEditorCommands）を、Unity CLI（com.unity.pipeline）のコマンドとして出す（S38）。
    /// unity cmd motion_open --prefab ... --clip ... のように呼べる。Pipeline が入っているときだけコンパイルする（MKT_PIPELINE）。
    /// 結果は操作口の応答の result をそのまま返す。失敗は例外にして、CLI でも失敗として見えるようにする
    /// </summary>
    static class MotionEditorPipelineCommands
    {
        const string kTag = "motion-editor";

        [CliCommand ("motion_open", "Open a character and a motion editor clip (*.rig.anim) without the window. Returns the character, clip, key frames and full-body IK points", Tags = new[] { kTag })]
        public static JToken Open (
            [CliArg ("prefab", "Character prefab path (Assets/...)", Required = true)] string prefab,
            [CliArg ("clip", "Editing clip path (Assets/....rig.anim)", Required = true)] string clip,
            [CliArg ("create", "Create the clip if it does not exist")] bool create = false,
            [CliArg ("definition", "Rig definition path (empty uses the project setting)")] string definition = null)
        {
            return Run (new MotionEditorRequest { command = "open", prefab = prefab, clip = clip, create = create, definition = definition });
        }

        [CliCommand ("motion_describe", "Describe the open character and clip (key frames, writable range, full-body IK points)", Tags = new[] { kTag })]
        public static JToken Describe ()
        {
            return Run (new MotionEditorRequest { command = "describe" });
        }

        [CliCommand ("motion_read_pose", "Read the pose at a frame as full-body IK points (body frame: x right, y up, z forward, meters from the feet; rotation in degrees from the T-pose)", Tags = new[] { kTag })]
        public static JToken ReadPose (
            [CliArg ("frame", "Frame to read")] int frame = 0,
            [CliArg ("compact", "Round positions to 1 mm and rotations to 0.1 degrees, and omit the position/rotation of Free points and unused rotation fields")] bool compact = true)
        {
            return Run (new MotionEditorRequest { command = "readPose", frame = frame, compact = compact });
        }

        [CliCommand ("motion_write_pose", "Write a pose (the result of motion_read_pose, edited) as keys at pose.frame. Only the listed points are changed. Returns the pose read back, with residual per point (meters from its target), unreached (points farther than 1 cm) and issues (floor and muscle range at that frame)", Tags = new[] { kTag })]
        public static JToken WritePose (
            [CliArg ("pose", "Pose JSON: {\"frame\":0,\"points\":[{\"name\":\"LeftHand\",\"state\":\"Pin\",\"position\":{\"x\":0,\"y\":1,\"z\":0.3}}]}. Use pose_file instead when shell quoting gets in the way")] string pose = null,
            [CliArg ("pose_file", "Path of a file with the pose JSON (absolute, or relative to the project folder)")] string poseFile = null,
            [CliArg ("dry_run", "Solve and return the pose without keying it")] bool dryRun = false,
            [CliArg ("compact", "Return the pose rounded and without unused fields (same as motion_read_pose)")] bool compact = true)
        {
            if (!string.IsNullOrEmpty (pose) && !string.IsNullOrEmpty (poseFile)) throw new System.ArgumentException ("Pass either pose or pose_file, not both");
            if (string.IsNullOrEmpty (pose)) return Run (new MotionEditorRequest { command = "writePose", poseFile = poseFile, dryRun = dryRun, compact = compact });
            return Run ("{\"command\":\"writePose\",\"dryRun\":" + (dryRun ? "true" : "false") + ",\"compact\":" + (compact ? "true" : "false") + ",\"pose\":" + pose + "}");
        }

        [CliCommand ("motion_write_motion", "Write every key of a motion in one call: {\"keys\":[{\"frame\":0,\"points\":[...],\"hands\":[{\"preset\":\"Hand_Fist\",\"hand\":\"Both\",\"muscles\":[...]}]}]} (the shape motion_read_motion returns). Keys are written in frame order, body then hands. Everything is checked first; nothing is written if any key is wrong. One motion_undo reverts it all. Returns per key the max residual and unreached points, then the lint report", Tags = new[] { kTag })]
        public static JToken WriteMotion (
            [CliArg ("file", "Path of a file with the keys JSON (absolute, or relative to the project folder)", Required = true)] string file,
            [CliArg ("replace", "Delete every key of the clip before writing (start over)")] bool replace = false,
            [CliArg ("lint", "Check the whole motion after writing")] bool lint = true)
        {
            string error;
            string json = MotionEditorCommands.ReadJsonFile (file, out error);
            if (error != null) throw new System.InvalidOperationException (error);
            MotionEditorRequest request;
            try {
                request = UnityEngine.JsonUtility.FromJson<MotionEditorRequest> (json);
            }
            catch (System.Exception e) {
                throw new System.InvalidOperationException (file + ": " + e.Message);
            }
            request.command = "writeMotion";
            request.replace = replace;
            request.lint = lint;
            return Run (request);
        }

        [CliCommand ("motion_read_motion", "Read every key frame of the clip as {\"keys\":[{\"frame\":...,\"points\":[...],\"hands\":[...]}]} (compact poses and finger muscles), the same shape motion_write_motion takes", Tags = new[] { kTag })]
        public static JToken ReadMotion ()
        {
            return Run (new MotionEditorRequest { command = "readMotion" });
        }

        [CliCommand ("motion_delete_keys", "Delete the keys at the given frames (every curve of the write target, fingers included), or all keys. Nothing is deleted if a frame has no key. On a cycle clip, deleting the cycle (last) key removes the cycle mark; otherwise the last key is synced to the first again. One motion_undo restores them", Tags = new[] { kTag })]
        public static JToken DeleteKeys (
            [CliArg ("frames", "Frames to delete, comma separated (e.g. 10,20)")] string frames = null,
            [CliArg ("all", "Delete every key")] bool all = false)
        {
            return Run (new MotionEditorRequest { command = "deleteKeys", frames = SplitInts (frames), all = all });
        }

        [CliCommand ("motion_sample", "Sample point positions (body frame, mm steps), speeds (m/s), ground contact and muscle values per frame, as columns per point: {\"frames\":[...],\"points\":[{\"name\":...,\"x\":[...],\"y\":[...],\"z\":[...],\"speed\":[...],\"contact\":[...]}],\"muscles\":[...]}", Tags = new[] { kTag })]
        public static JToken Sample (
            [CliArg ("start", "First frame")] int start = 0,
            [CliArg ("end", "Last frame (-1 is the end of the clip)")] int end = -1,
            [CliArg ("step", "Frame step")] int step = 1,
            [CliArg ("points", "Point names, comma separated (empty for every point)")] string points = null,
            [CliArg ("muscles", "Muscle names, comma separated (e.g. Spine Front-Back,Left Upper Leg In-Out). Empty for none")] string muscles = null)
        {
            return Run (new MotionEditorRequest { command = "sample", start = start, end = end, step = step, pointNames = SplitNames (points), muscleNames = SplitNames (muscles) });
        }

        [CliCommand ("motion_undo", "Undo the last motion_write_pose / motion_write_hand. Refused if the clip changed after that write (edited in the window, undone...). Returns the same as motion_describe", Tags = new[] { kTag })]
        public static JToken Undo ()
        {
            return Run (new MotionEditorRequest { command = "undo" });
        }

        [CliCommand ("motion_lint", "Check the motion for foot sliding, floor penetration, out-of-range muscles, pops and large rotations between keys", Tags = new[] { kTag })]
        public static JToken Lint (
            [CliArg ("start", "First frame")] int start = 0,
            [CliArg ("end", "Last frame (-1 is the end of the clip)")] int end = -1)
        {
            return Run (new MotionEditorRequest { command = "lint", start = start, end = end });
        }

        [CliCommand ("motion_bake", "Bake and save the Humanoid clip for the game", Tags = new[] { kTag })]
        public static JToken Bake ([CliArg ("output_name", "Output name (empty uses the clip name)")] string output_name = null)
        {
            return Run (new MotionEditorRequest { command = "bake", outputName = output_name });
        }

        [CliCommand ("motion_capture", "Render the pose at a frame from 4 views (front, right, top, quarter) to PNG files and return their paths. Needs an Editor with graphics", Tags = new[] { kTag })]
        public static JToken Capture (
            [CliArg ("frame", "Frame to render")] int frame = 0,
            [CliArg ("folder", "Output folder (empty uses Temp/MotionEditorCaptures in the project)")] string folder = null,
            [CliArg ("size", "Image size in pixels")] int size = 512)
        {
            return Run (new MotionEditorRequest { command = "capture", frame = frame, folder = folder, size = size });
        }

        [CliCommand ("motion_import_override", "Import a Humanoid clip made elsewhere (e.g. a motion model) as an Override layered on the open clip, without changing the open clip", Tags = new[] { kTag })]
        public static JToken ImportOverride (
            [CliArg ("source", "Humanoid clip path to import", Required = true)] string source,
            [CliArg ("override_clip", "Override clip path to write (Assets/....anim)", Required = true)] string override_clip,
            [CliArg ("create", "Create the override clip if it does not exist")] bool create = true,
            [CliArg ("mode", "Additive or Override")] string mode = "Additive",
            [CliArg ("lock_feet", "Keep feet on the floor where they are low and still")] bool lock_feet = false)
        {
            return Run (new MotionEditorRequest { command = "importOverride", source = source, overrideClip = override_clip, create = create, mode = mode, lockFeet = lock_feet });
        }

        [CliCommand ("motion_write_hand", "Key a hand shape at a frame: apply a preset (PoseBank Humanoid pose such as Hand_Fist) to the fingers, then individual finger muscles. Write the body poses first (they key the fingers too). Returns the finger muscles read back", Tags = new[] { kTag })]
        public static JToken WriteHand (
            [CliArg ("frame", "Frame to key")] int frame = 0,
            [CliArg ("preset", "Preset name (motion_read_hand lists them) or pose asset path. Empty applies only the muscles")] string preset = null,
            [CliArg ("hand", "Left, Right or Both")] string hand = "Both",
            [CliArg ("muscles", "Finger muscles JSON: [{\"name\":\"Index 1 Stretched\",\"value\":0.5}]. Names without Left/Right apply to the chosen hand")] string muscles = null)
        {
            JObject request = new JObject { ["command"] = "writeHand", ["frame"] = frame, ["preset"] = preset ?? "", ["hand"] = hand ?? "Both" };
            if (!string.IsNullOrEmpty (muscles)) request["muscles"] = JArray.Parse (muscles);
            return Run (request.ToString ());
        }

        [CliCommand ("motion_read_hand", "Read the finger muscles of both hands at a frame (-1 curled .. 1 stretched; Spread opens the fingers) and the available hand presets", Tags = new[] { kTag })]
        public static JToken ReadHand ([CliArg ("frame", "Frame to read")] int frame = 0)
        {
            return Run (new MotionEditorRequest { command = "readHand", frame = frame });
        }

        [CliCommand ("motion_save", "Save the edited clips", Tags = new[] { kTag })]
        public static JToken Save ()
        {
            return Run (new MotionEditorRequest { command = "save" });
        }

        [CliCommand ("motion_close", "Close the open character", Tags = new[] { kTag })]
        public static JToken Close ()
        {
            return Run (new MotionEditorRequest { command = "close" });
        }

        [CliCommand ("motion_request", "Run a raw motion editor request JSON ({\"command\":...}) or a batch ({\"requests\":[...]}, closed at the end unless \"keepOpen\":true)", Tags = new[] { kTag })]
        public static JToken Request (
            [CliArg ("json", "Request JSON")] string json = null,
            [CliArg ("file", "Path of a file with the request JSON (absolute, or relative to the project folder), instead of json")] string file = null)
        {
            if (!string.IsNullOrEmpty (json) && !string.IsNullOrEmpty (file)) throw new System.ArgumentException ("Pass either json or file, not both");
            if (!string.IsNullOrEmpty (file)) {
                string error;
                json = MotionEditorCommands.ReadJsonFile (file, out error);
                if (error != null) throw new System.InvalidOperationException (error);
            }
            if (json != null && json.Contains ("\"requests\"")) return JToken.Parse (MotionEditorCommands.RunBatch (json));
            return Run (json);
        }

        static System.Collections.Generic.List<string> SplitNames (string text)
        {
            System.Collections.Generic.List<string> names = new System.Collections.Generic.List<string> ();
            if (string.IsNullOrEmpty (text)) return names;
            foreach (string name in text.Split (',')) {
                if (name.Trim ().Length > 0) names.Add (name.Trim ());
            }
            return names;
        }

        static System.Collections.Generic.List<int> SplitInts (string text)
        {
            System.Collections.Generic.List<int> values = new System.Collections.Generic.List<int> ();
            foreach (string item in SplitNames (text)) {
                int value;
                if (!int.TryParse (item, out value)) throw new System.ArgumentException ("Not a frame: " + item);
                values.Add (value);
            }
            return values;
        }

        static JToken Run (MotionEditorRequest request)
        {
            return Unwrap (MotionEditorCommands.Run (request));
        }

        static JToken Run (string json)
        {
            return Unwrap (MotionEditorCommands.Run (json));
        }

        static JToken Unwrap (string response)
        {
            JObject parsed = JObject.Parse (response);
            if (!parsed.Value<bool> ("success")) throw new System.InvalidOperationException (parsed.Value<string> ("error"));
            return parsed["result"];
        }
    }

}
