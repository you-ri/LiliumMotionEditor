# デモの両手剣を Blender で作って FBX に書き出す（このリポジトリで自作した素材。パッケージと同じ MIT）。
#
#   blender --background --factory-startup --python make_two_handed_sword.py -- <書き出す FBX のパス>
#
# 原点は柄の真ん中。Unity へ取り込むと、物のローカルで +Y が切っ先、+Z が刃の向き、+X が刃の平らな面の向きになる。
# 空の Grip_R / Grip_L は、持つ手の手首の場所と向き（物のローカル）。向きは「T ポーズの体の右・上・前」を手と一緒に回した枠で書く。
# 右手なら、指（体の右）が刃の向きと切っ先の間、手の甲（体の上）が +X、親指（体の前）が切っ先の側を向く。Motion Editor の持ち物の握りの初期値になる。
# 空の Point_Tip / Point_Pommel / Point_Center は、切っ先・柄の一番下・重心（密度を一様としたメッシュの重心）。Motion Editor の持ち物の点の初期値になる。

import math
import sys

import bpy
import bmesh
from mathutils import Matrix, Vector

HANDLE_LENGTH = 0.30
HANDLE_RADIUS = 0.016
GUARD_WIDTH = 0.22
GUARD_THICKNESS = 0.028
BLADE_LENGTH = 0.95
BLADE_WIDTH = 0.055
BLADE_THICKNESS = 0.012
POMMEL_RADIUS = 0.028
# 手首から握る所まで（指の向きと手のひらの向き）。デモのキャラに合わせた仮の値で、リグエディタで直せる
WRIST_BEHIND = 0.075
WRIST_ABOVE = 0.03
GRIP_ALONG = 0.075
# 柄が手のひらを斜めに横切る角度（指を切っ先の方へ倒す）。直角に握ると、腕を前へ伸ばしたときに手首が折れる
GRIP_TILT = math.radians (40)


def create_material (name, color, metallic, roughness):
    material = bpy.data.materials.new (name)
    material.use_nodes = True
    shader = material.node_tree.nodes["Principled BSDF"]
    shader.inputs["Base Color"].default_value = (*color, 1)
    shader.inputs["Metallic"].default_value = metallic
    shader.inputs["Roughness"].default_value = roughness
    return material


def add_prism (mesh, rings, material_index):
    """断面（同じ頂点数の輪）を並べた柱を足す。両端は面で閉じる"""
    verts = [[mesh.verts.new (p) for p in ring] for ring in rings]
    count = len (rings[0])
    faces = [mesh.faces.new (verts[0][::-1]), mesh.faces.new (verts[-1])]
    for a, b in zip (verts, verts[1:]):
        for i in range (count):
            j = (i + 1) % count
            faces.append (mesh.faces.new ((a[i], a[j], b[j], b[i])))
    for face in faces:
        face.material_index = material_index


def circle (z, radius, sides=10):
    return [(radius * math.cos (2 * math.pi * i / sides), radius * math.sin (2 * math.pi * i / sides), z) for i in range (sides)]


def create_sword_mesh ():
    mesh = bmesh.new ()
    half = HANDLE_LENGTH / 2
    guard = half + GUARD_THICKNESS

    # 刃。断面はひし形で、切っ先へ細くする（Blender の -Y が刃の向き）
    def blade_ring (z, width, thickness):
        return [(0, -width, z), (thickness, 0, z), (0, width, z), (-thickness, 0, z)]

    tip = guard + BLADE_LENGTH
    add_prism (mesh, [
        blade_ring (guard, BLADE_WIDTH / 2, BLADE_THICKNESS / 2),
        blade_ring (tip - 0.12, BLADE_WIDTH * 0.42, BLADE_THICKNESS * 0.42),
        blade_ring (tip, 0.001, 0.001),
    ], 0)

    # 鍔
    def guard_ring (z):
        w, t = GUARD_WIDTH / 2, 0.022
        return [(-t, -w, z), (t, -w, z), (t, w, z), (-t, w, z)]

    add_prism (mesh, [guard_ring (half), guard_ring (guard)], 2)
    # 柄
    add_prism (mesh, [circle (-half, HANDLE_RADIUS), circle (half, HANDLE_RADIUS)], 1)
    # 柄頭
    z = -half - POMMEL_RADIUS
    rings = [circle (z + POMMEL_RADIUS * math.sin (a), POMMEL_RADIUS * math.cos (a)) for a in (-1.2, -0.6, 0, 0.6, 1.2)]
    add_prism (mesh, rings, 2)

    data = bpy.data.meshes.new ("TwoHandedSword")
    mesh.to_mesh (data)
    mesh.free ()
    for polygon in data.polygons:
        polygon.use_smooth = False
    data.materials.append (create_material ("M_Sword_Blade", (0.72, 0.75, 0.78), 1, 0.3))
    data.materials.append (create_material ("M_Sword_Grip", (0.16, 0.09, 0.06), 0, 0.8))
    data.materials.append (create_material ("M_Sword_Guard", (0.65, 0.5, 0.18), 1, 0.4))
    return data


