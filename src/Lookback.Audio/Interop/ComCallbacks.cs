using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lookback.Audio.Interop;

/// <summary>
/// A hand-built COM object for the two interfaces WASAPI calls back on:
/// <c>IMMNotificationClient</c> (default device changes) and
/// <c>IActivateAudioInterfaceCompletionHandler</c> (process loopback
/// activation). The native block is [vtable][GCHandle to the target][refcount].
/// It answers <c>IAgileObject</c> too, which activation requires. The block
/// lives until <see cref="Dispose"/>, which the owner calls only after the
/// callback has been unregistered or has completed.
/// </summary>
internal sealed unsafe class ComCallback : IDisposable
{
    private static readonly nint NotificationVtbl = BuildNotificationVtbl();
    private static readonly nint CompletionVtbl = BuildCompletionVtbl();

    private readonly GCHandle _target;
    private nint _block;

    private ComCallback(nint vtbl, object target)
    {
        _target = GCHandle.Alloc(target);
        _block = Marshal.AllocHGlobal(3 * sizeof(nint));
        ((nint*)_block)[0] = vtbl;
        ((nint*)_block)[1] = GCHandle.ToIntPtr(_target);
        ((nint*)_block)[2] = 1;
    }

    /// <summary>The COM pointer to hand to WASAPI.</summary>
    public nint Pointer => _block;

    public static ComCallback ForNotifications(IDeviceNotifications target) => new(NotificationVtbl, target);

    public static ComCallback ForActivation(IActivationCompleted target) => new(CompletionVtbl, target);

    public void Dispose()
    {
        if (_block != 0)
        {
            Marshal.FreeHGlobal(_block);
            _block = 0;
            _target.Free();
        }
    }

    private static T? Target<T>(nint self)
        where T : class
    {
        nint handle = ((nint*)self)[1];
        return handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as T;
    }

    private static nint BuildNotificationVtbl()
    {
        nint* v = (nint*)NativeMemory.Alloc(8, (nuint)sizeof(nint));
        v[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryNotification;
        v[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        v[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        v[3] = (nint)(delegate* unmanaged[Stdcall]<nint, char*, uint, int>)&OnDeviceStateChanged;
        v[4] = (nint)(delegate* unmanaged[Stdcall]<nint, char*, int>)&OnDeviceAdded;
        v[5] = (nint)(delegate* unmanaged[Stdcall]<nint, char*, int>)&OnDeviceRemoved;
        v[6] = (nint)(delegate* unmanaged[Stdcall]<nint, int, int, char*, int>)&OnDefaultDeviceChanged;
        v[7] = (nint)(delegate* unmanaged[Stdcall]<nint, char*, PropertyKey, int>)&OnPropertyValueChanged;
        return (nint)v;
    }

    private static nint BuildCompletionVtbl()
    {
        nint* v = (nint*)NativeMemory.Alloc(4, (nuint)sizeof(nint));
        v[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryCompletion;
        v[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        v[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        v[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&ActivateCompleted;
        return (nint)v;
    }

    private static int Query(nint self, Guid* iid, nint* ppv, in Guid own)
    {
        if (*iid == Wasapi.IidUnknown || *iid == own || *iid == Wasapi.IidAgileObject)
        {
            Interlocked.Increment(ref RefCount(self));
            *ppv = self;
            return Native.SOk;
        }

        *ppv = 0;
        return Native.ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryNotification(nint self, Guid* iid, nint* ppv) => Query(self, iid, ppv, Wasapi.IidMMNotificationClient);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryCompletion(nint self, Guid* iid, nint* ppv) => Query(self, iid, ppv, Wasapi.IidActivateCompletionHandler);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref RefCount(self));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self) => (uint)Math.Max(0, Interlocked.Decrement(ref RefCount(self)));

    private static ref int RefCount(nint self) => ref *(int*)((nint*)self + 2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceStateChanged(nint self, char* id, uint state)
    {
        Target<IDeviceNotifications>(self)?.DeviceStateChanged(id == null ? "" : new string(id), state);
        return Native.SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceAdded(nint self, char* id) => Native.SOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceRemoved(nint self, char* id) => Native.SOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDefaultDeviceChanged(nint self, int flow, int role, char* id)
    {
        Target<IDeviceNotifications>(self)?.DefaultDeviceChanged(flow, role, id == null ? null : new string(id));
        return Native.SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnPropertyValueChanged(nint self, char* id, PropertyKey key) => Native.SOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ActivateCompleted(nint self, nint operation)
    {
        Target<IActivationCompleted>(self)?.Completed(operation);
        return Native.SOk;
    }
}

/// <summary>What the notification callback forwards. Called on an MMDevice thread: set a flag, never block.</summary>
internal interface IDeviceNotifications
{
    void DefaultDeviceChanged(int flow, int role, string? deviceId);

    void DeviceStateChanged(string deviceId, uint state);
}

/// <summary>Receives the finished <c>IActivateAudioInterfaceAsyncOperation</c>.</summary>
internal interface IActivationCompleted
{
    void Completed(nint operation);
}
