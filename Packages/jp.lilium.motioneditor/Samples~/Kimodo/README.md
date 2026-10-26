# Kimodo (optional)

Generate motion with NVIDIA's [Kimodo](https://github.com/nv-tlabs/kimodo) (a kinematic motion diffusion model) from
the keys you made in the motion editor and a text prompt, and bring it back as an Override layered on your clip.
The keyed poses stay as they are, and Kimodo fills the frames between them.

This sample is optional. The motion editor works without it, and nothing here is needed unless you use Kimodo.
No model weights are included: you download them yourself after checking their licenses (see below).

What is in this sample:

| File | What it does |
|---|---|
| `Editor/KimodoSkeleton.cs` | `Assets > Create > Lilium Motion Editor > Kimodo SOMA Skeleton From BVH...` makes the External Skeleton for Kimodo's SOMA skeleton from a BVH that Kimodo wrote (only the joint-name table is included) |
| `Editor/KimodoOverlay.cs`, `KimodoSetup.cs`, `KimodoRunner.cs`, `KimodoSettings.cs`, `KimodoTextServer.cs` | The **Kimodo** panel of the Motion Editor window (`Window > Lilium Motion Editor > Kimodo` shows it): generates between the keys of the clip open in the window, and layers the result on it or uses it as the interpolation between keys. Until Kimodo is set up, the panel shows a button that opens the install guide |
| `Editor/KimodoInstallWindow.cs` | `Window > Lilium Motion Editor > Kimodo Install` (or **Open the Install Guide...** on the panel and the settings page): the install guide, steps 1 to 3 and **Install Everything...** |
| `Editor/KimodoConsoleWindow.cs` | `Window > Lilium Motion Editor > Kimodo Console` (or **Console** on the panel): the log of the generation, the details of a failure, and the text server's log |
| `Editor/KimodoText.cs`, `Editor/Locales/en.json`, `ja.json` | The words of the panel, the console and the settings, in the Motion Editor's language (`Edit > Preferences > Lilium Motion Editor > Language`). Add a language by adding its file next to these (keys start with `KIMODO_`) |
| `Editor/Icons/Generate.png`, `d_Generate.png` (and `@2x`) | The icon of the panel and the console: a sparkle for "make with AI", made for this sample (other motion models may be added later) |
| `Editor/Icons/Kimodo.png`, `Kimodo@2x.png` | The icon of the Kimodo Install window: a "K" in the green of Kimodo's banner, made for this sample (Kimodo has no official icon; this is not an NVIDIA logo) |
| `Python/kimodo_constraints.py` | Turns the exported keys into a Kimodo constraints file. Runs inside the Kimodo environment |
| `Python/install_kimodo.ps1` | Installs Kimodo into a Python environment in one go (Windows): what **Install Everything...** on the panel runs |
| `Python/kimodo_run.py` | Runs one generation (constraints, then `kimodo_generate.py`) in a Python environment, as the panel does without Docker |
| `Python/kimodo_generate.py` | Runs `kimodo_gen` with a cache of encoded prompts, so generating again with the same prompt skips the text model |

## Licenses (check before use)

**Built with Meta Llama 3.**

This sample is not made, endorsed or supported by NVIDIA or Meta. It contains no code, model weights or data of Kimodo, Llama 3 or SOMA:
it only runs the Kimodo you install yourself, and it does not grant any license. Read the license texts yourself.

