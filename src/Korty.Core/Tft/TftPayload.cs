using Korty.Core.Hid;
using Korty.Core.Protocol;

namespace Korty.Core.Tft;

/// <summary>
/// Encoding knobs that DIFFER between the two public descriptions. Defaults pick the value stated by the source
/// of each family; hardware acceptance tests must settle which is right for the owner's firmware.
/// </summary>
public sealed record TftEncodingOptions
{
    public ProtocolFamily Family { get; init; } = ProtocolFamily.AaFrame;
    /// <summary>wsclx: delay byte = 5 ms units. kabelklaus: 2 ms units. UNRESOLVED CONFLICT.</summary>
    public int DelayUnitMs { get; init; } = 5;
    public int MaxFrames { get; init; } = 120;
    public bool Dither { get; init; }
    /// <summary>wsclx documents a 0x00 terminator after the last delay byte; kabelklaus does not. Needs one spare header byte (≤254 frames).</summary>
    public bool ZeroTerminator { get; init; }
    /// <summary>04-family only: AKS075 (default in kabelklaus) vs gohv framing.</summary>
    public bool GohvVariant { get; init; }

    public static TftEncodingOptions For(ProtocolFamily family) => family switch
    {
        ProtocolFamily.AaFrame => new TftEncodingOptions { Family = family, DelayUnitMs = 5, MaxFrames = 120 },
        _ => new TftEncodingOptions { Family = family, DelayUnitMs = 2, MaxFrames = 120 },
    };
}

public static class TftPayloadBuilder
{
    public const int HeaderLength = 256;
    public const int Alignment = 4096;
    public const int AbsoluteMaxFrames = 255;

    /// <summary>Header (256 B) + RGB565 frames, padded with 0xFF to a multiple of 4096.</summary>
    public static byte[] Build(TftAnimation animation, TftEncodingOptions o)
    {
        int max = Math.Min(o.MaxFrames, o.ZeroTerminator ? AbsoluteMaxFrames - 1 : AbsoluteMaxFrames);
        var anim = animation.Decimate(max);
        int n = anim.Frames.Count;

        var header = new byte[HeaderLength];
        Array.Fill(header, (byte)0xFF);
        header[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            int units = n == 1 ? 0 : Math.Clamp((int)Math.Round(anim.Frames[i].DelayMs / (double)o.DelayUnitMs, MidpointRounding.AwayFromZero), 1, 255);
            header[1 + i] = (byte)units;
        }
        if (o.ZeroTerminator) header[1 + n] = 0x00;

        int raw = HeaderLength + n * Rgb565.FrameBytes;
        int padded = (raw + Alignment - 1) / Alignment * Alignment;
        var payload = new byte[padded];
        Array.Fill(payload, (byte)0xFF);
        Buffer.BlockCopy(header, 0, payload, 0, HeaderLength);
        for (int i = 0; i < n; i++)
        {
            var f = Rgb565.FromBgra(anim.Frames[i].Bgra, o.Dither);
            Buffer.BlockCopy(f, 0, payload, HeaderLength + i * Rgb565.FrameBytes, f.Length);
        }
        return payload;
    }

    /// <summary>Inverse of <see cref="Build"/> (frames + delays). Used by the simulated keyboard and tests.</summary>
    public static TftAnimation Decode(byte[] payload, int delayUnitMs)
    {
        if (payload.Length < HeaderLength) throw new InvalidDataException("Payload shorter than header.");
        int n = payload[0];
        if (n == 0) throw new InvalidDataException("Zero frames.");
        if (payload.Length < HeaderLength + n * Rgb565.FrameBytes) throw new InvalidDataException("Payload truncated.");
        var frames = new List<TftFrame>(n);
        for (int i = 0; i < n; i++)
        {
            var raw = payload.AsSpan(HeaderLength + i * Rgb565.FrameBytes, Rgb565.FrameBytes);
            frames.Add(new TftFrame(Rgb565.ToBgra(raw), payload[1 + i] * delayUnitMs));
        }
        return new TftAnimation(frames);
    }
}

/// <summary>A complete, inspectable upload. Building a plan sends nothing.</summary>
public sealed record TftUploadPlan(
    ProtocolFamily Family,
    IReadOnlyList<ProtocolPacket> Packets,
    int FrameCount,
    int PayloadBytes,
    int DataChunks,
    int DataChunkBodyLength,
    IReadOnlyList<string> Warnings)
{
    /// <summary>"png" for a still, "gif" for an animation (Feature04 family).</summary>
    public string? Stage { get; init; }
    public long TotalWireBytes => Packets.Sum(p => (long)p.WireLength);
}

public static class TftUploadPlanner
{
    public const int Aa_ChunkPayload = 4096;
    public const int Aa_HeaderLength = 8;
    public const ushort Aa_Magic = 0x0650;

