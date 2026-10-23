"""Build a Kimodo constraints JSON from the keys exported by Lilium Motion Editor.

Run inside the Kimodo environment (the Docker image or a virtual environment with
the kimodo package), because it reads the BVH with Kimodo's own reader.

  python kimodo_constraints.py keys.bvh export.json constraints.json [--mode fullbody|ends|root]

keys.bvh     BVH written by motion_export_keys (SOMA skeleton, standard T-pose rest)
export.json  The response of motion_export_keys (frames, frameRate, lastFrame)
             Either the whole CLI response or just its "result" object.

Kimodo starts its motions at the origin facing +Z and runs at 30 fps, so the keys are
moved and turned to match, and their frames are mapped to 30 fps. Import the result with
motion_import_override (align_root on, the default) to put it back where the clip is.

Prints the duration (seconds) to pass to kimodo_gen --duration.
"""

import argparse
import json
import math
import sys

import torch

from kimodo.geometry import matrix_to_axis_angle
from kimodo.motion_rep.feature_utils import compute_heading_angle
from kimodo.skeleton.bvh import parse_bvh_motion
from kimodo.skeleton.registry import build_skeleton

KIMODO_FPS = 30
MAX_SECONDS = 10.0

# Kimodo's end-effector presets (left-hand, right-hand, left-foot, right-foot)
END_TYPES = ("left-hand", "right-hand", "left-foot", "right-foot")


def read_export(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    # Accept the whole CLI response or only its result
    for key in ("data", "result"):
        if isinstance(data, dict) and key in data and isinstance(data[key], dict):
            data = data[key]
    return data


def rotate_y(angle, dtype):
    c, s = math.cos(angle), math.sin(angle)
    return torch.tensor([[c, 0, s], [0, 1, 0], [-s, 0, c]], dtype=dtype)


def read_keys(path, skeleton):
    """Read the local rotations and root positions of the keys' BVH (standard T-pose rest).

    Kimodo's bvh_to_kimodo_motion also smooths the root path, which fails on three
    frames or fewer (two or three keys), so read only what is used here.
    """
    rotations, positions, _ = parse_bvh_motion(path)
    if int(rotations.shape[1]) != int(skeleton.nbjoints):
        sys.exit("The BVH has %d joints but the SOMA skeleton has %d" % (rotations.shape[1], skeleton.nbjoints))
    device = skeleton.neutral_joints.device
    return rotations.to(device=device), positions.to(device=device, dtype=rotations.dtype)


def heading_angle(rotations, positions, skeleton):
    """Kimodo's heading angle of the body at each frame."""
    _, joints, _ = skeleton.fk(rotations, positions)
    return compute_heading_angle(joints.unsqueeze(0), skeleton).squeeze(0)


def canonicalize(rotations, positions, skeleton):
    """Move the first key's root to the origin and turn it to face +Z (heading 0)."""
    angle = float(heading_angle(rotations, positions, skeleton)[0])
    flat = torch.tensor([1.0, 0.0, 1.0], dtype=positions.dtype)
    # The sign of Kimodo's heading angle is not documented; take the one that ends at heading 0
    for sign in (1, -1):
        turn = rotate_y(sign * angle, rotations.dtype)
        r = rotations.clone()
        r[:, 0] = turn @ rotations[:, 0]
        p = (positions - positions[0] * flat) @ turn.T
        if math.cos(float(heading_angle(r, p, skeleton)[0])) > 0.99:
            return r, p
    raise RuntimeError("Could not turn the keys to face +Z")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("bvh")
    parser.add_argument("export")
    parser.add_argument("output")
    parser.add_argument("--mode", choices=("fullbody", "ends", "root"), default="fullbody",
                        help="fullbody: whole pose at each key (default). ends: hands and feet only. root: only the path of the hips on the ground")
    args = parser.parse_args()

    export = read_export(args.export)
    frames = [int(f) for f in export["frames"]]
    rate = float(export["frameRate"])
    # kimodo_gen makes int(duration * 30) frames; cover the clip's last frame
    kimodo_frames = round(int(export["lastFrame"]) * KIMODO_FPS / rate) + 1
    seconds = kimodo_frames / KIMODO_FPS + 0.001
    if kimodo_frames > MAX_SECONDS * KIMODO_FPS:
        sys.exit("The clip is %.2f s; Kimodo makes at most %.0f s" % (seconds, MAX_SECONDS))

    skeleton = build_skeleton(77)
    rotations, positions = read_keys(args.bvh, skeleton)
    if len(frames) != rotations.shape[0]:
        sys.exit("The BVH has %d frames but the export lists %d" % (rotations.shape[0], len(frames)))
    rotations, positions = canonicalize(rotations, positions, skeleton)

    # Clip frames to Kimodo frames (two keys on the same Kimodo frame keep the first)
    indices, rows = [], []
    for row, frame in enumerate(frames):
        index = round(frame * KIMODO_FPS / rate)
        if index in indices:
            continue
        indices.append(index)
        rows.append(row)
    rotations = rotations[rows]
    positions = positions[rows]

    if args.mode == "root":
        constraints = [{
            "type": "root2d",
            "frame_indices": indices,
            "smooth_root_2d": positions[:, [0, 2]].tolist(),
        }]
    else:
        item = {
            "frame_indices": indices,
            "local_joints_rot": matrix_to_axis_angle(rotations).tolist(),
            "root_positions": positions.tolist(),
        }
        if args.mode == "fullbody":
            constraints = [dict(item, type="fullbody")]
        else:
            constraints = [dict(item, type=t) for t in END_TYPES]

    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(constraints, f)
    print(json.dumps({"duration": round(seconds, 4), "frames": indices, "mode": args.mode}))


if __name__ == "__main__":
    main()
