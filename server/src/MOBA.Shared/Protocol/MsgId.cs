// SPDX-License-Identifier: MIT
namespace MOBA.Shared.Protocol;

/// <summary>Wire-level message identifiers (see PRD §5.2).</summary>
public static class MsgId
{
    public const byte C2S_JoinRoom      = 0x01;
    public const byte S2C_RoomStart     = 0x02;
    public const byte C2S_Input         = 0x10;
    public const byte S2C_FrameBatch    = 0x11;
    public const byte C2S_HashReport    = 0x20;
    public const byte C2S_RequestResync = 0x30;
    public const byte S2C_Snapshot      = 0x31;
    public const byte C2S_BuyItem       = 0x40;
    public const byte S2C_GameOver      = 0x50;
    public const byte C2S_SpectateRoom  = 0x60;
    public const byte S2C_SpectateAck   = 0x61;
    public const byte Ping              = 0xF0;
    public const byte Pong              = 0xF1;
}
