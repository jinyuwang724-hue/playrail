#include "pch.h"
#include "OsdPage.h"
#include "OsdPage.g.cpp"
#include <cmath>
#include <vector>
#include <algorithm>

using namespace winrt;
using namespace Windows::Data::Json;
using namespace Windows::Foundation;
using namespace Windows::UI;
using namespace Windows::UI::Xaml;
using namespace Windows::UI::Xaml::Controls;
using namespace Windows::UI::Xaml::Documents;
using namespace Windows::UI::Xaml::Media;
using namespace Microsoft::Gaming::XboxGameBar;

namespace
{
    JsonObject Object(JsonObject const& data, wchar_t const* name)
    {
        if (!data) return JsonObject();
        auto value = data.GetNamedValue(name, JsonValue::CreateNullValue());
        return value.ValueType() == JsonValueType::Object ? value.GetObject() : JsonObject();
    }
    hstring Format(JsonObject const& data, wchar_t const* key, int decimals = 0)
    {
        auto value = data.GetNamedValue(key, JsonValue::CreateNullValue());
        if (value.ValueType() != JsonValueType::Number || !std::isfinite(value.GetNumber())) return L"--";
        wchar_t text[48]; swprintf_s(text, L"%.*f", decimals, value.GetNumber()); return text;
    }
    IJsonValue S(wchar_t const* s) { return JsonValue::CreateStringValue(s); }
    IJsonValue N(double n) { return JsonValue::CreateNumberValue(n); }
    IJsonValue B(bool b) { return JsonValue::CreateBooleanValue(b); }
    JsonObject Setting(wchar_t const* key, IJsonValue const& value) { JsonObject o; o.SetNamedValue(key, value); return o; }
}

namespace winrt::ClawWidget::implementation
{
    OsdPage::OsdPage()
    {
        InitializeComponent();
        m_agent = std::make_shared<claw::Agent>(Dispatcher());
        auto weak = get_weak();
        m_agent->OnConnection = [weak](bool connected) {
            if (auto self = weak.get()) { self->m_connected = connected; self->m_pending = false; if (connected) self->Refresh(); self->Update(); }
        };
        m_agent->OnEvent = [weak](hstring const& event, IJsonValue const& value) {
            if (auto self = weak.get(); self && event == L"power_status" && value.ValueType() == JsonValueType::Object)
                self->Accept(Object(Object(value.GetObject(), L"osd"), L"monitor"));
        };
        AddMenu(L"详细程度", {
            { L"关闭", [weak] { if (auto s = weak.get()) s->Set(Setting(L"enabled", B(false))); } },
            { L"1 · 简洁", [weak] { if (auto s = weak.get()) { auto o = Setting(L"level", N(1)); o.SetNamedValue(L"enabled", B(true)); s->Set(o); } } },
            { L"2 · 标准", [weak] { if (auto s = weak.get()) { auto o = Setting(L"level", N(2)); o.SetNamedValue(L"enabled", B(true)); s->Set(o); } } },
            { L"3 · 详细", [weak] { if (auto s = weak.get()) { auto o = Setting(L"level", N(3)); o.SetNamedValue(L"enabled", B(true)); s->Set(o); } } },
            { L"4 · 完整", [weak] { if (auto s = weak.get()) { auto o = Setting(L"level", N(4)); o.SetNamedValue(L"enabled", B(true)); s->Set(o); } } }
        });
        AddMenu(L"字号", {
            { L"小", [weak] { if (auto s = weak.get()) s->Set(Setting(L"size", S(L"small"))); } },
            { L"中", [weak] { if (auto s = weak.get()) s->Set(Setting(L"size", S(L"medium"))); } },
            { L"大", [weak] { if (auto s = weak.get()) s->Set(Setting(L"size", S(L"large"))); } }
        });
        AddMenu(L"排版", {
            { L"一行", [weak] { if (auto s = weak.get()) s->Set(Setting(L"rows", N(1))); } },
            { L"两行", [weak] { if (auto s = weak.get()) s->Set(Setting(L"rows", N(2))); } },
            { L"缩小单位", [weak] { if (auto s = weak.get()) s->Set(Setting(L"smallUnits", B(true))); } },
            { L"正常单位", [weak] { if (auto s = weak.get()) s->Set(Setting(L"smallUnits", B(false))); } }
        });
        AddMenu(L"识别", {
            { L"将目标程序视为游戏", [weak] { if (auto s = weak.get()) s->Correct(L"game"); } },
            { L"忽略目标程序", [weak] { if (auto s = weak.get()) s->Correct(L"ignore"); } },
            { L"恢复目标程序自动识别", [weak] { if (auto s = weak.get()) s->Correct(L"auto"); } }
        });
        AddMenu(L"显示方式", {
            { L"Game Bar", [weak] { if (auto s = weak.get()) s->Set(Setting(L"renderer", S(L"gamebar"))); } },
            { L"兼容显示（RTSS）", [weak] { if (auto s = weak.get()) s->Set(Setting(L"renderer", S(L"rtss"))); } }
        });
        m_timer.Interval(std::chrono::seconds(1));
        m_timer.Tick([weak](auto&&, auto&&) {
            if (auto self = weak.get()) { if (self->m_connected) self->Refresh(); else self->m_agent->Connect(); self->Update(); }
        });
        Loaded([weak](auto&&, auto&&) { if (auto self = weak.get()) { self->m_agent->Connect(); self->m_timer.Start(); } });
        Unloaded([weak](auto&&, auto&&) { if (auto self = weak.get()) self->m_timer.Stop(); });
    }

