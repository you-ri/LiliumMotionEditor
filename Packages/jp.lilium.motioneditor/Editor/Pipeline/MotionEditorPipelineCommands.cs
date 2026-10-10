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
        public static JToken ReadPose ([CliArg ("frame", "Frame to read")] int frame = 0)
        {
            return Run (new MotionEditorRequest { command = "readPose", frame = frame });
        }

        [CliCommand ("motion_write_pose", "Write a pose (the result of motion_read_pose, edited) as keys at pose.frame. Only the listed points are changed. Returns the pose read back", Tags = new[] { kTag })]
        public static JToken WritePose ([CliArg ("pose", "Pose JSON: {\"frame\":0,\"points\":[{\"name\":\"LeftHand\",\"state\":\"Pin\",\"position\":{\"x\":0,\"y\":1,\"z\":0.3}}]}", Required = true)] string pose)
        {
            return Run ("{\"command\":\"writePose\",\"pose\":" + pose + "}");
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

        [CliCommand ("motion_request", "Run a raw motion editor request JSON ({\"command\":...}) or a batch ({\"requests\":[...]})", Tags = new[] { kTag })]
        public static JToken Request ([CliArg ("json", "Request JSON", Required = true)] string json)
        {
            if (json != null && json.Contains ("\"requests\"")) return JToken.Parse (MotionEditorCommands.RunBatch (json));
            return Run (json);
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
