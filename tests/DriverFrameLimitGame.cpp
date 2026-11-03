#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdio>

LRESULT CALLBACK LimiterWindow(HWND window, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_CLOSE) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProc(window, message, w, l);
}
// Short-lived DX11 workload. Counts the game's own Present calls, without OSD software.
int main() {
    HINSTANCE instance = GetModuleHandle(nullptr);
    WNDCLASSW wc{}; wc.lpfnWndProc = LimiterWindow; wc.hInstance = instance; wc.lpszClassName = L"ClawDriverLimiterTest";
    RegisterClassW(&wc);
    HWND window = CreateWindowW(wc.lpszClassName, L"Claw driver limiter verification", WS_OVERLAPPEDWINDOW,
        80, 80, 480, 320, nullptr, nullptr, instance, nullptr);
    if (!window) return 1;
    ShowWindow(window, SW_SHOWNOACTIVATE);
    DXGI_SWAP_CHAIN_DESC desc{}; desc.BufferCount = 2; desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.OutputWindow = window; desc.SampleDesc.Count = 1;
    desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    ID3D11Device* device = nullptr; ID3D11DeviceContext* context = nullptr; IDXGISwapChain* chain = nullptr;
    if (FAILED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, &context))) return 2;
    ID3D11Texture2D* buffer = nullptr; ID3D11RenderTargetView* view = nullptr;
    HRESULT hr = chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&buffer));
    if (SUCCEEDED(hr)) hr = device->CreateRenderTargetView(buffer, nullptr, &view);
    if (buffer) buffer->Release();
    if (FAILED(hr)) return 3;
    LARGE_INTEGER frequency, began, mark, now; QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&began);
    mark = began; unsigned frames = 0; MSG message{};
    while (message.message != WM_QUIT) {
        while (PeekMessage(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessage(&message); }
        if (message.message == WM_QUIT) break;
        float color[] = { .1f, .16f, .25f, 1.f }; context->ClearRenderTargetView(view, color);
        if (FAILED(chain->Present(0, 0))) return 4;
        QueryPerformanceCounter(&now);
        double elapsed = double(now.QuadPart - began.QuadPart) / frequency.QuadPart;
        if (elapsed < 2) { mark = now; frames = 0; }
        else ++frames;
        if (elapsed >= 7) {
            double seconds = double(now.QuadPart - mark.QuadPart) / frequency.QuadPart;
            printf("{\"fps\":%.4f,\"frames\":%u,\"seconds\":%.4f,\"rtssInjected\":%s}\n",
                frames / seconds, frames, seconds, GetModuleHandleW(L"RTSSHooks64.dll") ? "true" : "false");
            break;
        }
    }
    view->Release(); context->Release(); device->Release(); chain->Release(); DestroyWindow(window);
    return 0;
}