    void OsdPage::AddMenu(wchar_t const* label, std::initializer_list<std::pair<wchar_t const*, std::function<void()>>> items)
    {
        Button button; button.Content(box_value(label)); button.MinHeight(40); button.Padding(Thickness{ 10, 4, 10, 4 });
        MenuFlyout menu;
        for (auto const& [name, action] : items)
        {
            MenuFlyoutItem item; item.Text(name); item.Click([action](auto&&, auto&&) { action(); }); menu.Items().Append(item);
        }
        button.Flyout(menu); Buttons().Children().Append(button);
    }

    void OsdPage::OnNavigatedTo(Navigation::NavigationEventArgs const& e)
    {
        m_widget = e.Parameter().try_as<XboxGameBarWidget>();
        if (!m_widget) return;
        m_target = std::make_unique<claw::GameTarget>(m_widget);
        auto weak = get_weak();
        auto changed = [weak](auto&&, auto&&) {
            if (auto self = weak.get()) self->Dispatcher().RunAsync(Windows::UI::Core::CoreDispatcherPriority::Normal,
                [weak] { if (auto page = weak.get()) page->Update(); });
        };
        m_widget.GameBarDisplayModeChanged(changed);
        m_widget.PinnedChanged(changed);
        m_widget.RequestedOpacityChanged(changed);
        Update();
    }

    void OsdPage::Refresh()
    {
        if (m_pending || !m_agent->Ready()) return;
        if (m_target) m_target->Report(*m_agent);
        JsonObject window;
        window.SetNamedValue(L"pinned", B(m_widget && m_widget.Pinned()));
        window.SetNamedValue(L"visible", B(m_widget && m_widget.Visible()));
        JsonArray args;
        if (m_widget) args.Append(window);
        m_pending = true;
        m_agent->Call(L"get_monitor_state", args, [weak = get_weak()](IJsonValue const& result, hstring const& error) {
            if (auto self = weak.get())
            {
                self->m_pending = false;
                if (error.empty() && result.ValueType() == JsonValueType::Object) self->Accept(result.GetObject());
                else { self->m_message = error; self->Update(); }
            }
        });
    }

    void OsdPage::Accept(JsonObject const& data)
    {
        m_data = data; m_received = std::chrono::steady_clock::now(); Update();
    }

    void OsdPage::Set(JsonObject const& settings)
    {
        if (!m_agent->Ready()) return;
        JsonArray args; args.Append(settings);
        m_agent->Call(L"set_osd_preferences", args, [weak = get_weak()](IJsonValue const& result, hstring const& error) {
            if (auto self = weak.get()) { self->m_message = error; if (error.empty() && result.ValueType() == JsonValueType::Object) self->Accept(result.GetObject()); else self->Update(); }
        });
    }

    void OsdPage::Correct(wchar_t const* mode)
    {
        if (!m_data || !m_agent->Ready()) return;
        auto target = Object(Object(m_data, L"session"), L"correctionTarget");
        auto key = target.GetNamedString(L"identity", L"");
        if (key.empty()) { m_message = L"请先切回目标程序，再打开 Game Bar"; Update(); return; }
        JsonArray args; args.Append(S(mode)); args.Append(JsonValue::CreateStringValue(key));
        m_agent->Call(L"correct_game_recognition", args, [weak = get_weak()](IJsonValue const&, hstring const& error) {
            if (auto self = weak.get()) { self->m_message = error.empty() ? L"识别设置已保存" : error; self->Refresh(); }
        });
    }

