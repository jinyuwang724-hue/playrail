#include "pch.h"
#include "WidgetPage.h"
#if __has_include("WidgetPage.g.cpp")
#include "WidgetPage.g.cpp"
#endif
#include "Icons.h"
#include <algorithm>
#include <cmath>

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Data::Json;
using namespace Windows::Storage;
using namespace Windows::UI;
using namespace Windows::UI::Xaml;
using namespace Windows::UI::Xaml::Controls;
using namespace Windows::UI::Xaml::Input;
using namespace Windows::UI::Xaml::Media;
using namespace Microsoft::Gaming::XboxGameBar;
using Windows::System::VirtualKey;

namespace
{
    constexpr int TileColumns = 3;
    constexpr int TileRows = 3;
    constexpr size_t MaxPinnedTiles = TileColumns * TileRows;
    const std::vector<std::wstring> DefaultTiles{ L"power", L"fan", L"fps", L"refresh", L"osd", L"magpie", L"rumble", L"lossless", L"fse" };
    const std::vector<std::wstring> Pool{ L"target", L"cpu", L"fanfull", L"ecore", L"sharpen", L"screenshot" };
    const std::map<std::wstring, std::pair<std::wstring, std::wstring>> Names{  // id -> (name, icon)
        { L"power", { L"功耗", L"power" } }, { L"fan", { L"风扇", L"fan" } }, { L"fps", { L"限帧", L"fps" } },
        { L"refresh", { L"刷新率", L"refresh" } }, { L"osd", { L"性能显示", L"osd" } }, { L"magpie", { L"Magpie", L"magpie" } },
        { L"rumble", { L"震动", L"rumble" } }, { L"lossless", { L"小黄鸭", L"qamDuck" } }, { L"fse", { L"返回 FSE", L"qamFse" } },
        { L"target", { L"功耗目标", L"target" } }, { L"cpu", { L"CPU 频率", L"cpu" } }, { L"fanfull", { L"风扇全速", L"fanfull" } },
        { L"ecore", { L"E 核调度（重启后生效）", L"ecore" } }, { L"sharpen", { L"画面锐化", L"sharpen" } }, { L"screenshot", { L"截图", L"screenshot" } } };
    const std::map<std::wstring, std::wstring> Hints{
        { L"target", L"智能模式用" }, { L"cpu", L"最高 / 限频" }, { L"fanfull", L"开 / 关" }, { L"ecore", L"" },
        { L"sharpen", L"Magpie" }, { L"screenshot", L"保存到相册" } };
    const std::vector<int> FpsSteps{ 0, 30, 40, 45, 60, 90, 120 };
    const std::vector<int> AutoTargets{ 30, 40, 45, 60, 72, 90, 120, 144, 165, 240 };
    const std::vector<std::pair<int, std::wstring>> RumbleSteps{ { 0, L"关" }, { 35, L"弱" }, { 70, L"中" }, { 100, L"强" } };
    const std::vector<std::pair<std::wstring, std::wstring>> FanSteps{ { L"quiet", L"静音" }, { L"balanced", L"均衡" }, { L"performance", L"性能" } };

    // ---- JSON helpers: dotted paths into the agent's readings ----
    IJsonValue Get(JsonObject const& root, std::wstring const& path)
    {
        if (!root) return nullptr;
        IJsonValue value = root;
        size_t start = 0;
        while (true)
        {
            size_t dot = path.find(L'.', start);
            auto key = path.substr(start, dot == std::wstring::npos ? std::wstring::npos : dot - start);
            if (value.ValueType() != JsonValueType::Object) return nullptr;
            auto object = value.GetObject();
            if (!object.HasKey(key)) return nullptr;
            value = object.GetNamedValue(key);
            if (dot == std::wstring::npos) return value;
            start = dot + 1;
        }
    }
    bool Has(JsonObject const& o, std::wstring const& p) { auto v = Get(o, p); return v && v.ValueType() != JsonValueType::Null; }
    double Num(JsonObject const& o, std::wstring const& p, double fallback = 0)
    { auto v = Get(o, p); return v && v.ValueType() == JsonValueType::Number ? v.GetNumber() : fallback; }
    bool Bool(JsonObject const& o, std::wstring const& p)
    { auto v = Get(o, p); return v && v.ValueType() == JsonValueType::Boolean && v.GetBoolean(); }
    hstring Str(JsonObject const& o, std::wstring const& p, hstring const& fallback = L"")
    { auto v = Get(o, p); return v && v.ValueType() == JsonValueType::String ? v.GetString() : fallback; }
    JsonArray Arr(JsonObject const& o, std::wstring const& p)
    { auto v = Get(o, p); return v && v.ValueType() == JsonValueType::Array ? v.GetArray() : JsonArray(); }
    JsonObject Obj(JsonObject const& o, std::wstring const& p)
    { auto v = Get(o, p); return v && v.ValueType() == JsonValueType::Object ? v.GetObject() : JsonObject(); }
    void SetPath(JsonObject const& root, std::wstring const& path, IJsonValue const& value)
    {
        auto object = root;
        size_t start = 0, dot;
        while ((dot = path.find(L'.', start)) != std::wstring::npos)
        {
            auto key = path.substr(start, dot - start);
            if (!object.HasKey(key) || object.GetNamedValue(key).ValueType() != JsonValueType::Object) object.SetNamedValue(key, JsonObject());
            object = object.GetNamedObject(key);
            start = dot + 1;
        }
        object.SetNamedValue(path.substr(start), value);
    }
    JsonObject Clone(JsonObject const& o) { return o ? JsonObject::Parse(o.Stringify()) : nullptr; }
    IJsonValue N(double v) { return JsonValue::CreateNumberValue(v); }
    IJsonValue B(bool v) { return JsonValue::CreateBooleanValue(v); }
    IJsonValue S(std::wstring const& v) { return JsonValue::CreateStringValue(v); }
    JsonArray Args(std::initializer_list<IJsonValue> values) { JsonArray a; for (auto& v : values) a.Append(v); return a; }

    hstring Fmt(double v, int digits)
    {
        wchar_t buffer[32];
        swprintf_s(buffer, L"%.*f", digits, v);
        return buffer;
    }
    hstring Ghz(double mhz)
    {
        std::wstring text = Fmt(mhz / 1000, 2).c_str();
        while (!text.empty() && text.back() == L'0') text.pop_back();
        if (!text.empty() && text.back() == L'.') text.pop_back();
        return hstring(text + L" GHz");
    }

    Brush Resource(wchar_t const* name) { return Application::Current().Resources().Lookup(box_value(name)).as<Brush>(); }

    // A design icon (24x24 stroke path) at the given size, drawn in `brush`.
    FrameworkElement Icon(std::wstring const& name, double size = 20, Brush brush = nullptr)
    {
        auto path = Markup::XamlReader::Load(hstring(L"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
            L"StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Data='" + Icons().at(name) + L"'/>"))
            .as<Shapes::Path>();
        path.Stroke(brush ? brush : Resource(L"TextFillColorPrimaryBrush"));
        Canvas canvas; canvas.Width(24); canvas.Height(24); canvas.Children().Append(path);
        Viewbox box; box.Width(size); box.Height(size); box.Child(canvas);
        return box;
    }

    int Nearest(std::vector<int> const& list, double value)
    {
        int best = 0;
        for (int i = 0; i < static_cast<int>(list.size()); i++) if (std::abs(list[i] - value) < std::abs(list[best] - value)) best = i;
        return best;
    }

    GridLength Star() { return GridLengthHelper::FromValueAndType(1, GridUnitType::Star); }

    double Tok(wchar_t const* name) { return unbox_value<double>(Application::Current().Resources().Lookup(box_value(name))); }
}

namespace winrt::ClawWidget::implementation
{
    WidgetPage::WidgetPage()
    {
        InitializeComponent();
        // Keep the first three rows in the user's order. Overflow remains available in the picker.
        auto settings = ApplicationData::Current().LocalSettings().Values();
        auto saved = settings.TryLookup(L"tiles");
        JsonArray list{ nullptr };
        if (saved && JsonArray::TryParse(unbox_value<hstring>(saved), list))
            for (auto const& v : list)
            {
                if (m_tiles.size() == MaxPinnedTiles) break;
                if (v.ValueType() != JsonValueType::String) continue;
                std::wstring id = v.GetString().c_str();
                if (id == L"keyboard") id = L"lossless";
                else if (id == L"close") id = L"fse";
                if (Names.count(id) && std::find(m_tiles.begin(), m_tiles.end(), id) == m_tiles.end()) m_tiles.push_back(id);
            }
        else m_tiles = DefaultTiles;

        BuildRail();
        BuildPowerControl();
        BuildQuickShortcuts();
        BuildTiles();

        m_agent = std::make_shared<claw::Agent>(Dispatcher());
        m_agent->OnConnection = [this](bool connected) {
            if (connected) { m_failedSince = {}; Refresh(); }
            else if (m_failedSince == Clock::time_point{}) m_failedSince = Clock::now();
            Update();
        };
        m_agent->OnEvent = [this](hstring const& event, IJsonValue const& value) {
            if (!m_data || value.ValueType() != JsonValueType::Object) return;
            auto data = Clone(m_data);
            if (event == L"power_status") data.SetNamedValue(L"power", Clone(value.GetObject()));
            else if (event == L"magpie_status") data.SetNamedValue(L"magpie", Clone(value.GetObject()));
            else if (event == L"gamepad_mode") SetPath(data, L"gamepad.mode", value.GetObject().GetNamedValue(L"mode"));
            else return;
            Accept(data);
        };

        m_saveTimer.Interval(std::chrono::milliseconds(200));
        m_saveTimer.Tick([this](auto&&, auto&&) {
            m_saveTimer.Stop();
            if (m_draft) Send(L"power", L"save_power", Args({ Clone(m_draft) }));
        });
        m_pollTimer.Interval(std::chrono::milliseconds(500));
        m_pollTimer.Tick([this](auto&&, auto&&) {
            PublishPowerVisibility();
            if (!m_scalingPage.empty() && (!m_widget || m_widget.Visible())) RefreshScaling();
            // The 小黄鸭 tile shows whether it is scaling; its state is not part of the main reading.
            else if (m_scalingPage.empty() && m_agent->Ready() && (!m_widget || m_widget.Visible()) && !m_losslessPolling
                && Clock::now() - m_losslessPolled > std::chrono::seconds(3)) {
                m_losslessPolling = true;
                m_losslessPolled = Clock::now();
                m_agent->Call(L"qam_scaling_state", Args({ S(L"lossless") }), [this](auto const& value, hstring const&) {
                    m_losslessPolling = false;
                    if (m_shutdown || !value || value.ValueType() != JsonValueType::Object) return;
                    bool active = Bool(value.GetObject(), L"active");
                    if (active == m_losslessActive) return;
                    m_losslessActive = active;
                    if (auto tile = m_views.find(L"lossless"); tile != m_views.end()) UpdateTile(tile->first, tile->second);
                });
            }
            if (m_agent->Ready()) { if (!m_saveTimer.IsEnabled()) Refresh(); }
            else m_agent->Connect();
            Update();
        });
        m_armTimer.Interval(std::chrono::seconds(1));
        m_armTimer.Tick([this](auto&&, auto&&) { if (--m_armLeft <= 0) ClearArm(); else Update(); });
        m_flashTimer.Interval(std::chrono::seconds(4));
        m_flashTimer.Tick([this](auto&&, auto&&) { m_flashTimer.Stop(); MessageBar().IsOpen(false); ShowBanners(); });

        EditButton().Click([this](auto&&, auto&&) { SetEditing(!m_editing); });
        DoneButton().Click([this](auto&&, auto&&) { SetEditing(false); });
        ScalingBack().Content(Icon(L"chevronLeft", 18));
        ScalingBack().Click([this](auto&&, auto&&) { OpenScaling({}); });
        ReconnectButton().Click([this](auto&&, auto&&) { m_failedSince = {}; m_agent->Connect(); Update(); });
        MuteButton().Click([this](auto&&, auto&&) {
            if (!m_data) return;
            bool muted = !Bool(m_data, L"system.muted");
            HoldValue(L"system.muted", B(muted), L"mute");
            Send(L"mute", L"set_mute", Args({ B(muted) }));
            Update();
        });
        auto power = [this](wchar_t const* action) {
            return [this, action](IInspectable const&, RoutedEventArgs const&) { Send(L"power_action", L"power_action", Args({ S(action) })); };
        };
        SleepItem().Click(power(L"sleep"));
        ShutdownItem().Click([this](auto&&, auto&&) { RequestAnimatedPower(L"shutdown"); });
        RestartItem().Click([this](auto&&, auto&&) { RequestAnimatedPower(L"restart"); });
        PowerPreviewItem().Click([this](auto&&, auto&&) { RequestAnimatedPower(L"restart", true); });
        LockItem().Click(power(L"lock"));
        for (auto const& [slider, key] : { std::pair{ BrightnessSlider(), std::wstring(L"brightness") },
                                          std::pair{ VolumeSlider(), std::wstring(L"volume") } })
        {
            slider.ValueChanged([this, key = key](IInspectable const&, Primitives::RangeBaseValueChangedEventArgs const& e) {
                if (m_updatingSliders || !m_data || !m_agent->Ready()) return;
                int value = static_cast<int>(std::round(e.NewValue()));
                HoldValue(L"system." + key, N(value), key);
                if (key == L"volume") HoldValue(L"system.muted", B(false), key);
                Send(key, key == L"volume" ? L"set_volume" : L"set_brightness", Args({ N(value) }));
                Update();
            });
        }
        auto ink = SolidColorBrush(ColorHelper::FromArgb(255, 244, 244, 245));
        for (auto const& [slider, label] : { std::pair{ PowerSlider(), PowerSetpoint() },
                 std::pair{ BrightnessSlider(), BrightnessValue() }, std::pair{ VolumeSlider(), VolumeValue() } }) {
            slider.GotFocus([label, ink](IInspectable const& sender, auto&&) {
                sender.as<Slider>().Foreground(ink);
                label.Foreground(ink);
            });
            slider.LostFocus([this, label](IInspectable const& sender, auto&&) {
                auto control = sender.as<Slider>();
                if (control == m_railAdjustment) SetRailAdjustment(nullptr);
                control.ClearValue(Control::ForegroundProperty());
                label.ClearValue(TextBlock::ForegroundProperty());
            });
        }
        PreviewKeyDown({ this, &WidgetPage::OnPreviewKey });
        SizeChanged([this](IInspectable const&, SizeChangedEventArgs const& e) {
            bool narrow = e.NewSize().Width < 480;
            if (narrow == m_narrow && m_layoutDone) return;
            m_narrow = narrow; m_layoutDone = true;
            Header().Padding(narrow ? Thickness{ 12, 8, 12, 8 } : Thickness{ 14, 10, 14, 10 });
            Rail().Width(narrow ? 44 : 48);
            for (auto& [id, view] : m_views) view.button.Padding(narrow ? Thickness{ 10, 10, 10, 10 } : Thickness{ 12, 12, 12, 12 });
            Update();
        });

        Loaded([this](IInspectable const&, RoutedEventArgs const&) {
            if (!m_tiles.empty()) m_views[m_tiles.front()].button.Focus(FocusState::Programmatic);
        });
        Window::Current().Closed([weak = get_weak()](auto&&, auto&&) { if (auto self = weak.get()) self->Shutdown(); });
        Unloaded([weak = get_weak()](auto&&, auto&&) { if (auto self = weak.get()) self->Shutdown(); });
        Update();
        LoadCache();
        m_agent->Connect();
        m_pollTimer.Start();
    }

