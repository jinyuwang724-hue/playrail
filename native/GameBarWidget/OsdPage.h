#pragma once
#include "OsdPage.g.h"
#include "Agent.h"
#include "GameTarget.h"
#include <memory>

namespace winrt::ClawWidget::implementation
{
    struct OsdPage : OsdPageT<OsdPage>
    {
        OsdPage();
        void OnNavigatedTo(Windows::UI::Xaml::Navigation::NavigationEventArgs const& e);
    private:
        using JsonObject = Windows::Data::Json::JsonObject;
        void Refresh();
        void Accept(JsonObject const& data);
        void Update();
        void Set(JsonObject const& settings);
        void AddMenu(wchar_t const* label, std::initializer_list<std::pair<wchar_t const*, std::function<void()>>> items);
        void Correct(wchar_t const* mode);
        Microsoft::Gaming::XboxGameBar::XboxGameBarWidget m_widget{ nullptr };
        std::shared_ptr<claw::Agent> m_agent;
        std::unique_ptr<claw::GameTarget> m_target;
        JsonObject m_data{ nullptr };
        Windows::UI::Xaml::DispatcherTimer m_timer;
        std::chrono::steady_clock::time_point m_received{};
        bool m_connected = false, m_pending = false;
        double m_height = 0;
        hstring m_message;
    };
}
namespace winrt::ClawWidget::factory_implementation
{
    struct OsdPage : OsdPageT<OsdPage, implementation::OsdPage> {};
}
