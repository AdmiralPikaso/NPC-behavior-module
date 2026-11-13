# Собирает assets/characters/polygon/PolygonCharacter.glb для Godot:
# персонаж Synty Polygon + выбранные in-place анимации (каждая отдельным клипом).
#
# Запуск (из любой папки):
#   "C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe" -b --factory-startup \
#       --python "F:\Юфушка\диплом\zero\tools\blender\build_character.py"
#
# Исходники берутся из assets/_source/synty (копии файлов пакета, сам пакет не трогаем).
# Почему через Blender, а не прямой импорт FBX в Godot: анимации Synty записаны
# относительно базовой позы скелета из анимационных FBX, а не T-позы персонажа.
# Здесь меш перепривязывается к скелету анимационного файла, и всё уходит в один GLB.
#
# С ключом --darkfantasy (после "--") вместо PolygonCharacter.glb собирается
# assets/characters/darkfantasy/DarkFantasyCharacters.glb: тот же скелет и те же 16 клипов,
# но кроме манекена (Mannequin__Body) в нём лежат персонажи Synty POLYGON Dark Fantasy
# (охотник и 4 скелета) с насадками. Каждый меш назван "<Скин>__<меш>", какой скин
# показать, решает src/Common/Visual/CharacterSkin.cs. Перед этим нужно выгрузить насадки:
#   Godot_v4.7.2-stable_mono_win64_console.exe --headless --path . -s res://tools/godot/export_character_parts.gd
#   blender.exe -b --factory-startup --python tools\blender\build_character.py -- --darkfantasy
import bpy, os, sys, json, math

ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
DARK = "--darkfantasy" in ARGS

PROJECT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SRC = os.path.join(PROJECT, "assets", "_source", "synty")
OUT = os.path.join(PROJECT, "assets", "characters", "polygon", "PolygonCharacter.glb")
TEXTURE = os.path.join(SRC, "Textures", "T_Polygon_Dummy_01.png")
DF_FBX = os.path.join(PROJECT, "assets", "Synty", "PolygonDarkFantasy", "Models", "Characters.fbx")
DF_DIR = os.path.join(PROJECT, "assets", "characters", "darkfantasy")
DF_PARTS = os.path.join(DF_DIR, "parts")
if DARK:
    OUT = os.path.join(DF_DIR, "DarkFantasyCharacters.glb")

# имя клипа в Godot -> файл анимации
CLIPS = {
    "Idle": "A_Idle_Standing_Masc.fbx",
    "Idle_Crouch": "A_Idle_Crouching_Masc.fbx",
    "Walk_F": "A_Walk_F_Masc.fbx",
    "Walk_B": "A_Walk_BckStrafeB_Masc.fbx",
    "Walk_L": "A_Walk_FwdStrafeL_Masc.fbx",
    "Walk_R": "A_Walk_FwdStrafeR_Masc.fbx",
    "Run_F": "A_Run_F_Masc.fbx",
    "Sprint_F": "A_Sprint_F_Masc.fbx",
    "Crouch_F": "A_Crouch_FwdStrafeF_Masc.fbx",
    "Crouch_B": "A_Crouch_BckStrafeB_Masc.fbx",
    "Crouch_L": "A_Crouch_FwdStrafeL_Masc.fbx",
    "Crouch_R": "A_Crouch_FwdStrafeR_Masc.fbx",
    "Jump": "A_Jump_Idle_Masc.fbx",
    "Jump_Run": "A_Jump_Running_Masc.fbx",
    "Fall": "A_InAir_FallShort_Masc.fbx",
    "Land": "A_Land_IdleSoft_Masc.fbx",
}

bpy.ops.wm.read_factory_settings(use_empty=True)


