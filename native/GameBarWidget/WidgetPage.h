#pragma once
#include "WidgetPage.g.h"
#include "Agent.h"
#include <chrono>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace winrt::ClawWidget::implementation
{
    // The widget's single page, native XAML after the approved design. Readings and settings come
    // from the toolbox agent; presses change the shown value at once and are sent right after.
    struct WidgetPage : WidgetPageT<WidgetPage>
    {
        WidgetPage();
        void OnNavigatedTo(Windows::UI::Xaml::Navigation::NavigationEventArgs const& e);

    private:
        using Clock = std::chrono::steady_clock;
        using JsonObject = Windows::Data::Json::JsonObject;
        using IJsonValue = Windows::Data::Json::IJsonValue;
        using JsonArray = Windows::Data::Json::JsonArray;

        struct Level { int index = -1, count = 0; hstring value; bool disabled = false; };
        struct TileView {
            Windows::UI::Xaml::Controls::Button button{ nullptr };
            Windows::UI::Xaml::Controls::TextBlock value{ nullptr };
            Windows::UI::Xaml::Controls::StackPanel dots{ nullptr };
            Windows::UI::Xaml::Controls::Grid top{ nullptr };
        };
        struct Hold { IJsonValue value; std::wstring key; Clock::time_point until; };
        struct Pending { std::wstring method; JsonArray args; };

        // setup
        void BuildRail();
        void BuildStats();
        void BuildTiles();
        void BuildEditTiles();
        winrt::fire_and_forget LoadCache();

        // data
        void Refresh();
        void Accept(JsonObject const& data);
        void ApplyHolds(JsonObject const& data);
        void HoldValue(std::wstring const& path, IJsonValue const& value, std::wstring const& key);
        void ReleaseHold(std::wstring const& key);
        void Send(std::wstring const& key, std::wstring const& method, JsonArray const& args = JsonArray());
        void Dispatch(std::wstring const& key, Pending const& pending);
        void EditPower();
        void Flash(hstring const& text);
        winrt::fire_and_forget SaveCache();

        // tiles
        Level TileLevel(std::wstring const& id);
        void Press(std::wstring const& id);
        void ArmClose(double pid);
        void ClearArm();

        // view
        void Update();
        void UpdateTile(std::wstring const& id, TileView& view);
        void SetEditing(bool editing);
        void MoveTile(int dx, int dy);
        void OnPreviewKey(Windows::Foundation::IInspectable const&, Windows::UI::Xaml::Input::KeyRoutedEventArgs const& e);
        void FocusSelf();

        Microsoft::Gaming::XboxGameBar::XboxGameBarWidget m_widget{ nullptr };
        std::unique_ptr<claw::Agent> m_agent;
        JsonObject m_data{ nullptr };
        JsonObject m_draft{ nullptr };
        std::vector<std::wstring> m_tiles;
        std::map<std::wstring, TileView> m_views;
        std::map<std::wstring, Hold> m_holds;
        std::map<std::wstring, std::optional<Pending>> m_sending;
        Clock::time_point m_draftHeldUntil{}, m_failedSince{}, m_lastCache{}, m_lastFocus{};
        bool m_saving = false, m_updatingSliders = false, m_editing = false, m_offline = false, m_narrow = false, m_layoutDone = false;
        std::wstring m_moving;
        double m_armPid = 0; int m_armLeft = 0;
        Windows::UI::Xaml::DispatcherTimer m_saveTimer, m_pollTimer, m_armTimer, m_flashTimer;
        Windows::UI::Xaml::Controls::TextBlock m_powerValue{ nullptr }, m_cpuValue{ nullptr }, m_batteryValue{ nullptr };
        Windows::UI::Xaml::Controls::Grid m_pinnedFocus{ nullptr };
    };
}

namespace winrt::ClawWidget::factory_implementation
{
    struct WidgetPage : WidgetPageT<WidgetPage, implementation::WidgetPage> {};
}
