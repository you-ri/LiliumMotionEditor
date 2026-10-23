# Kimodo (optional)

Generate motion with NVIDIA's [Kimodo](https://github.com/nv-tlabs/kimodo) (a kinematic motion diffusion model) from
the keys you made in the motion editor and a text prompt, and bring it back as an Override layered on your clip.
The keyed poses stay as they are, and Kimodo fills the frames between them.

This sample is optional. The motion editor works without it, and nothing here is needed unless you use Kimodo.
No model weights are included: you download them yourself after checking their licenses (see below).

What is in this sample:

| File | What it does |
|---|---|
| `Editor/KimodoSkeleton.cs` | `Tools > Lilium Motion Editor > Kimodo > Create SOMA Skeleton From BVH...` makes the External Skeleton for Kimodo's SOMA skeleton from a BVH that Kimodo wrote (only the joint-name table is included) |
| `Editor/KimodoWindow.cs`, `KimodoRunner.cs`, `KimodoSettings.cs` | `Tools > Lilium Motion Editor > Kimodo > Generate Between Keys`: generates between the keys of the clip open in the Motion Editor window and layers the result on it |
| `Python/kimodo_constraints.py` | Turns the exported keys into a Kimodo constraints file. Runs inside the Kimodo environment |

## Licenses (check before use)

+ Kimodo code: Apache-2.0.
+ Kimodo models: the SOMA and G1 models (`Kimodo-SOMA-RP`, `Kimodo-SOMA-SEED`, `Kimodo-G1-RP`) are under the NVIDIA Open Model License and are commercially usable.
  NVIDIA claims no ownership of the outputs, and you are responsible for the outputs and their use. The SMPL-X model is for non-commercial research only; do not use it.
+ Kimodo encodes the text prompt with Meta's Llama 3 (`meta-llama/Meta-Llama-3-8B-Instruct`), under the Meta Llama 3 Community License.
  Its weights are gated: you request access on Hugging Face yourself. Llama 3.1 does not work in its place.
+ Read the license texts yourself. This sample only drives the tools; it does not grant any license.

## Requirements

+ An NVIDIA GPU. With the text encoder on the CPU (`TEXT_ENCODER_DEVICE=cpu`), Kimodo uses about 1.5 GB of video memory.
+ About 16 GB of free memory for the text encoder on the CPU. With Docker Desktop on Windows, WSL gets half of the PC's memory by default;
  raise it in `%USERPROFILE%\.wslconfig` (`[wsl2]` `memory=24GB`) if generation runs out of memory.