def imported(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    return [o for o in bpy.data.objects if o not in before]


def walk(bone):
    yield bone
    for ch in bone.children:
        yield from walk(ch)


# 1. Персонаж
char_objs = imported(os.path.join(SRC, "Character", "PolygonSyntyCharacter.fbx"))
char_rig = next(o for o in char_objs if o.type == 'ARMATURE')
meshes = [o for o in char_objs if o.type == 'MESH']

img = bpy.data.images.load(TEXTURE, check_existing=True)
for m in {m for o in meshes for m in o.data.materials if m and m.node_tree}:
    nt = m.node_tree
    for n in [n for n in nt.nodes if n.type == 'TEX_IMAGE']:
        if any(l.to_socket.name == "Base Color" for l in n.outputs["Color"].links):
            n.image = img
        else:
            nt.nodes.remove(n)

# 2. Скелет из анимационного файла, меш ставим в его базовую позу
anim_objs = imported(os.path.join(SRC, "Animations", CLIPS["Idle"]))
rig = next(o for o in anim_objs if o.type == 'ARMATURE')
if rig.animation_data:
    rig.animation_data.action = None
bpy.data.batch_remove([o for o in anim_objs if o is not rig])

rest = {b.name: rig.matrix_world @ b.matrix_local for b in rig.data.bones}
for root in [b for b in char_rig.data.bones if b.parent is None]:
    for b in walk(root):
        if b.name in rest:
            char_rig.pose.bones[b.name].matrix = char_rig.matrix_world.inverted() @ rest[b.name]
            bpy.context.view_layer.update()
for mesh in meshes:
    bpy.context.view_layer.objects.active = mesh
    for mod in list(mesh.modifiers):
        if mod.type == 'ARMATURE':
            bpy.ops.object.modifier_apply(modifier=mod.name)
    mw = mesh.matrix_world.copy()
    mesh.parent = rig
    mesh.matrix_world = mw
    mesh.modifiers.new("Armature", 'ARMATURE').object = rig
    mesh.name = "Body"
bpy.data.objects.remove(char_rig)
rig.name = "Rig"
rig.data.name = "Rig"


def df_bone_name(name, rig_bones):
    """Имя кости Dark Fantasy -> имя кости анимационного скелета.
    Тело совпадает, а пальцы названы по-разному: Finger_01_L/_R против Finger_01/Finger_01_1."""
    if name in rig_bones:
        return name
    if name.endswith("_L") and name[:-2] in rig_bones:
        return name[:-2]
    if name.endswith("_R") and name[:-2] + "_1" in rig_bones:
        return name[:-2] + "_1"
    return None


# Главный дочерний сустав: по нему считается направление кости.
PRIMARY_CHILD = {"Root": "Hips", "Hips": "Spine_01", "Spine_03": "Neck", "Hand_L": "Finger_01", "Hand_R": "Finger_01_1"}


def retarget_rest(df_rig, rig_bones):
    """Ставит скелет Dark Fantasy из его позы покоя (T-поза) в позу покоя анимационного Rig (A-поза).

    Скопировать матрицы костей Rig нельзя: локальные оси костей в Characters.fbx повёрнуты
    иначе (у позвоночника и ног на ~90°), и меш скручивается. Поэтому поворот каждой кости
    считается в мировых координатах по суставам, без опоры на оси: кость наследует поворот
    родителя и доворачивается кратчайшим путём так, чтобы направление «сустав -> главный
    дочерний сустав» совпало с Rig. Кисть дополнительно выравнивается вокруг своей оси по
    направлению на большой палец (иначе ладонь повёрнута не туда), а половина этого
    скручивания отдаётся предплечью, чтобы запястье не перекручивалось."""
    from mathutils import Matrix, Quaternion

    aw = df_rig.matrix_world
    head_df = {b.name: aw @ b.head_local for b in df_rig.data.bones}
    mat_df = {b.name: aw @ b.matrix_local for b in df_rig.data.bones}
    tgt = {b.name: df_bone_name(b.name, rig_bones) for b in df_rig.data.bones}
    head_a = {n: rest[t].translation for n, t in tgt.items() if t}
    delta = {}  # мировой поворот кости: поза Rig = delta @ поза DF

    def primary(b):
        want = PRIMARY_CHILD.get(tgt[b.name])
        kids = [c for c in b.children if tgt[c.name]]
        if want:
            kids = [c for c in kids if tgt[c.name] == want] or kids
        for c in kids:
            if (head_df[c.name] - head_df[b.name]).length > 1e-4 and (head_a[c.name] - head_a[b.name]).length > 1e-4:
                return c
        return None

    def twist_to(d, axis_a, side_df, side_a):
        # поворот вокруг axis_a, совмещающий проекции side_df (после d) и side_a
        v1 = d @ side_df
        v1 = v1 - axis_a * v1.dot(axis_a)
        v2 = side_a - axis_a * side_a.dot(axis_a)
        if v1.length < 1e-6 or v2.length < 1e-6:
            return Quaternion()
        return v1.normalized().rotation_difference(v2.normalized())

    for root in [b for b in df_rig.data.bones if b.parent is None]:
        for b in walk(root):
            if not tgt[b.name]:
                print("WARNING: кость", b.name, "без пары в анимационном скелете", flush=True)
                continue
            d = delta.get(b.parent.name, Quaternion()) if b.parent else Quaternion()
            c = primary(b)
            if c:
                v_df = d @ (head_df[c.name] - head_df[b.name])
                v_a = head_a[c.name] - head_a[b.name]
                d = v_df.normalized().rotation_difference(v_a.normalized()) @ d
                if tgt[b.name] in ("Hand_L", "Hand_R"):
                    thumb = next(ch for ch in b.children if tgt[ch.name].startswith("Thumb_01"))
                    tw = twist_to(d, v_a.normalized(), head_df[thumb.name] - head_df[b.name],
                                  head_a[thumb.name] - head_a[b.name])
                    print("кисть", b.name, "доворот %.0f°" % math.degrees(tw.angle), flush=True)
                    half = Quaternion(tw.axis, tw.angle / 2)
                    # половину отдать предплечью (Elbow) — для него ось та же с точностью до сгиба
                    elbow = b.parent
                    delta[elbow.name] = half @ delta[elbow.name]
                    d = tw @ d
            delta[b.name] = d

    # Предплечье получило доворот после того, как его дети уже посчитаны: кисть
    # и пальцы считаются от полного поворота, так что пересчитывать их не нужно.
    for root in [b for b in df_rig.data.bones if b.parent is None]:
        for b in walk(root):
            if not tgt[b.name]:
                continue
            m = (delta[b.name].to_matrix() @ mat_df[b.name].to_3x3()).to_4x4()
            m.translation = head_a[b.name]
            df_rig.pose.bones[b.name].matrix = aw.inverted() @ m
            bpy.context.view_layer.update()


def add_dark_fantasy():
    """2б. Персонажи Dark Fantasy на тот же скелет Rig (тем же способом, что и манекен)."""
    with open(os.path.join(DF_PARTS, "manifest.json"), encoding="utf-8") as f:
        manifest = json.load(f)
    rig_bones = {b.name for b in rig.data.bones}

    df_objs = imported(DF_FBX)
    df_rig = next(o for o in df_objs if o.type == 'ARMATURE')
    by_name = {o.name: o for o in df_objs if o.type == 'MESH'}

    # насадки (шляпы, шлемы, плащи) из Godot: уже в мировых координатах позы покоя
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=os.path.join(DF_PARTS, "attachments.glb"))
    parts = {o.name: o for o in bpy.data.objects if o not in before and o.type == 'MESH'}
    junk = [o for o in bpy.data.objects if o not in before and o.type != 'MESH']

    keep = []  # (объект, итоговое имя)
    for skin, entry in manifest.items():
        for mesh_name in entry["body"]:
            body = by_name[mesh_name]
            # проверка совмещения координат FBX (Blender) и префаба (Godot)
            chk = parts.get("CHECK__%s__%s" % (skin, mesh_name))
            if chk:
                a = [chk.matrix_world @ v.co for v in chk.data.vertices]
                b = [body.matrix_world @ v.co for v in body.data.vertices]  # меш в позе покоя FBX
                ca = sum(a, a[0] * 0) / len(a)
                cb = sum(b, b[0] * 0) / len(b)
                print("check", skin, mesh_name, "центр godot", tuple(round(x, 3) for x in ca),
                      "fbx", tuple(round(x, 3) for x in cb), flush=True)
            if any(o is body for o, _ in keep):  # одно тело в двух скинах
                body = body.copy(); body.data = body.data.copy()
                bpy.context.collection.objects.link(body)
            keep.append((body, "%s__%s" % (skin, mesh_name)))
        for att in entry["attachments"]:
            o = parts[att["node"]]
            o.parent = None
            vg = o.vertex_groups.new(name=att["bone"])
            vg.add(range(len(o.data.vertices)), 1.0, 'REPLACE')
            o.parent = df_rig
            o.matrix_parent_inverse = df_rig.matrix_world.inverted()
            o.modifiers.new("Armature", 'ARMATURE').object = df_rig
            keep.append((o, "%s__%s" % (skin, att["mesh"])))
    kept = {o for o, _ in keep}
    bpy.data.batch_remove([o for o in list(by_name.values()) + list(parts.values()) + junk if o not in kept])

    retarget_rest(df_rig, rig_bones)

    mat = bpy.data.materials.new("PolygonDarkFantasy")  # в Godot заменяется материалом пакета
    for o, name in keep:
        bpy.context.view_layer.objects.active = o
        for mod in list(o.modifiers):
            if mod.type == 'ARMATURE':
                bpy.ops.object.modifier_apply(modifier=mod.name)
        for vg in list(o.vertex_groups):
            target = df_bone_name(vg.name, rig_bones)
            if target is None:
                o.vertex_groups.remove(vg)
            else:
                vg.name = target
        mw = o.matrix_world.copy()
        o.parent = rig
        o.matrix_world = mw
        o.modifiers.new("Armature", 'ARMATURE').object = rig
        o.data.materials.clear()
        o.data.materials.append(mat)
        o.name = name
        o.data.name = name
        print("dark fantasy", name, "вершин", len(o.data.vertices), flush=True)
    bpy.data.objects.remove(df_rig)


