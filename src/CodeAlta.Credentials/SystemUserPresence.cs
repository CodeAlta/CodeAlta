using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Credentials;

/// <summary>Creates the verification of the user of this system: Windows Hello on Windows 11, Touch ID or the account password on macOS.</summary>
public static class SystemUserPresence
{
    /// <summary>Creates the verifier of this system.</summary>
    /// <param name="window">Gives the native handle of the window the prompt belongs to (a HWND on Windows); 0 when there is none.</param>
    /// <returns>The verifier; one that is never available on a system without a verification CodeAlta knows (Linux).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    public static IUserPresenceVerifier ForThisSystem(Func<nint> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return new WindowsHelloVerifier(window);
        if (OperatingSystem.IsMacOS()) return new MacLocalAuthenticationVerifier();
        return new Unavailable(OperatingSystem.IsWindows()
            ? "Windows Hello asks for a secret from Windows 11 on."
            : "This system has no verification of the user CodeAlta can ask.");
    }

    private sealed class Unavailable(string reason) : IUserPresenceVerifier
    {
        public ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new UserPresenceAvailability(false, reason));

        public ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken) => ValueTask.FromResult(UserPresenceResult.Unavailable);
    }
}

/// <summary>
/// Windows Hello through <c>IUserConsentVerifierInterop.RequestVerificationForWindowAsync</c> (Windows 11): the PIN, a
/// fingerprint or the face of the user, in a dialog that belongs to the window of CodeAlta. It is a prompt, not a key.
/// </summary>
[SupportedOSPlatform("windows10.0.22000")]
internal sealed class WindowsHelloVerifier(Func<nint> window) : IUserPresenceVerifier
{
    private const string ClassName = "Windows.Security.Credentials.UI.UserConsentVerifier";
    private static readonly Guid InteropId = new("39E050C3-4E74-441A-8DC0-B81104DF949C");
    private static readonly Guid StaticsId = new("AF4F3F91-564C-4DDC-B8B8-973447627C8C");
    private static readonly Guid AsyncInfoId = new("00000036-0000-0000-C000-000000000046");
    private const string AsyncOperation = "{9fc2b0bb-e446-44e2-aa61-9cab8f636af2}";
    private static readonly Guid VerificationOperationId = ParameterizedId($"pinterface({AsyncOperation};enum(Windows.Security.Credentials.UI.UserConsentVerificationResult;i4))");
    private static readonly Guid AvailabilityOperationId = ParameterizedId($"pinterface({AsyncOperation};enum(Windows.Security.Credentials.UI.UserConsentVerifierAvailability;i4))");

