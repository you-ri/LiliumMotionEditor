# 全身 IK のデバッグ用の鎖を Blender で作って FBX に書き出す（このリポジトリだけのデバッグ用。パッケージには入れない）。
#
#   blender --background --factory-startup --python make_ik_chain.py -- <書き出す FBX のパス> [rope]
#
# rope を付けると、同じ骨・同じ台で、鎖の代わりに紐（丸い節を玉でつないだもの）を作る（IkRope.fbx。角度制限なしで試す）。
#
# 輪 1 つが骨 1 本。根元の台（Root）に立てた U の字の輪から、5 つの輪（Joint1〜Joint5）が真上へ噛み合って並び、反対側の台（End）に吊った U の字の輪で終わる。
# 両端の台は同じ形で、End は Root を上下に返した物。End の付け根は Joint5 と噛み合う所（そこを中心に回る）。
# 骨の付け根は、下の輪と噛み合う所（輪の内側の下端）。隣り合う輪は 90° 向きを変える（奇数の輪は左右、偶数の輪と台の輪は前後に開く）。
# 部品は留め金（Clasp_Root・Clasp_End。台と U の字の輪）と鎖の輪（Link1〜Link5）に分け、それぞれ 1 本の骨の子にする（曲げても輪は歪まず、折れた角度がそのまま見える）。
# 場所は Unity の向き（x 右・y 上・z 前）で書き、置くときに Blender の向きへ直す。Unity で Generic として取り込む（取り込みの設定は IkChainSetup.cs）。

import math
import sys

import bpy
import bmesh
from mathutils import Matrix, Vector

JOINT_COUNT = 5
# 輪の内側の長さ（関節の間隔）・針金の太さ（半径）・輪の幅（針金の中心で測った半分）。
# 幅は相手の輪の針金が通るよう、針金の太さの 2 倍より大きくする
PITCH = 0.2
WIRE_RADIUS = 0.025
LINK_HALF_WIDTH = 0.06
BASE_HEIGHT = 0.05
BASE_RADIUS = 0.15
# 台に立てた輪の、台の面から隣の関節までの高さ。End の台の外側の面は、End の付け根から ANCHOR_RISE + BASE_HEIGHT 上（IkChainSetup.cs の点の場所と合わせる）
ANCHOR_RISE = 0.1
SIDES = 12
ARC_SEGMENTS = 12
# 紐の太さ（半径）。関節の玉は紐より少し太くして、どれだけ曲げても継ぎ目が開かないようにする
ROPE_RADIUS = 0.02
KNOT_RADIUS = 0.024

BONES = ["Root"] + ["Joint%d" % i for i in range (1, JOINT_COUNT + 1)] + ["End"]


def unity_to_blender (v):
    """Unity の向きを Blender の向きへ（Unity の +X = Blender の -X、+Y = +Z、+Z = -Y。剣と同じ書き出しの設定）"""
    return Vector ((-v[0], -v[2], v[1]))


def bone_heights ():
    """骨の付け根の高さ（Unity の y）。Root は台の底、Joint1 は台の輪の内側の上端、その先は PITCH ずつで、最後が End"""
    first = BASE_HEIGHT + ANCHOR_RISE
    return [0.0] + [first + PITCH * i for i in range (JOINT_COUNT + 1)]


def side_of (link):
    """輪の開く向き（Unity の向き）。奇数の輪は左右（x）、偶数の輪と台の輪は前後（z）"""
    return Vector ((1, 0, 0)) if link % 2 == 1 else Vector ((0, 0, 1))


def create_material (name, color, metallic, roughness):
    material = bpy.data.materials.new (name)
    material.use_nodes = True
    shader = material.node_tree.nodes["Principled BSDF"]
    shader.inputs["Base Color"].default_value = (*color, 1)
    shader.inputs["Metallic"].default_value = metallic
    shader.inputs["Roughness"].default_value = roughness
    return material


def link_path (bottom, top, side, kind):
    """
    針金の中心の線（Unity の向き）。半円の中心の高さ bottom・top の間を、横 ±LINK_HALF_WIDTH の直線でつなぐ。
    kind は "ring"（閉じた輪）・"stand"（下の半円を除いた U。足が bottom まで下りて下の台に埋まる）・"hang"（上の半円を除いた U。足が top まで上がって上の台に埋まる）
    """
    up = Vector ((0, 1, 0))
    path = []

    def arc (center, start, end):
        for k in range (ARC_SEGMENTS + 1):
            a = start + (end - start) * k / ARC_SEGMENTS
            path.append (side * (LINK_HALF_WIDTH * math.cos (a)) + up * (center + LINK_HALF_WIDTH * math.sin (a)))

    if kind == "stand":
        path.append (side * LINK_HALF_WIDTH + up * bottom)
        arc (top, 0, math.pi)
        path.append (-side * LINK_HALF_WIDTH + up * bottom)
    elif kind == "hang":
        path.append (side * LINK_HALF_WIDTH + up * top)
        arc (bottom, 0, -math.pi)
        path.append (-side * LINK_HALF_WIDTH + up * top)
    else:
        arc (top, 0, math.pi)
        arc (bottom, math.pi, 2 * math.pi)
    return path


