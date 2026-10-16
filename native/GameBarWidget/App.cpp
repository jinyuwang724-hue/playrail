#include "pch.h"
#include "App.h"
#include "WidgetPage.h"

using namespace winrt;
using namespace Windows::ApplicationModel::Activation;
using namespace Windows::UI::Xaml;
using namespace Windows::UI::Xaml::Controls;
using namespace Microsoft::Gaming::XboxGameBar;

namespace winrt::ClawWidget::implementation
{
    App::App()
    {
        InitializeComponent();
    }

    // Game Bar starts the widget through the ms-gamebarwidget protocol.
    void App::OnActivated(IActivatedEventArgs const& args)
    {
        if (args.Kind() != ActivationKind::Protocol) return;
        auto protocol = args.as<IProtocolActivatedEventArgs>();
        if (protocol.Uri().SchemeName() != L"ms-gamebarwidget") return;
        auto widgetArgs = args.as<XboxGameBarWidgetActivatedEventArgs>();
        if (!widgetArgs.IsLaunchActivation()) return;

        Frame frame;
        Window::Current().Content(frame);
        m_widget = XboxGameBarWidget(widgetArgs, Window::Current().CoreWindow(), frame);
        frame.Navigate(xaml_typename<ClawWidget::WidgetPage>(), m_widget);
        Window::Current().Closed([this](auto&&, auto&&) { m_widget = nullptr; });
        Window::Current().Activate();
    }

    // Started from the Start menu instead of Game Bar: there is nothing to show outside Game Bar.
    void App::OnLaunched(LaunchActivatedEventArgs const&)
    {
        Frame frame;
        Window::Current().Content(frame);
        frame.Navigate(xaml_typename<ClawWidget::WidgetPage>());
        Window::Current().Activate();
    }
}
