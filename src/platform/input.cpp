#include "platform/input.h"

#if defined(_XBOX)
#include <xtl.h>
#else
#include <windows.h>
#include <xinput.h>
#endif

namespace ch360 {

static InputState g_prev;

void InputInit() { g_prev = InputState(); }
void InputPump() {}

InputState InputRead() {
    InputState s;
#if defined(_XBOX)
    XINPUT_STATE xs;
    if (XInputGetState(0, &xs) == ERROR_SUCCESS) {
        s.axis_x = (f32)xs.Gamepad.sThumbLX / 32767.0f;
        s.axis_y = (f32)xs.Gamepad.sThumbLY / 32767.0f;
        s.jump_held = (xs.Gamepad.wButtons & XINPUT_GAMEPAD_A) != 0;
        s.jump_pressed = s.jump_held && !g_prev.jump_held;
        s.dash_pressed = (xs.Gamepad.wButtons & XINPUT_GAMEPAD_X) != 0 && !g_prev.dash_pressed;
        s.start_pressed = (xs.Gamepad.wButtons & XINPUT_GAMEPAD_START) != 0;
        if (xs.Gamepad.wButtons & XINPUT_GAMEPAD_BACK) s.quit_pressed = true;
    }
#else
    if (GetAsyncKeyState('A') & 0x8000) s.axis_x -= 1.0f;
    if (GetAsyncKeyState('D') & 0x8000) s.axis_x += 1.0f;
    if (GetAsyncKeyState('W') & 0x8000) s.axis_y += 1.0f;
    if (GetAsyncKeyState('S') & 0x8000) s.axis_y -= 1.0f;
    s.jump_held = (GetAsyncKeyState(VK_SPACE) & 0x8000) != 0;
    s.jump_pressed = s.jump_held && !g_prev.jump_held;
    bool dash = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
    s.dash_pressed = dash && !g_prev.dash_pressed;
    s.start_pressed = (GetAsyncKeyState(VK_RETURN) & 0x8000) != 0;
    s.quit_pressed = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
#endif
    g_prev = s;
    return s;
}

} // namespace ch360
