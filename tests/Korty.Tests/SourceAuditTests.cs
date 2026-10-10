using System.Text.RegularExpressions;
using Xunit;

namespace Korty.Tests;

/// <summary>Static guard rails over the repository itself — they fail the build if someone (or some AI) adds an unsafe path.</summary>
public class SourceAuditTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KortyKeyboardStudio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("solution root not found");
    }

    private static IEnumerable<(string rel, string code)> SourceFiles()
    {
        var root = Root();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            yield return (Path.GetRelativePath(root, f), StripCommentsAndStrings(File.ReadAllText(f)));
        }
    }

    private static string StripCommentsAndStrings(string s)
    {
        s = Regex.Replace(s, @"/\*.*?\*/", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"//[^\r\n]*", "");
        s = Regex.Replace(s, "\"(?:\\\\.|[^\"\\\\])*\"", "\"\"");
        return s;
    }

    [Fact]
    public void OnlyTheWindowsTransportMayTouchWriteApis()
    {
        var forbidden = new Regex(@"\b(WriteFile|HidD_SetFeature|HidD_SetOutputReport|HidD_SetNumInputBuffers|DeviceIoControl|HidD_GetFeature|HidD_GetInputReport|ReadFile|WinUsb_\w+|libusb\w*|hid_write|hid_send_feature_report)\b");
        var offenders = SourceFiles().Where(f => forbidden.IsMatch(f.code)).Select(f => f.rel).ToList();
        Assert.Equal(new[] { Path.Combine("src", "Korty.Hid.Windows", "WindowsHidTransport.cs") }, offenders.ToArray());
    }

    [Fact]
    public void EnumeratorOpensDevicesWithZeroAccessOnly()
    {
        var code = SourceFiles().Single(f => f.rel.EndsWith("WindowsHidEnumerator.cs")).code;
        foreach (Match m in Regex.Matches(code, @"CreateFile\(([^;]*?)\)", RegexOptions.Singleline))
        {
            if (m.Value.Contains("string name")) continue; // the P/Invoke declaration
            Assert.Matches(@"^\s*path\s*,\s*0\s*,", m.Groups[1].Value);
        }
        Assert.DoesNotContain("GENERIC_WRITE", code);
    }

    [Fact]
    public void TransportConsumesOnlyApprovedWrites()
    {
        var iface = typeof(Korty.Core.Hid.IHidTransport).GetMethod("SendAsync")!;
        Assert.Equal(typeof(Korty.Core.Safety.ApprovedWrite), iface.GetParameters()[0].ParameterType);
        Assert.Empty(typeof(Korty.Core.Safety.ApprovedWrite).GetConstructors()); // no public ctor → only WriteGate can mint tokens
    }

    [Fact]
    public void NoNetworkProcessLaunchRegistryOrSelfModificationApisInSource()
    {
        var forbidden = new Regex(@"\b(HttpClient|WebClient|WebRequest|TcpClient|UdpClient|Socket|Process\.Start|ProcessStartInfo|Registry\.|RegistryKey|Assembly\.Load|Activator\.CreateInstanceFrom|DllInject|Environment\.FailFast)\b|System\.Net\.");
        var hits = SourceFiles().Where(f => forbidden.IsMatch(f.code)).Select(f => f.rel + ": " + forbidden.Match(f.code).Value).ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void UiLayerNeverFormatsRawPackets()
    {
        // Only the typed builders (AaFrameBuilder.SetLedEffect, Feature04Builder.*, TftUploadPlanner) may be called from the app;
        // PacketFormatter in Helpers.cs reads bytes for the dry-run display only.
        var forbidden = new Regex(@"new\s+ProtocolPacket\s*\(|\.Frame\s*\(|\.Cmd\s*\(|ToWireBuffer|\.Body\b|new\s+ApprovedWrite");
        var appFiles = SourceFiles()
            .Where(f => f.rel.StartsWith(Path.Combine("src", "Korty.App"), StringComparison.OrdinalIgnoreCase) && !f.rel.EndsWith("Helpers.cs"))
            .ToList();
        Assert.Contains(appFiles, f => f.rel.EndsWith("RgbViewModel.cs"));   // guard against a vacuous scan
        var offenders = appFiles.Where(f => forbidden.IsMatch(f.code)).Select(f => f.rel).ToList();
        Assert.True(offenders.Count == 0, "UI layer must not format raw packets: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ProjectFilesHaveNoBuildHooksScriptsOrNetworkPackages()
    {
        var root = Root();
        foreach (var f in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(root, "Directory.Build.*", SearchOption.TopDirectoryOnly)))
        {
            var xml = File.ReadAllText(f);
            Assert.DoesNotMatch(@"<Exec\b|PostBuildEvent|PreBuildEvent|DownloadFile|<Target\b|UsingTask", xml);
        }
        // Package references are limited to the test framework.
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
            Assert.DoesNotContain("PackageReference", File.ReadAllText(f));
    }

    [Fact]
    public void NoPowerShellExecutionPolicyBypassInRepoScripts()
    {
        var root = Root();
        foreach (var f in Directory.EnumerateFiles(root, "*.ps1", SearchOption.AllDirectories).Where(x => !x.Contains("\\obj\\") && !x.Contains("\\bin\\")))
        {
            var t = File.ReadAllText(f);
            Assert.DoesNotMatch(@"(?i)Invoke-WebRequest|Invoke-RestMethod|DownloadString|DownloadFile|Start-BitsTransfer|Set-ExecutionPolicy|-ExecutionPolicy\s+Bypass|Add-Type.*DllImport|reg(\.exe)?\s+(add|delete)|pnputil|devcon|Remove-Item.*-Recurse.*(\\Windows|\\System32)", t);
        }
    }
}
