using System.Runtime.InteropServices;
using System.Text;

namespace TravelRepo.Native;

/// <summary>
/// The C ABI of TravelRepo (<c>include/travelrepo.h</c>), for clients that compile TravelRepo with NativeAOT into a
/// native library, such as a Swift app on iOS. Strings are UTF-8 and end with a zero byte. TravelRepo copies the
/// strings it is given before the function returns; a response passed to a callback is valid only during the call.
/// </summary>
public static unsafe class NativeApi
{
    /// <summary>
    /// Runs <paramref name="command"/> with <paramref name="arguments"/> (a JSON object, or null) in
    /// <paramref name="session"/> (0 for the commands without a session) and answers once through
    /// <paramref name="callback"/>, on another thread, possibly before this function returns.
    /// </summary>
    /// <returns>The id of the call for <c>tr_cancel</c>, or 0 when <paramref name="callback"/> is null.</returns>
    [UnmanagedCallersOnly(EntryPoint = "tr_call")]
    public static ulong Call(ulong session, byte* command, byte* arguments, delegate* unmanaged<nint, byte*, void> callback, nint context)
    {
        if (callback is null) return 0;
        var target = (nint)callback;
        return NativeHost.Call(session, Text(command) ?? "", Text(arguments), response => Respond(target, context, response));
    }

    /// <summary>Cancels a running call; it then answers with the error code <c>call.cancelled</c>.</summary>
    [UnmanagedCallersOnly(EntryPoint = "tr_cancel")]
    public static void Cancel(ulong call) => NativeHost.Cancel(call);

    /// <summary>Closes a session that <c>open</c> returned.</summary>
    [UnmanagedCallersOnly(EntryPoint = "tr_close")]
    public static void Close(ulong session) => NativeHost.Close(session);

    /// <summary>
    /// Sets the function Git asks for credentials, with a request id and the origin (for example
    /// <c>https://github.com</c>). The host answers each request with <c>tr_credential_reply</c>. Null removes it.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "tr_set_credential_handler")]
    public static void SetCredentialHandler(delegate* unmanaged<nint, ulong, byte*, void> handler, nint context)
    {
        if (handler is null) { NativeHost.Credentials.Request = null; return; }
        var target = (nint)handler;
        NativeHost.Credentials.Request = (request, origin) =>
        {
            var bytes = Zero(origin);
            fixed (byte* text = bytes) ((delegate* unmanaged<nint, ulong, byte*, void>)target)(context, request, text);
        };
    }

    /// <summary>
    /// Sets the function that receives events as JSON, on a TravelRepo thread: <c>clone_progress</c> during
    /// <c>clone</c> and <c>trip_changed</c> for sessions that <c>watch</c>. Null removes it.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "tr_set_event_handler")]
    public static void SetEventHandler(delegate* unmanaged<nint, byte*, void> handler, nint context)
    {
        if (handler is null) { NativeHost.Events = null; return; }
        var target = (nint)handler;
        NativeHost.Events = text => Respond(target, context, text);
    }

    /// <summary>Answers a credential request with <c>{"username":…,"password":…}</c>, or null for none.</summary>
    [UnmanagedCallersOnly(EntryPoint = "tr_credential_reply")]
    public static void CredentialReply(ulong request, byte* credential) => NativeHost.Credentials.Reply(request, Text(credential));

    private static void Respond(nint callback, nint context, string response)
    {
        var bytes = Zero(response);
        fixed (byte* text = bytes) ((delegate* unmanaged<nint, byte*, void>)callback)(context, text);
    }

    private static string? Text(byte* value) => value is null ? null : Marshal.PtrToStringUTF8((nint)value);

    private static byte[] Zero(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }
}
