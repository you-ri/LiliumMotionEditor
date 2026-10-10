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
- **Only characters with a full-body IK rig definition** can be written (a two-bone IK definition returns a reason). The default rig definition is full-body IK
- Always pass `--project-path`, take results with `--json`, and read `data.result` (failures: `success:false` and `errors`)

```bash
P="C:/Work/Lilium/LiliumMotionEditor"
unity cmd motion_describe --project-path "$P" --json
```

- Do not use `eval` for the work (it times out after 5 s). `motion_*` commands have no time limit
- Temporary files (pose JSON, scripts, images) go to the scratchpad. The only assets created are the editing clip and the baked clip
- Write helper scripts with the Write tool, not shell heredocs (quotes inside heredocs break the shell call).
  On Windows, Python text files default to CRLF: open files with `newline="\n"` when a shell loop reads them (otherwise arguments end in `\r`)

## Workflow

1. **Open** — the character (prefab or FBX) and the editing clip (`*.rig.anim`). `--create true` creates a missing clip (60 fps)
   ```bash
   unity cmd motion_open --project-path "$P" --prefab "Assets/.../Character.fbx" --clip "Assets/.../Motions/Walk.rig.anim" --create true --json
   ```
   Returns: key frames, last frame, cycle flag, last writable frame (`writableUntil`, -1 = unlimited), `humanScale`, the points (name and whether it has a rotation), and why writing is impossible now (`writeProblem`, empty = writable).
   Keys already in the clip stay. To start over, use a new clip path (or delete the asset first); otherwise old keys at frames you do not rewrite remain
2. **Read the reference** — read frame 0 (the T-pose if the clip is empty) and keep the point positions. Build every pose by offsetting these values
   ```bash
   unity cmd motion_read_pose --project-path "$P" --frame 0 --json > "$S/rest.json"
   ```
3. **Write the body keys** — one pose JSON per key frame (see "Pose JSON"). The pose read back at that frame is returned
   ```bash
   unity cmd motion_write_pose --project-path "$P" --pose "$(cat "$S/key_10.json")" --json
   ```
   With many keys, generate all JSON files with a Python script, then write them in a loop (never assemble them by hand)
4. **Write the hand shapes** — after all body keys, key a hand shape at every key frame (see "Hands").
   **A body key (`motion_write_pose`) keys the fingers too, at their current value**, so hands written first get frozen by later body keys
5. **Lint** — `motion_lint`. Fix and go back to 3 (see "Fixing lint issues")
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
- To aim a hand or foot, build its rotation from direction vectors (e.g. fingers and palm normal; toes and sole normal) and convert to Euler in a helper, instead of guessing angles.
  T-pose: left-hand fingers point -x, right-hand fingers +x, palms face -y; toes point +z, soles face -y
- **Only the listed points change**. Unlisted points keep their state at that frame. A wrong name or state writes nothing and returns a reason.
  Listing every point you manage (with `Free` for unused ones) on every key avoids surprises from states carried over
- To reuse a pose on a character of a different size, scale positions by the ratio of `humanScale` (same as `PoseDocument.Rescale`)
- Points of the default rig definition:
  - Body: `Hips` `HipsDirection` `Spine` `Chest` `UpperChest` `UpperChestDirection` `Neck` `Head` `HeadDirection` `HeadTop`
  - Arms: `Left/RightShoulder` `Left/RightUpperArm` `Left/RightElbowPole` `Left/RightLowerArm` `Left/RightHand` `Left/RightPalmIndex` `Left/RightPalmLittle`
  - Legs: `Left/RightUpperLeg` `Left/RightKneePole` `Left/RightLowerLeg` `Left/RightFoot`
  - Soles: `Left/RightHeel` `Left/RightSoleBall` `Left/RightLittleToeSide` `Left/RightToeTip`

  The list depends on the rig definition; check `points` from `motion_describe` before use.
  The direction points (`HipsDirection`, `UpperChestDirection`, `HeadDirection`) do not pull by default: turn the hips and head with their rotation instead

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
- **A planted foot needs more than the ankle**: lock `Heel`, `ToeTip` and `LittleToeSide` (and `SoleBall` when the foot pivots on the ball).
  `LittleToeSide` does not pull, so it does not stop the foot from rolling onto its side: also lock `Foot` with its rotation (yaw of the foot, pitch when the heel is up).
  With only the ankle point, the foot turns around the ankle and the toes sink into the floor
- **Keep the knee over the toes** on a planted leg: pin `KneePole` above and in front of the ankle, in the direction the foot points. Otherwise the thigh can twist out of range when the hips turn
- **Turn an elbow with `ElbowPole`** (rig definition version 8+; behind the elbow in the T-pose): pin it where the elbow should point and the arm turns about the shoulder–hand axis without moving the hand.
  It is an alternative to pinning `LowerArm` (computing an elbow position that the bones can reach); use one or the other, as with the knee below