+ Docker (or a Python environment as in Kimodo's install guide), and about 50 GB of disk (the image is about 35 GB, the weights about 17 GB).
+ A Hugging Face account with access to `meta-llama/Meta-Llama-3-8B-Instruct`, and a Read token.

## Setup (once)

1. Request access to [meta-llama/Meta-Llama-3-8B-Instruct](https://huggingface.co/meta-llama/Meta-Llama-3-8B-Instruct) on Hugging Face and wait for approval.
2. Make a Read token on Hugging Face and run `hf auth login` (it is saved to `~/.cache/huggingface/token`).
3. Get Kimodo and its viewer fork. On Windows, keep the line endings as they are, or the Docker entry script breaks:

   ```
   git clone -c core.autocrlf=false https://github.com/nv-tlabs/kimodo.git
   cd kimodo
   git clone -c core.autocrlf=false https://github.com/nv-tlabs/kimodo-viser.git
   docker build -t kimodo:1.0 .
   ```

4. Make a T-pose BVH of Kimodo's skeleton (the weights are downloaded on the first run; it takes a while).
   In the commands below, `KIMODO` is the kimodo folder and `WORK` is a folder for the files you exchange (for example the project's `Temp/Kimodo`):

   ```
   docker run --rm --gpus all --shm-size 16g --ipc host --entrypoint /bin/bash ^
     -v KIMODO:/workspace -v WORK:/work -v %USERPROFILE%/.cache/huggingface:/workspace/.cache/huggingface ^
     -e HF_HOME=/workspace/.cache/huggingface -e TEXT_ENCODER_MODE=local -e TEXT_ENCODER_DEVICE=cpu ^
     kimodo:1.0 -lc "kimodo_gen 'a person stands still' --duration 1 --output /work/tpose --bvh --bvh_standard_tpose"
   ```

5. In Unity, `Tools > Lilium Motion Editor > Kimodo > Create SOMA Skeleton From BVH...`, pick `WORK/tpose.bvh`, and save the skeleton asset.

## Generate from the window

1. Open your clip in the Motion Editor window and key two or more poses (for a walk, for example, the contacts of a step). The clip must be 10 seconds or shorter.
2. `Tools > Lilium Motion Editor > Kimodo > Generate Between Keys`. The first time, open **Settings** and set the kimodo folder, the Docker image (`kimodo:1.0`),
   the Hugging Face cache (leave it empty for `%USERPROFILE%/.cache/huggingface`) and the SOMA skeleton. They are saved in the project's `UserSettings` folder.
3. Write the prompt in English (the whole motion, for example "a person walks forward at a normal pace"), choose the constraints, and press **Generate Between Keys**.
   The window checks Docker, the image, the token file and the skeleton before it starts, and says what is missing.
4. Generation runs in the background (about 5 minutes; most of it is loading the models). **Stop** removes the container.
   The work files go to the project's `Temp/Kimodo` folder.
5. When it finishes, the motion is imported as `<clip>.kimodo.override.anim` next to your clip, layered on it as an Override, and made the write target (✏) so you see it play.
   Your keyed poses are kept at their frames. If you changed the keys while Kimodo was working, the window asks before importing.

After importing:

+ **See the Result** puts the write target on the Override (the generated motion plays). Keys you set now go into the Override.
+ **Edit the Keys** puts the write target back on your clip. The generated motion is hidden while you edit, and the next generation uses your clip's keys.
+ The list shows how far Kimodo was from each key before the keys were kept. Over about 10 cm, the blend around that key is visible; add keys or change the prompt.
+ Generating again replaces the same Override clip (Undo brings back the previous one).

## Generate from your keys with the Unity CLI

With the Unity CLI (`unity cmd`, as the motion editor's AI skills use it). `P` is the project folder:

1. Open the clip and export its keys to Kimodo's skeleton:

   ```
   unity cmd motion_open --project-path P --prefab Assets/.../Character.fbx --clip Assets/.../Walk.rig.anim --json
   unity cmd motion_export_keys --project-path P --skeleton Assets/.../Kimodo SOMA Skeleton.asset --path WORK/keys.bvh --json > WORK/export.json
   ```

2. Make the constraints and generate (the copy of `Python/kimodo_constraints.py` is in `WORK`). The constraints script prints the `--duration` to use:

   ```
   docker run ... kimodo:1.0 -lc "python /work/kimodo_constraints.py /work/keys.bvh /work/export.json /work/constraints.json && kimodo_gen 'a person walks forward at a normal pace' --duration 1.034 --constraints /work/constraints.json --output /work/walk --bvh --bvh_standard_tpose"
   ```

3. Import the result as an Override, keeping your keyed poses at their frames:

   ```
   unity cmd motion_import_override --project-path P --source WORK/walk.bvh --skeleton Assets/.../Kimodo SOMA Skeleton.asset --override_clip Assets/.../Walk.kimodo.override.anim --keep_keys true --json
   unity cmd motion_lint --project-path P --json
   ```

   The response's `keyDeviations` tells how far Kimodo's motion was from each key before it was blended back.
   The Override is layered on the open clip; open the clip in the motion editor window to see it.

Notes:

+ The text prompt is required (English). Kimodo makes at most 10 seconds, at 30 fps.
+ `kimodo_constraints.py --mode` chooses what to pass: `fullbody` (the whole pose at each key, the default), `ends` (hands and feet only), or `root` (only the hips' path on the ground).
+ Fingers are not imported (Kimodo's skeleton has no finger joints in the model); key hand shapes in the motion editor.
+ Do not use `lock_feet` on Kimodo's motion: Kimodo already cleans up foot sliding, and locking feet that already stay put makes them pop.
+ Kimodo only moves the body. Attach held items (weapons) to the hands with the character settings; for two-handed items, put the hands back together with full-body IK after importing.
+ Each `kimodo_gen` run loads the models again (about 4 minutes with the text encoder on the CPU; about 1 minute per motion once loaded).
+ If Docker Desktop on Windows fails to start with "The file cannot be accessed by the system" on a `.sock` or `dockerInference` file,
  stop Docker Desktop and delete the stale socket files from WSL (`wsl -d docker-desktop -e sh -c 'rm -f /mnt/host/c/Users/<you>/AppData/Local/Docker/run/*'`).
