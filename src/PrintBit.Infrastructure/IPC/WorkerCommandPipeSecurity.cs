using System;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PrintBit.Infrastructure.IPC;

public static class WorkerCommandPipeSecurity
{
    /// <summary>
    /// Creates a secure PipeSecurity configuration for the admin recovery command pipe.
    /// Grants FullControl to LocalSystem and the current service identity, and ReadWrite to BUILTIN\Administrators.
    /// Excludes WorldSid (Everyone) and AuthenticatedUserSid.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity CreatePipeSecurity()
    {
        var pipeSecurity = new PipeSecurity();

        using var currentIdentity = WindowsIdentity.GetCurrent();
        if (currentIdentity.User != null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                currentIdentity.User,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return pipeSecurity;
    }

    /// <summary>
    /// Creates a NamedPipeServerStream configured with secure ACL on Windows,
    /// enforcing admin-only and LocalSystem access for the command pipe.
    /// </summary>
    public static NamedPipeServerStream CreateServerStream(
        string pipeName,
        int maxNumberOfServerInstances = 1,
        PipeTransmissionMode transmissionMode = PipeTransmissionMode.Byte,
        PipeOptions options = PipeOptions.Asynchronous)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipeSecurity = CreatePipeSecurity();
            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances,
                transmissionMode,
                options,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity);
        }

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances,
            transmissionMode,
            options);
    }
}
