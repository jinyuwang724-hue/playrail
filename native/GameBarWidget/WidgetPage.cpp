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
    const std::vector<std::wstring> DefaultTiles{ L"power", L"fan", L"fps", L"refresh", L"osd", L"magpie", L"rumble", L"keyboard", L"close" };
    const std::vector<std::wstring> Pool{ L"target", L"cpu", L"fanfull", L"ecore", L"sharpen", L"screenshot" };
    const std::map<std::wstring, std::pair<std::wstring, std::wstring>> Names{  // id -> (name, icon)
        { L"power", { L"功耗", L"power" } }, { L"fan", { L"风扇", L"fan" } }, { L"fps", { L"限帧", L"fps" } },
        { L"refresh", { L"刷新率", L"refresh" } }, { L"osd", { L"性能显示", L"osd" } }, { L"magpie", { L"画面缩放", L"magpie" } },
        { L"rumble", { L"震动", L"rumble" } }, { L"keyboard", { L"显示键盘", L"keyboard" } }, { L"close", { L"关闭程序", L"close" } },
        { L"target", { L"目标帧率", L"target" } }, { L"cpu", { L"CPU 频率", L"cpu" } }, { L"fanfull", { L"风扇全速", L"fanfull" } },
        { L"ecore", { L"E 核调度", L"ecore" } }, { L"sharpen", { L"画面锐化", L"sharpen" } }, { L"screenshot", { L"截图", L"screenshot" } } };
    const std::map<std::wstring, std::wstring> Hints{
        { L"target", L"智能模式用" }, { L"cpu", L"最高 / 限频" }, { L"fanfull", L"开 / 关" }, { L"ecore", L"重启后生效" },
        { L"sharpen", L"Magpie" }, { L"screenshot", L"保存到相册" } };
    const std::vector<int> FpsSteps{ 0, 30, 40, 45, 60, 90, 120 };
    const std::vector<std::pair<int, std::wstring>> RumbleSteps{ { 0, L"关" }, { 35, L"弱" }, { 70, L"中" }, { 100, L"强" } };
    const std::vector<std::pair<std::wstring, std::wstring>> FanSteps{ { L"firmware", L"固件" }, { L"quiet", L"静音" }, { L"balanced", L"均衡" }, { L"performance", L"性能" } };

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
}

namespace winrt::ClawWidget::implementation
{
    WidgetPage::WidgetPage()
    {
        InitializeComponent();
        // Tile order the user arranged (LocalSettings), else the design's default nine.
        auto saved = ApplicationData::Current().LocalSettings().Values().TryLookup(L"tiles");
        JsonArray list{ nullptr };
        if (saved && JsonArray::TryParse(unbox_value<hstring>(saved), list))
            for (auto const& v : list)
            {
                std::wstring id = v.GetString().c_str();
                if (Names.count(id) && std::find(m_tiles.begin(), m_tiles.end(), id) == m_tiles.end()) m_tiles.push_back(id);
            }
        else m_tiles = DefaultTiles;

        BuildRail();
        BuildStats();
        BuildTiles();

        m_agent = std::make_unique<claw::Agent>(Dispatcher());
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
            else return;
            Accept(data);
        };

        m_saveTimer.Interval(std::chrono::milliseconds(200));
        m_saveTimer.Tick([this](auto&&, auto&&) {
            m_saveTimer.Stop();
            if (m_draft) Send(L"power", L"save_power", Args({ Clone(m_draft) }));
        });
        m_pollTimer.Interval(std::chrono::milliseconds(1500));
        m_pollTimer.Tick([this](auto&&, auto&&) {
            if (m_agent->Ready()) { if (!m_saveTimer.IsEnabled()) Refresh(); }
            else m_agent->Connect();
            Update();
        });
        m_armTimer.Interval(std::chrono::seconds(1));
        m_armTimer.Tick([this](auto&&, auto&&) { if (--m_armLeft <= 0) ClearArm(); else Update(); });
        m_flashTimer.Interval(std::chrono::seconds(4));
        m_flashTimer.Tick([this](auto&&, auto&&) { m_flashTimer.Stop(); MessageBar().IsOpen(false); });

