extends SceneTree
## Собирает визуальные слои из ассетов Synty POLYGON Dark Fantasy (тема: заброшенное кладбище
## у руин собора):
##   scenes/arena/decor/ArenaDecor.tscn     — для scenes/arena/Arena.tscn;
##   scenes/test/decor/TestLevelDecor.tscn  — для scenes/test/TestLevel.tscn.
##
## Каждой серой коробке ставится набор префабов, вписанный в её габариты по AABB видимой
## геометрии (функция _fit), чтобы видимое совпадало с коллайдером. Коллизии префабов при
## запуске удаляет скрипт ArenaDecor.cs (он же скрывает меши серых коробок).
##
## Запуск (после правок раскладки — пересобрать сцены):
##   Godot_v4.7.2-stable_mono_win64_console.exe --headless --path . -s res://tools/godot/build_arena_decor.gd
## Перед запуском должен быть собран C# (dotnet build), иначе у корня не будет скрипта.

const PREFABS := "res://assets/Synty/PolygonDarkFantasy/Prefabs/"
const MATERIALS := "res://assets/Synty/PolygonDarkFantasy/Materials/"
const OUT := "res://scenes/arena/decor/ArenaDecor.tscn"
const OUT_TEST := "res://scenes/test/decor/TestLevelDecor.tscn"

var _root: Node3D
var _aabbs := {}
var _counter := {}


func _initialize() -> void:
	var err := _save("ArenaDecor", OUT, func() -> void:
		_build_floor(_group("Floor"))
		_build_cover(_group("Cover"))
		_build_border(_group("Border"))
		_build_torches(_group("Torches"))
		_build_scatter(_group("Scatter"))
		_build_background(_group("Background")))
	if err == OK:
		err = _save("TestLevelDecor", OUT_TEST, _build_test_level)
	quit(0 if err == OK else 1)


func _save(name: String, out: String, build: Callable) -> Error:
	_root = Node3D.new()
	_root.name = name
	_root.set_script(load("res://src/Arena/ArenaDecor.cs"))
	_counter.clear()
	build.call()
	var packed := PackedScene.new()
	var err := packed.pack(_root)
	if err == OK:
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out.get_base_dir()))
		err = ResourceSaver.save(packed, out)
	print("[декор] сохранено %s: %s, префабов: %d" % [out, error_string(err), _total()])
	_root.free()
	return err


# ---------------------------------------------------------------- серые коробки арены

## Низкое укрытие 3 × 1.2 × 0.6: кирпичная стенка с каменными столбиками по краям и карнизом.
func _low_wall(parent: Node3D, name: String, pos: Vector3, yaw: float, broken := false) -> void:
	var g := _group(name, parent, pos, yaw)
	for x in [-1.275, 1.275]:
		_fit(g, "Building/SM_Bld_Railing_Pillar_01", Vector3(x, 0.6, 0), Vector3(0.45, 1.2, 0.6))
	_fit(g, "Base/SM_Bld_Base_Wall_Half_01", Vector3(0, 0.525, 0), Vector3(2.1, 1.05, 0.5))
	_fit(g, "Base/SM_Bld_Base_Roof_Cap_01", Vector3(0, 1.125, 0), Vector3(2.1, 0.15, 0.6))
	if broken:
		# обломки у подножия — плоские, ниже колена
		_drop(g, "Props/SM_Prop_Rubble_Pile_Burnt_03", Vector3(0.6, 0, 0.75), 30.0, Vector3(0.6, 0.5, 0.45))


## Скамья собора 3 × 1.2 × 0.6 (длинная ось — локальная X).
func _pew(parent: Node3D, name: String, pos: Vector3, yaw: float) -> void:
	var g := _group(name, parent, pos, yaw)
	_fit(g, "Props/SM_Prop_Pew_Large_01", Vector3(0, 0.6, 0), Vector3(3.0, 1.2, 0.6))


## Блок 2 × 2 × 2 — гробница.
func _tomb(parent: Node3D, name: String, pos: Vector3, yaw: float) -> void:
	var g := _group(name, parent, pos, yaw)
	_fit(g, "Props/SM_Prop_Tomb_01", Vector3(0, 1.0, 0), Vector3(2.0, 2.0, 2.0))


