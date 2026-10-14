#pragma once
#include "WidgetPage.g.h"

namespace winrt::ClawWidget::implementation
{
    // The widget UI is a web page (web\index.html in the package) shown in WebView2; it talks to
    // the toolbox agent itself over a local WebSocket. This page only hosts it.
    struct WidgetPage : WidgetPageT<WidgetPage>
    {
        WidgetPage();
        void OnNavigatedTo(Windows::UI::Xaml::Navigation::NavigationEventArgs const& e);

    private:
        Windows::Foundation::IAsyncAction StartAsync();
        void Post(hstring const& json);
        void FocusSelf();
        std::chrono::steady_clock::time_point m_lastFocus{};
        std::chrono::steady_clock::time_point m_keyboardAllowedUntil{};
        event_token m_inputPaneToken{};

        Microsoft::Gaming::XboxGameBar::XboxGameBarWidget m_widget{ nullptr };
        event_token m_displayModeToken{};
    };
}

namespace winrt::ClawWidget::factory_implementation
{
    struct WidgetPage : WidgetPageT<WidgetPage, implementation::WidgetPage> {};
}
