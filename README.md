# Lilium Motion Editor (Experimental)

English | [日本語](README.ja.md)

An editor extension for creating character motions inside the Unity Editor.

+ You pose a character through an "editing rig" that duplicates its bones, and the rig values are saved to an animation clip.
+ Handles FK, two-bone IK for arms and legs, full-body IK, and Animation Rigging values.
+ Clips made with the same editing rig can be shared between characters with different body proportions.
+ Under development. The specification will change in future versions.

## Dependencies

+ Unity 6000.0 or later (on 6000.6 or later, the bone handles in the preview window use Unity's tool system, and you can pick tools from the Tools overlay)
+ Burst / Mathematics (installed automatically as package dependencies)
+ Animation Rigging (optional. When installed, Animation Rigging values can be edited)
+ Timeline (optional. When installed, Timeline clips can be edited)

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

`Demo/Poses` contains eight hand poses (Fist, Open, Point, and more). To show them in the PoseBank, add `{prefabFolder}/Poses` and the imported `Demo/Poses` folder (e.g. `Assets/Samples/Lilium Motion Editor/0.1.0/Demo/Poses`) to PoseBank Folders in `Project Settings > Lilium Motion Editor`. They are Humanoid finger values, so they can be pasted onto any Humanoid character with finger bones.

## License

[MIT](LICENSE)

Icons use Google's [Material Icons](https://github.com/google/material-design-icons) (Apache License 2.0), and the demo models use Quaternius' Universal Base Characters (CC0). See `Third-Party Notices.txt` in the package for details.
