namespace ClawLibrary.Engine;

/// <summary>确认只切换调整状态；编辑权只属于当前页面、当前行，刷新同一行时保留。</summary>
internal sealed class SettingsEditSession
{
    internal readonly record struct Key(string Page, int Row, string Title);
    private Key? _editing;

    public bool IsEditing(Key key) => _editing == key;
    public void Toggle(Key key) => _editing = IsEditing(key) ? null : key;
    public void Observe(Key? focus) { if (_editing != focus) _editing = null; }
    public bool End()
    {
        bool active = _editing != null;
        _editing = null;
        return active;
    }
}