    // No protocol activation or foreground change: the native renderer dismisses Game Bar
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

    void WidgetPage::OnNavigatedTo(Navigation::NavigationEventArgs const& e)
    {
        m_widget = e.Parameter().try_as<XboxGameBarWidget>();
        if (!m_widget) return;
        PublishPowerVisibility();
        auto visibilityChanged = [weak = get_weak()](auto&&, auto&&) {
            if (auto self = weak.get()) self->Dispatcher().RunAsync(Core::CoreDispatcherPriority::Normal,
                [self]() {
                    if (self->m_widget && !self->m_widget.Visible()) self->SetRailAdjustment(nullptr);
                    self->PublishPowerVisibility();
                });
        };
        m_widget.GameBarDisplayModeChanged(visibilityChanged);
        m_widget.VisibleChanged(visibilityChanged);
        m_widget.WindowStateChanged(visibilityChanged);
        m_target = std::make_unique<claw::GameTarget>(m_widget);
        m_widget.GameBarDisplayModeChanged([weak = get_weak()](auto&&, auto&&) {
            if (auto self = weak.get())
                self->Dispatcher().RunAsync(Core::CoreDispatcherPriority::Normal, [self]() {
                    if (self->m_widget.GameBarDisplayMode() != XboxGameBarDisplayMode::Foreground) return;
                    self->Refresh();
                });
        });
        // The OEM first slot selects this widget. A foreground notification is
        // a reading, never a request to activate Game Bar a second time.
        Dispatcher().RunAsync(Core::CoreDispatcherPriority::Normal, [weak = get_weak()]() {
            if (auto self = weak.get(); self && self->m_widget.GameBarDisplayMode() == XboxGameBarDisplayMode::Foreground)
                self->Refresh();
        });
    }

    void WidgetPage::Shutdown()
    {
        m_shutdown = true;
        m_saveTimer.Stop(); m_pollTimer.Stop(); m_armTimer.Stop(); m_flashTimer.Stop();
        if (m_agent) m_agent->Shutdown();
        m_sending.clear(); m_target.reset(); m_widget = nullptr; PublishPowerVisibility();
    }

    // ---- setup ---------------------------------------------------------------------------------
    void WidgetPage::TogglePadMode()
    {
        if (m_modeSwitching || !m_agent || !m_agent->Ready() || !Bool(m_data, L"gamepad.available")) return;
        m_modeSwitching = true;
        m_holds.erase(L"gamepad.mode");
        Update();
        auto strong = get_strong();
        auto finish = [this, strong](IJsonValue const& result, hstring const& error) {
            m_modeSwitching = false;
            if (!error.empty()) Flash(error);
            else if (m_data && result.ValueType() == JsonValueType::Object)
            {
                auto mode = result.GetObject().GetNamedString(L"mode", L"");
                if (mode == L"desktop" || mode == L"xinput")
                {
                    HoldValue(L"gamepad.mode", JsonValue::CreateStringValue(mode), L"padmode");
                    ReleaseHold(L"padmode");
                }
            }
            Refresh(); Update();
        };
        // Polls and mode events may still describe the previous mode. Read the
        // actual logical/firmware state before choosing the opposite mode.
        m_agent->Call(L"get_gamepad_state", JsonArray(), [this, strong, finish](IJsonValue const& result, hstring const& error) {
            if (!error.empty()) { finish(result, error); return; }
            if (result.ValueType() != JsonValueType::Object || !Bool(result.GetObject(), L"available"))
            { finish(result, L"读不到当前手柄模式，请稍后重试"); return; }
            auto mode = Str(result.GetObject(), L"mode");
            if (mode != L"desktop" && mode != L"xinput")
            { finish(result, L"当前手柄模式不支持切换"); return; }
            m_agent->Call(L"set_gamepad_mode", Args({ S(mode == L"desktop" ? L"xinput" : L"desktop") }), finish);
        });
    }

    void WidgetPage::BuildRail()
    {
        EditButton().Content(Icon(L"edit"));
        AutoBrightness().Content(Icon(L"sun"));
        MuteButton().Content(Icon(L"speaker"));
        PowerButton().Content(Icon(L"shutdown"));
    }

    void WidgetPage::BuildPowerControl()
    {
        PowerLabel().Children().InsertAt(0, Icon(L"power", 17, Resource(L"TextFillColorSecondaryBrush")));
        for (int i = 0; i < 4; ++i) {
            Shapes::Rectangle tick;
            tick.Width(2); tick.Height(9); tick.RadiusX(1); tick.RadiusY(1);
            PowerTicks().Children().Append(tick);
        }
        PowerTicks().SizeChanged([this](auto&&, auto&&) { UpdatePowerControl(m_data && !m_offline); });
        PowerSlider().ValueChanged([this](auto&&, auto const& e) {
            if (m_updatingPower || !m_draft || !m_agent || !m_agent->Ready()) return;
            m_draft.SetNamedValue(L"enabled", B(true));
            m_draft.SetNamedValue(L"mode", S(L"manual"));
            SetPath(m_draft, L"manual.tdp", N(std::round(e.NewValue())));
            EditPower();
            UpdatePowerControl(true);
            if (auto tile = m_views.find(L"power"); tile != m_views.end()) UpdateTile(tile->first, tile->second);
        });
    }

    void WidgetPage::UpdatePowerControl(bool usable)
    {
        auto number = [&](wchar_t const* path, int digits) {
            return usable && Has(m_data, path) ? Fmt(Num(m_data, path), digits) : hstring(L"--");
        };
        PowerActual().Text(L"实际 " + number(L"power.actual.package", 1) + L" W  ·  " + number(L"power.live.cpuTemp", 0) + L" °C");
        double low = Num(m_data, L"power.limits.tdpMin", 8);
        double high = std::max(low, Num(m_data, L"power.limits.tdpMax", 35));
        double manual = Num(m_draft, L"manual.tdp", low);
        double limit = Bool(m_draft, L"enabled") && Str(m_draft, L"mode") == L"manual" ? manual : Num(m_data, L"power.target.pl1", manual);
        PowerSetpoint().Text(usable ? Fmt(limit, 0) : hstring(L"--"));
        auto slider = PowerSlider();
        m_updatingPower = true;
        slider.IsEnabled(usable && m_draft && m_agent && m_agent->Ready() && high > low);
        slider.Maximum(std::max(high, slider.Minimum()));
        slider.Minimum(low);
        slider.Maximum(high);
        slider.Value(std::clamp(limit, low, high));
        m_updatingPower = false;

        double trackWidth = PowerTicks().ActualWidth() - 24;
        static constexpr double marks[]{ 8, 15, 25, 35 };
        for (uint32_t i = 0; i < 4; ++i) {
            auto tick = PowerTicks().Children().GetAt(i).as<Shapes::Rectangle>();
            double mark = marks[i];
            bool show = trackWidth > 0 && high > low && mark >= low && mark <= high
                && std::abs(mark - slider.Value()) * trackWidth > 12 * (high - low);
            tick.Visibility(show ? Visibility::Visible : Visibility::Collapsed);
            if (show) {
                Canvas::SetLeft(tick, 11 + trackWidth * (mark - low) / (high - low));
                tick.Fill(SolidColorBrush(ColorHelper::FromArgb(mark <= limit ? 255 : 89, 244, 244, 245)));
            }
        }
    }