def add_tube (mesh, path, closed, side, material_index):
    """path に沿って針金の管を張る"""
    plane = side.cross (Vector ((0, 1, 0))).normalized ()
    count = len (path)
    rings = []
    for k in range (count):
        following = path[(k + 1) % count] if closed or k < count - 1 else path[k]
        preceding = path[(k - 1) % count] if closed or k > 0 else path[k]
        tangent = (following - preceding).normalized ()
        outward = tangent.cross (plane).normalized ()
        ring = []
        for s in range (SIDES):
            a = 2 * math.pi * s / SIDES
            ring.append (mesh.verts.new (unity_to_blender (path[k] + (plane * math.cos (a) + outward * math.sin (a)) * WIRE_RADIUS)))
        rings.append (ring)
    faces = []
    for k in range (count if closed else count - 1):
        a, b = rings[k], rings[(k + 1) % count]
        for s in range (SIDES):
            t = (s + 1) % SIDES
            faces.append (mesh.faces.new ((a[s], a[t], b[t], b[s])))
    for face in faces:
        face.material_index = material_index
        face.smooth = True
    return faces


def add_plate (mesh, bottom, material_index):
    """台（円盤）。底の高さは bottom"""
    result = bmesh.ops.create_cone (mesh, cap_ends=True, cap_tris=False, segments=32, radius1=BASE_RADIUS, radius2=BASE_RADIUS, depth=BASE_HEIGHT)
    for vertex in result["verts"]:
        vertex.co.z += bottom + BASE_HEIGHT / 2
    faces = {face for vertex in result["verts"] for face in vertex.link_faces}
    for face in faces:
        face.material_index = material_index
    return list (faces)


def create_armature ():
    data = bpy.data.armatures.new ("IkChain")
    armature = bpy.data.objects.new ("Armature", data)
    bpy.context.collection.objects.link (armature)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set (mode='EDIT')
    heights = bone_heights ()
    parent = None
    for i, name in enumerate (BONES):
        bone = data.edit_bones.new (name)
        bone.head = unity_to_blender ((0, heights[i], 0))
        # End は台の外側の面まで伸ばす（Unity では長さを使わない。Blender で見えるように）
        tail = heights[i + 1] if i + 1 < len (heights) else heights[i] + ANCHOR_RISE + BASE_HEIGHT
        bone.tail = unity_to_blender ((0, tail, 0))
        bone.parent = parent
        bone.use_connect = parent is not None
        bone.use_deform = True
        parent = bone
    bpy.ops.object.mode_set (mode='OBJECT')
    return armature


def create_part (name, armature, bone, materials, build):
    """部品を 1 つの物にして骨の子にする（骨と一緒に動くだけなので、重みを付けない）。build (mesh) が面を足して返す"""
    data = bpy.data.meshes.new (name)
    mesh = bmesh.new ()
    faces = build (mesh)
    bmesh.ops.recalc_face_normals (mesh, faces=faces)
    mesh.to_mesh (data)
    mesh.free ()
    for material in materials:
        data.materials.append (material)
    part = bpy.data.objects.new (name, data)
    bpy.context.collection.objects.link (part)
    # 骨の子にしても、頂点は今の場所（世界）のまま
    part.parent = armature
    part.parent_type = 'BONE'
    part.parent_bone = bone
    bpy.context.view_layer.update ()
    part.matrix_world = Matrix.Identity (4)
    return part