## Блок 2 × 2 × 2 — постамент со статуей (статуя выше 2 м — только картинка, над головами).
func _statue(parent: Node3D, name: String, pos: Vector3, yaw: float, statue: String) -> void:
	var g := _group(name, parent, pos, yaw)
	_fit(g, "Building/SM_Bld_Pillar_04", Vector3(0, 1.0, 0), Vector3(2.0, 2.0, 2.0))
	_drop(g, statue, Vector3(0, 2.0, 0), 0.0, Vector3.ONE * 0.9)


## Блок 2 × 2 × 2 — штабель из двух больших ящиков.
func _crates(parent: Node3D, name: String, pos: Vector3, yaw: float) -> void:
	var g := _group(name, parent, pos, yaw)
	_fit(g, "Props/SM_Prop_Crate_02", Vector3(0, 0.5, 0), Vector3(2.0, 1.0, 2.0))
	_fit(g, "Props/SM_Prop_Crate_02", Vector3(0, 1.5, 0), Vector3(1.75, 1.0, 1.75), 8.0)


## Стена length × height × 0.5 (по умолчанию 6 × 2.5): три столба и два кирпичных пролёта с карнизом.
func _wall(parent: Node3D, name: String, pos: Vector3, yaw: float, length := 6.0, height := 2.5) -> void:
	var g := _group(name, parent, pos, yaw)
	var edge := length / 2.0 - 0.25
	for x in [-edge, 0.0, edge]:
		_fit(g, "Building/SM_Bld_Pillar_01", Vector3(x, height / 2.0, 0), Vector3(0.5, height, 0.5))
	var w := (length - 1.5) / 2.0
	for x in [-(0.25 + w / 2.0), 0.25 + w / 2.0]:
		_fit(g, "Base/SM_Bld_Base_Wall_01", Vector3(x, (height - 0.2) / 2.0, 0), Vector3(w, height - 0.2, 0.4))
		_fit(g, "Base/SM_Bld_Base_Roof_Cap_01", Vector3(x, height - 0.1, 0), Vector3(w, 0.2, 0.5))


func _build_cover(c: Node3D) -> void:
	# центр
	_low_wall(c, "Mid", Vector3(0, 0, 0), 0.0, true)
	_statue(c, "BlockW", Vector3(-6, 0, 0), 90.0, "Props/SM_Prop_Statue_People_01")
	_statue(c, "BlockE", Vector3(6, 0, 0), -90.0, "Props/SM_Prop_Statue_People_02")
	_crates(c, "BlockMidW", Vector3(-9, 0, 3), 0.0)
	_crates(c, "BlockMidE", Vector3(9, 0, -3), 90.0)
	# север — руины собора
	_wall(c, "WallN", Vector3(0, 0, -8), 0.0)
	_low_wall(c, "LowNW", Vector3(-10, 0, -7), 0.0)
	_low_wall(c, "LowNE", Vector3(10, 0, -7), 180.0, true)
	_tomb(c, "BlockNW", Vector3(-4, 0, -12), 0.0)
	_tomb(c, "BlockNE", Vector3(4, 0, -12), 180.0)
	_pew(c, "SideNW", Vector3(-13, 0, -12), 90.0)
	_pew(c, "SideNE", Vector3(13, 0, -12), -90.0)
	# юг — кладбище
	_wall(c, "WallS", Vector3(0, 0, 8), 180.0)
	_low_wall(c, "LowSW", Vector3(-10, 0, 7), 0.0, true)
	_low_wall(c, "LowSE", Vector3(10, 0, 7), 180.0)
	_tomb(c, "BlockSW", Vector3(-4, 0, 12), 90.0)
	_tomb(c, "BlockSE", Vector3(4, 0, 12), -90.0)
	_low_wall(c, "SideSW", Vector3(-13, 0, 12), 90.0)
	_low_wall(c, "SideSE", Vector3(13, 0, 12), -90.0, true)
	# боковые длинные стены
	_wall(c, "LongW", Vector3(-15, 0, 0), 90.0)
	_wall(c, "LongE", Vector3(15, 0, 0), -90.0)


# ---------------------------------------------------------------- граница: кладбищенская ограда

