using System;
using System.Text;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace PrivateRoulette.Services;

/// <summary>
/// Sends chat lines by calling the game's chat-entry function directly.
/// Falls back to the command manager when the signature is unavailable.
/// </summary>
public sealed unsafe class ChatSender
{
    private readonly nint processChatBoxEntry;
    private readonly IPluginLog log;

    public ChatSender(ISigScanner sigScanner, IPluginLog log)
    {
        this.log = log;
        try
        {
            this.processChatBoxEntry = sigScanner.ScanText("48 89 5C 24 ?? 57 48 83 EC 20 48 8B DA 48 8B F9 48 85 D2 74 5A");
            this.log.Debug($"chat sender ready (0x{this.processChatBoxEntry:X})");
        }
        catch (Exception e)
        {
            this.processChatBoxEntry = 0;
            this.log.Warning($"chat sender unavailable: {e.Message}");
        }
    }

    public bool Send(string message)
    {
        if (this.processChatBoxEntry == 0) return false;
        try
        {
            var ui = (nint)UIModule.Instance();
            if (ui == 0) return false;

            var bytes = Encoding.UTF8.GetBytes(message + "\0");
            fixed (byte* p = bytes)
            {
                ((delegate* unmanaged<nint, byte*, nint, void>)this.processChatBoxEntry)(ui, p, 0);
            }

            return true;
        }
        catch (Exception e)
        {
            this.log.Warning($"chat send failed: {e.Message}");
            return false;
        }
    }
}
