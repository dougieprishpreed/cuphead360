#include "platform/display.h"
#include "core/log.h"

#if defined(_XBOX)
#include <xtl.h>
#include <d3d9.h>
#ifndef D3DFVF_XYZRHW
#define D3DFVF_XYZRHW 0x004
#endif
#else
#include <windows.h>
#endif

#include <string.h>

namespace ch360 {

#if defined(_XBOX)

static IDirect3DDevice9* g_device = 0;
static IDirect3DTexture9* g_texture = 0;
static u32 g_width = 0;
static u32 g_height = 0;
static bool g_quit = false;

struct ScreenVertex {
    f32 x, y, z, w;
    f32 u, v;
};

static const DWORD kFvf = D3DFVF_XYZRHW | D3DFVF_TEX1;

bool DisplayInit(u32 width, u32 height, bool hidden) {
    (void)hidden;
    g_width = width;
    g_height = height;
    IDirect3D9* d3d = Direct3DCreate9(D3D_SDK_VERSION);
    if (!d3d) return false;
    D3DPRESENT_PARAMETERS pp;
    ZeroMemory(&pp, sizeof(pp));
    pp.BackBufferWidth = width;
    pp.BackBufferHeight = height;
    pp.BackBufferFormat = D3DFMT_A8R8G8B8;
    pp.BackBufferCount = 1;
    pp.MultiSampleType = D3DMULTISAMPLE_NONE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.PresentationInterval = D3DPRESENT_INTERVAL_ONE;
    HRESULT hr = d3d->CreateDevice(0, D3DDEVTYPE_HAL, NULL,
                                   D3DCREATE_HARDWARE_VERTEXPROCESSING, &pp, &g_device);
    d3d->Release();
    if (FAILED(hr)) return false;
    if (FAILED(g_device->CreateTexture(width, height, 1, D3DUSAGE_CPU_CACHED_MEMORY,
                                       D3DFMT_A8R8G8B8, D3DPOOL_MANAGED,
                                       &g_texture, NULL))) {
        return false;
    }
    g_device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
    g_device->SetRenderState(D3DRS_ZENABLE, FALSE);
    g_device->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
    g_device->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_ONE);
    g_device->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_ZERO);
    g_device->SetSamplerState(0, D3DSAMP_MINFILTER, D3DTEXF_POINT);
    g_device->SetSamplerState(0, D3DSAMP_MAGFILTER, D3DTEXF_POINT);
    g_device->SetSamplerState(0, D3DSAMP_ADDRESSU, D3DTADDRESS_CLAMP);
    g_device->SetSamplerState(0, D3DSAMP_ADDRESSV, D3DTADDRESS_CLAMP);
    LogInfo("display", "D3D9 %ux%u initialized", width, height);
    return true;
}

void DisplayShutdown() {
    if (g_texture) { g_texture->Release(); g_texture = 0; }
    if (g_device) { g_device->Release(); g_device = 0; }
}

void DisplayPresent(const u32* pixels) {
    if (!g_device || !g_texture || !pixels) return;
    D3DLOCKED_RECT lock;
    if (SUCCEEDED(g_texture->LockRect(0, &lock, NULL, 0))) {
        u8* dst = (u8*)lock.pBits;
        const u32* src = pixels;
        for (u32 y = 0; y < g_height; ++y) {
            memcpy(dst, src, g_width * 4);
            dst += lock.Pitch;
            src += g_width;
        }
        g_texture->UnlockRect(0);
    }
    g_device->Clear(0, NULL, D3DCLEAR_TARGET, D3DCOLOR_XRGB(0, 0, 0), 1.0f, 0);
    g_device->BeginScene();
    ScreenVertex v[4] = {
        { -0.5f, -0.5f, 0.0f, 1.0f, 0.0f, 0.0f },
        { (f32)g_width - 0.5f, -0.5f, 0.0f, 1.0f, 1.0f, 0.0f },
        { -0.5f, (f32)g_height - 0.5f, 0.0f, 1.0f, 0.0f, 1.0f },
        { (f32)g_width - 0.5f, (f32)g_height - 0.5f, 0.0f, 1.0f, 1.0f, 1.0f },
    };
    g_device->SetTexture(0, g_texture);
    g_device->SetFVF(kFvf);
    g_device->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, v, sizeof(ScreenVertex));
    g_device->EndScene();
    g_device->Present(NULL, NULL, NULL, NULL);
}

void DisplayPump() {
    XINPUT_STATE state;
    if (XInputGetState(0, &state) == ERROR_SUCCESS) {
        if (state.Gamepad.wButtons & XINPUT_GAMEPAD_START) g_quit = true;
    }
}

bool DisplayShouldQuit() { return g_quit; }

#else // Win32

static HWND g_hwnd = 0;
static u32 g_width = 0;
static u32 g_height = 0;
static bool g_quit = false;
static bool g_hidden = true;

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam) {
    switch (msg) {
        case WM_CLOSE: g_quit = true; return 0;
        case WM_DESTROY: g_quit = true; PostQuitMessage(0); return 0;
        case WM_KEYDOWN:
            if (wparam == VK_ESCAPE) g_quit = true;
            return 0;
        default: return DefWindowProcA(hwnd, msg, wparam, lparam);
    }
}

bool DisplayInit(u32 width, u32 height, bool hidden) {
    g_width = width;
    g_height = height;
    g_hidden = hidden;
    g_quit = false;
    if (hidden) return true;
    HINSTANCE inst = GetModuleHandleA(NULL);
    WNDCLASSA wc;
    ZeroMemory(&wc, sizeof(wc));
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.lpszClassName = "Cuphead360Display";
    RegisterClassA(&wc);
    RECT rect = { 0, 0, (LONG)width, (LONG)height };
    AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE);
    g_hwnd = CreateWindowA("Cuphead360Display", "Cuphead360", WS_OVERLAPPEDWINDOW,
                           CW_USEDEFAULT, CW_USEDEFAULT,
                           rect.right - rect.left, rect.bottom - rect.top,
                           NULL, NULL, inst, NULL);
    if (!g_hwnd) return false;
    ShowWindow(g_hwnd, SW_SHOW);
    UpdateWindow(g_hwnd);
    return true;
}

void DisplayShutdown() {
    if (g_hwnd) {
        DestroyWindow(g_hwnd);
        g_hwnd = 0;
    }
}

void DisplayPresent(const u32* pixels) {
    if (g_hidden || !g_hwnd || !pixels) return;
    HDC dc = GetDC(g_hwnd);
    BITMAPINFO bmi;
    ZeroMemory(&bmi, sizeof(bmi));
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = (LONG)g_width;
    bmi.bmiHeader.biHeight = -(LONG)g_height;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;
    StretchDIBits(dc, 0, 0, (int)g_width, (int)g_height,
                  0, 0, (int)g_width, (int)g_height,
                  pixels, &bmi, DIB_RGB_COLORS, SRCCOPY);
    ReleaseDC(g_hwnd, dc);
}

void DisplayPump() {
    if (g_hidden) return;
    MSG msg;
    while (PeekMessageA(&msg, NULL, 0, 0, PM_REMOVE)) {
        if (msg.message == WM_QUIT) g_quit = true;
        TranslateMessage(&msg);
        DispatchMessageA(&msg);
    }
}

bool DisplayShouldQuit() { return g_quit; }

#endif

} // namespace ch360
