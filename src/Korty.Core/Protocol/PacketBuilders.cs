using Korty.Core.Hid;
using Korty.Core.Model;

namespace Korty.Core.Protocol;

/// <summary>
/// Pure functions that turn typed settings into packets. They never touch a device. Callers (UI) must go through
/// <see cref="DeviceCommandService"/> — the UI itself never formats raw bytes.
/// Destructive/unknown commands have no builder at all.
/// </summary>
public static class AaFrameBuilder
{
    public const int FrameLength = 64;
    public const int MaxPayload = 56;

    public static byte[] Frame(byte cmd, byte lenOrType, ushort addr = 0, byte opt0 = 0, byte opt1 = 0, byte opt2 = 0, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException($"Payload {payload.Length} > {MaxPayload}");
        var f = new byte[FrameLength];
        f[0] = 0xAA;
        f[1] = cmd;
        f[2] = lenOrType;
        f[3] = (byte)(addr & 0xFF);
        f[4] = (byte)(addr >> 8);
        f[5] = opt0; f[6] = opt1; f[7] = opt2;
        payload.CopyTo(f.AsSpan(8));
        return f;
    }

    /// <summary>16-byte LED payload per wsclx PROTOCOL.md ("LED Effect Payload"), trailer AA 55.</summary>
    public static byte[] LedPayload(RgbSettings s)
    {
        s.Validate();
        var p = new byte[16];
        p[0] = (byte)s.Mode;
        p[1] = s.Primary.R; p[2] = s.Primary.G; p[3] = s.Primary.B;
        p[4] = 0xFF; // driverSetting: "always 0xFF in the official driver"
        p[5] = s.Secondary.R; p[6] = s.Secondary.G; p[7] = s.Secondary.B;
        p[8] = (byte)s.ColorMode;
        p[9] = (byte)s.Brightness;
        p[10] = (byte)s.Speed;
        p[11] = (byte)s.Direction;
        p[12] = 0; // effectModeType: not documented, left 0
        p[13] = 0;
        p[14] = 0xAA; p[15] = 0x55;
        return p;
    }

    public static ProtocolPacket SetLedEffect(RgbSettings s) => new(
        ProtocolFamily.AaFrame, 0x23, "SET_LED_EFFECT", ReportKind.Output, TargetRole.Control, 0,
        Frame(0x23, 16, 0, 0, 1, 0, LedPayload(s)),
        CommandRisk.PersistentSetting,
        $"Lighting: mode {s.Mode}, {s.Primary.ToHex()}, brightness {s.Brightness}, speed {s.Speed}",
        Assumption: "Byte 6 (last-packet flag) set to 1 for a single-packet command; upstream only documents the flag for multi-packet writes.");

    public static ProtocolPacket GetDeviceInfo() => new(
        ProtocolFamily.AaFrame, 0x10, "GET_DEVICE_INFO", ReportKind.Output, TargetRole.Control, 0,
        Frame(0x10, 0), CommandRisk.ReadRequest, "Ask the keyboard for firmware/battery/profile info");
}

/// <summary>Activation stages. Each stage enables a distinct set of (command × interface) entries; nothing leaks across stages.</summary>
public static class Stages
{
    public const string Rgb = "rgb";
    public const string Png = "png";
    public const string Gif = "gif";
    /// <summary>Keyboard settings block: key response level, sleep time, key disables.</summary>
    public const string Settings = "settings";
    public static readonly IReadOnlyList<string> All = new[] { Rgb, Png, Gif, Settings };
    public static string ForFrames(int frames) => frames <= 1 ? Png : Gif;
}

/// <summary>Reference-equivalent knobs (defaults = defaults of the working web configurator).</summary>
public sealed record Feature04Options
{
    /// <summary>Byte 8 of SAVE. Reference default 0x00 ("no flag").</summary>
    public byte SaveFlag { get; init; }
    /// <summary>Send FINISH after lighting SAVE. Reference default: off.</summary>
    public bool FinishAfterLighting { get; init; }
    /// <summary>Image variant: false = AKS075 (START 0, CFG 3, closes with SAVE) — reference default; true = gohv (START 1, CFG 2, closes with FINISH).</summary>
    public bool GohvImage { get; init; }
}

public static class Feature04Builder
{
    public const int ReportLength = 64;
    public const int ChunkLength = 4096;
    public const int ChunkAckWaitMs = 300;

    public static byte[] Cmd(byte cmd, byte sub = 0, byte enable = 0, byte b9 = 0)
    {
        var b = new byte[ReportLength];
        b[0] = 0x04; b[1] = cmd; b[2] = sub; b[8] = enable; b[9] = b9;
        return b;
    }

    private static ProtocolPacket Feature(byte cmd, string name, CommandRisk risk, string desc, string stage, int? handshake, int settle = 0, byte sub = 0, byte enable = 0, byte b9 = 0, TargetRole role = TargetRole.Control) =>
        new(ProtocolFamily.Feature04, cmd, name, ReportKind.Feature, role, 0, Cmd(cmd, sub, enable, b9), risk, desc)
        { Stage = stage, HandshakeAfterMs = handshake, SettleMs = settle };