    void WidgetPage::BuildQuickShortcuts()
    {
        // Three square controls, then a card that fills the rest of the widget column.
        constexpr double cell = 62.4;
        for (int col = 0; col < 3; ++col) {
            ColumnDefinition column; column.Width(GridLengthHelper::FromPixels(cell));
            QuickShortcuts().ColumnDefinitions().Append(column);
        }
        ColumnDefinition wideColumn;
        wideColumn.Width(Star());
        QuickShortcuts().ColumnDefinitions().Append(wideColumn);
        for (int row = 0; row < 2; ++row) {
            RowDefinition definition; definition.Height(GridLengthHelper::FromPixels(cell));
            QuickShortcuts().RowDefinitions().Append(definition);
        }
        QuickShortcuts().SizeChanged([this](auto&&, SizeChangedEventArgs const& e) {
            double spacing = QuickShortcuts().ColumnSpacing() * 4;
            if (e.NewSize().Width <= spacing || e.NewSize().Width == e.PreviousSize().Width) return;
            auto size = GridLengthHelper::FromPixels(std::min(cell, (e.NewSize().Width - spacing) / 5.0));
            for (uint32_t col = 0; col < 3; ++col) QuickShortcuts().ColumnDefinitions().GetAt(col).Width(size);
            for (auto const& row : QuickShortcuts().RowDefinitions()) row.Height(size);
        });
        auto add = [this](int row, int col, wchar_t const* label, wchar_t const* icon, auto action, TextBlock* caption = nullptr) {
            bool wide = col == 3;
            Button button;
            button.HorizontalAlignment(HorizontalAlignment::Stretch);
            button.VerticalAlignment(VerticalAlignment::Stretch);
            button.MinWidth(0);
            button.MinHeight(0);
            button.Padding(wide ? Thickness{ 12, 0, 8, 0 } : Thickness{});
            button.BorderThickness(Thickness{});
            button.Background(SolidColorBrush(ColorHelper::FromArgb(21, 244, 244, 245)));
            Automation::AutomationProperties::SetName(button, label);
            ToolTipService::SetToolTip(button, box_value(label));
            if (wide) {
                Grid content; content.ColumnSpacing(10);
                ColumnDefinition iconColumn, labelColumn;
                iconColumn.Width(GridLengthHelper::Auto()); labelColumn.Width(Star());
                content.ColumnDefinitions().Append(iconColumn); content.ColumnDefinitions().Append(labelColumn);
                Border backing; backing.Width(38); backing.Height(38);
                backing.CornerRadius(CornerRadiusHelper::FromUniformRadius(4));
                backing.Background(SolidColorBrush(ColorHelper::FromArgb(25, 244, 244, 245)));
                backing.Child(Icon(icon, 20));
                TextBlock name; name.Text(label); name.FontSize(15); name.FontWeight(Text::FontWeights::SemiBold());
                if (caption) *caption = name;
                name.VerticalAlignment(VerticalAlignment::Center);
                name.TextTrimming(TextTrimming::CharacterEllipsis);
                Grid::SetColumn(name, 1);
                content.Children().Append(backing); content.Children().Append(name);
                button.HorizontalContentAlignment(HorizontalAlignment::Stretch);
                button.Content(content);
            } else button.Content(Icon(icon, 24));
            button.Click([action](auto&&, auto&&) { action(); });
            Grid::SetRow(button, row); Grid::SetColumn(button, col);
            QuickShortcuts().Children().Append(button);
            return button;
        };
        m_quickFreeze = add(0, 0, L"冻结游戏", L"qamFreeze", [this] {
            if (m_sending.count(L"game-freeze")) return;
            if (Has(m_data, L"gameFreeze.frozen") || Bool(m_data, L"gameFreeze.recoveryPending"))
                Send(L"game-freeze", L"resume_frozen_game");
            else Send(L"game-freeze", L"freeze_game", Args({ JsonValue::CreateStringValue(Str(m_data, L"system.game.identity")) }));
            Update();
        });
        m_quickTaskManager = add(0, 1, L"任务管理器", L"qamTasks", [this] { Send(L"task-manager", L"open_task_manager"); });
        add(0, 2, L"返回桌面", L"qamDesktop", [this] { Send(L"show_desktop", L"show_desktop"); });
        add(0, 3, L"游戏库", L"qamLibrary", [this] { OpenQuickLink(L"claw-library://library"); });
        add(1, 0, L"截图", L"screenshot", [this] { Press(L"screenshot"); });
        add(1, 1, L"键盘", L"keyboard", [this] { Press(L"keyboard"); });
        m_quickPad = add(1, 2, L"手柄模式", L"gamepad", [this] { TogglePadMode(); });
        m_quickExit = add(1, 3, L"退出游戏", L"close", [this] { Press(L"close"); }, &m_quickExitLabel);
    }

    fire_and_forget WidgetPage::OpenQuickLink(hstring uri)
    {
        auto lifetime = get_strong();
        try {
            auto link = Uri(uri);
            if (m_widget) {
                if (co_await m_widget.LaunchUriAsync(link)) co_return;
            } else if (co_await Windows::System::Launcher::LaunchUriAsync(link)) co_return;
        } catch (...) {}
        if (!m_shutdown) Flash(L"无法打开此页面");
    }

    void WidgetPage::OpenScaling(std::wstring engine)
    {
        auto previous = m_scalingPage;
        m_scalingPage = std::move(engine);
        bool open = !m_scalingPage.empty();
        ScalingPanel().Visibility(open ? Visibility::Visible : Visibility::Collapsed);
        MainPanel().Visibility(open ? Visibility::Collapsed : Visibility::Visible);
        Rail().Visibility(open ? Visibility::Collapsed : Visibility::Visible);
        m_scalingData = nullptr;
        m_losslessProfile = -1;
        m_scalingError.clear();
        m_scalingAt = {};
        m_scalingSignature = {};
        m_scalingRows.clear();
        ScalingList().Children().Clear();
        if (open) {
            ScalingTitle().Text(m_scalingPage == L"lossless" ? L"小黄鸭" : L"Magpie");
            RefreshScaling();
            UpdateScaling();
        } else if (auto tile = m_views.find(previous); tile != m_views.end()) {
            tile->second.button.Focus(FocusState::Programmatic);
        }
    }

    void WidgetPage::RefreshScaling()
    {
        if (m_scalingPage.empty() || m_scalingReading || m_scalingSending || !m_agent->Ready()
            || Clock::now() - m_scalingAt < std::chrono::milliseconds(750)) return;
        m_scalingAt = Clock::now();
        m_scalingReading = true;
        auto page = m_scalingPage;
        m_agent->Call(L"qam_scaling_state", Args({ S(page) }), [this, page](auto const& value, hstring const& error) {
            m_scalingReading = false;
            if (m_shutdown || page != m_scalingPage) return;
            if (!error.empty()) m_scalingError = error;
            else if (value && value.ValueType() == JsonValueType::Object) {
                m_scalingData = value.GetObject();
                m_scalingError.clear();
                if (page == L"lossless") m_losslessActive = Bool(m_scalingData, L"active");
            }
            UpdateScaling();
        });
    }

    WidgetPage::JsonObject WidgetPage::LosslessProfile()
    {
        double index = m_losslessProfile >= 0 ? m_losslessProfile : Num(m_scalingData, L"profileIndex", -1);
        for (auto const& entry : Arr(m_scalingData, L"profiles")) {
            auto profile = entry.GetObject();
            if (Num(profile, L"index", -1) == index) return profile;
        }
        return nullptr;
    }

    // The rows of the open page, from the last reading. Choices carry the exact JSON value the agent takes.
    std::vector<WidgetPage::ScalingRow> WidgetPage::ScalingRows()
    {
        std::vector<ScalingRow> rows;
        auto const& d = m_scalingData;
        bool duck = m_scalingPage == L"lossless";
        bool online = m_agent->Ready() && d && Bool(d, L"available");
        bool active = Bool(d, duck ? L"active" : L"enabled"), pending = Bool(d, L"pending");
        auto error = m_scalingError.empty() ? Str(d, L"error") : m_scalingError;

        ScalingRow hero{ L"hero", L"scale", L"缩放", duck ? L"qamDuck" : L"magpie" };
        hero.on = active || pending;
        hero.off = !online || Bool(d, L"blocked");
        if (!d) hero.detail = m_scalingError.empty() ? L"" : m_scalingError;
        else if (!Bool(d, L"available")) hero.detail = duck ? L"没有找到小黄鸭" : L"Magpie 不可用";
        else if (pending) hero.detail = L"返回游戏后开始";
        else if (!error.empty()) hero.detail = error;
        else if (active && duck) hero.detail = Str(d, L"targetTitle", L"缩放中");
        else if (active) hero.detail = Bool(d, L"active") ? Str(d, L"targetTitle", L"缩放中") : L"返回游戏后生效";
        else hero.detail = L"关";
        rows.push_back(hero);

        auto add = [&](std::wstring kind, std::wstring key, hstring label, hstring icon, bool sub, bool off) -> ScalingRow& {
            ScalingRow row{ std::move(kind), std::move(key), label, icon };
            row.sub = sub; row.off = off || !online;
            rows.push_back(row);
            return rows.back();
        };
        auto group = [&](std::wstring key, hstring label, hstring icon) {
            auto& row = add(L"group", key, label, icon, false, false);
            row.off = !d;
            row.on = m_scalingOpenGroups[key];
            return row.on;
        };

        if (!duck) {
            auto values = Obj(d, L"values");
            bool game = !Str(d, L"game").empty();
            auto pick = [&](std::wstring key, hstring label, hstring icon, bool sub, std::vector<std::pair<IJsonValue, hstring>> const& choices) {
                auto& row = add(L"pick", key, label, icon, sub, !game);
                auto current = Get(values, key);
                for (auto const& [value, text] : choices) {
                    if (current && value.Stringify() == current.Stringify()) row.index = static_cast<int>(row.choices.size());
                    row.choices.push_back({ value, text });
                }
            };
            auto toggle = [&](std::wstring key, hstring label, hstring icon, bool sub, bool invert = false) {
                auto& row = add(L"toggle", key, label, icon, sub, !game);
                row.on = Bool(values, key) != invert;  // shown state
                if (invert) row.detail = L"invert";
            };
            auto numbers = [](std::vector<std::pair<double, hstring>> const& list) {
                std::vector<std::pair<IJsonValue, hstring>> out;
                for (auto const& [v, t] : list) out.push_back({ N(v), t });
                return out;
            };
            {
                auto& row = add(L"pick", L"preset", L"缩放模式", L"qamMode", false, !game);
                auto preset = Str(d, L"preset");
                for (auto const& name : Arr(d, L"presets")) {
                    if (name.GetString() == preset) row.index = static_cast<int>(row.choices.size());
                    row.choices.push_back({ name, name.GetString() });
                }
            }
            pick(L"capture", L"捕获方式", L"qamCapture", false, { { S(L"graphics"), L"Graphics Capture" }, { S(L"desktop"), L"Desktop Duplication" },
                { S(L"gdi"), L"GDI" }, { S(L"dwm"), L"DwmSharedSurface" } });
            {
                auto& row = add(L"toggle", L"auto", L"进游戏自动缩放", L"qamAuto", false, !game);
                row.on = Bool(d, L"auto");
            }
            pick(L"maxFrameRate", L"帧率限制", L"fps", false, numbers({ { 0, L"不限" }, { 30, L"30" }, { 40, L"40" }, { 45, L"45" }, { 60, L"60" },
                { 72, L"72" }, { 90, L"90" }, { 120, L"120" }, { 144, L"144" } }));
            toggle(L"game3D", L"3D 游戏模式", L"qamCube", false);
            if (group(L"cursor", L"光标", L"qamCursor")) {
                pick(L"cursorScaling", L"光标缩放", L"", true, numbers({ { .5, L"0.5×" }, { .75, L"0.75×" }, { 1, L"不缩放" }, { 1.25, L"1.25×" },
                    { 1.5, L"1.5×" }, { 2, L"2×" }, { 0, L"和源窗口相同" } }));
                pick(L"cursorInterpolation", L"插值算法", L"", true, numbers({ { 0, L"最近邻" }, { 1, L"双线性" } }));
                toggle(L"adjustCursor", L"缩放时调整光标速度", L"", true);
                pick(L"autoHideCursorDelay", L"自动隐藏光标", L"", true, numbers({ { 0, L"关" }, { 1, L"1 秒" }, { 2, L"2 秒" }, { 3, L"3 秒" }, { 5, L"5 秒" } }));
            }
            if (group(L"advanced", L"高级", L"qamAdvanced")) {
                auto crop = numbers({ { 0, L"0" }, { 4, L"4 像素" }, { 8, L"8 像素" }, { 16, L"16 像素" }, { 32, L"32 像素" }, { 64, L"64 像素" } });
                pick(L"cropX", L"左右裁剪", L"", true, crop);
                pick(L"cropY", L"上下裁剪", L"", true, crop);
                toggle(L"captureTitleBar", L"捕获标题栏", L"", true);
                toggle(L"disableDirectFlip", L"DirectFlip", L"", true, true);
                toggle(L"simulateExclusiveFullscreen", L"模拟独占全屏", L"", true);
                pick(L"toolbar", L"叠加层工具栏", L"", true, numbers({ { 0, L"关" }, { 1, L"始终显示" }, { 2, L"自动隐藏" } }));
                pick(L"duplicateFrameDetection", L"重复帧检测", L"", true, numbers({ { 0, L"总是" }, { 1, L"动态" }, { 2, L"从不" } }));
                pick(L"multiMonitorUsage", L"多显示器", L"", true, numbers({ { 0, L"最近的显示器" }, { 1, L"相交的显示器" }, { 2, L"所有显示器" } }));
            }
            return rows;
        }

        auto profile = LosslessProfile();
        bool locked = active || pending || !profile;
        auto value = [&](std::wstring const& key) { return Get(profile, L"values." + key); };
        auto text = [&](std::wstring const& key) { auto v = value(key); return v && v.ValueType() == JsonValueType::String ? v.GetString() : hstring(); };
        auto pick = [&](std::wstring key, hstring label, hstring icon, bool sub, bool off = false) {
            auto choices = Arr(d, L"choices." + key);
            bool flag = choices.Size() == 2 && Get(choices.GetObjectAt(0), L"value").ValueType() == JsonValueType::Boolean;
            auto& row = add(flag ? L"toggle" : L"pick", key, label, icon, sub, locked || off);
            auto current = value(key);
            for (auto const& entry : choices) {
                auto choice = entry.GetObject();
                auto v = Get(choice, L"value");
                if (current && v.Stringify() == current.Stringify()) row.index = static_cast<int>(row.choices.size());
                row.choices.push_back({ v, Str(choice, L"label") });
            }
            if (flag) row.on = current && current.ValueType() == JsonValueType::Boolean && current.GetBoolean();
        };
        {
            auto& row = add(L"pick", L"profile", L"档案", L"qamProfile", false, active || pending);
            auto selected = Num(profile, L"index", -1);
            for (auto const& entry : Arr(d, L"profiles")) {
                auto p = entry.GetObject();
                if (Num(p, L"index", -1) == selected) row.index = static_cast<int>(row.choices.size());
                row.choices.push_back({ N(Num(p, L"index", -1)), Str(p, L"label") });
            }
        }
        pick(L"ScalingType", L"缩放算法", L"qamAlgorithm", false);
        auto type = text(L"ScalingType");
        if (type == L"LS1") pick(L"LS1Sharpness", L"锐化强度", L"", true);
        else if (type == L"FSR" || type == L"NIS" || type == L"BicubicCAS") pick(L"Sharpness", L"锐化强度", L"", true);
        pick(L"FrameGeneration", L"帧生成", L"qamFrames", false);
        auto fg = text(L"FrameGeneration");
        bool lsfg3 = fg == L"LSFG3", frames = lsfg3 || fg == L"LSFG2";
        pick(L"LSFG3Mode1", L"模式", L"", true, !lsfg3);
        if (fg == L"LSFG2") pick(L"LSFG2Mode", L"倍率", L"", true);
        else if (lsfg3 && text(L"LSFG3Mode1") == L"ADAPTIVE") pick(L"LSFG3Target", L"目标帧率", L"", true);
        else pick(L"LSFG3Multiplier", L"倍率", L"", true, !lsfg3);
        pick(L"LSFGSize", L"性能模式", L"", true, !frames);
        if (group(L"output", L"捕获与输出", L"qamOutput")) {
            pick(L"ScalingFitMode", L"画面适配", L"", true);
            pick(L"LSFGFlowScale", L"光流比例", L"", true, !frames);
            pick(L"CaptureApi", L"捕获 API", L"", true);
            pick(L"SyncMode", L"同步模式", L"", true);
            pick(L"MaxFrameLatency", L"最大帧延迟", L"", true);
            pick(L"HdrSupport", L"HDR 支持", L"", true);
            pick(L"DrawFps", L"显示帧率", L"", true);
            pick(L"ClipCursor", L"限制光标在画面内", L"", true);
            pick(L"WindowedMode", L"窗口化输出", L"", true);
        }
        auto& open = add(L"action", L"open", L"打开小黄鸭", L"qamOpen", false, pending);
        open.off = !online || pending;
        return rows;
    }

