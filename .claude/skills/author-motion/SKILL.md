---
name: author-motion
description: Drive Lilium Motion Editor through the Unity CLI (unity cmd motion_*) to author character poses and motions (walks, waves, kicks...) and hand shapes (fist, open hand, per-finger values) as keys, check them with lint and rendered images, and bake them to Humanoid clips for the game. Use for requests such as "make a motion", "make a walk", "make a kick", "put the character in this pose", "make a fist", "key this", "import a motion made elsewhere" (in Japanese: 「モーションを作って」「歩くモーションを作って」「このポーズにして」「手をグーにして」「キーを打って」「外で作った動きを取り込んで」). Works without opening the Motion Editor window: poses are written as full-body IK point positions in JSON.
---

# author-motion

Author motions with the Motion Editor **without opening its window**. A pose is written as positions of full-body IK points
(body frame, meters); the editor solves full-body IK and keys every bone. Fingers are written as Humanoid muscle values
(presets plus per-finger values). Never write bone rotations (quaternions) directly.

Background and the reasons behind the rules: `Docs/Specs/motion-editor.md`, sections S35–S40 ("AI がポーズ・モーションを作れるようにする") and S42 (hands).

## Prerequisites

- The project is open in the Editor and the Pipeline server is reachable (`unity pipeline list` shows reachable = true).
  If not, ask the user to press Ctrl+R in the Editor. General CLI usage: skill `unity-pipeline`
- Characters whose rig definition has no full-body IK points cannot be written (the reason is returned). The default rig definition has them
- Always pass `--project-path`, take results with `--json`, and read `data.result` (failures: `success:false` and `errors`)

```bash
P="C:/Work/Lilium/LiliumMotionEditor"
unity cmd motion_describe --project-path "$P" --json
```

- Do not use `eval` for the work (it times out after 5 s). `motion_*` commands have no time limit
- Temporary files (pose JSON, scripts, images) go to the scratchpad. The only assets created are the editing clip and the baked clip
- Write helper scripts with the Write tool, not shell heredocs (quotes inside heredocs break the shell call).
  On Windows, Python text files default to CRLF: open files with `newline="\n"` when a shell loop reads them (otherwise arguments end in `\r`)
- Pass JSON as files (`motion_write_pose --pose_file`, `motion_request --file`), not as inline strings

## Workflow

1. **Open** — the character (prefab or FBX) and the editing clip (`*.rig.anim`). `--create true` creates a missing clip (60 fps)
   ```bash
   unity cmd motion_open --project-path "$P" --prefab "Assets/.../Character.fbx" --clip "Assets/.../Motions/Walk.rig.anim" --create true --json
   ```
   Returns: key frames, last frame, cycle flag, last writable frame (`writableUntil`, -1 = unlimited), `humanScale`, why writing is impossible now (`writeProblem`, empty = writable), and the points.
   Each point has: `name`, `hasRotation`, `bone` (the bone it sits on), `rest` (its T-pose position, body frame, meters — even when the clip already has keys),
   `link` (the point it hangs from, e.g. `LeftLowerArm` → `LeftUpperArm`) with `length` (T-pose distance to `link` = the bone length), `contact` (checked for floor and sliding) and `ground` (sole points).
   Keys already in the clip stay. To start over, write with `motion_write_motion --replace true` (step 4)
2. **Keep the reference** — save the `motion_open` response. Build every pose by offsetting the `rest` positions, and place elbows and knees with the `length`s (two-bone geometry)
   ```bash
   unity cmd motion_open ... --json > "$S/open.json"
   ```
3. **Try difficult poses** (optional) — `motion_write_pose --dry_run true` solves one pose (see "Pose JSON") and returns it without keying anything.
   Pass the pose as a file with `--pose_file` (an inline `--pose` breaks easily on shell quoting)
   ```bash
   unity cmd motion_write_pose --project-path "$P" --pose_file "$S/key_10.json" --dry_run true --json
   ```
   Responses of `motion_read_pose` / `motion_write_pose` are compact by default: positions in mm steps, rotations in 0.1° steps,
   and Free points carry only `name` and `state` (`--compact false` returns every field at full precision). Check:
   - `unreached`: pinned/locked points farther than 1 cm (× `humanScale`) from their target. Each point's `residual` is that distance in meters. A point is never moved for you: move the pin where the bones reach (elbows and knees: bone lengths; hands: arm length)
   - `issues`: floor penetration and out-of-range muscles at that frame (same `kind`s as `motion_lint`; foot sliding and pops need `motion_lint`)
