---
name: generate-motion
description: Generate motion between the keys of a Lilium Motion Editor clip with NVIDIA Kimodo (a motion diffusion model run locally in Docker) from a text prompt, and import it as an Override that keeps the keyed poses. Optional; needs Kimodo set up as in the package's Kimodo sample. Use for requests such as "fill in between the keys with Kimodo", "generate the motion with Kimodo", "make a walk with Kimodo from these keys" (in Japanese: 「Kimodo でキーの間を作って」「Kimodo で動きを作って」「キーから Kimodo で補間して」).
---

# generate-motion

Hand the keys of the open clip and a text prompt to Kimodo, let it make the motion, and layer the result on the clip as an
Override. At the clip's key frames the keyed pose is kept; around them the motion blends into Kimodo's.
Kimodo is optional: if it is not set up, say so and stop (do not try to install it unasked; downloads need the user's consent).

Background: `Docs/Specs/motion-editor.md`, S71–S76 ("外部のモーション生成モデルを使う"). Setup and licenses for users:
`Packages/jp.lilium.motioneditor/Samples~/Kimodo/README.md`. Writing keys: skill `author-motion`.

## Prerequisites (check, and stop with the reason if one is missing)

```bash
P="C:/Work/Lilium/LiliumMotionEditor"
KIMODO="C:/Work/kimodo"                      # the kimodo clone (ask the user if it is elsewhere)
docker info --format '{{.ServerVersion}}'    # Docker is running
docker images kimodo:1.0 --format '{{.Size}}' # the image is built
ls ~/.cache/huggingface/token                # the user ran hf auth login (never ask for the token itself)
```

- The project is open in the Editor with the Pipeline server reachable (skill `unity-pipeline`)
- A SOMA External Skeleton asset exists (`Assets > Create > Lilium Motion Editor > Kimodo SOMA Skeleton From BVH...` from the Kimodo sample).
  If not, make the T-pose BVH as in the sample README and create it with `KimodoSkeleton.Create (bvhPath, assetPath)`
- The user has accepted the licenses (Kimodo model: NVIDIA Open Model License; text encoder: Meta Llama 3). Use only the SOMA models, never SMPL-X
- If Docker Desktop fails with "The file cannot be accessed by the system" on a socket file, stop it and delete the stale sockets from WSL
  (`wsl -d docker-desktop -e sh -c 'rm -f /mnt/host/c/Users/<you>/AppData/Local/Docker/run/*'`), then start it again

## Steps

Work files go to a work folder `W` (the scratchpad). Copy `Samples~/Kimodo/Python/kimodo_constraints.py` there.

```bash
W="<scratchpad>/kimodo"; mkdir -p "$W"
cp "$P/Packages/jp.lilium.motioneditor/Samples~/Kimodo/Python/kimodo_constraints.py" "$W/"
SKEL="Assets/.../Kimodo SOMA Skeleton.asset"
```

1. **Open the clip fresh.** Close first: reopening without `motion_close` keeps Overrides imported earlier in the session layered

   ```bash
   unity cmd motion_close --project-path "$P" --json
   unity cmd motion_open --project-path "$P" --prefab "Assets/.../Character.fbx" --clip "Assets/.../Walk.rig.anim" --json
   ```

   The keys come from the clip. Make them first with `author-motion` (a few keys at the poses that matter, e.g. contacts and passing poses of a step).
   The clip must be 10 seconds or shorter.

2. **Export the keys** to Kimodo's skeleton

   ```bash
   unity cmd motion_export_keys --project-path "$P" --skeleton "$SKEL" --path "$W/keys.bvh" --json > "$W/export.json"
   ```

3. **Make the constraints and generate** in one container run (loading the models takes most of the time).
   The prompt is required and in English; describe the whole motion ("a person walks forward at a normal pace").
   `kimodo_constraints.py` prints `{"duration": ...}`; pass it to `--duration` (run the script first if you need the value, it is fast)

   ```bash
   MSYS_NO_PATHCONV=1 docker run --rm --gpus all --shm-size 16g --ipc host --entrypoint /bin/bash \
     -v "$KIMODO":/workspace -v "$W":/work -v "C:/Users/<you>/.cache/huggingface":/workspace/.cache/huggingface \
     -e HF_HOME=/workspace/.cache/huggingface -e TEXT_ENCODER_MODE=local -e TEXT_ENCODER_DEVICE=cpu \
     kimodo:1.0 -lc "python /work/kimodo_constraints.py /work/keys.bvh /work/export.json /work/constraints.json && \
       kimodo_gen 'a person walks forward at a normal pace' --duration <duration> --constraints /work/constraints.json --output /work/gen --bvh --bvh_standard_tpose"
   ```

   Run it in the background (about 4 minutes). `--num_samples 3` makes three variants in one run (`gen_00.bvh`...).
   `--mode ends` (hands and feet only) or `--mode root` (hips path only) gives Kimodo more freedom than the default `fullbody`

4. **Import with the keys kept**

   ```bash
   unity cmd motion_import_override --project-path "$P" --source "$W/gen.bvh" --skeleton "$SKEL" \
     --override_clip "Assets/.../Walk.kimodo.override.anim" --keep_keys true --json
   ```

   - `align_root` (default on) moves and turns the whole motion once so its first frame matches the clip (Kimodo starts at the origin facing +Z)
   - Fingers are not imported (the keyed hand shapes stay). Do not pass `--lock_feet` (Kimodo already fixes foot sliding; locking makes feet pop)
   - Read `keyDeviations`: per key frame, how far Kimodo's motion was from the key before blending (`distance`, `bone`, `hips`, meters).
     Large values (over ~10 cm) at a key mean Kimodo did not follow it there; the blend around that key will be visible. Add keys or change the prompt

5. **Check** with `motion_lint` and `motion_capture` (skill `author-motion`). Compare with the base: lint the clip before importing too.
   The imported Override is registered only in this CLI session; to see it in the window, tell the user to add it in the Layers overlay (+Override)
   and move the write target (✏) to it (Overrides above the write target are not applied while editing)

Users who prefer the window can do all of this without the CLI: the Kimodo sample adds a **Kimodo** panel to the Motion Editor window
(`Window > Lilium Motion Editor > Kimodo` shows it; the log is in `Window > Lilium Motion Editor > Kimodo Console`; see the sample README).
It works on the clip open in that window, and shows the setup steps until Kimodo is installed.

## Use the motion as the interpolation between keys (instead of an Override)

The window's **Use the Result as: Interpolation between keys** saves the motion as `<clip>.between.anim` next to the clip and makes the chosen segments
(spans between two neighboring keys) take their in-between frames from it; the other segments keep their curve / linear / constant interpolation.
From the CLI, after generating `gen.bvh` as above, write it with `PoseSession.ImportBetween` (no CLI command yet), then switch segments with
`motion_write_motion` keys' `"interpolation": "external"`, and check `motion_read_motion` (`"stale": true` means the keys changed after the motion was made) and `motion_lint` (`Between` issues).

## Faster runs

Use `Samples~/Kimodo/Python/kimodo_generate.py` in place of `kimodo_gen` (same arguments) with `-v <project>/Temp/Kimodo/text-cache:/cache`:
it keeps each encoded prompt, so running again with the same prompt takes about 30 seconds instead of 4–6 minutes. A new prompt still loads the text model,
unless the text server runs (`python -m kimodo.scripts.run_text_encoder_server`, port 9550, about 15 GB of memory; generate with `TEXT_ENCODER_MODE=api`
and `TEXT_ENCODER_URL=http://host.docker.internal:9550/`).

## Notes

- Kimodo moves only the body. Held items (weapons) follow the hands through the character settings; for two-handed items, write the hands back together with full-body IK keys after importing
- At most 10 seconds, 30 fps internally (the import resamples to the clip's frame rate)
- Report to the user: the prompt, the mode, the time it took, the lint before and after, and the key deviations
