#pragma once
// Claw integration, GPL-3.0-or-later, 2026. Anonymous pipes belong to the parent.
namespace Magpie::ClawControl {
constexpr UINT Message = WM_APP + 171;
bool Initialize(const wchar_t* arguments);
void Dispatch();
void Uninitialize();
}
