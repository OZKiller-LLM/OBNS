Android port test train: a two-car EMU, one motor car and one trailer, 20 m each.
Physics only - no exterior objects, panel or sounds yet.

train.dat carries no comments on purpose: the parser reads each section's fields by line
position and a comment line still occupies a position, shifting every value after it.

#ACCELERATION  a0, a1 (km/h/s), v1, v2 (km/h), e - one line per power notch (4)
#PERFORMANCE   deceleration (km/h/s), static friction, reserved, rolling resistance, aerodynamic drag
#DELAY         power up/down, brake up/down (s)
#MOVE          jerk power up/down, jerk brake up/down, brake cylinder up/down
#BRAKE         brake type 0 = electromagnetic straight air brake, control system, control speed
#PRESSURE      brake cylinder service max, emergency max, main reservoir min/max, brake pipe (kPa)
#HANDLE        separate handles, 4 power notches, 7 brake notches
#CAB           driver X, Y, Z (mm), driver car index
#CAR           motor car mass (t), motor cars, trailer car mass (t), trailer cars, car length (m),
               front car is motor, width, height, centre of gravity, exposed / unexposed frontal area
