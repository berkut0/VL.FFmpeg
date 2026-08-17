# Vendored FFmpeg.AutoGen binding

- Upstream: <https://github.com/Ruslan-B/FFmpeg.AutoGen>
- NuGet version: `8.1.0`
- Commit: `444925cd53d3611fd4c8c295873fb631be56ab21`
- License: MIT; see `LICENSE.txt`

The source is compiled into `VL.FFmpeg.dll`. Its namespace is relocated from
`FFmpeg.AutoGen` to `VL.FFmpeg.Interop.AutoGen`, outside the imported
`VL.FFmpeg.Nodes` namespace.

Do not replace this with a `PackageReference`. Gamma's
`VL.Stride.Runtime -> Stride.Video` graph uses `FFmpeg.AutoGen 3.4.0.2`.
Embedding and relocation avoid that assembly-name conflict and keep the
generated binding out of the node browser.

Update procedure:

1. Select an upstream release matching the native FFmpeg major/minor.
2. Record its commit and verify the license.
3. Replace only upstream `.cs` files and `LICENSE.txt`.
4. Relocate `namespace FFmpeg.AutoGen` and matching `using` directives to
   `VL.FFmpeg.Interop.AutoGen`.
5. Add `#nullable disable` as the first line of every imported `.cs` file.
6. Confirm the main project includes the vendored sources and does not
   reference an AutoGen project or package.
7. Build and run native version-probe tests before changing packaged binaries.
