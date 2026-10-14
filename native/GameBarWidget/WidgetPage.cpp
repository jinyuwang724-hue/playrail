#include "pch.h"
#include "WidgetPage.h"
#if __has_include("WidgetPage.g.cpp")
#include "WidgetPage.g.cpp"
#endif

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::UI::Xaml;
using namespace Microsoft::Web::WebView2::Core;
using namespace Microsoft::Gaming::XboxGameBar;

namespace winrt::ClawWidget::implementation
{
    WidgetPage::WidgetPage()
    {
        InitializeComponent();
    }

    void WidgetPage::OnNavigatedTo(Navigation::NavigationEventArgs const& e)
    {
        m_widget = e.Parameter().try_as<XboxGameBarWidget>();
        StartAsync();
    }

    IAsyncAction WidgetPage::StartAsync()
    {
        auto strong = get_strong();
        // Transparent until the page paints, so Game Bar never flashes white (WinUI 2 has no property for it).
        SetEnvironmentVariableW(L"WEBVIEW2_DEFAULT_BACKGROUND_COLOR", L"0");
        co_await Web().EnsureCoreWebView2Async();
        auto core = Web().CoreWebView2();
        auto settings = core.Settings();
        settings.IsStatusBarEnabled(false);
        settings.AreDefaultContextMenusEnabled(false);
        settings.IsZoomControlEnabled(false);
        // Page files ship in the package; the agent's endpoint and token sit in this app's LocalState.
        auto package = Windows::ApplicationModel::Package::Current().InstalledLocation().Path();
        core.SetVirtualHostNameToFolderMapping(L"widget.claw", package + L"\\web", CoreWebView2HostResourceAccessKind::Allow);
        core.SetVirtualHostNameToFolderMapping(L"state.claw",
            Windows::Storage::ApplicationData::Current().LocalFolder().Path(), CoreWebView2HostResourceAccessKind::Allow);
        if (m_widget)
        {
            // Tell the page when Game Bar opens or closes, so it can poll only while visible.
            m_displayModeToken = m_widget.GameBarDisplayModeChanged([weak = get_weak()](auto&&, auto&&) {
                if (auto self = weak.get())
                    self->Dispatcher().RunAsync(Windows::UI::Core::CoreDispatcherPriority::Normal, [self]() {
                        bool open = self->m_widget.GameBarDisplayMode() == XboxGameBarDisplayMode::Foreground;
                        self->Post(open ? L"{\"gamebar\":\"foreground\"}" : L"{\"gamebar\":\"pinned\"}");
                        // Game Bar opened: bring this widget to the front with controller focus, the
                        // way ClawTweaks does, so the Xbox button lands here instead of on Home.
                        if (open) { self->FocusSelf(); self->Sink().Focus(FocusState::Programmatic); }
                    });
            });
        }
        // The touch keyboard came up on every open: Windows treats the focused WebView2 as a text
        // field. Hide it unless the page's own "显示键盘" tile just asked for it.
        core.WebMessageReceived([weak = get_weak()](auto&&, CoreWebView2WebMessageReceivedEventArgs const& args) {
            auto self = weak.get();
            if (!self) return;
            std::wstring_view json = args.WebMessageAsJson();
            if (json.find(L"keyboard") != std::wstring_view::npos)
                self->m_keyboardAllowedUntil = std::chrono::steady_clock::now() + std::chrono::seconds(3);
            if (json.find(L"\"canBack\":true") != std::wstring_view::npos) self->m_pageCanGoBack = true;
            else if (json.find(L"\"canBack\":false") != std::wstring_view::npos) self->m_pageCanGoBack = false;
        });
        // Controller focus stays on Sink; a tap may still focus the WebView2, so hand focus back.
        Sink().KeyDown({ this, &WidgetPage::OnSinkKeyDown });
        Web().GotFocus([weak = get_weak()](auto&&, auto&&) {
            if (auto self = weak.get()) self->Sink().Focus(FocusState::Programmatic);
        });
        Sink().Focus(FocusState::Programmatic);
        m_inputPaneToken = Windows::UI::ViewManagement::InputPane::GetForCurrentView().Showing(
            [weak = get_weak()](Windows::UI::ViewManagement::InputPane const& pane, auto&&) {
                auto self = weak.get();
                if (!self || std::chrono::steady_clock::now() > self->m_keyboardAllowedUntil) pane.TryHide();
            });
        core.Navigate(L"https://widget.claw/index.html");
    }

    void WidgetPage::FocusSelf()
    {
        auto now = std::chrono::steady_clock::now();
        if (now - m_lastFocus < std::chrono::seconds(2)) return;  // the launch itself re-raises Foreground
        m_lastFocus = now;
        Windows::System::Launcher::LaunchUriAsync(Uri(
            L"ms-gamebar://launchForeground/activate/ClawToolbox.GameBarWidget_ba3r68zr466za_App_Toolbox/"));
    }

    // Controller and arrow keys arrive here and go to the page as {"nav": ...}. B goes to the page only
    // while it has something to close (menu, edit mode); otherwise Game Bar keeps its own B.
    void WidgetPage::OnSinkKeyDown(IInspectable const&, Input::KeyRoutedEventArgs const& e)
    {
        using Windows::System::VirtualKey;
        const wchar_t* nav = nullptr;
        switch (e.Key())
        {
        case VirtualKey::GamepadDPadUp: case VirtualKey::GamepadLeftThumbstickUp: case VirtualKey::Up: nav = L"up"; break;
        case VirtualKey::GamepadDPadDown: case VirtualKey::GamepadLeftThumbstickDown: case VirtualKey::Down: nav = L"down"; break;
        case VirtualKey::GamepadDPadLeft: case VirtualKey::GamepadLeftThumbstickLeft: case VirtualKey::Left: nav = L"left"; break;
        case VirtualKey::GamepadDPadRight: case VirtualKey::GamepadLeftThumbstickRight: case VirtualKey::Right: nav = L"right"; break;
        case VirtualKey::GamepadA: case VirtualKey::Enter: case VirtualKey::Space: nav = L"a"; break;
        case VirtualKey::GamepadX: case VirtualKey::X: nav = L"x"; break;
        case VirtualKey::GamepadB: case VirtualKey::Escape: if (m_pageCanGoBack) nav = L"b"; break;
        default: break;
        }
        if (!nav) return;
        Post(hstring(L"{\"nav\":\"") + nav + L"\"}");
        e.Handled(true);
    }

    void WidgetPage::Post(hstring const& json)
    {
        if (auto core = Web().CoreWebView2()) core.PostWebMessageAsJson(json);
    }
}