def create_chain (armature):
    # 隣り合う輪は明るさを変えて、どの輪がどれだけ回ったか見分けられるようにする
    materials = [
        create_material ("M_IkChain_Dark", (0.10, 0.11, 0.13), 0.9, 0.45),
        create_material ("M_IkChain_Light", (0.45, 0.47, 0.50), 0.9, 0.35),
    ]
    heights = bone_heights ()
    end = len (BONES) - 1

    # 根元の留め金: 台と、台に立てた輪。足は台に埋める。上の内側が 1 つめの関節
    def root_clasp (mesh):
        top = heights[1] + WIRE_RADIUS - LINK_HALF_WIDTH
        return add_plate (mesh, 0, 0) + add_tube (mesh, link_path (BASE_HEIGHT * 0.5, top, side_of (0), "stand"), False, side_of (0), 1)

    # 反対側の留め金: 根元を上下に返した形。下の内側が End の付け根。向きは Joint5 と 90° 違える
    def end_clasp (mesh):
        plate = heights[end] + ANCHOR_RISE
        bottom = heights[end] - WIRE_RADIUS + LINK_HALF_WIDTH
        return add_plate (mesh, plate, 0) + add_tube (mesh, link_path (bottom, plate + BASE_HEIGHT * 0.5, side_of (end), "hang"), False, side_of (end), 1)

    # 鎖の輪: 内側の下端が自分の関節、内側の上端が次の関節
    def link (i):
        def build (mesh):
            bottom = heights[i] - WIRE_RADIUS + LINK_HALF_WIDTH
            top = heights[i + 1] + WIRE_RADIUS - LINK_HALF_WIDTH
            return add_tube (mesh, link_path (bottom, top, side_of (i), "ring"), True, side_of (i), (i - 1) % 2)
        return build

    create_part ("Clasp_Root", armature, BONES[0], materials, root_clasp)
    for i in range (1, JOINT_COUNT + 1):
        create_part ("Link%d" % i, armature, BONES[i], materials, link (i))
    create_part ("Clasp_End", armature, BONES[end], materials, end_clasp)


def faces_of (result, material_index, smooth):
    """bmesh.ops の作った頂点の面に、材質と滑らかさを入れて返す"""
    faces = list ({face for vertex in result["verts"] for face in vertex.link_faces})
    for face in faces:
        face.material_index = material_index
        face.smooth = smooth
    return faces


def add_cord (mesh, bottom, top, material_index):
    """紐の節（Unity の y で bottom から top までの丸い棒）"""
    result = bmesh.ops.create_cone (mesh, cap_ends=True, cap_tris=False, segments=16, radius1=ROPE_RADIUS, radius2=ROPE_RADIUS, depth=top - bottom)
    for vertex in result["verts"]:
        vertex.co.z += (bottom + top) / 2
    return faces_of (result, material_index, True)


def add_knot (mesh, height, material_index):
    """関節の玉（Unity の y で height の所）"""
    result = bmesh.ops.create_uvsphere (mesh, u_segments=16, v_segments=8, radius=KNOT_RADIUS)
    for vertex in result["verts"]:
        vertex.co.z += height
    return faces_of (result, material_index, True)


def create_rope (armature):
    """紐。台は鎖と同じ。節は関節から次の関節まで、玉は関節に置いて子の骨に付ける。隣り合う節は色を変える"""
    materials = [
        create_material ("M_IkRope_Plate", (0.10, 0.11, 0.13), 0.9, 0.45),
        create_material ("M_IkRope_Dark", (0.35, 0.22, 0.12), 0.0, 0.8),
        create_material ("M_IkRope_Light", (0.75, 0.62, 0.42), 0.0, 0.8),
    ]
    heights = bone_heights ()
    end = len (BONES) - 1
    plate = heights[end] + ANCHOR_RISE

    # 根元の留め金: 台と、台から 1 つめの関節までの紐（足は台に埋める）
    def root_clasp (mesh):
        return add_plate (mesh, 0, 0) + add_cord (mesh, BASE_HEIGHT * 0.5, heights[1], 2)

    # 反対側の留め金: 台と、End の付け根（関節の玉）から台までの紐
    def end_clasp (mesh):
        return add_plate (mesh, plate, 0) + add_cord (mesh, heights[end], plate + BASE_HEIGHT * 0.5, 2) + add_knot (mesh, heights[end], 2)

    def cord (i):
        def build (mesh):
            material = 1 if i % 2 == 1 else 2
            return add_cord (mesh, heights[i], heights[i + 1], material) + add_knot (mesh, heights[i], material)
        return build

    create_part ("Clasp_Root", armature, BONES[0], materials, root_clasp)
    for i in range (1, JOINT_COUNT + 1):
        create_part ("Rope%d" % i, armature, BONES[i], materials, cord (i))
    create_part ("Clasp_End", armature, BONES[end], materials, end_clasp)


def main ():
    arguments = sys.argv[sys.argv.index ("--") + 1:]
    path = arguments[0]
    rope = "rope" in arguments[1:]
    bpy.ops.object.select_all (action='SELECT')
    bpy.ops.object.delete ()

    armature = create_armature ()
    if rope:
        create_rope (armature)
    else:
        create_chain (armature)

    bpy.ops.export_scene.fbx (
        filepath=path,
        object_types={'ARMATURE', 'MESH'},
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


main ()
