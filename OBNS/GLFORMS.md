# The main menu drawn with OpenGL (GLforms)

On 64-bit macOS, Windows Forms cannot show a window, so upstream replaces its main menu (formMain) with a much smaller
menu drawn in the game window. OBNS instead shows formMain itself: every page, control and layout of the Windows menu,
drawn with OpenGL in a window of its own, before the game's window opens, exactly as formMain is shown on Windows.

## How

- **The forms are upstream's.** `OBNS/glforms.ps1` (run by `build-desktop.ps1`) copies formMain and the dialogs it
  opens (About, bug report, image view and export, RailDriver calibration, log view) from the build's mirror into
  `OBNS/build/glforms`, renamed (`formMain` becomes `GLformMain`) and with `System.Windows.Forms` replaced by
  `OpenBve.GLForms`. Their designer code, layout and logic are unchanged; an upstream update to formMain reaches the menu
  at the next build. The only other change points the few calls that hand a Windows Forms control to the rest of
  OpenBVE (the language and cursor lists, an input device plugin's settings) at OBNS's equivalents (`Interop.cs`).
- **The controls are OBNS's** (`OBNS/overrides/source/OpenBVE/UserInterface/GLForms`): the part of Windows Forms these
  forms use, with the same names, members and events - Control with anchoring and docking, Form with modal
  `ShowDialog`, labels, links, buttons, check and radio boxes (as toggle buttons too), group boxes, panels (scrolling),
  split containers, picture boxes, track bars, text boxes (one line or several, editable), rich text boxes (links
  clickable), numeric up-downs, combo boxes (lists or editable, owner-drawn items, data binding), list views with
  columns and image lists (scrolling both ways), data grids, tab controls, context menus, tool tips and timers. A
  control paints itself with System.Drawing into a bitmap of its own, kept until it changes; the host (`Host.cs`) draws
  the bitmaps with OpenGL 3.3 (core, forward compatible, as the game's context) and routes the mouse and keyboard to
  the controls as Windows does (focus, Tab order, Enter and Escape for the form's buttons, double clicks, the wheel).
- **Dialogs** (message boxes, file and folder choosers, the About box) are drawn in the same window, over the menu,
  modal: `ShowDialog` runs the loop until they close, as Windows Forms' does. The file and folder choosers are OBNS's
  (places on the left - home, desktop, documents, OpenBVE's folders, drives or volumes - the folder's contents, a name
  and the filter). Files can also be dropped on the window (package installation).
- **Threads:** the menu's thread has a synchronization context, so formMain's background workers (the route preview)
  report back on it, and `Invoke`/`BeginInvoke` reach it from other threads.

## When

- macOS, 64-bit: by default (`OBNS_GLFORMS=0`, or `/glmenu=true` on the command line, gives upstream's in-window menu).
- Anywhere else: with `OBNS_GLFORMS=1` (for testing on Windows, where formMain itself is shown otherwise).

Switches for testing: `OBNS_MENU_SCALE` (form units per pixel, 0.5 to 4), `OBNS_MENU_DEBUG=1` (key events, frame counts,
OpenGL errors, and where the menu's thread is if its loop stops for 4 s), `OBNS_MENU_SNAPSHOT=<folder>` (the frame
saved as menu.png twice a second: a capture of an OpenGL window from outside can catch it between frames).

## Checked on Windows (2026-10-29), with `OBNS_GLFORMS=1`

- Start: the route folder typed in, the Tuen Ma Line's routes listed with their icons, a route chosen - its preview
  (picture, description in Chinese and Japanese, map, gradient profile) from formMain's background loader - then the
  SP1900 chosen (description, picture) and Start: the menu closed and the game opened its window and loaded the route.
- Customize controls (the list scrolled both ways, the joystick picture formMain paints), Options (all three pages,
  OBNS's renderer row, the language changed to French and back: every label and the flag follow), Package Management,
  the About box, the controls' Import file dialog, resizing (formMain's own layout code), Close (exit code 0).

## Not yet

- On a Mac: the window, fonts (Microsoft Sans Serif is replaced by the system's), Retina scaling and the Command key
  (taken as Control) are untested. To be checked on the Mac that runs the "TackSim Rinkin" session.
- The Review page (needs a game played first), installing and creating packages, and the RailDriver calibration were
  not exercised.
- An input device plugin's settings window is a Windows Forms window of the plugin's own: shown on Windows; on macOS the
  menu says it cannot be shown.
- Later (V2): a native macOS version written in Swift, with the same items in the same layout.