if DARK:
    for mesh in meshes:
        mesh.name = "Mannequin__Body"
    add_dark_fantasy()

# 3. Анимации. У каждого анимационного FBX Synty своя базовая поза скелета, поэтому
# действие нельзя просто переназначить на общий скелет (присед превращается в стойку).
# Вместо этого каждую анимацию проигрываем на её родном скелете и запекаем на Rig
# мировые позы костей (Copy Transforms + bake с visual keying).
for a in list(bpy.data.actions):
    bpy.data.actions.remove(a)
rig.animation_data_create()
scene = bpy.context.scene
for clip, fname in CLIPS.items():
    new = imported(os.path.join(SRC, "Animations", fname))
    src = next(o for o in new if o.type == 'ARMATURE' and o.animation_data and o.animation_data.action)
    src_act = src.animation_data.action
    start, end = (int(round(v)) for v in src_act.frame_range)

    cons = []
    for pb in rig.pose.bones:
        if pb.name in src.pose.bones:
            c = pb.constraints.new('COPY_TRANSFORMS')
            c.target = src
            c.subtarget = pb.name
            cons.append(c)

    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='POSE')
    bpy.ops.pose.select_all(action='SELECT')
    bpy.ops.nla.bake(frame_start=start, frame_end=end, only_selected=False, visual_keying=True,
                     clear_constraints=True, use_current_action=False, bake_types={'POSE'})
    bpy.ops.object.mode_set(mode='OBJECT')

    act = rig.animation_data.action
    act.name = clip
    act.use_fake_user = True
    scene.frame_set((start + end) // 2)
    hips = rig.pose.bones["Hips"]
    print("clip", clip, (start, end), "hips_z", round((rig.matrix_world @ hips.head).z, 3), flush=True)
    rig.animation_data.action = None
    for pb in rig.pose.bones:  # сброс позы, чтобы следующий клип не унаследовал её
        pb.location = (0, 0, 0); pb.rotation_quaternion = (1, 0, 0, 0); pb.scale = (1, 1, 1)

    datas = [o.data for o in new if o.data]
    bpy.data.batch_remove(new)
    bpy.data.batch_remove([d for d in datas if d.users == 0])
    bpy.data.actions.remove(src_act)

for coll in (bpy.data.materials, bpy.data.images, bpy.data.meshes, bpy.data.armatures):
    for d in list(coll):
        if d.users == 0:
            coll.remove(d)

# 4. Экспорт
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.export_scene.gltf(
    filepath=OUT,
    export_format='GLB',
    export_animations=True,
    export_animation_mode='ACTIONS',
    export_force_sampling=True,
    export_def_bones=False,
    export_optimize_animation_size=False,
    export_yup=True,
)
print("saved", OUT, flush=True)
