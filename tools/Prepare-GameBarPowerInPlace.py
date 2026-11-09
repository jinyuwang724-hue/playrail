"""Replace the two app switches with a native, visibility-checked overlay dismissal."""
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/gamebar-power-in-place'
OLD = ROOT / 'artifacts/gamebar-power-handoff'
installed = Path('C:/Program Files/WindowsApps/ClawToolbox.GameBarWidget_0.4.43.0_x64__ba3r68zr466za/ClawWidget.exe')
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
assert sha(installed) == sha(OLD / 'bin/ClawWidget/ClawWidget.exe')
stage = OUT / 'src/native/GameBarWidget'
stage.mkdir(parents=True, exist_ok=False)
for file in (OLD / 'src/native/GameBarWidget').iterdir():
    if file.is_file(): shutil.copy2(file, stage / file.name)
shutil.copytree(OLD / 'src/native/GameBarWidget/Assets', stage / 'Assets')
manifest = stage / 'Package.appxmanifest'
manifest.write_text(manifest.read_text(encoding='utf-8-sig').replace('Version="0.4.43.0"', 'Version="0.4.44.0"', 1), encoding='utf-8')

replacement = r'''    // No protocol activation or foreground change: the native renderer dismisses Game Bar
    // immediately before its frames, including physical power-button and wake paths.
    fire_and_forget WidgetPage::RequestAnimatedPower(hstring action, bool preview)
    {
        auto lifetime = get_strong();
        if (m_powerTransition) co_return;
        m_powerTransition = true;
        PowerMenu().Hide();
        PublishPowerVisibility();
        if (preview) Send(L"power-preview", L"preview_power_animation");
        else Send(L"power_action", L"power_action", Args({ S(action.c_str()) }));
        auto ui = apartment_context();
        co_await resume_after(std::chrono::seconds(2));
        co_await ui;
        m_powerTransition = false;
        Update();
    }

    fire_and_forget WidgetPage::PublishPowerVisibility()
    {
        auto lifetime = get_strong();
        m_powerVisibilityDirty = true;
        if (m_powerVisibilityWriting) co_return;
        m_powerVisibilityWriting = true;
        try
        {
            while (m_powerVisibilityDirty)
            {
                m_powerVisibilityDirty = false;
                JsonObject report;
                report.Insert(L"version", N(1));
                report.Insert(L"pid", N(GetCurrentProcessId()));
                bool foreground = false, visible = false;
                if (m_widget)
                {
                    foreground = m_widget.GameBarDisplayMode() == XboxGameBarDisplayMode::Foreground;
                    visible = m_widget.Visible();
                }
                report.Insert(L"foreground", B(foreground));
                report.Insert(L"visible", B(visible));
                auto snapshot = report.Stringify();
                if (snapshot == m_lastPowerVisibility) continue;
                report.Insert(L"atUnixMs", N(static_cast<double>(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::system_clock::now().time_since_epoch()).count())));
                auto file = co_await ApplicationData::Current().LocalFolder().CreateFileAsync(
                    L"power-visibility.next", CreationCollisionOption::ReplaceExisting);
                co_await FileIO::WriteTextAsync(file, report.Stringify());
                co_await file.RenameAsync(L"power-visibility.json", NameCollisionOption::ReplaceExisting);
                m_lastPowerVisibility = snapshot;
            }
        }
        catch (...) {}
        m_powerVisibilityWriting = false;
    }

'''
for folder in (ROOT / 'native/GameBarWidget', stage):
    cpp = folder / 'WidgetPage.cpp'
    text = cpp.read_text(encoding='utf-8-sig')
    begin = text.index('    // The Game Bar host launches the library')
    end = text.index('    void WidgetPage::OnNavigatedTo(', begin)
    text = text[:begin] + replacement + text[end:]
    text = text.replace('        m_pollTimer.Tick([this](auto&&, auto&&) {', '        m_pollTimer.Tick([this](auto&&, auto&&) {\n            PublishPowerVisibility();', 1)
    text = text.replace('        if (!m_widget) return;\n', '''        if (!m_widget) return;
        PublishPowerVisibility();
        auto visibilityChanged = [weak = get_weak()](auto&&, auto&&) {
            if (auto self = weak.get()) self->Dispatcher().RunAsync(Core::CoreDispatcherPriority::Normal,
                [self]() { self->PublishPowerVisibility(); });
        };
        m_widget.GameBarDisplayModeChanged(visibilityChanged);
        m_widget.VisibleChanged(visibilityChanged);
        m_widget.WindowStateChanged(visibilityChanged);
''', 1)
    if folder == stage:
        text = text.replace('m_sending.clear(); m_target.reset(); m_widget = nullptr;', 'm_sending.clear(); m_target.reset(); m_widget = nullptr; PublishPowerVisibility();', 1)
    cpp.write_text(text, encoding='utf-8')
    header = folder / 'WidgetPage.h'
    text = header.read_text(encoding='utf-8-sig').replace('        bool m_powerTransition = false;', '''        bool m_powerTransition = false;
        winrt::fire_and_forget PublishPowerVisibility();
        bool m_powerVisibilityWriting = false, m_powerVisibilityDirty = false;
        hstring m_lastPowerVisibility;''', 1)
    header.write_text(text, encoding='utf-8')

build = (ROOT / 'tools/Build-GameBarPowerHandoff.ps1').read_text(encoding='utf-8-sig')
build = build.replace('gamebar-power-handoff', 'gamebar-power-in-place').replace('0.4.43.0', '0.4.44.0')
(ROOT / 'tools/Build-GameBarPowerInPlace.ps1').write_text(build, encoding='utf-8')
(OUT / 'base.json').write_text(json.dumps({'version': '0.4.43.0', 'exeSha256': sha(installed)}, indent=2), encoding='utf-8')
print('Prepared 0.4.44 with no library launch; native dismissal covers button, menu and wake')