- **Never pin `KneePole` while `LowerLeg` (the knee) is pinned** (same for `ElbowPole` and `LowerArm`). With a knee point the pole stops being a pole and aims the thigh instead; it fights the knee pin and drags the feet through the floor.
  When the knee is placed (kneeling), leave `KneePole` Free. Lint then reports `FloorPenetration` for the Free pole under the floor; that is the control point only, not the mesh
- **`Locked` also works off the ground**. For spans where a point should travel on a straight line (just before landing, just after lift-off), lock it on both end keys (straight line between keys).
  With Pin, keys interpolate bone angles, the heel path curves as the body moves, and the foot slides at contact
- **Lift a foot straight up and put it straight down**: before lift-off and before landing, add a key with the sole points `Locked` a few cm above the floor, right above the contact spot.
  A Pin foot pointed down puts the toes behind the ankle, and the toes drag along the floor between the keys
- **Locked points move on straight lines**. Build arcs (a swinging foot) with more keys
- **Place pinned elbows and knees where the bones can reach**: compute them from the shoulder/hip and the wrist/ankle with the bone lengths (two-bone geometry) and a bend direction.
  An unreachable elbow pin drags the clavicle far out of range (and breaks the shoulder mesh)
- **Pin `UpperArm` near its T-pose spot relative to the chest** when the hands are pinned (guards, arm swings). Leaving it free lets the IK shrug the shoulders.
  When the chest twists or bends relative to the hips, orient that spot about 60% of the way from the hips rotation to the chest rotation (the spine shares the twist);
  with the full chest rotation the clavicle gets pushed forward/back out of range. Raise it a few cm to shrug (anger, sobbing), lower it to droop
- **Place hands that touch the head in the head frame** (covering the face, hands on the head): position, fingers and palm relative to the `Head` point and its rotation, then convert to the chest frame.
  Placed in the chest frame they float 10–20 cm off the face once the head bends
- **Do not lower an arm straight down**. A Humanoid arm only goes about 60° down; pulling the hand straight down rotates the shoulder far out of range.
  Put the hand about 60° down and pin `UpperArm` at the side of the body
- **Keep the wrist straight**: orient the hand with the fingers along the forearm (wrist-from-elbow direction)
- **Keep the palm direction continuous between keys** (e.g. inward on every key of a swing, or turn it gradually). Switching a palm from inward to forward between two keys is a 90–170° hand rotation
- **Keep the rotation between adjacent keys at 90° or less**. Rotations interpolate per component: large steps distort the speed and above 180° they turn the wrong way. Add keys in between
- **No keys after the cycle key** (`writableUntil`). For a loop, make the last key the same pose as the first
- The floor is y = 0. Sole points have y >= 0

## Example: walk (2 steps in 60 frames, 13 keys)

Placement that gave zero lint issues on the demo character (`humanScale` about 1). Stride 0.55 m, hips height 0.88–0.93 m. One step (left foot to right foot) is 30 frames.

| Frame | Left foot (3 points) | Right foot (3 points) | Hips / arms |
|---|---|---|---|
| 0 | Locked. Heel strike (toes up) | Locked. Toes only (heel up) | hips y 0.90 |
| 7 | Locked. Whole sole down | Locked. Heel up (push-off) | hips y 0.88 (lowest) |
| 10 | Locked | Pin. Foot leaves the floor | |
| 15 | Locked | Pin. Passing. **Bend the knee and drop the toes** (do not swing it flat) | hips y 0.92 |
| 22 | Locked | Pin. Swing forward | hips y 0.93 (highest) |
| 26 | Locked | **Locked**. Just before contact (heel 4 cm above the floor) | |
| 30 | Locked. Heel starts to rise | Locked. Heel strike | frame 0 mirrored |
| 37–60 | Frames 7–30 mirrored, z advanced by one step (0.55 m) | | |

- Arms on every key: `UpperArm` pinned at the side of the body (x ±0.2, y 1.4), hands pinned about 60° down (x ±0.37, y 0.95–1.0), swinging opposite to the legs
- Hips pinned on every key. z advances about 0.018 m per frame (the root does not move; the hips and foot points move)

## Example: roundhouse kick for a fighting game (50 frames, 16 keys)

Right-leg roundhouse from an orthodox stance (left foot forward, body turned 35° to the right), returning to the same stance so it chains into the idle.
z is toward the opponent. Startup 15 frames, active 15–18, recovery to 50.