4. **Write the whole motion at once** — generate one keys file with a Python script (never assemble it by hand) and write it with `motion_write_motion`.
   Each key has the body points (as in "Pose JSON") and the hand shapes for that frame (as in "Hands"):
   ```json
   {"keys": [
     {"frame": 0, "points": [...], "hands": [{"preset": "Hand_Relaxed"}]},
     {"frame": 13, "points": [...], "hands": [{"preset": "Hand_Fist", "hand": "Right"}, {"hand": "Left", "muscles": [{"name": "Index 1 Stretched", "value": 0.6}]}]}
   ]}
   ```
   ```bash
   unity cmd motion_write_motion --project-path "$P" --file "$S/keys.json" --replace true --json
   ```
   - Keys are written in frame order, body first, then hands, so key order in the file and the "body before hands" rule do not matter
   - Everything is checked first: a wrong point, state, preset, muscle or frame writes nothing and names the frame of the bad key
   - `--replace true` deletes every key of the clip first (start over). Without it, keys at other frames stay
   - The response has, per key, `maxResidual` and `unreached` (as above), then `lint` (the same report as `motion_lint`; `--lint false` skips it)
   - `motion_read_motion` returns the clip's keys in the same shape (compact poses and the 40 finger muscles per key): read, edit in Python, and write back with `--replace true`
   - `motion_undo` removes the last `motion_write_motion` / `motion_write_pose` / `motion_write_hand` as a whole (repeat to go further back, up to 32). It is refused if the clip was changed after that write (in the window, by Unity's undo...)
5. **Fix lint issues** — edit the keys file and write it again with `--replace true` (see "Fixing lint issues")
6. **Look at images** — capture the key frames and one or two frames between keys with `motion_capture`, open them with Read and look.
   **Images find problems even when lint reports nothing** (a swinging foot kept flat, arms spread into an A-pose, a broken shoulder...)
7. **Bake and save** — `motion_bake` (writes the Humanoid clip for the game next to the editing clip) → `motion_save`
8. **Close** — `motion_close`. Otherwise the hidden preview scene stays alive

If the same clip is open in the Motion Editor window, ask the user to reopen the clip there after writing through the CLI.

## Pose JSON

```json
{"frame": 10, "points": [
  {"name": "LeftHeel", "state": "Locked", "position": {"x": -0.11, "y": 0.0, "z": 0.21}},
  {"name": "Hips", "state": "Pin", "position": {"x": 0.0, "y": 0.89, "z": 0.14}, "hasRotation": true, "pinRotation": true, "rotation": {"x": 5, "y": 30, "z": 0}},
  {"name": "RightHand", "state": "Pin", "position": {"x": 0.35, "y": 0.97, "z": 0.17}},
  {"name": "Head", "state": "Pin", "position": {"x": 0.0, "y": 1.6, "z": 0.1}, "hasRotation": true, "pinRotation": true, "rotation": {"x": 10, "y": 0, "z": 0}}
]}
```

- **Position**: body frame (x right, y up, z forward), meters from the root of the editing body (between the feet). Independent of where the character is placed; the left hand has negative x
- **State**: `Free` (follows the body; position ignored) / `Pin` (pulled to this position) / `Locked` (held where placed until the next key)
- **Rotation**: only for points that have one (Hips, Head, LeftHand / RightHand, LeftFoot / RightFoot). Euler angles in degrees, as the difference from the T-pose,
  in Unity's `Quaternion.Euler` order (Z, then X, then Y). +y turns the point to its right, +x pitches it forward/down.
  **Set both `hasRotation: true` and `pinRotation: true`**; without `hasRotation` the rotation is silently dropped
- **Aim** (preferred over Euler angles): `"aim": {"restForward": ..., "restUp": ..., "forward": ..., "up": ...}` turns the point so that the direction that was
  `restForward` in the T-pose points along `forward`, and `restUp` along `up` (body frame; `restForward` defaults to +z, `restUp` to +y, `up` to `restUp`).
  Instead of `forward`, `"target": {...}` turns `restForward` toward a position (a head looking at something). `aim` wins over `rotation`; it needs no `hasRotation`/`pinRotation`.
  T-pose: left-hand fingers point -x, right-hand fingers +x, palms face -y; toes point +z, soles face -y.
  Example, right fingers forward and palm down: `"aim": {"restForward": {"x":1,"y":0,"z":0}, "restUp": {"x":0,"y":-1,"z":0}, "forward": {"x":0,"y":0,"z":1}, "up": {"x":0,"y":-1,"z":0}}`
- **Space**: `"space": "Head"` reads `position`, `aim` and `rotation` in that point's frame: origin at the point, axes turned by the point's rotation from the T-pose
  (in the T-pose the axes match the body frame). The reference is the same request's value when that point is placed in it, otherwise the current pose at that frame.
  `"spaceBlendFrom": "Hips", "spaceBlend": 0.6` turns the axes only 60% of the way from the hips rotation to the space point's rotation. A space point cannot have a space itself
- **Snap**: `"snap": true` on an elbow or knee (`LowerArm` / `LowerLeg`) moves its pin to the closest spot the bones reach, given its parent point (`UpperArm` / `UpperLeg`) and child point
  (`Hand` / `Foot`) from the same request or the current pose; the bend stays on the side of the written position. The response lists it under `snapped` (`distance`, `position`).
  Without `snap` a pin is never moved
- **Only the listed points change**. Unlisted points keep their state at that frame. A wrong name or state writes nothing and returns a reason.
  Listing every point you manage (with `Free` for unused ones) on every key avoids surprises from states carried over
- To reuse a pose on a character of a different size, scale positions by the ratio of `humanScale` (same as `PoseDocument.Rescale`)
- **Contacts** (a whole planted foot in one entry, next to `points`; also allowed in each key of `motion_write_motion`):
  ```json
  {"frame": 7, "points": [...], "contacts": [
    {"point": "LeftFoot", "mode": "Flat", "at": {"x": -0.1, "y": 0, "z": 0.25}, "yaw": 10},
    {"point": "RightFoot", "mode": "Ball", "at": {"x": 0.1, "y": 0, "z": -0.3}, "yaw": 0, "pitch": 35, "lift": 0}
  ]}
  ```
  It places the foot point and every contact point on its bones (heel, ball, toe tip, little-toe side) in their T-pose shape and sets the foot rotation, all `Locked` (`state` to change it).
  `at` is where the ankle would stand with the foot flat (x and z); `yaw` turns the foot; `mode` `Flat` / `Ball` (heel raised by `pitch` degrees around the toe joint) / `Heel` (toes raised by `pitch` around the heel);
  `lift` raises everything (lift-off and landing keys). Ball/Heel keep the pivot where it is when flat. Do not list those points in `points` too
- Points of the default rig definition:
  - Body: `Hips` `HipsDirection` `Stomach` `Chest` `ChestDirection` `Neck` `Head` `HeadDirection` `HeadTop`
    (`Chest` is the upper spine: one point at the middle of the torso (halfway from the hips to the head base); `Stomach` is the lower spine, halfway from the hips to `Chest`.
    Both exist on every character whatever its spine bone count; the spine bones between follow by stiffness)
  - Arms: `Left/RightUpperArm` `Left/RightLowerArm` `Left/RightHand` `Left/RightPalmIndex` `Left/RightPalmLittle`
  - Legs: `Left/RightUpperLeg` `Left/RightLowerLeg` `Left/RightFoot`
  - Soles: `Left/RightHeel` `Left/RightSoleBall` `Left/RightLittleToeSide` `Left/RightToeTip` (only on characters with toe bones; palm points need finger bones)

  The list depends on the rig definition; check `points` from `motion_describe` before use.
  The direction points (`HipsDirection`, `ChestDirection`, `HeadDirection`) do not pull by default: turn the hips and head with their rotation instead

## Hands

Fingers are Humanoid muscle values: one number per joint in -1..1 (`Stretched`: -1 fully curled, 1 fully stretched; `Spread`: opens the finger).
They do not depend on the character's bone axes and are never clamped when baking. Apply a preset (a PoseBank hand shape), then adjust individual fingers.

```bash
# Available presets and the current finger values (40 values, both hands)
unity cmd motion_read_hand --project-path "$P" --frame 0 --json
# Preset on both hands
unity cmd motion_write_hand --project-path "$P" --frame 13 --preset Hand_Fist --json
# Preset on the right hand only, then stretch the index finger (names without Left/Right go to --hand)
unity cmd motion_write_hand --project-path "$P" --frame 20 --preset Hand_Fist --hand Right --muscles '[{"name":"Index 1 Stretched","value":0.6},{"name":"Index 2 Stretched","value":0.8}]' --json
```

- Presets (Humanoid poses in the PoseBank folders; the demo has them in `Demo/Poses`): `Hand_Fist`, `Hand_Open`, `Hand_Relaxed`,
  `Hand_Flat`, `Hand_Grip`, `Hand_Peace`, `Hand_Point`, `Hand_ThumbsUp`. Check the names in `presets` from `motion_read_hand`. An asset path also works
- `--hand` is `Left` / `Right` / `Both` (default). A one-handed preset is mirrored to the other hand
- `--muscles` names: `Left Index 1 Stretched` (as `motion_read_hand` prints), `LeftHand.Index.1 Stretched`, or without the side, `Index 1 Stretched`.
  Fingers: `Thumb` `Index` `Middle` `Ring` `Little`; joints: `1 Stretched` `2 Stretched` `3 Stretched` (from the base) and `Spread`. Non-finger muscles return a reason
- Only the finger bones moved by the preset/values are keyed. Other bones, the IK points and other frames do not change. The finger values after writing are returned
- Hand shapes interpolate between keys. To clench or open at a given moment, also key the previous shape on the key just before (otherwise it changes gradually from the previous key)
- Rewriting a body key keeps the fingers at that frame as they were (no need to rewrite the hand)

## Rules

These follow from how the editor interpolates. Breaking them trips lint or breaks the pose between keys.

- **One key keys every bone** (fingers included). You cannot key just part of the body (except fingers via `motion_write_hand`). The other parts at that frame freeze at the interpolated value
- **Point states carry over between keys, Pin positions do not**. To keep a contact over a span, make the point `Locked` on the keys at both ends
- **Plant feet with `contacts`**, not with single points. With only the ankle point locked, the foot turns around the ankle and the toes sink into the floor
- **Keep the knee over the toes** on a planted leg: pin `LowerLeg` (the knee) above and in front of the ankle, in the direction the foot points. Otherwise the thigh can twist out of range when the hips turn
- **Turn an elbow by pinning `LowerArm`** with `"snap": true` (the default rig definition has no knee or elbow poles). Write roughly where the elbow should point; snap puts it where the bones reach
- **`Locked` also works off the ground**. For spans where a point should travel on a straight line (just before landing, just after lift-off), lock it on both end keys (straight line between keys).
  With Pin, keys interpolate bone angles, the heel path curves as the body moves, and the foot slides at contact
- **Lift a foot straight up and put it straight down**: before lift-off and before landing, add a key with the foot in `contacts` with a `lift` of a few cm, at the same `at` as the contact.
  A Pin foot pointed down puts the toes behind the ankle, and the toes drag along the floor between the keys
- **Locked points move on straight lines**. Build arcs (a swinging foot) with more keys
- **Place pinned elbows and knees where the bones can reach** (`snap`). An unreachable elbow pin drags the clavicle far out of range (and breaks the shoulder mesh)
- **Pin `UpperArm` near its T-pose spot relative to the chest** when the hands are pinned (guards, arm swings). Leaving it free lets the IK shrug the shoulders.
  Write it with `"space": "Chest", "spaceBlendFrom": "Hips", "spaceBlend": 0.6` and its T-pose offset from the chest (`rest` of `UpperArm` minus `rest` of `Chest` from `motion_open`):
  the spine shares the twist, and with the full chest rotation the clavicle gets pushed forward/back out of range. Raise it a few cm to shrug (anger, sobbing), lower it to droop
- **Place hands that touch the head with `"space": "Head"`** (covering the face, hands on the head), with `aim` in the same space for fingers and palm.
  Placed in the body frame they float 10–20 cm off the face once the head bends
- **Do not lower an arm straight down**. A Humanoid arm only goes about 60° down; pulling the hand straight down rotates the shoulder far out of range.
  Put the hand about 60° down and pin `UpperArm` at the side of the body
- **Keep the wrist straight**: orient the hand with the fingers along the forearm (wrist-from-elbow direction)
- **Keep the palm direction continuous between keys** (e.g. inward on every key of a swing, or turn it gradually). Switching a palm from inward to forward between two keys is a 90–170° hand rotation
- **Keep the rotation between adjacent keys at 90° or less**. Rotations interpolate per component: large steps distort the speed and above 180° they turn the wrong way. Add keys in between
- **No keys after the cycle key** (`writableUntil`). For a loop, make the last key the same pose as the first
- The floor is y = 0. Sole points have y >= 0

## Examples

Worked placements (walk, roundhouse kick, exaggerated emotions) are in `references/examples.md`. Read it before authoring a similar motion.
They were written before `contacts`, `aim`, `space` and `snap` existed: the numbers still apply, but write planted feet with `contacts` and elbows with `snap`.

## Fixing lint issues

`motion_lint` merges consecutive frames with the same problem on the same target into one issue (`kind`, `target`, `frame`–`endFrame`, `value`, `limit`).

| kind | Meaning | Fix |
|---|---|---|
| `FootSlide` | A grounded foot point (y <= 3 cm × scale) moves more than 5 mm (× scale) horizontally in one frame | Lock the foot points on the keys at both ends of the contact. Lock the foot on the key just before landing / after lift-off too, a few cm above the floor |
| `FloorPenetration` | A point is more than 5 mm below the floor (y = 0) | Lock the foot points and the foot rotation. Keep y >= 0. If it sinks between keys, add a key |
| `MuscleRange` | A joint turns far enough to be clamped when baked for the game (muscle > 1.05) | Shoulder: elbow pin unreachable, `UpperArm` not pinned. Arm: lowered too far. Wrist: hand not along the forearm. Leg: smaller stride/abduction, add hip roll, knee pole over the toes |
| `Pop` | One frame jumps | Check the keys around it (state switches, forgotten points). Add a key in between |
| `LargeKeyRotation` | Rotation between adjacent keys exceeds 90° | Add a key in between |

`maxAsymmetry` (left/right difference) is not a problem by itself. Only look at it when the motion should be symmetric (like a walk) and it is large.

## Images

```bash
unity cmd motion_capture --project-path "$P" --frame 15 --folder "$S/cap" --size 512 --json
```

- 4 images: `<clip name>_<frame>_front.png` / `_right.png` / `_top.png` (orthographic) and `_quarter.png` (perspective). Without `--folder`, the project's `Temp/MotionEditorCaptures`. Open the returned `paths` with Read
- Front: the character faces the camera. Right: the camera is on the character's right (+z points right in the image). Top: +z points up in the image
- The orthographic views have no perspective, so you can check whether the feet sit on the floor line and whether left and right match. The floor and grid are visible
- Combining the 4 images into one 2x2 image (Pillow) halves the number of Reads
- A Unity without graphics (`-nographics`) cannot capture (a reason is returned)

## Importing a motion made elsewhere

Import a Humanoid clip made by a motion generation model or a mocap service as an Override layered on the open clip. The open clip does not change.

```bash
unity cmd motion_import_override --project-path "$P" --source "Assets/.../Generated.anim" --override_clip "Assets/.../Walk.rig.override.anim" --lock_feet true --json
```

- Stored as the difference from the base (`--mode Additive`) or as replacement values (`--mode Override`); keys that do not change are removed
- `--lock_feet true` re-solves the leg only where a foot is low and still but slides, and holds it on the floor
- The imported Override is layered and becomes the write target. Continue with lint, images and bake
- For outputs of trained models, confirm the license of the training data with the user first (many are non-commercial)

## Opening with Overrides and masks

`motion_open` cannot take Overrides. To open with layers, write the request JSON to a file and pass it to `motion_request --file`.

```json
{"command":"open","prefab":"Assets/.../Character.fbx","clip":"Assets/.../Walk.rig.anim","overrides":[{"clip":"Assets/.../Walk.rig.override.anim","mode":"Additive","mask":"Packages/jp.lilium.motioneditor/Editor/Masks/Upper Body.asset"}],"overrideWriteIndex":0}
```

```bash
unity cmd motion_request --project-path "$P" --file "$S/open_layers.json"
```

- `overrideWriteIndex` is the Override to write to (0 = lowest; -1 = the base clip). Curves outside the mask are not layered and keys there are not written
- Bundled masks: `Body` / `Upper Body` / `Hands` / `Face` (`Packages/jp.lilium.motioneditor/Editor/Masks`)
- To send several requests: `{"requests":[...]}` (run in order, closed at the end)

## Do not

- Write bone rotations (quaternions) directly. Build poses from point positions and hand shapes from muscles only
- Use `eval` to poke the window's internals instead of using the window (handles, panels)
- Edit the baked (game) clip by hand. Fix the editing clip and bake again
- Commit or copy to extrival without the user's instruction (CLAUDE.md)