    public async ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var availability = await Task.Run(() => CheckAvailability(cancellationToken), cancellationToken).ConfigureAwait(false);
            return availability switch
            {
                0 => new UserPresenceAvailability(true, null),
                1 => new UserPresenceAvailability(false, "No device of Windows Hello is present."),
                2 => new UserPresenceAvailability(false, "Windows Hello is not set up for you: set up a PIN or a fingerprint in the Settings of Windows."),
                3 => new UserPresenceAvailability(false, "Windows Hello is turned off by a policy of this computer."),
                _ => new UserPresenceAvailability(false, "Windows Hello is busy."),
            };
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new UserPresenceAvailability(false, $"Windows Hello cannot be asked: {exception.Message}");
        }
    }

    public async ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var handle = window();
        if (handle == 0) return UserPresenceResult.Unavailable;
        try
        {
            // The dialog of Windows Hello can open behind the window of an application that is not packaged: it is brought forward.
            using var forward = new CancellationTokenSource();
            _ = Task.Run(() => BringDialogForward(forward.Token), CancellationToken.None);
            var result = await Task.Run(() => RequestVerification(handle, message, cancellationToken), cancellationToken).ConfigureAwait(false);
            await forward.CancelAsync().ConfigureAwait(false);
            return result switch
            {
                0 => UserPresenceResult.Verified,
                5 or 6 => UserPresenceResult.Canceled,
                _ => UserPresenceResult.Unavailable,
            };
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return UserPresenceResult.Unavailable;
        }
    }

    private static unsafe int CheckAvailability(CancellationToken cancellationToken)
    {
        var statics = Factory(StaticsId);
        try
        {
            nint operation;
            // IUserConsentVerifierStatics.CheckAvailabilityAsync, after the six methods of IInspectable.
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(statics, 6))(statics, &operation));
            return Await(operation, AvailabilityOperationId, cancellationToken);
        }
        finally
        {
            Release(statics);
        }
    }

    private static unsafe int RequestVerification(nint handle, string message, CancellationToken cancellationToken)
    {
        var interop = Factory(InteropId);
        var text = HString(message);
        try
        {
            nint operation;
            var id = VerificationOperationId;
            // IUserConsentVerifierInterop.RequestVerificationForWindowAsync, after the six methods of IInspectable.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, Guid*, nint*, int>)Method(interop, 6))(interop, handle, text, &id, &operation));
            return Await(operation, VerificationOperationId, cancellationToken);
        }
        finally
        {
            WindowsDeleteString(text);
            Release(interop);
        }
    }

    // Waits for an IAsyncOperation<enum> to end, and gives its result.
    private static unsafe int Await(nint operation, Guid operationId, CancellationToken cancellationToken)
    {
        try
        {
            nint info;
            var asyncInfoId = AsyncInfoId;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(operation, 0))(operation, &asyncInfoId, &info));
            try
            {
                while (true)
                {
                    int status;
                    // IAsyncInfo.Status: 0 started, 1 completed, 2 canceled, 3 error.
                    Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Method(info, 7))(info, &status));
                    if (status == 1) break;
                    if (status == 2) return 6;
                    if (status == 3)
                    {
                        int error;
                        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Method(info, 8))(info, &error));
                        throw new COMException("Windows Hello failed.", error);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        _ = ((delegate* unmanaged[Stdcall]<nint, int>)Method(info, 9))(info);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    Thread.Sleep(50);
                }
            }
            finally
            {
                Release(info);
            }

            nint typed;
            var id = operationId;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(operation, 0))(operation, &id, &typed));
            try
            {
                int result;
                // IAsyncOperation<T>.GetResults, after IInspectable, put_Completed and get_Completed.
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Method(typed, 8))(typed, &result));
                return result;
            }
            finally
            {
                Release(typed);
            }
        }
        finally
        {
            Release(operation);
        }
    }

    private static unsafe nint Factory(Guid interfaceId)
    {
        _ = RoInitialize(1);
        var name = HString(ClassName);
        try
        {
            nint factory;
            Check(RoGetActivationFactory(name, &interfaceId, &factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    private static unsafe nint HString(string text)
    {
        nint result;
        fixed (char* chars = text)
        {
            Check(WindowsCreateString(chars, (uint)text.Length, &result));
        }

        return result;
    }

    private static unsafe void* Method(nint instance, int slot) => (*(void***)instance)[slot];

    private static unsafe void Release(nint instance)
    {
        if (instance != 0) _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Method(instance, 2))(instance);
    }

    private static void Check(int result)
    {
        if (result < 0) throw new COMException($"Windows answered 0x{result:X8}.", result);
    }

    // The interface id of a parameterized interface of the Windows Runtime: a version 5 UUID of its signature.
    internal static Guid ParameterizedId(string signature)
    {
        byte[] space = [0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee];
        var hash = SHA1.HashData([.. space, .. Encoding.UTF8.GetBytes(signature)]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static void BringDialogForward(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            var dialog = FindWindowW("Credential Dialog Xaml Host", null);
            if (dialog != 0)
            {
                _ = SetForegroundWindow(dialog);
                return;
            }

            Thread.Sleep(50);
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(int type);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static unsafe extern int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static unsafe extern int WindowsCreateString(char* sourceString, uint length, nint* result);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint text);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetForegroundWindow(nint window);
}

/// <summary>
/// macOS: <c>LAContext.evaluatePolicy</c> with <c>LAPolicyDeviceOwnerAuthentication</c>, which asks Touch ID, the Apple Watch
/// or the password of the account. It is a prompt, not a key.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacLocalAuthenticationVerifier : IUserPresenceVerifier
{
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const long DeviceOwnerAuthentication = 2;

    public ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(CheckAvailability());

    private static unsafe UserPresenceAvailability CheckAvailability()
    {
        try
        {
            var context = NewContext();
            try
            {
                nint error = 0;
                var can = ((delegate* unmanaged<nint, nint, long, nint*, byte>)MessageSend)(context, Selector("canEvaluatePolicy:error:"), DeviceOwnerAuthentication, &error) != 0;
                return can ? new UserPresenceAvailability(true, null) : new UserPresenceAvailability(false, "This Mac cannot verify you: set a password or Touch ID.");
            }
            finally
            {
                ((delegate* unmanaged<nint, nint, void>)MessageSend)(context, Selector("release"));
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return new UserPresenceAvailability(false, $"Touch ID cannot be asked: {exception.Message}");
        }
    }

    public async ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(completion);
        Pending? pending = null;
        try
        {
            pending = Start(message, GCHandle.ToIntPtr(handle));
            var context = pending.Context;
            using var registration = cancellationToken.Register(() => Invalidate(context));
            return await completion.Task.ConfigureAwait(false) ? UserPresenceResult.Verified : UserPresenceResult.Canceled;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return UserPresenceResult.Unavailable;
        }
        finally
        {
            if (pending is not null) Finish(pending);
            handle.Free();
        }
    }

    // What a verification holds while it waits: the context and the block given to it, freed once it answered.
    private sealed record Pending(nint Context, nint Block, nint Descriptor);

    private static unsafe Pending Start(string message, nint completion)
    {
        var block = (Block*)NativeMemory.AllocZeroed((nuint)sizeof(Block));
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        nint context;
        try
        {
            context = NewContext();
        }
        catch
        {
            NativeMemory.Free(block);
            NativeMemory.Free(descriptor);
            throw;
        }

        descriptor->Size = (nuint)sizeof(Block);
        block->Isa = GlobalBlockClass();
        block->Flags = 1 << 28; // BLOCK_IS_GLOBAL: a copy of it is itself, and it is freed here once it answered.
        block->Invoke = (nint)(delegate* unmanaged<Block*, byte, nint, void>)&Reply;
        block->Descriptor = descriptor;
        block->Completion = completion;
        var reason = NewString(message);
        ((delegate* unmanaged<nint, nint, long, nint, Block*, void>)MessageSend)(context, Selector("evaluatePolicy:localizedReason:reply:"), DeviceOwnerAuthentication, reason, block);
        return new Pending(context, (nint)block, (nint)descriptor);
    }

    private static unsafe void Invalidate(nint context) => ((delegate* unmanaged<nint, nint, void>)MessageSend)(context, Selector("invalidate"));

    private static unsafe void Finish(Pending pending)
    {
        ((delegate* unmanaged<nint, nint, void>)MessageSend)(pending.Context, Selector("release"));
        NativeMemory.Free((void*)pending.Block);
        NativeMemory.Free((void*)pending.Descriptor);
    }

    [UnmanagedCallersOnly]
    private static unsafe void Reply(Block* block, byte success, nint error)
    {
        if (GCHandle.FromIntPtr(block->Completion).Target is TaskCompletionSource<bool> completion) completion.TrySetResult(success != 0);
    }

    private static unsafe nint NewContext()
    {
        _ = Dlopen("/System/Library/Frameworks/LocalAuthentication.framework/LocalAuthentication", 1);
        var type = objc_getClass("LAContext");
        if (type == 0) throw new InvalidOperationException("LocalAuthentication is not available.");
        var allocated = ((delegate* unmanaged<nint, nint, nint>)MessageSend)(type, Selector("alloc"));
        return ((delegate* unmanaged<nint, nint, nint>)MessageSend)(allocated, Selector("init"));
    }

    private static unsafe nint NewString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* pointer = bytes)
        {
            return ((delegate* unmanaged<nint, nint, byte*, nint>)MessageSend)(objc_getClass("NSString"), Selector("stringWithUTF8String:"), pointer);
        }
    }

    private static nint GlobalBlockClass()
    {
        var library = Dlopen("/usr/lib/libSystem.B.dylib", 1);
        var symbol = Dlsym(library, "_NSConcreteGlobalBlock");
        return symbol != 0 ? symbol : throw new InvalidOperationException("The blocks of Objective-C are not available.");
    }

    private static nint MessageSend => NativeLibrary.GetExport(NativeLibrary.Load(ObjectiveC), "objc_msgSend");

    private static nint Selector(string name) => sel_registerName(name);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Block
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public BlockDescriptor* Descriptor;
        public nint Completion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [DllImport(ObjectiveC)]
    private static extern nint objc_getClass(string name);

    [DllImport(ObjectiveC)]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen")]
    private static extern nint Dlopen(string path, int mode);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym")]
    private static extern nint Dlsym(nint handle, string name);
}