def unity_to_blender (v):
    """Unity の物のローカルの向きを Blender の向きへ（Unity の +X = Blender の -X、+Y = +Z、+Z = -Y）"""
    return Vector ((-v[0], -v[2], v[1]))


def add_grip (name, center, side):
    """空を置く。center は握る所（Unity の Y）、side は右手 1・左手 -1"""
    # Unity の物のローカルで書く: X = 刃の平らな面、Y = 切っ先、Z = 刃の向き
    fingers = Vector ((0, math.sin (GRIP_TILT), math.cos (GRIP_TILT)))
    thumb = Vector ((0, math.cos (GRIP_TILT), -math.sin (GRIP_TILT)))
    back = Vector ((side, 0, 0))
    wrist = Vector ((0, center, 0)) - fingers * WRIST_BEHIND + back * WRIST_ABOVE
    # 手の枠の軸（T ポーズの体の右・上・前）。右手は指が体の右、左手は指が体の左
    x, y, z = fingers * side, back, thumb
    # 空の軸は Unity の X = Blender の -X 軸、Y = Blender の Z 軸、Z = Blender の -Y 軸（書き出しの axis の設定で確かめた値）
    empty = bpy.data.objects.new (name, None)
    empty.empty_display_size = 0.05
    bpy.context.collection.objects.link (empty)
    matrix = Matrix ((-unity_to_blender (x), -unity_to_blender (z), unity_to_blender (y))).transposed ().to_4x4 ()
    matrix.translation = unity_to_blender (wrist)
    empty.matrix_world = matrix


def center_of_mass (data):
    """閉じたメッシュの重心（Blender の向き）。原点と面でできる四面体の符号付き体積で求める"""
    data.calc_loop_triangles ()
    volume = 0
    center = Vector ((0, 0, 0))
    for triangle in data.loop_triangles:
        a, b, c = (data.vertices[i].co for i in triangle.vertices)
        v = a.dot (b.cross (c)) / 6
        volume += v
        center += v * (a + b + c) / 4
    return center / volume


def add_point (name, position):
    """空を置く。position は Blender の向きの場所"""
    empty = bpy.data.objects.new (name, None)
    empty.empty_display_size = 0.03
    bpy.context.collection.objects.link (empty)
    empty.location = position


def main ():
    path = sys.argv[sys.argv.index ("--") + 1]
    bpy.ops.object.select_all (action='SELECT')
    bpy.ops.object.delete ()

    sword = bpy.data.objects.new ("Sword", create_sword_mesh ())
    bpy.context.collection.objects.link (sword)

    add_grip ("Grip_R", GRIP_ALONG, 1)
    add_grip ("Grip_L", -GRIP_ALONG, -1)
    half = HANDLE_LENGTH / 2
    add_point ("Point_Tip", unity_to_blender ((0, half + GUARD_THICKNESS + BLADE_LENGTH, 0)))
    add_point ("Point_Pommel", unity_to_blender ((0, -half - POMMEL_RADIUS * (1 + math.sin (1.2)), 0)))
    add_point ("Point_Center", center_of_mass (sword.data))

    bpy.ops.export_scene.fbx (
        filepath=path,
        object_types={'MESH', 'EMPTY'},
        apply_scale_options='FBX_SCALE_ALL',
        bake_space_transform=True,
        axis_forward='-Z',
        axis_up='Y',
        add_leaf_bones=False,
        bake_anim=False,
        mesh_smooth_type='FACE',
    )


main ()