func _build_border(b: Node3D) -> void:
	# 12 пролётов по 3.33 м на сторону; столбы смещены наружу, чтобы не выступать внутрь бордюра.
	var n := 12
	var step := 40.0 / n
	for side in 4:
		var yaw := side * 90.0
		var g := _group("Side%d" % side, b, Vector3.ZERO, yaw)
		for i in n:
			var x := -20.0 + step * (i + 0.5)
			if side == 0 and i in [5, 6]:
				continue # ворота посередине южной стороны
			var fence := "Building/SM_Bld_Fence_Cemetary_01" if (i + side) % 3 != 0 else "Building/SM_Bld_Fence_Cemetary_02"
			_fit(g, fence, Vector3(x, 0.95, 20.0), Vector3(step - 0.8, 1.9, 0.41))
		for i in n:
			var x := -20.0 + step * i
			if side == 0 and i == 6:
				continue # середина ворот
			_fit(g, "Building/SM_Bld_Fence_Pillar_Cemetary_01", Vector3(x, 1.18, 20.2), Vector3(0.8, 2.36, 0.8))
	# ворота (закрыты) на юге, за спиной у точки старта игрока
	_drop(b, "Building/SM_Bld_Gates_Cemetary_01", Vector3(0, 0, 20.25), 0.0, Vector3(1.63, 1.0, 1.0))


# ---------------------------------------------------------------- светильники под Torch1..6

func _build_torches(t: Node3D) -> void:
	# Позиции совпадают с OmniLight Torch1..6 в Arena.tscn (свет не трогаем, высота огня ≈ 2.2 м).
	var spots := [Vector3(0, 0, -2), Vector3(-11, 0, -10), Vector3(11, 0, -10),
		Vector3(-8, 0, 6), Vector3(8, 0, 6), Vector3(0, 0, 12)]
	for i in spots.size():
		var g := _group("Torch%d" % (i + 1), t, spots[i], 0.0)
		# тонкий каменный столб и жаровня сверху
		_fit(g, "Base/SM_Bld_Base_Pillar_02", Vector3(0, 0.8, 0), Vector3(0.3, 1.6, 0.3))
		# чаша ниже лампы (2.2 м) и без теней, чтобы не затенять пол вокруг источника света
		_no_shadows(_drop(g, "Props/SM_Prop_Brazier_01", Vector3(0, 1.6, 0), 0.0, Vector3.ONE * 0.6))
		_flame(g, Vector3(0, 2.08, 0))


# ---------------------------------------------------------------- пол

func _build_floor(f: Node3D) -> void:
	# земля кладбища: плоскость 40 × 40 поверх скрытого серого пола
	var ground_mat := StandardMaterial3D.new()
	ground_mat.albedo_texture = load("res://assets/Synty/PolygonDarkFantasy/Textures/Misc/PolygonDarkFantasy_Stone_01.png")
	ground_mat.albedo_color = Color(0.5, 0.47, 0.38)
	ground_mat.roughness = 0.95
	ground_mat.uv1_scale = Vector3(8, 8, 1)
	_plane(f, "Ground", Vector3(0, 0.003, 0), Vector2(40, 40), ground_mat)
	# пол бывшего нефа собора на севере
	var tiles := (load(MATERIALS + "Misc/Floor_Tiles_01_mat.tres") as StandardMaterial3D).duplicate() as StandardMaterial3D
	tiles.uv1_scale = Vector3(28.0 / 4.0, 11.0 / 4.0, 1)
	_plane(f, "NaveTiles", Vector3(0, 0.007, -14.25), Vector2(28, 11), tiles)
	# земля за оградой
	var outer_mat := ground_mat.duplicate() as StandardMaterial3D
	outer_mat.albedo_color = Color(0.32, 0.31, 0.27)
	outer_mat.uv1_scale = Vector3(30, 30, 1)
	_plane(f, "Outer", Vector3(0, -0.02, 0), Vector2(160, 160), outer_mat)


# ---------------------------------------------------------------- мелочь на полу (плоская, проходимая)

