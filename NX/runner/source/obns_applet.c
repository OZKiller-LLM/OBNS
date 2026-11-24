// OBNS 1.32: the console's ways (OBNS/SWITCH.md, step 5) - the HOME menu, sleep and wake-up, docking - as libnx's applet
// messages, which nothing processed before (the user, 2026-11-12: "work on sleep and resume").
// Copyright (c) The OpenBVE Android port contributors. BSD-2-Clause, as OBNS.
//
// The system tells an application through applet messages that it lost the focus (the HOME menu, a system applet over
// it), got it back, woke from sleep or was docked; appletMainLoop takes one message each call and runs libnx's handling
// and the hooks registered for it. An application is suspended while out of focus (libnx's default focus handling
// for applications): its threads stop, and whatever measured time sees the whole stretch away at once when they go on.
// So the game asks here once a frame (and the loading screen while it draws) what happened since, and pauses, and leaves
// the time away out of the simulation.

#include <stdint.h>
#include <switch.h>

#define EXPORT __attribute__((visibility("default")))

// What obns_applet_poll reports (NX/OpenBve.NX/Applet.cs's AppletEvents): the bits since the last poll.
enum {
    EventFocusLost = 1 << 0,    // out of focus: the HOME menu, or an applet over the game
    EventFocusBack = 1 << 1,    // in focus again
    EventResumed = 1 << 2,      // woken from sleep
    EventDocking = 1 << 3,      // docked or undocked (the operation mode changed)
    EventPerformance = 1 << 4,  // the performance mode changed (docking, or the power)
    EventExit = 1 << 5          // the system asks the application to close
};

static Mutex lock;
static AppletHookCookie cookie;
static int hooked;
static volatile uint32_t pending;
static volatile uint32_t awayCount;
static int lastFocus = -1;

static void on_applet(AppletHookType hook, void *param)
{
    (void)param;
    switch (hook) {
        case AppletHookType_OnFocusState:
            if (appletGetFocusState() == AppletFocusState_InFocus) {
                pending |= EventFocusBack;
            } else {
                pending |= EventFocusLost;
                awayCount++;
            }
            break;
        case AppletHookType_OnResume:
            pending |= EventResumed;
            awayCount++;
            break;
        case AppletHookType_OnOperationMode:
            pending |= EventDocking;
            break;
        case AppletHookType_OnPerformanceMode:
            pending |= EventPerformance;
            break;
        case AppletHookType_OnExitRequest:
            pending |= EventExit;
            break;
        default:
            break;
    }
}

// Takes the applet's messages waiting now (a few a call at most, without waiting) and returns what happened since the
// last call (the Event bits). Any thread; one at a time.
EXPORT uint32_t obns_applet_poll(void)
{
    mutexLock(&lock);
    if (!hooked) {
        appletHook(&cookie, on_applet, NULL);
        hooked = 1;
    }

    for (int i = 0; i < 4; i++) {
        if (!appletMainLoop()) {
            pending |= EventExit;
            break;
        }
    }

    // The focus state as it is now, compared with the last poll's, besides the hook: a HOME press on 2026-11-16 left
    // nothing in the log, so the state itself is checked too (a change the messages didn't report still counts).
    int focus = (int)appletGetFocusState();
    if (lastFocus >= 0 && focus != lastFocus) {
        if (focus == AppletFocusState_InFocus) {
            pending |= EventFocusBack;
        } else {
            if (!(pending & EventFocusLost))
                awayCount++;
            pending |= EventFocusLost;
        }
    }
    lastFocus = focus;

    uint32_t events = pending;
    pending = 0;
    mutexUnlock(&lock);
    return events;
}

// How many times the game has been away (out of focus or asleep) since it started: the watchdog starts its count of a
// stuck frame again when this changes, since the time away is not a frame stuck.
EXPORT uint32_t obns_applet_away_count(void)
{
    return awayCount;
}

// Whether the game has the focus now.
EXPORT int obns_applet_in_focus(void)
{
    return appletGetFocusState() == AppletFocusState_InFocus;
}
