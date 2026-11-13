using Godot;
using Zero.Actors;

namespace Zero.Arena;

/// <summary>
/// Эффекты ручной игры на арене (в headless-прогонах не создаётся):
/// кольцо перезарядки вокруг прицела с остатком магазина и затемнение экрана при низком здоровье
/// (как в Uncharted: картинка теряет цвет, края темнеют, ниже 30% — лёгкая пульсация).
/// Слои: эффект — <see cref="EffectLayer"/>, тексты арены и игрока — выше (<see cref="HudLayer"/>).
/// </summary>
public partial class PlayOverlay : Node
{
    public const int EffectLayer = 2;
    public const int HudLayer = 5;

    /// <summary>Ниже этой доли здоровья картинка начинает терять цвет.</summary>
    private const float FadeFrom = 0.6f;
    /// <summary>К этой доле здоровья картинка почти чёрно-белая с тёмными краями.</summary>
    private const float FadeFull = 0.15f;
    private const float PulseBelow = 0.3f;

    private const string ShaderCode = @"
shader_type canvas_item;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform float amount : hint_range(0.0, 1.0) = 0.0;
uniform float pulse : hint_range(0.0, 1.0) = 0.0;
void fragment() {
    vec3 c = texture(screen_tex, SCREEN_UV).rgb;
    float g = dot(c, vec3(0.299, 0.587, 0.114));
    vec3 col = mix(c, vec3(g), amount * 0.92);
    float r = length((SCREEN_UV - 0.5) * vec2(1.25, 1.0));
    float vignette = smoothstep(0.3, 0.85, r);
    col *= 1.0 - vignette * amount * (0.8 + 0.2 * pulse);
    COLOR = vec4(col, 1.0);
}";

    private readonly ActorBody _player;
    private ColorRect _fade = null!;
    private ShaderMaterial _material = null!;
    private ReloadRing _ring = null!;
    private float _amount;
    private float _time;

    public PlayOverlay(ActorBody player) => _player = player;

    // Godot создаёт узлы скриптов и без аргументов (например, при перезагрузке сборки в редакторе)
    public PlayOverlay() => _player = null!;

    public override void _Ready()
    {
        var effect = new CanvasLayer { Name = "LowHealth", Layer = EffectLayer };
        AddChild(effect);
        _material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        _fade = new ColorRect
        {
            Material = _material, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
        };
        _fade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        effect.AddChild(_fade);

        var hud = new CanvasLayer { Name = "ReloadRing", Layer = HudLayer };
        AddChild(hud);
        _ring = new ReloadRing(_player);
        _ring.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        hud.AddChild(_ring);
    }

    public override void _Process(double delta)
    {
        if (_player is null)
            return;
        var dt = (float)delta;
        _time += dt;
        var hp = _player.IsDead ? 0f : _player.Health / Mathf.Max(1f, _player.MaxHealth);
        var target = Mathf.Clamp((FadeFrom - hp) / (FadeFrom - FadeFull), 0f, 1f);
        // плавно: темнеет быстрее, чем возвращается цвет
        var rate = target > _amount ? 3f : 1.2f;
        _amount = Mathf.MoveToward(_amount, target, rate * dt);
        var pulse = hp < PulseBelow && !_player.IsDead ? 0.5f + 0.5f * Mathf.Sin(_time * 5f) : 0f;
        _material.SetShaderParameter("amount", _amount);
        _material.SetShaderParameter("pulse", pulse);
        _fade.Visible = _amount > 0.005f;
        _ring.QueueRedraw();
    }

    /// <summary>Кольцо перезарядки вокруг центра экрана и остаток магазина «5/8» рядом с прицелом.</summary>
    private partial class ReloadRing : Control
    {
        private readonly ActorBody _player;

        public ReloadRing(ActorBody player)
        {
            _player = player;
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public ReloadRing() => _player = null!;

        public override void _Draw()
        {
            if (_player is null || _player.IsDead || _player.MagazineSize <= 0)
                return;
            var center = Size / 2f;
            const float radius = 22f;
            if (_player.IsReloading)
            {
                var done = 1f - _player.ReloadLeft / Mathf.Max(0.01f, _player.ReloadTime);
                DrawArc(center, radius, 0f, Mathf.Tau, 48, new Color(0f, 0f, 0f, 0.45f), 4f, true);
                DrawArc(center, radius, -Mathf.Pi / 2f, -Mathf.Pi / 2f + Mathf.Tau * done, 48,
                    new Color(1f, 0.9f, 0.6f, 0.95f), 3f, true);
            }
            var empty = _player.Ammo <= 0;
            var text = $"{_player.Ammo}/{_player.MagazineSize}";
            var color = empty ? new Color(1f, 0.25f, 0.2f) : new Color(1f, 1f, 1f, 0.8f);
            var font = ThemeDB.FallbackFont;
            var pos = center + new Vector2(radius + 8f, radius + 6f);
            DrawString(font, pos + new Vector2(1f, 1f), text, HorizontalAlignment.Left, -1, 14, new Color(0f, 0f, 0f, 0.7f));
            DrawString(font, pos, text, HorizontalAlignment.Left, -1, 14, color);
        }
    }
}