    // The public factory methods keep the reference's timings: set → [wait] → feature read-back.
    public static ProtocolPacket Start(byte enable, string stage = Stages.Rgb, int? handshakeAfterMs = 40) =>
        Feature(0x18, "START", CommandRisk.VolatileSetting, "Open transfer session", stage, handshakeAfterMs, 0, 0, enable);
    public static ProtocolPacket ModeCfg(string stage = Stages.Rgb) =>
        Feature(0x13, "MODE_CFG", CommandRisk.VolatileSetting, "Announce lighting data", stage, 40, 0, 0, 1);
    public static ProtocolPacket ImageCfg(byte sub, int chunks, string stage = Stages.Png) =>
        chunks is < 1 or > 0xFFFF ? throw new ArgumentOutOfRangeException(nameof(chunks))
        : Feature(0x72, "IMAGE_CFG", CommandRisk.PersistentSetting, $"Announce {chunks} image chunks", stage, 0, 0, sub, (byte)(chunks & 0xFF), (byte)(chunks >> 8));
    public static ProtocolPacket Save(byte flag, string stage = Stages.Rgb, int? handshakeAfterMs = 100) =>
        Feature(0x02, "SAVE", CommandRisk.PersistentSetting, "Commit saved settings", stage, handshakeAfterMs, 0, 0, flag);
    public static ProtocolPacket Finish(string stage = Stages.Rgb, int? handshakeAfterMs = 100) =>
        Feature(0xF0, "FINISH", CommandRisk.VolatileSetting, "Close transfer session", stage, handshakeAfterMs, 0, 0, 1);

    public static byte[] ModeData(RgbSettings s)
    {
        s.Validate();
        var b = new byte[ReportLength];
        b[0] = (byte)s.Mode;
        b[1] = s.Primary.R; b[2] = s.Primary.G; b[3] = s.Primary.B;
        b[8] = (byte)(s.ColorMode > 0 ? 1 : 0);
        b[9] = (byte)s.Brightness; b[10] = (byte)s.Speed; b[11] = (byte)s.Direction;
        b[14] = 0x55; b[15] = 0xAA;
        return b;
    }

    // ---- keyboard settings (key response / sleep / key disables) ----
    // Layout recovered from the vendor software's settings function (DeviceDriver.exe 0x414060): START (04 18), SETTINGS_CFG (04 17 01, byte8 = 1),
    // one data report, SAVE (04 02). Data report: [0]=00 [1]=01 [2]=Alt+Tab-off [3]=Alt+F4-off [4]=Win-off [5]=fn_switch [6]=sleep [7]=00 [8]=key response, [62..63]=AA 55.
    public static byte[] SettingsData(KeyboardSettings s)
    {
        s.Validate();
        var b = new byte[ReportLength];
        b[0] = 0x00; b[1] = 0x01;
        b[2] = (byte)(s.DisableAltTab ? 1 : 0); b[3] = (byte)(s.DisableAltF4 ? 1 : 0); b[4] = (byte)(s.DisableWinKey ? 1 : 0);
        b[5] = KeyboardSettings.FnSwitch; b[6] = (byte)s.SleepLevel; b[8] = (byte)s.KeyResponseLevel;
        b[62] = 0xAA; b[63] = 0x55;
        return b;
    }

    /// <summary>START → SETTINGS_CFG → data → SAVE [→ FINISH], same timings as the other settings sequences (40/40/40 ms, SAVE 100 ms).</summary>
    public static IReadOnlyList<ProtocolPacket> SettingsSequence(KeyboardSettings settings, Feature04Options? o = null)
    {
        o ??= new Feature04Options();
        var list = new List<ProtocolPacket>
        {
            Start(0, Stages.Settings, 40),                                          // the vendor software sends START with byte 8 = 0 here
            Feature(0x17, "SETTINGS_CFG", CommandRisk.PersistentSetting, "Announce keyboard settings", Stages.Settings, 40, 0, 1, 1),
            new(ProtocolFamily.Feature04, 0x17, "SETTINGS_DATA", ReportKind.Feature, TargetRole.Control, 0, SettingsData(settings), CommandRisk.PersistentSetting,
                $"Keyboard settings: key response {settings.KeyResponseLevel}, sleep {KeyboardSettings.SleepNames[settings.SleepLevel]}, " +
                $"disable Win {settings.DisableWinKey}/Alt+F4 {settings.DisableAltF4}/Alt+Tab {settings.DisableAltTab}")
            { Stage = Stages.Settings, HandshakeAfterMs = null, SettleMs = 40 },
            Save(o.SaveFlag, Stages.Settings, 100),
        };
        if (o.FinishAfterLighting) list.Add(Finish(Stages.Settings, 100));
        return list;
    }

    /// <summary>
    /// START → MODE_CFG → MODE_DATA → SAVE [→ FINISH], with the reference's exact timings:
    /// START/MODE_CFG: 40 ms then read-back; MODE_DATA: no read-back, 40 ms settle; SAVE/FINISH: 100 ms then read-back.
    /// </summary>
    public static IReadOnlyList<ProtocolPacket> LightingSequence(RgbSettings s, Feature04Options? o = null)
    {
        o ??= new Feature04Options();
        var list = new List<ProtocolPacket>
        {
            Start(1, Stages.Rgb, 40),
            ModeCfg(Stages.Rgb),
            new(ProtocolFamily.Feature04, 0x13, "MODE_DATA", ReportKind.Feature, TargetRole.Control, 0, ModeData(s), CommandRisk.VolatileSetting,
                $"Lighting data: mode {s.Mode}, {s.Primary.ToHex()}, brightness {s.Brightness}, speed {s.Speed}")
            { Stage = Stages.Rgb, HandshakeAfterMs = null, SettleMs = 40 },
            Save(o.SaveFlag, Stages.Rgb, 100),
        };
        if (o.FinishAfterLighting) list.Add(Finish(Stages.Rgb, 100));
        return list;
    }
}
