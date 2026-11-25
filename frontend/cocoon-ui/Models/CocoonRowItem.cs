namespace CocoonUI.Models;

/// <summary>Unified row interaction kind across full-screen pages, modal sheets, and drawers.</summary>
public enum CocoonRowKind
{
    Toggle,
    Dropdown,
    Slider,
    Stepper,
    Nav,
    Field,
    Order,
    Info,
}

/// <summary>Platform-agnostic declarative row item consumed by CocoonUI renderers and templates.</summary>
public sealed class CocoonRowItem
{
    public required string Id { get; init; }
    public CocoonRowKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; set; }
    public string? Icon { get; set; }
    public float Indent { get; set; }
    public bool Disabled { get; set; }
    public bool Changed { get; set; }
    public bool Danger { get; set; }
    public bool GroupBreak { get; set; }

    // Toggle
    public bool Checked { get; set; }
    public Action<bool>? OnToggle { get; set; }

    // Dropdown (single & multi-select)
    public IReadOnlyList<string>? Options { get; set; }
    public int SelectedIndex { get; set; } = -1;
    public Action<int>? OnSelect { get; set; }
    public bool MultiSelect { get; set; }
    public Func<int, bool>? IsOptionChecked { get; set; }
    public Action<int>? OnToggleOption { get; set; }

    // Slider / Stepper
    public string? ValueText { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
    public int Step { get; set; } = 1;
    public int Value { get; set; }
    public Action<int>? OnValueChanged { get; set; }

    // Nav / Field
    public Action? OnPress { get; set; }
    public Action<string>? OnCommitText { get; set; }
    public bool Multiline { get; set; }

    // Order
    public bool CanMoveUp { get; set; }
    public bool CanMoveDown { get; set; }
    public Action<int>? OnMoveOrder { get; set; }
}

/// <summary>Category descriptor for TwoColumnPage left-hand navigation.</summary>
public sealed record CocoonCategoryItem(string Id, string Name, string? Subtitle = null);
