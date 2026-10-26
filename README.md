# Lilium Motion Editor (Experimental)

English | [日本語](README.ja.md)

An editor extension for creating character motions inside the Unity Editor.

![Making the motion between keys with Kimodo in the Motion Editor window](Docs/Images/demo.gif)

+ No need to go back and forth with a DCC tool. Your edits show up in the motion right away.
+ Create motions non-destructively while checking the final pose as it will look in the game.
+ Edit poses with full-body IK.
+ Outputs Humanoid clips for the game, with Animation Rigging values and other properties.
+ Choose curve, linear, stepped or AI interpolation between keys, and cycle keys for loops.
+ Generates the poses between keys from a text prompt with NVIDIA's Kimodo motion model. (optional)
+ Under development. The specification will change in future versions.

## Dependencies

+ Unity 6000.0 or later
+ Burst / Mathematics (installed automatically as package dependencies)
+ Animation Rigging (optional. When installed, Animation Rigging values can be edited)

## Install

Enter the following in the Package Manager's `Install package from git URL...`.

```
https://github.com/you-ri/LiliumMotionEditor.git?path=/Packages/jp.lilium.motioneditor
```

## How to use

1. Open `Window > Lilium Motion Editor > Motion Editor`.
2. Pick a character prefab that has an Animator.
3. Pose it, set keys, and save to a clip.

+ Project-wide settings are in `Project Settings > Lilium Motion Editor`.
+ Editing rig definitions can be created with `Create > Lilium Motion Editor > Rig Definition`.
+ The UI language (English / 日本語) can be chosen with Language in `Preferences > Lilium Motion Editor`. It follows the OS language by default.

### Demo

Select this package in the Package Manager and import the `Demo` sample.

The demo models are the male and female characters from Quaternius' [Universal Base Characters](https://quaternius.com/packs/universalbasecharacters.html) (CC0).

`Demo/Poses` contains eight hand poses (Fist, Open, Point, and more). To show them in the PoseBank, add `{prefabFolder}/Poses` and the imported `Demo/Poses` folder (e.g. `Assets/Samples/Lilium Motion Editor/<version>/Demo/Poses`) to PoseBank Folders in `Project Settings > Lilium Motion Editor`. They are Humanoid finger values, so they can be pasted onto any Humanoid character with finger bones.

### Kimodo (optional)

The Kimodo (optional) sample lets NVIDIA's [Kimodo](https://github.com/nv-tlabs/kimodo) motion model generate the motion between your keys from a text prompt.
Kimodo itself is not included, but the editor has a setup feature for it. Check its license and run it yourself. The motion editor itself works without Kimodo.

To run Kimodo you need:

+ An NVIDIA GPU (RTX 20 series or later; Kimodo uses about 1.5 GB of video memory)
+ About 16 GB of free memory (the text encoder runs on the CPU)
+ About 25 GB of disk (the Python environment and the AI model)
+ A Hugging Face account with access to Llama 3 (`meta-llama/Meta-Llama-3-8B-Instruct`)
+ Checked on Windows 11 with an RTX 2070

## License

[MIT](LICENSE)

Icons use Google's [Material Icons](https://github.com/google/material-design-icons) (Apache License 2.0), and the demo models use Quaternius' Universal Base Characters (CC0). See `Third-Party Notices.txt` in the package for details.
