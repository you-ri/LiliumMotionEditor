# 全身 IK のデバッグ用に、デモの男のキャラ（Quaternius の Universal Base Characters。CC0）から右腕を切り出し、棒を握らせて FBX に書き出す
# （このリポジトリだけのデバッグ用。パッケージには入れない）。
#
#   blender --background --factory-startup --python make_ik_arm.py -- <男のキャラの FBX> <書き出す腕の FBX> <書き出す棒の FBX>
#
# 骨は胸（spine_03）を根にして、右の鎖骨・上腕・前腕・手・指だけを残す。メッシュは右腕と胸の右半分だけを残し、重みを残した骨で付け直す。
# 指は曲げて握った形を基準姿勢にし、握りの中心に空（StickGrip。手の骨の子）を置く。
# 棒は持ち物（キャラの設定の heldObjects。持つ・離すをキーで切り替える）にするので、腕とは別の FBX にする（原点は棒の真ん中）。
# Unity で腕は Generic として取り込み、StickGrip の所に棒を置いた基準姿勢から、持ち物の握りを求める（IkArmSetup.cs）。

import math
import sys

import bpy
import bmesh
from mathutils import Matrix, Quaternion, Vector

ROOT_BONE = "spine_03"
ARM_TOP = "clavicle_r"
BODY_MESH = "SuperHero_Male"
# 指の節ごとの曲げ（度。付け根から）。手のひらは下向きなので、指は下へ曲がる
FINGER_CURL = (70, 85, 60)
THUMB_CURL = (15, 30, 30)
# 棒の太さ（半径）と長さ。握りの中心は中指の付け根から手首の側へ・手のひらの側へずらした所
STICK_RADIUS = 0.017
STICK_LENGTH = 0.8
GRIP_OFFSET = Vector ((-0.012, 0.0, -0.032))
GRIP_NAME = "StickGrip"
# 胸の右半分だけを残す（左右の真ん中から右へ。体の右は Blender の +x）
CHEST_SIDE = 0.0


def find_armature ():
    for obj in bpy.data.objects:
        if obj.type == 'ARMATURE':
            return obj
    raise RuntimeError ("アーマチュアが無い")


def kept_bones (armature):
    """胸と、鎖骨から先の骨の名前"""
    names = {ROOT_BONE}
    stack = [armature.data.bones[ARM_TOP]]
    while stack:
        bone = stack.pop ()
        names.add (bone.name)
        stack.extend (bone.children)
    return names


def trim_mesh (body, keep):
    """
    残す骨に重みの半分以上（割合で）が乗る面だけを残す。腕に乗らない面（胸の骨だけの面）は体の右半分だけ。
    元のモデルは重みの合計が 1 でない頂点（0.1〜0.4）があるので、重みの値でなく合計に対する割合で見る（値で見ると、腕・手の頂点が抜けて穴が開いた）。
    頂点ごとに消すと境目の面が歯抜けになるので、面ごとに決める（面の頂点の割合の平均）。残す骨以外の重みは根（胸）へ移してから付け直す
    """
    arm = {name for name in keep if name != ROOT_BONE}
    names = {group.index: group.name for group in body.vertex_groups}
    mesh = bmesh.new ()
    mesh.from_mesh (body.data)
    deform = mesh.verts.layers.deform.verify ()
    world = body.matrix_world
    kept_share = {}
    arm_share = {}
    for vertex in mesh.verts:
        weights = vertex[deform]
        total = sum (weights.values ())
        kept_share[vertex] = sum (w for g, w in weights.items () if names.get (g) in keep) / total if total > 0 else 0
        arm_share[vertex] = sum (w for g, w in weights.items () if names.get (g) in arm) / total if total > 0 else 0
    remove = []
    for face in mesh.faces:
        count = len (face.verts)
        kept = sum (kept_share[v] for v in face.verts) / count
        on_arm = sum (arm_share[v] for v in face.verts) / count
        center = world @ face.calc_center_median ()
        if kept < 0.5 or (on_arm < 0.05 and center.x < CHEST_SIDE):
            remove.append (face)
    bmesh.ops.delete (mesh, geom=remove, context='FACES_ONLY')
    bmesh.ops.delete (mesh, geom=[v for v in mesh.verts if not v.link_faces], context='VERTS')
    # 消す骨の重みは根（胸）へ移す（胸のまわりの頂点が、付け直したときに残りの骨へ引っ張られないように）
    root = next (g.index for g in body.vertex_groups if g.name == ROOT_BONE)
    for vertex in mesh.verts:
        weights = vertex[deform]
        moved = sum (w for g, w in weights.items () if names.get (g) not in keep)
        if moved <= 0:
            continue
        weights[root] = weights.get (root, 0) + moved
        for g in [g for g in weights.keys () if names.get (g) not in keep]:
            del weights[g]
    mesh.to_mesh (body.data)
    mesh.free ()
    for group in list (body.vertex_groups):
        if group.name not in keep:
            body.vertex_groups.remove (group)
    bpy.context.view_layer.objects.active = body
    body.select_set (True)
    bpy.ops.object.vertex_group_normalize_all (lock_active=False)


def trim_bones (armature, keep):
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set (mode='EDIT')
    for bone in list (armature.data.edit_bones):
        if bone.name not in keep:
            armature.data.edit_bones.remove (bone)
    bpy.ops.object.mode_set (mode='OBJECT')