func _build_scatter(s: Node3D) -> void:
	var rng := RandomNumberGenerator.new()
	rng.seed = 20261110
	var flat := ["Environment/SM_Env_Ground_Grass_Round_01", "Environment/SM_Env_Ground_Dirt_Round_01",
		"Environment/SM_Env_Grunge_01", "Environment/SM_Env_Grunge_03", "Environment/SM_Env_Moss_01", "Environment/SM_Env_Moss_03"]
	for i in 26:
		var p := Vector3(rng.randf_range(-18, 18), 0.01 + 0.001 * i, rng.randf_range(-3, 18))
		var k: float = rng.randf_range(0.25, 0.4) if i % flat.size() < 2 else rng.randf_range(0.5, 0.9)
		_drop(s, flat[i % flat.size()], p, rng.randf_range(0, 360), Vector3.ONE * k)
	for i in 10:
		var p := Vector3(rng.randf_range(-18, 18), 0.0, rng.randf_range(-18, 18))
		_drop(s, "Environment/SM_Env_Plant_Grass_Patch_01", p, rng.randf_range(0, 360), Vector3.ONE * 0.6)
	# кости и черепа у стен — ниже щиколотки
	for p in [Vector3(-2.5, 0, -8.6), Vector3(2.0, 0, 8.6), Vector3(-15.6, 0, 1.5), Vector3(11.5, 0, 7.6), Vector3(-6.0, 0, 13.4)]:
		_drop(s, "Props/SM_Prop_Bone_Pile_02", p, rng.randf_range(0, 360), Vector3.ONE * 0.8)
	for p in [Vector3(3.5, 0, -8.7), Vector3(-11.6, 0, -6.4), Vector3(15.6, 0, -2.0)]:
		_drop(s, "Props/SM_Prop_Skull_Pile_02", p, rng.randf_range(0, 360), Vector3.ONE * 0.7)


# ---------------------------------------------------------------- фон за оградой

func _build_background(bg: Node3D) -> void:
	# север: остатки собора — стены с окнами-розетками, контрфорсы, арки
	_drop(bg, "Building/SM_Bld_Wall_Window_Huge_02", Vector3(0, 0, -27), 0.0)
	_drop(bg, "Building/SM_Bld_Wall_Window_Huge_01", Vector3(-7.5, 0, -26.5), 0.0)
	_drop(bg, "Building/SM_Bld_Wall_Window_Huge_Cropped_01", Vector3(7.5, 0, -26.5), 0.0)
	for x in [-10.5, -4.0, 4.0, 10.5]:
		_drop(bg, "Building/SM_Bld_Pillar_05", Vector3(x, 0, -26.8), 0.0, Vector3(1.2, 1.4, 1.2))
	_drop(bg, "Building/SM_Bld_Ruin_02", Vector3(-17, 0, -25), 30.0, Vector3.ONE * 1.3)
	_drop(bg, "Building/SM_Bld_Ruin_01", Vector3(17, 0, -25), -20.0, Vector3.ONE * 1.3)
	_drop(bg, "Building/SM_Bld_Ruin_05", Vector3(-25, 0, -22), 70.0)
	_drop(bg, "Building/SM_Bld_Buttress_Pillar_03", Vector3(-13.5, 0, -24), 0.0, Vector3(1.3, 2.0, 1.3))
	_drop(bg, "Building/SM_Bld_Buttress_Pillar_03", Vector3(13.5, 0, -24), 0.0, Vector3(1.3, 2.0, 1.3))
	# склепы по бокам
	_drop(bg, "Props/SM_Prop_Crypt_01", Vector3(-33, 0, 6), 90.0)
	_drop(bg, "Props/SM_Prop_Crypt_02", Vector3(33, 0, -6), -90.0)
	_drop(bg, "Props/SM_Prop_Crypt_02", Vector3(-33, 0, -10), 90.0)
	# ряды надгробий за оградой
	var rng := RandomNumberGenerator.new()
	rng.seed = 7
	var stones := ["Props/SM_Prop_Tomb_Stone_01", "Props/SM_Prop_Tomb_Stone_02", "Props/SM_Prop_Tomb_Stone_03",
		"Props/SM_Prop_Tomb_Stone_04", "Props/SM_Prop_Tomb_Stone_Basic_01", "Props/SM_Prop_Tomb_Stone_Basic_02"]
	for row in 3:
		for i in 12:
			var z := -16.0 + i * 3.2 + rng.randf_range(-0.4, 0.4)
			var tilt := rng.randf_range(-12, 12)
			_drop(bg, stones[rng.randi() % stones.size()], Vector3(22.5 + row * 2.6, 0, z), -90.0 + tilt)
			if z < 18.0:
				_drop(bg, stones[rng.randi() % stones.size()], Vector3(-22.5 - row * 2.6, 0, z + 1.0), 90.0 + tilt)
		for i in 10:
			var x := -16.0 + i * 3.6 + rng.randf_range(-0.4, 0.4)
			if abs(x) < 3.0:
				continue
			_drop(bg, stones[rng.randi() % stones.size()], Vector3(x, 0, 22.5 + row * 2.6), 180.0 + rng.randf_range(-12, 12))
	# мёртвые деревья и камни
	var trees := [[Vector3(-25, 0, 23), 0.0, 1.0], [Vector3(25.5, 0, 23.5), 120.0, 0.8], [Vector3(-31, 0, -2), 60.0, 0.9],
		[Vector3(31, 0, 9), 200.0, 1.0], [Vector3(9, 0, 30), 300.0, 0.9], [Vector3(-12, 0, 29), 30.0, 1.1],
		[Vector3(-22, 0, -31), 150.0, 1.0], [Vector3(24, 0, -30), 80.0, 0.8]]
	for t in trees:
		var kind := "Environment/SM_Env_Tree_Dead_04" if t[2] > 0.95 else "Environment/SM_Env_Tree_Dead_02"
		_drop(bg, kind, t[0], t[1], Vector3.ONE * t[2])
	_drop(bg, "Environment/SM_Env_Tree_Dead_03", Vector3(-28, 0, 26), 10.0)
	_drop(bg, "Environment/SM_Env_Tree_Dead_01", Vector3(21.5, 0, 21.5), 45.0)
	for r in [[Vector3(-34, 0, 14), 0.0, 1.0], [Vector3(35, 0, -18), 90.0, 1.2], [Vector3(18, 0, 34), 40.0, 1.0],
			[Vector3(-30, 0, -24), 200.0, 1.3], [Vector3(30, 0, 28), 300.0, 0.9]]:
		_drop(bg, "Environment/SM_Env_Rock_Cliff_04", r[0], r[1], Vector3.ONE * r[2])
	# виселица у ворот
	_drop(bg, "Props/SM_Prop_Gibbet_01", Vector3(12, 0, 31), 200.0)
	# холмы и скалы по горизонту
	for i in 10:
		var a := TAU * i / 10.0 + 0.2
		_drop(bg, "Environment/SM_Env_BackgroundHill_01" if i % 2 == 0 else "Environment/SM_Env_BackgroundHill_02",
			Vector3(cos(a) * 62.0, -0.5, sin(a) * 62.0), rad_to_deg(a), Vector3(1.6, 1.4, 1.6))
	_drop(bg, "Environment/SM_Env_Cliff_Basalt_01", Vector3(-30, -7.0, -48), 15.0)
	_drop(bg, "Environment/SM_Env_Cliff_Basalt_03", Vector3(34, -7.0, -46), -20.0)
	# луна над руинами собора
	var moon := _add(bg, "Environment/SM_Env_Moon_02", Transform3D(Basis(Vector3.RIGHT, deg_to_rad(90)).scaled(Vector3.ONE * 2.2), Vector3(-18, 42, -95)))
	_no_shadows(moon)


