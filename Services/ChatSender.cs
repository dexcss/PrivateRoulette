using System;
using System.Text;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace PrivateRoulette.Services;

/// <summary>
/// Sends chat lines through the game's chat entry (UIModule::ProcessChatBoxEntry)
/// via FFXIVClientStructs — no signature scanning, no wrong-address crashes.
/// </summary>
public sealed unsafe class ChatSender
{
    private readonly IPluginLog log;

    public ChatSender(IPluginLog log)
    {
        this.log = log;
    }

    public bool Send(string message)
    {
        var utf8 = (Utf8String*)0;
        try
        {
            var ui = UIModule.Instance();
            if (ui == null)
            {
                this.log.Warning("chat send failed: UIModule not available");
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(message);
            utf8 = (Utf8String*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(Utf8String));
            utf8->Ctor();

            fixed (byte* p = bytes)
            {
                utf8->SetString(new ReadOnlySpan<byte>(p, bytes.Length));
            }

            ui->ProcessChatBoxEntry(utf8, 0, false);
            return true;
        }
        catch (Exception e)
        {
            this.log.Warning($"chat send failed: {e.Message}");
            return false;
        }
        finally
        {
            if (utf8 != null)
            {
                try { utf8->Dtor(true); } catch { }
                System.Runtime.InteropServices.NativeMemory.Free(utf8);
            }
        }
    }
}
