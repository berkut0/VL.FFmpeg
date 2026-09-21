# License texts

`FFmpeg-LGPL-3.0.txt` is the license from the vendored BtbN LGPL shared
FFmpeg build. It is committed with the native DLLs. `eng/Pack.ps1` requires
the file; the nuspec includes it in the package.

`libvpx-BSD-3-Clause.txt` is the required notice for libvpx, which is embedded
in the FFmpeg build and used for VP8/VP9 alpha decoding. The pack script and
nuspec include it in the package.