# ---------------------------------------------------------------- тестовая сцена

func _build_test_level() -> void:
	# пол: та же земля, что на арене
	var f := _group("Floor")
	var ground_mat := StandardMaterial3D.new()
	ground_mat.albedo_texture = load("res://assets/Synty/PolygonDarkFantasy/Textures/Misc/PolygonDarkFantasy_Stone_01.png")
	ground_mat.albedo_color = Color(0.5, 0.47, 0.38)
	ground_mat.roughness = 0.95
	ground_mat.uv1_scale = Vector3(8, 8, 1)
	_plane(f, "Ground", Vector3(0, 0.003, 0), Vector2(40, 40), ground_mat)
	# коробки TestLevel.tscn: Block1..3 — 2 × 2 × 2, Wall1/Wall2 — 8 × 3 × 0.5
	var c := _group("Cover")
	_tomb(c, "Block1", Vector3(3, 0, -3), 0.0)
	_statue(c, "Block2", Vector3(-5, 0, -6), 30.0, "Props/SM_Prop_Statue_People_03")
	_crates(c, "Block3", Vector3(7, 0, 5), 0.0)
	_wall(c, "Wall1", Vector3(0, 0, 8), 0.0, 8.0, 3.0)
	_wall(c, "Wall2", Vector3(-10, 0, 0), 90.0, 8.0, 3.0)
	# жаровня под OmniLight «Torch» (-4, 2.5, 3)
	var t := _group("Torch", null, Vector3(-4, 0, 3))
	_fit(t, "Base/SM_Bld_Base_Pillar_02", Vector3(0, 0.95, 0), Vector3(0.3, 1.9, 0.3))
	_no_shadows(_drop(t, "Props/SM_Prop_Brazier_01", Vector3(0, 1.9, 0), 0.0, Vector3.ONE * 0.6))
	_flame(t, Vector3(0, 2.38, 0))
	# плоская мелочь на полу
	var s := _group("Scatter")
	var rng := RandomNumberGenerator.new()
	rng.seed = 4
	var flat := ["Environment/SM_Env_Grunge_01", "Environment/SM_Env_Grunge_03", "Environment/SM_Env_Moss_01", "Environment/SM_Env_Moss_03"]
	for i in 14:
		var p := Vector3(rng.randf_range(-18, 18), 0.01 + 0.001 * i, rng.randf_range(-18, 18))
		_drop(s, flat[i % flat.size()], p, rng.randf_range(0, 360), Vector3.ONE * rng.randf_range(0.5, 0.9))
	for i in 8:
		var p := Vector3(rng.randf_range(-18, 18), 0.0, rng.randf_range(-18, 18))
		_drop(s, "Environment/SM_Env_Plant_Grass_Patch_01", p, rng.randf_range(0, 360), Vector3.ONE * 0.6)
	# фон за краем пола: площадка стоит на возвышении, вокруг кладбище на 2.5 м ниже (обрыв остаётся заметным)
	var bg := _group("Background", null, Vector3(0, -2.5, 0))
	var outer_mat := ground_mat.duplicate() as StandardMaterial3D
	outer_mat.albedo_color = Color(0.32, 0.31, 0.27)
	outer_mat.uv1_scale = Vector3(30, 30, 1)
	_plane(bg, "Outer", Vector3.ZERO, Vector2(160, 160), outer_mat)
	_fit(bg, "Environment/SM_Env_Rock_Cliff_06", Vector3(0, 1.25, -20.9), Vector3(42, 2.6, 1.6))
	_fit(bg, "Environment/SM_Env_Rock_Cliff_06", Vector3(0, 1.25, 20.9), Vector3(42, 2.6, 1.6), 180.0)
	_fit(bg, "Environment/SM_Env_Rock_Cliff_06", Vector3(20.9, 1.25, 0), Vector3(42, 2.6, 1.6), -90.0)
	_fit(bg, "Environment/SM_Env_Rock_Cliff_06", Vector3(-20.9, 1.25, 0), Vector3(42, 2.6, 1.6), 90.0)
	var stones := ["Props/SM_Prop_Tomb_Stone_01", "Props/SM_Prop_Tomb_Stone_02", "Props/SM_Prop_Tomb_Stone_04", "Props/SM_Prop_Tomb_Stone_Basic_01"]
	for i in 28:
		var a := TAU * i / 28.0
		var r := rng.randf_range(23.0, 27.0)
		_drop(bg, stones[i % stones.size()], Vector3(cos(a) * r * 1.05, 0, sin(a) * r * 1.05), rad_to_deg(-a) - 90.0 + rng.randf_range(-15, 15))
	for tr in [[Vector3(-26, 0, -24), 0.0], [Vector3(27, 0, -20), 90.0], [Vector3(-28, 0, 22), 200.0], [Vector3(25, 0, 26), 300.0]]:
		_drop(bg, "Environment/SM_Env_Tree_Dead_04", tr[0], tr[1])
	_drop(bg, "Building/SM_Bld_Wall_Window_Huge_02", Vector3(0, 0, -30), 0.0)
	_drop(bg, "Building/SM_Bld_Ruin_02", Vector3(-12, 0, -29), 20.0, Vector3.ONE * 1.3)
	_drop(bg, "Building/SM_Bld_Ruin_01", Vector3(12, 0, -29), -20.0, Vector3.ONE * 1.3)
	_drop(bg, "Props/SM_Prop_Crypt_01", Vector3(30, 0, 4), -90.0)


