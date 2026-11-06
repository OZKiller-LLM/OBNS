// OBNS 1.31N: the console's buttons and touch screen for the menu (NX/Game/Menu/MenuInput.cs's MenuButtons), and
// whether it is docked. Copyright (c) The OpenBVE Android port contributors. BSD-2-Clause, as OBNS.

#include <stdint.h>
#include <switch.h>

#define EXPORT __attribute__((visibility("default")))

static PadState pad;
static int ready;

// The menu's button bits (MenuButtons), the D-pad and either stick alike.
enum {
    MenuUp = 1 << 0, MenuDown = 1 << 1, MenuLeft = 1 << 2, MenuRight = 1 << 3,
    MenuA = 1 << 4, MenuB = 1 << 5, MenuX = 1 << 6, MenuY = 1 << 7,
    MenuL = 1 << 8, MenuR = 1 << 9, MenuZL = 1 << 10, MenuZR = 1 << 11,
    MenuPlus = 1 << 12, MenuMinus = 1 << 13
};

// The buttons held now, the touch screen's first finger (in 1280x720 screen units), and whether the console is docked
// (1) or handheld (0).
EXPORT uint32_t obns_input_read(int *touching, float *x, float *y, int *docked)
{
    if (!ready) {
        // Every controller, as player 1: handheld Joy-Cons, a pair, a Pro Controller.
        padConfigureInput(1, HidNpadStyleSet_NpadStandard);
        padInitializeDefault(&pad);
        hidInitializeTouchScreen();
        ready = 1;
    }

    padUpdate(&pad);
    uint64_t held = padGetButtons(&pad);
    uint32_t buttons = 0;
    if (held & (HidNpadButton_Up | HidNpadButton_StickLUp | HidNpadButton_StickRUp)) buttons |= MenuUp;
    if (held & (HidNpadButton_Down | HidNpadButton_StickLDown | HidNpadButton_StickRDown)) buttons |= MenuDown;
    if (held & (HidNpadButton_Left | HidNpadButton_StickLLeft | HidNpadButton_StickRLeft)) buttons |= MenuLeft;
    if (held & (HidNpadButton_Right | HidNpadButton_StickLRight | HidNpadButton_StickRRight)) buttons |= MenuRight;
    if (held & HidNpadButton_A) buttons |= MenuA;
    if (held & HidNpadButton_B) buttons |= MenuB;
    if (held & HidNpadButton_X) buttons |= MenuX;
    if (held & HidNpadButton_Y) buttons |= MenuY;
    if (held & HidNpadButton_L) buttons |= MenuL;
    if (held & HidNpadButton_R) buttons |= MenuR;
    if (held & HidNpadButton_ZL) buttons |= MenuZL;
    if (held & HidNpadButton_ZR) buttons |= MenuZR;
    if (held & HidNpadButton_Plus) buttons |= MenuPlus;
    if (held & HidNpadButton_Minus) buttons |= MenuMinus;

    HidTouchScreenState state = { 0 };
    *touching = 0;
    if (hidGetTouchScreenStates(&state, 1) && state.count > 0) {
        *touching = 1;
        *x = (float)state.touches[0].x;
        *y = (float)state.touches[0].y;
    }

    *docked = appletGetOperationMode() == AppletOperationMode_Console;
    return buttons;
}
