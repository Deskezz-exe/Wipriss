// Battery queries of wireless receivers (2.4 GHz). The request bytes and reply offsets follow the protocol notes of
// HaloBattery (github.com/HeyOkay/HaloBattery, docs/protocols.md and providers/*.py, MIT License,
// Copyright (c) 2026 Halo Battery contributors),
// rewritten in C# for Wipriss. Only read-only "get" requests are sent. No GPL code was looked at or used.
// The voltage-to-percent curve is Wipriss's own approximation of a Li-ion cell.

namespace Wipriss.Modules.Batteries;

public enum DeviceKind { Mouse, Keyboard, Headset, Other }

/// <summary>A charge reading. Percent is 0..100.</summary>
public readonly record struct Charge(int Percent, bool Charging);

/// <summary>Logitech HID++ 2.0 (receivers like Unifying, Lightspeed, Bolt and cabled devices). VID 046D.</summary>
public static class LogitechHidpp
{
    public const ushort Vendor = 0x046D;
    public const byte LongReport = 0x11, ShortReport = 0x10;
    public const int LongLength = 20;
    public const ushort RootFeature = 0x0000, NameFeature = 0x0005;
    public const ushort UnifiedBattery = 0x1004, BatteryStatus = 0x1000, BatteryVoltage = 0x1001;

    /// <summary>A long request: 11 slot index (function&lt;&lt;4 | swid) params…, padded to 20 bytes.</summary>
    public static byte[] Request(byte slot, byte featureIndex, byte function, byte swid, params byte[] parameters)
    {
        var r = new byte[LongLength];
        r[0] = LongReport;
        r[1] = slot;
        r[2] = featureIndex;
        r[3] = (byte)((function << 4) | (swid & 0x0F));
        Array.Copy(parameters, 0, r, 4, Math.Min(parameters.Length, LongLength - 4));
        return r;
    }

    /// <summary>The root feature's "which index has feature X" request.</summary>
    public static byte[] FindFeature(byte slot, ushort feature, byte swid) =>
        Request(slot, 0, 0, swid, (byte)(feature >> 8), (byte)feature);

    public static bool IsReply(ReadOnlySpan<byte> r, byte slot, byte featureIndex, byte function, byte swid) =>
        r.Length >= 5 && (r[0] == LongReport || r[0] == ShortReport) && r[1] == slot && r[2] == featureIndex
        && r[3] == (byte)((function << 4) | (swid & 0x0F));

    /// <summary>An error for this slot (device switched off, out of range, slot empty, old HID++ 1.0).</summary>
    public static bool IsError(ReadOnlySpan<byte> r, byte slot) =>
        r.Length >= 6 && (r[0] == LongReport || r[0] == ShortReport) && r[1] == slot && (r[2] == 0x8F || r[2] == 0xFF);

    /// <summary>Feature 0x1004 GetStatus: p0 percent (or 0 and level flags in p1), p2 charging state.</summary>
    public static Charge? ParseUnified(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3) return null;
        int percent = p[0];
        if (percent == 0)
            percent = (p[1] & 8) != 0 ? 90 : (p[1] & 4) != 0 ? 50 : (p[1] & 2) != 0 ? 20 : (p[1] & 1) != 0 ? 5 : -1;
        if (percent is < 0 or > 100) return null;
        return new Charge(percent, p[2] is >= 1 and <= 4);
    }

    /// <summary>Feature 0x1000 GetBatteryLevelStatus: p0 percent, p2 status (1..4 = charging).</summary>
    public static Charge? ParseStatus(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3 || p[0] is 0 or > 100) return null;
        return new Charge(p[0], p[2] is >= 1 and <= 4);
    }

    /// <summary>Feature 0x1001 GetBatteryVoltage: millivolts in p0:p1, p2 bit 7 = charging.</summary>
    public static Charge? ParseVoltage(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3) return null;
        int mv = (p[0] << 8) | p[1];
        if (mv is < 2500 or > 5000) return null;
        return new Charge(VoltageToPercent(mv), (p[2] & 0x80) != 0);
    }

    /// <summary>A Li-ion cell under a light load, approximately.</summary>
    public static int VoltageToPercent(int mv)
    {
        (int Mv, int Pct)[] curve = [(3400, 0), (3550, 5), (3650, 12), (3700, 22), (3750, 35), (3800, 48), (3900, 65), (4000, 78), (4100, 90), (4200, 100)];
        if (mv <= curve[0].Mv) return 0;
        for (int i = 1; i < curve.Length; i++)
            if (mv <= curve[i].Mv)
            {
                var (m0, p0) = curve[i - 1];
                var (m1, p1) = curve[i];
                return (int)Math.Round(p0 + (double)(mv - m0) * (p1 - p0) / (m1 - m0));
            }
        return 100;
    }

    /// <summary>Feature 0x0005 GetDeviceType.</summary>
    public static DeviceKind KindOf(byte type) => type switch
    {
        0 or 2 => DeviceKind.Keyboard,
        3 or 4 or 5 => DeviceKind.Mouse,
        8 => DeviceKind.Headset,
        _ => DeviceKind.Other,
    };
}