    // One row: [icon] label [detail] … value / switch / count. The focus mark is a short bar on the left.
    void WidgetPage::BuildScalingRow(ScalingRow const& row)
    {
        Button button;
        button.Style(Resources().Lookup(box_value(L"ScalingRow")).as<Windows::UI::Xaml::Style>());
        button.Tag(box_value(hstring(row.key)));
        bool hero = row.kind == L"hero";
        button.MinHeight(Tok(hero ? L"ScalingHeroHeight" : row.sub ? L"ScalingSubRowHeight" : L"ScalingRowHeight"));
        if (row.sub) {
            button.Padding(Application::Current().Resources().Lookup(box_value(L"ScalingSubRowInset")).as<Thickness>());
            button.Background(SolidColorBrush(ColorHelper::FromArgb(9, 255, 255, 255)));
        }
        button.IsEnabled(!row.off);
        Automation::AutomationProperties::SetName(button, row.label);

        Grid content;
        content.ColumnSpacing(Tok(L"SpaceM"));
        for (auto width : { GridLengthHelper::Auto(), Star(), GridLengthHelper::Auto() }) {
            ColumnDefinition column; column.Width(width); content.ColumnDefinitions().Append(column);
        }
        Shapes::Rectangle mark;
        mark.Width(Tok(L"FocusBarWidth")); mark.Height(Tok(L"FocusBarHeight") * .67);
        mark.RadiusX(Tok(L"RadiusBar")); mark.RadiusY(Tok(L"RadiusBar"));
        mark.Fill(Resource(L"AccentFillColorDefaultBrush"));
        mark.HorizontalAlignment(HorizontalAlignment::Left);
        mark.Margin(Thickness{ -(row.sub ? Tok(L"ScalingSubMarkOffset") : Tok(L"ScalingMarkOffset")), 0, 0, 0 });
        mark.Opacity(0);
        content.Children().Append(mark);

        if (hero) {
            Border box;
            box.Width(Tok(L"HeroIconBox")); box.Height(Tok(L"HeroIconBox"));
            box.CornerRadius(CornerRadiusHelper::FromUniformRadius(Tok(L"RadiusTile")));
            box.Background(row.on ? Resource(L"AccentFillColorDefaultBrush") : Resource(L"ButtonBackground"));
            box.Child(Icon(row.icon.c_str(), Tok(L"IconContent"), row.on ? SolidColorBrush(Colors::Black()) : nullptr));
            content.Children().Append(box);
        } else if (!row.icon.empty()) {
            auto icon = Icon(row.icon.c_str(), Tok(L"IconContent"), Resource(L"TextFillColorSecondaryBrush"));
            content.Children().Append(icon);
        }

        StackPanel text;
        text.VerticalAlignment(VerticalAlignment::Center);
        TextBlock label;
        label.Text(row.label);
        label.TextTrimming(TextTrimming::CharacterEllipsis);
        if (hero) { label.FontSize(Tok(L"TypeSubtitleSize")); label.FontWeight(Text::FontWeights::SemiBold()); }
        text.Children().Append(label);
        if (hero && !row.detail.empty()) {
            TextBlock detail;
            detail.Text(row.detail);
            detail.FontSize(Tok(L"TypeLabelSize"));
            detail.TextTrimming(TextTrimming::CharacterEllipsis);
            detail.Foreground(Resource(L"TextFillColorSecondaryBrush"));
            text.Children().Append(detail);
        }
        Grid::SetColumn(text, 1);
        content.Children().Append(text);

        StackPanel tail;
        tail.Orientation(Orientation::Horizontal);
        tail.Spacing(Tok(L"SpaceS"));
        tail.VerticalAlignment(VerticalAlignment::Center);
        auto secondary = Resource(L"TextFillColorSecondaryBrush");
        if (row.kind == L"pick") {
            tail.Spacing(0);
            tail.Children().Append(Icon(L"chevronLeft", Tok(L"IconMark"), secondary));
            TextBlock value;
            value.Text(row.index >= 0 ? row.choices[row.index].label : hstring(L"—"));
            value.Width(Tok(L"ScalingValueWidth"));
            value.TextAlignment(TextAlignment::Center);
            value.TextTrimming(TextTrimming::CharacterEllipsis);
            value.VerticalAlignment(VerticalAlignment::Center);
            tail.Children().Append(value);
            tail.Children().Append(Icon(L"chevronRight", Tok(L"IconMark"), secondary));
        } else if (row.kind == L"toggle" || hero) {
            bool on = row.on;
            if (!hero) {
                TextBlock state; state.Text(on ? L"开" : L"关"); state.Foreground(secondary);
                state.VerticalAlignment(VerticalAlignment::Center);
                tail.Children().Append(state);
            }
            Border track;
            track.Width(Tok(L"SwitchWidth")); track.Height(Tok(L"SwitchHeight"));
            track.CornerRadius(CornerRadiusHelper::FromUniformRadius(Tok(L"SwitchHeight") / 2));
            track.BorderThickness(Thickness{ 1, 1, 1, 1 });
            track.BorderBrush(on ? Resource(L"AccentFillColorDefaultBrush") : SolidColorBrush(ColorHelper::FromArgb(153, 255, 255, 255)));
            track.Background(on ? Resource(L"AccentFillColorDefaultBrush") : SolidColorBrush(Colors::Transparent()));
            Shapes::Ellipse knob;
            knob.Width(Tok(L"SwitchKnob")); knob.Height(Tok(L"SwitchKnob"));
            knob.Fill(on ? SolidColorBrush(Colors::Black()) : SolidColorBrush(ColorHelper::FromArgb(200, 255, 255, 255)));
            knob.HorizontalAlignment(on ? HorizontalAlignment::Right : HorizontalAlignment::Left);
            knob.Margin(Thickness{ Tok(L"SpaceXS") - 1, 0, Tok(L"SpaceXS") - 1, 0 });
            track.Child(knob);
            tail.Children().Append(track);
        } else if (row.kind == L"group") {
            tail.Children().Append(Icon(row.on ? L"chevronUp" : L"chevronDown", Tok(L"IconMark"), secondary));
        } else if (row.kind == L"action") {
            tail.Children().Append(Icon(L"openOut", Tok(L"IconMark"), secondary));
        }
        Grid::SetColumn(tail, 2);
        content.Children().Append(tail);
        button.Content(content);

        auto key = row.key;
        button.GotFocus([mark](auto&&, auto&&) { mark.Opacity(1); });
        button.LostFocus([mark](auto&&, auto&&) { mark.Opacity(0); });
        button.Click([this, key](auto&&, auto&&) { ScalingPress(key); });
        ScalingList().Children().Append(button);
    }

    void WidgetPage::UpdateScaling()
    {
        if (m_scalingPage.empty()) return;
        ScalingGame().Text(Str(m_scalingData, m_scalingPage == L"lossless" ? L"targetTitle" : L"game"));
        if (m_scalingPage == L"magpie") {
            std::wstring game = Str(m_scalingData, L"game").c_str();
            auto slash = game.find_last_of(L"\\/");
            ScalingGame().Text(slash == std::wstring::npos ? game : game.substr(slash + 1));
        }
        auto rows = ScalingRows();
        std::wstring signature;
        for (auto const& row : rows)
            signature += row.kind + L"|" + row.key + L"|" + to_hstring(row.index).c_str() + L"|" + (row.on ? L"1" : L"0")
                + (row.off ? L"x" : L"") + row.detail.c_str() + L";";
        if (signature == m_scalingSignature.c_str()) return;
        m_scalingSignature = signature;
        // Rebuild, keeping focus on the same row (or the first usable one when the page opens).
        std::wstring focused;
        if (auto element = FocusManager::GetFocusedElement().try_as<Button>(); element && element.Tag())
            if (auto tag = element.Tag().try_as<hstring>()) focused = tag->c_str();
        bool inside = false;
        for (auto const& child : ScalingList().Children()) if (child == FocusManager::GetFocusedElement()) inside = true;
        bool first = m_scalingRows.empty();
        m_scalingRows = rows;
        ScalingList().Children().Clear();
        for (auto const& row : m_scalingRows) BuildScalingRow(row);
        if (inside || first) {
            Button target{ nullptr };
            for (auto const& child : ScalingList().Children()) {
                auto button = child.as<Button>();
                if (!button.IsEnabled()) continue;
                if (!target) target = button;
                if (inside && unbox_value<hstring>(button.Tag()) == hstring(focused)) { target = button; break; }
            }
            if (target) target.Focus(FocusState::Keyboard);
            else ScalingBack().Focus(FocusState::Programmatic);
        }
    }

