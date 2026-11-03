"""Stage over the source of the exact installed 0.4.42 widget; preserve newer features."""
import ast
import hashlib
import json
import os
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/gamebar-power-handoff'
BASE = Path.home() / '.codex/worktrees/native-gamebar-osd/Claw-Playhub-ButtonRouter-Handoff'
INSTALLED = Path('C:/Program Files/WindowsApps/ClawToolbox.GameBarWidget_0.4.42.0_x64__ba3r68zr466za')
def sha(file): return hashlib.sha256(file.read_bytes()).hexdigest()
assert sha(INSTALLED / 'ClawWidget.exe') == sha(BASE / 'artifacts/gamebar-widget/bin/ClawWidget/ClawWidget.exe')
source = OUT / 'src/native/GameBarWidget'
source.mkdir(parents=True, exist_ok=False)
for item in (BASE / 'native/GameBarWidget').iterdir():
    if item.is_file(): shutil.copy2(item, source / item.name)
shutil.copytree(BASE / 'native/GameBarWidget/Assets', source / 'Assets')
manifest = source / 'Package.appxmanifest'
manifest.write_text(manifest.read_text(encoding='utf-8-sig').replace('Version="0.4.42.0"', 'Version="0.4.43.0"', 1), encoding='utf-8')

helper = r'''    // The Game Bar host launches the library and yields the overlay before desktop animation.
    // Keep the page alive; Close() can suspend the app before its request has been sent.
    fire_and_forget WidgetPage::RequestAnimatedPower(hstring action, bool preview)
    {
        auto lifetime = get_strong();
        if (m_powerTransition) co_return;
        m_powerTransition = true;
        auto ui = apartment_context();
        JsonObject trace;
        trace.Insert(L"action", S(action.c_str()));
        trace.Insert(L"preview", B(preview));
        trace.Insert(L"startedUnixMs", N(static_cast<double>(std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::system_clock::now().time_since_epoch()).count())));
        hstring problem;
        bool ready = false;
        try
        {
            if (!m_widget || !m_agent->Ready()) throw hresult_error(E_FAIL, L"工具箱尚未连接，请稍后重试");
            PowerMenu().Hide();
            trace.Insert(L"phase", S(L"launch-library"));
            bool launched = co_await m_widget.LaunchUriAsync(Uri(L"claw-library://restore-library"));
            trace.Insert(L"libraryLaunched", B(launched));
            if (!launched) throw hresult_error(E_FAIL, L"无法切回游戏库，已取消本次操作");
            co_await m_widget.MinimizeAsync();
            auto deadline = Clock::now() + std::chrono::seconds(2);
            while (m_widget.GameBarDisplayMode() == XboxGameBarDisplayMode::Foreground && Clock::now() < deadline)
            {
                co_await resume_after(std::chrono::milliseconds(50));
                co_await ui;
            }
            bool foreground = m_widget.GameBarDisplayMode() == XboxGameBarDisplayMode::Foreground;
            trace.Insert(L"gameBarForeground", B(foreground));
            if (foreground) throw hresult_error(E_FAIL, L"未能收起 Game Bar，已取消本次操作");
            // Let the host's closing transition finish before starting the first animation frame.
            co_await resume_after(std::chrono::milliseconds(200));
            co_await ui;
            if (m_widget.GameBarDisplayMode() == XboxGameBarDisplayMode::Foreground)
                throw hresult_error(E_FAIL, L"Game Bar 又被打开，已取消本次操作");
            ready = true;
            trace.Insert(L"phase", S(L"ready-to-animate"));
        }
        catch (hresult_error const& ex) { problem = ex.message(); }
        catch (...) { problem = L"无法准备电源动画，请重新打开小组件后重试"; }
        if (!problem.empty()) trace.Insert(L"error", S(problem.c_str()));
        try
        {
            auto file = co_await ApplicationData::Current().LocalFolder().CreateFileAsync(
                L"power-transition.json", CreationCollisionOption::ReplaceExisting);
            co_await FileIO::WriteTextAsync(file, trace.Stringify());
        }
        catch (...) {}
        if (ready)
        {
            if (preview) Send(L"power-preview", L"preview_power_animation");
            else Send(L"power_action", L"power_action", Args({ S(action.c_str()) }));
            co_await resume_after(std::chrono::seconds(2));
            co_await ui;
        }
        else if (!problem.empty()) Flash(problem);
        m_powerTransition = false;
        Update();
    }

'''
for folder in (ROOT / 'native/GameBarWidget', source):
    cpp = folder / 'WidgetPage.cpp'
    text = cpp.read_text(encoding='utf-8-sig')
    assert 'RequestAnimatedPower' not in text
    text = text.replace('ShutdownItem().Click(power(L"shutdown"));', 'ShutdownItem().Click([this](auto&&, auto&&) { RequestAnimatedPower(L"shutdown"); });', 1)
    text = text.replace('RestartItem().Click(power(L"restart"));', 'RestartItem().Click([this](auto&&, auto&&) { RequestAnimatedPower(L"restart"); });\n        PowerPreviewItem().Click([this](auto&&, auto&&) { RequestAnimatedPower(L"restart", true); });', 1)
    text = text.replace('    void WidgetPage::OnNavigatedTo(', helper + '    void WidgetPage::OnNavigatedTo(', 1)
    cpp.write_text(text, encoding='utf-8')
    header = folder / 'WidgetPage.h'
    text = header.read_text(encoding='utf-8-sig').replace('        void BuildRail();', '        winrt::fire_and_forget RequestAnimatedPower(hstring action, bool preview = false);\n        bool m_powerTransition = false;\n        void BuildRail();', 1)
    header.write_text(text, encoding='utf-8')
    xaml = folder / 'WidgetPage.xaml'
    text = xaml.read_text(encoding='utf-8-sig').replace('                            <MenuFlyoutSeparator />', '                            <MenuFlyoutItem x:Name="PowerPreviewItem" Text="动画预览" />\n                            <MenuFlyoutSeparator />', 1)
    xaml.write_text(text, encoding='utf-8')

backend = OUT / 'backend'
backend.mkdir()
home = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox/app'
expected = {}
for name in ('claw_agent.py', 'claw_service.py'):
    before = (home / name).read_text(encoding='utf-8-sig')
    assert 'preview_power_animation' not in before
    if name == 'claw_agent.py':
        after = before.replace('"close_game", "power_action",', '"close_game", "power_action", "preview_power_animation",', 1)
    else:
        reference = (ROOT / 'plugin' / name).read_text(encoding='utf-8-sig')
        node = next(n for n in ast.walk(ast.parse(reference)) if isinstance(n, ast.AsyncFunctionDef) and n.name == 'preview_power_animation')
        method = ''.join(reference.splitlines(True)[node.lineno - 1:node.end_lineno])
        after = before.replace('    async def toggle_keyboard(self):', method + '\n\n    async def toggle_keyboard(self):', 1)
    assert after != before
    compile(after, name, 'exec')
    (backend / name).write_text(after, encoding='utf-8')
    expected[name] = sha(home / name)
(backend / 'expected.json').write_text(json.dumps(expected, indent=2), encoding='utf-8')
(OUT / 'base.json').write_text(json.dumps({'widgetVersion': '0.4.42.0', 'widgetExeSha256': sha(INSTALLED / 'ClawWidget.exe'), 'source': str(BASE / 'native/GameBarWidget')}, indent=2), encoding='utf-8')
print('Prepared 0.4.43 widget with supported host handoff and non-destructive preview endpoint')
