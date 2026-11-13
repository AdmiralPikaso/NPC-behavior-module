using System.Text.RegularExpressions;
using Godot;

namespace Zero.Common.Visual;

/// <summary>
/// Внешний вид персонажа (только картинка, на ИИ, физику и бой не влияет).
/// Висит на корне модели DarkFantasyCharacters.glb: там на одном скелете с анимациями лежат
/// все скины, меши названы "&lt;Скин&gt;__&lt;меш&gt;" (см. tools/blender/build_character.py).
/// Скрипт показывает меши выбранного скина, ставит им материал пакета Synty и, если нужно,
/// вкладывает в правую руку (кость Prop_R) оружие — тоже только для вида, без коллизий.
/// </summary>
[Tool, GlobalClass]
public partial class CharacterSkin : Node3D
{
    public enum SkinKind
    {
        /// <summary>Скелет, вариант по номеру NPC (Enemy1, Enemy2… или порядку в сцене).</summary>
        AutoSkeleton,
        Mannequin,
        Hunter,
        Skeleton,
        SkeletonLightArmor,
        SkeletonHeavyArmor,
        SkeletonRanger,
    }

    public enum WeaponKind
    {
        /// <summary>Своё для каждого скина: охотнику посох, скелетам меч/топор/булава/арбалет.</summary>
        Auto,
        None,
        Staff,
        Crossbow,
        Sword,
        Axe,
        Mace,
    }

    private static readonly SkinKind[] SkeletonVariants =
    {
        SkinKind.Skeleton, SkinKind.SkeletonLightArmor, SkinKind.SkeletonHeavyArmor, SkinKind.SkeletonRanger,
    };

    private const string SyntyRoot = "res://assets/Synty/PolygonDarkFantasy/";
    private const string MaterialPath = SyntyRoot + "Materials/Alts/PolygonDarkFantasy_01_A_mat.tres";
    private const string WeaponNodeName = "SkinWeapon";

    private SkinKind _skin = SkinKind.AutoSkeleton;
    private WeaponKind _weapon = WeaponKind.Auto;

    [Export]
    public SkinKind Skin
    {
        get => _skin;
        set { _skin = value; if (IsInsideTree()) Apply(); }
    }

    [Export]
    public WeaponKind Weapon
    {
        get => _weapon;
        set { _weapon = value; if (IsInsideTree()) Apply(); }
    }

    /// <summary>Скин после разрешения AutoSkeleton.</summary>
    public SkinKind ResolvedSkin { get; private set; }

    public override void _Ready() => Apply();

    private void Apply()
    {
        var skeleton = FindSkeleton(this);
        if (skeleton is null)
            return;
        ResolvedSkin = _skin == SkinKind.AutoSkeleton ? SkeletonVariants[ActorIndex() % SkeletonVariants.Length] : _skin;
        var prefix = ResolvedSkin + "__";
        var material = ResourceLoader.Exists(MaterialPath) ? GD.Load<Material>(MaterialPath) : null;
        foreach (var child in skeleton.GetChildren())
        {
            if (child is not MeshInstance3D mesh || child.Name == WeaponNodeName)
                continue;
            var name = mesh.Name.ToString();
            mesh.Visible = name.StartsWith(prefix);
            // манекену оставить его собственный материал из GLB
            if (mesh.Visible && !name.StartsWith("Mannequin__"))
                mesh.MaterialOverride = material;
        }
        ApplyWeapon(skeleton, material);
    }

    private void ApplyWeapon(Skeleton3D skeleton, Material? material)
    {
        skeleton.GetNodeOrNull(WeaponNodeName)?.Free();
        var weapon = _weapon != WeaponKind.Auto ? _weapon : ResolvedSkin switch
        {
            SkinKind.Hunter => WeaponKind.Staff,
            SkinKind.Skeleton => WeaponKind.Sword,
            SkinKind.SkeletonLightArmor => WeaponKind.Axe,
            SkinKind.SkeletonHeavyArmor => WeaponKind.Mace,
            SkinKind.SkeletonRanger => WeaponKind.Crossbow,
            _ => WeaponKind.None,
        };
        if (weapon == WeaponKind.None || skeleton.FindBone("Prop_R") < 0)
            return;
        var (file, grip) = WeaponMesh(weapon);
        var path = SyntyRoot + "Models/extracted/" + file + ".res";
        if (!ResourceLoader.Exists(path))
            return;
        var attachment = new BoneAttachment3D { Name = WeaponNodeName, BoneName = "Prop_R" };
        // узел не сохраняется в сцену (нет Owner): создаётся заново при каждом запуске
        skeleton.AddChild(attachment);
        // скелет в GLB масштабирован (кости в сантиметрах, скелет × 0.01): вернуть метры
        var scale = 1f / (GlobalTransform.AffineInverse() * skeleton.GlobalTransform).Basis.Scale.X;
        attachment.AddChild(new MeshInstance3D
        {
            Mesh = GD.Load<Mesh>(path),
            MaterialOverride = material,
            Transform = new Transform3D(grip.Basis.Scaled(Vector3.One * scale), grip.Origin * scale),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        });
    }

    /// <summary>
    /// Меш оружия и его положение относительно кости Prop_R (подобрано по кадрам). Оси кости,
    /// когда играют клипы (Idle и др.): X — вверх вдоль опущенной руки, Y — наружу, вправо,
    /// Z — вперёд. В позе покоя GLB кисть повёрнута иначе, поэтому смотреть надо по клипам.
    /// Посох стоит вертикально, клинки смотрят вперёд с наклоном 10° вниз (сильнее — втыкаются
    /// в пол в приседе), арбалет ложем вперёд.
    /// </summary>
    private static (string File, Transform3D Grip) WeaponMesh(WeaponKind weapon)
    {
        var tilt = Mathf.DegToRad(10f);
        var shaft = new Vector3(-Mathf.Sin(tilt), 0f, Mathf.Cos(tilt)); // ось клинка: вперёд и чуть вниз
        var wide = new Vector3(Mathf.Cos(tilt), 0f, Mathf.Sin(tilt));   // широкая сторона — вертикально
        // меш: Y — рукоять/клинок, Z — широкая сторона (гарда, лезвие топора)
        var blade = new Basis(shaft.Cross(wide), shaft, wide);
        return weapon switch
        {
            WeaponKind.Staff => ("SM_Wep_Staff_01",
                new Transform3D(new Basis(Vector3.Back, Vector3.Right, Vector3.Up), Vector3.Right * 0.6f)),
            WeaponKind.Crossbow => ("SM_Wep_Crossbow_01",
                new Transform3D(new Basis(Vector3.Down, Vector3.Right, Vector3.Back), Vector3.Zero)),
            WeaponKind.Sword => ("SM_Wep_Sword_03", new Transform3D(blade, Vector3.Zero)),
            WeaponKind.Axe => ("SM_Wep_Axe_02", new Transform3D(blade, Vector3.Zero)),
            _ => ("SM_Wep_Mace_01", new Transform3D(blade, Vector3.Zero)),
        };
    }

    private static Skeleton3D? FindSkeleton(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Skeleton3D s)
                return s;
            if (FindSkeleton(child) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Номер персонажа: цифры в конце имени актёра (Enemy3 → 2), иначе порядок в сцене.</summary>
    private int ActorIndex()
    {
        // Model → Visual → актёр
        var actor = GetParent()?.GetParent();
        if (actor is null)
            return 0;
        var m = Regex.Match(actor.Name.ToString(), @"(\d+)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            return Mathf.Max(0, n - 1);
        return actor.GetIndex();
    }
}
