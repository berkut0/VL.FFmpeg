#nullable disable
using VL.FFmpeg.Interop.AutoGen.Native;
using System;
using System.Runtime.InteropServices;

namespace VL.FFmpeg.Interop.AutoGen;

public static class FunctionResolverFactory
{
    public static PlatformID GetPlatformId()
    {
#if NETSTANDARD2_0_OR_GREATER
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return PlatformID.Win32NT;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return PlatformID.Unix;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return PlatformID.MacOSX;
        return PlatformID.Unix; // Android, iOS, and other Unix-like platforms
#else
        return Environment.OSVersion.Platform;

#endif
    }

    public static IFunctionResolver Create()
    {
        switch (GetPlatformId())
        {
            case PlatformID.MacOSX:
                return new MacFunctionResolver();
            case PlatformID.Unix:
                return new LinuxFunctionResolver();
            case PlatformID.Win32NT:
                return new WindowsFunctionResolver();
            default:
                return new LinuxFunctionResolver();
        }
    }
}
