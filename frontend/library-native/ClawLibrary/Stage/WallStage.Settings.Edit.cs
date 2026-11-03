using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private readonly SettingsEditSession _settingsEdit = new();
    private bool SettingsPickerRows => (_gpPicker?.Level is GpSubLevel.Key or GpSubLevel.Settings or GpSubLevel.Group) && (_gpOpen || GsPadPicking);
    private string SettingsEditPage => SettingsPickerRows && _gpPicker is { } p
        ? $"pad:{p.Level}:{p.Source}:{p.Act}:{p.Group}:{p.Desktop}"
        : _dsOpen ? "display" : _syOpen ? $"system:{_syUpdatesOpen}" : _perfOpen ? "performance"
        : $"settings:{_gsItem?.Id}:{_stCat}:{_stBrowse}:{_lgPage}:{_mgStage}:{_mgAdding}:{_mgAddGroup}";

    private SettingsEditSession.Key SettingsEditKey(PerfRow row, int? index = null) => new(SettingsEditPage, row.EditId != null ? -1 : index ?? SettingsFocus, row.EditId ?? row.Title);
    private bool SettingsRowEditing(PerfRow row, int? index = null) => row.Numeric && !row.Disabled && !row.Skip && _settingsEdit.IsEditing(SettingsEditKey(row, index));

    private bool SettingsFinishEdit()
    {
        if (!_settingsEdit.End()) return false;
        SettingsPaint();
        return true;
    }
}
