#include "pch.h"
#include "WidgetPage.h"
#if __has_include("WidgetPage.g.cpp")
#include "WidgetPage.g.cpp"
#endif

namespace winrt::ClawWidget::implementation
{
    WidgetPage::WidgetPage()
    {
        InitializeComponent();
    }
}
