#ifndef CH360_PLATFORM_INPUT_H
#define CH360_PLATFORM_INPUT_H

#include "core/types.h"

namespace ch360 {

struct InputState {
    f32 axis_x;
    f32 axis_y;
    bool jump_pressed;
    bool jump_held;
    bool shoot_held;
    bool dash_pressed;
    bool start_pressed;
    bool quit_pressed;

    InputState() : axis_x(0), axis_y(0), jump_pressed(false), jump_held(false),
                   shoot_held(false),
                   dash_pressed(false), start_pressed(false), quit_pressed(false) {}
};

void InputInit();
void InputPump();
InputState InputRead();

} // namespace ch360

#endif // CH360_PLATFORM_INPUT_H
