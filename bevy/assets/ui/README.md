# Original engine widget art

`spin_increment.png` and `spin_decrement.png` are the unmodified 16×8 `SpinBox`
`up` and `down` icons returned by the original Godot 4.6.2 runtime theme.
Source: https://github.com/godotengine/godot/tree/4.6.2-stable/scene/theme
Godot's MIT notice is retained in `LICENSE.Godot.txt`.

Reproduce with `Control.get_theme_icon("up", "SpinBox").get_image().save_png(...)`
and the corresponding `down` call in the original engine. This preserves the
runtime raster rather than substituting a platform font arrow.

`slider_grabber.png` and `slider_grabber_highlight.png` are the unmodified 16×16
`HSlider` `grabber` and `grabber_highlight` icons extracted by the same runtime
method. The native split slider uses the original 16px thumb and 8px track.

spin_increment.png SHA256: `580cd541c6dedbca16adb993c0cd29375a33dd3f1668db56aa586e8b8adadc53`.
spin_decrement.png SHA256: `f554ba273eaab4d6f4fd5d05fa20c08a54ec70ea0c15f8ff7f3a133469ef4e2a`.
slider_grabber.png SHA256: `57d0f8fcf266e744300bd4aec3df75c41095b8eae4dcdb16681227bbeee75cd5`.
slider_grabber_highlight.png SHA256: `671e04b034d515697b090dba6ad52f08046fe5377823222f36515268e9521b34`.