    // A on a row: the scaling switch starts / stops (it begins once Game Bar closes and the game is in front),
    // a switch flips, a choice row lists its choices, a group opens or closes.
    void WidgetPage::ScalingPress(std::wstring const& key)
    {
        auto at = std::find_if(m_scalingRows.begin(), m_scalingRows.end(), [&](auto const& r) { return r.key == key; });
        if (at == m_scalingRows.end() || at->off || m_scalingSending) return;
        auto const& row = *at;
        if (row.kind == L"hero") {
            bool duck = m_scalingPage == L"lossless";
            auto action = Bool(m_scalingData, L"pending") ? L"cancel" : Bool(m_scalingData, duck ? L"active" : L"enabled") ? L"stop" : L"start";
            SendScaling(L"qam_scaling_action", Args({ S(m_scalingPage), S(action) }));
        }
        else if (row.kind == L"group") { m_scalingOpenGroups[key] = !m_scalingOpenGroups[key]; UpdateScaling(); }
        else if (row.kind == L"action") SendScaling(L"open_lossless");
        else if (row.kind == L"toggle") {
            if (key == L"auto") { JsonObject patch; patch.SetNamedValue(L"auto", B(!row.on)); SendScaling(L"qam_magpie_set", Args({ patch })); }
            else if (row.choices.size() == 2) ScalingChoose(key, row.on ? 0 : 1);
            else { JsonObject patch; patch.SetNamedValue(key, B(row.detail == L"invert" ? row.on : !row.on)); SendScaling(L"qam_magpie_set", Args({ patch })); }
        }
        else if (row.kind == L"pick") ScalingFlyout(key);
    }

    void WidgetPage::ScalingStep(std::wstring const& key, int step)
    {
        auto at = std::find_if(m_scalingRows.begin(), m_scalingRows.end(), [&](auto const& r) { return r.key == key; });
        if (at == m_scalingRows.end() || at->off || at->kind != L"pick" || at->choices.empty()) return;
        int next = std::clamp(std::max(at->index, 0) + step, 0, static_cast<int>(at->choices.size()) - 1);
        if (next != at->index) ScalingChoose(key, next);
    }

    void WidgetPage::ScalingChoose(std::wstring const& key, int index)
    {
        auto at = std::find_if(m_scalingRows.begin(), m_scalingRows.end(), [&](auto const& r) { return r.key == key; });
        if (at == m_scalingRows.end() || index < 0 || index >= static_cast<int>(at->choices.size()) || m_scalingSending) return;
        auto value = at->choices[index].value;
        if (key == L"profile") { m_losslessProfile = value.GetNumber(); UpdateScaling(); return; }
        // Show the new value at once; the reply brings the saved state.
        at->index = index;
        if (m_scalingPage == L"lossless") {
            auto profile = LosslessProfile();
            if (!profile) return;
            if (auto values = Obj(profile, L"values")) values.SetNamedValue(key, value);
            SendScaling(L"qam_lossless_set", Args({ N(Num(profile, L"index")), S(key), value }));
        } else {
            JsonObject patch;
            patch.SetNamedValue(key, value);
            if (key == L"preset") m_scalingData.SetNamedValue(L"preset", value);
            else if (auto values = Obj(m_scalingData, L"values")) values.SetNamedValue(key, value);
            SendScaling(L"qam_magpie_set", Args({ patch }));
        }
        UpdateScaling();
    }

    void WidgetPage::ScalingFlyout(std::wstring const& key)
    {
        auto at = std::find_if(m_scalingRows.begin(), m_scalingRows.end(), [&](auto const& r) { return r.key == key; });
        if (at == m_scalingRows.end()) return;
        Button anchor{ nullptr };
        for (auto const& child : ScalingList().Children())
            if (auto button = child.as<Button>(); unbox_value<hstring>(button.Tag()) == hstring(key)) anchor = button;
        if (!anchor) return;
        MenuFlyout menu;
        menu.Placement(Primitives::FlyoutPlacementMode::Bottom);
        for (int i = 0; i < static_cast<int>(at->choices.size()); ++i) {
            ToggleMenuFlyoutItem item;
            item.Text(at->choices[i].label);
            item.IsChecked(i == at->index);
            item.Click([this, key, i](auto&&, auto&&) { ScalingChoose(key, i); });
            menu.Items().Append(item);
        }
        menu.ShowAt(anchor);
    }

    void WidgetPage::SendScaling(std::wstring const& method, JsonArray const& args)
    {
        if (m_scalingSending || m_scalingPage.empty() || !m_agent->Ready()) return;
        m_scalingSending = true;
        m_scalingError.clear();
        auto page = m_scalingPage;
        m_agent->Call(method, args, [this, page](auto const& value, hstring const& error) {
            m_scalingSending = false;
            if (m_shutdown || page != m_scalingPage) return;
            if (!error.empty()) m_scalingError = error;
            else if (value && value.ValueType() == JsonValueType::Object && value.GetObject().HasKey(L"available"))
                m_scalingData = value.GetObject();
            m_scalingAt = {};
            RefreshScaling();
            UpdateScaling();
        });
    }

    void WidgetPage::BuildTiles()
    {
        Tiles().Children().Clear();
        Tiles().RowDefinitions().Clear();
        Tiles().ColumnDefinitions().Clear();
        m_views.clear();
        for (int c = 0; c < TileColumns; c++) { ColumnDefinition column; column.Width(Star()); Tiles().ColumnDefinitions().Append(column); }
        for (int r = 0; r < TileRows; r++) { RowDefinition row; row.Height(Star()); Tiles().RowDefinitions().Append(row); }
        for (size_t i = 0; i < m_tiles.size(); i++)
        {
            auto id = m_tiles[i];
            TileView view;
            view.button = Button();
            auto& b = view.button;
            b.HorizontalAlignment(HorizontalAlignment::Stretch); b.VerticalAlignment(VerticalAlignment::Stretch);
            b.HorizontalContentAlignment(HorizontalAlignment::Stretch); b.VerticalContentAlignment(VerticalAlignment::Stretch);
            b.Padding(m_narrow ? Thickness{ 10, 10, 10, 10 } : Thickness{ 12, 12, 12, 12 });
            Grid content;
            RowDefinition top, name, value; top.Height(Star()); name.Height(GridLengthHelper::Auto()); value.Height(GridLengthHelper::Auto());
            content.RowDefinitions().Append(top); content.RowDefinitions().Append(name); content.RowDefinitions().Append(value);
            view.top = Grid();
            view.top.VerticalAlignment(VerticalAlignment::Top);
            auto icon = Icon(Names.at(id).second);
            icon.HorizontalAlignment(HorizontalAlignment::Left);
            view.top.Children().Append(icon);
            view.dots = StackPanel(); view.dots.Orientation(Orientation::Horizontal); view.dots.Spacing(3);
            view.dots.HorizontalAlignment(HorizontalAlignment::Right); view.dots.VerticalAlignment(VerticalAlignment::Center);
            view.top.Children().Append(view.dots);
            auto title = view.title = TextBlock(); title.Text(Names.at(id).first); title.FontWeight(Text::FontWeights::SemiBold()); title.Margin(Thickness{ 0, 8, 0, 0 });
            view.value = TextBlock(); view.value.TextTrimming(TextTrimming::CharacterEllipsis);
            Grid::SetRow(title, 1); Grid::SetRow(view.value, 2);
            content.Children().Append(view.top); content.Children().Append(title); content.Children().Append(view.value);
            b.Content(content);
            if (id == L"osd") AddOsdOptions(b);
            b.Click([this, id](IInspectable const&, RoutedEventArgs const&) { Press(id); });
            Grid::SetRow(b, static_cast<int>(i / TileColumns)); Grid::SetColumn(b, static_cast<int>(i % TileColumns));
            Tiles().Children().Append(b);
            m_views[id] = view;
        }
    }

    void WidgetPage::AddOsdOptions(Button const& button)
    {
        MenuFlyout menu;
        auto weak = get_weak();
        auto apply = [weak](wchar_t const* key, IJsonValue const& value) {
            if (auto self = weak.get()) {
                JsonObject settings; settings.SetNamedValue(key, value);
                self->Send(L"osd", L"set_osd_preferences", Args({ settings }));
            }
        };
        auto item = [](auto const& parent, wchar_t const* name, std::function<void()> action) {
            MenuFlyoutItem entry; entry.Text(name);
            entry.Click([action](auto&&, auto&&) { action(); }); parent.Items().Append(entry);
        };
        MenuFlyoutSubItem level; level.Text(L"详细程度");
        item(level, L"关闭", [apply] { apply(L"enabled", B(false)); });
        for (int n = 1; n <= 4; ++n) {
            auto name = std::vector<hstring>{ L"FPS 数字", L"横向摘要", L"标准列表", L"完整监控" }[n - 1];
            item(level, name.c_str(), [weak, n] {
                if (auto self = weak.get()) {
                    JsonObject settings; settings.SetNamedValue(L"enabled", B(true)); settings.SetNamedValue(L"level", N(n));
                    self->Send(L"osd", L"set_osd_preferences", Args({ settings }));
                }
            });
        }
        menu.Items().Append(level);
        MenuFlyoutSubItem size; size.Text(L"字号");
        item(size, L"小", [apply] { apply(L"size", S(L"small")); });
        item(size, L"中", [apply] { apply(L"size", S(L"medium")); });
        item(size, L"大", [apply] { apply(L"size", S(L"large")); });
        menu.Items().Append(size);
        MenuFlyoutSubItem scale; scale.Text(L"缩放");
        for (int n : { 75, 100, 125, 150, 175, 200 }) {
            auto label = to_hstring(n) + L"%";
            item(scale, label.c_str(), [apply, n] { apply(L"scale", N(n)); });
        }
        menu.Items().Append(scale);
        MenuFlyoutSubItem position; position.Text(L"显示位置");
        std::vector<hstring> positions{ L"左上", L"上方居中", L"右上", L"左侧居中", L"屏幕居中", L"右侧居中", L"左下", L"下方居中", L"右下" };
        for (int n = 0; n < 9; ++n) item(position, positions[n].c_str(), [apply, n] { apply(L"position", N(n)); });
        menu.Items().Append(position);
        button.ContextFlyout(menu);
        ToolTipService::SetToolTip(button, box_value(L"单击：关闭／1—4 档；右键：显示位置与字号设置"));
    }