# ---------------------------------------------------------------- вспомогательное

func _group(name: String, parent: Node3D = null, pos := Vector3.ZERO, yaw := 0.0) -> Node3D:
	var g := Node3D.new()
	g.name = name
	g.transform = Transform3D(Basis(Vector3.UP, deg_to_rad(yaw)), pos)
	(parent if parent != null else _root).add_child(g)
	g.owner = _root
	return g


func _aabb(prefab: String) -> AABB:
	if not _aabbs.has(prefab):
		var inst := (load(PREFABS + prefab + ".tscn") as PackedScene).instantiate()
		var acc := [null]
		_collect(inst, Transform3D.IDENTITY, acc)
		inst.free()
		_aabbs[prefab] = acc[0] if acc[0] != null else AABB()
	return _aabbs[prefab]


func _collect(n: Node, xf: Transform3D, acc: Array) -> void:
	var t := xf
	if n is Node3D:
		t = xf * (n as Node3D).transform
	if n is MeshInstance3D and (n as MeshInstance3D).mesh != null:
		var a: AABB = t * (n as MeshInstance3D).get_aabb()
		acc[0] = a if acc[0] == null else (acc[0] as AABB).merge(a)
	for c in n.get_children():
		_collect(c, t, acc)


func _add(parent: Node3D, prefab: String, xf: Transform3D) -> Node3D:
	var inst := (load(PREFABS + prefab + ".tscn") as PackedScene).instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE) as Node3D
	var base := prefab.get_file().trim_prefix("SM_")
	_counter[base] = _counter.get(base, 0) + 1
	inst.name = "%s_%d" % [base, _counter[base]]
	inst.transform = xf
	parent.add_child(inst)
	inst.owner = _root
	return inst