+ Kimodo code: Apache-2.0 (the [License section](https://github.com/nv-tlabs/kimodo#license) of Kimodo's README).
  Kimodo installs other open source software too; see its [ATTRIBUTIONS.MD](https://github.com/nv-tlabs/kimodo/blob/main/ATTRIBUTIONS.MD).
+ Kimodo models: the SOMA and G1 models (`Kimodo-SOMA-RP`, `Kimodo-SOMA-SEED`, `Kimodo-G1-RP`, `Kimodo-G1-SEED` and their versions; see the table in Kimodo's README)
  are under the [NVIDIA Open Model License](https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/), and their model cards say they are "ready for commercial use".
  The license has conditions: you follow NVIDIA's Trustworthy AI terms, the license ends if you sue NVIDIA over the model or bypass its guardrails,
  and NVIDIA claims no ownership of the outputs while you are responsible for the outputs and their use (including claims by others).
+ The SMPL-X model (`Kimodo-SMPLX-RP`) is for non-commercial research only; do not use it.
+ Kimodo encodes the text prompt with [LLM2Vec](https://huggingface.co/McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp) (MIT) on top of Meta's Llama 3 (`meta-llama/Meta-Llama-3-8B-Instruct`),
  under the [Meta Llama 3 Community License](https://www.llama.com/llama3/license/) and its [Acceptable Use Policy](https://www.llama.com/llama3/use-policy/).
  The Llama 3 weights are gated: you request access on Hugging Face yourself. Llama 3.1 does not work in its place.

## Requirements

+ An NVIDIA GPU. With the text encoder on the CPU (`TEXT_ENCODER_DEVICE=cpu`), Kimodo uses about 1.5 GB of video memory.
+ About 16 GB of free memory for the text encoder on the CPU.
+ A Hugging Face account with access to `meta-llama/Meta-Llama-3-8B-Instruct`, and a Read token.
+ A Python environment with Kimodo (recommended; about 25 GB of disk with the weights), or Docker (about 50 GB: the image is about 35 GB).
+ Kimodo is developed on Linux. The Python environment was checked on Windows 11 with an RTX 2070. macOS has no CUDA, so Kimodo would run on the CPU only (not checked).

## Setup (once)

**The easy way (Windows):** open `Window > Lilium Motion Editor > Kimodo Install` (or **Open the Install Guide...** on the Kimodo panel) and do its three steps:

1. **Apply on Hugging Face:** make an account, apply to use Llama 3, and make a Read token (the buttons open the pages).
2. **Install Everything...:** choose the folder to put Kimodo in. A window opens and runs `Python/install_kimodo.ps1` (below); paste the token when it asks.
3. **Check That It Works:** press it when the window says Done. It also makes Kimodo's skeleton from the T-pose the script made.

Everything else (Docker, the commands, each setting) is under **Advanced** in that window. The steps below are what it does, for doing it by hand.

1. Request access to [meta-llama/Meta-Llama-3-8B-Instruct](https://huggingface.co/meta-llama/Meta-Llama-3-8B-Instruct) on Hugging Face and wait for approval.
2. Make a Read token on Hugging Face. You run `hf auth login` with it below (it is saved to `~/.cache/huggingface/token`).
3. Get Kimodo:

   ```
   git clone -c core.autocrlf=false https://github.com/nv-tlabs/kimodo.git
   ```

4. Install Kimodo in a Python environment (recommended) or build the Docker image (below).

### Python environment (recommended)

**All at once (Windows):** press **Install Everything...** in the Kimodo Install window and choose the folder to put Kimodo in.
A window opens and runs `Python/install_kimodo.ps1`, which does the steps below one at a time:
it checks git, uv and the C++ compiler (and asks before installing the missing ones with winget), downloads Kimodo into `<folder>\kimodo`,
makes `.venv` (Python 3.11), installs PyTorch, CMake and Kimodo, signs in to Hugging Face (`hf auth login`) if there is no token yet,
and runs Kimodo once to make `<folder>\kimodo\tpose.bvh` (this downloads the models, about 20 GB).
Steps that are already done are skipped, so run it again after a failure. Unity fills in **Python** with `<folder>\kimodo\.venv\Scripts\python.exe`;
when the window says it is done, press **Check That It Works**, which also makes the skeleton asset (`Kimodo SOMA Skeleton.asset` in the sample folder) from `tpose.bvh`.
You can also run it yourself (`-Check` only reports what is installed and changes nothing):

```
powershell -ExecutionPolicy Bypass -File install_kimodo.ps1 -Folder C:\Tools -Check
powershell -ExecutionPolicy Bypass -File install_kimodo.ps1 -Folder C:\Tools
```

**Step by step:** Kimodo builds a small C++ part when it is installed, so it needs a C++ compiler: on Windows, Visual Studio with "Desktop development with C++".
With [uv](https://docs.astral.sh/uv/), in the kimodo folder (Windows; on Linux use `.venv/bin/python` and `export PATH=...`):

```
uv venv .venv --python 3.11
uv pip install --python .venv\Scripts\python.exe torch==2.6.0 --index-url https://download.pytorch.org/whl/cu124
uv pip install --python .venv\Scripts\python.exe cmake
set PATH=%CD%\.venv\Scripts;%PATH%
uv pip install --python .venv\Scripts\python.exe -e .
.venv\Scripts\hf auth login
```

Choose the PyTorch build for your GPU and driver at [pytorch.org](https://pytorch.org/get-started/locally/) (the one above supports GPUs from the RTX 20 series).
Then make a T-pose BVH of Kimodo's skeleton (the weights are downloaded on the first run; it takes a while). `WORK` is a folder for the files you exchange:

```
set TEXT_ENCODER_MODE=local
set TEXT_ENCODER_DEVICE=cpu
.venv\Scripts\kimodo_gen "a person stands still" --duration 1 --output WORK\tpose --bvh --bvh_standard_tpose
```

In the Kimodo panel's setup steps (or later in `Edit > Project Settings > Lilium Motion Editor > Kimodo`), set **Run Kimodo With** to **Python environment** and **Python** to `kimodo\.venv\Scripts\python.exe`.
This runs Kimodo directly on the PC: no WSL memory limit and no Docker Desktop. A new prompt takes about 2 minutes, the same prompt again about 40 seconds.

### Docker

Get the viewer fork too and build the image. On Windows, keep the line endings as they are, or the Docker entry script breaks:

```
cd kimodo
git clone -c core.autocrlf=false https://github.com/nv-tlabs/kimodo-viser.git
docker build -t kimodo:1.0 .
```

Run `hf auth login` on the PC (install it with `pip install --upgrade huggingface_hub` if you do not have `hf`).

Make the T-pose BVH (`KIMODO` is the kimodo folder):

```
docker run --rm --gpus all --shm-size 16g --ipc host --entrypoint /bin/bash ^
  -v KIMODO:/workspace -v WORK:/work -v %USERPROFILE%/.cache/huggingface:/workspace/.cache/huggingface ^
  -e HF_HOME=/workspace/.cache/huggingface -e TEXT_ENCODER_MODE=local -e TEXT_ENCODER_DEVICE=cpu ^
  kimodo:1.0 -lc "kimodo_gen 'a person stands still' --duration 1 --output /work/tpose --bvh --bvh_standard_tpose"
```

In the Kimodo panel, set **Run Kimodo With** to **Docker**, the kimodo folder and the image (`kimodo:1.0`).
With Docker Desktop on Windows, WSL gets half of the PC's memory by default; raise it in `%USERPROFILE%\.wslconfig` (`[wsl2]` `memory=24GB`) if generation runs out of memory.

### The SOMA skeleton

In Unity, `Assets > Create > Lilium Motion Editor > Kimodo SOMA Skeleton From BVH...`, pick `WORK/tpose.bvh`, and save the skeleton asset.

## Generate from the panel

1. Open your clip in the Motion Editor window and key two or more poses (for a walk, for example, the contacts of a step). The clip must be 10 seconds or shorter.
2. Use the **Kimodo** panel of the Motion Editor window (if it is hidden, `Window > Lilium Motion Editor > Kimodo`, or the window's overlay menu).
   Until Kimodo is set up, the panel only says so and shows **Open the Install Guide...**, which opens the Kimodo Install window (see Setup).
   When everything is ready, the panel switches to generating.
   Change the settings later in `Edit > Project Settings > Lilium Motion Editor > Kimodo` (**Settings...** on the panel opens it).
   They are saved in the project's `UserSettings` folder (settings of this PC, not shared through the repository).
3. Write the prompt in English (the whole motion, for example "a person walks forward at a normal pace"), choose the constraints and how to use the result
   (**Use the Result as**: **Interpolation between keys**, the default, or **Override layer on the clip**), and press **Generate**.
   The panel checks the Python environment (or Docker and the image), the token file and the skeleton before it starts; if Kimodo is missing, it asks you to open the install guide.
4. Generation runs in the background (see "Faster generations" for how long). **Stop** stops it.
   **Console** opens the Kimodo Console window with the log. The work files go to the project's `Temp/Kimodo` folder (**Open Work Folder** in the console).
5. When it finishes, the motion is used as described below, and you see it play.

### Use the result as the interpolation between keys (default)

With **Use the Result as** set to **Interpolation between keys**, Kimodo makes the whole clip (up to 10 seconds) every time, so the panel does not ask which segments
(the spans between two neighboring keys) to fill. At first every segment uses the motion; switch single segments to Curve, Linear or Stepped from the timeline (below).
New segments made by adding keys use the motion too.
The motion is not layered as an Override: it is saved next to your clip as `<clip>.between.anim`, and the AI segments take their in-between frames from it.
Every in-between frame of an AI segment uses the motion. The motion does not land exactly on your keys, so the difference at the two keys is spread over the segment:
the clip stays exactly on your keys and moves the way the motion moves in between. The other segments keep their curve, linear or stepped interpolation.
Full-body IK points follow the same rules as in any other segment: a point that is Locked on the keys at both ends stays on its path and holds the motion there
(for example a planted foot). Make a point Free (or Pin) on those keys to let the generated motion move it.

+ In the timeline, segments that use the generated motion are drawn with a wavy arrow. Click the arrow between two keys to select it (drag a box, Shift + click or Ctrl + click to select several),
  then right-click it to switch the selected segments between Curve, Linear, Stepped and AI (the generated motion). Choosing AI on a Linear or Stepped segment puts the curve back under it, so the segment is a curve again if you later switch AI off.
+ If you change a key's value afterwards, the segment keeps the motion and the arrow turns dotted (it was made for the old keys; generate again to match).
  If you add, move or delete a key inside a segment, the segment stops using the motion and goes back to its curve.
+ **Bake the AI Motion into Keys** (the same arrow menu) turns a segment into keys, so the clip no longer needs the `.between.anim` file (for example, to copy the clip to another project).
  Lint reports a missing `.between.anim`, and segments made for older keys.
+ Each generation makes the whole clip again and replaces `.between.anim`.
+ With **Auto Generate** on (the default), it generates again each time you set, move or delete a key.
  Opening a clip does not start a generation by itself. Turn it off on the panel to generate only with **Generate**.
  It starts 1.5 seconds after the last change, and only when the keys differ from the ones the current result was made from.
  If the keys change while it is generating, the result is imported for the segments that still fit, and then it generates again with the current keys.
  Auto runs import without asking, and each run uses the GPU for about 30 seconds to 2 minutes.
+ While generating, the sparkle icon animates (on the collapsed panel, the panel's button in the window toolbar, the **Generate** button and the Kimodo Console tab),
  and in the timeline the segments being generated are drawn as a flowing wavy arrow.

### Layer the result as an Override

With **Use the Result as** set to **Override layer on the clip**, the motion is imported as `<clip>.kimodo.override.anim` next to your clip, layered on it as an Override, and made the write target (✏) so you see it play.
Your keyed poses are kept at their frames. If you changed the keys while Kimodo was working, you are asked before importing.

+ **See the Result** puts the write target on the Override (the generated motion plays). Keys you set now go into the Override.
+ **Edit the Keys** puts the write target back on your clip. The generated motion is hidden while you edit, and the next generation uses your clip's keys.
+ The list shows how far Kimodo was from each key before the keys were kept. Over about 10 cm, the blend around that key is visible; add keys or change the prompt.
+ Generating again replaces the same Override clip (Undo brings back the previous one).

### Faster generations

Most of a generation is the text model (Llama 3 8B): loading it takes about 4 minutes, and encoding one prompt on the CPU about 50 seconds.

+ **The same prompt again** (after changing the keys, for example) takes about 30 seconds: the panel keeps each encoded prompt in the project's `Temp/Kimodo/text-cache`,
  so the text model is not needed at all.
+ **A new prompt** loads the text model. In a Python environment this takes about 2 minutes in all. With Docker it takes about 6 minutes, unless the text server is running.

With Docker, **Start Text Server** on the Kimodo panel keeps it loaded in a container
(`lilium-kimodo-text`, port 9550 on this PC only), and generations use it instead of loading it again.
It keeps using about 15 GB of memory until you press **Stop Text Server** (Unity asks when you quit).
With the default WSL memory (half of the PC's), the server leaves little room for the generation, and a new prompt takes about 2 minutes instead of about 1.
With Docker Desktop on Windows, WSL gets half of the PC's memory by default; if the server or a generation stops with exit code 137, raise it in
`%USERPROFILE%\.wslconfig` (`[wsl2]` `memory=24GB`).

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