    public static TftUploadPlan Plan(TftAnimation animation, TftEncodingOptions o)
    {
        var payload = TftPayloadBuilder.Build(animation, o);
        int frames = payload[0];
        var warnings = new List<string>();
        if (animation.Frames.Count > frames) warnings.Add($"Анимация сокращена с {animation.Frames.Count} до {frames} кадров (лимит {o.MaxFrames}).");
        if (frames > 30 && o.Family == ProtocolFamily.AaFrame) warnings.Add("Ёмкость экрана не подтверждена: wsclx оценивает «около 30+» кадров, GET_DEVICE_INFO заявляет 255. Большие GIF могут не поместиться/вызвать чёрный экран.");
        if (o.DelayUnitMs != 5 && o.Family == ProtocolFamily.AaFrame) warnings.Add("Единица задержки отличается от документированной для AA-семейства (5 мс).");

        return o.Family switch
        {
            ProtocolFamily.AaFrame => PlanAa(payload, frames, warnings),
            ProtocolFamily.Feature04 => Plan04(payload, frames, o, warnings),
            _ => throw new NotSupportedException(),
        };
    }

    private static TftUploadPlan PlanAa(byte[] payload, int frames, List<string> warnings)
    {
        int chunks = payload.Length / Aa_ChunkPayload;
        var list = new List<ProtocolPacket>(chunks);
        for (int i = 0; i < chunks; i++)
        {
            var body = new byte[Aa_HeaderLength + Aa_ChunkPayload];
            body[0] = 0xAA; body[1] = 0x50;
            body[2] = (byte)i; body[3] = (byte)(i >> 8);
            body[4] = (byte)chunks; body[5] = (byte)(chunks >> 8);
            body[6] = (byte)(Aa_Magic & 0xFF); body[7] = (byte)(Aa_Magic >> 8);
            Buffer.BlockCopy(payload, i * Aa_ChunkPayload, body, Aa_HeaderLength, Aa_ChunkPayload);
            list.Add(new ProtocolPacket(ProtocolFamily.AaFrame, 0x50, "SET_TFT_USER_ANIMATION", ReportKind.Output, TargetRole.Data, 0, body,
                CommandRisk.PersistentSetting, $"TFT chunk {i + 1}/{chunks}",
                Assumption: i == 0 ? "Требуется интерфейс с output-отчётом ≥4104 байт; в wsclx это interface 3 (FF67), но тот же документ говорит, что FF67 молча отбрасывает команды управления. Активация анимации (см. HANDOFF §5b) не подтверждена." : null));
        }
        warnings.Add("AA-семейство: загрузка принимается прошивкой, но wsclx сообщает, что экран не переключается на пользовательскую анимацию (последовательность активации неизвестна).");
        return new TftUploadPlan(ProtocolFamily.AaFrame, list, frames, payload.Length, chunks, Aa_HeaderLength + Aa_ChunkPayload, warnings);
    }

    private static TftUploadPlan Plan04(byte[] payload, int frames, TftEncodingOptions o, List<string> warnings)
    {
        const int Chunk = Feature04Builder.ChunkLength;
        int chunks = payload.Length / Chunk;
        string stage = Stages.ForFrames(frames);
        // Reference sequence (#imgUpload): START → handshake (no sleep) → IMAGE_CFG → handshake → N × (output report, wait ACK ≤300 ms) → SAVE|FINISH → handshake.
        var list = new List<ProtocolPacket>
        {
            Feature04Builder.Start((byte)(o.GohvVariant ? 1 : 0), stage, handshakeAfterMs: 0),
            Feature04Builder.ImageCfg((byte)(o.GohvVariant ? 0x02 : 0x03), chunks, stage),
        };
        for (int i = 0; i < chunks; i++)
        {
            var body = new byte[Chunk];
            Buffer.BlockCopy(payload, i * Chunk, body, 0, Chunk);
            list.Add(new ProtocolPacket(ProtocolFamily.Feature04, 0x72, "IMAGE_DATA", ReportKind.Output, TargetRole.Data, 0, body,
                CommandRisk.PersistentSetting, $"TFT chunk {i + 1}/{chunks}")
            { Stage = stage, AckWaitMs = Feature04Builder.ChunkAckWaitMs });
        }
        list.Add(o.GohvVariant ? Feature04Builder.Finish(stage, 0) : Feature04Builder.Save(0, stage, 0));
        if (frames > 1 && frames > 30) warnings.Add("Анимация длиннее ~30 кадров: ёмкость экрана не проверена на вашей клавиатуре — начинайте с малого (этап GIF).");
        warnings.Add("Эталон (kabelklaus): векторные интерфейсы доступны только по USB-кабелю; по 2.4 ГГц так загрузить нельзя.");
        return new TftUploadPlan(ProtocolFamily.Feature04, list, frames, payload.Length, chunks, Chunk, warnings) { Stage = stage };
    }
}