    void WidgetPage::BuildEditTiles()
    {
        auto fill = [this](Grid grid, std::vector<std::wstring> const& ids, bool pinned) {
            grid.Children().Clear(); grid.RowDefinitions().Clear(); grid.ColumnDefinitions().Clear();
            for (int c = 0; c < TileColumns; c++) { ColumnDefinition column; column.Width(Star()); grid.ColumnDefinitions().Append(column); }
            for (size_t r = 0; r < (ids.size() + TileColumns - 1) / TileColumns; r++) { RowDefinition row; row.Height(GridLengthHelper::FromPixels(96)); grid.RowDefinitions().Append(row); }
            for (size_t i = 0; i < ids.size(); i++)
            {
                auto id = ids[i];
                bool moving = pinned && id == m_moving;
                Button b;
                b.Tag(box_value(hstring(id)));
                b.HorizontalAlignment(HorizontalAlignment::Stretch); b.VerticalAlignment(VerticalAlignment::Stretch);
                b.HorizontalContentAlignment(HorizontalAlignment::Stretch); b.VerticalContentAlignment(VerticalAlignment::Stretch);
                b.Padding(m_narrow ? Thickness{ 10, 10, 10, 10 } : Thickness{ 12, 12, 12, 12 });
                if (!pinned) { b.Background(SolidColorBrush(Colors::Transparent())); b.BorderBrush(Resource(L"ControlStrongStrokeColorDefaultBrush")); }
                if (moving) { TranslateTransform lift; lift.Y(-4); b.RenderTransform(lift); }
                Grid content;
                RowDefinition top, name, value; top.Height(Star()); name.Height(GridLengthHelper::Auto()); value.Height(GridLengthHelper::Auto());
                content.RowDefinitions().Append(top); content.RowDefinitions().Append(name); content.RowDefinitions().Append(value);
                Grid head;
                auto icon = Icon(Names.at(id).second); icon.HorizontalAlignment(HorizontalAlignment::Left); icon.VerticalAlignment(VerticalAlignment::Top);
                head.Children().Append(icon);
                auto badge = Icon(moving ? L"move" : pinned ? L"minus" : L"plus", 14);
                badge.HorizontalAlignment(HorizontalAlignment::Right); badge.VerticalAlignment(VerticalAlignment::Top);
                head.Children().Append(badge);
                TextBlock title; title.Text(Names.at(id).first); title.FontWeight(Text::FontWeights::SemiBold()); title.Margin(Thickness{ 0, 8, 0, 0 });
                TextBlock sub; sub.Foreground(Resource(L"TextFillColorSecondaryBrush")); sub.TextTrimming(TextTrimming::CharacterEllipsis);
                sub.Text(pinned ? (m_data ? TileLevel(id).value : hstring()) : hstring(Hints.count(id) ? Hints.at(id) : L""));
                Grid::SetRow(title, 1); Grid::SetRow(sub, 2);
                content.Children().Append(head); content.Children().Append(title); content.Children().Append(sub);
                b.Content(content);
                b.Click([this, id, pinned](IInspectable const&, RoutedEventArgs const&) {
                    if (pinned)
                    {
                        if (!m_moving.empty()) { m_moving.clear(); BuildEditTiles(); return; }
                        m_tiles.erase(std::remove(m_tiles.begin(), m_tiles.end(), id), m_tiles.end());
                    }
                    else {
                        if (m_tiles.size() >= MaxPinnedTiles) return;
                        m_tiles.push_back(id);
                    }
                    JsonArray list; for (auto& t : m_tiles) list.Append(S(t));
                    ApplicationData::Current().LocalSettings().Values().Insert(L"tiles", box_value(list.Stringify()));
                    BuildEditTiles();
                });
                b.IsEnabled(pinned || m_tiles.size() < MaxPinnedTiles);
                Grid::SetRow(b, static_cast<int>(i / TileColumns)); Grid::SetColumn(b, static_cast<int>(i % TileColumns));
                grid.Children().Append(b);
            }
        };
        std::vector<std::wstring> available;
        for (auto const& list : { DefaultTiles, Pool })
            for (auto const& id : list) if (std::find(m_tiles.begin(), m_tiles.end(), id) == m_tiles.end()) available.push_back(id);
        fill(PinnedTiles(), m_tiles, true);
        fill(AvailableTiles(), available, false);
        PinnedCount().Text(to_hstring(static_cast<int>(m_tiles.size())) + L" 个");
        AvailableHint().Text(m_tiles.size() >= MaxPinnedTiles ? L"先移除一个已固定磁贴" : L"按 A 固定");
        AvailableHeader().Visibility(available.empty() ? Visibility::Collapsed : Visibility::Visible);
        // Keep the controller on the tile being moved.
        if (!m_moving.empty())
            for (auto const& child : PinnedTiles().Children())
                if (auto b = child.try_as<Button>(); b && unbox_value<hstring>(b.Tag()) == hstring(m_moving)) b.Focus(FocusState::Programmatic);
    }

    winrt::fire_and_forget WidgetPage::LoadCache()
    {
        auto strong = get_strong();
        try
        {
            auto file = co_await ApplicationData::Current().LocalFolder().GetFileAsync(L"state.json");
            auto text = co_await FileIO::ReadTextAsync(file);
            JsonObject cached{ nullptr };
            if (!m_data && JsonObject::TryParse(text, cached))
            {
                m_data = cached;
                m_draft = Clone(Obj(m_data, L"power.settings"));
                Update();
            }
        }
        catch (...) {}  // first run: nothing cached yet
    }

    winrt::fire_and_forget WidgetPage::SaveCache()
    {
        auto strong = get_strong();
        auto text = m_data.Stringify();
        try
        {
            auto file = co_await ApplicationData::Current().LocalFolder().CreateFileAsync(L"state.json", CreationCollisionOption::ReplaceExisting);
            co_await FileIO::WriteTextAsync(file, text);
            // Keep the host viewport and measured control bounds available for
            // a standalone preview at exactly the same size as Game Bar.
            JsonObject layout;
            layout.SetNamedValue(L"width", N(ActualWidth()));
            layout.SetNamedValue(L"height", N(ActualHeight()));
            layout.SetNamedValue(L"gameBar", B(m_widget != nullptr));
            auto slider = PowerSlider();
            auto bounds = slider.TransformToVisual(*this).TransformPoint(Point{});
            JsonObject powerSlider;
            powerSlider.SetNamedValue(L"x", N(bounds.X));
            powerSlider.SetNamedValue(L"y", N(bounds.Y));
            powerSlider.SetNamedValue(L"width", N(slider.ActualWidth()));
            powerSlider.SetNamedValue(L"minimum", N(slider.Minimum()));
            powerSlider.SetNamedValue(L"maximum", N(slider.Maximum()));
            powerSlider.SetNamedValue(L"value", N(slider.Value()));
            layout.SetNamedValue(L"powerSlider", powerSlider);
            auto layoutFile = co_await ApplicationData::Current().LocalFolder().CreateFileAsync(
                m_widget ? L"gamebar-layout.json" : L"preview-layout.json", CreationCollisionOption::ReplaceExisting);
            co_await FileIO::WriteTextAsync(layoutFile, layout.Stringify());
        }
        catch (...) {}
    }

    // ---- data ----------------------------------------------------------------------------------
    void WidgetPage::Refresh()
    {
        if (m_shutdown) return;
        if (!m_agent || !m_agent->Ready()) return;
        if (m_target) m_target->Report(*m_agent);
        m_agent->Call(L"widget_state", JsonArray(), [this](IJsonValue const& result, hstring const& error) {
            if (!error.empty() || result.ValueType() != JsonValueType::Object) return;
            Accept(result.GetObject());
        });
    }

    void WidgetPage::Accept(JsonObject const& data)
    {
        ApplyHolds(data);
        m_data = data;
        if (!m_saveTimer.IsEnabled() && Clock::now() > m_draftHeldUntil) m_draft = Clone(Obj(m_data, L"power.settings"));
        if (Clock::now() - m_lastCache > std::chrono::seconds(5)) { m_lastCache = Clock::now(); SaveCache(); }
        Update();
    }

    // Values the user just changed win over readings until the agent has handled them (and a moment
    // after): a reading taken before the change must not pull the tile back.
    void WidgetPage::HoldValue(std::wstring const& path, IJsonValue const& value, std::wstring const& key)
    {
        if (m_data) SetPath(m_data, path, value);
        m_holds[path] = { value, key, Clock::time_point::max() };
    }

    void WidgetPage::ApplyHolds(JsonObject const& data)
    {
        auto now = Clock::now();
        for (auto it = m_holds.begin(); it != m_holds.end();)
        {
            if (it->second.until > now) { SetPath(data, it->first, it->second.value); ++it; }
            else it = m_holds.erase(it);
        }
    }

    void WidgetPage::ReleaseHold(std::wstring const& key)
    {
        auto until = Clock::now() + std::chrono::milliseconds(1500);
        for (auto& [path, hold] : m_holds) if (hold.key == key) hold.until = until;
        if (key == L"power") m_draftHeldUntil = until;
    }

    // One request per control in flight; presses made meanwhile collapse into the latest value.
    void WidgetPage::Send(std::wstring const& key, std::wstring const& method, JsonArray const& args)
    {
        auto found = m_sending.find(key);
        if (found != m_sending.end()) { found->second = Pending{ method, args }; return; }
        m_sending[key] = std::nullopt;
        Dispatch(key, Pending{ method, args });
    }

    void WidgetPage::Dispatch(std::wstring const& key, Pending const& pending)
    {
        m_agent->Call(pending.method, pending.args, [this, key](IJsonValue const&, hstring const& error) {
            if (!error.empty()) Flash(error);
            auto found = m_sending.find(key);
            if (found != m_sending.end() && found->second)
            {
                auto next = *found->second;
                found->second = std::nullopt;
                Dispatch(key, next);
                return;
            }
            m_sending.erase(key);
            ReleaseHold(key);
            if (key == L"game-freeze" || key == L"fse") Refresh();
        });
    }

    // Power settings: the draft changes at once; the agent gets the latest draft 200 ms after the
    // last press, and readings do not replace the draft until it has been handled.
    void WidgetPage::EditPower()
    {
        m_draftHeldUntil = Clock::time_point::max();
        m_saveTimer.Stop();
        m_saveTimer.Start();
    }

    void WidgetPage::Flash(hstring const& text)
    {
        MessageBar().Message(text);
        MessageBar().IsOpen(true);
        ShowBanners();
        m_flashTimer.Stop();
        m_flashTimer.Start();
    }