def curl (armature, name, axis_world, degrees):
    """骨を、世界の軸 axis_world まわりに degrees 曲げる（姿勢で。付け根まわり）"""
    pose_bone = armature.pose.bones[name]
    rest = (armature.matrix_world.to_3x3 () @ pose_bone.bone.matrix_local.to_3x3 ()).normalized ()
    axis = (rest.inverted () @ axis_world).normalized ()
    pose_bone.rotation_mode = 'QUATERNION'
    pose_bone.rotation_quaternion = Quaternion (axis, math.radians (degrees))


def make_fist (armature, body):
    """指を曲げて握った形にし、その形を基準姿勢にする（メッシュは曲げた形で焼き、骨の基準も曲げた形に置き直す）"""
    # 手のひらは下向き（Blender の -z）、指は体の外（+x）。+y まわりに回すと、指先は下へ曲がる
    down = Vector ((0, 1, 0))
    for finger in ("index", "middle", "ring", "pinky"):
        for segment, degrees in zip (("01", "02", "03"), FINGER_CURL):
            curl (armature, "%s_%s_r" % (finger, segment), down, degrees)
    thumb = armature.data.bones["thumb_02_r"].head_local - armature.data.bones["thumb_01_r"].head_local
    thumb_axis = thumb.cross (Vector ((0, 0, -1))).normalized ()
    for segment, degrees in zip (("01", "02", "03"), THUMB_CURL):
        curl (armature, "thumb_%s_r" % segment, thumb_axis, degrees)
    bpy.context.view_layer.update ()

    bpy.ops.object.select_all (action='DESELECT')
    bpy.context.view_layer.objects.active = body
    body.select_set (True)
    modifier = next (m for m in body.modifiers if m.type == 'ARMATURE')
    bpy.ops.object.modifier_apply (modifier=modifier.name)

    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set (mode='POSE')
    bpy.ops.pose.select_all (action='SELECT')
    bpy.ops.pose.armature_apply (selected=False)
    bpy.ops.object.mode_set (mode='OBJECT')
    modifier = body.modifiers.new ("Armature", 'ARMATURE')
    modifier.object = armature


def add_grip (armature):
    """握りの中心に空（StickGrip）を置き、手の骨の子にする。Unity で持ち物の棒を置く所と、握り（物から見た手首）を求める目印"""
    grip = armature.matrix_world @ armature.data.bones["middle_01_r"].head_local + GRIP_OFFSET
    empty = bpy.data.objects.new (GRIP_NAME, None)
    empty.empty_display_size = 0.03
    bpy.context.collection.objects.link (empty)
    empty.parent = armature
    empty.parent_type = 'BONE'
    empty.parent_bone = "hand_r"
    bpy.context.view_layer.update ()
    empty.matrix_world = Matrix.Translation (grip)
    print ("GRIP", tuple (round (v, 4) for v in grip))


def export_stick (path):
    """持ち物の棒だけの FBX。原点は棒の真ん中、長さは Blender の y（Unity で持ち物の点は、メッシュの長い向きの両端と真ん中から作る）"""
    bpy.ops.object.select_all (action='SELECT')
    bpy.ops.object.delete ()
    data = bpy.data.meshes.new ("Stick")
    mesh = bmesh.new ()
    # create_cone は z に伸びるので、x まわりに 90° 回して y へ向ける
    result = bmesh.ops.create_cone (mesh, cap_ends=True, cap_tris=False, segments=16, radius1=STICK_RADIUS, radius2=STICK_RADIUS, depth=STICK_LENGTH,
                                    matrix=Matrix.Rotation (math.pi / 2, 4, 'X'))
    for face in {f for v in result["verts"] for f in v.link_faces}:
        face.smooth = True
    mesh.to_mesh (data)
    mesh.free ()
    material = bpy.data.materials.new ("M_IkArm_Stick")
    material.use_nodes = True
    shader = material.node_tree.nodes["Principled BSDF"]
    shader.inputs["Base Color"].default_value = (0.55, 0.36, 0.18, 1)
    shader.inputs["Roughness"].default_value = 0.7
    data.materials.append (material)
    stick = bpy.data.objects.new ("Stick", data)
    bpy.context.collection.objects.link (stick)
    export (path, {'MESH'})


def export (path, types):
    bpy.ops.export_scene.fbx (
        filepath=path,
        object_types=types,
        apply_scale_options='FBX_SCALE_ALL',
        axis_forward='-Z',
        axis_up='Y',
        primary_bone_axis='Y',
        secondary_bone_axis='X',
        add_leaf_bones=False,
        use_armature_deform_only=True,
        bake_anim=False,
        mesh_smooth_type='FACE',
    )


def main ():
    arguments = sys.argv[sys.argv.index ("--") + 1:]
    source, path, stick_path = arguments[0], arguments[1], arguments[2]
    bpy.ops.object.select_all (action='SELECT')
    bpy.ops.object.delete ()
    bpy.ops.import_scene.fbx (filepath=source)

    armature = find_armature ()
    body = bpy.data.objects[BODY_MESH]
    for obj in list (bpy.data.objects):
        if obj.type == 'MESH' and obj != body:
            bpy.data.objects.remove (obj)
    keep = kept_bones (armature)
    trim_mesh (body, keep)
    trim_bones (armature, keep)
    make_fist (armature, body)
    add_grip (armature)
    export (path, {'ARMATURE', 'MESH', 'EMPTY'})
    export_stick (stick_path)


main ()
