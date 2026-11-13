extends SceneTree
## Шаг 1 сборки персонажей Dark Fantasy (шаг 2 — tools/blender/build_character.py --darkfantasy).
##
## Персонажи Synty POLYGON Dark Fantasy состоят из скиннингованного тела (оно есть в
## Models/Characters.fbx, его Blender читает сам) и насадок — шляп, волос, шлемов, плащей,
## которые в пакете лежат только как готовые меши Godot (Models/extracted/*.res) и вешаются
## на кости через BoneAttachment3D. Blender такие меши прочитать не может, поэтому здесь
## каждая видимая насадка выбранных префабов запекается в мировые координаты позы покоя
## (как она стоит на персонаже в префабе) и выгружается в один GLB вместе с именем кости.
## Тела тоже выгружаются (только для проверки совмещения координат в Blender).
##
## Результат (не в git, собран из платных ассетов):
##   assets/characters/darkfantasy/parts/attachments.glb   — меши насадок и тел в мировых координатах;
##   assets/characters/darkfantasy/parts/manifest.json     — состав каждого скина.
##
## Запуск:
##   Godot_v4.7.2-stable_mono_win64_console.exe --headless --path . -s res://tools/godot/export_character_parts.gd

const PREFABS := "res://assets/Synty/PolygonDarkFantasy/Prefabs/Characters/"
const OUT_DIR := "res://assets/characters/darkfantasy/parts/"

## Скин в игре -> префаб Synty.
const SKINS := {
	"Hunter": "SM_Chr_Hunter_Male_01",
	"Skeleton": "SM_Chr_Skeleton_01",
	"SkeletonLightArmor": "SM_Chr_Skeleton_LightArmor_01",
	"SkeletonHeavyArmor": "SM_Chr_Skeleton_HeavyArmor_01",
	"SkeletonRanger": "SM_Chr_Skeleton_Ranger_01",
}

var _instances := {}
var _frames := 0


func _initialize() -> void:
	for skin in SKINS:
		var scene := load(PREFABS + SKINS[skin] + ".tscn") as PackedScene
		if scene == null:
			push_error("нет префаба " + SKINS[skin])
			quit(1)
			return
		var inst := scene.instantiate()
		root.add_child(inst)
		_instances[skin] = inst


func _process(_delta: float) -> bool:
	# пара кадров, чтобы BoneAttachment3D встали на кости
	_frames += 1
	if _frames < 3:
		return false
	quit(_export())
	return true


func _export() -> int:
	var out := Node3D.new()
	out.name = "Parts"
	var manifest := {}
	for skin in _instances:
		var inst: Node = _instances[skin]
		var skeleton := inst.get_node("Skeleton3D") as Skeleton3D
		var entry := {"prefab": SKINS[skin], "body": [], "attachments": []}
		var index := 0
		for mi in _meshes(inst):
			if not mi.is_visible_in_tree():
				continue
			var bone := _attachment_bone(mi)
			var name: String
			if bone == "":
				if mi.get_parent() != skeleton:
					push_warning("%s: меш %s не на кости и не тело, пропущен" % [skin, mi.name])
					continue
				entry["body"].append(String(mi.name))
				name = "CHECK__%s__%s" % [skin, mi.name]
			else:
				name = "ATT__%s__%s__%d" % [skin, bone, index]
				entry["attachments"].append({"node": name, "mesh": String(mi.name), "bone": bone})
				index += 1
			var baked := _world_mesh(mi)
			if baked == null:
				push_error("%s: не удалось запечь %s" % [skin, mi.name])
				return 1
			var node := MeshInstance3D.new()
			node.name = name
			node.mesh = baked
			out.add_child(node)
		manifest[skin] = entry
		print("[персонажи] %s: тело %s, насадки %s" % [skin, entry["body"], entry["attachments"].map(func(a): return "%s@%s" % [a["mesh"], a["bone"]])])

	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))
	# папку parts Godot импортировать не должен: это промежуточные файлы для Blender
	FileAccess.open(OUT_DIR + ".gdignore", FileAccess.WRITE).store_string("")
	var doc := GLTFDocument.new()
	var state := GLTFState.new()
	var err := doc.append_from_scene(out, state)
	if err == OK:
		err = doc.write_to_filesystem(state, ProjectSettings.globalize_path(OUT_DIR + "attachments.glb"))
	var f := FileAccess.open(OUT_DIR + "manifest.json", FileAccess.WRITE)
	f.store_string(JSON.stringify(manifest, "  "))
	f.close()
	out.free()
	print("[персонажи] сохранено %sattachments.glb: %s" % [OUT_DIR, error_string(err)])
	return 0 if err == OK else 1


func _meshes(node: Node) -> Array[MeshInstance3D]:
	var res: Array[MeshInstance3D] = []
	for ch in node.get_children():
		if ch is MeshInstance3D:
			res.append(ch)
		res.append_array(_meshes(ch))
	return res


func _attachment_bone(node: Node) -> String:
	var p := node.get_parent()
	while p != null:
		if p is BoneAttachment3D:
			return (p as BoneAttachment3D).bone_name
		p = p.get_parent()
	return ""


## Меш в мировых координатах позы покоя, без материалов и костей.
func _world_mesh(mi: MeshInstance3D) -> ArrayMesh:
	var src: Mesh = mi.mesh
	if src == null:
		return null
	# Скиннингованные меши считаем вручную (bake_mesh_from_current_skeleton_pose
	# без рендера в --headless не работает): вершина = Σ w · (скелет · поза кости · bind).
	var skeleton := mi.get_node_or_null(mi.skeleton) as Skeleton3D if mi.skin != null else null
	var binds: Array[Transform3D] = []
	if skeleton != null:
		for b in mi.skin.get_bind_count():
			var bone := mi.skin.get_bind_bone(b)
			if bone < 0:
				bone = skeleton.find_bone(mi.skin.get_bind_name(b))
			binds.append(skeleton.global_transform * skeleton.get_bone_global_pose(bone) * mi.skin.get_bind_pose(b))
	var res := ArrayMesh.new()
	for s in src.get_surface_count():
		var arrays := src.surface_get_arrays(s)
		var xf := mi.global_transform
		var verts: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
		var normals: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
		if skeleton != null and arrays[Mesh.ARRAY_BONES] != null:
			var bones = arrays[Mesh.ARRAY_BONES]
			var weights = arrays[Mesh.ARRAY_WEIGHTS]
			var per: int = bones.size() / verts.size()
			for i in verts.size():
				var p := Vector3.ZERO
				var n := Vector3.ZERO
				for k in per:
					var w: float = weights[i * per + k]
					if w > 0.0:
						var m := binds[int(bones[i * per + k])]
						p += (m * verts[i]) * w
						n += (m.basis * normals[i]) * w
				verts[i] = p
				normals[i] = n.normalized()
		else:
			for i in verts.size():
				verts[i] = xf * verts[i]
			for i in normals.size():
				normals[i] = (xf.basis * normals[i]).normalized()
		var clean := []
		clean.resize(Mesh.ARRAY_MAX)
		clean[Mesh.ARRAY_VERTEX] = verts
		clean[Mesh.ARRAY_NORMAL] = normals
		clean[Mesh.ARRAY_TEX_UV] = arrays[Mesh.ARRAY_TEX_UV]
		clean[Mesh.ARRAY_INDEX] = arrays[Mesh.ARRAY_INDEX]
		res.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, clean)
	return res
