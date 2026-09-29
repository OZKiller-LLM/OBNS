# Cab touch test panel

`Panel2.cfg` is the KCR East Rail MLR panel with two large `[DigitalNumber]` handle indicators
added at the end (subjects `power` and `brake`, 200 x 200 panel pixels, images generated as
`touch_power.bmp` / `touch_brake.bmp`). With panel2 extended mode on (the default here), each
becomes two touch areas: power top half = decrease, bottom half = increase; brake top half =
increase, bottom half = decrease.

To use: copy the KCR train folder on the device to `Train/zz-touch test train`, replace its
`Panel2.cfg`, add the two images, and delete `ats.cfg` (so the default safety system runs).
The log lists the areas: `cab touch area N (Command): x0,y0 - x1,y1 of WxH`.