| Frame | Support foot (left) | Kicking foot (right) | Hips (yaw, roll) / arms |
|---|---|---|---|
| 0 | Locked flat, yaw 20 | Locked flat, yaw 45 | (35, 0), guard, both fists |
| 4–5 | Weight forward, heel 4 cm up, starts turning | Heel up 9 → 11 cm (on the toes) | (22 → 15) |
| 7 | Yaw -20 | **Locked 10 cm above the floor** (lift straight up) | (5, 8) |
| 9–11 | Yaw -35 → -50 | Pin. Knee up front, then chamber: shin folded flat to the outside | (-10 → -25, 14 → 20) |
| 13 | Yaw -70 | Pin. Extending; the instep leads the swing | (-50, 40), right arm half way back |
| 15 | Yaw -82 | Pin. Leg straight, foot at about chest height, instep facing the swing (-x), knee facing -x | (-75, 45), torso leans away, right arm swung back, right hand relaxed |
| 18 | Yaw -88 | Pin. Follow-through to the left | (-88, 42) |
| 21–24 | Yaw -80 → -65 | Pin. Re-chamber: keep the knee up, fold the shin | (-70 → -45) |
| 29 | Yaw -30 | **Locked 12 cm above the landing spot** (put it straight down) | (5, 8), guard |
| 33 | Yaw 12 | Locked 3 cm above, on the toes | (20, 3) |
| 36 | Yaw 15, heel down | Locked, heel 5 cm up (absorbing) | (30, 0), hips lowest |
| 42–50 | Back to stance (50 = frame 0) | | |

- The support foot pivots on the ball (heel 5 cm up, `SoleBall` locked). The toe tip and little-toe side slide 1–2 cm per frame while pivoting: that `FootSlide` is the pivot, not a defect
- Put the hip roll (kicking hip up) into the pelvis rotation; it reduces the leg abduction needed. Without it, `Upper Leg In-Out` goes out of range
- Torso yaw stays 12–20° behind the hips; more twists the upper chest out of range

## Example: exaggerated emotions (anger 100 F, joy 100 F, sadness 120 F)

Keys are built from parameter sets (hips, hips/chest/head rotation, arms, feet, shrug); keys in between are blended from two neighbors and then adjusted (arm arcs).

- Anger: chest out with fists (shrug +3 cm) → lift a foot 32 cm and stomp (hips down to 0.79, chest pitched 28°, fists slammed by the hips) →
  fists trembling in front of the chest (±3 cm every 3 frames) → `Hand_Point` jabbing at the target → fist overhead, then slammed forward → second stomp → arms crossed, chin up, head turned 40°
- Joy: deep crouch with arms back → arms swing through an arc (2 extra keys) to front-up on tiptoes → jump (hips +0.36 m, legs tucked, arms in a V, `Hand_Open` with spread fingers) →
  landing crouch → two fist pumps → arms up, swaying left/right with the opposite heel up
- Sadness: head hanging 45°, shoulders dropped (UpperArm -2 cm), arms limp → sigh (shoulders up, then lower) → knees buckle forward →
  kneel on tucked toes (knee one shin length in front of the ankle, hips above the knees) → hands cover the face (head frame) → sobbing (chest ±3°, shoulders ±2.5 cm every 4 frames) → fold forward with the hips down on the heels
- Rapid trembling/sobbing keys 3–4 frames apart show up as small `Pop` issues just over the limit; they are the intended shake
- A deep slump bent only at the chest exceeds `UpperChest Front-Back`; tilt the pelvis too (hips pitch 5–55°) and keep the chest pitch at 20–60°

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

`motion_open` cannot take Overrides. To open with layers, pass the request JSON to `motion_request`.

```bash
unity cmd motion_request --project-path "$P" --json '{"command":"open","prefab":"Assets/.../Character.fbx","clip":"Assets/.../Walk.rig.anim","overrides":[{"clip":"Assets/.../Walk.rig.override.anim","mode":"Additive","mask":"Packages/jp.lilium.motioneditor/Editor/Masks/Upper Body.asset"}],"overrideWriteIndex":0}'
```

- `overrideWriteIndex` is the Override to write to (0 = lowest; -1 = the base clip). Curves outside the mask are not layered and keys there are not written
- Bundled masks: `Body` / `Upper Body` / `Hands` / `Face` (`Packages/jp.lilium.motioneditor/Editor/Masks`)
- To send several requests: `{"requests":[...]}` (run in order, closed at the end)

## Do not

- Write bone rotations (quaternions) directly. Build poses from point positions and hand shapes from muscles only
- Use `eval` to poke the window's internals instead of using the window (handles, panels)
- Edit the baked (game) clip by hand. Fix the editing clip and bake again
- Commit or copy to extrival without the user's instruction (CLAUDE.md)