    // ---- tiles ---------------------------------------------------------------------------------
    WidgetPage::Level WidgetPage::TileLevel(std::wstring const& id)
    {
        Level level;
        auto const& d = m_draft;
        if (!m_data || !d) return level;
        if (id == L"power")
        {
            auto quick = Arr(d, L"quick");
            bool enabled = Bool(d, L"enabled");
            auto mode = Str(d, L"mode");
            double tdp = Num(d, L"manual.tdp");
            int at = -1;
            for (uint32_t i = 0; i < quick.Size(); i++) if (quick.GetNumberAt(i) == tdp) at = static_cast<int>(i);
            level.count = static_cast<int>(quick.Size()) + 2;
            level.index = !enabled ? 0 : mode == L"autotdp" ? 1 : (mode == L"manual" && at >= 0) ? 2 + at : 1;
            level.value = !enabled ? hstring(L"系统控制") : mode == L"autotdp" ? hstring(L"Auto")
                : mode == L"manual" ? Fmt(tdp, 0) + L" W" : L"预设 · " + Fmt(Num(m_data, L"power.target.pl1"), 0) + L" W";
        }
        else if (id == L"fan")
        {
            auto mode = Str(d, L"fan.mode");
            level.count = static_cast<int>(FanSteps.size()); level.index = 0;
            for (int i = 0; i < level.count; i++) if (mode == FanSteps[i].first) level.index = i;
            level.value = FanSteps[level.index].second;
        }
        else if (id == L"fps")
        {
            level.disabled = !Has(m_data, L"system.fpsLimit");
            double v = Num(m_data, L"system.fpsLimit");
            level.count = static_cast<int>(FpsSteps.size()); level.index = Nearest(FpsSteps, v);
            level.value = v ? Fmt(v, 0) + L" 帧" : hstring(L"不限");
        }
        else if (id == L"refresh")
        {
            double v = Num(m_data, L"system.refresh");
            std::vector<int> rates;
            for (auto const& r : Arr(m_data, L"system.refreshRates")) if (r.GetNumber() >= 60) rates.push_back(static_cast<int>(r.GetNumber()));
            level.count = std::max(1, static_cast<int>(rates.size()));
            level.index = 0;
            for (int i = 0; i < static_cast<int>(rates.size()); i++) if (rates[i] == v) level.index = i;
            level.value = v ? Fmt(v, 0) + L" Hz" : hstring(L"--");
            level.disabled = rates.size() < 2;
        }
        else if (id == L"osd")
        {
            int value = Bool(d, L"osd.enabled") ? static_cast<int>(Num(d, L"osd.level")) : 0;
            level.count = 5; level.index = value;
            level.value = value ? to_hstring(value) + L" 档" : hstring(L"关");
        }
        else if (id == L"magpie") level.value = Bool(m_data, L"magpie.active") ? L"缩放中" : Bool(m_data, L"magpie.enabled") ? L"返回游戏后生效" : L"关";
        else if (id == L"lossless") level.value = m_losslessActive ? L"缩放中" : L"关";
        else if (id == L"fse")
        {
            level.value = Bool(m_data, L"xboxFse") ? L"退出全屏体验" : L"大屏幕主页";
            level.disabled = m_sending.count(L"fse") != 0;
        }
        else if (id == L"rumble")
        {
            double v = Has(m_data, L"rumble.strength") ? Num(m_data, L"rumble.strength") : 100;
            std::vector<int> steps; for (auto& s : RumbleSteps) steps.push_back(s.first);
            level.count = 4; level.index = Nearest(steps, v); level.value = RumbleSteps[level.index].second;
        }
        else if (id == L"keyboard") level.value = L"打开";
        else if (id == L"close")
        {
            level.disabled = !Has(m_data, L"system.game");
            level.value = Str(m_data, L"system.game.title", L"没有游戏");
        }
        else if (id == L"target")
        {
            bool autoMode = Str(d, L"mode") == L"autotdp";
            if (autoMode)
            {
                double target = Num(d, L"autotdp.target", 60);
                level.count = static_cast<int>(AutoTargets.size()); level.index = Nearest(AutoTargets, target);
                level.value = Fmt(target, 0) + L" 帧";
                level.disabled = !Bool(d, L"enabled");
            }
            else
            {
                level.value = L"Auto 模式可调";
                level.disabled = true;
            }
        }
        else if (id == L"cpu")
        {
            std::wstring profile = Str(d, L"mode") == L"manual" ? L"manual" : Bool(m_data, L"power.charger") ? L"ac" : L"dc";
            auto choices = Arr(m_data, L"power.limits.freq");
            double v = Num(d, profile + L".freq");
            level.count = static_cast<int>(choices.Size()); level.index = 0;
            for (uint32_t i = 0; i < choices.Size(); i++) if (choices.GetNumberAt(i) == v) level.index = static_cast<int>(i);
            level.value = v ? Ghz(v) : hstring(L"最高");
            level.disabled = choices.Size() < 2 || Str(d, L"mode") == L"autotdp";
            if (Str(d, L"mode") == L"autotdp") level.value = L"不限频";
        }
        else if (id == L"fanfull")
        {
            bool on = Bool(d, L"fan.full");
            level.count = 2; level.index = on; level.value = on ? L"开" : L"关";
        }
        else if (id == L"ecore")
        {
            bool on = Bool(m_data, L"power.ecore.enabled");
            level.count = 2; level.index = on;
            auto status = Str(m_data, L"power.ecore.status");
            level.value = hstring(on ? L"开 · " : L"关 · ") + (status.empty() ? L"修改后需重启" : status);
            level.disabled = Has(m_data, L"power.ecore.supported") && !Bool(m_data, L"power.ecore.supported");
        }
        else if (id == L"sharpen")
        {
            bool on = Bool(m_data, L"magpie.preferences.sharpen");
            level.count = 2; level.index = on; level.value = on ? L"开" : L"关";
            level.disabled = !Bool(m_data, L"magpie.enabled");
        }
        else if (id == L"screenshot") level.value = L"保存到相册";
        return level;
    }

    void WidgetPage::Press(std::wstring const& id)
    {
        if (!m_data || !m_draft) return;
        auto level = TileLevel(id);
        if (level.disabled) return;
        int next = level.count ? (level.index + 1) % level.count : 0;
        auto d = m_draft;
        if (id == L"power")
        {
            auto quick = Arr(d, L"quick");
            if (next == 0) d.SetNamedValue(L"enabled", B(false));
            else if (next == 1) { d.SetNamedValue(L"enabled", B(true)); d.SetNamedValue(L"mode", S(L"autotdp")); }
            else { d.SetNamedValue(L"enabled", B(true)); d.SetNamedValue(L"mode", S(L"manual")); SetPath(d, L"manual.tdp", N(quick.GetNumberAt(next - 2))); }
            EditPower();
        }
        else if (id == L"fan") { SetPath(d, L"fan.mode", S(FanSteps[next].first)); EditPower(); }
        else if (id == L"osd")
        {
            SetPath(d, L"osd.enabled", B(next > 0));
            if (next) SetPath(d, L"osd.level", N(next));
            m_draftHeldUntil = Clock::now() + std::chrono::seconds(2);
            HoldValue(L"power.settings.osd.enabled", B(next > 0), L"osd");
            HoldValue(L"power.settings.osd.level", N(Num(d, L"osd.level", 2)), L"osd");
            JsonObject preferences;
            preferences.SetNamedValue(L"enabled", B(next > 0));
            preferences.SetNamedValue(L"level", N(Num(d, L"osd.level", 2)));
            preferences.SetNamedValue(L"renderer", S(L"gamebar"));
            Send(L"osd", L"set_osd_preferences", Args({ preferences }));
        }
        else if (id == L"target")
        {
            SetPath(d, L"autotdp.target", N(AutoTargets[next]));
            EditPower();
        }
        else if (id == L"fanfull") { SetPath(d, L"fan.full", B(!Bool(d, L"fan.full"))); EditPower(); }
        else if (id == L"cpu")
        {
            std::wstring profile = Str(d, L"mode") == L"manual" ? L"manual" : Bool(m_data, L"power.charger") ? L"ac" : L"dc";
            SetPath(d, profile + L".freq", N(Arr(m_data, L"power.limits.freq").GetNumberAt(next)));
            EditPower();
        }
        else if (id == L"fps") { HoldValue(L"system.fpsLimit", N(FpsSteps[next]), L"fps"); Send(L"fps", L"set_fps_limit", Args({ N(FpsSteps[next]) })); }
        else if (id == L"refresh")
        {
            std::vector<double> rates;
            for (auto const& r : Arr(m_data, L"system.refreshRates")) if (r.GetNumber() >= 60) rates.push_back(r.GetNumber());
            if (rates.empty()) return;
            HoldValue(L"system.refresh", N(rates[next % rates.size()]), L"refresh");
            Send(L"refresh", L"set_refresh_rate", Args({ N(rates[next % rates.size()]) }));
        }
        else if (id == L"magpie" || id == L"lossless") { OpenScaling(id); return; }
        else if (id == L"fse" && !m_sending.count(L"fse")) Send(L"fse", Bool(m_data, L"xboxFse") ? L"leave_xbox_fse" : L"open_gaming_home");
        else if (id == L"rumble") { HoldValue(L"rumble.strength", N(RumbleSteps[next].first), L"rumble"); Send(L"rumble", L"set_rumble_strength", Args({ N(RumbleSteps[next].first) })); }
        else if (id == L"ecore") { bool on = !Bool(m_data, L"power.ecore.enabled"); HoldValue(L"power.ecore.enabled", B(on), L"ecore"); Send(L"ecore", L"set_ecore", Args({ B(on) })); }
        else if (id == L"keyboard") Send(L"keyboard", L"toggle_keyboard");
        else if (id == L"screenshot") Send(L"screenshot", L"screenshot");
        else if (id == L"sharpen")
        {
            auto prefs = Clone(Obj(m_data, L"magpie.preferences"));
            prefs.SetNamedValue(L"target", N(Num(m_data, L"magpie.target")));
            prefs.SetNamedValue(L"sharpen", B(!Bool(prefs, L"sharpen")));
            HoldValue(L"magpie.preferences.sharpen", B(Bool(prefs, L"sharpen")), L"sharpen");
            Send(L"sharpen", L"apply_magpie", Args({ prefs }));
        }
        else if (id == L"close")
        {
            double pid = Num(m_data, L"system.game.pid");
            if (m_armLeft > 0 && m_armPid == pid) { ClearArm(); Send(L"close", L"close_game", Args({ N(pid) })); }
            else ArmClose(pid);
        }
        Update();
    }

    // "关闭程序": the first press arms a 3 s countdown, a second press within it closes the game.
    void WidgetPage::ArmClose(double pid)
    {
        m_armPid = pid; m_armLeft = 3;
        m_armTimer.Stop(); m_armTimer.Start();
        Update();
    }

    void WidgetPage::ClearArm()
    {
        m_armLeft = 0; m_armTimer.Stop();
        Update();
    }

    // ---- view ----------------------------------------------------------------------------------
    void WidgetPage::Update()
    {
        if (m_shutdown) return;
        bool connected = m_agent && m_agent->Ready();
        m_offline = !connected && m_failedSince != Clock::time_point{} && (Clock::now() - m_failedSince > std::chrono::seconds(4) || !m_data);
        bool usable = m_data && !m_offline;
        OfflineBar().IsOpen(m_offline);
        ConflictBar().IsOpen(usable && Bool(m_data, L"power.otherTool") && m_draft && Bool(m_draft, L"enabled"));
        ShowBanners();

        UpdatePowerControl(usable);

        // Rail
        m_updatingSliders = true;
        for (auto const& [slider, label, key] : { std::tuple{ BrightnessSlider(), BrightnessValue(), std::wstring(L"brightness") },
                                                   std::tuple{ VolumeSlider(), VolumeValue(), std::wstring(L"volume") } })
        {
            bool has = usable && Has(m_data, L"system." + key);
            double value = key == L"volume" && Bool(m_data, L"system.muted") ? 0 : Num(m_data, L"system." + key);
            slider.IsEnabled(connected && has);
            if (has && std::abs(slider.Value() - value) >= 0.5) slider.Value(value);
            label.Text(has ? Fmt(value, 0) : hstring(L"--"));
        }
        m_updatingSliders = false;
        bool muted = m_data && Bool(m_data, L"system.muted");
        MuteButton().Content(Icon(muted ? L"muted" : L"speaker", 20, muted ? Resource(L"AccentTextFillColorPrimaryBrush") : nullptr));
        MuteButton().IsEnabled(usable);
        PowerButton().IsEnabled(usable);
        EditButton().IsEnabled(usable || m_editing);
        bool desktop = Str(m_data, L"gamepad.mode") == L"desktop";
        m_quickPad.IsEnabled(connected && usable && Bool(m_data, L"gamepad.available") && !m_modeSwitching);
        m_quickTaskManager.IsEnabled(connected);
        bool frozen = Has(m_data, L"gameFreeze.frozen") || Bool(m_data, L"gameFreeze.recoveryPending");
        m_quickFreeze.IsEnabled(connected && !m_sending.count(L"game-freeze") && Bool(m_data, L"gameFreeze.available")
            && (frozen || Bool(m_data, L"gameFreeze.canFreeze")));
        if (auto tag = m_quickFreeze.Tag(); !tag || unbox_value<bool>(tag) != frozen) {
            m_quickFreeze.Tag(box_value(frozen));
            auto label = frozen ? L"继续游戏" : L"冻结游戏";
            m_quickFreeze.Content(Icon(frozen ? L"qamResume" : L"qamFreeze", 24));
            ToolTipService::SetToolTip(m_quickFreeze, box_value(label));
            Automation::AutomationProperties::SetName(m_quickFreeze, label);
        }
        bool canExit = connected && usable && m_draft && !TileLevel(L"close").disabled;
        bool confirmExit = canExit && m_armLeft > 0 && m_armPid == Num(m_data, L"system.game.pid");
        m_quickExit.IsEnabled(canExit);
        m_quickExitLabel.Text(confirmExit ? L"再按一次退出" : L"退出游戏");
        Automation::AutomationProperties::SetName(m_quickExit, m_quickExitLabel.Text());
        if (auto tag = m_quickPad.Tag(); !tag || unbox_value<bool>(tag) != desktop) {
            m_quickPad.Tag(box_value(desktop));
            m_quickPad.Content(Icon(desktop ? L"mouse" : L"gamepad", 24));
            Automation::AutomationProperties::SetName(m_quickPad, desktop ? L"切换到手柄模式" : L"切换到键鼠模式");
        }

        for (auto& [id, view] : m_views) UpdateTile(id, view);
        UpdateScaling();
    }