        EditButton().Click([this](auto&&, auto&&) { SetEditing(!m_editing); });
        DoneButton().Click([this](auto&&, auto&&) { SetEditing(false); });
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
        ShutdownItem().Click(power(L"shutdown"));
        RestartItem().Click(power(L"restart"));
        LockItem().Click(power(L"lock"));
        for (auto const& [slider, key] : { std::pair{ BrightnessSlider(), std::wstring(L"brightness") }, std::pair{ VolumeSlider(), std::wstring(L"volume") } })
        {
            slider.ValueChanged([this, key = key](IInspectable const&, Primitives::RangeBaseValueChangedEventArgs const& e) {
                if (m_updatingSliders || !m_data) return;
                int value = static_cast<int>(std::round(e.NewValue()));
                HoldValue(L"system." + key, N(value), key);
                if (key == L"volume") HoldValue(L"system.muted", B(false), key);
                Send(key, key == L"volume" ? L"set_volume" : L"set_brightness", Args({ N(value) }));
                Update();
            });
            // Vertical slider: up/down change the value, left/right leave it (XAML would adjust it).
            slider.PreviewKeyDown([](IInspectable const&, KeyRoutedEventArgs const& e) {
                auto key = e.Key();
                bool left = key == VirtualKey::GamepadDPadLeft || key == VirtualKey::GamepadLeftThumbstickLeft || key == VirtualKey::Left;
                bool right = key == VirtualKey::GamepadDPadRight || key == VirtualKey::GamepadLeftThumbstickRight || key == VirtualKey::Right;
                if (!left && !right) return;
                FocusManager::TryMoveFocus(left ? FocusNavigationDirection::Left : FocusNavigationDirection::Right);
                e.Handled(true);
            });
        }
        PreviewKeyDown({ this, &WidgetPage::OnPreviewKey });
        SizeChanged([this](IInspectable const&, SizeChangedEventArgs const& e) {
            // Narrow (under 480 wide): readings go under the game name, as in the design's 400 variant.
            bool narrow = e.NewSize().Width < 480;
            if (narrow == m_narrow && m_layoutDone) return;
            m_narrow = narrow; m_layoutDone = true;
            Grid::SetRow(Stats(), narrow ? 1 : 0);
            Grid::SetColumn(Stats(), narrow ? 0 : 1);
            Grid::SetColumnSpan(Stats(), narrow ? 2 : 1);
            // Q-400-3: game name and frame rate on one line, compact readings, slimmer rail and tiles.
            TitleBlock().Orientation(narrow ? Orientation::Horizontal : Orientation::Vertical);
            TitleBlock().Spacing(narrow ? 10 : 0);
            FpsValue().FontSize(narrow ? 28 : 32);
            Header().Padding(narrow ? Thickness{ 12, 8, 12, 8 } : Thickness{ 14, 10, 14, 10 });
            Rail().Width(narrow ? 44 : 48);
            for (auto const& child : Stats().Children())
            {
                auto panel = child.as<StackPanel>();
                panel.Orientation(narrow ? Orientation::Horizontal : Orientation::Vertical);
                panel.Spacing(narrow ? 4 : 0);
                panel.Children().GetAt(1).as<TextBlock>().FontSize(narrow ? 14 : 18);
            }
            for (auto& [id, view] : m_views) view.button.Padding(narrow ? Thickness{ 10, 10, 10, 10 } : Thickness{ 12, 12, 12, 12 });
            Update();
        });

