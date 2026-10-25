#ifndef CORE_LIBS_PRX_LIBSCEPAD_PADINPUTTYPES_HPP
#define CORE_LIBS_PRX_LIBSCEPAD_PADINPUTTYPES_HPP

#include <array>
#include <cstdint>
#include <string>
#include <vector>
#include "SDL_scancode.h"
#include "SDL_mouse.h"

namespace Pad {
enum class InputControl {
    Button,
    LeftStickLeft,
    LeftStickRight,
    LeftStickUp,
    LeftStickDown,
    RightStickLeft,
    RightStickRight,
    RightStickUp,
    RightStickDown,
    TouchLeft,
    TouchRight,
    ToggleMouse,
    ToggleFullscreen,
    Steering,
    Throttle,
    Brake,
    Clutch
};

enum class PadButton : std::uint32_t {
    None = 0,
    L3 = 0x0002,
    R3 = 0x0004,
    Options = 0x0008,
    Up = 0x0010,
    Right = 0x0020,
    Down = 0x0040,
    Left = 0x0080,
    L2 = 0x0100,
    R2 = 0x0200,
    L1 = 0x0400,
    R1 = 0x0800,
    Triangle = 0x1000,
    Circle = 0x2000,
    Cross = 0x4000,
    Square = 0x8000,
    TouchPad = 0x100000
};

inline bool IsDpad(PadButton button) {
    return button == PadButton::Up || button == PadButton::Right || button == PadButton::Down || button == PadButton::Left;
}

inline bool IsStick(InputControl control) {
    return control == InputControl::LeftStickLeft || control == InputControl::LeftStickRight ||
           control == InputControl::LeftStickUp || control == InputControl::LeftStickDown ||
           control == InputControl::RightStickLeft || control == InputControl::RightStickRight ||
           control == InputControl::RightStickUp || control == InputControl::RightStickDown;
}

// An action an axis can point: a dpad direction or a stick direction.
inline bool IsDirectional(InputControl control, PadButton button) {
    return IsDpad(button) || IsStick(control);
}

enum class MouseButton : std::uint8_t {
    None = 0,
    Left = SDL_BUTTON_LEFT,
    Middle = SDL_BUTTON_MIDDLE,
    Right = SDL_BUTTON_RIGHT,
    X1 = SDL_BUTTON_X1,
    X2 = SDL_BUTTON_X2
};

struct InputBinding {
    SDL_Scancode key;
    MouseButton mouseButton;
    InputControl control;
    PadButton button = PadButton::None;
    int wheelDirection = 0;
};

// A wheel, pedal set or other joystick that SDL does not treat as a game controller.
// Only one of axis, button or hat is set. An empty device means the primary wheel; otherwise
// the first joystick whose name contains it (case-insensitive).
struct JoystickBinding {
    InputControl control;
    PadButton button = PadButton::None;
    int axis = -1;
    int joystickButton = -1;
    std::uint8_t hat = 0;
    bool invert = false;
    std::string device;
};

struct InputConfig {
    std::vector<InputBinding> bindings;
    std::vector<JoystickBinding> joystickBindings;
    // Actions the file bound to a joystick source, which replace the built-in wheel defaults.
    std::vector<InputControl> joystickControls;
    std::vector<PadButton> joystickButtons;
};

}

#endif
