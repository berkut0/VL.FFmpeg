using NUnit.Framework;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;
using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Tests;

public sealed class FFmpegRuntimeTests
{
    [Test]
    public void ProcessNodeAssemblyUsesImportAsIs()
    {
        var attributes = typeof(FFmpegVideoPlayer).Assembly.GetCustomAttributesData();
        var importAsIs = attributes.Single(
            attribute => attribute.AttributeType.FullName == "VL.Core.Import.ImportAsIsAttribute");
        var namedArguments = importAsIs.NamedArguments.ToDictionary(
            argument => argument.MemberName,
            argument => argument.TypedValue.Value as string);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                attributes.Count(attribute =>
                    attribute.AttributeType.FullName == "VL.Core.Import.ImportTypeAttribute"),
                Is.Zero);
            Assert.That(namedArguments["Namespace"], Is.EqualTo("VL.FFmpeg.Nodes"));
            Assert.That(namedArguments["Category"], Is.EqualTo("Video.FFmpeg"));

            var importedTypes = typeof(FFmpegVideoPlayer).Assembly.ExportedTypes
                .Where(type => type.Namespace == "VL.FFmpeg.Nodes")
                .Select(type => type.Name)
                .OrderBy(name => name)
                .ToArray();
            Assert.That(importedTypes, Is.EqualTo(new[]
            {
                nameof(FFmpegDecodeMode),
                nameof(FFmpegDecodePath),
                nameof(FFmpegPlaybackPhase),
                nameof(FFmpegRuntimeDiagnostics),
                nameof(FFmpegVideoPlayer)
            }));
        }
    }

    [Test]
    public void ExplicitPathReportsTheFirstMissingRuntimeFile()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var success = NativeRuntimePathResolver.TryResolve(
            missingPath,
            typeof(FFmpegRuntimeTests).Assembly,
            out var resolvedPath,
            out var error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.False);
            Assert.That(resolvedPath, Is.Empty);
            Assert.That(error, Does.Contain("missing 'directory'"));
            Assert.That(error, Does.Contain(Path.GetFullPath(missingPath)));
        }
    }

    [Test]
    public void PinnedNativeRuntimeHasExpectedAbi()
    {
        var nativePath = Environment.GetEnvironmentVariable("VL_FFMPEG_TEST_NATIVE_PATH")
            ?? FindRepositoryRuntime();
        var result = FFmpegRuntime.Probe(nativePath);
        if (nativePath is null && !result.Available)
        {
            Assert.Ignore("Acquire the runtime or set VL_FFMPEG_TEST_NATIVE_PATH to run the native probe.");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Available, Is.True, result.Status);
            Assert.That(result.Version, Does.Contain("8.1"));
            if (nativePath is not null)
                Assert.That(result.NativeDirectory, Is.EqualTo(Path.GetFullPath(nativePath)));
        }
    }

    [Test]
    public void RelocatedBindingIsEmbeddedInTheNodeAssembly()
    {
        var nodeAssembly = typeof(FFmpegVideoPlayer).Assembly;
        var references = nodeAssembly.GetReferencedAssemblies();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(typeof(ffmpeg).Assembly, Is.SameAs(nodeAssembly));
            Assert.That(references.Select(reference => reference.Name),
                Does.Not.Contain("VL.FFmpeg.AutoGen"));
            Assert.That(references.Select(reference => reference.Name),
                Does.Not.Contain("FFmpeg.AutoGen"));
            Assert.That(references.Select(reference => reference.Name),
                Does.Not.Contain("SharpDX"));
            Assert.That(references.Select(reference => reference.Name),
                Does.Not.Contain("Vortice.Direct3D11"));
            Assert.That(references.Select(reference => reference.Name),
                Does.Not.Contain("Silk.NET.Direct3D11"));
        }
    }

    private static string? FindRepositoryRuntime()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "runtimes",
                    "win-x64",
                    "native");
                return File.Exists(Path.Combine(candidate, "avcodec-62.dll"))
                    ? candidate
                    : null;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