## Вписать AABB видимой геометрии префаба в коробку size с центром center (в координатах parent),
## с поворотом yaw вокруг вертикали. Масштаб неравномерный.
func _fit(parent: Node3D, prefab: String, center: Vector3, size: Vector3, yaw := 0.0) -> Node3D:
	var a := _aabb(prefab)
	var s := Vector3(
		size.x / a.size.x if a.size.x > 0.001 else 1.0,
		size.y / a.size.y if a.size.y > 0.001 else 1.0,
		size.z / a.size.z if a.size.z > 0.001 else 1.0)
	var basis := Basis(Vector3.UP, deg_to_rad(yaw)) * Basis.from_scale(s)
	return _add(parent, prefab, Transform3D(basis, center - basis * a.get_center()))


## Поставить префаб так, чтобы низ его AABB стоял в точке pos (по центру основания).
func _drop(parent: Node3D, prefab: String, pos: Vector3, yaw := 0.0, scale := Vector3.ONE) -> Node3D:
	var a := _aabb(prefab)
	var c := a.get_center()
	c.y = a.position.y
	var basis := Basis(Vector3.UP, deg_to_rad(yaw)) * Basis.from_scale(scale)
	return _add(parent, prefab, Transform3D(basis, pos - basis * c))


func _plane(parent: Node3D, name: String, pos: Vector3, size: Vector2, mat: Material) -> MeshInstance3D:
	var m := MeshInstance3D.new()
	m.name = name
	var pm := PlaneMesh.new()
	pm.size = size
	pm.material = mat
	m.mesh = pm
	m.position = pos
	parent.add_child(m)
	m.owner = _root
	return m


func _no_shadows(n: Node) -> void:
	if n is GeometryInstance3D:
		(n as GeometryInstance3D).cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	for c in n.get_children():
		_no_shadows(c)


func _total() -> int:
	var t := 0
	for k in _counter:
		t += _counter[k]
	return t


## Светящийся «огонь» в жаровне: только картинка (эмиссия без источника света; свет — Torch* в Arena.tscn).
var _flame_mat: StandardMaterial3D

func _flame(parent: Node3D, pos: Vector3) -> void:
	if _flame_mat == null:
		_flame_mat = StandardMaterial3D.new()
		_flame_mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
		_flame_mat.albedo_color = Color(1.0, 0.55, 0.18)
		_flame_mat.emission_enabled = true
		_flame_mat.emission = Color(1.0, 0.45, 0.1)
		_flame_mat.emission_energy_multiplier = 3.0
	var m := MeshInstance3D.new()
	m.name = "Flame"
	var sphere := SphereMesh.new()
	sphere.radius = 0.13
	sphere.height = 0.36
	sphere.radial_segments = 8
	sphere.rings = 4
	sphere.material = _flame_mat
	m.mesh = sphere
	m.position = pos
	m.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	parent.add_child(m)
	m.owner = _root
