extends Node3D
## Галерея скинов: все скины из DarkFantasyCharacters.glb рядом, по очереди играют клипы.
## Рядом с каждым — прозрачная капсула игрока/NPC (r 0.35, h 1.8), чтобы проверить рост,
## ступни на полу и присед. Сцена tools/godot/SkinGallery.tscn, запуск (кадры — в папку):
##   Godot_v4.7.2-stable_mono_win64_console.exe --path . res://tools/godot/SkinGallery.tscn --write-movie C:/tmp/g/f.png --fixed-fps 30
## Параметры: CLIP — играть только этот клип; CLOSE — крупный план одного скина (индекс).

const MODEL := "res://assets/characters/darkfantasy/DarkFantasyCharacters.glb"
const SKIN_SCRIPT := "res://src/Common/Visual/CharacterSkin.cs"
const SKINS := ["Mannequin", "Hunter", "Skeleton", "SkeletonLightArmor", "SkeletonHeavyArmor", "SkeletonRanger"]
const CLIPS := ["Idle", "Walk_F", "Run_F", "Sprint_F", "Walk_L", "Idle_Crouch", "Crouch_F", "Jump", "Fall", "Land"]
const CLIP_TIME := 2.5
const SPACING := 1.3

@export var clip := ""
@export var close := -1
@export var yaw_deg := 25.0

var _players: Array[AnimationPlayer] = []
var _label: Label3D
var _t := 0.0
var _index := -1


func _ready() -> void:
	var env := WorldEnvironment.new()
	env.environment = Environment.new()
	env.environment.background_mode = Environment.BG_COLOR
	env.environment.background_color = Color(0.32, 0.34, 0.38)
	env.environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.environment.ambient_light_color = Color(0.7, 0.7, 0.75)
	env.environment.ambient_light_energy = 0.6
	add_child(env)
	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-45, 30, 0)
	sun.shadow_enabled = true
	add_child(sun)

	var floor_mesh := MeshInstance3D.new()
	var plane := PlaneMesh.new()
	plane.size = Vector2(12, 6)
	floor_mesh.mesh = plane
	var fm := StandardMaterial3D.new()
	fm.albedo_color = Color(0.45, 0.45, 0.42)
	floor_mesh.material_override = fm
	add_child(floor_mesh)

	var scene := load(MODEL) as PackedScene
	var cap_mat := StandardMaterial3D.new()
	cap_mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	cap_mat.albedo_color = Color(0.2, 0.6, 1.0, 0.18)
	cap_mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	for i in SKINS.size():
		var x := (i - (SKINS.size() - 1) / 2.0) * SPACING
		var model := scene.instantiate() as Node3D
		model.set_script(load(SKIN_SCRIPT))
		model.set("Skin", i + 1)  # CharacterSkin.SkinKind: 0 — AutoSkeleton, дальше как в SKINS
		model.position = Vector3(x, 0, 0)
		model.rotation_degrees.y = yaw_deg
		add_child(model)
		var ap := model.find_child("AnimationPlayer", true, false) as AnimationPlayer
		for c in CLIPS:
			if ap.has_animation(c) and c not in ["Jump", "Land"]:
				ap.get_animation(c).loop_mode = Animation.LOOP_LINEAR
		_players.append(ap)
		var cap := MeshInstance3D.new()
		var cm := CapsuleMesh.new()
		cm.radius = 0.35
		cm.height = 1.8
		cap.mesh = cm
		cap.material_override = cap_mat
		cap.position = Vector3(x + 0.55, 0.9, -0.3)
		add_child(cap)
		var name_label := Label3D.new()
		name_label.text = SKINS[i]
		name_label.font_size = 28
		name_label.position = Vector3(x, 0.05, 0.6)
		name_label.rotation_degrees.x = -60
		add_child(name_label)

	_label = Label3D.new()
	_label.font_size = 64
	_label.position = Vector3(0, 2.4, 0)
	add_child(_label)

	var cam := Camera3D.new()
	if close >= 0:
		var x := (close - (SKINS.size() - 1) / 2.0) * SPACING
		cam.position = Vector3(x + 0.9, 1.3, 2.4)
		cam.look_at_from_position(cam.position, Vector3(x + 0.25, 0.9, 0))
	else:
		cam.position = Vector3(0, 1.5, 5.0)
		cam.look_at_from_position(cam.position, Vector3(0, 0.9, 0))
	cam.current = true
	add_child(cam)
	_next()


func _process(delta: float) -> void:
	_t += delta
	if _t >= CLIP_TIME:
		_t = 0.0
		_next()


func _next() -> void:
	_index += 1
	if clip == "" and _index >= CLIPS.size():
		get_tree().quit()
		return
	var c: String = clip if clip != "" else CLIPS[_index]
	_label.text = c
	for ap in _players:
		ap.play(c)
