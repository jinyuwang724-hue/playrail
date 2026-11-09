#pragma once
#include "WidgetPage.g.h"
#include "Agent.h"
#include "GameTarget.h"
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
        void Shutdown();
        bool IsShutdown() const { return m_shutdown; }
        void OnNavigatedTo(Windows::UI::Xaml::Navigation::NavigationEventArgs const& e);

    private:
        using Clock = std::chrono::steady_clock;
        using JsonObject = Windows::Data::Json::JsonObject;
        using IJsonValue = Windows::Data::Json::IJsonValue;
        using JsonArray = Windows::Data::Json::JsonArray;

        struct Level { int index = -1, count = 0; hstring value; bool disabled = false; };
        struct TileView {
            Windows::UI::Xaml::Controls::Button button{ nullptr };
            Windows::UI::Xaml::Controls::TextBlock title{ nullptr };
            Windows::UI::Xaml::Controls::TextBlock value{ nullptr };
            Windows::UI::Xaml::Controls::StackPanel dots{ nullptr };
            Windows::UI::Xaml::Controls::Grid top{ nullptr };
        };
        struct Hold { IJsonValue value; std::wstring key; Clock::time_point until; };
        struct Pending { std::wstring method; JsonArray args; };

        // setup
        winrt::fire_and_forget RequestAnimatedPower(hstring action, bool preview = false);
        bool m_powerTransition = false;
        winrt::fire_and_forget PublishPowerVisibility();
        bool m_powerVisibilityWriting = false, m_powerVisibilityDirty = false;
        hstring m_lastPowerVisibility;
        void BuildRail();
        void BuildPowerControl();
        void UpdatePowerControl(bool usable);
        void BuildQuickShortcuts();
        winrt::fire_and_forget OpenQuickLink(hstring uri);
        void OpenScaling(std::wstring engine);
        void RefreshScaling();
        void UpdateScaling();
        void ScaleAction();
        void SetMagpieOption(std::wstring const& key);
        void ShowLosslessOptions(Windows::UI::Xaml::Controls::Button const& anchor, std::wstring const& key);
        void ShowLosslessProfiles();
        JsonObject LosslessProfile();
        std::wstring LosslessRateKey();
        IJsonValue LosslessValue(std::wstring const& key);
        void ApplyLosslessEdits();
        void SendScaling(std::wstring const& method, JsonArray const& args = JsonArray());
        void BuildTiles();
        void AddOsdOptions(Windows::UI::Xaml::Controls::Button const& button);
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
        void ShowBanners();
        void SetEditing(bool editing);
        void MoveTile(int dx, int dy);
        bool MoveMainFocus(Windows::UI::Xaml::Controls::Control const& from, int dx, int dy);
        void OnPreviewKey(Windows::Foundation::IInspectable const&, Windows::UI::Xaml::Input::KeyRoutedEventArgs const& e);
        void SetRailAdjustment(Windows::UI::Xaml::Controls::Slider const& slider);
        void TogglePadMode();

        Microsoft::Gaming::XboxGameBar::XboxGameBarWidget m_widget{ nullptr };
        std::shared_ptr<claw::Agent> m_agent;
        std::unique_ptr<claw::GameTarget> m_target;
        JsonObject m_data{ nullptr };
        JsonObject m_draft{ nullptr };
        std::vector<std::wstring> m_tiles;
        std::map<std::wstring, TileView> m_views;
        std::map<std::wstring, Hold> m_holds;
        std::map<std::wstring, std::optional<Pending>> m_sending;
        Clock::time_point m_draftHeldUntil{}, m_failedSince{}, m_lastCache{};
        Clock::time_point m_scalingAt{};
        std::wstring m_scalingPage;
        hstring m_scalingError;
        JsonObject m_scalingData{ nullptr };
        JsonObject m_losslessEdits;
        double m_losslessProfile = -1;
        hstring m_losslessRevision;
        bool m_scalingReading = false, m_scalingSending = false;
        bool m_saving = false, m_updatingSliders = false, m_editing = false, m_offline = false, m_narrow = false, m_layoutDone = false, m_shutdown = false;
        bool m_modeSwitching = false;
        bool m_updatingPower = false;
        winrt::weak_ref<Windows::UI::Xaml::Controls::Control> m_directionalFocus;
        Windows::UI::Xaml::Controls::Slider m_railAdjustment{ nullptr };
        Windows::Foundation::Point m_focusAnchor{};
        std::wstring m_moving;
        double m_armPid = 0; int m_armLeft = 0;
        Windows::UI::Xaml::DispatcherTimer m_saveTimer, m_pollTimer, m_armTimer, m_flashTimer;
        Windows::UI::Xaml::Controls::Grid m_pinnedFocus{ nullptr };
        Windows::UI::Xaml::Controls::Button m_quickFreeze{ nullptr }, m_quickTaskManager{ nullptr }, m_quickPad{ nullptr };
        Windows::UI::Xaml::Controls::Button m_quickExit{ nullptr };
        Windows::UI::Xaml::Controls::TextBlock m_quickExitLabel{ nullptr };
    };
}

namespace winrt::ClawWidget::factory_implementation
{
    struct WidgetPage : WidgetPageT<WidgetPage, implementation::WidgetPage> {};
}
