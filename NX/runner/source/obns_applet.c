// OBNS 1.32: the console's ways (OBNS/SWITCH.md, step 5) - the HOME menu, sleep and wake-up, docking - as libnx's applet
// messages, which nothing processed before (the user, 2026-11-12: "work on sleep and resume").
// Copyright (c) The OpenBVE Android port contributors. BSD-2-Clause, as OBNS.
//
// The system tells an application through applet messages that it lost the focus (the HOME menu, a system applet over
// it), got it back, woke from sleep or was docked; appletMainLoop takes one message each call and runs libnx's handling
// and the hooks registered for it. With libnx's default focus handling for applications (SuspendHomeSleep) the system
// suspends the game out of focus and tells it nothing, and SuspendHomeSleepNotify did no better on the console (the
// tests of 2026-11-24: not one message while driving). So the game is not suspended (NoSuspend): it hears the focus go and come back and waits itself, no frames drawn and the sound paused, until it is
// back (NX/OpenBve.NX/Applet.cs, WaitForFocus). The game asks here once a frame (the menu and the loading screen too)
// what happened since.

#include <stdint.h>
#include <stdio.h>
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

// The console's messages as they came, raw (AppletMessage numbers), for the log: on 2026-11-24 neither a HOME press nor
// sleep reached the hooks, so what arrives at all is recorded (obns_applet_messages).
#define MESSAGES 32
static uint32_t messages[MESSAGES];
static volatile uint32_t messagesIn, messagesOut;

static Thread listener;
static volatile int listening;

// A thread of its own waiting on the console's message event: each message is taken and handled (libnx's hooks: on_applet)
// the moment it is signalled, whatever the game's thread is doing - a frame, a load - and not only when a frame polls.
static void listen(void *arg)
{
    (void)arg;
    Event *event = appletGetMessageEvent();
    while (listening) {
        // The handle waited on directly, not eventWait: that would reset an autoclearing event, and appletGetMessage,
        // which checks the same event before it reads, would then find nothing.
        if (R_FAILED(waitSingleHandle(event->revent, 500000000ULL)))
            continue;
        int taken = 0;
        for (int i = 0; i < 16; i++) {
            u32 msg = 0;
            mutexLock(&lock);
            if (R_FAILED(appletGetMessage(&msg))) {
                mutexUnlock(&lock);
                break;
            }
            taken++;
            messages[messagesIn % MESSAGES] = msg;
            messagesIn++;
            if (!appletProcessMessage(msg))
                pending |= EventExit;
            mutexUnlock(&lock);
        }
        // Signalled but nothing to read: not straight back to the wait, which would return at once.
        if (taken == 0)
            svcSleepThread(50000000ULL);
    }
}

// The raw messages since the last call, oldest first, at most max of them; returns how many.
EXPORT int obns_applet_messages(uint32_t *out, int max)
{
    mutexLock(&lock);
    int count = 0;
    if (messagesIn - messagesOut > MESSAGES)
        messagesOut = messagesIn - MESSAGES;
    while (messagesOut != messagesIn && count < max) {
        out[count++] = messages[messagesOut % MESSAGES];
        messagesOut++;
    }
    mutexUnlock(&lock);
    return count;
}

// Every message waiting when called, taken here (the listener's own read can miss one whose event its wait cleared).
static void drain(void)
{
    for (int i = 0; i < 4; i++) {
        u32 msg = 0;
        if (R_FAILED(appletGetMessage(&msg)))
            break;
        messages[messagesIn % MESSAGES] = msg;
        messagesIn++;
        if (!appletProcessMessage(msg))
            pending |= EventExit;
    }
}

// Returns what happened since the last call (the Event bits). Any thread.
EXPORT uint32_t obns_applet_poll(void)
{
    mutexLock(&lock);
    if (!hooked) {
        // Not suspended out of focus: the suspending modes sent no focus message at all on the console (2026-11-24,
        // SuspendHomeSleep and SuspendHomeSleepNotify alike), so the game waits itself (the header's comment).
        Result mode = appletSetFocusHandlingMode(AppletFocusHandlingMode_NoSuspend);
        printf("OBNS applet: focus handling NoSuspend: %s (0x%X)\n", R_SUCCEEDED(mode) ? "set" : "failed", (unsigned)mode);
        appletHook(&cookie, on_applet, NULL);
        hooked = 1;
        listening = 1;
        if (R_FAILED(threadCreate(&listener, listen, NULL, NULL, 0x4000, 0x2B, -2)) || R_FAILED(threadStart(&listener)))
            listening = 0;
    }

    drain();

    // The focus state as libnx last read it, compared with the last poll's (it changes only as a FocusStateChanged
    // message is handled, so this is a second look at the same messages).
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