    void OsdPage::Update()
    {
        bool foreground = !m_widget || m_widget.GameBarDisplayMode() == XboxGameBarDisplayMode::Foreground;
        Options().Visibility(foreground ? Visibility::Visible : Visibility::Collapsed);
        bool connected = m_connected && std::chrono::steady_clock::now() - m_received < std::chrono::seconds(4);
        auto settings = Object(m_data, L"settings"), session = Object(m_data, L"session"), sample = Object(m_data, L"sample");
        bool enabled = settings.GetNamedBoolean(L"enabled", false);
        bool native = settings.GetNamedString(L"renderer", L"gamebar") == L"gamebar";
        bool visible = session.GetNamedBoolean(L"visible", false);
        bool fresh = connected && m_data && m_data.GetNamedBoolean(L"fresh", false);
        bool show = foreground || (connected && enabled && native && visible);
        ReadoutBackground().Visibility(show ? Visibility::Visible : Visibility::Collapsed);
        double opacity = m_widget ? m_widget.RequestedOpacity() / 100.0 : 1.0;
        auto layout = Object(settings, L"layout");
        bool custom = layout.GetNamedBoolean(L"custom", false);
        if (custom) opacity *= layout.GetNamedNumber(L"opacity", 100) / 100.0;
        ReadoutBackground().Opacity(opacity);
        ReadoutBackground().Background(SolidColorBrush(Color{ static_cast<uint8_t>(custom ? layout.GetNamedNumber(L"background", 0) * 2.55 : 192), 24, 27, 37 }));
        auto size = settings.GetNamedString(L"size", L"medium");
        double font = size == L"small" ? 14 : size == L"large" ? 22 : 17;
        Readout().FontSize(font);
        Readout().Inlines().Clear();
        auto text = [&](hstring const& value) { Run run; run.Text(value); Readout().Inlines().Append(run); };
        if (!connected) text(L"工具箱后台未连接");
        else if (!enabled) text(L"性能显示已关闭");
        else if (!native) text(L"正在使用 RTSS 兼容显示");
        else if (!visible) text(session.GetNamedString(L"state", L"") == L"candidate" ? L"正在识别目标程序…" : L"等待游戏");
        else if (!fresh) text(L"等待监控数据…");
        else if (m_data && m_data.GetNamedValue(L"nativeItems", JsonValue::CreateNullValue()).ValueType() == JsonValueType::Array
                 && Object(settings, L"layout").GetNamedBoolean(L"custom", false))
        {
            auto items = m_data.GetNamedArray(L"nativeItems");
            int previousRow = -1;
            for (auto const& value : items)
            {
                auto item = value.GetObject();
                int row = static_cast<int>(item.GetNamedNumber(L"row", 0));
                if (previousRow >= 0) text(previousRow == row ? L"   " : L"\n");
                previousRow = row;
                auto rgb = item.GetNamedString(L"color", L"FFFFFF");
                unsigned int color = 0xFFFFFF; swscanf_s(rgb.c_str(), L"%x", &color);
                Run label; label.Text(item.GetNamedString(L"label") + L" ");
                label.Foreground(SolidColorBrush(Color{ 255, static_cast<uint8_t>(color >> 16), static_cast<uint8_t>(color >> 8), static_cast<uint8_t>(color) }));
                Readout().Inlines().Append(label);
                text(item.GetNamedString(L"value"));
                Run unit; unit.Text(item.GetNamedString(L"unit"));
                if (settings.GetNamedBoolean(L"smallUnits", false)) unit.FontSize(font * .75);
                Readout().Inlines().Append(unit);
            }
        }
        else
        {
            int level = static_cast<int>(settings.GetNamedNumber(L"level", 2));
            bool smallUnits = settings.GetNamedBoolean(L"smallUnits", false);
            struct Metric { hstring name, value, unit; Color color; };
            Color green{ 255, 166, 227, 161 }, blue{ 255, 137, 180, 250 }, peach{ 255, 250, 179, 135 }, mauve{ 255, 203, 166, 247 };
            std::vector<Metric> metrics;
            auto add = [&](wchar_t const* name, wchar_t const* key, wchar_t const* unit, int digits, Color color) {
                metrics.push_back({ hstring(name), Format(sample, key, digits), hstring(unit), color });
            };
            bool presented = sample.GetNamedString(L"fpsKind", L"application") == L"presented";
            add(presented ? L"呈现FPS" : L"FPS", presented ? L"presented" : L"fps", L"", 0, green);
            add(L"功耗", L"package", L"W", 1, peach);
            add(L"温度", L"cpuTemp", L"°C", 0, peach);
            add(L"电量", L"battery", L"%", 0, mauve);
            if (level >= 2) { add(L"CPU", L"cpuUsage", L"%", 0, blue); add(L"GPU", L"gpuUsage", L"%", 0, green); add(L"内存", L"ramUsed", L"GB", 1, blue); }
            if (level >= 3) { add(L"平均帧时", L"frameTime", L"ms", 1, green); add(L"1% Low", L"fps01", L"", 0, green); add(L"风扇", L"fanRpm", L"RPM", 0, peach); add(L"电池", L"batteryW", L"W", 1, mauve); }
            if (level >= 4) { if (!presented) add(L"呈现", L"presented", L"FPS", 0, green); add(L"GPU频率", L"gpuMhz", L"MHz", 0, green); add(L"CPU频率", L"cpuMhz", L"MHz", 0, blue); add(L"显存", L"gpuMem", L"MB", 0, blue); }
            size_t split = settings.GetNamedNumber(L"rows", 1) == 2 ? (metrics.size() + 1) / 2 : metrics.size();
            for (size_t i = 0; i < metrics.size(); ++i)
            {
                if (i) text(i == split ? L"\n" : L"   ");
                auto const& metric = metrics[i];
                Run label; label.Text(metric.name + L" "); label.Foreground(SolidColorBrush(metric.color)); Readout().Inlines().Append(label);
                text(metric.value);
                if (!metric.unit.empty()) { Run unit; unit.Text(metric.unit); if (smallUnits) unit.FontSize(font * .75); Readout().Inlines().Append(unit); }
            }
        }
        auto game = Object(session, L"game"), correction = Object(session, L"correctionTarget");
        hstring status = m_widget && m_widget.Pinned() ? L"已固定 · 收起 Game Bar 后继续显示" : L"请点击窗口图钉，收起 Game Bar 后继续显示";
        if (connected && game.HasKey(L"title")) status = status + L"\n" + game.GetNamedString(L"title");
        if (foreground && correction.HasKey(L"title")) status = status + L" · 识别目标：" + correction.GetNamedString(L"title");
        if (session.HasKey(L"targetTrackingEnabled") && session.GetNamedValue(L"targetTrackingEnabled").ValueType() == JsonValueType::Boolean && !session.GetNamedBoolean(L"targetTrackingEnabled"))
            status = status + L"\n可在 Game Bar 设置中允许目标跟踪；当前使用窗口识别";
        if (connected && m_data && !m_data.GetNamedString(L"framesError", L"").empty()) status = status + L"\n帧率暂不可用：" + m_data.GetNamedString(L"framesError");
        if (!m_message.empty()) status = status + L"\n" + m_message;
        Status().Text(status);
        bool wantsGraph = !custom && settings.GetNamedNumber(L"level", 2) >= 4;
        if (custom)
            for (auto const& value : layout.GetNamedArray(L"items", JsonArray()))
                if (value.GetObject().GetNamedString(L"id", L"") == L"frameGraph" && value.GetObject().GetNamedBoolean(L"enabled", false)) wantsGraph = true;
        auto frameTimes = sample.GetNamedArray(L"frameTimes", JsonArray());
        bool graph = show && connected && enabled && native && visible && fresh && wantsGraph && frameTimes.Size() > 1;
        Graph().Visibility(graph ? Visibility::Visible : Visibility::Collapsed);
        GraphLine().Points().Clear();
        if (graph)
        {
            double width = GraphCanvas().ActualWidth();
            for (uint32_t i = 0; i < frameTimes.Size(); ++i)
            {
                double value = frameTimes.GetNumberAt(i);
                GraphLine().Points().Append(Point{ static_cast<float>(width * i / (frameTimes.Size() - 1)),
                    static_cast<float>(40 - std::clamp(value, 0.0, 50.0) * .8) });
            }
        }
        // Resize only on layout-mode changes, not once per sample. Narrow windows wrap naturally.
        double height = foreground ? 240 : (font >= 22 || settings.GetNamedNumber(L"rows", 1) == 2 ? 120 : 84);
        if (graph) height += 56;
        height = std::clamp(std::max(height, Readout().ActualHeight() + 32 + (graph ? 60 : 0)
            + (foreground ? Options().ActualHeight() + 12 : 0)), 64.0, 500.0);
        if (m_widget && m_height != height) { m_height = height; m_widget.TryResizeWindowAsync(Size{ static_cast<float>(m_widget.WindowBounds().Width), static_cast<float>(height) }); }
    }
}