/// <summary>Razer wireless mice and headsets: a 90-byte feature report. VID 1532.</summary>
public static class RazerProtocol
{
    public const ushort Vendor = 0x1532;
    public const int ReportLength = 90;
    public const byte LevelCommand = 0x80, ChargingCommand = 0x84;

    /// <summary>Transaction ids to try: mice, headsets, older devices.</summary>
    public static readonly byte[] TransactionIds = [0x1F, 0x3F, 0xFF];

    /// <summary>The 90-byte request (without the report id): class 0x07 power, the command, a XOR checksum.</summary>
    public static byte[] Request(byte transactionId, byte command)
    {
        var r = new byte[ReportLength];
        r[1] = transactionId;
        r[5] = 0x02; // data size
        r[6] = 0x07; // command class: power
        r[7] = command;
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= r[i];
        r[88] = crc;
        return r;
    }

    /// <summary>The reply's value byte, if the reply is a success for this command.</summary>
    public static byte? Value(ReadOnlySpan<byte> reply, byte command)
    {
        if (reply.Length < 10 || reply[0] != 0x02 || reply[6] != 0x07 || reply[7] != command) return null;
        return reply[9];
    }

    /// <summary>The level comes as 0..255.</summary>
    public static int LevelToPercent(byte value) => (int)Math.Round(value * 100 / 255.0);
}

/// <summary>HyperX Cloud II Wireless (HP revision): vendor page FF90, usage 0303. VID 03F0.</summary>
public static class HyperXProtocol
{
    public const ushort Vendor = 0x03F0, UsagePage = 0xFF90, Usage = 0x0303;
    public const int RequestLength = 52;
    public const byte BatteryCommand = 0x02, ChargingCommand = 0x03;

    public static byte[] Request(byte command)
    {
        var r = new byte[RequestLength];
        r[0] = 0x06;
        r[1] = 0xFF;
        r[2] = 0xBB;
        r[3] = command;
        return r;
    }

    public static bool IsReplyTo(ReadOnlySpan<byte> reply, byte command) =>
        reply.Length >= 8 && reply[0] == 0x06 && reply[1] == 0xFF && reply[2] == 0xBB && reply[3] == command;

    public static int? Percent(ReadOnlySpan<byte> reply) =>
        IsReplyTo(reply, BatteryCommand) && reply[7] <= 100 ? reply[7] : null;

    public static bool Charging(ReadOnlySpan<byte> reply) => IsReplyTo(reply, ChargingCommand) && reply[4] == 1;
}

/// <summary>SteelSeries Arctis Nova 7 family: output report "00 B0" on vendor page FFC0. VID 1038.</summary>
public static class SteelSeriesProtocol
{
    public const ushort Vendor = 0x1038, UsagePage = 0xFFC0;
    public static readonly byte[] Request = [0x00, 0xB0];

    public static Charge? Parse(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 4 || reply[0] != 0xB0 || reply[2] > 100) return null;
        return new Charge(reply[2], reply[3] is 1 or 2);
    }
}

/// <summary>Corsair HS80 and similar headsets: "02 09 02 0F" on vendor page FF42; the level is in tenths of a percent. VID 1B1C.</summary>
public static class CorsairProtocol
{
    public const ushort Vendor = 0x1B1C, UsagePage = 0xFF42;
    public static readonly byte[] Request = [0x02, 0x09, 0x02, 0x0F];

    public static int? Percent(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 6) return null;
        int value = reply[4] | (reply[5] << 8);
        if (value is <= 0 or > 1000) return null; // sometimes the headset's id comes instead
        return (int)Math.Round(value / 10.0);
    }
}

/// <summary>Bluetooth device class bits and LE appearance to a kind of device.</summary>
public static class BluetoothKinds
{
    /// <summary>Class of Device (classic Bluetooth). Phones and computers are not ours to show.</summary>
    public static DeviceKind? FromClassOfDevice(uint raw)
    {
        uint major = (raw >> 8) & 0x1F, minor = (raw >> 2) & 0x3F;
        return major switch
        {
            5 => (minor >> 4) switch
            {
                1 or 3 => DeviceKind.Keyboard,
                2 => DeviceKind.Mouse,
                _ => DeviceKind.Other,
            },
            4 => DeviceKind.Headset,
            1 or 2 => null, // computer, phone
            _ => DeviceKind.Other,
        };
    }

    /// <summary>Bluetooth LE appearance: category in the top 10 bits.</summary>
    public static DeviceKind FromAppearance(ushort raw)
    {
        int category = raw >> 6, sub = raw & 0x3F;
        return category switch
        {
            0x0F => sub switch { 1 => DeviceKind.Keyboard, 2 => DeviceKind.Mouse, _ => DeviceKind.Other },
            0x25 or 0x29 => DeviceKind.Headset, // audio sink, wearable audio
            _ => DeviceKind.Other,
        };
    }
}
