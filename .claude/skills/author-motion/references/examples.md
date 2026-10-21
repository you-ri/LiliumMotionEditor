# author-motion examples

Placements that gave zero lint issues on the demo character. See SKILL.md for the pose JSON and the rules.

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