    void WidgetPage::ShowBanners()
    {
        bool any = OfflineBar().IsOpen() || ConflictBar().IsOpen() || MessageBar().IsOpen();
        Banners().Visibility(any ? Visibility::Visible : Visibility::Collapsed);
    }

    void WidgetPage::UpdateTile(std::wstring const& id, TileView& view)
    {
        bool usable = m_data && !m_offline;
        auto level = usable ? TileLevel(id) : Level{};
        if (id == L"fse") {
            auto title = Bool(m_data, L"xboxFse") ? L"返回桌面" : L"返回 FSE";
            view.title.Text(title);
            Automation::AutomationProperties::SetName(view.button, title);
        }
        bool armed = id == L"close" && m_armLeft > 0;
        view.button.IsEnabled(usable && !level.disabled);
        view.value.Text(!usable ? hstring(L"--") : armed ? L"再按一次关闭 " + level.value + L"（" + to_hstring(m_armLeft) + L"）" : level.value);
        if (armed)
        {
            view.button.Background(SolidColorBrush(ColorHelper::FromArgb(255, 0xFF, 0x99, 0xA4)));
            view.button.Foreground(SolidColorBrush(Colors::Black()));
            view.value.Foreground(SolidColorBrush(Colors::Black()));
        }
        else
        {
            view.button.ClearValue(Control::BackgroundProperty());
            view.button.ClearValue(Control::ForegroundProperty());
            view.value.Foreground(Resource(L"TextFillColorSecondaryBrush"));
        }
        view.dots.Spacing(m_narrow ? 2 : 3);
        view.dots.Children().Clear();
        if (usable && level.count >= 2)
            for (int i = 0; i < level.count; i++)
            {
                Shapes::Ellipse dot; dot.Width(m_narrow ? 5 : 6); dot.Height(m_narrow ? 5 : 6);
                dot.Fill(i == level.index ? Resource(L"TextFillColorPrimaryBrush") : SolidColorBrush(ColorHelper::FromArgb(61, 255, 255, 255)));
                view.dots.Children().Append(dot);
            }
    }

    void WidgetPage::SetEditing(bool editing)
    {
        m_editing = editing;
        m_moving.clear();
        MainPanel().Visibility(editing ? Visibility::Collapsed : Visibility::Visible);
        EditPanel().Visibility(editing ? Visibility::Visible : Visibility::Collapsed);
        if (editing) { BuildEditTiles(); DoneButton().Focus(FocusState::Programmatic); }
        else { BuildTiles(); Update(); EditButton().Focus(FocusState::Programmatic); }
    }

    void WidgetPage::MoveTile(int dx, int dy)
    {
        auto at = std::find(m_tiles.begin(), m_tiles.end(), m_moving);
        if (at == m_tiles.end()) return;
        int from = static_cast<int>(at - m_tiles.begin());
        int to = std::clamp(from + dx + dy * TileColumns, 0, static_cast<int>(m_tiles.size()) - 1);
        if (to == from) return;
        auto id = m_tiles[from];
        m_tiles.erase(m_tiles.begin() + from);
        m_tiles.insert(m_tiles.begin() + to, id);
        JsonArray list; for (auto& t : m_tiles) list.Append(S(t));
        ApplicationData::Current().LocalSettings().Values().Insert(L"tiles", box_value(list.Stringify()));
        BuildEditTiles();
    }

    bool WidgetPage::MoveMainFocus(Control const& from, int dx, int dy)
    {
        std::vector<Control> candidates{ EditButton(), BrightnessSlider(), VolumeSlider(),
            MuteButton(), PowerButton() };
        auto railFocus = std::find(candidates.begin(), candidates.end(), from);
        if (dy && railFocus != candidates.end()) {
            auto current = railFocus - candidates.begin();
            auto count = candidates.end() - candidates.begin();
            for (auto next = current + dy; next >= 0 && next < count; next += dy) {
                if (candidates[next].IsEnabled() && candidates[next].Focus(FocusState::Keyboard)) break;
            }
            m_directionalFocus = {};
            return true;
        }
        candidates.push_back(PowerSlider());
        if (OfflineBar().IsOpen()) candidates.push_back(ReconnectButton());
        for (auto const& grid : { QuickShortcuts(), Tiles() })
            for (auto const& child : grid.Children()) candidates.push_back(child.as<Button>());
        if (std::find(candidates.begin(), candidates.end(), from) == candidates.end()) return false;

        auto bounds = [&](Control const& control) {
            return control.TransformToVisual(Root()).TransformBounds({ 0, 0,
                static_cast<float>(control.ActualWidth()), static_cast<float>(control.ActualHeight()) });
        };
        auto start = bounds(from);
        auto center = [](Rect const& rect) { return Point{ rect.X + rect.Width / 2, rect.Y + rect.Height / 2 }; };
        auto origin = center(start);
        auto anchor = m_directionalFocus.get() == from ? m_focusAnchor : origin;
        Control best{ nullptr };
        Rect bestBounds{};
        std::tuple<double, double, double> bestScore;
        for (auto const& target : candidates) {
            if (target == from || !target.IsEnabled() || !target.IsTabStop() || target.Visibility() != Visibility::Visible) continue;
            auto rect = bounds(target);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            auto midpoint = center(rect);
            double forward = dx * (midpoint.X - origin.X) + dy * (midpoint.Y - origin.Y);
            if (forward <= 0.5) continue;
            double alongExtent = dx ? start.Width + rect.Width : start.Height + rect.Height;
            double gap = std::max(0.0, forward - alongExtent / 2);
            double cross = dx ? anchor.Y : anchor.X;
            double crossStart = dx ? rect.Y : rect.X;
            double crossEnd = crossStart + (dx ? rect.Height : rect.Width);
            double offset = std::abs(cross - std::clamp(cross, crossStart, crossEnd));
            auto score = std::make_tuple(gap + 2 * offset, offset, std::abs(cross - (dx ? midpoint.Y : midpoint.X)));
            if (!best || score < bestScore) { best = target; bestBounds = rect; bestScore = score; }
        }
        if (!best || !best.Focus(FocusState::Keyboard)) return false;
        auto midpoint = center(bestBounds);
        // Wide cards and tall sliders keep the row or column used to enter them.
        m_focusAnchor = dx ? Point{ midpoint.X, std::clamp(anchor.Y, bestBounds.Y, bestBounds.Y + bestBounds.Height) }
                          : Point{ std::clamp(anchor.X, bestBounds.X, bestBounds.X + bestBounds.Width), midpoint.Y };
        m_directionalFocus = make_weak(best);
        return true;
    }

    void WidgetPage::SetRailAdjustment(Slider const& slider)
    {
        if (m_railAdjustment == slider) return;
        if (m_railAdjustment) {
            auto old = m_railAdjustment;
            old.Foreground(old.FocusState() != FocusState::Unfocused ? Resource(L"TextFillColorPrimaryBrush") : nullptr);
            ToolTipService::SetToolTip(old, box_value(old == BrightnessSlider() ? L"按 A 调节亮度" : L"按 A 调节音量"));
        }
        m_railAdjustment = slider;
        if (slider) {
            slider.Foreground(Resource(L"AccentFillColorDefaultBrush"));
            ToolTipService::SetToolTip(slider, box_value(L"上下键每次 1%，按 A 或 B 完成"));
        }
    }

    // Edit mode: X picks up / drops the focused tile, the D-pad moves a picked tile, B steps back.
    // B also cancels an armed "关闭程序". Otherwise B stays with Game Bar.
    void WidgetPage::OnPreviewKey(IInspectable const&, KeyRoutedEventArgs const& e)
    {
        auto key = e.Key();
        bool back = key == VirtualKey::GamepadB || key == VirtualKey::Escape;
        if (!m_scalingPage.empty()) {
            if (back) { OpenScaling({}); e.Handled(true); return; }
            // One column: left / right never move focus here, they step the focused choice.
            int step = key == VirtualKey::GamepadDPadLeft || key == VirtualKey::Left || key == VirtualKey::GamepadLeftThumbstickLeft ? -1
                : key == VirtualKey::GamepadDPadRight || key == VirtualKey::Right || key == VirtualKey::GamepadLeftThumbstickRight ? 1 : 0;
            if (!step) return;
            if (auto row = FocusManager::GetFocusedElement().try_as<Button>(); row && row.Tag())
                if (auto tag = row.Tag().try_as<hstring>()) ScalingStep(tag->c_str(), step);
            e.Handled(true);
            return;
        }
        if (back && m_railAdjustment) { SetRailAdjustment(nullptr); e.Handled(true); return; }
        if (back && m_armLeft > 0) { ClearArm(); e.Handled(true); return; }
        if (m_editing && back) { if (!m_moving.empty()) { m_moving.clear(); BuildEditTiles(); } else SetEditing(false); e.Handled(true); return; }
        if (m_editing && (key == VirtualKey::GamepadX || key == VirtualKey::X))
        {
            auto focused = FocusManager::GetFocusedElement().try_as<Button>();
            if (!m_moving.empty()) m_moving.clear();
            else if (focused && focused.Tag())
            {
                std::wstring id = unbox_value<hstring>(focused.Tag()).c_str();
                if (std::find(m_tiles.begin(), m_tiles.end(), id) != m_tiles.end()) m_moving = id;
            }
            BuildEditTiles();
            e.Handled(true);
            return;
        }
        if (m_editing && m_moving.empty()) return;
        auto adjust = [](Slider const& slider, int step) {
            if (slider.IsEnabled()) slider.Value(std::clamp(std::round(slider.Value()) + step, slider.Minimum(), slider.Maximum()));
        };
        auto focused = FocusManager::GetFocusedElement().try_as<Control>();
        auto slider = focused.try_as<Slider>();
        bool railSlider = slider && (slider == BrightnessSlider() || slider == VolumeSlider());
        if (!m_editing && railSlider) {
            if (key == VirtualKey::GamepadA || key == VirtualKey::Enter || key == VirtualKey::Space) {
                if (!e.KeyStatus().WasKeyDown && slider.IsEnabled())
                    SetRailAdjustment(slider == m_railAdjustment ? nullptr : slider);
                e.Handled(true);
                return;
            }
            bool brightness = slider == BrightnessSlider();
            auto decrease = brightness ? VirtualKey::GamepadLeftShoulder : VirtualKey::GamepadLeftTrigger;
            auto increase = brightness ? VirtualKey::GamepadRightShoulder : VirtualKey::GamepadRightTrigger;
            if (slider == m_railAdjustment && (key == decrease || key == increase)) {
                adjust(slider, key == decrease ? -1 : 1);
                e.Handled(true);
                return;
            }
        }
        int dx = 0, dy = 0;
        if (key == VirtualKey::GamepadDPadLeft || key == VirtualKey::Left || key == VirtualKey::GamepadLeftThumbstickLeft) dx = -1;
        else if (key == VirtualKey::GamepadDPadRight || key == VirtualKey::Right || key == VirtualKey::GamepadLeftThumbstickRight) dx = 1;
        else if (key == VirtualKey::GamepadDPadUp || key == VirtualKey::Up || key == VirtualKey::GamepadLeftThumbstickUp) dy = -1;
        else if (key == VirtualKey::GamepadDPadDown || key == VirtualKey::Down || key == VirtualKey::GamepadLeftThumbstickDown) dy = 1;
        else return;
        if (m_editing) { MoveTile(dx, dy); e.Handled(true); return; }
        if (!focused) return;
        if (slider && ((slider == PowerSlider() && dx) || (slider == m_railAdjustment && dy))) {
            adjust(slider, dx ? dx : -dy);
            e.Handled(true);
        } else if (MoveMainFocus(focused, dx, dy) || slider) e.Handled(true);
    }
}
