// OBNS 1.32: the console's buttons and touch screen for the menu (NX/Game/Menu/MenuInput.cs's MenuButtons), and
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
    MenuPlus = 1 << 12, MenuMinus = 1 << 13, MenuStickL = 1 << 14, MenuStickR = 1 << 15
};

// Whether the console is docked (the TV): the game then draws at 1920x1080, handheld at 1280x720 (OBNS/SWITCH.md).
EXPORT int obns_docked(void)
{
    return appletGetOperationMode() == AppletOperationMode_Console;
}

static void ensure_pad(void)
{
    if (!ready) {
        // Every controller, as player 1: handheld Joy-Cons, a pair, a Pro Controller.
        padConfigureInput(1, HidNpadStyleSet_NpadStandard);
        padInitializeDefault(&pad);
        hidInitializeTouchScreen();
        ready = 1;
    }
}

// For driving (NX/Game/SwitchPad.cs): libnx's own button bits (HidNpadButton: the face buttons, the stick clicks, L, R,
// ZL, ZR, + and -, the D-pad), and both sticks from -1 to 1 (up positive).
EXPORT uint64_t obns_input_pad(float *leftX, float *leftY, float *rightX, float *rightY)
{
    ensure_pad();
    padUpdate(&pad);
    HidAnalogStickState left = padGetStickPos(&pad, 0), right = padGetStickPos(&pad, 1);
    *leftX = left.x / 32767.0f;
    *leftY = left.y / 32767.0f;
    *rightX = right.x / 32767.0f;
    *rightY = right.y / 32767.0f;
    return padGetButtons(&pad);
}

// For the touch controls (NX/Game/Hud/TouchControls.cs): every finger on the touch screen (up to max), each with its
// id - the same while the finger stays down - and its place in 1280x720 screen units; and whether the console is
// docked (1) or handheld (0). Returns how many.
EXPORT int obns_input_touches(int max, uint32_t *ids, float *xs, float *ys, int *docked)
{
    ensure_pad();
    *docked = appletGetOperationMode() == AppletOperationMode_Console;
    HidTouchScreenState state = { 0 };
    if (!hidGetTouchScreenStates(&state, 1))
        return 0;
    int count = state.count < max ? state.count : max;
    for (int i = 0; i < count; i++) {
        ids[i] = state.touches[i].finger_id;
        xs[i] = (float)state.touches[i].x;
        ys[i] = (float)state.touches[i].y;
    }
    return count;
}

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
    if (held & HidNpadButton_StickL) buttons |= MenuStickL;
    if (held & HidNpadButton_StickR) buttons |= MenuStickR;

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

// --- Train controllers (OBNS/SWITCH.md, "Upstream's train controllers"; the user, 2026-11-12) ---
//
// The Switch's own Densha de GO! controllers - HORI's One Handle controller (0f0d:00c1), the ZUIKI Mascon (33dd:0001 to
// 0005) and the ZUIKI Mascon Pro (33dd:0006) - are licensed for the console, whose controller service takes them as
// wired pads: their HID reports' axes become the sticks and their buttons the pad's, so the lever (the report's Y axis,
// upstream's ZuikiInput's axis 1) is the left stick's Y. Which pad it is comes from the USB host service: if one of them
// is plugged in, the first full-key pad among players 1 to 8 is taken as it, and left out of the pad that drives the game
// otherwise (obns_input_pad), so the lever doesn't move the camera. *(verify on the console: the lever's sign and travel)*

static PadState masconPad;
static int masconId = -1;

// Which of them is plugged in, from the USB host service's list of interfaces: 0 none, 1 HORI's One Handle controller,
// 2 a ZUIKI Mascon, 3 the ZUIKI Mascon Pro; -1 if the USB host service can't be used.
EXPORT int obns_mascon_detect(void)
{
    if (R_FAILED(usbHsInitialize()))
        return -1;
    static UsbHsInterface interfaces[16];
    UsbHsInterfaceFilter filter = { 0 };
    s32 total = 0;
    int model = 0;
    if (R_SUCCEEDED(usbHsQueryAllInterfaces(&filter, interfaces, sizeof(interfaces), &total))) {
        for (int i = 0; i < total && i < 16; i++) {
            uint16_t vid = interfaces[i].device_desc.idVendor, pid = interfaces[i].device_desc.idProduct;
            int found = 0;
            if (vid == 0x0f0d && pid == 0x00c1)
                found = 1;
            else if (vid == 0x33dd && pid >= 0x0001 && pid <= 0x0005)
                found = 2;
            else if (vid == 0x33dd && pid == 0x0006)
                found = 3;
            if (found > model)
                model = found;
        }
    }
    usbHsExit();
    return model;
}

// Takes the first full-key pad among players 1 to 8 as the controller (1), or none (0): with one, the game's own pad
// (obns_input_pad) reads every other pad and the handheld Joy-Cons. Two players are configured, so a second controller
// can be used beside it.
EXPORT int obns_mascon_attach(int attach)
{
    ensure_pad();
    masconId = -1;
    if (attach) {
        padConfigureInput(2, HidNpadStyleSet_NpadStandard);
        for (int id = HidNpadIdType_No1; id <= HidNpadIdType_No8; id++) {
            if (hidGetNpadStyleSet((HidNpadIdType)id) & HidNpadStyleTag_NpadFullKey) {
                masconId = id;
                break;
            }
        }
    }

    if (masconId >= 0) {
        padInitializeWithMask(&masconPad, 1UL << masconId);
        padInitializeWithMask(&pad, PAD_ANY_ID_MASK & ~(1UL << masconId));
    } else {
        padInitializeDefault(&pad);
    }

    return masconId >= 0;
}

// The controller's state: the sticks from -1 to 1 (up and right positive, as libnx's), libnx's button bits. 0 if it
// isn't connected.
EXPORT int obns_mascon_read(float *leftX, float *leftY, float *rightX, float *rightY, uint64_t *buttons)
{
    if (masconId < 0)
        return 0;
    padUpdate(&masconPad);
    if (!padIsConnected(&masconPad))
        return 0;
    HidAnalogStickState left = padGetStickPos(&masconPad, 0), right = padGetStickPos(&masconPad, 1);
    *leftX = left.x / 32767.0f;
    *leftY = left.y / 32767.0f;
    *rightX = right.x / 32767.0f;
    *rightY = right.y / 32767.0f;
    *buttons = padGetButtons(&masconPad);
    return 1;
}
