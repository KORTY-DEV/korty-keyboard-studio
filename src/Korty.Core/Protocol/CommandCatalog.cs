namespace Korty.Core.Protocol;

/// <summary>
/// Every command this application knows about, with a risk class and the basis for the claim.
/// Anything not listed here is <see cref="CommandRisk.Unknown"/> and cannot be sent.
/// Sources: W = wsclx/ak820pro-modder docs/PROTOCOL.md + HANDOFF.md (MIT);
///          K = kabelklaus/ajazz-ak820-pro README + app.js (MIT);
///          F = fpb/ajazz-ak820-pro (GPL-2.0, custom QMK firmware — NOT the stock protocol, used only for hardware facts).
/// </summary>
public static class CommandCatalog
{
    private const string W = "wsclx PROTOCOL.md";
    private const string K = "kabelklaus app.js";

    public static IReadOnlyList<CommandDescriptor> All { get; } = new[]
    {
        // ---- AA-frame family ----
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x10, "GET_DEVICE_INFO", CommandRisk.ReadRequest, EvidenceLevel.SingleSource, W, "48-byte reply: firmware, battery, current profile, tftMaxFrames (255). Offsets documented by one source."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x11, "GET_GAME_MODE", CommandRisk.ReadRequest, EvidenceLevel.SingleSource, "wsclx HANDOFF.md", "56-byte reply containing reportRate (byte 5, values undocumented), sleepTime, tftDisplayTime. NOT used by the app: polling-rate values are unknown."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x13, "GET_LED_EFFECT", CommandRisk.ReadRequest, EvidenceLevel.SingleSource, W, "Read global lighting."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x23, "SET_LED_EFFECT", CommandRisk.PersistentSetting, EvidenceLevel.Conflicting, W + " vs " + K, "16-byte payload. Trailer is AA 55 in W and 55 AA in K (different families). W claims hardware verification on fw 1.07 ISO-DE/macOS only."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x50, "SET_TFT_USER_ANIMATION", CommandRisk.PersistentSetting, EvidenceLevel.Conflicting, W + " / HANDOFF 5b", "4104-byte chunks. W reports uploads are accepted but the screen keeps the built-in animation (activation unknown). Needs an interface with a >=4104-byte output report; control iface is 64 bytes."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x51, "SET_TFT_BUILT_IN_INDEX", CommandRisk.Unknown, EvidenceLevel.SingleSource, W, "Documented as a 'backlog guess'. Not implemented, not sendable."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x34, "SET_TEMPORARY_COMMAND_DATA (clock)", CommandRisk.Unknown, EvidenceLevel.SingleSource, "wsclx HANDOFF.md", "10-byte date/time payload — documented as 'reserved, not implemented' upstream. Not implemented here."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x0F, "SET_FACTORY_RESET", CommandRisk.Destructive, EvidenceLevel.SingleSource, W, "Arg 0xFF = full reset. BLOCKED BY DESIGN."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x22, "SET_KEY", CommandRisk.Destructive, EvidenceLevel.SingleSource, W, "Key remap — out of scope (no remapping), wrong page type silently changes the keymap. BLOCKED BY DESIGN."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x26, "SET_FN_KEY", CommandRisk.Destructive, EvidenceLevel.SingleSource, W, "Out of scope. BLOCKED BY DESIGN."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x25, "SET_MACRO", CommandRisk.Destructive, EvidenceLevel.SingleSource, W, "Out of scope (no macros). BLOCKED BY DESIGN."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x21, "SET_GAME_MODE", CommandRisk.Destructive, EvidenceLevel.SingleSource, "wsclx HANDOFF.md", "Would carry reportRate/debounce bytes whose values are undocumented. BLOCKED BY DESIGN until values are verified."),
        new CommandDescriptor(ProtocolFamily.AaFrame, 0x24, "SET_CUSTOM_LED_DATA", CommandRisk.Unknown, EvidenceLevel.SingleSource, W, "Per-key RGB, 512 bytes. Documented by one source; not implemented in v0.2."),

        // ---- 04-feature family ----
        new CommandDescriptor(ProtocolFamily.Feature04, 0x18, "START", CommandRisk.VolatileSetting, EvidenceLevel.ReferenceVerified, K, "Opens a transfer session. byte8: 01 lighting/time/standby; image: 00 (AKS075 variant) or 01 (gohv variant)."),
        new CommandDescriptor(ProtocolFamily.Feature04, 0x13, "MODE_CFG", CommandRisk.VolatileSetting, EvidenceLevel.ReferenceVerified, K, "Announces a lighting MODE_DATA packet (the MODE_DATA report itself carries the mode in byte 0 and is covered by this id)."),
        new CommandDescriptor(ProtocolFamily.Feature04, 0x72, "IMAGE_CFG", CommandRisk.PersistentSetting, EvidenceLevel.ReferenceVerified, K, "Announces N data chunks (16-bit LE in bytes 8..9). sub: 03 AKS075, 02 gohv. Chunk data (4096 B output reports on FF68) is covered by this id."),
        new CommandDescriptor(ProtocolFamily.Feature04, 0x02, "SAVE", CommandRisk.PersistentSetting, EvidenceLevel.ReferenceVerified, K, "Commits settings; the reference sends it at the end of every lighting and image transfer (byte8 0x00 by default). The reference's own raw-command scanner never sends 0x02 on its own. KORTY only ever sends it as the last step of a staged transfer."),
        new CommandDescriptor(ProtocolFamily.Feature04, 0xF0, "FINISH", CommandRisk.VolatileSetting, EvidenceLevel.ReferenceVerified, K, "Ends a transfer session (optional in lighting; closes the gohv image variant)."),
        new CommandDescriptor(ProtocolFamily.Feature04, 0x17, "SETTINGS_CFG", CommandRisk.PersistentSetting, EvidenceLevel.VendorAnalysis, "vendor DeviceDriver.exe 0x414060", "Keyboard settings block: key response level 1..5, sleep 0..3, Win/Alt+F4/Alt+Tab disables, fn_switch. Layout read from the vendor software's code and database (static analysis, nothing executed); not yet tested on hardware by KORTY. NOTE: the community reference (kabelklaus) writes a single level into byte 8 of this block and labels it standby — per the vendor software byte 8 is the KEY RESPONSE level."),
        new CommandDescriptor(ProtocolFamily.Feature04, 0x28, "TIME_CFG", CommandRisk.Unknown, EvidenceLevel.SingleSource, K, "Clock sync (year-2000, month, day, h, m, s). Not offered in KORTY (the factory screen clock is not a priority); not sendable."),
    };

    public static CommandDescriptor? Find(ProtocolFamily family, int id) =>
        All.FirstOrDefault(c => c.Family == family && c.Id == id);

    /// <summary>Unlisted ids are Unknown by definition.</summary>
    public static CommandRisk RiskOf(ProtocolFamily family, int id) => Find(family, id)?.Risk ?? CommandRisk.Unknown;
}