        Loaded([this](IInspectable const&, RoutedEventArgs const&) {
            if (!m_tiles.empty()) m_views[m_tiles.front()].button.Focus(FocusState::Programmatic);
        });
        Update();
        LoadCache();
        m_agent->Connect();
        m_pollTimer.Start();
    }

    void WidgetPage::OnNavigatedTo(Navigation::NavigationEventArgs const& e)
    {
        m_widget = e.Parameter().try_as<XboxGameBarWidget>();
        if (!m_widget) return;
        // Game Bar keeps the window size from earlier versions; once per layout change, set the
        // size this version is designed for (464 wide, the width of Game Bar's own widget panels).
        auto values = ApplicationData::Current().LocalSettings().Values();
        if (unbox_value_or<hstring>(values.TryLookup(L"windowSize"), L"") != L"464x480")
        {
            m_widget.TryResizeWindowAsync(Size{ 464, 480 });
            values.Insert(L"windowSize", box_value(L"464x480"));
        }
        m_widget.GameBarDisplayModeChanged([weak = get_weak()](auto&&, auto&&) {
            if (auto self = weak.get())
                self->Dispatcher().RunAsync(Core::CoreDispatcherPriority::Normal, [self]() {
                    if (self->m_widget.GameBarDisplayMode() != XboxGameBarDisplayMode::Foreground) return;
                    self->FocusSelf();
                    self->Refresh();
                });
        });
    }

    // Game Bar opened: bring this widget to the front with controller focus (as ClawTweaks does),
    // so the Xbox button lands on it instead of on Game Bar's home.
    void WidgetPage::FocusSelf()
    {
        auto now = Clock::now();
        if (now - m_lastFocus < std::chrono::seconds(2)) return;  // the launch itself re-raises Foreground
        m_lastFocus = now;
        Windows::System::Launcher::LaunchUriAsync(Uri(L"ms-gamebar://launchForeground/activate/ClawToolbox.GameBarWidget_ba3r68zr466za_App_Toolbox/"));
    }

    // ---- setup ---------------------------------------------------------------------------------
    void WidgetPage::BuildRail()
    {
        EditButton().Content(Icon(L"edit"));
        AutoBrightness().Content(Icon(L"sun"));
        MuteButton().Content(Icon(L"speaker"));
        PowerButton().Content(Icon(L"shutdown"));
    }

    void WidgetPage::BuildStats()
    {
        auto stat = [this](wchar_t const* label, bool first) {
            StackPanel panel;
            if (!first)
            {
                panel.BorderBrush(Resource(L"DividerStrokeColorDefaultBrush"));
                panel.BorderThickness(Thickness{ 1, 0, 0, 0 });
                panel.Margin(Thickness{ 12, 0, 0, 0 });
                panel.Padding(Thickness{ 12, 0, 0, 0 });
            }
            TextBlock name; name.Text(label); name.Foreground(Resource(L"TextFillColorSecondaryBrush"));
            TextBlock value; value.FontSize(18); value.FontWeight(Text::FontWeights::SemiBold()); value.Text(L"--");
            panel.Children().Append(name); panel.Children().Append(value);
            Stats().Children().Append(panel);
            return value;
        };
        m_powerValue = stat(L"功耗", true);
        m_cpuValue = stat(L"CPU", false);
        m_batteryValue = stat(L"电量", false);
    }

    void WidgetPage::BuildTiles()
    {
        Tiles().Children().Clear();
        Tiles().RowDefinitions().Clear();
        Tiles().ColumnDefinitions().Clear();
        m_views.clear();
        for (int c = 0; c < 3; c++) { ColumnDefinition column; column.Width(Star()); Tiles().ColumnDefinitions().Append(column); }
        int rows = std::max(1, static_cast<int>((m_tiles.size() + 2) / 3));
        for (int r = 0; r < rows; r++) { RowDefinition row; row.Height(Star()); Tiles().RowDefinitions().Append(row); }
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
            TextBlock title; title.Text(Names.at(id).first); title.FontWeight(Text::FontWeights::SemiBold()); title.Margin(Thickness{ 0, 8, 0, 0 });
            view.value = TextBlock(); view.value.TextTrimming(TextTrimming::CharacterEllipsis);
            Grid::SetRow(title, 1); Grid::SetRow(view.value, 2);
            content.Children().Append(view.top); content.Children().Append(title); content.Children().Append(view.value);
            b.Content(content);
            b.Click([this, id](IInspectable const&, RoutedEventArgs const&) { Press(id); });
            Grid::SetRow(b, static_cast<int>(i / 3)); Grid::SetColumn(b, static_cast<int>(i % 3));
            Tiles().Children().Append(b);
            m_views[id] = view;
        }
    }

    void WidgetPage::BuildEditTiles()
    {
        auto fill = [this](Grid grid, std::vector<std::wstring> const& ids, bool pinned) {
            grid.Children().Clear(); grid.RowDefinitions().Clear(); grid.ColumnDefinitions().Clear();
            for (int c = 0; c < 3; c++) { ColumnDefinition column; column.Width(Star()); grid.ColumnDefinitions().Append(column); }
            for (size_t r = 0; r < (ids.size() + 2) / 3; r++) { RowDefinition row; row.Height(GridLengthHelper::FromPixels(96)); grid.RowDefinitions().Append(row); }
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
                    else m_tiles.push_back(id);
                    JsonArray list; for (auto& t : m_tiles) list.Append(S(t));
                    ApplicationData::Current().LocalSettings().Values().Insert(L"tiles", box_value(list.Stringify()));
                    BuildEditTiles();
                });
                Grid::SetRow(b, static_cast<int>(i / 3)); Grid::SetColumn(b, static_cast<int>(i % 3));
                grid.Children().Append(b);
            }
        };
        std::vector<std::wstring> available;
        for (auto const& list : { DefaultTiles, Pool })
            for (auto const& id : list) if (std::find(m_tiles.begin(), m_tiles.end(), id) == m_tiles.end()) available.push_back(id);
        fill(PinnedTiles(), m_tiles, true);
        fill(AvailableTiles(), available, false);
        PinnedCount().Text(to_hstring(static_cast<int>(m_tiles.size())) + L" 个");
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
        }
        catch (...) {}
    }

    // ---- data ----------------------------------------------------------------------------------
    void WidgetPage::Refresh()
    {
        if (!m_agent || !m_agent->Ready()) return;
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
            level.index = !enabled ? 0 : mode == L"smart" ? 1 : (mode == L"manual" && at >= 0) ? 2 + at : 1;
            double limit = Has(m_data, L"power.smart.limit") ? Num(m_data, L"power.smart.limit") : Num(m_data, L"power.target.pl1");
            level.value = !enabled ? hstring(L"系统控制") : mode == L"smart" ? L"智能 · " + Fmt(limit, 0) + L" W"
                : mode == L"manual" ? Fmt(tdp, 0) + L" W" : L"预设 · " + Fmt(Num(m_data, L"power.target.pl1"), 0) + L" W";
        }
        else if (id == L"fan")
        {
            auto mode = Str(d, L"fan.mode");
            level.count = 4; level.index = 0;
            for (int i = 0; i < 4; i++) if (mode == FanSteps[i].first) level.index = i;
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
        else if (id == L"magpie")
        {
            bool on = Bool(m_data, L"magpie.enabled");
            level.count = 2; level.index = on; level.value = on ? L"开" : L"关";
            level.disabled = !Bool(m_data, L"magpie.available");
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
            double v = Num(d, L"smart.target");
            level.count = static_cast<int>(FpsSteps.size()); level.index = Nearest(FpsSteps, v);
            level.value = v ? Fmt(v, 0) + L" 帧" : hstring(L"不限");
        }
        else if (id == L"cpu")
        {
            std::wstring profile = Str(d, L"mode") == L"manual" ? L"manual" : Bool(m_data, L"power.charger") ? L"ac" : L"dc";
            auto choices = Arr(m_data, L"power.limits.freq");
            double v = Num(d, profile + L".freq");
            level.count = static_cast<int>(choices.Size()); level.index = 0;
            for (uint32_t i = 0; i < choices.Size(); i++) if (choices.GetNumberAt(i) == v) level.index = static_cast<int>(i);
            level.value = v ? Ghz(v) : hstring(L"最高");
            level.disabled = choices.Size() < 2;
        }
        else if (id == L"fanfull")
        {
            bool on = Bool(d, L"fan.full");
            level.count = 2; level.index = on; level.value = on ? L"开" : L"关";
            level.disabled = Str(d, L"fan.mode") == L"firmware";
        }
        else if (id == L"ecore")
        {
            bool on = Bool(m_data, L"power.ecore.enabled");
            level.count = 2; level.index = on;
            level.value = hstring(on ? L"开" : L"关") + (Bool(m_data, L"power.ecore.pending") ? L" · 重启后生效" : L"");
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
            else if (next == 1) { d.SetNamedValue(L"enabled", B(true)); d.SetNamedValue(L"mode", S(L"smart")); }
            else { d.SetNamedValue(L"enabled", B(true)); d.SetNamedValue(L"mode", S(L"manual")); SetPath(d, L"manual.tdp", N(quick.GetNumberAt(next - 2))); }
            EditPower();
        }
        else if (id == L"fan") { SetPath(d, L"fan.mode", S(FanSteps[next].first)); EditPower(); }
        else if (id == L"osd") { SetPath(d, L"osd.enabled", B(next > 0)); if (next) SetPath(d, L"osd.level", N(next)); EditPower(); }
        else if (id == L"target") { SetPath(d, L"smart.target", N(FpsSteps[next])); EditPower(); }
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
        else if (id == L"magpie") { HoldValue(L"magpie.enabled", B(!Bool(m_data, L"magpie.enabled")), L"magpie"); Send(L"magpie", L"toggle_magpie"); }
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
        bool connected = m_agent && m_agent->Ready();
        m_offline = !connected && m_failedSince != Clock::time_point{} && (Clock::now() - m_failedSince > std::chrono::seconds(4) || !m_data);
        bool usable = m_data && !m_offline;
        OfflineBar().IsOpen(m_offline);
        ConflictBar().IsOpen(usable && Bool(m_data, L"power.otherTool") && m_draft && Bool(m_draft, L"enabled"));

        // Header
        auto secondary = Resource(L"TextFillColorSecondaryBrush");
        bool game = m_data && Has(m_data, L"system.game");
        GameName().Text(game ? Str(m_data, L"system.game.title") : hstring(L"Claw 掌机工具箱"));
        bool fps = usable && Has(m_data, L"power.live.fps");
        FpsValue().Text(fps ? Fmt(Num(m_data, L"power.live.fps"), 0) : hstring());
        FpsValue().Visibility(fps ? Visibility::Visible : Visibility::Collapsed);
        FpsLabel().Text(m_offline ? L"数据和设置暂不可用" : fps ? L"真实帧率" : game ? L"等待帧率数据" : L"未在游戏中");
        auto reading = [&](TextBlock block, wchar_t const* path, wchar_t const* unit, int digits) {
            block.Inlines().Clear();
            Documents::Run value;
            if (!usable || !Has(m_data, path)) { value.Text(L"--"); block.Inlines().Append(value); block.Foreground(Resource(L"TextFillColorDisabledBrush")); return; }
            block.ClearValue(TextBlock::ForegroundProperty());
            value.Text(Fmt(Num(m_data, path), digits));
            Documents::Run suffix; suffix.Text(unit); suffix.FontSize(14); suffix.FontWeight(Text::FontWeights::Normal()); suffix.Foreground(secondary);
            block.Inlines().Append(value); block.Inlines().Append(suffix);
        };
        reading(m_powerValue, L"power.actual.package", L" W", 1);
        reading(m_cpuValue, L"power.live.cpuTemp", L" °C", 0);
        reading(m_batteryValue, L"power.live.battery", m_data && Bool(m_data, L"system.charging") ? L" % ⚡" : L" %", 0);

        // Rail
        m_updatingSliders = true;
        for (auto const& [slider, label, key] : { std::tuple{ BrightnessSlider(), BrightnessValue(), std::wstring(L"brightness") },
                                                   std::tuple{ VolumeSlider(), VolumeValue(), std::wstring(L"volume") } })
        {
            bool has = usable && Has(m_data, L"system." + key);
            double value = key == L"volume" && Bool(m_data, L"system.muted") ? 0 : Num(m_data, L"system." + key);
            slider.IsEnabled(has);
            if (has && std::abs(slider.Value() - value) >= 0.5) slider.Value(value);
            label.Text(has ? Fmt(value, 0) : hstring(L"--"));
        }
        m_updatingSliders = false;
        bool muted = m_data && Bool(m_data, L"system.muted");
        MuteButton().Content(Icon(muted ? L"muted" : L"speaker", 20, muted ? Resource(L"AccentTextFillColorPrimaryBrush") : nullptr));
        MuteButton().IsEnabled(usable);
        PowerButton().IsEnabled(usable);
        EditButton().IsEnabled(usable || m_editing);

        for (auto& [id, view] : m_views) UpdateTile(id, view);
    }

    void WidgetPage::UpdateTile(std::wstring const& id, TileView& view)
    {
        bool usable = m_data && !m_offline;
        auto level = usable ? TileLevel(id) : Level{};
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
        int to = std::clamp(from + dx + dy * 3, 0, static_cast<int>(m_tiles.size()) - 1);
        if (to == from) return;
        auto id = m_tiles[from];
        m_tiles.erase(m_tiles.begin() + from);
        m_tiles.insert(m_tiles.begin() + to, id);
        JsonArray list; for (auto& t : m_tiles) list.Append(S(t));
        ApplicationData::Current().LocalSettings().Values().Insert(L"tiles", box_value(list.Stringify()));
        BuildEditTiles();
    }

    // Edit mode: X picks up / drops the focused tile, the D-pad moves a picked tile, B steps back.
    // B also cancels an armed "关闭程序". Otherwise B stays with Game Bar.
    void WidgetPage::OnPreviewKey(IInspectable const&, KeyRoutedEventArgs const& e)
    {
        auto key = e.Key();
        bool back = key == VirtualKey::GamepadB || key == VirtualKey::Escape;
        if (back && m_armLeft > 0) { ClearArm(); e.Handled(true); return; }
        if (!m_editing) return;
        if (back) { if (!m_moving.empty()) { m_moving.clear(); BuildEditTiles(); } else SetEditing(false); e.Handled(true); return; }
        if (key == VirtualKey::GamepadX || key == VirtualKey::X)
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
        if (m_moving.empty()) return;
        int dx = 0, dy = 0;
        if (key == VirtualKey::GamepadDPadLeft || key == VirtualKey::Left || key == VirtualKey::GamepadLeftThumbstickLeft) dx = -1;
        else if (key == VirtualKey::GamepadDPadRight || key == VirtualKey::Right || key == VirtualKey::GamepadLeftThumbstickRight) dx = 1;
        else if (key == VirtualKey::GamepadDPadUp || key == VirtualKey::Up || key == VirtualKey::GamepadLeftThumbstickUp) dy = -1;
        else if (key == VirtualKey::GamepadDPadDown || key == VirtualKey::Down || key == VirtualKey::GamepadLeftThumbstickDown) dy = 1;
        else return;
        MoveTile(dx, dy);
        e.Handled(true);
    }
}
