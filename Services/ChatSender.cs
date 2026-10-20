using System;
using System.Text;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace PrivateRoulette.Services;

/// <summary>
/// Sends chat lines through the game's chat entry (UIModule::ProcessChatBoxEntry),
/// replicating ECommons' Chat.SendMessage exactly: null-terminated UTF-8 built
/// into a Utf8String via Ctor_FromSequence, sent, then freed.
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
        Utf8String* utf8 = null;
        try
        {
            var ui = UIModule.Instance();
            if (ui == null)
            {
                this.log.Warning("chat send failed: UIModule not available");
                return false;
            }

            // ECommons-exact: UTF-8 bytes with a trailing null terminator
            var bytes = Encoding.UTF8.GetBytes(message);
            var buf = new byte[bytes.Length + 1];
            bytes.CopyTo(buf, 0);

            utf8 = (Utf8String*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(Utf8String));
            fixed (byte* p = buf)
            {
                utf8->Ctor_FromSequence(p, (nuint)buf.Length);
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
