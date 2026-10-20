using System;
using System.Text;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace PrivateRoulette.Services;

/// <summary>
/// Sends a line through the game's own chat box (UIModule::ProcessChatBoxEntry),
/// exactly like ECommons' Chat.SendMessage did for SimpleRoulette: build a
/// null-terminated Utf8String, hand it to the chat entry, then free it.
/// The game itself validates the channel and target, so /tell, /p and any
/// other slash command behave as if typed by the dealer.
/// </summary>
public sealed unsafe class ChatSender
{
    private const int MaxBytes = 500;

    private readonly IPluginLog log;

    public ChatSender(IPluginLog log)
    {
        this.log = log;
    }

    public bool Send(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        Utf8String* mes = null;
        try
        {
            var ui = UIModule.Instance();
            if (ui == null)
            {
                this.log.Warning("chat send failed: UIModule unavailable");
                return false;
            }

            // Chat is single-line; newlines would be dropped by the client anyway.
            var clean = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            var bytes = Encoding.UTF8.GetByteCount(clean);
            if (bytes > MaxBytes)
            {
                this.log.Warning($"chat send failed: {bytes} bytes exceeds the {MaxBytes}-byte chat limit");
                return false;
            }

            mes = Utf8String.FromString(clean);
            if (mes == null)
            {
                this.log.Warning("chat send failed: Utf8String allocation returned null");
                return false;
            }

            ui->ProcessChatBoxEntry(mes);
            this.log.Debug($"[chat] sent {clean.Length} chars");
            return true;
        }
        catch (Exception e)
        {
            this.log.Warning($"chat send failed: {e.Message}");
            return false;
        }
        finally
        {
            if (mes != null)
            {
                try { mes->Dtor(true); } catch { }
            }
        }
    }
}
